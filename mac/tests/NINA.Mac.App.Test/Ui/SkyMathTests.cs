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
using NINA.Mac.App.Astro;
using NINA.Mac.App.Services;
using NUnit.Framework;
using System;
using System.Linq;

namespace NINA.Mac.App.Test.Ui {

    [TestFixture]
    public class SkyMathTests {
        private const double Latitude = 22.25;
        private static readonly GeoSite Site = new(22.25, 114.18);

        // Meeus, Astronomical Algorithms (2nd ed.), example 12.a: 1987 April 10, 0h UT, mean sidereal time 13h10m46.3668s
        [Test]
        public void Gmst_MeeusExample12a() {
            var gmst = SkyMath.GreenwichMeanSiderealTimeHours(new DateTimeOffset(1987, 4, 10, 0, 0, 0, TimeSpan.Zero));
            gmst.Should().BeApproximately(13 + (10 / 60.0) + (46.3668 / 3600.0), 1e-6);
        }

        // Meeus example 12.b: 1987 April 10, 19h21m00s UT, mean sidereal time 128.7378734 deg = 8h34m57.0896s
        [Test]
        public void Gmst_MeeusExample12b() {
            var gmst = SkyMath.GreenwichMeanSiderealTimeHours(new DateTimeOffset(1987, 4, 10, 19, 21, 0, TimeSpan.Zero));
            gmst.Should().BeApproximately(128.7378734 / 15.0, 1e-6);
        }

        [Test]
        public void CornerRadius_Bin2() {
            SkyMath.CornerRadiusPixels(3840, 2160, 2).Should().BeApproximately(1101.45, 0.01);
        }

        /// <summary>
        /// MAC_PORT_PLAN.md section 6 table (bin 2x2, 1 px corner blur, 22.25 N). The plan's values are given to
        /// 0.1 s, so the tolerance is half of that.
        /// </summary>
        [TestCase(-30, 0.0, 10.6)]
        [TestCase(-30, 3.0, 16.6)]
        [TestCase(-30, -3.0, 16.6)]
        [TestCase(0, 0.0, 5.1)]
        [TestCase(0, 3.0, 28.7)]
        [TestCase(10, 0.0, 2.9)]
        [TestCase(10, 3.0, 64.7)]
        public void MaxSub_MatchesPlanTable(double dec, double hourAngle, double expectedSeconds) {
            var (alt, az) = SkyMath.ToAltAz(hourAngle, dec, Latitude);
            var radius = SkyMath.CornerRadiusPixels(3840, 2160, 2);
            SkyMath.MaxSubSeconds(Latitude, alt, az, radius, 1.0).Should().BeApproximately(expectedSeconds, 0.05);
        }

        [Test]
        public void MaxSub_DueEastIsUnlimited_ZenithIsZero() {
            SkyMath.MaxSubSeconds(Latitude, 40, 90, 1101.45).Should().Be(double.PositiveInfinity);
            SkyMath.MaxSubSeconds(Latitude, 90, 180, 1101.45).Should().Be(0);
        }

        [Test]
        public void AltAz_TransitIsDueSouthAtTransitAltitude() {
            var (alt, az) = SkyMath.ToAltAz(0, -30, Latitude);
            alt.Should().BeApproximately(SkyMath.TransitAltitude(-30, Latitude), 1e-9);
            alt.Should().BeApproximately(37.75, 1e-9);
            az.Should().BeApproximately(180, 1e-9);
        }

        [Test]
        public void AltAz_EastBeforeTransitWestAfter() {
            SkyMath.ToAltAz(-2, -20, Latitude).Azimuth.Should().BeInRange(90, 180);
            SkyMath.ToAltAz(2, -20, Latitude).Azimuth.Should().BeInRange(180, 270);
        }

        [Test]
        public void AltAz_RoundTrip() {
            var t = TestTimes.EveningOct10;
            foreach (var (ra, dec) in new[] { (0.7925, -25.2883), (18.06, -24.39), (5.5881, -5.3911), (13.6169, -29.8658) }) {
                var (alt, az) = SkyMath.ToAltAz(t, ra, dec, Site);
                var (ra2, dec2) = SkyMath.FromAltAz(t, alt, az, Site);
                dec2.Should().BeApproximately(dec, 1e-9);
                (Math.Abs(ra2 - ra) % 24).Should().BeLessThan(1e-9);
            }
        }

        // Low-precision Sun vs. 2026 equinox/solstice instants (Sun declination 0 and +23.44 deg)
        [Test]
        public void Sun_EquinoxAndSolstice() {
            SkyMath.SunPosition(new DateTimeOffset(2026, 3, 20, 14, 46, 0, TimeSpan.Zero)).DeclinationDegrees.Should().BeApproximately(0, 0.1);
            SkyMath.SunPosition(new DateTimeOffset(2026, 6, 21, 8, 24, 0, TimeSpan.Zero)).DeclinationDegrees.Should().BeApproximately(23.44, 0.1);
        }

        [Test]
        public void AstronomicalDawn_HongKongEarlyOctober_IsAroundFiveAm() {
            // Sanity bound: HK sunrise is about 06:12 in early October and astronomical twilight lasts about 70 min at 22 N.
            var dawn = SkyMath.NextAstronomicalDawn(TestTimes.EveningOct10, Site);
            dawn.Should().NotBeNull();
            var local = dawn.Value.ToOffset(TimeSpan.FromHours(8));
            local.Date.Should().Be(new DateTime(2026, 10, 11));
            local.TimeOfDay.Should().BeGreaterThan(TimeSpan.FromHours(4.75)).And.BeLessThan(TimeSpan.FromHours(5.33));
            SkyMath.SunAltitude(dawn.Value, Site).Should().BeApproximately(-18, 0.05);
        }

        [Test]
        public void TimeToTransit_IsLessThanASiderealDay() {
            var t = SkyMath.TimeToTransit(TestTimes.EveningOct10, 0.7925, Site.LongitudeDegrees);
            t.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThan(TimeSpan.FromHours(23.94));
            var (alt, _) = SkyMath.ToAltAz(TestTimes.EveningOct10 + t, 0.7925, -25.2883, Site);
            alt.Should().BeApproximately(SkyMath.TransitAltitude(-25.2883, Latitude), 0.01);
        }

        [TestCase(5.5881, "05h35m17s")]
        [TestCase(-1.0, "23h00m00s")]
        public void FormatHours(double hours, string expected) => SkyMath.FormatHours(hours).Should().Be(expected);

        [TestCase(-5.3911, "-05°23′28″")]
        [TestCase(22.7211, "+22°43′16″")]
        public void FormatDegrees(double deg, string expected) => SkyMath.FormatDegrees(deg).Should().Be(expected);
    }

    [TestFixture]
    public class TargetPlannerTests {
        private static readonly AppSettings Settings = new();
        private static CatalogTarget Find(string name) => BuiltInTargetCatalog.Targets.Single(t => t.Name == name);

        private static DateTimeOffset TransitOf(CatalogTarget t) =>
            TestTimes.EveningOct10 + SkyMath.TimeToTransit(TestTimes.EveningOct10, t.RightAscensionHours, Settings.Site.LongitudeDegrees);

        [Test]
        public void Andromeda_TransitsInTheBlockedNorth() {
            var m31 = Find("M31");
            var v = TargetPlanner.Evaluate(m31, TransitOf(m31), Settings, 10);
            (v.Azimuth < 1 || v.Azimuth > 359).Should().BeTrue($"azimuth {v.Azimuth} should be due north");
            v.InBlockedNorth.Should().BeTrue();
            v.Observable.Should().BeFalse();
            v.Warnings.Should().Contain(w => w.Contains("blocked northern sky"));
        }

        [Test]
        public void Dumbbell_TransitsThroughTheKeyhole() {
            var m27 = Find("M27");
            var v = TargetPlanner.Evaluate(m27, TransitOf(m27), Settings, 10);
            v.TransitAltitude.Should().BeGreaterThan(85);
            v.AboveMaxAltitude.Should().BeTrue();
            v.Warnings.Should().Contain(w => w.Contains("keyhole"));
        }

        [Test]
        public void Tarantula_NeverRises() {
            var v = TargetPlanner.Evaluate(Find("NGC 2070"), TestTimes.EveningOct10, Settings, 10);
            v.NeverRises.Should().BeTrue();
            v.Observable.Should().BeFalse();
        }

        [Test]
        public void SculptorGalaxy_AtTransit_TenSecondSubsAreNearTheLimit() {
            var ngc253 = Find("NGC 253");
            var v = TargetPlanner.Evaluate(ngc253, TransitOf(ngc253), Settings, 30);
            v.Observable.Should().BeTrue();
            v.Azimuth.Should().BeApproximately(180, 0.5);
            v.MaxSubSecondsNow.Should().BeInRange(9, 13); // Dec -25 at transit: between the plan's Dec -30 (10.6 s) and Dec 0 (5.1 s) rows
            v.Warnings.Should().ContainSingle(w => w.Contains("field-rotation limit"));
        }

        [Test]
        public void Catalog_SearchIgnoresSpacesAndCase() {
            var catalog = new BuiltInTargetCatalog();
            catalog.Search("ngc253").Select(t => t.Name).Should().Equal("NGC 253");
            catalog.Search("helix").Select(t => t.Name).Should().Equal("NGC 7293");
            catalog.Search("").Should().HaveCount(BuiltInTargetCatalog.Targets.Count);
        }

        [TestCase(double.NaN, "n/a")]
        [TestCase(double.PositiveInfinity, "> 10 min")]
        [TestCase(5.06, "5.1 s")]
        [TestCase(28.7, "29 s")]
        public void FormatSeconds(double s, string expected) => TargetPlanner.FormatSeconds(s).Should().Be(expected);
    }
}
