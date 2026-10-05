#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Diagnostics;

namespace NINA.Mac.Equipment.Test {

    /// <summary>
    /// Independent FITS consumers on this Mac: Siril's command-line build and CFITSIO's fitsverify (Homebrew cfitsio). Siril
    /// always runs offline with its own empty ini in the test folder (-o -i), so the user's Siril settings are never read or
    /// written; output is forced to the C locale because the checks read Siril's English log.
    /// </summary>
    internal static class SirilCli {
        public const string Path = "/Applications/Siril.app/Contents/MacOS/siril-cli";
        public const string Fitsverify = "/opt/homebrew/bin/fitsverify";

        /// <summary>
        /// Copies <paramref name="fitsPath"/> to &lt;work&gt;/siril/frame.fits, then has Siril load it, debayer it from its header
        /// (convert -debayer), load the result and print its statistics. Returns Siril's log; throws when Siril fails.
        /// </summary>
        public static string LoadAndDebayer(string fitsPath, string workFolder) {
            var folder = System.IO.Path.Combine(workFolder, "siril");
            Directory.CreateDirectory(folder);
            File.Copy(fitsPath, System.IO.Path.Combine(folder, "frame.fits"));
            var ini = System.IO.Path.Combine(folder, "siril-cli.ini");
            File.WriteAllText(ini, string.Empty);
            var script = System.IO.Path.Combine(folder, "check.ssf");
            // "requires" must come first: without it siril-cli skips the script and still reports success
            File.WriteAllText(script, "requires 1.4.0\nload frame\nconvert frame -debayer -out=deb\nload deb/frame_00001\nstat\nclose\n");
            var (exitCode, output) = Run(Path, folder, TimeSpan.FromMinutes(2), "-o", "-i", ini, "-d", folder, "-s", script);
            if (exitCode != 0 || !output.Contains("Script execution finished successfully")) {
                throw new InvalidOperationException($"siril-cli failed (exit {exitCode}):\n{output}");
            }
            return output;
        }

        public static (int ExitCode, string Output) Run(string tool, string workingDirectory, TimeSpan timeout, params string[] arguments) {
            var start = new ProcessStartInfo(tool) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            };
            foreach (var argument in arguments) {
                start.ArgumentList.Add(argument);
            }
            start.Environment["LC_ALL"] = "C";
            start.Environment["LANG"] = "C";
            start.Environment["LANGUAGE"] = "C";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds)) {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{tool} did not finish within {timeout}");
            }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
    }
}
