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
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NINA.Mac.Equipment.Test {

    /// <summary>
    /// Merge guards for NINA.Equipment.Mac, which compiles an opt-in subset of upstream NINA.Equipment. It declares its folder as
    /// EquipmentUpstreamDir, so NINA.Mac.Engine.Test's UpstreamParityTest (which requires every upstream package) skips it; this
    /// fixture compares it with NINA.Equipment.csproj instead. An upstream merge that adds a package, a project reference, a
    /// resource or a source linked from outside the folder fails here until someone decides what the mac build does with it.
    /// </summary>
    [TestFixture]
    public class EquipmentParityTest {

        /// <summary>
        /// Upstream packages the mac build leaves out, pinned to upstream's current version: a bump or removal upstream asks for
        /// a re-check of the reason. Every one is used only by files the include list leaves out (plan m3b §4c).
        /// </summary>
        private static readonly Dictionary<string, (string Version, string Why)> droppedPackages = new() {
            ["ASCOM.Alpaca.Components"] = ("3.1.0", "Alpaca drivers and discovery (AlpacaDirect*, AlpacaInteraction): not compiled"),
            ["ASCOM.Alpaca.Device"] = ("3.1.0", "Alpaca drivers: not compiled"),
            ["ASCOM.Com.Components"] = ("3.1.0", "ASCOM COM drivers (Ascom*, ASCOMInteraction): COM, Windows only, not compiled"),
            ["Castle.Core"] = ("5.2.1", "only SBIGCameraASCOMService.cs: not compiled"),
            ["Castle.Core.AsyncInterceptor"] = ("2.1.0", "only SBIGCameraASCOMService.cs: not compiled"),
            ["Google.Protobuf.Tools"] = ("3.35.1", "NINA.Equipment has no Protobuf items; build-time only"),
            ["Grpc.Tools"] = ("2.83.0", "NINA.Equipment has no Protobuf items; 2.83.0's macOS protoc is x86_64 only"),
            ["GrpcDotNetNamedPipes"] = ("3.1.0", "only SBIGCamera.cs (out-of-process SBIG service): not compiled"),
            ["Microsoft.NETFramework.ReferenceAssemblies"] = ("1.0.3", "build-time, .NET Framework targets only"),
            ["NJsonSchema"] = ("11.6.1", "only MyGPS/Gpsd.cs: not compiled"),
            ["SharpGIS.NmeaParser"] = ("2.2.2", "only MyGPS/NMEAGps.cs: not compiled"),
        };

        /// <summary>Upstream project references the mac build leaves out (only excluded vendor files use them).</summary>
        private static readonly Dictionary<string, string> droppedProjects = new() {
            ["nikoncswrapper"] = "Nikon DSLR driver (NikonCamera.cs): not compiled",
            ["NINA.MGEN"] = "Lacerta MGEN guider (MGENGuider.cs): not compiled",
        };

        private static string UpstreamDir => Path.Combine(TestHost.NinaRoot, "NINA.Equipment") + "/";

        private static XDocument Upstream => XDocument.Load(Path.Combine(UpstreamDir, "NINA.Equipment.csproj"));

        private static XDocument Mac => XDocument.Load(Path.Combine(TestHost.NinaRoot, "mac", "src", "NINA.Equipment.Mac", "NINA.Equipment.Mac.csproj"));

        [Test]
        public void MacProject_DeclaresEquipmentUpstreamDir_NotUpstreamDir() {
            // UpstreamDir would enrol the project in UpstreamParityTest, whose package rule cannot hold for an opt-in subset
            Mac.Descendants("UpstreamDir").Should().BeEmpty();
            Mac.Descendants("EquipmentUpstreamDir").Single().Value.Should().Be("$(NinaRoot)NINA.Equipment/");
        }

        [Test]
        public void PackageReferences_AreUpstreamsMinusThePinnedVendorPackages() {
            var upstream = Packages(Upstream);
            var mac = Packages(Mac);
            var problems = new List<string>();
            foreach (var (id, version) in upstream) {
                if (droppedPackages.TryGetValue(id, out var dropped)) {
                    if (version != dropped.Version) {
                        problems.Add($"{id}: dropped on macOS ({dropped.Why}) was pinned at upstream {dropped.Version}, upstream now has {version}: re-check and update the pin");
                    }
                    if (mac.ContainsKey(id)) {
                        problems.Add($"{id}: pinned as dropped but referenced by the mac project");
                    }
                } else if (!mac.TryGetValue(id, out var macVersion)) {
                    problems.Add($"{id} {version}: new in upstream; reference it in NINA.Equipment.Mac.csproj or add it to the dropped list with a reason");
                } else if (macVersion != version) {
                    problems.Add($"{id}: upstream {version}, mac {macVersion}");
                }
            }
            foreach (var id in mac.Keys.Where(id => !upstream.ContainsKey(id))) {
                problems.Add($"{id}: referenced by the mac project but not by NINA.Equipment.csproj");
            }
            foreach (var id in droppedPackages.Keys.Where(id => !upstream.ContainsKey(id))) {
                problems.Add($"{id}: pinned as dropped but upstream no longer references it; remove the pin");
            }
            problems.Should().BeEmpty();
        }

        [Test]
        public void ProjectReferences_AreUpstreamsMinusTheVendorWrappers() {
            var upstream = Upstream.Descendants("ProjectReference").Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/'))).ToList();
            var mac = Mac.Descendants("ProjectReference").Select(e => Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!)).ToList();

            droppedProjects.Keys.Should().BeSubsetOf(upstream, "a pinned dropped project reference must still exist upstream");
            foreach (var project in upstream.Except(droppedProjects.Keys)) {
                mac.Should().Contain(project + ".Mac", $"upstream NINA.Equipment references {project}; the mac build must reference its mac shadow");
            }
        }

        [Test]
        public void Upstream_HasNoResourcesProtosOrForeignSources_TheMacBuildWouldMiss() {
            Directory.GetFiles(UpstreamDir, "*.resx", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f))
                .Should().BeEmpty("NINA.Equipment.Mac embeds no resources");
            Directory.GetFiles(UpstreamDir, "*.proto", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f))
                .Should().BeEmpty("NINA.Equipment.Mac generates no protobuf code");
            Upstream.Descendants("Protobuf").Should().BeEmpty();
            Items(Upstream, "Compile", "Include").Should().BeSubsetOf(new[] { "../CommonAssemblyInfo.cs" },
                "Engine.props links CommonAssemblyInfo.cs; any other source upstream links in must be linked by the mac project too");
            Items(Upstream, "Compile", "Remove").Where(p => !p.StartsWith("publish/", StringComparison.Ordinal)).Should().BeEmpty(
                "a file upstream leaves out of its build must stay out of the mac build");
            Upstream.Descendants("Page").Where(e => !((string?)e.Attribute("Remove") ?? "").StartsWith("publish", StringComparison.Ordinal)).Should().BeEmpty();
        }

        [Test]
        public void EveryIncludePattern_MatchesUpstreamFiles() {
            // An explicit path that upstream renames fails the compile (CS2001); a wildcard that matches nothing would not
            var empty = new List<string>();
            foreach (var pattern in MacIncludes()) {
                if (UpstreamFiles(pattern).Count == 0) {
                    empty.Add(pattern);
                }
            }
            empty.Should().BeEmpty("every include in NINA.Equipment.Mac.csproj must still name upstream files");
        }

        [Test]
        public void IncludeList_KeepsVendorDriversAndSdkBindingsOut() {
            var compiled = CompiledUpstreamFiles();
            TestContext.Out.WriteLine($"{compiled.Count} upstream files compiled");

            compiled.Where(f => f.StartsWith("SDK/", StringComparison.Ordinal)).Should().Equal("SDK/CameraSDKs/ASISDK/ASICameraDll.cs");
            compiled.Where(f => f.StartsWith("Equipment/MyCamera/", StringComparison.Ordinal) && !f.EndsWith("Info.cs", StringComparison.Ordinal))
                .Should().BeEquivalentTo("Equipment/MyCamera/ASICamera.cs", "Equipment/MyCamera/ASICameras.cs", "Equipment/MyCamera/PersistSettingsCameraDecorator.cs");
            compiled.Where(f => f.StartsWith("Equipment/My", StringComparison.Ordinal) && !f.StartsWith("Equipment/MyCamera/", StringComparison.Ordinal)
                    && !f.StartsWith("Equipment/MyGuider/", StringComparison.Ordinal))
                .Should().OnlyContain(f => f.EndsWith("Info.cs", StringComparison.Ordinal), "only the *Info classes of devices the rig does not have are compiled");
            compiled.Should().NotContain(f => f.Contains("Ascom", StringComparison.OrdinalIgnoreCase) || f.Contains("Alpaca", StringComparison.OrdinalIgnoreCase));
            compiled.Should().NotContain("Interfaces/ISVBonySDK.cs");
            compiled.Should().NotContain(f => f.StartsWith("Converter/", StringComparison.Ordinal));
            compiled.Where(f => f.StartsWith("Utility/", StringComparison.Ordinal)).Should().Equal("Utility/ImageMetaDataExtension.cs");
        }

        /// <summary>Upstream files (relative to NINA.Equipment/) that the mac project compiles, from its Include and Remove items.</summary>
        internal static List<string> CompiledUpstreamFiles() {
            var removed = Items(Mac, "Compile", "Remove").Select(Relative).SelectMany(UpstreamFiles).ToHashSet();
            return MacIncludes().SelectMany(UpstreamFiles).Where(f => !removed.Contains(f)).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<string> MacIncludes() {
            return Items(Mac, "Compile", "Include").Where(i => i.StartsWith("$(EquipmentUpstreamDir)", StringComparison.Ordinal)).Select(Relative);
        }

        private static string Relative(string item) {
            return item.Replace("$(EquipmentUpstreamDir)", "");
        }

        private static List<string> UpstreamFiles(string pattern) {
            var regex = Glob(pattern);
            return Directory.GetFiles(UpstreamDir, "*.cs", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(UpstreamDir, f).Replace('\\', '/'))
                .Where(f => !IsBuildOutput(f) && regex.IsMatch(f))
                .ToList();
        }

        private static Dictionary<string, string> Packages(XDocument project) {
            return project.Descendants("PackageReference")
                .ToDictionary(e => (string)e.Attribute("Include")!, e => (string?)e.Attribute("Version") ?? e.Element("Version")!.Value.Trim());
        }

        private static IEnumerable<string> Items(XDocument project, string item, string attribute) {
            return project.Descendants(item).Select(e => (string?)e.Attribute(attribute)).Where(v => v != null)
                .SelectMany(v => v!.Split(';', StringSplitOptions.RemoveEmptyEntries)).Select(v => v.Trim().Replace('\\', '/'));
        }

        private static bool IsBuildOutput(string path) {
            var relative = Path.IsPathRooted(path) ? Path.GetRelativePath(UpstreamDir, path).Replace('\\', '/') : path;
            return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal)
                || relative.StartsWith("publish/", StringComparison.Ordinal);
        }

        private static Regex Glob(string pattern) {
            var regex = Regex.Escape(pattern).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
            return new Regex("^" + regex + "$");
        }
    }
}
