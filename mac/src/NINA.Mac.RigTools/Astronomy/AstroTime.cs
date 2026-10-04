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
    /// Julian dates and mean sidereal time for planning.
    /// <para>
    /// Accuracy: UTC is used in place of UT1 (|UT1-UTC| &lt; 0.9 s by IERS rule) and mean sidereal time in
    /// place of apparent (the equation of the equinoxes is at most ~1.2 s). LST is therefore good to about
    /// 2 s of time (0.01 deg of hour angle), which is ample for planning but not for pointing. Upstream NINA
    /// gets LST from NOVAS (NINA.Astrometry/AstroUtil.cs GetLocalSiderealTime), which this deliberately
    /// does not depend on.
    /// </para>
    /// </summary>
    public static class AstroTime {

        /// <summary>Julian date of J2000.0 (2000-01-01 12:00 TT; here used on the UTC scale).</summary>
        public const double J2000 = 2451545.0;

        public const double DaysPerJulianCentury = 36525.0;

        /// <summary>Mean sidereal days per mean solar day (Meeus, Astronomical Algorithms 2nd ed., ch. 12 / 15).</summary>
        public const double SiderealPerSolar = 1.00273790935;

        private static readonly long J2000Ticks = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;

        /// <summary>Days since J2000.0 on the UTC scale, computed from exact tick arithmetic.</summary>
        public static double DaysSinceJ2000(DateTimeOffset instant) {
            return (instant.UtcTicks - J2000Ticks) / (double)TimeSpan.TicksPerDay;
        }

        /// <summary>Julian date (UTC scale) of an instant.</summary>
        public static double JulianDate(DateTimeOffset instant) {
            return J2000 + DaysSinceJ2000(instant);
        }

        /// <summary>The UTC instant of a Julian date (UTC scale).</summary>
        public static DateTimeOffset FromJulianDate(double julianDate) {
            var ticks = J2000Ticks + (long)Math.Round((julianDate - J2000) * TimeSpan.TicksPerDay);
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        /// <summary>
        /// Greenwich mean sidereal time in degrees [0, 360) for a Julian date on the UT scale.
        /// IAU 1982 expression as given by Meeus, Astronomical Algorithms 2nd ed., eq. (12.4):
        /// theta0 = 280.46061837 + 360.98564736629 (JD - 2451545) + 0.000387933 T^2 - T^3 / 38710000.
        /// </summary>
        public static double GreenwichMeanSiderealTimeDegrees(double julianDateUt) {
            var d = julianDateUt - J2000;
            var t = d / DaysPerJulianCentury;
            var theta = 280.46061837 + 360.98564736629 * d + 0.000387933 * t * t - t * t * t / 38710000.0;
            return AngleMath.Normalize360(theta);
        }

        /// <summary>Greenwich mean sidereal time in hours [0, 24).</summary>
        public static double GreenwichMeanSiderealTimeHours(DateTimeOffset instant) {
            return GreenwichMeanSiderealTimeDegrees(JulianDate(instant)) / 15.0;
        }

        /// <summary>Local mean sidereal time in hours [0, 24). Longitude is east-positive (114.18 for Hong Kong).</summary>
        public static double LocalMeanSiderealTimeHours(DateTimeOffset instant, double longitudeEastDeg) {
            return AngleMath.Normalize24(GreenwichMeanSiderealTimeHours(instant) + longitudeEastDeg / 15.0);
        }
    }
}
