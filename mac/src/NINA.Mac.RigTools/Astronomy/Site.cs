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
using System.Globalization;

namespace NINA.Mac.RigTools.Astronomy {

    /// <summary>
    /// An observing site with a fixed UTC offset (Hong Kong has no daylight saving time).
    /// Longitude is east-positive (cartographic), unlike the LX200 protocol's west-positive :Sg.
    /// </summary>
    public sealed class Site {

        public Site(string name, double latitudeDeg, double longitudeDeg, TimeSpan utcOffset) {
            if (!(latitudeDeg >= -90.0 && latitudeDeg <= 90.0)) {
                throw new ArgumentOutOfRangeException(nameof(latitudeDeg), latitudeDeg, "Latitude must be within [-90, 90]");
            }
            if (double.IsNaN(longitudeDeg) || double.IsInfinity(longitudeDeg)) {
                throw new ArgumentOutOfRangeException(nameof(longitudeDeg));
            }
            Name = name ?? string.Empty;
            LatitudeDeg = latitudeDeg;
            LongitudeDeg = AngleMath.NormalizeSigned180(longitudeDeg);
            UtcOffset = utcOffset;
        }

        /// <summary>The rig's backyard: Deep Water Bay, Hong Kong, 22.25 N 114.18 E, UTC+8 (MAC_PORT_PLAN.md).</summary>
        public static Site DeepWaterBay { get; } = new Site("Deep Water Bay, Hong Kong", 22.25, 114.18, TimeSpan.FromHours(8));

        public string Name { get; }
        public double LatitudeDeg { get; }

        /// <summary>East-positive longitude in (-180, 180].</summary>
        public double LongitudeDeg { get; }

        public TimeSpan UtcOffset { get; }

        public DateTimeOffset ToLocal(DateTimeOffset instant) {
            return instant.ToOffset(UtcOffset);
        }

        public double LocalSiderealTimeHours(DateTimeOffset instant) {
            return AstroTime.LocalMeanSiderealTimeHours(instant, LongitudeDeg);
        }

        /// <summary>Hour angle in (-12, 12] of an equatorial position of date.</summary>
        public double HourAngleHours(EquatorialCoordinates ofDate, DateTimeOffset instant) {
            return SphericalAstronomy.HourAngleHours(LocalSiderealTimeHours(instant), ofDate.RaHours);
        }

        /// <summary>Altitude/azimuth of an equatorial position of date.</summary>
        public HorizontalCoordinates ToHorizontal(EquatorialCoordinates ofDate, DateTimeOffset instant) {
            return SphericalAstronomy.EquatorialToHorizontal(HourAngleHours(ofDate, instant), ofDate.DecDeg, LatitudeDeg);
        }

        /// <summary>Altitude/azimuth of a J2000 catalogue position (precessed to <paramref name="instant"/> first).</summary>
        public HorizontalCoordinates ToHorizontalFromJ2000(EquatorialCoordinates j2000, DateTimeOffset instant) {
            return ToHorizontal(Precession.FromJ2000(j2000, instant), instant);
        }

        public override string ToString() {
            return string.Format(CultureInfo.InvariantCulture, "{0} ({1:0.00}{2} {3:0.00}{4}, UTC{5}{6:hh\\:mm})", Name,
                Math.Abs(LatitudeDeg), LatitudeDeg >= 0 ? "N" : "S", Math.Abs(LongitudeDeg), LongitudeDeg >= 0 ? "E" : "W",
                UtcOffset < TimeSpan.Zero ? "-" : "+", UtcOffset.Duration());
        }
    }
}
