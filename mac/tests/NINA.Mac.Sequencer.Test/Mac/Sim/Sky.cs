#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;

namespace NINA.Mac.Sequencer.Test.Sim {

    /// <summary>
    /// Synthetic targets placed relative to the real clock. NINA's conditions and waits read DateTime.Now directly (plan section
    /// 4.2, rule 5), so a simulated night runs in real time and its targets are made to rise, set or climb through a limit within
    /// seconds of now. Altitudes come from NINA's own Coordinates.Transform, the transform the conditions use.
    /// </summary>
    internal static class Sky {

        /// <summary>The rig's site (Deep Water Bay, Hong Kong).</summary>
        public const double Latitude = 22.25;
        public const double Longitude = 114.18;
        public const double Elevation = 0;

        /// <summary>
        /// J2000 coordinates of a star at declination <paramref name="decDeg"/> that is at <paramref name="altitudeDeg"/> at
        /// <paramref name="at"/>, east of the meridian and rising, or west and setting.
        /// </summary>
        public static Coordinates TargetAt(double altitudeDeg, bool rising, double decDeg, DateTime at) {
            var phi = AstroUtil.ToRadians(Latitude);
            var delta = AstroUtil.ToRadians(decDeg);
            var cosH = (Math.Sin(AstroUtil.ToRadians(altitudeDeg)) - Math.Sin(phi) * Math.Sin(delta)) / (Math.Cos(phi) * Math.Cos(delta));
            if (cosH < -1 || cosH > 1) {
                throw new ArgumentException($"Dec {decDeg} never reaches altitude {altitudeDeg} at latitude {Latitude}");
            }
            var hourAngleHours = AstroUtil.ToDegree(Math.Acos(cosH)) / 15.0 * (rising ? -1 : 1);
            var lst = AstroUtil.GetLocalSiderealTime(at, Longitude);
            var ra = AstroUtil.EuclidianModulus(lst - hourAngleHours, 24);
            var jnow = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(decDeg), Epoch.JNOW);
            // Refine against NINA's full transform (precession, nutation, refraction-free topocentric) so the altitude is exact
            var j2000 = jnow.Transform(Epoch.J2000);
            for (var i = 0; i < 4; i++) {
                var error = Altitude(j2000, at) - altitudeDeg;
                var later = Altitude(j2000, at.AddSeconds(10));
                var rate = (later - Altitude(j2000, at)) / 10.0;
                if (Math.Abs(rate) < 1e-9) {
                    break;
                }
                // Shift RA so the target is where it would be error/rate seconds later
                var seconds = -error / rate;
                j2000 = new Coordinates(Angle.ByHours(AstroUtil.EuclidianModulus(j2000.RA - seconds / 3600.0 * 1.00273790935, 24)), Angle.ByDegree(j2000.Dec), Epoch.J2000);
            }
            return j2000;
        }

        /// <summary>
        /// J2000 coordinates of a star at declination <paramref name="decDeg"/> that crosses the meridian (reaches its highest
        /// altitude, as NINA's transform computes it) at <paramref name="transit"/>, to about 0.1 s.
        /// </summary>
        public static Coordinates TransitingAt(double decDeg, DateTime transit) {
            var lst = AstroUtil.GetLocalSiderealTime(transit, Longitude);
            var j2000 = new Coordinates(Angle.ByHours(lst), Angle.ByDegree(decDeg), Epoch.JNOW).Transform(Epoch.J2000);
            for (var i = 0; i < 3; i++) {
                // Find the peak of the altitude curve (golden-section search over +-5 min) and shift RA by the miss
                var lo = transit.AddMinutes(-5);
                var hi = transit.AddMinutes(5);
                var phi = (Math.Sqrt(5) - 1) / 2;
                while (hi - lo > TimeSpan.FromMilliseconds(20)) {
                    var a = hi - (hi - lo) * phi;
                    var b = lo + (hi - lo) * phi;
                    if (Altitude(j2000, a) > Altitude(j2000, b)) {
                        hi = b;
                    } else {
                        lo = a;
                    }
                }
                var peak = lo + (hi - lo) / 2;
                var miss = (transit - peak).TotalSeconds;
                if (Math.Abs(miss) < 0.05) {
                    break;
                }
                j2000 = new Coordinates(Angle.ByHours(AstroUtil.EuclidianModulus(j2000.RA + miss / 3600.0 * 1.00273790935, 24)), Angle.ByDegree(j2000.Dec), Epoch.J2000);
            }
            return j2000;
        }

        public static double Altitude(Coordinates coordinates, DateTime at) {
            return coordinates.Transform(Angle.ByDegree(Latitude), Angle.ByDegree(Longitude), Elevation, at).Altitude.Degree;
        }

        public static double Azimuth(Coordinates coordinates, DateTime at) {
            return coordinates.Transform(Angle.ByDegree(Latitude), Angle.ByDegree(Longitude), Elevation, at).Azimuth.Degree;
        }

        /// <summary>
        /// The altitude as NINA's altitude conditions see it: WaitLoopData.CurrentAltitude keeps it rounded to 0.01 degree
        /// (NINA.Sequencer/SequenceItem/Utility/WaitLoopData.cs), so a limit is crossed when the rounded value passes it.
        /// </summary>
        public static double NinaAltitude(Coordinates coordinates, DateTime at) {
            return Math.Round(Altitude(coordinates, at), 2);
        }

        /// <summary>
        /// The first time after <paramref name="from"/> at which <paramref name="stopped"/> becomes true for the altitude NINA's
        /// conditions use (<see cref="NinaAltitude"/>), to 0.1 s. The predicate must be false at <paramref name="from"/>.
        /// </summary>
        public static DateTime FirstTime(Coordinates coordinates, Func<double, bool> stopped, DateTime from, TimeSpan within) {
            return FirstTime(coordinates, stopped, from, within, NinaAltitude);
        }

        /// <summary>
        /// As <see cref="FirstTime(Coordinates, Func{double, bool}, DateTime, TimeSpan)"/> for the unrounded altitude, which NINA's
        /// WaitForAltitude compares with its limit.
        /// </summary>
        public static DateTime FirstTimeExact(Coordinates coordinates, Func<double, bool> stopped, DateTime from, TimeSpan within) {
            return FirstTime(coordinates, stopped, from, within, Altitude);
        }

        private static DateTime FirstTime(Coordinates coordinates, Func<double, bool> stopped, DateTime from, TimeSpan within, Func<Coordinates, DateTime, double> altitudeAt) {
            if (stopped(altitudeAt(coordinates, from))) {
                throw new InvalidOperationException("The limit is already passed at the start");
            }
            var step = TimeSpan.FromSeconds(1);
            var previous = from;
            for (var t = from + step; t <= from + within; t += step) {
                if (stopped(altitudeAt(coordinates, t))) {
                    var lo = previous;
                    var hi = t;
                    while (hi - lo > TimeSpan.FromMilliseconds(100)) {
                        var mid = lo + (hi - lo) / 2;
                        if (stopped(altitudeAt(coordinates, mid))) {
                            hi = mid;
                        } else {
                            lo = mid;
                        }
                    }
                    return hi;
                }
                previous = t;
            }
            throw new InvalidOperationException($"The limit is not passed within {within}");
        }
    }
}
