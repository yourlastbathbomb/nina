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
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NINA.Mac.Engine.Test {

    /// <summary>
    /// Merge guards for the engine projects. An upstream merge that only changes a csproj (package bumps, resource exclusions,
    /// proto files) or a file the mac build replaces would otherwise build and pass while the mac engine silently keeps the old
    /// state. Every mac csproj under mac/src that declares an UpstreamDir is compared with its upstream csproj.
    /// </summary>
    [TestFixture]
    public class UpstreamParityTest {

        /// <summary>The only deliberate package differences; each must still match upstream's current version to stay valid.</summary>
        private static readonly Dictionary<(string Project, string Package), (string Upstream, string Mac, string Why)> deliberatePackages = new() {
            [("NINA.Core", "Grpc.Tools")] = ("2.83.0", "2.84.0",
                "2.83.0's macOS protoc is x86_64-only; 2.84.0 ships a universal protoc with the same libprotoc and generates identical code"),
            [("NINA.Core", "System.Configuration.ConfigurationManager")] = (null, "10.0.10",
                "on Windows ApplicationSettingsBase comes from the WindowsDesktop framework (UseWPF)"),
        };

        /// <summary>
        /// Upstream files that NINA.Core.Mac excludes and re-implements in MacReplacements/. Pinned by SHA-256 of the file without its
        /// copyright region and with LF line endings. When one fails: re-sync the mac file with the upstream change, then update the pin.
        /// </summary>
        private static readonly (string Upstream, string Replacement, string Sha256)[] replacedFiles = {
            ("NINA.Core/Utility/Notification/Notification.cs", "mac/src/NINA.Core.Mac/MacReplacements/Notification.cs",
                "2b71f4e2056855386dfef2a523e07282a2e9ea4be5a66db965048feb23a439c3"),
            ("NINA.Core/MyMessageBox/MyMessageBox.cs", "mac/src/NINA.Core.Mac/MacReplacements/MyMessageBox.cs",
                "53d63db8288e27b467340aa25541162781bad910027bd50a872fcc6d2fb73617"),
            ("NINA.Core/Utility/WindowService/WindowService.cs", "mac/src/NINA.Core.Mac/MacReplacements/WindowService.cs",
                "fe7b7de49657b9a79753cfc081515c2c712e5dc1d65dbb9abd467e0a36a9df13"),
        };

        private static string NinaRoot =>
            typeof(UpstreamParityTest).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "NinaRoot").Value;

        public static IEnumerable<TestCaseData> EngineProjects() {
            var macSrc = Path.Combine(NinaRoot, "mac", "src");
            foreach (var csproj in Directory.GetFiles(macSrc, "*.csproj", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)) {
                var upstreamDir = XDocument.Load(csproj).Descendants("UpstreamDir").Select(e => e.Value).SingleOrDefault();
                if (upstreamDir != null) {
                    yield return new TestCaseData(Path.GetFileName(csproj)).SetArgDisplayNames(Path.GetFileNameWithoutExtension(csproj));
                }
            }
        }

        [Test]
        public void TheEngineProjectsAreDiscovered() {
            EngineProjects().Select(t => (string)t.Arguments[0]).Should().Contain(new[] { "NINA.Core.Mac.csproj", "NINA.Profile.Mac.csproj", "NINA.Astrometry.Mac.csproj" });
        }

        [TestCaseSource(nameof(EngineProjects))]
        public void PackageReferences_MatchUpstream(string macProject) {
            var pair = Load(macProject);
            var upstream = Packages(pair.Upstream);
            var mac = Packages(pair.Mac);
            var problems = new List<string>();
            foreach (var id in upstream.Keys.Union(mac.Keys).OrderBy(k => k, StringComparer.Ordinal)) {
                upstream.TryGetValue(id, out var upstreamVersion);
                mac.TryGetValue(id, out var macVersion);
                if (deliberatePackages.TryGetValue((pair.Name, id), out var deliberate)) {
                    if (upstreamVersion != deliberate.Upstream || macVersion != deliberate.Mac) {
                        problems.Add($"{id}: deliberate difference ({deliberate.Why}) was pinned as upstream {deliberate.Upstream ?? "absent"} / mac {deliberate.Mac}, " +
                            $"now upstream {upstreamVersion ?? "absent"} / mac {macVersion ?? "absent"}: re-check it and update the pin");
                    }
                } else if (upstreamVersion != macVersion) {
                    problems.Add($"{id}: upstream {upstreamVersion ?? "absent"}, mac {macVersion ?? "absent"}");
                }
            }
            problems.Should().BeEmpty($"{macProject} must use {Path.GetFileName(pair.UpstreamPath)}'s packages and versions");
        }

        [TestCaseSource(nameof(EngineProjects))]
        public void EmbeddedResources_MatchUpstream(string macProject) {
            // Upstream embeds every *.resx under its folder (SDK default glob) minus its EmbeddedResource Remove items
            var pair = Load(macProject);
            var removed = Items(pair.Upstream, "EmbeddedResource", "Remove").Where(p => !IsPublish(p)).ToList();
            var expected = Directory.GetFiles(pair.UpstreamDir, "*.resx", SearchOption.AllDirectories)
                .Select(f => Relative(pair.UpstreamDir, f))
                .Where(f => !IsBuildOutput(f) && !removed.Any(r => Glob(r).IsMatch(f)))
                .OrderBy(f => f, StringComparer.Ordinal).ToList();

            var embedded = new List<string>();
            foreach (var item in pair.Mac.Descendants("EmbeddedResource").Where(e => e.Attribute("Include") != null)) {
                var excludes = Split(item.Attribute("Exclude")?.Value, pair.UpstreamDir);
                foreach (var include in Split(item.Attribute("Include").Value, pair.UpstreamDir)) {
                    embedded.AddRange(Directory.GetFiles(pair.UpstreamDir, "*.resx", SearchOption.AllDirectories)
                        .Select(f => Relative(pair.UpstreamDir, f))
                        .Where(f => Glob(include).IsMatch(f) && !excludes.Any(x => Glob(x).IsMatch(f))));
                }
            }
            embedded.Distinct().OrderBy(f => f, StringComparer.Ordinal).Should().Equal(expected,
                $"{macProject} must embed exactly the .resx files {Path.GetFileName(pair.UpstreamPath)} embeds");
        }

        [TestCaseSource(nameof(EngineProjects))]
        public void ProtobufAndExtraCompileItems_MatchUpstream(string macProject) {
            var pair = Load(macProject);
            Split(string.Join(";", Items(pair.Mac, "Protobuf", "Include")), pair.UpstreamDir)
                .Should().BeEquivalentTo(Items(pair.Upstream, "Protobuf", "Include"), $"{macProject} must generate the same .proto files");

            // Upstream compiles its own folder by default; anything it links from elsewhere must be accounted for on macOS
            Items(pair.Upstream, "Compile", "Include").Should().BeSubsetOf(new[] { "../CommonAssemblyInfo.cs" },
                "Engine.props links CommonAssemblyInfo.cs; any other source upstream links in must be linked by the mac project too");
            var macRemoves = Split(string.Join(";", Items(pair.Mac, "Compile", "Remove")), pair.UpstreamDir);
            Items(pair.Upstream, "Compile", "Remove").Where(p => !IsPublish(p)).Should().BeSubsetOf(macRemoves,
                "a file upstream leaves out of its build must stay out of the mac build");
        }

        [Test]
        public void ReplacedUpstreamFiles_AreUnchangedSinceTheMacReplacementWasSynced() {
            var stale = new List<string>();
            foreach (var (upstream, replacement, pinned) in replacedFiles) {
                var actual = ContentHash(Path.Combine(NinaRoot, upstream));
                if (actual != pinned) {
                    stale.Add($"upstream {upstream} changed (sha256 {actual}): re-sync {replacement}, then update the pin in {nameof(UpstreamParityTest)}");
                }
                File.Exists(Path.Combine(NinaRoot, replacement)).Should().BeTrue(replacement);
            }
            stale.Should().BeEmpty();
        }

        [Test]
        public void ReplacedUpstreamFiles_AreTheOnesNinaCoreMacExcludes() {
            var removes = Split(string.Join(";", Items(Load("NINA.Core.Mac.csproj").Mac, "Compile", "Remove")), Path.Combine(NinaRoot, "NINA.Core") + "/");
            foreach (var (upstream, _, _) in replacedFiles) {
                removes.Should().Contain(upstream["NINA.Core/".Length..]);
            }
        }

        [Test]
        public void NinaCore_CompilesUpstreamAssemblyInfo_Unchanged() {
            // Upstream Properties/AssemblyInfo.cs (with WPF's ThemeInfo) is compiled as is, not replaced by a mac copy
            var assembly = typeof(NINA.Core.Utility.CoreUtil).Assembly;
            assembly.GetCustomAttribute<AssemblyTitleAttribute>().Title.Should().Be("N.I.N.A. Core Library");
            assembly.GetCustomAttribute<AssemblyDescriptionAttribute>().Description.Should().Be("This assembly contains the core components of N.I.N.A.");
            var theme = assembly.GetCustomAttribute<System.Windows.ThemeInfoAttribute>();
            theme.ThemeDictionaryLocation.Should().Be(System.Windows.ResourceDictionaryLocation.None);
            theme.GenericDictionaryLocation.Should().Be(System.Windows.ResourceDictionaryLocation.SourceAssembly);
        }

        private static string ContentHash(string path) {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n");
            // Drop the copyright region (a yearly header bump is no API change). A UTF-8 BOM reads as \u00EF\u00BB\u00BF in Latin-1.
            text = Regex.Replace(text, "^(\u00EF\u00BB\u00BF)?#region \"copyright\".*?#endregion \"copyright\"\n*", "", RegexOptions.Singleline);
            return Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(text))).ToLowerInvariant();
        }

        private sealed record ProjectPair(string Name, string UpstreamDir, string UpstreamPath, XDocument Upstream, XDocument Mac);

        private static ProjectPair Load(string macProject) {
            var macPath = Directory.GetFiles(Path.Combine(NinaRoot, "mac", "src"), macProject, SearchOption.AllDirectories).Single();
            var mac = XDocument.Load(macPath);
            var upstreamDir = mac.Descendants("UpstreamDir").Single().Value.Replace("$(NinaRoot)", NinaRoot.TrimEnd('/') + "/");
            var name = Path.GetFileName(upstreamDir.TrimEnd('/'));
            var upstreamPath = Path.Combine(upstreamDir, name + ".csproj");
            return new ProjectPair(name, upstreamDir, upstreamPath, XDocument.Load(upstreamPath), mac);
        }

        private static Dictionary<string, string> Packages(XDocument project) {
            return project.Descendants("PackageReference")
                .ToDictionary(e => (string)e.Attribute("Include"), e => (string)e.Attribute("Version") ?? e.Element("Version")?.Value.Trim());
        }

        private static IEnumerable<string> Items(XDocument project, string item, string attribute) {
            return project.Descendants(item).Select(e => (string)e.Attribute(attribute)).Where(v => v != null)
                .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries)).Select(v => v.Trim().Replace('\\', '/'));
        }

        /// <summary>Splits a mac item spec and makes each entry relative to the upstream folder.</summary>
        private static List<string> Split(string spec, string upstreamDir) {
            if (string.IsNullOrEmpty(spec)) {
                return new List<string>();
            }
            return spec.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().Replace("$(UpstreamDir)", "").Replace(upstreamDir, "").Replace('\\', '/'))
                .ToList();
        }

        private static string Relative(string root, string path) {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        private static bool IsPublish(string pattern) {
            return pattern.StartsWith("publish/", StringComparison.Ordinal);
        }

        private static bool IsBuildOutput(string relative) {
            return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal) || IsPublish(relative);
        }

        private static Regex Glob(string pattern) {
            var regex = Regex.Escape(pattern).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
            return new Regex("^" + regex + "$");
        }
    }
}
