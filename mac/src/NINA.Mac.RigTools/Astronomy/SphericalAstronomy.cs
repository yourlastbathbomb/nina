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
    /// Standard spherical astronomy for planning. All altitudes are geometric (no refraction); refraction
    /// raises an object by ~0.5 deg at the horizon, ~3.5' at 15 deg and ~1' at 45 deg.
    /// Sources: Meeus, Astronomical Algorithms 2nd ed. (1998), chapters 13 (transformation of coordinates),
    /// 14 (parallactic angle) and 21 (precession).
    /// </summary>
    public static class SphericalAstronomy {

        /// <summary>
        /// Equatorial to horizontal (Meeus eq. 13.5 and 13.6), with azimuth turned to the north-based,
        /// east-positive convention used by NINA (NINA.Astrometry/AstroUtil.cs:331-366) and the LX200 protocol.
        /// sin h = sin phi sin delta + cos phi cos delta cos H;
        /// Az = atan2(-cos delta sin H, sin delta cos phi - cos delta sin phi cos H).
        /// </summary>
        /// <param name="hourAngleHours">Local hour angle, positive west of the meridian.</param>
        public static HorizontalCoordinates EquatorialToHorizontal(double hourAngleHours, double decDeg, double latitudeDeg) {
            var h = hourAngleHours * 15.0 * AngleMath.DegToRad;
            var d = decDeg * AngleMath.DegToRad;
            var p = latitudeDeg * AngleMath.DegToRad;
            var sinAlt = Math.Sin(p) * Math.Sin(d) + Math.Cos(p) * Math.Cos(d) * Math.Cos(h);
            var alt = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0));
            var y = -Math.Cos(d) * Math.Sin(h);
            var x = Math.Sin(d) * Math.Cos(p) - Math.Cos(d) * Math.Sin(p) * Math.Cos(h);
            var az = Math.Atan2(y, x);
            return new HorizontalCoordinates(alt * AngleMath.RadToDeg, AngleMath.Normalize360(az * AngleMath.RadToDeg));
        }

        /// <summary>Hour angle in (-12, 12] hours from local sidereal time and right ascension; negative = east.</summary>
        public static double HourAngleHours(double localSiderealTimeHours, double raHours) {
            return AngleMath.NormalizeSigned12(localSiderealTimeHours - raHours);
        }

        /// <summary>
        /// Parallactic angle q in degrees (-180, 180] (Meeus eq. 14.1):
        /// tan q = sin H / (tan phi cos delta - sin delta cos H).
        /// On an alt-az mount without a rotator the camera keeps its orientation to the vertical, so the
        /// frame turns on the sky by exactly the change in q.
        /// </summary>
        public static double ParallacticAngleDeg(double hourAngleHours, double decDeg, double latitudeDeg) {
            var h = hourAngleHours * 15.0 * AngleMath.DegToRad;
            var d = decDeg * AngleMath.DegToRad;
            var p = latitudeDeg * AngleMath.DegToRad;
            var q = Math.Atan2(Math.Sin(h), Math.Tan(p) * Math.Cos(d) - Math.Sin(d) * Math.Cos(h));
            return q * AngleMath.RadToDeg;
        }

        /// <summary>Altitude at upper culmination (transit): 90 - |phi - delta|.</summary>
        public static double TransitAltitudeDeg(double decDeg, double latitudeDeg) {
            return 90.0 - Math.Abs(latitudeDeg - decDeg);
        }

        /// <summary>Azimuth at upper culmination: 180 (south) when delta &lt; phi, 0 (north) when delta &gt; phi.</summary>
        public static double TransitAzimuthDeg(double decDeg, double latitudeDeg) {
            return decDeg <= latitudeDeg ? 180.0 : 0.0;
        }

        /// <summary>Altitude at lower culmination: |phi + delta| - 90.</summary>
        public static double LowerCulminationAltitudeDeg(double decDeg, double latitudeDeg) {
            return Math.Abs(latitudeDeg + decDeg) - 90.0;
        }

        /// <summary>True when the object never sets below the geometric horizon.</summary>
        public static bool IsCircumpolar(double decDeg, double latitudeDeg) {
            return LowerCulminationAltitudeDeg(decDeg, latitudeDeg) > 0.0;
        }

        /// <summary>True when the object never rises above the geometric horizon.</summary>
        public static bool NeverRises(double decDeg, double latitudeDeg) {
            return TransitAltitudeDeg(decDeg, latitudeDeg) < 0.0;
        }

        /// <summary>
        /// Hour angle (sidereal hours, [0, 12]) at which an object crosses the given altitude, from
        /// cos H0 = (sin h0 - sin phi sin delta) / (cos phi cos delta) (Meeus eq. 15.1 with h0 as the target
        /// altitude). Returns 0 when the object never reaches h0 and 12 when it never drops below it.
        /// </summary>
        public static double HourAngleAtAltitudeHours(double decDeg, double latitudeDeg, double altitudeDeg) {
            var d = decDeg * AngleMath.DegToRad;
            var p = latitudeDeg * AngleMath.DegToRad;
            var h0 = altitudeDeg * AngleMath.DegToRad;
            var denominator = Math.Cos(p) * Math.Cos(d);
            if (Math.Abs(denominator) < 1e-12) {
                // Pole or observer at a pole: altitude is constant.
                return Math.Sin(p) * Math.Sin(d) >= Math.Sin(h0) ? 12.0 : 0.0;
            }
            var cosH0 = (Math.Sin(h0) - Math.Sin(p) * Math.Sin(d)) / denominator;
            if (cosH0 >= 1.0) { return 0.0; }
            if (cosH0 <= -1.0) { return 12.0; }
            return Math.Acos(cosH0) * AngleMath.RadToDeg / 15.0;
        }

        /// <summary>
        /// Time spent above an altitude per transit, in mean solar time (2 H0 / 1.00273790935).
        /// Returns zero when the object never gets that high and one sidereal day (23h56m04s) when it is always above.
        /// </summary>
        public static TimeSpan TimeAboveAltitude(double decDeg, double latitudeDeg, double altitudeDeg) {
            var h0 = HourAngleAtAltitudeHours(decDeg, latitudeDeg, altitudeDeg);
            return TimeSpan.FromHours(2.0 * h0 / AstroTime.SiderealPerSolar);
        }

        /// <summary>
        /// The transit (upper culmination, H = 0) nearest to <paramref name="reference"/>, using local mean
        /// sidereal time. RA should be of date (see <see cref="Precession"/>) for best results; using J2000 RA
        /// in 2026 shifts transit by up to ~1.5 minutes.
        /// </summary>
        public static DateTimeOffset NearestTransit(DateTimeOffset reference, double raHours, double longitudeEastDeg) {
            var lst = AstroTime.LocalMeanSiderealTimeHours(reference, longitudeEastDeg);
            var ha = HourAngleHours(lst, raHours);
            return reference - TimeSpan.FromHours(ha / AstroTime.SiderealPerSolar);
        }
    }

    /// <summary>
    /// Precession of mean equatorial coordinates from J2000.0 to the mean equinox of date with the IAU 1976
    /// angles (Meeus eq. 21.3 with T = 0, and 21.4). Nutation (up to ~17") and annual aberration (up to ~20.5")
    /// are deliberately ignored; proper motion is not applied. Over 2000-2050 the result is good to ~0.01 deg
    /// against apparent place, which is what planning needs.
    /// </summary>
    public static class Precession {

        public static EquatorialCoordinates FromJ2000(EquatorialCoordinates j2000, DateTimeOffset epoch) {
            return FromJ2000(j2000, AstroTime.JulianDate(epoch));
        }

        public static EquatorialCoordinates FromJ2000(EquatorialCoordinates j2000, double julianDate) {
            var t = (julianDate - AstroTime.J2000) / AstroTime.DaysPerJulianCentury;
            // Meeus (21.3), starting epoch J2000.0 so T = 0; arcseconds.
            var zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) / AngleMath.ArcsecPerRadian;
            var z = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) / AngleMath.ArcsecPerRadian;
            var theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) / AngleMath.ArcsecPerRadian;

            var a0 = j2000.RaHours * 15.0 * AngleMath.DegToRad;
            var d0 = j2000.DecDeg * AngleMath.DegToRad;
            // Meeus (21.4)
            var a = Math.Cos(d0) * Math.Sin(a0 + zeta);
            var b = Math.Cos(theta) * Math.Cos(d0) * Math.Cos(a0 + zeta) - Math.Sin(theta) * Math.Sin(d0);
            var c = Math.Sin(theta) * Math.Cos(d0) * Math.Cos(a0 + zeta) + Math.Cos(theta) * Math.Sin(d0);
            var ra = Math.Atan2(a, b) + z;
            // atan2 form instead of asin(c) stays accurate near the poles (Meeus' remark after 21.4).
            var dec = Math.Atan2(c, Math.Sqrt(a * a + b * b));
            return new EquatorialCoordinates(AngleMath.Normalize360(ra * AngleMath.RadToDeg) / 15.0, dec * AngleMath.RadToDeg);
        }
    }
}
