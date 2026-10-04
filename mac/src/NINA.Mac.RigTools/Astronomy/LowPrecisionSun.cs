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

namespace NINA.Mac.RigTools.Astronomy {

    /// <summary>
    /// The Sun's position from the Astronomical Almanac's "low precision formulas for the Sun"
    /// (Section C; also published by USNO as "Approximate Solar Coordinates"), quoted accuracy 0.01 deg
    /// between 1950 and 2050:
    /// <code>
    /// n = JD - 2451545.0
    /// L = 280.460 + 0.9856474 n          (mean longitude, aberration included)
    /// g = 357.528 + 0.9856003 n          (mean anomaly)
    /// lambda = L + 1.915 sin g + 0.020 sin 2g,  beta = 0
    /// epsilon = 23.439 - 0.0000004 n
    /// alpha = atan2(cos epsilon sin lambda, cos lambda),  delta = asin(sin epsilon sin lambda)
    /// </code>
    /// UTC is used for n (TT - UTC = 69 s moves the Sun by 0.0008 deg). At Hong Kong the Sun's altitude
    /// changes by ~0.2 deg per minute near twilight, so 0.01 deg is a few seconds of twilight time.
    /// Upstream NINA uses NOVAS + JPL ephemeris for this (NINA.Astrometry/AstroUtil.cs:797-803).
    /// </summary>
    public static class LowPrecisionSun {

        /// <summary>Civil, nautical and astronomical twilight limits, as used upstream (NINA.Astrometry/RiseAndSet/*TwilightRiseAndSet.cs).</summary>
        public const double CivilTwilightDeg = -6.0;
        public const double NauticalTwilightDeg = -12.0;
        public const double AstronomicalTwilightDeg = -18.0;

        /// <summary>Apparent right ascension and declination of the Sun (equinox of date).</summary>
        public static EquatorialCoordinates Position(DateTimeOffset instant) {
            var n = AstroTime.DaysSinceJ2000(instant);
            var l = 280.460 + 0.9856474 * n;
            var g = (357.528 + 0.9856003 * n) * AngleMath.DegToRad;
            var lambda = (l + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2.0 * g)) * AngleMath.DegToRad;
            var epsilon = (23.439 - 0.0000004 * n) * AngleMath.DegToRad;
            var ra = Math.Atan2(Math.Cos(epsilon) * Math.Sin(lambda), Math.Cos(lambda));
            var dec = Math.Asin(Math.Sin(epsilon) * Math.Sin(lambda));
            return new EquatorialCoordinates(AngleMath.Normalize360(ra * AngleMath.RadToDeg) / 15.0, dec * AngleMath.RadToDeg);
        }

        /// <summary>Geometric altitude of the Sun's centre (no refraction, no parallax) at a site.</summary>
        public static double AltitudeDeg(DateTimeOffset instant, Site site) {
            var sun = Position(instant);
            var lst = AstroTime.LocalMeanSiderealTimeHours(instant, site.LongitudeDeg);
            var ha = SphericalAstronomy.HourAngleHours(lst, sun.RaHours);
            return SphericalAstronomy.EquatorialToHorizontal(ha, sun.DecDeg, site.LatitudeDeg).AltitudeDeg;
        }

        /// <summary>
        /// The darkness interval (Sun below <paramref name="sunAltitudeDeg"/>) of the night that starts on the local
        /// evening of <paramref name="eveningDate"/>: searched from local noon to the next local noon. Returns null
        /// when the Sun never gets that low. If the Sun is already below the limit at noon (polar night), the
        /// interval starts at noon; if it never comes back above, it ends at the next noon.
        /// </summary>
        public static (DateTimeOffset Dusk, DateTimeOffset Dawn)? DarkInterval(DateOnly eveningDate, Site site, double sunAltitudeDeg) {
            var start = new DateTimeOffset(eveningDate.Year, eveningDate.Month, eveningDate.Day, 12, 0, 0, site.UtcOffset);
            var end = start.AddDays(1);
            var step = TimeSpan.FromMinutes(5);

            bool IsDark(DateTimeOffset t) => AltitudeDeg(t, site) < sunAltitudeDeg;

            DateTimeOffset? dusk = IsDark(start) ? start : null;
            DateTimeOffset? dawn = null;
            var previous = start;
            var previousDark = IsDark(start);
            for (var t = start + step; t <= end; t += step) {
                var dark = IsDark(t);
                if (dark != previousDark) {
                    var crossing = Bisect(previous, t, previousDark, IsDark);
                    if (dark && dusk == null) {
                        dusk = crossing;
                    } else if (!dark && dusk != null) {
                        dawn = crossing;
                        break;
                    }
                }
                previous = t;
                previousDark = dark;
            }
            if (dusk == null) {
                return null;
            }
            return (dusk.Value, dawn ?? end);
        }

        /// <summary>First instant (to 0.5 s) after <paramref name="a"/> at which the predicate differs from <paramref name="stateAtA"/>.</summary>
        internal static DateTimeOffset Bisect(DateTimeOffset a, DateTimeOffset b, bool stateAtA, Func<DateTimeOffset, bool> predicate) {
            while (b - a > TimeSpan.FromSeconds(0.5)) {
                var mid = a + TimeSpan.FromTicks((b - a).Ticks / 2);
                if (predicate(mid) == stateAtA) {
                    a = mid;
                } else {
                    b = mid;
                }
            }
            return b;
        }
    }
}
