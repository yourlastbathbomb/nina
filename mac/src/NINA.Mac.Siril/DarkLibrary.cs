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

    /// <summary>One folder of raw darks in the library and the master it builds.</summary>
    public sealed class DarkSet {

        internal DarkSet(string directory, IReadOnlyList<string> frames, FitsHeader firstHeader, string masterPath, IReadOnlyList<string> problems, IReadOnlyList<string> warnings) {
            Directory = directory;
            Frames = frames;
            FirstHeader = firstHeader;
            MasterPath = masterPath;
            Problems = problems;
            Warnings = warnings;
        }

        public string Directory { get; }

        public string Name => Path.GetFileName(Directory);

        public IReadOnlyList<string> Frames { get; }

        public FitsHeader FirstHeader { get; }

        /// <summary>Master path predicted from the frames' headers; null when the headers cannot name one.</summary>
        public string MasterPath { get; }

        public string MasterFileName => MasterPath == null ? null : Path.GetFileName(MasterPath);

        /// <summary>Errors that stop this set from being built.</summary>
        public IReadOnlyList<string> Problems { get; }

        public IReadOnlyList<string> Warnings { get; }

        public bool CanBuild => Problems.Count == 0;

        public bool MasterExists => MasterPath != null && File.Exists(MasterPath);

        /// <summary>True when the master no longer matches the raw frames on disk (see <see cref="MasterStaleReason"/>).</summary>
        public bool MasterIsStale => MasterStaleReason != null;

        /// <summary>
        /// Why the existing master must be rebuilt, or null: a raw frame is newer than the master, or the master's
        /// STACKCNT (frames Siril stacked) differs from the frames now in the folder, i.e. frames were deleted, added
        /// or replaced by copies that kept an older modification time (Finder copies do).
        /// </summary>
        public string MasterStaleReason {
            get {
                if (!MasterExists) {
                    return null;
                }
                var masterTime = File.GetLastWriteTimeUtc(MasterPath);
                var newer = Frames.Count(f => File.GetLastWriteTimeUtc(f) > masterTime);
                if (newer > 0) {
                    return $"{newer} raw frame(s) newer than {MasterFileName}";
                }
                FitsHeader master;
                try {
                    master = FitsFile.ReadHeader(MasterPath);
                } catch (Exception ex) when (ex is IOException || ex is InvalidDataException) {
                    return $"{MasterFileName} cannot be read ({ex.Message})";
                }
                var stacked = master.GetInt("STACKCNT");
                if (stacked.HasValue && stacked.Value != Frames.Count) {
                    return $"{MasterFileName} was stacked from {stacked.Value} frames, the folder now holds {Frames.Count}";
                }
                return null;
            }
        }

        internal DarkSet WithProblem(string problem) => new(Directory, Frames, FirstHeader, MasterPath, Problems.Append(problem).ToList(), Warnings);
    }

    /// <summary>Result of building master darks.</summary>
    public sealed class DarkLibraryBuildResult {

        internal DarkLibraryBuildResult(IReadOnlyList<DarkSet> built, IReadOnlyList<DarkSet> skipped, SirilRunResult run, IReadOnlyList<string> missingMasters) {
            Built = built;
            Skipped = skipped;
            Run = run;
            MissingMasters = missingMasters;
        }

        public IReadOnlyList<DarkSet> Built { get; }

        /// <summary>Sets not built: masters already up to date, plus <see cref="Unbuildable"/>.</summary>
        public IReadOnlyList<DarkSet> Skipped { get; }

        /// <summary>Sets that cannot be built (see each set's Problems), e.g. mixed settings or two sets sharing one master name.</summary>
        public IReadOnlyList<DarkSet> Unbuildable => Skipped.Where(s => !s.CanBuild).ToList();

        /// <summary>The siril-cli run; null when nothing needed building.</summary>
        public SirilRunResult Run { get; }

        /// <summary>Masters the C# side predicted but Siril did not write (naming disagreement or Siril failure).</summary>
        public IReadOnlyList<string> MissingMasters { get; }

        /// <summary>Every set that was built succeeded. Sets with problems are not attempted: check <see cref="Unbuildable"/>.</summary>
        public bool Succeeded => (Run == null || Run.Succeeded) && MissingMasters.Count == 0;
    }

    /// <summary>
    /// Shared master-dark library (MAC_PORT_PLAN.md decision 7; research SIR-M1). Raw darks live in
    /// library/darks/&lt;set&gt;/; masters are written by Siril to library/masters/ under a file name built from Siril
    /// header tokens (default <see cref="SirilPathTemplate.MasterDarkFileName"/>). Lights are calibrated with
    /// "-dark=&lt;library&gt;/masters/dark_$EXPTIME:%d$s_...fit": Siril fills the tokens from the lights' reference frame and
    /// aborts the script if no master matches.
    /// </summary>
    public sealed class DarkLibrary {

        public DarkLibrary(string root, string masterDarkFileTemplate = SirilPathTemplate.MasterDarkFileName) {
            if (string.IsNullOrWhiteSpace(root)) {
                throw new ArgumentException("Library root is required", nameof(root));
            }
            if (string.IsNullOrWhiteSpace(masterDarkFileTemplate) || !masterDarkFileTemplate.EndsWith(".fit", StringComparison.Ordinal)) {
                throw new ArgumentException("The master-dark template must end in .fit (scripts pin Siril's extension with 'setext fit' and turn compression off)", nameof(masterDarkFileTemplate));
            }
            if (masterDarkFileTemplate.Contains('/') || masterDarkFileTemplate.Contains('\\')) {
                throw new ArgumentException("The master-dark template is a file name, not a path", nameof(masterDarkFileTemplate));
            }
            Root = Path.GetFullPath(root);
            MasterDarkFileTemplate = masterDarkFileTemplate;
            SirilScriptGenerator.EnsureSirilSafeDirectory(Root, "dark library");
        }

        public string Root { get; }

        public string DarksDirectory => Path.Combine(Root, SessionLayout.DarksFolder);

        public string MastersDirectory => Path.Combine(Root, SessionLayout.MastersFolder);

        public string ProcessDirectory => Path.Combine(Root, SessionLayout.ProcessFolder);

        public string MasterDarkFileTemplate { get; }

        /// <summary>Absolute path template for calibrate -dark=.</summary>
        public string MasterDarkPathTemplate => Path.Combine(MastersDirectory, MasterDarkFileTemplate);

        /// <summary>The master Siril will look for when calibrating lights whose reference frame has this header.</summary>
        public string ExpectedMasterPath(FitsHeader lightHeader) => Path.Combine(MastersDirectory, SirilPathTemplate.Resolve(MasterDarkFileTemplate, lightHeader));

        public IReadOnlyList<string> ListMasters() {
            if (!System.IO.Directory.Exists(MastersDirectory)) {
                return Array.Empty<string>();
            }
            return System.IO.Directory.EnumerateFiles(MastersDirectory, "*.fit").OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Raw dark sets: every sub-folder of darks/ holding FITS frames, checked for one exact setting per set and one
        /// set per master name. Siril's %d truncates (EXPTIME 20.0 and 20.5 both give "20s", SET-TEMP -0.6 and 0 both
        /// "T0"), so two sets can resolve to the same master; both are then marked unbuildable instead of one silently
        /// overwriting the other.
        /// </summary>
        public IReadOnlyList<DarkSet> ScanSets() {
            var sets = new List<DarkSet>();
            if (!System.IO.Directory.Exists(DarksDirectory)) {
                return sets;
            }
            foreach (var directory in System.IO.Directory.EnumerateDirectories(DarksDirectory).OrderBy(d => d, StringComparer.Ordinal)) {
                var frames = SessionLayout.ListFitsFrames(directory);
                if (frames.Count == 0) {
                    continue;
                }
                sets.Add(Inspect(directory, frames));
            }
            // APFS is case-insensitive by default, so names differing only in case are the same file
            foreach (var group in sets.Where(s => s.MasterPath != null).GroupBy(s => s.MasterPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList()) {
                foreach (var set in group) {
                    var others = string.Join(", ", group.Where(o => !ReferenceEquals(o, set)).Select(o => o.Name));
                    sets[sets.IndexOf(set)] = set.WithProblem(
                        $"{set.MasterFileName} is also the master of {others}: the template's %d formatting truncates, so the sets would overwrite one master; keep one set or make the template tell them apart");
                }
            }
            return sets;
        }

        private DarkSet Inspect(string directory, IReadOnlyList<string> frames) {
            var problems = new List<string>();
            var warnings = new List<string>();
            var keys = SirilPathTemplate.GetKeys(MasterDarkFileTemplate);
            FitsHeader first = null;
            string masterName = null;
            int? width = null;
            int? height = null;
            foreach (var frame in frames) {
                FitsHeader header;
                try {
                    header = FitsFile.ReadHeader(frame);
                } catch (Exception ex) when (ex is IOException || ex is InvalidDataException) {
                    problems.Add($"{Path.GetFileName(frame)}: {ex.Message}");
                    continue;
                }
                first ??= header;
                var type = header.GetString("IMAGETYP");
                if (type != null && !type.Equals("DARK", StringComparison.OrdinalIgnoreCase)) {
                    problems.Add($"{Path.GetFileName(frame)}: IMAGETYP = {type}, expected DARK");
                }
                string name;
                try {
                    name = SirilPathTemplate.Resolve(MasterDarkFileTemplate, header);
                } catch (SirilPathTemplateException ex) {
                    problems.Add($"{Path.GetFileName(frame)}: cannot name the master ({ex.Message})");
                    continue;
                }
                if (masterName == null) {
                    masterName = name;
                } else if (name != masterName) {
                    problems.Add($"{Path.GetFileName(frame)} belongs to {name}, the set's first frame to {masterName}; one folder must hold one exposure/gain/offset/set-point/binning");
                } else {
                    // Same master name, but %d may have hidden a real difference (20.0 s and 20.5 s are both "20s")
                    foreach (var key in keys) {
                        var value = header.GetComparableValue(key);
                        var expected = first.GetComparableValue(key);
                        if (!string.Equals(value, expected, StringComparison.Ordinal)) {
                            problems.Add($"{Path.GetFileName(frame)} has {key} = {value}, the set's first frame {expected}; one folder must hold one exposure/gain/offset/set-point/binning");
                        }
                    }
                }
                var w = header.GetInt("NAXIS1");
                var h = header.GetInt("NAXIS2");
                if (width == null) {
                    width = w;
                    height = h;
                } else if (w != width || h != height) {
                    problems.Add($"{Path.GetFileName(frame)} is {w}x{h}, the set's first frame {width}x{height}");
                }
            }
            if (frames.Count < 3) {
                problems.Add($"{frames.Count} frame(s); rejection stacking needs at least 3 (30-50 recommended)");
            } else if (frames.Count < 15) {
                warnings.Add($"only {frames.Count} frames; 30-50 recommended for a library master");
            }
            var masterPath = masterName == null ? null : Path.Combine(MastersDirectory, masterName);
            return new DarkSet(directory, frames, first, masterPath, problems, warnings);
        }

        /// <summary>
        /// Siril script that stacks each set into its master (stock darks step of OSC_Preprocessing: convert, then
        /// "stack dark rej 3 3 -nonorm"). The -out name is the header-token template itself, so Siril names the master
        /// with exactly the formatting it uses when looking it up. Each set's process/&lt;set&gt; folder must not exist or be
        /// empty when the script runs (<see cref="BuildMastersAsync"/> cleans it): convert only overwrites dark_00001..N,
        /// and stack would pick up links left from a larger earlier build.
        /// </summary>
        public SirilScript CreateMasterBuildScript(IEnumerable<DarkSet> sets) {
            var list = sets.Where(s => s.CanBuild).ToList();
            if (list.Count == 0) {
                throw new InvalidOperationException("No buildable dark sets");
            }
            var outTemplate = Path.Combine(MastersDirectory, MasterDarkFileTemplate.Substring(0, MasterDarkFileTemplate.Length - ".fit".Length));
            var b = new SirilScriptBuilder(Root);
            b.Comment("Master darks for the NINA-mac dark library (generated by NINA.Mac.Siril)");
            b.Comment($"Library: {Root}");
            b.Blank();
            b.Command("requires", SirilScriptGenerator.RequiredSirilVersion);
            SirilScriptGenerator.PinOutputFormat(b); // .fit names, 32-bit float masters, no .fz whatever the seeded preferences say
            foreach (var set in list) {
                var process = Path.Combine(ProcessDirectory, set.Name);
                b.Blank();
                b.Comment($"{set.Name}: {set.Frames.Count} frames -> {set.MasterFileName}");
                b.CdAbsolute(set.Directory);
                b.Command("convert", "dark", "-out=" + process);
                b.CdAbsolute(process);
                b.Command("stack", "dark", "rej", "3", "3", "-nonorm", "-out=" + outTemplate);
            }
            b.Blank();
            b.Command("close");
            return new SirilScript(b.ToString(), Root, "nina_build_master_darks.ssf", list.SelectMany(s => s.Warnings.Select(w => $"{s.Name}: {w}")).ToList());
        }

        /// <summary>
        /// Scans the library, builds missing or stale masters with siril-cli, and checks Siril wrote the predicted files.
        /// The process/&lt;set&gt; folder of every set being built is deleted first (it only holds Siril's links and .seq).
        /// </summary>
        public async Task<DarkLibraryBuildResult> BuildMastersAsync(SirilRunner runner, bool rebuildAll = false, CancellationToken token = default) {
            var sets = ScanSets();
            var wanted = sets.Where(s => s.CanBuild && (rebuildAll || !s.MasterExists || s.MasterIsStale)).ToList();
            var skipped = sets.Except(wanted).ToList();
            if (wanted.Count == 0) {
                return new DarkLibraryBuildResult(wanted, skipped, null, Array.Empty<string>());
            }
            var script = CreateMasterBuildScript(wanted);
            System.IO.Directory.CreateDirectory(ProcessDirectory);
            foreach (var set in wanted) {
                CleanSetProcessDirectory(set);
            }
            var path = script.Save(Path.Combine(ProcessDirectory, script.FileName));
            var run = await runner.RunAsync(path, Root, Path.Combine(ProcessDirectory, "nina_build_master_darks.log"), token).ConfigureAwait(false);
            var missing = wanted.Where(s => !File.Exists(s.MasterPath)).Select(s => s.MasterPath).ToList();
            return new DarkLibraryBuildResult(wanted, skipped, run, missing);
        }

        /// <summary>Deletes process/&lt;set&gt; (Siril's links and sequence file for that set) and nothing else.</summary>
        internal void CleanSetProcessDirectory(DarkSet set) {
            var processRoot = Path.GetFullPath(ProcessDirectory);
            var folder = Path.GetFullPath(Path.Combine(processRoot, set.Name));
            if (string.IsNullOrEmpty(set.Name) || set.Name == "." || set.Name == ".." || set.Name.IndexOfAny(new[] { '/', '\\' }) >= 0
                    || !string.Equals(Path.GetDirectoryName(folder), processRoot, StringComparison.Ordinal)) {
                throw new InvalidOperationException($"Not cleaning {folder}: only <library>/process/<set> is cleaned");
            }
            if (System.IO.Directory.Exists(folder)) {
                // Directory.Delete removes the symlinks Siril's convert made, not the raw darks they point to
                System.IO.Directory.Delete(folder, recursive: true);
            }
        }
    }
}
