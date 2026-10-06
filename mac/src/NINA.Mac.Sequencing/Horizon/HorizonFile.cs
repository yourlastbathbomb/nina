#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RigHorizon = NINA.Mac.RigTools.Horizon;

namespace NINA.Mac.Sequencing.Horizon {

    /// <summary>A horizon file read by <see cref="HorizonFile"/>: NINA's <see cref="CustomHorizon"/> plus everything the reader had to say.</summary>
    public sealed class HorizonLoadResult {

        internal HorizonLoadResult(string source, CustomHorizon horizon, IReadOnlyList<RigHorizon.HorizonPoint> points,
                                   IReadOnlyList<RigHorizon.HorizonParseIssue> rejectedLines, IReadOnlyList<RigHorizon.HorizonParseIssue> warnings,
                                   string error) {
            Source = source;
            Horizon = horizon;
            Points = points ?? Array.Empty<RigHorizon.HorizonPoint>();
            RejectedLines = rejectedLines ?? Array.Empty<RigHorizon.HorizonParseIssue>();
            Warnings = warnings ?? Array.Empty<RigHorizon.HorizonParseIssue>();
            Error = error;
        }

        /// <summary>The file path (or a description of the text).</summary>
        public string Source { get; }

        /// <summary>NINA's horizon built from the points; null when the file could not be used (<see cref="Error"/>).</summary>
        public CustomHorizon Horizon { get; }

        /// <summary>The points as NINA uses them: sorted by azimuth, with the 0° and 360° ends NINA's grooming adds.</summary>
        public IReadOnlyList<RigHorizon.HorizonPoint> Points { get; }

        /// <summary>Lines that gave no point, each with its line number and why. Upstream NINA only logs these (or drops them silently).</summary>
        public IReadOnlyList<RigHorizon.HorizonParseIssue> RejectedLines { get; }

        /// <summary>Accepted input worth a look: a repeated azimuth, or no point at 0 or 360 (line 0).</summary>
        public IReadOnlyList<RigHorizon.HorizonParseIssue> Warnings { get; }

        /// <summary>Why the file could not be used at all (missing, fewer than two points, bad MW4 JSON); null when it loaded.</summary>
        public string Error { get; }

        public bool Succeeded => Horizon != null;

        /// <summary>One line for a log or a status bar.</summary>
        public string Summary {
            get {
                if (!Succeeded) {
                    return $"Horizon {Source}: not loaded: {Error}";
                }
                var sb = new StringBuilder();
                sb.Append(CultureInfo.InvariantCulture, $"Horizon {Source}: {Points.Count} points, {Points.Min(p => p.AltitudeDeg):0.#}° to {Points.Max(p => p.AltitudeDeg):0.#}°");
                if (RejectedLines.Count > 0) {
                    sb.Append(CultureInfo.InvariantCulture, $"; {RejectedLines.Count} line(s) not read: ");
                    sb.Append(string.Join("; ", RejectedLines.Select(i => i.ToString())));
                }
                if (Warnings.Count > 0) {
                    sb.Append("; ").Append(string.Join("; ", Warnings.Select(i => i.ToString())));
                }
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Reads and writes NINA horizon files for the engine (MAC_PORT_PLAN section 6, "Horizon"). Upstream's reader
    /// (CustomHorizon.FromReader_Standard) treats only lines that start with '#' as comments, so "180 4  # over the water" has
    /// four columns and the point is dropped with a log line nobody sees. Upstream is not changed (Windows keeps its behaviour);
    /// instead the mac engine reads the file with NINA.Mac.RigTools' parser, which accepts whole-line and inline comments and
    /// blank lines and reports every line it could not read with its line number, and builds NINA's own <see cref="CustomHorizon"/>
    /// from the points by feeding upstream's reader a clean "azimuth altitude" text. Everything that consults the profile's
    /// horizon (AboveHorizonCondition, WaitUntilAboveHorizon, DeepSkyObjectContainer, the PlanValidator and the LX200 driver's
    /// slew guard) then sees the same horizon the app shows. <see cref="Save"/> writes only what upstream's reader reads
    /// correctly, so a saved file means the same in Windows NINA.
    /// </summary>
    public static class HorizonFile {

        /// <summary>Reads a horizon file (".hpts": MountWizzard4 JSON, as upstream; anything else: NINA's "azimuth altitude" lines).</summary>
        public static HorizonLoadResult Load(string path) {
            if (string.IsNullOrWhiteSpace(path)) {
                return new HorizonLoadResult(path ?? string.Empty, null, null, null, null, "no horizon file is set");
            }
            try {
                return FromParse(path, RigHorizon.HorizonFile.Load(path));
            } catch (FileNotFoundException) {
                return new HorizonLoadResult(path, null, null, null, null, "the file does not exist");
            } catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or FormatException) {
                return new HorizonLoadResult(path, null, null, null, null, ex.Message);
            }
        }

        /// <summary>Reads NINA's "azimuth altitude" text (comments allowed anywhere after '#').</summary>
        public static HorizonLoadResult Parse(string text, string source = "(text)") {
            try {
                using var reader = new StringReader(text ?? string.Empty);
                return FromParse(source, RigHorizon.HorizonFile.ParseStandard(reader, source));
            } catch (ArgumentException ex) {
                return new HorizonLoadResult(source, null, null, null, null, ex.Message);
            }
        }

        private static HorizonLoadResult FromParse(string source, RigHorizon.HorizonParseResult parsed) {
            var points = parsed.Profile.Points;
            using var clean = new StringReader(ToNinaText(points, null));
            var horizon = CustomHorizon.FromReader_Standard(clean);
            return new HorizonLoadResult(source, horizon, points, parsed.RejectedLines, parsed.Warnings, null);
        }

        /// <summary>
        /// Writes a horizon file that upstream NINA reads exactly as this engine does: comments only on their own lines (each
        /// line of <paramref name="comments"/> gets a leading "# "), then one "azimuth altitude" pair per line in invariant
        /// culture with the shortest round-trip number format, sorted by azimuth. Azimuth 0..360, altitude -90..90, finite, at
        /// least two distinct azimuths; otherwise <see cref="ArgumentException"/>. Written to a temporary file first and moved
        /// over the target, so a reader never sees half a file.
        /// </summary>
        public static void Save(string path, IEnumerable<(double AzimuthDeg, double AltitudeDeg)> points, IEnumerable<string> comments = null) {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(points);
            var list = new List<RigHorizon.HorizonPoint>();
            foreach (var (az, alt) in points) {
                if (!double.IsFinite(az) || az < 0 || az > 360) {
                    throw new ArgumentException(FormattableString.Invariant($"azimuth {az} is outside 0..360"), nameof(points));
                }
                if (!double.IsFinite(alt) || alt < -90 || alt > 90) {
                    throw new ArgumentException(FormattableString.Invariant($"altitude {alt} at azimuth {az} is outside -90..90"), nameof(points));
                }
                list.Add(new RigHorizon.HorizonPoint(az, alt));
            }
            var distinct = list.GroupBy(p => p.AzimuthDeg).Select(g => g.Last()).OrderBy(p => p.AzimuthDeg).ToList();
            if (distinct.Count < 2) {
                throw new ArgumentException("a horizon needs at least two points with different azimuths", nameof(points));
            }
            var text = ToNinaText(distinct, comments);
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, full, overwrite: true);
        }

        /// <summary>Saves the points of a loaded horizon (NINA's groomed points, so 0 and 360 are both written).</summary>
        public static void Save(string path, HorizonLoadResult horizon, IEnumerable<string> comments = null) {
            ArgumentNullException.ThrowIfNull(horizon);
            if (!horizon.Succeeded) {
                throw new ArgumentException($"the horizon was not loaded: {horizon.Error}", nameof(horizon));
            }
            Save(path, horizon.Points.Select(p => (p.AzimuthDeg, p.AltitudeDeg)), comments);
        }

        private static string ToNinaText(IEnumerable<RigHorizon.HorizonPoint> points, IEnumerable<string> comments) {
            var sb = new StringBuilder();
            foreach (var comment in comments ?? Enumerable.Empty<string>()) {
                foreach (var line in (comment ?? string.Empty).Replace("\r\n", "\n").Split('\n')) {
                    sb.Append("# ").Append(line.Trim()).Append('\n');
                }
            }
            foreach (var p in points) {
                sb.Append(p.AzimuthDeg.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(p.AltitudeDeg.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Loads the active profile's <c>AstrometrySettings.HorizonFilePath</c> with this reader and sets
        /// <c>AstrometrySettings.Horizon</c>. Rejected lines are logged and shown as a warning naming each line; a file that
        /// cannot be used is an error notification, and the horizon is then none (the path is kept, so the settings still show
        /// which file failed). With no file set, the horizon is cleared only when <paramref name="clearWhenNoFile"/> (as upstream's
        /// ChangeHorizon does for an empty path); otherwise a horizon a host set in memory stays. Returns what was read.
        /// </summary>
        public static HorizonLoadResult ApplyToProfile(IProfileService profileService, bool notify = true, bool clearWhenNoFile = false) {
            ArgumentNullException.ThrowIfNull(profileService);
            var astrometry = profileService.ActiveProfile?.AstrometrySettings;
            if (astrometry == null) {
                return new HorizonLoadResult(string.Empty, null, null, null, null, "no active profile");
            }
            var path = astrometry.HorizonFilePath;
            if (string.IsNullOrWhiteSpace(path)) {
                if (clearWhenNoFile) {
                    astrometry.Horizon = null;
                }
                return new HorizonLoadResult(string.Empty, null, null, null, null, "no horizon file is set");
            }
            var result = Load(path);
            astrometry.Horizon = result.Horizon;
            if (!result.Succeeded) {
                Logger.Error(result.Summary);
                if (notify) {
                    Notification.ShowError($"{result.Summary}. Gotos, horizon waits and the plan check use no horizon (0°) until it is fixed.");
                }
            } else if (result.RejectedLines.Count > 0) {
                Logger.Warning(result.Summary);
                if (notify) {
                    Notification.ShowWarning(result.Summary);
                }
            } else {
                Logger.Info(result.Summary);
            }
            return result;
        }
    }

    /// <summary>
    /// Keeps the profile's horizon loaded with <see cref="HorizonFile"/>: once at start (replacing what upstream's reader made
    /// of the file when the profile was deserialised), whenever <c>HorizonFilePath</c> changes, when the profile service raises
    /// HorizonChanged (a new file at the same path), and when the active profile changes. Owned by <see cref="Headless.HeadlessHost"/>.
    /// </summary>
    public sealed class ProfileHorizonWatcher : IDisposable {
        private readonly IProfileService profileService;
        private readonly object sync = new();
        private INotifyPropertyChanged watchedSettings;
        private string loadedKey;

        public ProfileHorizonWatcher(IProfileService profileService) {
            this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            profileService.HorizonChanged += OnHorizonChanged;
            profileService.ProfileChanged += OnProfileChanged;
            Watch();
            Reload(force: true);
        }

        /// <summary>The last load, with its rejected lines and warnings.</summary>
        public HorizonLoadResult Last { get; private set; }

        /// <summary>Raised after every load (on the thread that caused it).</summary>
        public event EventHandler<HorizonLoadResult> Loaded;

        private void Watch() {
            lock (sync) {
                if (watchedSettings != null) {
                    watchedSettings.PropertyChanged -= OnSettingsChanged;
                }
                watchedSettings = profileService.ActiveProfile?.AstrometrySettings as INotifyPropertyChanged;
                if (watchedSettings != null) {
                    watchedSettings.PropertyChanged += OnSettingsChanged;
                }
            }
        }

        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName == nameof(IAstrometrySettings.HorizonFilePath)) {
                Reload(force: false, clearWhenNoFile: true);
            }
        }

        private void OnHorizonChanged(object sender, EventArgs e) => Reload(force: false, clearWhenNoFile: true);

        private void OnProfileChanged(object sender, EventArgs e) {
            Watch();
            Reload(force: true);
        }

        /// <summary>
        /// Reloads unless <paramref name="force"/> is off and the same file (path, size, write time) was loaded last. With no file
        /// set, an in-memory horizon is cleared only when <paramref name="clearWhenNoFile"/> (the path was just emptied).
        /// </summary>
        public HorizonLoadResult Reload(bool force = true, bool clearWhenNoFile = false) {
            HorizonLoadResult result;
            lock (sync) {
                var key = Key(profileService.ActiveProfile?.AstrometrySettings?.HorizonFilePath);
                if (!force && key == loadedKey && Last != null) {
                    return Last;
                }
                result = HorizonFile.ApplyToProfile(profileService, clearWhenNoFile: clearWhenNoFile);
                loadedKey = key;
                Last = result;
            }
            Loaded?.Invoke(this, result);
            return result;
        }

        private static string Key(string path) {
            if (string.IsNullOrWhiteSpace(path)) {
                return string.Empty;
            }
            try {
                var info = new FileInfo(path);
                return info.Exists ? FormattableString.Invariant($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}") : path + "|missing";
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
                return path + "|unreadable";
            }
        }

        public void Dispose() {
            profileService.HorizonChanged -= OnHorizonChanged;
            profileService.ProfileChanged -= OnProfileChanged;
            lock (sync) {
                if (watchedSettings != null) {
                    watchedSettings.PropertyChanged -= OnSettingsChanged;
                    watchedSettings = null;
                }
            }
        }
    }
}
