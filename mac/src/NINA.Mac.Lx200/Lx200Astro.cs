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

namespace NINA.Mac.Lx200 {

    /// <summary>
    /// Just enough astronomy for the bench probe and the simulator: sidereal time and alt-az conversions.
    /// The mount reports 1 s of time and 1' of site, so the IAU 1982 GMST expression (Meeus, Astronomical
    /// Algorithms 2nd ed., eq. 12.4, ~0.1 s) is ample. Azimuth is measured from North through East.
    /// The engine's SOFA/NOVAS path (M3) replaces this for real pointing.
    /// </summary>
    public static class Lx200Astro {

        private static readonly DateTime J2000 = new(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        public const double SiderealDaySeconds = 86164.0905;

        /// <summary>Julian date of a UTC instant.</summary>
        public static double JulianDate(DateTime utc) => 2451545.0 + DaysSinceJ2000(utc);

        /// <summary>Greenwich mean sidereal time in hours [0, 24), Meeus eq. 12.4.</summary>
        public static double GmstHours(DateTime utc) {
            var d = DaysSinceJ2000(utc);
            var t = d / 36525.0;
            var deg = 280.46061837 + (360.98564736629 * d) + (0.000387933 * t * t) - (t * t * t / 38710000.0);
            return Wrap(deg, 360.0) / 15.0;
        }

        /// <summary>Local mean sidereal time in hours [0, 24) for an east-positive longitude.</summary>
        public static double LstHours(DateTime utc, double eastLongitudeDegrees) => Wrap(GmstHours(utc) + (eastLongitudeDegrees / 15.0), 24.0);

        /// <summary>a - b in hours, wrapped into [-12, 12).</summary>
        public static double HourDifference(double a, double b) => Wrap(a - b + 12.0, 24.0) - 12.0;

        /// <summary>a - b in degrees, wrapped into [-180, 180).</summary>
        public static double DegreeDifference(double a, double b) => Wrap(a - b + 180.0, 360.0) - 180.0;

        public static (double AltitudeDeg, double AzimuthDeg) ToAltAz(double raHours, double decDeg, double lstHours, double latitudeDeg) {
            var ha = Rad(HourDifference(lstHours, raHours) * 15.0);
            double dec = Rad(decDeg), lat = Rad(latitudeDeg);
            var sinAlt = (Math.Sin(dec) * Math.Sin(lat)) + (Math.Cos(dec) * Math.Cos(lat) * Math.Cos(ha));
            var alt = Math.Asin(Math.Clamp(sinAlt, -1, 1));
            var az = Math.Atan2(-Math.Sin(ha) * Math.Cos(dec), (Math.Sin(dec) * Math.Cos(lat)) - (Math.Cos(dec) * Math.Sin(lat) * Math.Cos(ha)));
            return (Deg(alt), Wrap(Deg(az), 360.0));
        }

        public static (double RaHours, double DecDeg) ToRaDec(double altitudeDeg, double azimuthDeg, double lstHours, double latitudeDeg) {
            double alt = Rad(altitudeDeg), az = Rad(azimuthDeg), lat = Rad(latitudeDeg);
            var sinDec = (Math.Sin(alt) * Math.Sin(lat)) + (Math.Cos(alt) * Math.Cos(lat) * Math.Cos(az));
            var dec = Math.Asin(Math.Clamp(sinDec, -1, 1));
            var ha = Math.Atan2(-Math.Sin(az) * Math.Cos(alt), (Math.Sin(alt) * Math.Cos(lat)) - (Math.Cos(alt) * Math.Sin(lat) * Math.Cos(az)));
            return (Wrap(lstHours - (Deg(ha) / 15.0), 24.0), Deg(dec));
        }

        /// <summary>Parallactic angle in degrees: the angle at the target between the directions to the pole and the zenith.</summary>
        public static double ParallacticAngleDeg(double raHours, double decDeg, double lstHours, double latitudeDeg) {
            var ha = Rad(HourDifference(lstHours, raHours) * 15.0);
            double dec = Rad(decDeg), lat = Rad(latitudeDeg);
            return Deg(Math.Atan2(Math.Sin(ha), (Math.Tan(lat) * Math.Cos(dec)) - (Math.Sin(dec) * Math.Cos(ha))));
        }

        /// <summary>Great-circle separation in degrees.</summary>
        public static double SeparationDeg(double ra1Hours, double dec1Deg, double ra2Hours, double dec2Deg) {
            double r1 = Rad(ra1Hours * 15), d1 = Rad(dec1Deg), r2 = Rad(ra2Hours * 15), d2 = Rad(dec2Deg);
            var c = (Math.Sin(d1) * Math.Sin(d2)) + (Math.Cos(d1) * Math.Cos(d2) * Math.Cos(r1 - r2));
            return Deg(Math.Acos(Math.Clamp(c, -1, 1)));
        }

        public static double Wrap(double value, double modulus) {
            var r = value % modulus;
            return r < 0 ? r + modulus : r;
        }

        private static double DaysSinceJ2000(DateTime utc) {
            if (utc.Kind == DateTimeKind.Local) {
                utc = utc.ToUniversalTime();
            }
            return (utc - J2000).Ticks / (double)TimeSpan.TicksPerDay;
        }

        private static double Rad(double deg) => deg * Math.PI / 180.0;

        private static double Deg(double rad) => rad * 180.0 / Math.PI;
    }
}
