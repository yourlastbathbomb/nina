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
using System.Text.Json;

namespace NINA.Mac.RigTools.Horizon {

    /// <summary>A line the parser did not turn into a point, or a point that replaced an earlier one.</summary>
    public sealed class HorizonParseIssue {

        public HorizonParseIssue(int lineNumber, string line, string reason) {
            LineNumber = lineNumber;
            Line = line;
            Reason = reason;
        }

        /// <summary>1-based line number in the file, or 0 for an issue about the file as a whole.</summary>
        public int LineNumber { get; }

        /// <summary>The raw line, or empty for a whole-file issue.</summary>
        public string Line { get; }

        public string Reason { get; }

        public override string ToString() {
            return LineNumber > 0 ? $"line {LineNumber}: {Reason}: '{Line}'" : Reason;
        }
    }

    /// <summary>Profile plus everything the parser had to say about the file.</summary>
    public sealed class HorizonParseResult {

        public HorizonParseResult(HorizonProfile profile, IReadOnlyList<HorizonParseIssue> rejectedLines, IReadOnlyList<HorizonParseIssue> warnings, int pointCount) {
            Profile = profile;
            RejectedLines = rejectedLines;
            Warnings = warnings;
            PointCount = pointCount;
        }

        public HorizonProfile Profile { get; }

        /// <summary>Non-blank, non-comment lines that did not give a point. Upstream only logs these.</summary>
        public IReadOnlyList<HorizonParseIssue> RejectedLines { get; }

        /// <summary>
        /// Accepted input worth a look: a duplicate azimuth that replaced an earlier altitude, or (line 0) a file
        /// with no point at 0 or 360, whose wrap across north is copied rather than interpolated.
        /// </summary>
        public IReadOnlyList<HorizonParseIssue> Warnings { get; }

        /// <summary>Distinct points read from the file, before 0/360 grooming.</summary>
        public int PointCount { get; }
    }

    /// <summary>
    /// Reader for NINA horizon files.
    /// <para>
    /// Standard format (any extension except .hpts), as upstream NINA.Core/Model/CustomHorizon.cs:93-119:
    /// one "azimuth altitude" pair per line, separated by tab, space, comma or semicolon, invariant-culture
    /// numbers, '#' comments. Differences from upstream, all deliberate:
    /// </para>
    /// <list type="number">
    /// <item>Inline comments work: everything from the first '#' is dropped before splitting. Upstream treats
    /// only lines that start with '#' as comments, so "180 4  # over water" splits into more than two tokens
    /// and the point is silently lost (CustomHorizon.cs:99-101,111-112; research/rig_verify_solve.md SLV-13).</item>
    /// <item>Rejected lines are returned with line number and reason (upstream logs a warning and moves on), and
    /// <paramref name="strict"/> turns them into an error.</item>
    /// <item>Non-finite numbers ("NaN", "Infinity"), azimuths outside [0, 360] and altitudes outside [-90, 90] are
    /// rejected; upstream accepts them.</item>
    /// <item>A repeated azimuth still replaces the earlier altitude (upstream SortedDictionary behaviour) but is
    /// reported as a warning.</item>
    /// <item>A file with no point at 0 or 360 still gets upstream's grooming (both ends copy the altitude of the
    /// point nearest an end, not a line across north), but is reported as a warning naming the copied altitude
    /// and what a straight line across north would give (<see cref="MissingWrapWarning"/>). The MW4 reader does
    /// the same.</item>
    /// </list>
    /// <para>
    /// MountWizzard4 format (.hpts): a JSON array of [altitude, azimuth] pairs, with the structure and range checks
    /// and messages of CustomHorizon.FromReader_MW4 (CustomHorizon.cs:121-156). Like upstream's Newtonsoft
    /// JToken.Value&lt;double&gt;(), a coordinate may be a JSON number or a numeric string ("10", " 1e1 "), and
    /// comments and trailing commas are tolerated. Deliberate differences, all of which upstream either loads
    /// silently or fails on with an unhelpful exception:
    /// </para>
    /// <list type="bullet">
    /// <item>Malformed JSON is an error. Newtonsoft loads a truncated file ("[[10,0],[20,180]") or ignores text
    /// after the array, so a damaged file would quietly give a partial profile.</item>
    /// <item>true/false (upstream converts them to 1/0), null, nested arrays/objects and NaN are rejected.</item>
    /// <item>Every failure, including malformed JSON and an empty file, is an <see cref="ArgumentException"/>
    /// (upstream can throw JsonReaderException, InvalidCastException, FormatException or NullReferenceException).</item>
    /// </list>
    /// </summary>
    public static class HorizonFile {

        /// <summary>Token separators, as upstream CustomHorizon.cs:100.</summary>
        public static readonly char[] Separators = { '\t', ' ', ',', ';' };

        public const string PlaceholderResourceName = "NINA.Mac.RigTools.Horizon.DeepWaterBay.placeholder.hrz";

        /// <summary>Loads a horizon file; ".hpts" selects the MountWizzard4 JSON reader like upstream FromFilePath.</summary>
        public static HorizonParseResult Load(string filePath, bool strict = false) {
            if (!File.Exists(filePath)) {
                throw new FileNotFoundException("Horizon file not found", filePath);
            }
            using (var reader = new StreamReader(filePath)) {
                if (string.Equals(Path.GetExtension(filePath), ".hpts", StringComparison.Ordinal)) {
                    return ParseMw4(reader, filePath);
                }
                return ParseStandard(reader, filePath, strict);
            }
        }

        /// <summary>Removes an inline '#' comment and surrounding whitespace.</summary>
        public static string StripComment(string line) {
            if (line == null) { return string.Empty; }
            var hash = line.IndexOf('#');
            return (hash >= 0 ? line.Substring(0, hash) : line).Trim();
        }

        /// <summary>Parses the standard NINA format. See the class remarks for the differences from upstream.</summary>
        /// <exception cref="FormatException">strict is set and at least one line was rejected.</exception>
        /// <exception cref="ArgumentException">Fewer than two points (same message as upstream).</exception>
        public static HorizonParseResult ParseStandard(TextReader reader, string source = null, bool strict = false, bool isPlaceholder = false) {
            if (reader == null) { throw new ArgumentNullException(nameof(reader)); }
            var map = new SortedDictionary<double, double>();
            var firstLineOf = new Dictionary<double, int>();
            var rejected = new List<HorizonParseIssue>();
            var warnings = new List<HorizonParseIssue>();

            string raw;
            var lineNumber = 0;
            while ((raw = reader.ReadLine()) != null) {
                lineNumber++;
                var line = StripComment(raw);
                if (line.Length == 0) {
                    continue;
                }
                var columns = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length != 2) {
                    rejected.Add(new HorizonParseIssue(lineNumber, raw, $"expected 2 values (azimuth altitude), found {columns.Length}"));
                    continue;
                }
                // NumberStyles.Any + invariant culture, as upstream CustomHorizon.cs:102-103.
                if (!double.TryParse(columns[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var azimuth) || !double.IsFinite(azimuth)) {
                    rejected.Add(new HorizonParseIssue(lineNumber, raw, $"invalid azimuth '{columns[0]}'"));
                    continue;
                }
                if (!double.TryParse(columns[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var altitude) || !double.IsFinite(altitude)) {
                    rejected.Add(new HorizonParseIssue(lineNumber, raw, $"invalid altitude '{columns[1]}'"));
                    continue;
                }
                if (azimuth < 0 || azimuth > 360) {
                    rejected.Add(new HorizonParseIssue(lineNumber, raw, $"azimuth {azimuth.ToString(CultureInfo.InvariantCulture)} outside [0, 360]"));
                    continue;
                }
                if (altitude < -90 || altitude > 90) {
                    rejected.Add(new HorizonParseIssue(lineNumber, raw, $"altitude {altitude.ToString(CultureInfo.InvariantCulture)} outside [-90, 90]"));
                    continue;
                }
                if (firstLineOf.TryGetValue(azimuth, out var earlier)) {
                    warnings.Add(new HorizonParseIssue(lineNumber, raw, $"azimuth {azimuth.ToString(CultureInfo.InvariantCulture)} repeats line {earlier}; this altitude replaces it"));
                } else {
                    firstLineOf[azimuth] = lineNumber;
                }
                map[azimuth] = altitude;
            }

            if (strict && rejected.Count > 0) {
                var sb = new StringBuilder();
                sb.Append("Horizon file ").Append(source ?? "(stream)").Append(" has ").Append(rejected.Count).Append(" invalid line(s):");
                foreach (var issue in rejected) {
                    sb.Append(Environment.NewLine).Append("  ").Append(issue);
                }
                throw new FormatException(sb.ToString());
            }

            var pointCount = map.Count;
            var profile = HorizonProfile.FromPoints(map.Select(kv => new HorizonPoint(kv.Key, kv.Value)), source, isPlaceholder);
            var wrap = MissingWrapWarning(map);
            if (wrap != null) {
                warnings.Add(wrap);
            }
            return new HorizonParseResult(profile, rejected, warnings, pointCount);
        }

        /// <summary>
        /// Whole-file warning (line 0) when the points include neither azimuth 0 nor 360. Upstream's grooming
        /// (<see cref="HorizonProfile.WrapSourceAzimuth"/>, CustomHorizon.cs:58-68) then copies one end point's
        /// altitude to both 0 and 360 instead of joining the last and first points across north, which can put the
        /// horizon tens of degrees too low or too high there (e.g. points at 345: 60 and 15: 10 give 26.7 at
        /// azimuth 355, where a straight line gives 43.3). The profile keeps upstream's values for parity; this
        /// only says so. Null when 0 or 360 is present, or with fewer than two points.
        /// </summary>
        public static HorizonParseIssue MissingWrapWarning(IReadOnlyDictionary<double, double> points) {
            if (points == null || points.Count < 2 || points.ContainsKey(0) || points.ContainsKey(360)) {
                return null;
            }
            var first = points.OrderBy(kv => kv.Key).First();
            var last = points.OrderBy(kv => kv.Key).Last();
            var key = HorizonProfile.WrapSourceAzimuth(points.Keys);
            // Straight line from the last point, across north, to the first point, evaluated at azimuth 0.
            var gap = 360.0 - last.Key + first.Key;
            var straight = last.Value + (first.Value - last.Value) * (360.0 - last.Key) / gap;
            var reason = FormattableString.Invariant(
                $"file has no point at azimuth 0 or 360: both get {points[key]}°, copied from azimuth {key} as upstream NINA does, so the horizon between azimuth {last.Key} and {first.Key} is not interpolated across north (a straight line would give {straight:0.##}° at azimuth 0); add a point at 0 or 360");
            return new HorizonParseIssue(0, string.Empty, reason);
        }

        /// <summary>Newtonsoft's JsonTextReader, which upstream uses, skips comments and accepts trailing commas.</summary>
        private static readonly JsonDocumentOptions Mw4JsonOptions = new JsonDocumentOptions {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        /// <summary>Parses a MountWizzard4 .hpts file: a JSON array of [altitude, azimuth] pairs.</summary>
        /// <exception cref="ArgumentException">Any invalid content, including malformed JSON (see the class remarks).</exception>
        public static HorizonParseResult ParseMw4(TextReader reader, string source = null) {
            if (reader == null) { throw new ArgumentNullException(nameof(reader)); }
            var name = source ?? "(stream)";
            JsonDocument document;
            try {
                document = JsonDocument.Parse(reader.ReadToEnd(), Mw4JsonOptions);
            } catch (JsonException e) {
                throw new ArgumentException($"MW4-formatted horizon file {name} is not valid JSON: {e.Message}", e);
            }
            var map = new SortedDictionary<double, double>();
            using (document) {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array) {
                    throw new ArgumentException("Expected JSON array in MW4-formatted horizon file");
                }
                var index = 0;
                foreach (var point in root.EnumerateArray()) {
                    index++;
                    if (point.ValueKind != JsonValueKind.Array) {
                        throw new ArgumentException("Expected JSON array for each point in MW4-formatted horizon file");
                    }
                    if (point.GetArrayLength() != 2) {
                        throw new ArgumentException("Expected JSON 2-element array for each point in MW4-formatted horizon file");
                    }
                    var altitude = Mw4Coordinate(point[0], "altitude", index, name);
                    // Negated form so NaN (from "NaN") is rejected too; upstream's 'altitude < 0 || altitude > 90' lets it through.
                    if (!(altitude >= 0 && altitude <= 90.0)) {
                        throw new ArgumentException(FormattableString.Invariant($"Invalid altitude {altitude} found in MW4-formatted horizon file"));
                    }
                    var azimuth = Mw4Coordinate(point[1], "azimuth", index, name);
                    if (!(azimuth >= 0 && azimuth <= 360.0)) {
                        throw new ArgumentException(FormattableString.Invariant($"Invalid azimuth {azimuth} found in MW4-formatted horizon file"));
                    }
                    map[azimuth] = altitude;
                }
            }
            var pointCount = map.Count;
            var profile = HorizonProfile.FromPoints(map.Select(kv => new HorizonPoint(kv.Key, kv.Value)), source);
            var wrap = MissingWrapWarning(map);
            var warnings = wrap == null ? Array.Empty<HorizonParseIssue>() : new[] { wrap };
            return new HorizonParseResult(profile, Array.Empty<HorizonParseIssue>(), warnings, pointCount);
        }

        /// <summary>
        /// One MW4 coordinate. Upstream's JToken.Value&lt;double&gt;() returns a JSON number as is and converts a string
        /// with Convert.ChangeType(s, typeof(double), InvariantCulture), which is
        /// double.Parse(s, NumberStyles.Float | NumberStyles.AllowThousands, InvariantCulture); this does the same.
        /// </summary>
        private static double Mw4Coordinate(JsonElement element, string what, int pointNumber, string name) {
            switch (element.ValueKind) {
                case JsonValueKind.Number:
                    if (element.TryGetDouble(out var number)) {
                        return number;
                    }
                    break;
                case JsonValueKind.String:
                    if (double.TryParse(element.GetString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)) {
                        return parsed;
                    }
                    break;
            }
            throw new ArgumentException($"Invalid {what} {element.GetRawText()} in point {pointNumber} of MW4-formatted horizon file {name}: expected a number");
        }

        /// <summary>The text of the shipped placeholder profile (see <see cref="SiteHorizons"/>).</summary>
        public static string ReadPlaceholderText() {
            using var stream = typeof(HorizonFile).Assembly.GetManifestResourceStream(PlaceholderResourceName)
                ?? throw new InvalidOperationException($"Missing embedded resource {PlaceholderResourceName}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>Horizon profiles for the rig's site.</summary>
    public static class SiteHorizons {

        /// <summary>
        /// PLACEHOLDER "no northern sky" profile for the Deep Water Bay backyard: 60 deg from azimuth 300 through
        /// north to 60 (blocked), stepping down to 4 deg over the water due south. The shape is the corrected
        /// research template (research/rig_verify_solve.md, corrected Table 7); the altitudes are NOT measured.
        /// Replace it with a measured profile (altitude every ~15 deg of azimuth; MAC_PORT_PLAN.md section 7).
        /// Comments are on their own lines so the same file also loads in unpatched upstream NINA.
        /// </summary>
        public static HorizonProfile DeepWaterBayPlaceholder {
            get {
                using var reader = new StringReader(HorizonFile.ReadPlaceholderText());
                return HorizonFile.ParseStandard(reader, "built-in placeholder (Deep Water Bay, not measured)", strict: true, isPlaceholder: true).Profile;
            }
        }
    }
}
