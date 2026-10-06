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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Siril {

    /// <summary>Choices of <see cref="SirilNightProcessor"/>.</summary>
    public sealed class SirilNightOptions {

        public DarkMatchOptions DarkMatching { get; set; } = new DarkMatchOptions();

        public ProcessingMode Mode { get; set; } = ProcessingMode.Rgb;

        /// <summary>Default: the middle frame (setref), so the stack keeps the mid-session orientation of the alt-az field (plan section 6).</summary>
        public RegistrationReference Registration { get; set; } = RegistrationReference.MiddleFrame;

        public Framing Framing { get; set; } = Framing.Min;

        /// <summary>N for -bias="=N*$OFFSET" when there are flats but no biases or dark flats; null: such flats go uncalibrated (warned).</summary>
        public int? SyntheticOffsetMultiplier { get; set; }

        /// <summary>Build missing or stale masters from the library's raw dark sets before matching (siril-cli).</summary>
        public bool BuildMissingMasters { get; set; } = true;

        /// <summary>
        /// When no master dark matches: true (default) stacks the target without a dark and says so; false skips the target.
        /// A first night's stack without a dark is still a stack.
        /// </summary>
        public bool ProcessWithoutDark { get; set; } = true;

        /// <summary>Only these target folders (names as on disk); null: every target of the night with lights.</summary>
        public IReadOnlyCollection<string> Targets { get; set; }
    }

    /// <summary>One target of a night: what was found, what was decided, and what Siril made of it.</summary>
    public sealed class SirilTargetReport {

        internal SirilTargetReport(TargetFolders folders) {
            Folders = folders;
        }

        public TargetFolders Folders { get; }

        public string TargetName => Folders.FolderName;

        /// <summary>The lights' settings; null when the folder holds no readable lights.</summary>
        public FrameSettings Lights { get; internal set; }

        public DarkMatch Dark { get; internal set; }

        /// <summary>The plan the script is generated from; null for a skipped target.</summary>
        public SirilPreprocessingPlan Plan { get; internal set; }

        /// <summary>What the operator should know: the calibration chosen and anything missing (no flats, no dark, ...).</summary>
        public IReadOnlyList<string> Notes => notes;

        internal readonly List<string> notes = new();

        /// <summary>Why the target was not processed; null when it was (or would be).</summary>
        public string SkipReason { get; internal set; }

        /// <summary>Validation, script, run and result files; null until <see cref="SirilNightProcessor.RunAsync"/> ran it.</summary>
        public SirilPreprocessingResult Result { get; internal set; }

        /// <summary>The stacked images Siril wrote (result_*.fit in the target folder).</summary>
        public IReadOnlyList<string> Stacks => Result?.Results ?? Array.Empty<string>();

        public bool Succeeded => SkipReason == null && Result != null && Result.Succeeded;

        public string Summary {
            get {
                if (SkipReason != null) {
                    return $"{TargetName}: skipped: {SkipReason}";
                }
                if (Result == null) {
                    return $"{TargetName}: ready ({Plan?.Dark} dark, {Plan?.Flat} flats)";
                }
                var errors = Result.Issues.Where(i => i.Severity == SirilIssueSeverity.Error).Select(i => i.Message).ToList();
                if (Result.Run == null) {
                    return $"{TargetName}: not run: {string.Join("; ", errors)}";
                }
                if (Result.Succeeded) {
                    return $"{TargetName}: stacked {Lights?.FrameCount} lights into {string.Join(", ", Stacks)}";
                }
                var missingDark = Result.MissingMasterDark != null ? $" (master dark not found: {Result.MissingMasterDark})" : string.Empty;
                return $"{TargetName}: Siril failed: {Result.Run.Summary}{missingDark}; log {Result.Run.LogPath}";
            }
        }
    }

    /// <summary>A whole night.</summary>
    public sealed class SirilNightReport {

        internal SirilNightReport(DateOnly night, string nightDirectory, IReadOnlyList<SirilTargetReport> targets, DarkLibraryBuildResult masterBuild, IReadOnlyList<string> notes) {
            Night = night;
            NightDirectory = nightDirectory;
            Targets = targets;
            MasterBuild = masterBuild;
            Notes = notes;
        }

        public DateOnly Night { get; }

        public string NightDirectory { get; }

        public IReadOnlyList<SirilTargetReport> Targets { get; }

        /// <summary>The master-dark build run before matching; null when none was needed or asked for.</summary>
        public DarkLibraryBuildResult MasterBuild { get; }

        /// <summary>Night-level remarks (no targets, masters that could not be built, ...).</summary>
        public IReadOnlyList<string> Notes { get; }

        /// <summary>Every target with lights was stacked.</summary>
        public bool Succeeded => Targets.Count > 0 && Targets.All(t => t.Succeeded);

        /// <summary>The report as lines for a log or the app's processing panel.</summary>
        public IReadOnlyList<string> Lines {
            get {
                var lines = new List<string> { $"Siril, night {SessionLayout.NightLabel(Night)} ({NightDirectory}): {Targets.Count(t => t.Succeeded)} of {Targets.Count} target(s) stacked" };
                lines.AddRange(Notes);
                foreach (var t in Targets) {
                    lines.Add(t.Summary);
                    lines.AddRange(t.Notes.Select(n => "  " + n));
                }
                return lines;
            }
        }
    }

    /// <summary>
    /// "Stack last night": for every target of one night in the <see cref="SessionLayout"/>, finds the lights, the matching
    /// master dark (<see cref="MasterDarkIndex"/>, explicit path), the target's or the night's flats and dark flats/biases if
    /// present, and generates the OSC script (bin-2 lights as taken, calibrate, debayer, register on the middle frame for the
    /// alt-az field rotation, stack, <see cref="SirilScriptGenerator"/>). <see cref="RunAsync"/> then runs siril-cli with the
    /// app's own ini (<see cref="SirilRunner"/>: the Siril GUI's preferences are never written), streams its log, and reports
    /// the stacked image of each target. Every decision and every missing piece is said in plain words in the report.
    /// </summary>
    public static class SirilNightProcessor {

        /// <summary>Target folders of the night that hold FITS lights, sorted by name.</summary>
        public static IReadOnlyList<string> FindTargets(SessionLayout layout, DateOnly night) {
            ArgumentNullException.ThrowIfNull(layout);
            var directory = layout.NightDirectory(night);
            if (!Directory.Exists(directory)) {
                return Array.Empty<string>();
            }
            return Directory.EnumerateDirectories(directory)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Where(d => SessionLayout.ListFitsFrames(Path.Combine(d, SessionLayout.LightsFolder)).Count > 0)
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Decides everything for the night without running Siril (no master build either).</summary>
        public static SirilNightReport Prepare(SessionLayout layout, DateOnly night, SirilNightOptions options = null) {
            ArgumentNullException.ThrowIfNull(layout);
            options ??= new SirilNightOptions();
            var index = MasterDarkIndex.Scan(layout.Library);
            return Plan(layout, night, options, index, null, new List<string>());
        }

        /// <summary>
        /// Builds missing masters (if asked), plans every target and runs Siril on each in turn. <paramref name="log"/> gets the
        /// decisions and siril-cli's log lines (prefixed with the target) as they happen. A failed target does not stop the
        /// others.
        /// </summary>
        public static async Task<SirilNightReport> RunAsync(SessionLayout layout, DateOnly night, SirilRunner runner, SirilNightOptions options = null,
                Action<string> log = null, CancellationToken token = default) {
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(runner);
            options ??= new SirilNightOptions();
            void Log(string line) {
                try {
                    log?.Invoke(line);
                } catch (Exception) {
                    // the listener's problem
                }
            }
            var notes = new List<string>();
            if (!runner.IsAvailable) {
                throw new FileNotFoundException($"siril-cli not found at {runner.Options.SirilCliPath}; install Siril 1.4 or set its path");
            }

            DarkLibraryBuildResult build = null;
            if (options.BuildMissingMasters && layout.Library.ScanSets().Any(s => s.CanBuild && (!s.MasterExists || s.MasterIsStale))) {
                Log("Siril: building missing master darks from the dark library");
                build = await layout.Library.BuildMastersAsync(runner, token: token).ConfigureAwait(false);
                if (!build.Succeeded) {
                    notes.Add($"Master darks could not all be built: {build.Run?.Summary}{(build.MissingMasters.Count > 0 ? "; missing " + string.Join(", ", build.MissingMasters.Select(Path.GetFileName)) : string.Empty)}");
                }
            }
            if (build != null) {
                foreach (var set in build.Unbuildable) {
                    notes.Add($"Dark set {set.Name} cannot be built: {string.Join("; ", set.Problems)}");
                }
            }

            var index = MasterDarkIndex.Scan(layout.Library);
            var report = Plan(layout, night, options, index, build, notes);
            foreach (var line in report.Notes) {
                Log("Siril: " + line);
            }
            foreach (var target in report.Targets) {
                token.ThrowIfCancellationRequested();
                Log($"Siril: {target.TargetName}: {(target.SkipReason != null ? "skipped: " + target.SkipReason : target.Dark?.Message)}");
                foreach (var n in target.Notes) {
                    Log($"Siril: {target.TargetName}: {n}");
                }
                if (target.SkipReason != null) {
                    continue;
                }
                try {
                    target.Result = await SirilPreprocessor.RunAsync(target.Plan, runner, validate: true, cleanProcessDirectory: true, token,
                        line => Log($"[{target.TargetName}] {line}")).ConfigureAwait(false);
                    foreach (var issue in target.Result.Issues) {
                        target.notes.Add(issue.ToString());
                    }
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ArgumentException || ex is UnauthorizedAccessException) {
                    target.SkipReason = $"could not run Siril: {ex.Message}";
                }
                Log("Siril: " + target.Summary);
            }
            return report;
        }

        private static SirilNightReport Plan(SessionLayout layout, DateOnly night, SirilNightOptions options, MasterDarkIndex index, DarkLibraryBuildResult build, List<string> notes) {
            var names = FindTargets(layout, night);
            if (options.Targets != null) {
                names = names.Where(n => options.Targets.Contains(n)).ToList();
            }
            if (names.Count == 0) {
                notes.Add($"No target folder with lights in {layout.NightDirectory(night)}");
            }
            var targets = new List<SirilTargetReport>();
            foreach (var name in names) {
                targets.Add(PlanTarget(layout.GetTargetFolders(night, name), layout.Library, index, options));
            }
            return new SirilNightReport(night, layout.NightDirectory(night), targets, build, notes);
        }

        private static SirilTargetReport PlanTarget(TargetFolders folders, DarkLibrary library, MasterDarkIndex index, SirilNightOptions options) {
            var report = new SirilTargetReport(folders);
            var lightFrames = SessionLayout.ListFitsFrames(folders.Lights);
            FitsHeader first = null;
            try {
                first = lightFrames.Count > 0 ? FitsFile.ReadHeader(lightFrames[0]) : null;
            } catch (Exception ex) when (ex is IOException || ex is InvalidDataException) {
                report.notes.Add($"the first light cannot be read: {ex.Message}");
            }
            report.Lights = MasterDarkIndex.ReadLights(folders.Lights);
            if (report.Lights == null) {
                report.SkipReason = $"no readable FITS lights in {folders.Lights}";
                return report;
            }
            if (report.Lights.FrameCount < 3) {
                report.SkipReason = $"only {report.Lights.FrameCount} light(s); Siril's rejection stacking needs at least 3";
                return report;
            }

            var plan = SirilPreprocessingPlan.ForTarget(folders, library);
            plan.Mode = options.Mode;
            plan.Registration = options.Registration;
            plan.Framing = options.Framing;
            plan.LightCount = lightFrames.Count;
            plan.Title = $"{folders.FolderName} ({SessionLayout.NightLabel(folders.Night)})";

            // Dark
            report.Dark = index.Find(report.Lights, options.DarkMatching, first);
            foreach (var w in report.Dark.Warnings) {
                report.notes.Add(w);
            }
            if (report.Dark.Found) {
                plan.Dark = DarkSource.Library;
                plan.MasterDarkPath = report.Dark.Master.Path;
            } else if (options.ProcessWithoutDark) {
                plan.Dark = DarkSource.None;
                report.notes.Add($"processing WITHOUT a dark: {report.Dark.Message}");
            } else {
                report.SkipReason = report.Dark.Message;
                return report;
            }

            // Flats and what calibrates them
            var flats = folders.ResolveFlatsDirectory();
            var biases = folders.ResolveBiasesDirectory();
            var hasFlats = SessionLayout.ListFitsFrames(flats).Count > 0;
            var hasBiases = SessionLayout.ListFitsFrames(biases).Count > 0;
            plan.FlatsDirectory = flats;
            plan.BiasesDirectory = biases;
            plan.Flat = hasFlats ? FlatSource.Folder : FlatSource.None;
            if (!hasFlats) {
                report.notes.Add("no flats (target or night flats/): vignetting and dust are not corrected");
            }
            if (hasBiases) {
                plan.FlatCalibration = FlatCalibration.BiasFrames;
                if (hasFlats) {
                    report.notes.Add($"flats from {Relative(folders, flats)}, calibrated with {Relative(folders, biases)}");
                }
            } else if (hasFlats && options.SyntheticOffsetMultiplier is int n && n > 0) {
                plan.FlatCalibration = FlatCalibration.SyntheticOffset;
                plan.SyntheticOffsetMultiplier = n;
                report.notes.Add(string.Create(CultureInfo.InvariantCulture, $"flats from {Relative(folders, flats)}, calibrated with a synthetic offset of {n} x OFFSET (no biases or dark flats)"));
            } else {
                plan.FlatCalibration = FlatCalibration.None;
                if (hasFlats) {
                    report.notes.Add($"flats from {Relative(folders, flats)} are NOT calibrated (no biases or dark flats, no synthetic offset): their pedestal over-corrects the vignetting by a few percent");
                }
            }
            if (plan.Dark == DarkSource.None && !hasFlats && !hasBiases) {
                report.notes.Add("no calibration at all: the lights are only debayered, registered and stacked");
            }
            report.Plan = plan;
            return report;
        }

        private static string Relative(TargetFolders folders, string directory) {
            var relative = Path.GetRelativePath(folders.NightDirectory, directory);
            return relative.StartsWith("..", StringComparison.Ordinal) ? directory : relative + "/";
        }
    }
}
