#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.PlateSolving.Solvers;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>The built NINA.Platesolving.dll, read as metadata: identity, references, native imports, solvers, literals.</summary>
    [TestFixture]
    public class PlatesolvingAssemblyTest {

        private static readonly Assembly platesolving = typeof(IPlateSolver).Assembly;

        private static readonly string[] forbiddenReferences = {
            "ASCOM", "PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml", "System.Windows.Forms", "System.Drawing.Common",
            "System.Management", "Microsoft.Win32.Registry",
        };

        [Test]
        public void IsUpstreamsNinaPlatesolving_WithItsAssemblyInfo() {
            platesolving.GetName().Name.Should().Be("NINA.Platesolving");
            platesolving.GetName().Version.Should().Be(typeof(NINA.Core.Utility.CoreUtil).Assembly.GetName().Version, "Engine.props stamps CommonAssemblyInfo's version");
            platesolving.GetCustomAttribute<AssemblyTitleAttribute>()!.Title.Should().Be("NINA.Platesolving");
            platesolving.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description.Should().StartWith("This assembly contains the Platesolving components of N.I.N.A.");
        }

        [Test]
        public void References_NoAscomOrWpfAssembly_AndNoNativeImports() {
            using var pe = new PEReader(File.OpenRead(platesolving.Location));
            var md = pe.GetMetadataReader();
            var references = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            TestContext.Out.WriteLine("References: " + string.Join(", ", references));

            references.Should().NotContain(r => forbiddenReferences.Any(f => r.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
            references.Should().Contain(new[] { "NINA.Core", "NINA.Profile", "NINA.Astrometry", "NINA.Image", "NINA.Equipment", "NINA.Mac.WpfCompat" });
            md.MethodDefinitions.Select(h => md.GetMethodDefinition(h).GetImport()).Where(i => !i.Module.IsNil).Should().BeEmpty();
        }

        [Test]
        public void Solvers_AreUpstreamsEight_WithTheMacLocalSolver() {
            var solvers = platesolving.GetTypes().Where(t => typeof(IPlateSolver).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
                .Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();

            // Ordinal order: "ASTAPSolver" sorts before "AllSkyPlateSolver" ('S' < 'l')
            solvers.Should().Equal(
                "NINA.PlateSolving.Solvers.ASTAPSolver",
                "NINA.PlateSolving.Solvers.AllSkyPlateSolver",
                "NINA.PlateSolving.Solvers.AstrometryPlateSolver",
                "NINA.PlateSolving.Solvers.Dc3PinPointSolver",
                "NINA.PlateSolving.Solvers.LocalPlateSolver",
                "NINA.PlateSolving.Solvers.Platesolve2Solver",
                "NINA.PlateSolving.Solvers.Platesolve3Solver",
                "NINA.PlateSolving.Solvers.TheSkyXImageLinkSolver");
            // The LOCAL solver in the assembly is the mac one: no cmd.exe, no bash.exe
            typeof(LocalPlateSolver).GetField(nameof(LocalPlateSolver.Shell), BindingFlags.NonPublic | BindingFlags.Static).Should().NotBeNull();
            typeof(LocalPlateSolver).BaseType!.Name.Should().Be("CLISolver");
            typeof(LocalPlateSolver).GetConstructor(new[] { typeof(string) }).Should().NotBeNull("PlateSolverFactory calls new LocalPlateSolver(CygwinLocation)");
        }

        [Test]
        public void StringLiterals_HaveNoReplacementCharacter_AndNoCygwinCommandLine() {
            // Four upstream interface files are ISO-8859-1 (comments only); Engine.props transcodes them
            using var pe = new PEReader(File.OpenRead(platesolving.Location));
            var md = pe.GetMetadataReader();
            var literals = new List<string>();
            var handle = MetadataTokens.UserStringHandle(1);
            while (!handle.IsNil) {
                literals.Add(md.GetUserString(handle));
                handle = md.GetNextHandle(handle);
            }

            literals.Should().NotContain(s => s.Contains('�'));
            literals.Should().NotContain(s => s.Contains("bash.exe") || s.Contains("/usr/bin/solve-field") || s.Contains("--login"));
            literals.Should().Contain("cmd.exe", "upstream CLISolver.StartCLI still compares the executable with \"cmd.exe\" (harmless off Windows)");
        }
    }
}
