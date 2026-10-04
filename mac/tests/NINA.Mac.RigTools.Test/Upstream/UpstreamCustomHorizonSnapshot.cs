#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NINA.Mac.RigTools.Test.Upstream {

    /// <summary>
    /// TEST FIXTURE: a snapshot of upstream NINA.Core/Model/CustomHorizon.cs lines 26-156 (develop @ ee69f27,
    /// the base of the macos branch): constructor, GetAltitude, GroomHorizonData, FromReader_Standard and
    /// FromReader_MW4, kept line-for-line except that Logger.Warning appends to <see cref="Warnings"/> instead of
    /// Serilog, CoreUtil.EuclidianModulus is inlined (CoreUtil.cs:269-282) and FromReader_MW4 takes a TextReader
    /// instead of a StreamReader so tests can pass a StringReader. Interpolation calls the real
    /// Accord.Math.Tools.Interpolate1D from the same Accord.Math 3.8.2-alpha package upstream uses, and JSON goes
    /// through the same Newtonsoft.Json 13.0.4.
    /// It is a copy rather than a compile-link of the upstream file because this work must not build upstream
    /// sources; refresh it if upstream CustomHorizon.cs changes.
    /// <see cref="FromReader_Standard_ProposedPatch"/> is the same loop with the upstream patch proposed in
    /// mac/src/NINA.Mac.RigTools/README.md applied, and <see cref="GetAltitude_ProposedPatch"/> is GetAltitude
    /// with the proposed azimuth-wrap guard, so the tests exercise the patch before anyone sends it.
    /// </summary>
    internal class UpstreamCustomHorizonSnapshot {
        private double[] azimuths;
        private double[] altitudes;

        public List<string> Warnings { get; } = new List<string>();

        private UpstreamCustomHorizonSnapshot(IDictionary<double, double> horizonMap, List<string> warnings) {
            this.azimuths = horizonMap.Keys.ToArray();
            this.altitudes = horizonMap.Values.ToArray();
            Warnings.AddRange(warnings);
        }

        public IReadOnlyList<double> Azimuths => azimuths;

        public double GetAltitude(double azimuth) {
            if (azimuth < 0 || azimuth > 359) { azimuth = EuclidianModulus(azimuth, 360); }
            return Accord.Math.Tools.Interpolate1D(azimuth, azimuths, altitudes, 0, 0);
        }

        /// <summary>GetAltitude with the proposed patch: non-finite azimuth fails closed, a reduced 360 wraps to 0.</summary>
        public double GetAltitude_ProposedPatch(double azimuth) {
            if (double.IsNaN(azimuth) || double.IsInfinity(azimuth)) { return this.altitudes.Max(); }
            if (azimuth < 0 || azimuth > 359) { azimuth = EuclidianModulus(azimuth, 360); }
            // EuclidianModulus(-1e-14, 360) rounds to exactly 360, past the last knot, where Interpolate1D returns 0
            if (azimuth >= 360) { azimuth -= 360; }
            return Accord.Math.Tools.Interpolate1D(azimuth, azimuths, altitudes, 0, 0);
        }

        private static double EuclidianModulus(double x, double y) {
            if (y > 0) {
                double r = x % y;
                if (r < 0) {
                    return r + y;
                } else {
                    return r;
                }
            } else if (y < 0) {
                return -1 * EuclidianModulus(-1 * x, -1 * y);
            } else {
                return double.NaN;
            }
        }

        private static void GroomHorizonData(SortedDictionary<double, double> horizonMap) {
            if (horizonMap.Count < 2) {
                throw new ArgumentException("Horizon file does not contain enough entries or is invalid");
            }

            if (!horizonMap.ContainsKey(0) && !horizonMap.ContainsKey(360)) {
                var nearest0Azimuth = horizonMap.Keys.OrderBy(x => Math.Abs(x)).First();
                var nearest360Azimuth = horizonMap.Keys.OrderByDescending(x => Math.Abs(x)).First();

                var key = nearest0Azimuth;
                if (360 - nearest360Azimuth < nearest0Azimuth) {
                    key = nearest360Azimuth;
                }

                horizonMap[0] = horizonMap[key];
                horizonMap[360] = horizonMap[key];
            } else if (!horizonMap.ContainsKey(0) && horizonMap.ContainsKey(360)) {
                horizonMap[0] = horizonMap[360];
            } else if (horizonMap.ContainsKey(0) && !horizonMap.ContainsKey(360)) {
                horizonMap[360] = horizonMap[0];
            }
        }

        public static UpstreamCustomHorizonSnapshot FromReader_Standard(TextReader sr) {
            var horizonMap = new SortedDictionary<double, double>();
            var warnings = new List<string>();

            string line;
            while ((line = sr.ReadLine()?.Trim()) != null) {
                // Lines starting with # are comments
                if (!line.StartsWith("#") && !string.IsNullOrEmpty(line)) {
                    var columns = line.Split(new char[] { '\t', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    if (columns.Length == 2) {
                        if (double.TryParse(columns[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var azimuth)) {
                            if (double.TryParse(columns[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var altitude)) {
                                horizonMap[azimuth] = altitude;
                            } else {
                                warnings.Add($"Invalid value for altitude {columns[0]}");
                            }
                        } else {
                            warnings.Add($"Invalid value for azimuth {columns[0]}");
                        }
                    } else {
                        warnings.Add($"Invalid line for horizon values {line}");
                    }
                }
            }

            GroomHorizonData(horizonMap);
            return new UpstreamCustomHorizonSnapshot(horizonMap, warnings);
        }

        /// <summary>FromReader_Standard with the proposed patch: strip '#' comments anywhere, log the altitude token.</summary>
        public static UpstreamCustomHorizonSnapshot FromReader_Standard_ProposedPatch(TextReader sr) {
            var horizonMap = new SortedDictionary<double, double>();
            var warnings = new List<string>();

            string line;
            while ((line = sr.ReadLine()) != null) {
                // '#' starts a comment, on its own line or after a point ("180 4 # over water")
                var commentStart = line.IndexOf('#');
                if (commentStart >= 0) {
                    line = line.Substring(0, commentStart);
                }
                line = line.Trim();
                if (!string.IsNullOrEmpty(line)) {
                    var columns = line.Split(new char[] { '\t', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    if (columns.Length == 2) {
                        if (double.TryParse(columns[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var azimuth)) {
                            if (double.TryParse(columns[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var altitude)) {
                                horizonMap[azimuth] = altitude;
                            } else {
                                warnings.Add($"Invalid value for altitude {columns[1]}");
                            }
                        } else {
                            warnings.Add($"Invalid value for azimuth {columns[0]}");
                        }
                    } else {
                        warnings.Add($"Invalid line for horizon values {line}");
                    }
                }
            }

            GroomHorizonData(horizonMap);
            return new UpstreamCustomHorizonSnapshot(horizonMap, warnings);
        }

        private static readonly JsonSerializer JSON_SERIALIZER = JsonSerializer.Create();

        public static UpstreamCustomHorizonSnapshot FromReader_MW4(TextReader sr) {
            var horizonMap = new SortedDictionary<double, double>();

            using (JsonTextReader jsonReader = new JsonTextReader(sr)) {
                var deserialized = (JToken)JSON_SERIALIZER.Deserialize(jsonReader);
                if (deserialized.Type != JTokenType.Array) {
                    throw new ArgumentException($"Expected JSON array in MW4-formatted horizon file");
                }
                var arr = (JArray)deserialized;
                for (int i = 0; i < arr.Count; ++i) {
                    var point = arr[i];
                    if (point.Type != JTokenType.Array) {
                        throw new ArgumentException($"Expected JSON array for each point in MW4-formatted horizon file");
                    }

                    var coordinateArray = (JArray)point;
                    if (coordinateArray.Count != 2) {
                        throw new ArgumentException($"Expected JSON 2-element array for each point in MW4-formatted horizon file");
                    }

                    var altitude = coordinateArray[0].Value<double>();
                    if (altitude < 0 || altitude > 90.0) {
                        throw new ArgumentException($"Invalid altitude {altitude} found in MW4-formatted horizon file");
                    }

                    var azimuth = coordinateArray[1].Value<double>();
                    if (azimuth < 0 || azimuth > 360.0) {
                        throw new ArgumentException($"Invalid azimuth {azimuth} found in MW4-formatted horizon file");
                    }
                    horizonMap[azimuth] = altitude;
                }
            };

            GroomHorizonData(horizonMap);
            return new UpstreamCustomHorizonSnapshot(horizonMap, new List<string>());
        }
    }
}
