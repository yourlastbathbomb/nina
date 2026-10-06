#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Avalonia;
using NINA.Mac.App.Diagnostics;
using System;
using System.Linq;

namespace NINA.Mac.App {

    public static class Program {

        /// <summary>
        /// Entry point. <c>--smoke-test</c> initialises every app service and renders every screen headlessly, then
        /// exits (0 = pass), without showing a window. <c>--startup-check</c> sets up the real Avalonia.Native platform
        /// and lays out the main window without showing it (the smoke test runs it in a child process).
        /// <c>--gui-smoke</c> is a normal launch that shows the main window for a few seconds, checks it and quits.
        /// <c>--preflight [--with-devices]</c> prints the daytime checklist (PASS/WARN/FAIL with fixes) and exits 1 on a FAIL.
        /// <c>--stack-night [YYYY-MM-DD] [--dry-run]</c> stacks a night's targets with siril-cli (NINA.Mac.Siril's night processor).
        /// <c>--version</c> prints the version.
        /// </summary>
        [STAThread]
        public static int Main(string[] args) {
            if (args.Contains("--version")) {
                var info = AppInfo.Current;
                Console.WriteLine($"{info.DisplayName} {info.Version} (based on N.I.N.A. {info.NinaBaseVersion})");
                return 0;
            }
            if (args.Contains(StackNight.Flag)) {
                return StackNight.Run(args, Console.Out);
            }
            if (args.Contains(PreflightRunner.Flag)) {
                return PreflightRunner.RunCli(args, Console.Out);
            }
            if (args.Contains("--smoke-test")) {
                return SmokeTest.Run(args, Console.Out);
            }
            if (args.Contains(StartupCheck.Flag)) {
                return StartupCheck.Run(Console.Out);
            }
            if (args.Contains(GuiSmoke.Flag)) {
                return GuiSmoke.Run(args, Console.Out, TimeSpan.FromSeconds(45));
            }
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        /// <summary>
        /// Desktop app builder (also used by the Avalonia previewer, <c>--startup-check</c> and <c>--gui-smoke</c>). It is what
        /// Avalonia.Desktop's UsePlatformDetect() picks on macOS: AppKit windowing, Skia rendering and HarfBuzz text
        /// shaping. Without UseHarfBuzz() AppBuilder.Setup() throws "No text shaping system configured" on launch;
        /// Avalonia.Headless registers HarfBuzz itself, so only --startup-check and --gui-smoke cover this.
        /// </summary>
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UseAvaloniaNative()
                .UseSkia()
                .UseHarfBuzz()
                .With(new MacOSPlatformOptions { ShowInDock = true })
                .LogToTrace();
    }
}
