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
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.PlateSolving.Mac;
using NINA.PlateSolving.Solvers;
using System.Diagnostics;
using System.Globalization;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>
    /// Upstream's ASTAP solver on macOS, for this rig: the arguments NINA builds for the native f/10 bin-2 field, the result
    /// parsing, the version-check fix (upstream edit P1, bug 1 of the M3b plan) and, when ASTAP is installed, a run of the real
    /// astap_cli through NINA's CLISolver with and without the database launcher.
    /// </summary>
    [TestFixture]
    public class AstapSolverMacTest {

        private static readonly Coordinates M51 = SolverKit.Coordinates(202.4696, 47.1952);

        [Test]
        public void Arguments_ForTheNativeField_PassNinasFieldHeightAndTheRigOptions() {
            var parameter = SolverKit.RigParameter(M51);
            var properties = SolverKit.ImageProperties(parameter);
            var solver = new TestableAstapSolver("/x/astap");

            var args = solver.Arguments("/data/Application Support/PlateSolver/frame.fits", "/data/frame.ini", parameter, properties);

            // NINA's own field height: 1080 rows of 5.8 µm (2.9 µm x bin 2) at 2500 mm. Independently: 1080 x 206.264806"/mm x 5.8 / 2500 / 3600
            var independent = 1080 * 206.264806 * 5.8 / 2500 / 3600;
            properties.FoVH.Should().BeApproximately(independent, 1e-6);
            properties.FoVH.Should().BeApproximately(0.1436, 5e-5, "the plan's native field height, below D80's documented 0.15 deg");
            properties.ArcSecPerPixel.Should().BeApproximately(0.4785, 5e-5);

            var fov = Math.Round(properties.FoVH, 6).ToString(CultureInfo.InvariantCulture);
            args.Should().Be(
                "-f \"/data/Application Support/PlateSolver/frame.fits\" " +
                $"-fov {fov} -z 2 -s 500 -r 5 " +
                $"-ra {Math.Round(M51.RA, 6).ToString(CultureInfo.InvariantCulture)} " +
                $"-spd {Math.Round(M51.Dec + 90, 6).ToString(CultureInfo.InvariantCulture)}");
            fov.Should().Be("0.14356");
        }

        [Test]
        public void Arguments_WithoutAHint_SearchTheWholeSky_WhichIsWhyTheRigsBlindSolverIsSolveField() {
            var parameter = SolverKit.RigParameter(hint: null);
            var args = new TestableAstapSolver("/x/astap").Arguments("/d/f.fits", "/d/f.ini", parameter, SolverKit.ImageProperties(parameter));

            args.Should().EndWith("-r 180");
            args.Should().NotContain("-ra ").And.NotContain("-spd ");
        }

        [Test]
        public void Validation_OnMacOS_AcceptsAutoDownsample_ForAnExecutableWithoutAWindowsVersionResource() {
            // Bug 1 of the M3b plan: FileVersionInfo has no version for a Mach-O or script executable, so upstream rejected every
            // solve with DownSampleFactor 0 (NINA's default). With P1 the check runs on Windows only.
            var folder = TestHost.NewFolder("astap validation");
            var executable = Path.Combine(folder, "astap");
            File.WriteAllBytes(executable, Array.Empty<byte>());
            var solver = new TestableAstapSolver(executable);

            FileVersionInfo.GetVersionInfo(executable).FileVersion.Should().BeNull();
            solver.Invoking(s => s.Validate(SolverKit.RigParameter(M51, downSampleFactor: 0))).Should().NotThrow();
            solver.Invoking(s => s.Validate(SolverKit.RigParameter(M51, downSampleFactor: 2))).Should().NotThrow();

            new TestableAstapSolver(Path.Combine(folder, "missing")).Invoking(s => s.Validate(SolverKit.RigParameter(M51)))
                .Should().Throw<ASTAPSolver.ASTAPValidationFailedException>().WithMessage("*not found*");
            new TestableAstapSolver(string.Empty).Invoking(s => s.Validate(SolverKit.RigParameter(M51)))
                .Should().Throw<ASTAPSolver.ASTAPValidationFailedException>().WithMessage("*location missing*");
        }

        [Test]
        public void FileVersionInfo_HasNoVersionForTheInstalledAstap() {
            if (!File.Exists(SolverKit.AstapExecutable)) {
                Assert.Ignore($"ASTAP not installed at {SolverKit.AstapExecutable}");
            }
            // The cause of bug 1 on the real binary (Mach-O arm64): .NET reads only managed metadata off Windows
            FileVersionInfo.GetVersionInfo(SolverKit.AstapExecutable).FileVersion.Should().BeNull();
            new TestableAstapSolver(SolverKit.AstapExecutable).Invoking(s => s.Validate(SolverKit.RigParameter(M51, downSampleFactor: 0))).Should().NotThrow();
        }

        [Test]
        public void ReadResult_ParsesAnAstapSolutionAtTheRigsScale() {
            // A solution as astap_cli writes it (key=value .ini), for a 1920 x 1080 frame at 0.4785"/px rotated by 30 degrees
            var folder = TestHost.NewFolder("astap ini");
            var scale = 0.4785 / 3600;
            var theta = 30 * Math.PI / 180;
            var ini = Path.Combine(folder, "frame.ini");
            File.WriteAllLines(ini, new[] {
                "PLTSOLVD=T",
                "CRPIX1= 9.6050000000000000E+002",
                "CRPIX2= 5.4050000000000000E+002",
                "CRVAL1= " + 202.4696.ToString("E16", CultureInfo.InvariantCulture),
                "CRVAL2= " + 47.1952.ToString("E16", CultureInfo.InvariantCulture),
                "CD1_1=" + (-scale * Math.Cos(theta)).ToString("E16", CultureInfo.InvariantCulture),
                "CD1_2=" + (scale * Math.Sin(theta)).ToString("E16", CultureInfo.InvariantCulture),
                "CD2_1=" + (scale * Math.Sin(theta)).ToString("E16", CultureInfo.InvariantCulture),
                "CD2_2=" + (scale * Math.Cos(theta)).ToString("E16", CultureInfo.InvariantCulture),
                "CMDLINE=astap_cli -f frame.fits"
            });
            var parameter = SolverKit.RigParameter(M51);

            var result = new TestableAstapSolver("/x/astap").Result(ini, parameter, SolverKit.ImageProperties(parameter));

            result.Success.Should().BeTrue();
            result.Coordinates.RADegrees.Should().BeApproximately(202.4696, 1e-9);
            result.Coordinates.Dec.Should().BeApproximately(47.1952, 1e-9);
            result.Pixscale.Should().BeApproximately(0.4785, 1e-9);
            // Half the diagonal of 15.31' x 8.61'
            result.Radius.Should().BeApproximately(Math.Sqrt(1920.0 * 1920 + 1080.0 * 1080) * 0.4785 / 2 / 3600, 1e-9);
            var wcs = new WorldCoordinateSystem(202.4696, 47.1952, 960.5, 540.5, -scale * Math.Cos(theta), scale * Math.Sin(theta), scale * Math.Sin(theta), scale * Math.Cos(theta));
            result.PositionAngle.Should().BeApproximately(AstroUtil.EuclidianModulus(360 - (wcs.Rotation - 180), 360), 1e-9);
            result.Flipped.Should().Be(!wcs.Flipped);
        }

        [Test]
        public async Task RealAstap_ThroughNinasCliSolver_NeedsTheLauncherToFindTheDatabase() {
            if (!SolverKit.HasAstap) {
                Assert.Ignore($"ASTAP or its database not installed ({SolverKit.AstapExecutable}, {SolverKit.AstapDatabase})");
            }
            // A star-less frame cannot solve; what matters is why ASTAP gave up, which CLISolver archives in the failed .ini
            var direct = await SolveAndReadArchivedIni(SolverKit.AstapExecutable, "direct");
            AstapSetup.LauncherDirectory = TestHost.NewFolder("astap launcher");
            try {
                var launcher = AstapSetup.Resolve(SolverKit.AstapExecutable, SolverKit.AstapDatabase);
                var viaLauncher = await SolveAndReadArchivedIni(launcher, "launcher");

                direct.Should().Contain("ERROR=No star database found", "astap_cli only looks in /usr/local/opt/astap/ by itself");
                viaLauncher.Should().NotContain("No star database found");
                viaLauncher.Should().Contain("PLTSOLVD=F");
                viaLauncher.Should().Contain($"-d {SolverKit.AstapDatabase}", "the launcher adds -d before NINA's arguments");
            } finally {
                AstapSetup.LauncherDirectory = null;
            }
        }

        private static async Task<string> SolveAndReadArchivedIni(string astapLocation, string name) {
            IPlateSolver solver = new ASTAPSolver(astapLocation);
            var image = SolverKit.SmallImage(targetName: "AstapProbe-" + name);
            var result = await solver.SolveAsync(image, SolverKit.RigParameter(M51, downSampleFactor: 0), null, CancellationToken.None);
            result.Success.Should().BeFalse();
            var archived = Directory.GetFiles(TestableLocalPlateSolver.FailedDirectory, $"*.AstapProbe-{name}*.ini");
            archived.Should().ContainSingle();
            var text = File.ReadAllText(archived[0]);
            TestContext.Out.WriteLine($"{name}: {text.Replace(Environment.NewLine, " | ")}");
            return text;
        }
    }
}
