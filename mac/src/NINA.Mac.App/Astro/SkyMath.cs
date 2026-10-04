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

namespace NINA.Mac.App.Astro {

    /// <summary>Latitude/longitude in degrees (east positive).</summary>
    public readonly record struct GeoSite(double LatitudeDegrees, double LongitudeDegrees);

    /// <summary>
    /// Low-precision sky maths for the UI shell: sidereal time, alt/az, field rotation and the Sun. Good to a few
    /// arcminutes, which is plenty for readouts and planning. The engine (M3) will use NINA's SOFA/NOVAS for
    /// anything that drives the mount (J2000 to JNow, refraction).
    /// </summary>
    public static class SkyMath {

        /// <summary>Earth's sidereal rotation rate, rad/s.</summary>
        public const double SiderealRateRadPerSecond = 7.2921159e-5;

        private const double Deg = Math.PI / 180.0;
        private const double UnixEpochJulianDate = 2440587.5;
        private const double J2000 = 2451545.0;

        public static double JulianDate(DateTimeOffset time) =>
            UnixEpochJulianDate + ((time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerDay);

        /// <summary>Greenwich mean sidereal time in hours (Meeus, Astronomical Algorithms, eq. 12.4).</summary>
        public static double GreenwichMeanSiderealTimeHours(DateTimeOffset time) {
            var jd = JulianDate(time);
            var t = (jd - J2000) / 36525.0;
            var theta = 280.46061837 + (360.98564736629 * (jd - J2000)) + (0.000387933 * t * t) - (t * t * t / 38710000.0);
            return NormalizeDegrees(theta) / 15.0;
        }

        public static double LocalSiderealTimeHours(DateTimeOffset time, double longitudeEastDegrees) =>
            NormalizeHours(GreenwichMeanSiderealTimeHours(time) + (longitudeEastDegrees / 15.0));

        /// <summary>Hour angle in hours, in [-12, 12): negative east of the meridian (rising), positive west.</summary>
        public static double HourAngleHours(double localSiderealHours, double rightAscensionHours) {
            var ha = NormalizeHours(localSiderealHours - rightAscensionHours);
            return ha >= 12 ? ha - 24 : ha;
        }

        /// <summary>Altitude and azimuth (degrees, azimuth from north through east) for an hour angle and declination.</summary>
        public static (double Altitude, double Azimuth) ToAltAz(double hourAngleHours, double declinationDegrees, double latitudeDegrees) {
            var h = hourAngleHours * 15.0 * Deg;
            var dec = declinationDegrees * Deg;
            var lat = latitudeDegrees * Deg;
            var sinAlt = (Math.Sin(lat) * Math.Sin(dec)) + (Math.Cos(lat) * Math.Cos(dec) * Math.Cos(h));
            var alt = Math.Asin(Math.Clamp(sinAlt, -1, 1));
            var az = Math.Atan2(-Math.Cos(dec) * Math.Sin(h), (Math.Sin(dec) * Math.Cos(lat)) - (Math.Cos(dec) * Math.Sin(lat) * Math.Cos(h)));
            return (alt / Deg, NormalizeDegrees(az / Deg));
        }

        public static (double Altitude, double Azimuth) ToAltAz(DateTimeOffset time, double rightAscensionHours, double declinationDegrees, GeoSite site) {
            var ha = HourAngleHours(LocalSiderealTimeHours(time, site.LongitudeDegrees), rightAscensionHours);
            return ToAltAz(ha, declinationDegrees, site.LatitudeDegrees);
        }

        /// <summary>Inverse of <see cref="ToAltAz(DateTimeOffset, double, double, GeoSite)"/>.</summary>
        public static (double RightAscensionHours, double DeclinationDegrees) FromAltAz(DateTimeOffset time, double altitudeDegrees, double azimuthDegrees, GeoSite site) {
            var alt = altitudeDegrees * Deg;
            var az = azimuthDegrees * Deg;
            var lat = site.LatitudeDegrees * Deg;
            var sinDec = (Math.Sin(lat) * Math.Sin(alt)) + (Math.Cos(lat) * Math.Cos(alt) * Math.Cos(az));
            var dec = Math.Asin(Math.Clamp(sinDec, -1, 1));
            var h = Math.Atan2(-Math.Sin(az) * Math.Cos(alt), (Math.Cos(lat) * Math.Sin(alt)) - (Math.Sin(lat) * Math.Cos(alt) * Math.Cos(az)));
            var ra = NormalizeHours(LocalSiderealTimeHours(time, site.LongitudeDegrees) - (h / Deg / 15.0));
            return (ra, dec / Deg);
        }

        /// <summary>Altitude of the upper culmination (meridian transit).</summary>
        public static double TransitAltitude(double declinationDegrees, double latitudeDegrees) => 90.0 - Math.Abs(latitudeDegrees - declinationDegrees);

        /// <summary>Time until the next meridian transit (0 when transiting now).</summary>
        public static TimeSpan TimeToTransit(DateTimeOffset time, double rightAscensionHours, double longitudeEastDegrees) {
            var ha = HourAngleHours(LocalSiderealTimeHours(time, longitudeEastDegrees), rightAscensionHours);
            var siderealHours = ha <= 0 ? -ha : 24 - ha;
            return TimeSpan.FromHours(siderealHours * 0.9972695663);
        }

        /// <summary>
        /// Field rotation rate of an alt-az mount, rad/s: w = w_earth * cos(lat) * cos(Az) / cos(Alt). Sign follows
        /// cos(Az); use the magnitude for exposure limits.
        /// </summary>
        public static double FieldRotationRate(double latitudeDegrees, double altitudeDegrees, double azimuthDegrees) {
            var cosAlt = Math.Cos(altitudeDegrees * Deg);
            if (cosAlt < 1e-9) {
                return double.PositiveInfinity;
            }
            return SiderealRateRadPerSecond * Math.Cos(latitudeDegrees * Deg) * Math.Cos(azimuthDegrees * Deg) / cosAlt;
        }

        /// <summary>Distance from the frame centre to a corner in (binned) pixels.</summary>
        public static double CornerRadiusPixels(int sensorWidth, int sensorHeight, int bin) {
            var w = sensorWidth / (double)bin;
            var h = sensorHeight / (double)bin;
            return Math.Sqrt((w * w) + (h * h)) / 2.0;
        }

        /// <summary>
        /// Longest sub before field rotation smears the frame corner by <paramref name="blurPixels"/>. Infinity due
        /// east or west (no rotation), 0 at the zenith. Reproduces the table in MAC_PORT_PLAN.md section 6.
        /// </summary>
        public static double MaxSubSeconds(double latitudeDegrees, double altitudeDegrees, double azimuthDegrees, double cornerRadiusPixels, double blurPixels = 1.0) {
            var rate = Math.Abs(FieldRotationRate(latitudeDegrees, altitudeDegrees, azimuthDegrees));
            if (double.IsPositiveInfinity(rate)) {
                return 0;
            }
            if (rate < 1e-15) {
                return double.PositiveInfinity;
            }
            return blurPixels / (cornerRadiusPixels * rate);
        }

        /// <summary>
        /// Apparent Sun position, low precision (about 0.01 deg, 1950-2050): Astronomical Almanac "low precision
        /// formulas for the Sun".
        /// </summary>
        public static (double RightAscensionHours, double DeclinationDegrees) SunPosition(DateTimeOffset time) {
            var n = JulianDate(time) - J2000;
            var l = NormalizeDegrees(280.460 + (0.9856474 * n));
            var g = NormalizeDegrees(357.528 + (0.9856003 * n)) * Deg;
            var lambda = (l + (1.915 * Math.Sin(g)) + (0.020 * Math.Sin(2 * g))) * Deg;
            var epsilon = (23.439 - (0.0000004 * n)) * Deg;
            var ra = Math.Atan2(Math.Cos(epsilon) * Math.Sin(lambda), Math.Cos(lambda));
            var dec = Math.Asin(Math.Sin(epsilon) * Math.Sin(lambda));
            return (NormalizeHours(ra / Deg / 15.0), dec / Deg);
        }

        public static double SunAltitude(DateTimeOffset time, GeoSite site) {
            var (ra, dec) = SunPosition(time);
            return ToAltAz(time, ra, dec, site).Altitude;
        }

        /// <summary>
        /// First time after <paramref name="from"/> (within <paramref name="within"/>) when <paramref name="altitudeAt"/>
        /// crosses <paramref name="threshold"/> in the given direction; null if it does not. 2-minute scan, then
        /// bisection to about 1 second.
        /// </summary>
        public static DateTimeOffset? FindCrossing(Func<DateTimeOffset, double> altitudeAt, DateTimeOffset from, TimeSpan within, double threshold, bool rising) {
            var step = TimeSpan.FromMinutes(2);
            var t0 = from;
            var a0 = altitudeAt(t0) - threshold;
            for (var elapsed = TimeSpan.Zero; elapsed < within; elapsed += step) {
                var t1 = t0 + step;
                var a1 = altitudeAt(t1) - threshold;
                var crossed = rising ? a0 < 0 && a1 >= 0 : a0 > 0 && a1 <= 0;
                if (crossed) {
                    var lo = t0;
                    var hi = t1;
                    while (hi - lo > TimeSpan.FromSeconds(1)) {
                        var mid = lo + ((hi - lo) / 2);
                        var am = altitudeAt(mid) - threshold;
                        if (rising ? am < 0 : am > 0) {
                            lo = mid;
                        } else {
                            hi = mid;
                        }
                    }
                    return hi;
                }
                t0 = t1;
                a0 = a1;
            }
            return null;
        }

        /// <summary>Next astronomical dawn (Sun rising through -18 deg) within 24 h, or null (e.g. no true night).</summary>
        public static DateTimeOffset? NextAstronomicalDawn(DateTimeOffset from, GeoSite site) =>
            FindCrossing(t => SunAltitude(t, site), from, TimeSpan.FromHours(24), -18.0, rising: true);

        public static double NormalizeDegrees(double degrees) {
            var d = degrees % 360.0;
            return d < 0 ? d + 360.0 : d;
        }

        public static double NormalizeHours(double hours) {
            var h = hours % 24.0;
            return h < 0 ? h + 24.0 : h;
        }

        public static string FormatHours(double hours) {
            var totalSeconds = (int)Math.Round(NormalizeHours(hours) * 3600);
            return $"{totalSeconds / 3600:00}h{totalSeconds / 60 % 60:00}m{totalSeconds % 60:00}s";
        }

        public static string FormatDegrees(double degrees) {
            var sign = degrees < 0 ? "-" : "+";
            var totalSeconds = (int)Math.Round(Math.Abs(degrees) * 3600);
            return $"{sign}{totalSeconds / 3600:00}°{totalSeconds / 60 % 60:00}′{totalSeconds % 60:00}″";
        }
    }
}
