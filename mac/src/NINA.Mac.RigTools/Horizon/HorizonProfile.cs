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
using System.Linq;

namespace NINA.Mac.RigTools.Horizon {

    /// <summary>One horizon point: azimuth (deg, N = 0, E = 90) and the obstruction altitude (deg).</summary>
    public readonly struct HorizonPoint {

        public HorizonPoint(double azimuthDeg, double altitudeDeg) {
            AzimuthDeg = azimuthDeg;
            AltitudeDeg = altitudeDeg;
        }

        public double AzimuthDeg { get; }
        public double AltitudeDeg { get; }

        public override string ToString() {
            return FormattableString.Invariant($"{AzimuthDeg} {AltitudeDeg}");
        }
    }

    /// <summary>
    /// A local horizon: obstruction altitude as a function of azimuth, with the same grooming and
    /// interpolation as upstream NINA.Core/Model/CustomHorizon.cs, so a file gives the same altitudes here and
    /// in NINA, apart from the two <see cref="GetAltitude"/> edge cases where upstream reads 0 by accident (the
    /// parity tests run a snapshot of the upstream class to prove it):
    /// <list type="bullet">
    /// <item>points sorted by azimuth, one altitude per azimuth (upstream SortedDictionary, last one wins);</item>
    /// <item>0 and 360 filled in when missing (CustomHorizon.cs:50-75);</item>
    /// <item>piecewise-linear interpolation (CustomHorizon.cs:37-40, Accord.Math.Tools.Interpolate1D).</item>
    /// </list>
    /// The file parser that feeds it is <see cref="HorizonFile"/>, which, unlike upstream, keeps lines with inline
    /// '#' comments.
    /// </summary>
    public sealed class HorizonProfile {
        private readonly double[] azimuths;
        private readonly double[] altitudes;

        private HorizonProfile(SortedDictionary<double, double> groomedMap, string source, bool isPlaceholder) {
            azimuths = groomedMap.Keys.ToArray();
            altitudes = groomedMap.Values.ToArray();
            Source = source ?? string.Empty;
            IsPlaceholder = isPlaceholder;
        }

        /// <summary>Where the profile came from (file path, resource name, or a description).</summary>
        public string Source { get; }

        /// <summary>True for the shipped example profile, which is not a measurement.</summary>
        public bool IsPlaceholder { get; }

        /// <summary>The groomed points, sorted by azimuth, including the 0 and 360 entries.</summary>
        public IReadOnlyList<HorizonPoint> Points => azimuths.Select((az, i) => new HorizonPoint(az, altitudes[i])).ToArray();

        /// <summary>A flat horizon at a fixed altitude (default 0: the geometric horizon).</summary>
        public static HorizonProfile Flat(double altitudeDeg = 0.0) {
            var map = new SortedDictionary<double, double> { [0.0] = altitudeDeg, [360.0] = altitudeDeg };
            return new HorizonProfile(map, FormattableString.Invariant($"flat {altitudeDeg} deg"), false);
        }

        /// <summary>Builds a profile from points, grooming like upstream. Duplicate azimuths: the last one wins.</summary>
        public static HorizonProfile FromPoints(IEnumerable<HorizonPoint> points, string source = null, bool isPlaceholder = false) {
            var map = new SortedDictionary<double, double>();
            foreach (var p in points) {
                map[p.AzimuthDeg] = p.AltitudeDeg;
            }
            Groom(map);
            return new HorizonProfile(map, source, isPlaceholder);
        }

        /// <summary>
        /// Horizon altitude at an azimuth. Mirrors CustomHorizon.GetAltitude (CustomHorizon.cs:37-40): azimuths
        /// below 0 or above 359 are reduced modulo 360, then interpolated with lower/upper fallbacks of 0.
        /// <para>
        /// Two deliberate differences from upstream, both at points where upstream reports an open horizon (0)
        /// wherever the profile actually is:
        /// </para>
        /// <list type="number">
        /// <item>A tiny negative azimuth (|az| up to half an ulp of 360, ~2.8e-14, e.g. -0.00000000000001 from atan2 or a mount) reduces
        /// to x % 360 + 360, which rounds to exactly 360.0; Interpolate1D finds no knot above 360 and returns its
        /// fallback 0. Here a reduced value of 360 is wrapped to 0, so it reads the same as azimuth 0.</item>
        /// <item>A non-finite azimuth (NaN, +/-Infinity) fails closed: it returns <see cref="MaxAltitude"/>, so a
        /// caller with a bad azimuth sees "blocked" rather than "clear".</item>
        /// </list>
        /// Every finite azimuth that does not hit the rounding case gives exactly upstream's value (parity tests).
        /// </summary>
        public double GetAltitude(double azimuthDeg) {
            if (!double.IsFinite(azimuthDeg)) { return MaxAltitude; }
            if (azimuthDeg < 0 || azimuthDeg > 359) { azimuthDeg = EuclideanModulus(azimuthDeg, 360); }
            if (azimuthDeg >= 360) { azimuthDeg -= 360; }
            return Interpolate1D(azimuthDeg, azimuths, altitudes, 0, 0);
        }

        /// <summary>Highest point of the profile (CustomHorizon.GetMaxAltitude).</summary>
        public double MaxAltitude => altitudes.Max();

        /// <summary>Lowest point of the profile (CustomHorizon.GetMinAltitude).</summary>
        public double MinAltitude => altitudes.Min();

        /// <summary>
        /// Port of CustomHorizon.GroomHorizonData (NINA.Core/Model/CustomHorizon.cs:50-75):
        /// at least two points; if neither 0 nor 360 exists, copy the altitude of the point nearest to either end
        /// to both; otherwise mirror whichever of 0/360 exists.
        /// </summary>
        internal static void Groom(SortedDictionary<double, double> horizonMap) {
            if (horizonMap.Count < 2) {
                throw new ArgumentException("Horizon file does not contain enough entries or is invalid");
            }
            if (!horizonMap.ContainsKey(0) && !horizonMap.ContainsKey(360)) {
                var key = WrapSourceAzimuth(horizonMap.Keys);

                horizonMap[0] = horizonMap[key];
                horizonMap[360] = horizonMap[key];
            } else if (!horizonMap.ContainsKey(0) && horizonMap.ContainsKey(360)) {
                horizonMap[0] = horizonMap[360];
            } else if (horizonMap.ContainsKey(0) && !horizonMap.ContainsKey(360)) {
                horizonMap[360] = horizonMap[0];
            }
        }

        /// <summary>
        /// The point whose altitude <see cref="Groom"/> copies to both 0 and 360 when the file has neither
        /// (CustomHorizon.cs:58-68, verbatim logic): the point nearest to 0, unless the one nearest to 360 is strictly
        /// closer to its end. The gap across north is therefore NOT interpolated; <see cref="HorizonFile"/> warns.
        /// </summary>
        internal static double WrapSourceAzimuth(IEnumerable<double> azimuths) {
            var nearest0Azimuth = azimuths.OrderBy(x => Math.Abs(x)).First();
            var nearest360Azimuth = azimuths.OrderByDescending(x => Math.Abs(x)).First();

            var key = nearest0Azimuth;
            if (360 - nearest360Azimuth < nearest0Azimuth) {
                key = nearest360Azimuth;
            }
            return key;
        }

        /// <summary>
        /// Linear interpolation over ascending knots with constant fallbacks outside them, matching
        /// Accord.Math.Tools.Interpolate1D (Accord.NET 3.8, used by CustomHorizon.cs:39): the first knot strictly
        /// greater than <paramref name="value"/> closes the segment; below the first knot returns
        /// <paramref name="lower"/>, at or above the last knot returns <paramref name="upper"/>.
        /// </summary>
        internal static double Interpolate1D(double value, double[] x, double[] y, double lower, double upper) {
            for (var i = 0; i < x.Length; i++) {
                if (value < x[i]) {
                    if (i == 0) {
                        return lower;
                    }
                    var start = i - 1;
                    var next = i;
                    var m = (value - x[start]) / (x[next] - x[start]);
                    return y[start] + (y[next] - y[start]) * m;
                }
            }
            return upper;
        }

        /// <summary>Port of NINA.Core/Utility/CoreUtil.cs:269-282 EuclidianModulus.</summary>
        internal static double EuclideanModulus(double x, double y) {
            if (y > 0) {
                var r = x % y;
                return r < 0 ? r + y : r;
            } else if (y < 0) {
                return -1 * EuclideanModulus(-1 * x, -1 * y);
            } else {
                return double.NaN;
            }
        }
    }
}
