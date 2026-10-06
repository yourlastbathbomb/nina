#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.App.Services;
using NINA.Mac.Platform;
using NINA.Mac.Siril;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using SirilLayout = NINA.Mac.Siril.SessionLayout;

namespace NINA.Mac.App.Diagnostics {

    /// <summary>
    /// <c>--stack-night [YYYY-MM-DD] [--dry-run]</c>: "after the night" from the command line, with NINA.Mac.Siril's night
    /// processor: builds missing master darks from the dark library, then for every target of the night calibrates (matching
    /// master dark, the night's flats), debayers, registers on the middle frame (alt-az field rotation) and stacks with
    /// siril-cli, writing <c>result_*.fit</c> into each target's folder. Without a date it takes the newest night folder that
    /// holds lights. <c>--dry-run</c> only says what it would do. siril-cli runs with the app's own ini (a copy of the Siril
    /// GUI's preferences); the GUI's configuration is never written.
    /// </summary>
    public static class StackNight {
        public const string Flag = "--stack-night";
        public const string DryRunFlag = "--dry-run";

        public static int Run(string[] args, TextWriter output, CancellationToken token = default) {
            var info = AppInfo.Current;
            var basePaths = new UserDataPaths(info.Identity);
            var settings = new JsonSettingsStore(basePaths.SettingsFile).Current;
            var paths = new UserDataPaths(info.Identity, null, settings.ImagesRoot);
            return Run(args, output, paths, token);
        }

        internal static int Run(string[] args, TextWriter output, UserDataPaths paths, CancellationToken token = default) {
            ArgumentNullException.ThrowIfNull(paths);
            var dryRun = args.Contains(DryRunFlag);
            // NINA names the night folder from the Mac's local time ($$DATEMINUS12$$), so the layout uses the Mac's zone too
            var layout = new SirilLayout(new SessionLayoutOptions { Root = paths.ImagesRoot, SiteTimeZone = TimeZoneInfo.Local });
            DateOnly night;
            var index = Array.IndexOf(args, Flag);
            var given = index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
            if (given != null) {
                if (!DateOnly.TryParseExact(given, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out night)) {
                    output.WriteLine($"'{given}' is not a night (YYYY-MM-DD, the date the evening started)");
                    return 2;
                }
            } else {
                var latest = LatestNightWithLights(layout);
                if (latest is not DateOnly found) {
                    output.WriteLine($"No night with lights under {layout.Root}");
                    return 1;
                }
                night = found;
            }
            if (SirilNightProcessor.FindTargets(layout, night).Count == 0) {
                output.WriteLine($"No target with lights in {layout.NightDirectory(night)}");
                return 1;
            }
            output.WriteLine($"{AppInfo.Current.DisplayName}: {(dryRun ? "plan for" : "stacking")} night {SirilLayout.NightLabel(night)} in {layout.NightDirectory(night)}");
            if (dryRun) {
                foreach (var line in SirilNightProcessor.Prepare(layout, night).Lines) {
                    output.WriteLine(line);
                }
                return 0;
            }
            var runner = new SirilRunner(new SirilRunnerOptions {
                OwnConfigPath = Path.Combine(paths.SettingsDirectory, "siril", "siril-cli.ini"),
            });
            if (!runner.IsAvailable) {
                output.WriteLine($"siril-cli not found at {runner.Options.SirilCliPath}: install Siril 1.4 in /Applications");
                return 1;
            }
            var report = SirilNightProcessor.RunAsync(layout, night, runner, log: output.WriteLine, token: token).GetAwaiter().GetResult();
            output.WriteLine();
            foreach (var line in report.Lines) {
                output.WriteLine(line);
            }
            return report.Succeeded ? 0 : 1;
        }

        /// <summary>The newest yyyy-MM-dd folder under the root that has a target with lights.</summary>
        internal static DateOnly? LatestNightWithLights(SirilLayout layout) {
            if (!Directory.Exists(layout.Root)) {
                return null;
            }
            return Directory.EnumerateDirectories(layout.Root)
                .Select(d => DateOnly.TryParseExact(Path.GetFileName(d), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var n) ? n : (DateOnly?)null)
                .Where(n => n != null && SirilNightProcessor.FindTargets(layout, n.Value).Count > 0)
                .OrderByDescending(n => n)
                .FirstOrDefault();
        }
    }
}
