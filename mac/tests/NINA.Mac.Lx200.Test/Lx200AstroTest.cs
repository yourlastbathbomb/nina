#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NINA.Mac.Lx200;
using NUnit.Framework;
using System;

namespace NINA.Mac.Lx200.Test {

    [TestFixture]
    public class Lx200AstroTest {
        private const double Ms = 0.001 / 3600.0;   // one millisecond of time, in hours

        [Test]
        public void Gmst_MeeusExample12a() {
            // Meeus, Astronomical Algorithms (2nd ed.), Example 12.a: 1987 April 10, 0h UT -> 13h10m46.3668s
            var gmst = Lx200Astro.GmstHours(new DateTime(1987, 4, 10, 0, 0, 0, DateTimeKind.Utc));
            gmst.Should().BeApproximately(13 + (10 / 60.0) + (46.3668 / 3600.0), Ms);
        }

        [Test]
        public void Gmst_MeeusExample12b() {
            // Example 12.b: 1987 April 10, 19h21m00s UT -> 8h34m57.0896s
            var gmst = Lx200Astro.GmstHours(new DateTime(1987, 4, 10, 19, 21, 0, DateTimeKind.Utc));
            gmst.Should().BeApproximately(8 + (34 / 60.0) + (57.0896 / 3600.0), Ms);
        }

        [Test]
        public void JulianDate_J2000() {
            Lx200Astro.JulianDate(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).Should().Be(2451545.0);
        }

        [Test]
        public void Lst_HongKongIsGmstPlusLongitude() {
            var utc = new DateTime(2026, 10, 4, 10, 30, 0, DateTimeKind.Utc);
            var lst = Lx200Astro.LstHours(utc, 114.18);
            Lx200Astro.HourDifference(lst, Lx200Astro.GmstHours(utc) + (114.18 / 15)).Should().BeApproximately(0, 1e-12);
            lst.Should().BeInRange(0, 24);
        }

        [Test]
        public void OneDayDateError_ShiftsLstBy3m56s() {
            // the size of a wrong :SC date (RIM MNT-07): GMST advances 360.98564736629° per solar day (Meeus eq. 12.4),
            // i.e. 0.98564736629° = 236.555 s of sidereal time more than 24 h
            var utc = new DateTime(2026, 10, 4, 17, 0, 0, DateTimeKind.Utc);
            var shift = Lx200Astro.HourDifference(Lx200Astro.LstHours(utc.AddDays(1), 114.18), Lx200Astro.LstHours(utc, 114.18)) * 3600;
            shift.Should().BeApproximately(0.98564736629 / 15 * 3600, 0.01);
            shift.Should().BeApproximately(236.555, 0.01);
        }

        [Test]
        public void AltAz_SouthMeridianAndWest() {
            // on the meridian south of the zenith: azimuth 180, altitude 90 - lat + dec
            var (alt, az) = Lx200Astro.ToAltAz(raHours: 10, decDeg: -10, lstHours: 10, latitudeDeg: 22.25);
            alt.Should().BeApproximately(90 - 22.25 - 10, 1e-9);
            az.Should().BeApproximately(180, 1e-9);
            // six hours west of the meridian: azimuth between 180 and 360
            var (_, azWest) = Lx200Astro.ToAltAz(raHours: 4, decDeg: 0, lstHours: 10, latitudeDeg: 22.25);
            azWest.Should().BeInRange(180, 360);
        }

        [Test]
        public void AltAz_RoundTrips() {
            var rng = new Random(7);
            for (var i = 0; i < 500; i++) {
                var ra = rng.NextDouble() * 24;
                var dec = (rng.NextDouble() * 170) - 85;
                var lst = rng.NextDouble() * 24;
                var (alt, az) = Lx200Astro.ToAltAz(ra, dec, lst, 22.25);
                if (alt > 89.5) {
                    continue;   // azimuth is ill-defined at the zenith
                }
                var (ra2, dec2) = Lx200Astro.ToRaDec(alt, az, lst, 22.25);
                Lx200Astro.HourDifference(ra2, ra).Should().BeApproximately(0, 1e-7);
                dec2.Should().BeApproximately(dec, 1e-7);
            }
        }

        [Test]
        public void ParallacticAngle_ZeroOnMeridianSouth() {
            Lx200Astro.ParallacticAngleDeg(10, -10, 10, 22.25).Should().BeApproximately(0, 1e-9);
            Lx200Astro.ParallacticAngleDeg(8, -10, 10, 22.25).Should().BePositive();   // west of the meridian
        }

        [TestCase(23.9, 0.1, -0.2)]
        [TestCase(0.1, 23.9, 0.2)]
        [TestCase(12, 0, -12)]
        public void HourDifference_Wraps(double a, double b, double expected) {
            Lx200Astro.HourDifference(a, b).Should().BeApproximately(expected, 1e-9);
        }
    }
}
