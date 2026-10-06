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
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>
    /// Merge guards for NINA.Platesolving.Mac beyond what NINA.Mac.Engine.Test's UpstreamParityTest checks for every engine
    /// project (packages, resources, upstream Compile Remove items): the one replaced upstream file, the two compile-only
    /// stand-ins (each must go as soon as upstream no longer needs it), and the ASTAP guard (upstream edit P1).
    /// </summary>
    [TestFixture]
    public class PlatesolvingParityTest {

        private static string Upstream(string relative) => Path.Combine(TestHost.NinaRoot, relative);

        private static XDocument MacProject => XDocument.Load(Path.Combine(TestHost.NinaRoot, "mac", "src", "NINA.Platesolving.Mac", "NINA.Platesolving.Mac.csproj"));

        /// <summary>
        /// Upstream files that the mac project replaces by name, pinned by SHA-256 of the file without its copyright region and with
        /// LF line endings (the same hash UpstreamParityTest pins for NINA.Core.Mac's replacements). When one fails: re-sync the
        /// mac file with the upstream change, then update the pin.
        /// </summary>
        private static readonly (string Upstream, string Replacement, string Sha256)[] replacedFiles = {
            ("NINA.Platesolving/Solvers/LocalPlateSolver.cs", "mac/src/NINA.Platesolving.Mac/MacReplacements/LocalPlateSolver.cs",
                "6604fd944b66c474170a34db59971e9f60dcf6cb4162a00d9978b42660d58757"),
        };

        [Test]
        public void ReplacedUpstreamFiles_AreUnchangedSinceTheMacReplacementWasSynced() {
            var stale = new List<string>();
            foreach (var (upstream, replacement, pinned) in replacedFiles) {
                var actual = ContentHash(Upstream(upstream));
                if (actual != pinned) {
                    stale.Add($"upstream {upstream} changed (sha256 {actual}): re-sync {replacement}, then update the pin in {nameof(PlatesolvingParityTest)}");
                }
                File.Exists(Path.Combine(TestHost.NinaRoot, replacement)).Should().BeTrue(replacement);
            }
            stale.Should().BeEmpty();
        }

        [Test]
        public void TheProject_RemovesExactlyTheReplacedFiles_AndCompilesEveryOtherUpstreamFile() {
            var removes = MacProject.Descendants("Compile").Select(e => (string?)e.Attribute("Remove")).Where(v => v != null)
                .Select(v => v!.Replace("$(UpstreamDir)", "NINA.Platesolving/")).ToList();
            removes.Should().Equal(replacedFiles.Select(r => r.Upstream));

            // The upstream folder holds 26 .cs files today; all but the replaced one are compiled (by the ** glob)
            var upstreamSources = Directory.GetFiles(Upstream("NINA.Platesolving"), "*.cs", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(TestHost.NinaRoot, f).Replace('\\', '/'))
                .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal) && !f.Contains("/publish/", StringComparison.Ordinal))
                .ToList();
            upstreamSources.Should().HaveCount(26);
            upstreamSources.Except(removes).Should().HaveCount(25);
        }

        [Test]
        public void AscomNamespaceAnchor_IsStillNeeded() {
            // MacReplacements/AscomNamespaceAnchor.cs exists only for TheSkyXImageLinkSolver.cs's unused "using ASCOM;" (plan P3)
            var usings = Directory.GetFiles(Upstream("NINA.Platesolving"), "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadLines(f).Any(l => Regex.IsMatch(l, @"^\s*using\s+ASCOM\b")))
                .Select(Path.GetFileName).ToList();
            usings.Should().Equal(new[] { "TheSkyXImageLinkSolver.cs" },
                "when upstream drops the using, delete mac/src/NINA.Platesolving.Mac/MacReplacements/AscomNamespaceAnchor.cs");
            File.ReadAllText(Upstream("NINA.Platesolving/Solvers/TheSkyXImageLinkSolver.cs")).Should().NotMatchRegex(@"\bASCOM\.",
                "the anchor declares no types; real ASCOM use would need the ASCOM packages");
        }

        [Test]
        public void HttpDownloadImageRequestStandIn_IsStillNeeded() {
            // MacReplacements/HttpDownloadImageRequest.cs exists only because NINA.Core.Mac leaves the upstream class out and the dead
            // AstrometryPlateSolver.GetJobImage still names it (plan P2)
            var coreMac = File.ReadAllText(Path.Combine(TestHost.NinaRoot, "mac", "src", "NINA.Core.Mac", "NINA.Core.Mac.csproj"));
            coreMac.Should().Contain("Utility/Http/HttpDownloadImageRequest.cs",
                "NINA.Core.Mac compiles the upstream class again: delete mac/src/NINA.Platesolving.Mac/MacReplacements/HttpDownloadImageRequest.cs");
            var users = Directory.GetFiles(Upstream("NINA.Platesolving"), "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains("HttpDownloadImageRequest", StringComparison.Ordinal))
                .Select(Path.GetFileName).ToList();
            users.Should().Equal(new[] { "AstrometryPlateSolver.cs" },
                "when upstream drops GetJobImage, delete mac/src/NINA.Platesolving.Mac/MacReplacements/HttpDownloadImageRequest.cs");
            File.ReadAllText(Upstream("NINA.Platesolving/Solvers/AstrometryPlateSolver.cs")).Should().Contain("private Task<BitmapSource> GetJobImage(",
                "the stand-in throws; it is only safe while its one user stays private and uncalled");
            // ...and uncalled: the name appears only in its own declaration
            Regex.Matches(File.ReadAllText(Upstream("NINA.Platesolving/Solvers/AstrometryPlateSolver.cs")), @"\bGetJobImage\b").Count.Should().Be(1);
            // The stand-in's constructor still matches the upstream class it stands in for
            File.ReadAllText(Upstream("NINA.Core/Utility/Http/HttpDownloadImageRequest.cs"))
                .Should().Contain("public HttpDownloadImageRequest(string url, params object[] parameters) : base(url)");
        }

        [Test]
        public void AstapVersionCheck_RunsOnWindowsOnly() {
            // Upstream edit P1 (see mac/src/README-engine.md). A merge that drops it brings bug 1 back: every ASTAP solve with the
            // default auto downsample throws on macOS. AstapSolverMacTest checks the behaviour; this names the cause.
            var text = File.ReadAllText(Upstream("NINA.Platesolving/Solvers/ASTAPSolver.cs"));
            Regex.IsMatch(text, @"if \(OperatingSystem\.IsWindows\(\)\) \{\s*var astapVersionInfo = FileVersionInfo\.GetVersionInfo").Should().BeTrue();
        }

        private static string ContentHash(string path) {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n");
            // Drop the copyright region (a yearly header bump is no API change). A UTF-8 BOM reads as ï»¿ in Latin-1.
            text = Regex.Replace(text, "^(ï»¿)?#region \"copyright\".*?#endregion \"copyright\"\n*", "", RegexOptions.Singleline);
            return Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(text))).ToLowerInvariant();
        }
    }
}
