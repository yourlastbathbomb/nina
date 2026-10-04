#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Siril {

    public sealed class SirilRunnerOptions {

        public const string DefaultSirilCli = "/Applications/Siril.app/Contents/MacOS/siril-cli";

        /// <summary>The Siril GUI's configuration (read only, never written by the runner).</summary>
        public static readonly string UserSirilConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "org.siril.Siril", "siril", "config.1.4.ini");

        public string SirilCliPath { get; set; } = DefaultSirilCli;

        /// <summary>
        /// Configuration copied into <see cref="OwnConfigPath"/> before every run, so the user's Siril preferences apply
        /// (research SIR-M3). Null or missing: start from an empty file, i.e. Siril defaults.
        /// </summary>
        public string SeedConfigPath { get; set; } = UserSirilConfig;

        /// <summary>The port's own siril-cli configuration, passed with -i. siril-cli rewrites it (wd=, any 'set').</summary>
        public string OwnConfigPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "NINA-mac", "siril", "siril-cli.ini");

        public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(3);
    }

    public sealed class SirilRunResult {
        public string ScriptPath { get; internal set; }

        public string WorkingDirectory { get; internal set; }

        public string LogPath { get; internal set; }

        public int ExitCode { get; internal set; }

        public bool TimedOut { get; internal set; }

        public bool Cancelled { get; internal set; }

        /// <summary>Siril printed "Script execution finished successfully."</summary>
        public bool FinishedSuccessfully { get; internal set; }

        /// <summary>Siril printed "Script execution failed."</summary>
        public bool ReportedFailure { get; internal set; }

        public bool Succeeded => !TimedOut && !Cancelled && ExitCode == 0 && FinishedSuccessfully && !ReportedFailure;

        /// <summary>Script line number from "Error in line N ('command'): reason".</summary>
        public int? FailedLine { get; internal set; }

        /// <summary>
        /// Command named by "Error in line N"; when Siril failed without that message (e.g. stack hitting an unreadable
        /// frame), the last command Siril started ("Running command: X").
        /// </summary>
        public string FailedCommand { get; internal set; }

        /// <summary>Reason from "Error in line N"; otherwise the first error line logged after the failed command started.</summary>
        public string FailureReason { get; internal set; }

        /// <summary>
        /// Files Siril could not find or open: "&lt;path&gt;.[any_allowed_extension] not found." (e.g. the master dark it
        /// resolved from the lights' header) and the file cfitsio names after "failed to find or open the following file".
        /// </summary>
        public IReadOnlyList<string> MissingFiles { get; internal set; } = Array.Empty<string>();

        /// <summary>
        /// Log lines reporting errors or failures (error, failed, FITSIO status, not found, aborting), without the
        /// messages siril-cli prints in successful runs too (<see cref="SirilRunner.IsHarmless"/>, "0 failed" totals,
        /// progress lines) and without the final "Script execution failed".
        /// </summary>
        public IReadOnlyList<string> ErrorLines { get; internal set; } = Array.Empty<string>();

        /// <summary>Whole log, ANSI escapes removed.</summary>
        public IReadOnlyList<string> LogLines { get; internal set; } = Array.Empty<string>();

        public string Summary {
            get {
                if (Succeeded) {
                    return "Siril script finished successfully";
                }
                var sb = new StringBuilder();
                sb.Append(TimedOut ? "Siril timed out" : Cancelled ? "Siril run cancelled" : $"Siril script failed (exit code {ExitCode})");
                if (FailedLine.HasValue) {
                    sb.Append(CultureInfo.InvariantCulture, $" at line {FailedLine} ('{FailedCommand}'): {FailureReason}");
                } else if (FailedCommand != null) {
                    sb.Append(CultureInfo.InvariantCulture, $" in '{FailedCommand}'");
                    if (FailureReason != null) {
                        sb.Append(CultureInfo.InvariantCulture, $": {FailureReason}");
                    }
                }
                foreach (var missing in MissingFiles) {
                    sb.Append(CultureInfo.InvariantCulture, $"; not found: {missing}");
                }
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Runs a Siril script with siril-cli 1.4.4 as
    /// <c>siril-cli -o -i &lt;own ini&gt; -d &lt;working dir&gt; -s &lt;script&gt;</c> (flags checked with siril-cli --help:
    /// -o offline, -i initfile, -d directory, -s script). siril-cli writes wd= and every 'set' back into the ini it
    /// loaded, so without -i a run would change the user's GUI preferences (research SIR-M3). The runner refreshes its
    /// own ini from <see cref="SirilRunnerOptions.SeedConfigPath"/> (never written) before each run, captures
    /// stdout/stderr into a log file and decides success from the exit code plus Siril's own end-of-script message.
    /// </summary>
    public sealed class SirilRunner {
        private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.CultureInvariant);
        private static readonly Regex ErrorInLine = new(@"Error in line (?<line>\d+) \('(?<cmd>[^']*)'\): (?<reason>.*?)\.?\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex NotFound = new(@"^(?:log: )?(?<path>.+?)\.\[any_allowed_extension\] not found\.?\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex RunningCommand = new(@"^(?:log: )?Running command: (?<cmd>\S+)", RegexOptions.CultureInvariant);

        /// <summary>Whole words only: a substring test for "rror" also matched "mirrorx" and "Horizontal mirror".</summary>
        private static readonly Regex ErrorWords = new(@"\berror\b|\bfailed\b|\bfailure\b|FITSIO status|not found|aborting", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Totals siril-cli prints after every successful convert/register ("0 failed, 16 registered").</summary>
        private static readonly Regex NothingFailed = new(@"\b0 failed\b", RegexOptions.CultureInvariant);

        public SirilRunner(SirilRunnerOptions options = null) {
            Options = options ?? new SirilRunnerOptions();
        }

        public SirilRunnerOptions Options { get; }

        public bool IsAvailable => File.Exists(Options.SirilCliPath);

        /// <summary>
        /// Messages siril-cli 1.4.4 prints on this Mac in successful runs too: the Python environment check, and the probe
        /// for "&lt;name&gt;.seq" before every sequence command writes "&lt;name&gt;_.seq" (seen in every logged run).
        /// </summary>
        public static bool IsHarmless(string line) =>
            line.Contains("Python validation failed", StringComparison.Ordinal)
            || line.Contains("Python version check failed", StringComparison.Ordinal)
            || line.Contains("Failed to initialize Python virtual environment", StringComparison.Ordinal)
            || line.Contains("Reading sequence failed, file cannot be opened", StringComparison.Ordinal);

        /// <summary>Copies the seed configuration into the runner's own ini (or empties it) and returns its path.</summary>
        public string PrepareConfig() {
            var own = Path.GetFullPath(Options.OwnConfigPath ?? throw new InvalidOperationException("OwnConfigPath is required"));
            if (SamePath(own, SirilRunnerOptions.UserSirilConfig) || (Options.SeedConfigPath != null && SamePath(own, Options.SeedConfigPath))) {
                throw new InvalidOperationException($"Refusing to let siril-cli write {own}: the runner's ini must not be the Siril GUI's configuration");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(own));
            if (!string.IsNullOrEmpty(Options.SeedConfigPath) && File.Exists(Options.SeedConfigPath)) {
                File.Copy(Options.SeedConfigPath, own, overwrite: true);
            } else {
                // siril-cli exits when -i names a missing file; an empty one gives Siril's defaults
                File.WriteAllText(own, string.Empty);
            }
            return own;
        }

        /// <summary>Runs a saved script. <paramref name="logPath"/> defaults to siril_&lt;script&gt;_&lt;time&gt;.log next to the script.</summary>
        public async Task<SirilRunResult> RunAsync(string scriptPath, string workingDirectory, string logPath = null, CancellationToken token = default) {
            if (!IsAvailable) {
                throw new FileNotFoundException($"siril-cli not found at {Options.SirilCliPath}");
            }
            var script = Path.GetFullPath(scriptPath);
            var wd = Path.GetFullPath(workingDirectory);
            if (!File.Exists(script)) {
                throw new FileNotFoundException($"Siril script not found: {script}");
            }
            if (!Directory.Exists(wd)) {
                throw new DirectoryNotFoundException($"Siril working directory not found: {wd}");
            }
            CheckStartsWithRequires(script);
            var ini = PrepareConfig();
            var log = Path.GetFullPath(logPath ?? Path.Combine(Path.GetDirectoryName(script),
                $"siril_{Path.GetFileNameWithoutExtension(script)}_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.log"));
            Directory.CreateDirectory(Path.GetDirectoryName(log));

            var start = new ProcessStartInfo(Options.SirilCliPath) {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WorkingDirectory = wd,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { "-o", "-i", ini, "-d", wd, "-s", script }) {
                start.ArgumentList.Add(argument);
            }

            var lines = new List<string>();
            var sync = new object();
            var result = new SirilRunResult { ScriptPath = script, WorkingDirectory = wd, LogPath = log };
            using (var writer = new StreamWriter(log, append: false, new UTF8Encoding(false))) {
                writer.WriteLine($"# {Options.SirilCliPath} -o -i \"{ini}\" -d \"{wd}\" -s \"{script}\"");
                writer.WriteLine($"# started {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
                void OnLine(string data) {
                    if (data == null) {
                        return;
                    }
                    var clean = Ansi.Replace(data, string.Empty);
                    lock (sync) {
                        lines.Add(clean);
                        writer.WriteLine(clean);
                    }
                }

                using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => OnLine(e.Data);
                process.ErrorDataReceived += (_, e) => OnLine(e.Data);
                process.Start();
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var timeout = new CancellationTokenSource(Options.Timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
                try {
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    result.TimedOut = timeout.IsCancellationRequested && !token.IsCancellationRequested;
                    result.Cancelled = token.IsCancellationRequested;
                    try {
                        process.Kill(entireProcessTree: true);
                    } catch (InvalidOperationException) {
                        // already exited
                    }
                }
                process.WaitForExit(); // drains the redirected streams
                result.ExitCode = process.ExitCode;
                lock (sync) {
                    writer.WriteLine($"# exit code {process.ExitCode}");
                }
            }

            Parse(result, lines);
            return result;
        }

        internal static void Parse(SirilRunResult result, List<string> lines) {
            var missing = new List<string>();
            var errors = new List<string>();
            string lastCommand = null;
            string firstErrorOfLastCommand = null;
            var expectOpenFailureName = false;
            foreach (var line in lines) {
                if (expectOpenFailureName) {
                    // cfitsio: "failed to find or open the following file: (ffopen)" then the name on its own line
                    expectOpenFailureName = false;
                    if (line.Trim().Length > 0 && !line.Contains(' ')) {
                        missing.Add(line.Trim());
                    }
                }
                if (line.Contains("failed to find or open the following file", StringComparison.Ordinal)) {
                    expectOpenFailureName = true;
                }
                if (line.Contains("Script execution finished successfully", StringComparison.Ordinal)) {
                    result.FinishedSuccessfully = true;
                }
                var scriptFailed = line.Contains("Script execution failed", StringComparison.Ordinal);
                if (scriptFailed) {
                    result.ReportedFailure = true;
                }
                var running = RunningCommand.Match(line);
                if (running.Success && !result.ReportedFailure) {
                    lastCommand = running.Groups["cmd"].Value;
                    firstErrorOfLastCommand = null;
                }
                var m = ErrorInLine.Match(line);
                if (m.Success && !result.FailedLine.HasValue) {
                    result.FailedLine = int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture);
                    result.FailedCommand = m.Groups["cmd"].Value;
                    result.FailureReason = m.Groups["reason"].Value;
                }
                var nf = NotFound.Match(line);
                if (nf.Success) {
                    missing.Add(nf.Groups["path"].Value);
                }
                if (!scriptFailed && !IsHarmless(line) && !line.StartsWith("progress", StringComparison.Ordinal) && !NothingFailed.IsMatch(line)
                        && ErrorWords.IsMatch(line)) {
                    errors.Add(line);
                    firstErrorOfLastCommand ??= StripLogPrefix(line);
                }
            }
            if (!result.FailedLine.HasValue && (result.ReportedFailure || result.ExitCode != 0)) {
                result.FailedCommand = lastCommand;
                result.FailureReason = firstErrorOfLastCommand;
            }
            result.MissingFiles = missing;
            result.ErrorLines = errors;
            result.LogLines = lines.ToList();
        }

        private static string StripLogPrefix(string line) => (line.StartsWith("log: ", StringComparison.Ordinal) ? line.Substring(5) : line).Trim().TrimEnd('.');

        /// <summary>
        /// siril-cli 1.4.4 skips a script whose first command is not 'requires' yet prints "Script execution finished
        /// successfully" and exits 0 (verified on this Mac), so such a script is refused here.
        /// </summary>
        internal static void CheckStartsWithRequires(string scriptPath) {
            foreach (var raw in File.ReadLines(scriptPath)) {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) {
                    continue;
                }
                if (line.StartsWith("requires ", StringComparison.OrdinalIgnoreCase)) {
                    return;
                }
                break;
            }
            throw new InvalidOperationException($"{scriptPath}: the first command must be 'requires'; siril-cli silently skips the script otherwise");
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('/'), Path.GetFullPath(b).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}
