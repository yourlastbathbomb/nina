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
using System;
using System.Linq;

namespace NINA.Mac.App {

    public static class Program {

        /// <summary>
        /// Entry point. <c>--smoke-test</c> initialises every app service and renders every screen headlessly, then
        /// exits (0 = pass), without showing a window. <c>--version</c> prints the version.
        /// </summary>
        [STAThread]
        public static int Main(string[] args) {
            if (args.Contains("--version")) {
                var info = AppInfo.Current;
                Console.WriteLine($"{info.DisplayName} {info.Version} (based on N.I.N.A. {info.NinaBaseVersion})");
                return 0;
            }
            if (args.Contains("--smoke-test")) {
                return SmokeTest.Run(args, Console.Out);
            }
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        /// <summary>Desktop app builder (also used by the Avalonia previewer).</summary>
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UseAvaloniaNative()
                .UseSkia()
                .With(new MacOSPlatformOptions { ShowInDock = true })
                .LogToTrace();
    }
}
