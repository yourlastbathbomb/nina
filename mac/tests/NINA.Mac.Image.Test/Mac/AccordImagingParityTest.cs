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
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// Merge guards for the two M3b assemblies. NINA.Mac.Engine.Test's UpstreamParityTest already compares NINA.Image.Mac with
    /// NINA.Image.csproj (it finds every mac csproj that declares an UpstreamDir). Accord.Imaging.Mac is compared here, with
    /// "Accord.Imaging (NETStandard).csproj", because its upstream folder also holds the legacy Accord.Imaging.csproj that the
    /// generic test would pick. Both built assemblies must carry the identity Windows NINA ships, so that plugins and
    /// NINA.Image bind to them unchanged.
    /// </summary>
    [TestFixture]
    public class AccordImagingParityTest {
        private static string UpstreamCsproj => Path.Combine(TestHost.NinaRoot, "Accord.Imaging", "Accord.Imaging (NETStandard).csproj");
        private static string MacCsproj => Path.Combine(TestHost.NinaRoot, "mac", "src", "Accord.Imaging.Mac", "Accord.Imaging.Mac.csproj");

        private static string Property(XDocument project, string name) {
            return project.Descendants(name).Select(e => e.Value.Trim()).FirstOrDefault() ?? string.Empty;
        }

        [Test]
        public void PackageReferences_MatchUpstream() {
            static Dictionary<string, string> Packages(XDocument project) => project.Descendants("PackageReference")
                .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!);

            Packages(XDocument.Load(MacCsproj)).Should().BeEquivalentTo(Packages(XDocument.Load(UpstreamCsproj)));
        }

        [Test]
        public void AssemblyIdentityAndCompileSettings_MatchUpstream() {
            var upstream = XDocument.Load(UpstreamCsproj);
            var mac = XDocument.Load(MacCsproj);

            foreach (var name in new[] { "AssemblyName", "RootNamespace", "Version", "FileVersion", "AllowUnsafeBlocks", "Deterministic" }) {
                Property(mac, name).Should().Be(Property(upstream, name), name);
            }
            Property(mac, "TargetFramework").Should().Be(Property(upstream, "TargetFrameworks"));
            // No LangVersion upstream: the SDK default for netstandard2.0 is C# 7.3, which the mac project restates
            Property(upstream, "LangVersion").Should().BeEmpty();
            Property(mac, "LangVersion").Should().Be("7.3");
        }

        [Test]
        public void Upstream_StillCompilesItsWholeFolder() {
            // The mac project compiles Accord.Imaging/**/*.cs; that is right only while upstream adds no Compile items of its own
            var upstream = XDocument.Load(UpstreamCsproj);

            upstream.Descendants("Compile").Should().BeEmpty();
            upstream.Descendants("Import").Should().BeEmpty();
        }

        [Test]
        public void BuiltAccordImaging_HasTheIdentityWindowsNinaShips() {
            var assembly = typeof(Accord.Imaging.BlobCounter).Assembly;

            assembly.GetName().Name.Should().Be("Accord.Imaging");
            assembly.GetName().Version.Should().Be(new Version(3, 5, 3, 0));
            FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion.Should().Be("3.8.3.6155");
            assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName.Should().Be(".NETStandard,Version=v2.0");
        }

        [Test]
        public void BuiltNinaImage_HasUpstreamsIdentity_AndBindsToThisAccordImaging() {
            var assembly = typeof(NINA.Image.ImageData.BaseImageData).Assembly;

            assembly.GetName().Name.Should().Be("NINA.Image");
            assembly.GetName().Version.Should().Be(new Version(3, 3, 0, 1064));
            assembly.GetReferencedAssemblies().Single(a => a.Name == "Accord.Imaging").Version.Should().Be(new Version(3, 5, 3, 0));
            assembly.GetReferencedAssemblies().Should().NotContain(a => a.Name == "PresentationCore" || a.Name == "WindowsBase" || a.Name == "PresentationFramework",
                "the WPF types come from NINA.Mac.WpfCompat");
        }
    }
}
