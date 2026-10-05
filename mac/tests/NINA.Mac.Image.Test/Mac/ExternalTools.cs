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
using System.Globalization;
using System.Text.RegularExpressions;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// Independent FITS consumers installed on this Mac: CFITSIO's fitsverify and listhead (Homebrew cfitsio) and Siril.
    /// A test that needs one calls the Require method, which marks the test Ignored (with the path) when the tool is absent.
    /// Siril always runs with its own empty ini in the test folder (-o -i), so the user's Siril settings are never read or written.
    /// </summary>
    internal static class ExternalTools {
        public const string Fitsverify = "/opt/homebrew/bin/fitsverify";
        public const string Listhead = "/opt/homebrew/bin/listhead";
        public const string SirilCli = "/Applications/Siril.app/Contents/MacOS/siril-cli";

        public static void Require(string tool) {
            if (!File.Exists(tool)) {
                Assert.Ignore($"{tool} is not installed");
            }
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
            // The checks parse the tools' English, C-locale output (Siril translates its log and formats numbers per locale)
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

        /// <summary>
        /// Debayers <paramref name="fitsPath"/> with Siril's default (header-driven) settings and returns the mean of each
        /// channel as Siril's "stat" prints it. The script follows the one the M3b plan used (§1 E11, Appendix B).
        /// </summary>
        public static (double Red, double Green, double Blue, string Log) SirilDebayerMeans(string fitsPath, string workFolder) {
            Require(SirilCli);
            var folder = Path.Combine(workFolder, "siril");
            Directory.CreateDirectory(folder);
            File.Copy(fitsPath, Path.Combine(folder, "frame.fits"));
            var ini = Path.Combine(folder, "siril-cli.ini");
            File.WriteAllText(ini, string.Empty);
            var script = Path.Combine(folder, "check.ssf");
            // "requires" must come first: without it siril-cli skips the script and still reports success
            File.WriteAllText(script, "requires 1.4.0\nconvert frame -debayer -out=deb\nload deb/frame_00001\nstat\nclose\n");
            var (exitCode, output) = Run(SirilCli, folder, TimeSpan.FromMinutes(2), "-o", "-i", ini, "-d", folder, "-s", script);
            if (exitCode != 0 || !output.Contains("Script execution finished successfully")) {
                throw new InvalidOperationException($"siril-cli failed (exit {exitCode}):\n{output}");
            }
            return (Mean(output, "Red"), Mean(output, "Green"), Mean(output, "Blue"), output);
        }

        private static double Mean(string sirilOutput, string layer) {
            var match = Regex.Match(sirilOutput, layer + @" layer: Mean: ([0-9.]+)");
            if (!match.Success) {
                throw new InvalidOperationException($"No '{layer} layer: Mean' in Siril's output:\n{sirilOutput}");
            }
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }
}
