#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NINA.PlateSolving.Mac {

    /// <summary>
    /// Mac-only: where the local astrometry.net solver (PlateSolverEnum.LOCAL / BlindSolverEnum.LOCAL, the mac
    /// MacReplacements/LocalPlateSolver) finds its programs and index files, and the astrometry.cfg it hands to solve-field.
    /// <para>
    /// NINA's profile has a single setting for this solver, IPlateSolveSettings.CygwinLocation. On macOS it names the folder
    /// that holds solve-field and wcsinfo (empty means Homebrew's /opt/homebrew/bin). Everything else is set here by the host
    /// at startup. The solver writes its own astrometry.cfg (<see cref="ConfigFilePath"/>) before every solve: Homebrew's
    /// etc/astrometry.cfg and the index folders are only read, never written.
    /// </para>
    /// </summary>
    public static class AstrometryNetSetup {

        /// <summary>Homebrew's bin folder on Apple Silicon (astrometry-net 0.97 and netpbm, which solve-field calls).</summary>
        public const string HomebrewBinDirectory = "/opt/homebrew/bin";

        /// <summary>The config file name inside <see cref="ConfigDirectory"/>.</summary>
        public const string ConfigFileName = "astrometry.cfg";

        private static readonly object configLock = new object();
        private static readonly Regex series4200 = new Regex(@"^index-42(?<scale>\d\d)(?:-(?<tile>\d\d))?\.fits$", RegexOptions.CultureInvariant);
        private static string lastReportedGap;
        private static IReadOnlyList<string> indexDirectories = new[] { DefaultIndexDirectory };
        private static string configDirectory;
        private static int cpuLimitSeconds = 300;

        /// <summary>~/Library/Application Support/Astrometry, where this Mac keeps its 4200-series index files.</summary>
        public static string DefaultIndexDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Astrometry");

        /// <summary>
        /// Folders searched for index-*.fits files (written as add_path lines; autoindex loads what they hold). Paths may contain
        /// spaces; they must not contain line breaks or end in whitespace, which astrometry.cfg cannot express.
        /// </summary>
        public static IReadOnlyList<string> IndexDirectories {
            get => indexDirectories;
            set {
                var list = (value ?? throw new ArgumentNullException(nameof(value))).ToList();
                foreach (var dir in list) {
                    ValidateConfigPath(dir, nameof(IndexDirectories));
                }
                indexDirectories = list;
            }
        }

        /// <summary>
        /// Folder that holds the generated astrometry.cfg. Defaults to &lt;CoreUtil.APPLICATIONTEMPPATH&gt;/Solvers, outside the
        /// PlateSolver working folder that NINA cleans up.
        /// </summary>
        public static string ConfigDirectory {
            get => configDirectory ?? Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Solvers");
            set => configDirectory = value;
        }

        public static string ConfigFilePath => Path.Combine(ConfigDirectory, ConfigFileName);

        /// <summary>
        /// astrometry-engine's cpulimit for one field (Homebrew's default config says 300). The engine checks it only now and then:
        /// on this M1, a rig-size near solve that could not succeed stopped with "Total CPU time limit reached!" after 29.5 s of CPU
        /// for a limit of 5 and 42 s for 15, and with 60 it ran into the 120 s timeout. So for a failing solve the solver's own
        /// timeout, this plus <see cref="TimeoutMargin"/>, is usually what ends it: CLISolver kills solve-field and its engine and
        /// archives the files.
        /// </summary>
        public static int CpuLimitSeconds {
            get => cpuLimitSeconds;
            set => cpuLimitSeconds = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "must be positive");
        }

        public static readonly TimeSpan TimeoutMargin = TimeSpan.FromSeconds(60);

        public static TimeSpan SolverTimeout => TimeSpan.FromSeconds(CpuLimitSeconds) + TimeoutMargin;

        /// <summary>
        /// The folder holding solve-field and wcsinfo for a CygwinLocation value: empty means <see cref="HomebrewBinDirectory"/>;
        /// a leading ~/ is the home folder; a path to the solve-field executable itself means its folder.
        /// </summary>
        public static string ResolveBinDirectory(string cygwinLocation) {
            if (string.IsNullOrWhiteSpace(cygwinLocation)) {
                return HomebrewBinDirectory;
            }
            var path = cygwinLocation.Trim();
            if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal)) {
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : string.Empty);
            }
            if (string.Equals(Path.GetFileName(path.TrimEnd('/')), "solve-field", StringComparison.Ordinal) && File.Exists(path)) {
                path = Path.GetDirectoryName(path.TrimEnd('/'));
            }
            return path.Length > 1 ? path.TrimEnd('/') : path;
        }

        /// <summary>The index files (index-*.fits) in <see cref="IndexDirectories"/>, sorted.</summary>
        public static IReadOnlyList<string> FindIndexFiles() {
            return IndexDirectories
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.GetFiles(dir, "index-*.fits"))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// How many files one scale of astrometry.net's 4200 series (2MASS) has: scales 00-04 are split into 48 HEALPix tiles
        /// (index-42SS-00 to -47), 05-07 into 12 (-00 to -11), and 08-19 are a single file (index-42SS.fits).
        /// </summary>
        public static int TilesPerScale4200(int scale) => scale <= 4 ? 48 : scale <= 7 ? 12 : 1;

        /// <summary>
        /// The 4200-series index files that are missing from a scale that is partly installed in <see cref="IndexDirectories"/>
        /// (all folders together), as file names, sorted. A scale with no file at all is a choice, not a gap, and is not listed.
        /// solve-field cannot solve a field whose sky region lies in a missing tile at that scale, and fails without saying why.
        /// </summary>
        public static IReadOnlyList<string> FindMissingIndexTiles() {
            var present = new Dictionary<int, HashSet<int>>();
            foreach (var file in FindIndexFiles()) {
                var match = series4200.Match(Path.GetFileName(file));
                if (!match.Success) {
                    continue;
                }
                var scale = int.Parse(match.Groups["scale"].Value, CultureInfo.InvariantCulture);
                if (!present.TryGetValue(scale, out var tiles)) {
                    present[scale] = tiles = new HashSet<int>();
                }
                if (match.Groups["tile"].Success) {
                    tiles.Add(int.Parse(match.Groups["tile"].Value, CultureInfo.InvariantCulture));
                }
            }
            var missing = new List<string>();
            foreach (var (scale, tiles) in present.OrderBy(p => p.Key)) {
                var count = TilesPerScale4200(scale);
                if (count == 1) {
                    continue;
                }
                for (var tile = 0; tile < count; tile++) {
                    if (!tiles.Contains(tile)) {
                        missing.Add(string.Create(CultureInfo.InvariantCulture, $"index-42{scale:00}-{tile:00}.fits"));
                    }
                }
            }
            return missing;
        }

        /// <summary>
        /// A startup or settings check for the host: null when every partly installed 4200-series scale is complete, otherwise a
        /// message that names the missing tiles (<see cref="FindMissingIndexTiles"/>) and says where they go. Nightglass never
        /// downloads them itself.
        /// </summary>
        public static string DescribeMissingIndexTiles() {
            var missing = FindMissingIndexTiles();
            if (missing.Count == 0) {
                return null;
            }
            return string.Format(CultureInfo.InvariantCulture,
                "astrometry.net index incomplete: {0} tile{1} missing ({2}) in {3}. solve-field cannot solve fields in those sky regions at those scales; " +
                "download the missing files from data.astrometry.net/4200/ into that folder.",
                missing.Count, missing.Count == 1 ? "" : "s", string.Join(", ", missing.Select(f => Path.GetFileNameWithoutExtension(f))),
                string.Join(", ", IndexDirectories.Select(d => $"'{d}'")));
        }

        /// <summary>The astrometry.cfg text for the current settings: cpulimit, one add_path per index folder, autoindex.</summary>
        public static string BuildConfig() {
            var text = new StringBuilder();
            text.Append("# astrometry.net engine configuration written by Nightglass (NINA.Platesolving on macOS) before every local solve.\n");
            text.Append("# Edits are overwritten. Homebrew's own astrometry.cfg is not used; the index folders below are only read.\n");
            text.Append("cpulimit ").Append(CpuLimitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            foreach (var dir in IndexDirectories) {
                // astrometry.cfg takes the rest of the line verbatim (spaces included); trailing whitespace would become part of the path
                text.Append("add_path ").Append(dir).Append('\n');
            }
            text.Append("autoindex\n");
            return text.ToString();
        }

        /// <summary>
        /// Writes <see cref="BuildConfig"/> to <see cref="ConfigFilePath"/> (only when the text changed) and returns the path.
        /// Throws <see cref="InvalidOperationException"/> when no index folder holds an index-*.fits file, because solve-field
        /// would then run until its CPU limit and fail without saying why. Missing 4200-series tiles
        /// (<see cref="DescribeMissingIndexTiles"/>) are logged as a warning, once per distinct gap.
        /// </summary>
        public static string WriteConfig() {
            var indexFiles = FindIndexFiles();
            if (indexFiles.Count == 0) {
                throw new InvalidOperationException(
                    $"astrometry.net: no index files (index-*.fits) found in {string.Join(", ", IndexDirectories.Select(d => $"'{d}'"))}. " +
                    "Download the index files that match the field size, or set AstrometryNetSetup.IndexDirectories.");
            }
            var gap = DescribeMissingIndexTiles();
            if (gap != null && !string.Equals(Interlocked.Exchange(ref lastReportedGap, gap), gap, StringComparison.Ordinal)) {
                Logger.Warning(gap);
            }
            var path = ConfigFilePath;
            var text = BuildConfig();
            lock (configLock) {
                if (File.Exists(path) && File.ReadAllText(path) == text) {
                    return path;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            Logger.Debug($"astrometry.net config written to {path} ({indexFiles.Count} index files)");
            return path;
        }

        private static void ValidateConfigPath(string path, string name) {
            if (string.IsNullOrWhiteSpace(path)) {
                throw new ArgumentException("An index folder must not be empty", name);
            }
            if (path.IndexOfAny(new[] { '\n', '\r' }) >= 0 || char.IsWhiteSpace(path[^1]) || char.IsWhiteSpace(path[0])) {
                throw new ArgumentException($"astrometry.cfg cannot express the folder '{path}' (line break, or leading or trailing whitespace)", name);
            }
        }
    }
}
