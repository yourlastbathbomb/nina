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
using System.Diagnostics;
using System.IO;

namespace NINA.Mac.App.Test.Ui {

    /// <summary>
    /// Runs the app's own build output with --smoke-test in a separate process, from CWD "/" (as Finder does) and
    /// with HOME pointed at a temp folder so nothing is written to the real Library.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class SmokeTestProcessTests {

        private static string AppBinary() {
            // tests/NINA.Mac.App.Test/bin/<Config>/net10.0/osx-arm64 -> src/NINA.Mac.App/bin/<Config>/net10.0/osx-arm64
            var testDir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            var rid = testDir.Name;
            var tfm = testDir.Parent.Name;
            var config = testDir.Parent.Parent.Name;
            var mac = testDir.Parent.Parent.Parent.Parent.Parent.Parent.FullName;
            return Path.Combine(mac, "src", "NINA.Mac.App", "bin", config, tfm, rid, "NINA.Mac.App");
        }

        [Test]
        public void SmokeTestFlag_InitialisesEverythingHeadlessly_AndExitsZero() {
            var binary = AppBinary();
            File.Exists(binary).Should().BeTrue(binary);
            var home = Path.Combine(Path.GetTempPath(), "ninamac-smoke-home-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            try {
                var psi = new ProcessStartInfo(binary, "--smoke-test") {
                    WorkingDirectory = "/",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.Environment["HOME"] = home;
                // Framework-dependent dev build: point the apphost at the SDK's runtime (the packaged .app is self-contained)
                psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(Environment.ProcessPath);
                var sw = Stopwatch.StartNew();
                using var p = Process.Start(psi);
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                p.WaitForExit(120000).Should().BeTrue("the smoke test must finish");
                TestContext.Out.WriteLine(stdout.Result);
                TestContext.Out.WriteLine($"wall time {sw.Elapsed.TotalMilliseconds:0} ms");
                p.ExitCode.Should().Be(0, stdout.Result + stderr.Result);
                stdout.Result.Should().Contain("cwd '/'").And.Contain("PASS: 11/11 checks").And.NotContain("[FAIL]");
                stdout.Result.Should().Contain($"settings {home}/Library/Application Support/Nightglass/settings.json");
                Directory.Exists(Path.Combine(home, "Library", "Application Support", "Nightglass")).Should().BeTrue();
            } finally {
                Directory.Delete(home, true);
            }
        }

        [Test]
        public void VersionFlag() {
            var psi = new ProcessStartInfo(AppBinary(), "--version") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(Environment.ProcessPath);
            using var p = Process.Start(psi);
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60000).Should().BeTrue();
            p.ExitCode.Should().Be(0);
            output.Trim().Should().MatchRegex(@"^Nightglass \(working name\) 0\.1\.0 \(based on N\.I\.N\.A\. 3\.3\.0\.\d+-nightly\)$");
        }
    }
}
