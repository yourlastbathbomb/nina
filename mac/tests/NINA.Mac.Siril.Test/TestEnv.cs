#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace NINA.Mac.Siril.Test {

    /// <summary>Temp folders and a siril-cli runner that never reads or writes the user's Siril configuration.</summary>
    internal static class TestEnv {

        /// <summary>Stock scripts shipped with the installed Siril (read only).</summary>
        public const string StockScripts = "/Applications/Siril.app/Contents/Resources/share/siril/scripts";

        public static bool SirilInstalled => File.Exists(SirilRunnerOptions.DefaultSirilCli);

        public static void RequireSiril() {
            if (!SirilInstalled) {
                Assert.Ignore($"siril-cli not installed at {SirilRunnerOptions.DefaultSirilCli}");
            }
        }

        /// <summary>New temp folder whose path contains a space, to exercise Siril quoting.</summary>
        public static string NewTempDirectory(string name) {
            var dir = Path.Combine(Path.GetTempPath(), "nina-mac-siril-tests", $"{name} {Guid.NewGuid().ToString("N")[..8]}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void Delete(string dir) {
            if (Environment.GetEnvironmentVariable("NINA_SIRIL_KEEP") == "1") {
                TestContext.Progress.WriteLine($"kept {dir}");
                return;
            }
            if (dir != null && Directory.Exists(dir) && dir.StartsWith(Path.Combine(Path.GetTempPath(), "nina-mac-siril-tests"), StringComparison.Ordinal)) {
                Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>Runner with Siril defaults (empty seed) and its own ini inside <paramref name="tempDir"/>.</summary>
        public static SirilRunner Runner(string tempDir) {
            return new SirilRunner(new SirilRunnerOptions {
                SeedConfigPath = null,
                OwnConfigPath = Path.Combine(tempDir, "siril-cli.ini"),
                Timeout = TimeSpan.FromMinutes(10),
            });
        }

        /// <summary>Script lines that are commands (no comments, no blanks), trimmed.</summary>
        public static string[] Commands(string scriptText) =>
            scriptText.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();

        public static void Print(SirilRunResult run) {
            TestContext.Progress.WriteLine($"{Path.GetFileName(run.ScriptPath)}: {run.Summary} (log {run.LogPath})");
            if (!run.Succeeded) {
                foreach (var line in run.LogLines.Where(l => !l.StartsWith("progress", StringComparison.Ordinal)).TakeLast(40)) {
                    TestContext.Progress.WriteLine("  " + line);
                }
            }
        }
    }
}
