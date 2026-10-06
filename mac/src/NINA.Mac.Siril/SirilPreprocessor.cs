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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Siril {

    public sealed class SirilPreprocessingResult {
        public IReadOnlyList<SirilIssue> Issues { get; internal set; } = Array.Empty<SirilIssue>();

        public SirilScript Script { get; internal set; }

        public string ScriptPath { get; internal set; }

        /// <summary>Null when validation stopped the run.</summary>
        public SirilRunResult Run { get; internal set; }

        /// <summary>result_*.fit files written by this run in the working directory.</summary>
        public IReadOnlyList<string> Results { get; internal set; } = Array.Empty<string>();

        /// <summary>Master dark Siril resolved from the lights' header but could not find (dark mismatch), if that stopped it.</summary>
        public string MissingMasterDark { get; internal set; }

        public bool Succeeded => Run != null && Run.Succeeded && Results.Count > 0;
    }

    /// <summary>Validate, generate, save and run one target's Siril script (the engine's "Run Siril preprocessing" action).</summary>
    public static class SirilPreprocessor {

        /// <param name="validate">Run <see cref="SirilSessionValidator"/> first and stop on errors.</param>
        /// <param name="cleanProcessDirectory">Delete the target's process/ (Siril intermediates only) before running.</param>
        /// <param name="onLine">Receives siril-cli's log lines as they are printed (see <see cref="SirilRunner.RunAsync"/>).</param>
        public static async Task<SirilPreprocessingResult> RunAsync(SirilPreprocessingPlan plan, SirilRunner runner, bool validate = true,
                bool cleanProcessDirectory = true, CancellationToken token = default, Action<string> onLine = null) {
            var result = new SirilPreprocessingResult();
            if (validate) {
                result.Issues = SirilSessionValidator.Validate(plan);
                if (result.Issues.Any(i => i.Severity == SirilIssueSeverity.Error)) {
                    return result;
                }
            }
            result.Script = SirilScriptGenerator.Generate(plan);
            if (cleanProcessDirectory) {
                CleanProcessDirectory(plan);
            }
            result.ScriptPath = result.Script.Save();
            var started = DateTime.UtcNow.AddSeconds(-2);
            result.Run = await runner.RunAsync(result.ScriptPath, result.Script.WorkingDirectory, null, token, onLine).ConfigureAwait(false);
            result.Results = Directory.EnumerateFiles(result.Script.WorkingDirectory, "result_*.fit")
                .Where(f => File.GetLastWriteTimeUtc(f) >= started)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            var darkReference = plan.MasterDarkPath ?? plan.MasterDarkPathTemplate;
            if (!result.Run.Succeeded && plan.Dark == DarkSource.Library && darkReference != null) {
                var library = Path.GetFullPath(Path.GetDirectoryName(darkReference));
                result.MissingMasterDark = result.Run.MissingFiles.FirstOrDefault(f => string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)), library, StringComparison.Ordinal));
            }
            return result;
        }

        /// <summary>Deletes process/ only when it is the plan's process folder directly inside the working directory.</summary>
        public static void CleanProcessDirectory(SirilPreprocessingPlan plan) {
            var wd = Path.GetFullPath(plan.WorkingDirectory);
            var process = Path.GetFullPath(plan.ProcessDirectory ?? Path.Combine(wd, SessionLayout.ProcessFolder));
            if (Path.GetFileName(process) != SessionLayout.ProcessFolder || !string.Equals(Path.GetDirectoryName(process), wd, StringComparison.Ordinal)) {
                throw new InvalidOperationException($"Not cleaning {process}: only <working directory>/process is cleaned");
            }
            if (Directory.Exists(process)) {
                // Siril's convert puts symlinks to the raw frames here; Directory.Delete removes the links, not their targets
                Directory.Delete(process, recursive: true);
            }
        }
    }
}
