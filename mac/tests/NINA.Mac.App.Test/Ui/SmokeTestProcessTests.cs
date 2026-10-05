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
using NINA.Mac.App.Test.Platform;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace NINA.Mac.App.Test.Ui {

    /// <summary>
    /// Runs the app's own build output in a separate process, from CWD "/" (as Finder does) and with HOME pointed at a
    /// temp folder so nothing is written to the real Library.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class SmokeTestProcessTests {

        private static string AppBinary() => MacRepo.AppBinary;

        // Framework-dependent dev build: point the apphost at the SDK's runtime (the packaged .app is self-contained)
        private static string DotnetRoot => Path.GetDirectoryName(Environment.ProcessPath);

        /// <summary>
        /// What --startup-check prints. With an active display it starts the real Avalonia.Native platform; with every
        /// display asleep Avalonia.Native cannot start (render timer, CVReturn -6661), so it checks the builder only.
        /// </summary>
        private static string ExpectedStartup(DisplayOracle displays) => displays.AnyActive
            ? "ok: rendering Skia, text shaping HarfBuzz, windowing NativePlatformSettings"
            : "partial: rendering Skia and text shaping HarfBuzz configured; platform not started: no active display";

        private static void RequireSameDisplayState(DisplayOracle before) {
            if (DisplayOracle.Read().AnyActive != before.AnyActive) {
                Assert.Inconclusive("the display woke or went to sleep while the test ran; run it again");
            }
        }

        [Test]
        public void SmokeTestFlag_InitialisesEverythingHeadlessly_AndExitsZero() {
            var displays = DisplayOracle.Read();
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
                psi.Environment["DOTNET_ROOT"] = DotnetRoot;
                var sw = Stopwatch.StartNew();
                using var p = Process.Start(psi);
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                p.WaitForExit(120000).Should().BeTrue("the smoke test must finish");
                TestContext.Out.WriteLine(stdout.Result);
                TestContext.Out.WriteLine($"wall time {sw.Elapsed.TotalMilliseconds:0} ms");
                RequireSameDisplayState(displays);
                p.ExitCode.Should().Be(0, stdout.Result + stderr.Result);
                stdout.Result.Should().Contain("cwd '/'").And.Contain("PASS: 12/12 checks").And.NotContain("[FAIL]");
                // The real launch path (Avalonia.Native + Skia + HarfBuzz) was set up in a child process
                stdout.Result.Should().MatchRegex(@"\[ok\] native startup \(Avalonia\.Native, child process\) \(\d+ ms\): " + Regex.Escape(ExpectedStartup(displays)));
                stdout.Result.Should().Contain($"settings {home}/Library/Application Support/Nightglass/settings.json");
                Directory.Exists(Path.Combine(home, "Library", "Application Support", "Nightglass")).Should().BeTrue();
            } finally {
                Directory.Delete(home, true);
            }
        }

        /// <summary>
        /// What a Finder launch does before the window opens: Program.BuildAvaloniaApp() set up on the real
        /// Avalonia.Native platform (Dock icon off), text measured, main window laid out but never shown. Without
        /// UseHarfBuzz() this fails with "No text shaping system configured", which no headless test can see because
        /// Avalonia.Headless registers HarfBuzz itself. With every display asleep only the builder is checked (which
        /// still catches a missing UseHarfBuzz()); GuiSmokeFlag_* below is the full, on-screen check.
        /// </summary>
        [Test]
        public void StartupCheckFlag_SetsUpTheRealDesktopPlatform() {
            var displays = DisplayOracle.Read();
            var home = Path.Combine(Path.GetTempPath(), "ninamac-startup-home-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            try {
                var result = ChildProcess.Run(AppBinary(), new[] { "--startup-check" }, "/",
                    new Dictionary<string, string> { ["HOME"] = home, ["DOTNET_ROOT"] = DotnetRoot }, 60000);
                TestContext.Out.WriteLine(result.Output);
                RequireSameDisplayState(displays);
                result.ExitCode.Should().Be(0, result.Output);
                result.StdOut.Should().StartWith(ExpectedStartup(displays));
                if (displays.AnyActive) {
                    result.StdOut.Should().Contain("main window laid out at 1280x800");
                } else {
                    TestContext.Out.WriteLine("NOTE: no active display, so the native platform was not started; wake the display and rerun for the full check");
                }
                Directory.GetFileSystemEntries(home).Should().BeEmpty("the startup check uses in-memory settings");
            } finally {
                Directory.Delete(home, true);
            }
        }

        /// <summary>
        /// The real launch, on screen (Explicit: it shows the main window for a few seconds). Program.BuildAvaloniaApp()
        /// with the classic desktop lifetime, the real App and services, the window shown through Avalonia.Native, frames
        /// rendered by the native compositor, every page drawn by the real renderer; then the app quits by itself.
        /// "packaged" runs mac/artifacts/Nightglass.app (build it with mac/packaging/package-app.sh first).
        /// </summary>
        [Test]
        [Explicit("Shows the real main window on screen for a few seconds")]
        [Category("Gui")]
        [TestCase("packaged")]
        [TestCase("dev build")]
        public void GuiSmokeFlag_ShowsTheRealWindow_RendersEveryPage_AndQuits(string which) {
            if (DisplayOracle.Read() is { AnyActive: false }) {
                Assert.Inconclusive("no active display (asleep?): wake the display and run again");
            }
            var binary = which == "packaged" ? MacRepo.PackagedAppExecutable : AppBinary();
            if (!File.Exists(binary)) {
                Assert.Inconclusive($"{binary} is missing: run mac/packaging/package-app.sh first");
            }
            var home = Path.Combine(Path.GetTempPath(), "ninamac-gui-home-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            try {
                var env = new Dictionary<string, string> { ["HOME"] = home };
                if (which != "packaged") {
                    env["DOTNET_ROOT"] = DotnetRoot;
                }
                var sw = Stopwatch.StartNew();
                var result = ChildProcess.Run(binary, new[] { "--gui-smoke" }, "/", env, 90000);
                TestContext.Out.WriteLine(result.Output);
                TestContext.Out.WriteLine($"wall time {sw.Elapsed.TotalMilliseconds:0} ms");
                result.ExitCode.Should().Be(0, result.Output);
                result.StdOut.Should().Contain("cwd '/'")
                    .And.MatchRegex(@"\[ok\] platform \(\d+ ms\) rendering Skia, text shaping HarfBuzz, platform settings NativePlatformSettings")
                    .And.MatchRegex(@"\[ok\] native window \(\d+ ms\) NSWindow 0x[0-9a-f]+ 'Nightglass', client 1280x800 pt")
                    .And.MatchRegex(@"\[ok\] native frames \(\d+ ms\) compositor rendered a frame in \d+ ms; render loop ticked 3 times")
                    .And.Contain("[ok] every page, real renderer")
                    .And.Contain("[ok] app menu").And.Contain("About Nightglass")
                    .And.MatchRegex(@"PASS: 6/6 GUI checks in \d+ ms")
                    .And.NotContain("[FAIL]");
            } finally {
                Directory.Delete(home, true);
            }
        }

        [Test]
        public void VersionFlag() {
            var psi = new ProcessStartInfo(AppBinary(), "--version") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.Environment["DOTNET_ROOT"] = DotnetRoot;
            using var p = Process.Start(psi);
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60000).Should().BeTrue();
            p.ExitCode.Should().Be(0);
            output.Trim().Should().MatchRegex(@"^Nightglass 0\.1\.0 \(based on N\.I\.N\.A\. 3\.3\.0\.\d+-nightly\)$");
        }
    }
}
