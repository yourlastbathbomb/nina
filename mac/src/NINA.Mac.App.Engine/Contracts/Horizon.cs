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
using System.Text;

namespace NINA.Mac.App.Services {

    /// <summary>One point of the local horizon: the lowest usable altitude at an azimuth (degrees, azimuth from north through east).</summary>
    public sealed record HorizonPoint(double Azimuth, double Altitude);

    /// <summary>
    /// The site's local horizon: altitude limits by azimuth, linearly interpolated (as NINA's CustomHorizon does) and wrapped
    /// through north. Read with the engine's reader (NINA.Mac.Sequencing's HorizonFile) and written in NINA's plain horizon
    /// file format (<c>.hrz</c>): one "azimuth altitude" pair per line, comments only on their own lines starting with '#'. The
    /// reader also accepts inline comments ("90 15  # tree"), which upstream's parser silently drops; the writer never produces
    /// them and always writes points at 0° and 360°, so a saved file reads the same in upstream NINA, the engine and the app.
    /// A file whose comments contain <see cref="EstimateMarker"/> is an estimate (the built-in default for Deep Water Bay):
    /// preflight flags it until the operator measures the real horizon and saves it from the Target screen.
    /// </summary>
    public sealed class HorizonProfile {

        /// <summary>Comment text that marks a horizon as an estimate rather than a measurement.</summary>
        public const string EstimateMarker = "nightglass: estimate";

        /// <summary>The file name of the app's horizon in its settings folder.</summary>
        public const string DefaultFileName = "horizon.hrz";

        public const string BuiltInSource = "built-in estimate";

        private readonly HorizonPoint[] points;

        public HorizonProfile(IEnumerable<HorizonPoint> points, bool isEstimate = false, string source = null) {
            ArgumentNullException.ThrowIfNull(points);
            var list = new SortedDictionary<double, double>();
            foreach (var p in points) {
                if (p == null || !double.IsFinite(p.Azimuth) || !double.IsFinite(p.Altitude)) {
                    throw new ArgumentException("Horizon points need finite azimuth and altitude");
                }
                if (p.Azimuth < 0 || p.Azimuth > 360) {
                    throw new ArgumentException($"Azimuth {p.Azimuth:0.#}° is outside 0-360°");
                }
                if (p.Altitude < -90 || p.Altitude > 90) {
                    throw new ArgumentException($"Altitude {p.Altitude:0.#}° at azimuth {p.Azimuth:0.#}° is outside -90° to 90°");
                }
                list[p.Azimuth] = p.Altitude;
            }
            if (list.Count < 2) {
                throw new ArgumentException("A horizon needs at least two points");
            }
            this.points = list.Select(kv => new HorizonPoint(kv.Key, kv.Value)).ToArray();
            IsEstimate = isEstimate;
            Source = source;
        }

        /// <summary>Sorted by azimuth, one altitude per azimuth.</summary>
        public IReadOnlyList<HorizonPoint> Points => points;

        /// <summary>True for the built-in estimate or a file marked as one: preflight says "measure it".</summary>
        public bool IsEstimate { get; }

        /// <summary>The file it came from, or <see cref="BuiltInSource"/>.</summary>
        public string Source { get; }

        public double MinAltitude => points.Min(p => p.Altitude);

        public double MaxAltitude => points.Max(p => p.Altitude);

        /// <summary>
        /// The estimate shipped for Deep Water Bay until the real horizon is measured: the south open down to about 15°, the
        /// north blocked (80° from azimuth 280° through north to 80°).
        /// </summary>
        public static HorizonProfile SiteEstimate() => new(new[] {
            new HorizonPoint(0, 80),
            new HorizonPoint(80, 80),
            new HorizonPoint(90, 15),
            new HorizonPoint(270, 15),
            new HorizonPoint(280, 80),
            new HorizonPoint(360, 80),
        }, isEstimate: true, source: BuiltInSource);

        /// <summary>Horizon altitude at <paramref name="azimuth"/>: linear between the neighbouring points, wrapping through north.</summary>
        public double GetAltitude(double azimuth) {
            var az = ((azimuth % 360) + 360) % 360;
            var first = points[0];
            var last = points[^1];
            if (az < first.Azimuth) {
                return Lerp(az + 360, last.Azimuth, last.Altitude, first.Azimuth + 360, first.Altitude);
            }
            if (az > last.Azimuth) {
                return Lerp(az, last.Azimuth, last.Altitude, first.Azimuth + 360, first.Altitude);
            }
            for (var i = 1; i < points.Length; i++) {
                if (az <= points[i].Azimuth) {
                    return Lerp(az, points[i - 1].Azimuth, points[i - 1].Altitude, points[i].Azimuth, points[i].Altitude);
                }
            }
            return last.Altitude;
        }

        /// <summary>True when the altitude is at or above the horizon at that azimuth.</summary>
        public bool IsAbove(double altitude, double azimuth) => altitude >= GetAltitude(azimuth);

        /// <summary>A copy with other points (an edit): an edited horizon is a measurement, no longer an estimate unless kept so.</summary>
        public HorizonProfile With(IEnumerable<HorizonPoint> newPoints, bool isEstimate, string source = null) => new(newPoints, isEstimate, source ?? Source);

        /// <summary>
        /// The file text in NINA's plain format: comment lines first, then one "azimuth altitude" pair per line, always with
        /// points at 0° and 360° so upstream's parser (which fills a missing 0/360 from the nearest point) reads the same
        /// horizon as <see cref="GetAltitude"/>.
        /// </summary>
        public string ToFileText(string siteName = null, DateTimeOffset? saved = null) {
            var text = new StringBuilder();
            text.Append("# Nightglass local horizon").Append(string.IsNullOrWhiteSpace(siteName) ? "" : " for " + siteName.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
            text.Append("# Format: azimuth altitude (degrees; azimuth from north through east), one pair per line. Comments on their own lines only.\n");
            if (saved is { } when) {
                text.Append("# Saved ").Append(when.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)).Append('\n');
            }
            if (IsEstimate) {
                text.Append("# ").Append(EstimateMarker).Append(": ESTIMATE ONLY, not measured. Measure the real horizon (Target › Horizon) and save it.\n");
            }
            var output = new SortedDictionary<double, double>();
            foreach (var p in points) {
                output[p.Azimuth] = p.Altitude;
            }
            if (!output.ContainsKey(0)) {
                output[0] = Math.Round(GetAltitude(0), 2);
            }
            if (!output.ContainsKey(360)) {
                output[360] = output[0];
            }
            foreach (var (az, alt) in output) {
                text.Append(az.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ').Append(alt.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
            }
            return text.ToString();
        }

        /// <summary>
        /// Parses NINA's plain horizon format with the engine's reader (NINA.Mac.Sequencing's HorizonFile, over NINA.Mac.RigTools'
        /// parser), so the app shows exactly the horizon NINA's sequence uses: tabs, spaces, ',' or ';' between the two numbers,
        /// inline '#' comments, and upstream's grooming (points at 0° and 360° copied from the nearest end when missing). Lines that
        /// are not two numbers in range are skipped and reported in <paramref name="warnings"/> with their line numbers. Throws
        /// <see cref="FormatException"/> when fewer than two points remain.
        /// </summary>
        public static HorizonProfile Parse(string text, string source, out IReadOnlyList<string> warnings) {
            ArgumentNullException.ThrowIfNull(text);
            var result = NINA.Mac.Sequencing.Horizon.HorizonFile.Parse(text, source ?? "(text)");
            if (!result.Succeeded) {
                warnings = result.RejectedLines.Select(i => i.ToString()).ToList();
                throw new FormatException($"Horizon {source ?? "text"}: {result.Error}" + (result.RejectedLines.Count > 0 ? $" ({result.RejectedLines[0]})" : ""));
            }
            warnings = result.RejectedLines.Concat(result.Warnings).Select(i => i.ToString()).ToList();
            var estimate = text.Split('\n').Any(line => line.IndexOf('#') is var hash && hash >= 0 && line[(hash + 1)..].Contains(EstimateMarker, StringComparison.OrdinalIgnoreCase));
            return new HorizonProfile(result.Points.Select(p => new HorizonPoint(p.AzimuthDeg, p.AltitudeDeg)), estimate, source);
        }

        public static HorizonProfile Load(string path, out IReadOnlyList<string> warnings) {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return Parse(File.ReadAllText(path), path, out warnings);
        }

        /// <summary>Writes <see cref="ToFileText"/> atomically (temp file, then move) and returns the saved profile.</summary>
        public HorizonProfile Save(string path, string siteName = null, DateTimeOffset? saved = null) {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, ToFileText(siteName, saved));
            File.Move(temp, path, overwrite: true);
            return new HorizonProfile(points, IsEstimate, path);
        }

        /// <summary>One line for status texts: point count, lowest and highest altitude, source.</summary>
        public string Describe() =>
            string.Format(CultureInfo.InvariantCulture, "{0} points, {1:0.#}°-{2:0.#}°{3}, {4}", points.Length, MinAltitude, MaxAltitude,
                IsEstimate ? " (ESTIMATE)" : "", Source ?? "unsaved");

        private static double Lerp(double x, double x0, double y0, double x1, double y1) =>
            x1 - x0 < 1e-9 ? Math.Max(y0, y1) : y0 + ((y1 - y0) * (x - x0) / (x1 - x0));
    }

    /// <summary>
    /// The app's slew guard, shared by the simulated and the Real mount: refuses a goto below the mathematical horizon, below
    /// the local horizon (<see cref="HorizonProfile"/>, when one is set) or above the zenith keyhole limit, before anything is
    /// sent to the mount.
    /// </summary>
    public static class SlewGuard {

        public static void Check(double altitude, double azimuth, AppSettings limits, HorizonProfile horizon) {
            ArgumentNullException.ThrowIfNull(limits);
            if (altitude < 0) {
                throw new InvalidOperationException($"Slew refused: target is below the horizon (altitude {altitude:0.0}°)");
            }
            if (horizon != null) {
                var local = horizon.GetAltitude(azimuth);
                if (altitude < local) {
                    throw new InvalidOperationException(
                        $"Slew refused: altitude {altitude:0.0}° at azimuth {azimuth:0}° is below the local horizon there ({local:0}°{(horizon.IsEstimate ? ", estimated" : "")}; Target › Horizon)");
                }
            }
            if (altitude > limits.MaxAltitudeDegrees) {
                throw new InvalidOperationException($"Slew refused: altitude {altitude:0.0}° is above the {limits.MaxAltitudeDegrees:0}° keyhole limit");
            }
        }
    }
}
