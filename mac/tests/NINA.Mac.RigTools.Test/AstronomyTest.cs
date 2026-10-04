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
using NINA.Mac.RigTools.Astronomy;
using NINA.Mac.RigTools.Optics;
using NUnit.Framework;
using System;

namespace NINA.Mac.RigTools.Test {

    /// <summary>
    /// Published oracles from Meeus, Astronomical Algorithms, 2nd ed. (Willmann-Bell 1998): examples 7.a, 12.a,
    /// 12.b, 13.b, 21.b and 25.a. Tolerances are half of the last digit Meeus prints unless a test says why it
    /// needs more.
    /// </summary>
    [TestFixture]
    public class AstronomyTest {

        private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0, double s = 0) {
            return new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).AddTicks((long)Math.Round(s * TimeSpan.TicksPerSecond));
        }

        private static double Hms(int h, int m, double s) {
            return h + m / 60.0 + s / 3600.0;
        }

        [Test]
        public void JulianDate_MeeusExample7a_Sputnik() {
            // 1957 October 4.81 = 19h26m24s UT -> JD 2436116.31
            AstroTime.JulianDate(Utc(1957, 10, 4, 19, 26, 24)).Should().BeApproximately(2436116.31, 1e-9);
        }

        [TestCase(2000, 1, 1, 12, 2451545.0)]
        [TestCase(1987, 1, 27, 0, 2446822.5)]
        [TestCase(1987, 6, 19, 12, 2446966.0)]
        [TestCase(1999, 1, 1, 0, 2451179.5)]
        public void JulianDate_MeeusTable(int y, int m, int d, int h, double expected) {
            AstroTime.JulianDate(Utc(y, m, d, h)).Should().Be(expected);
            AstroTime.FromJulianDate(expected).Should().Be(Utc(y, m, d, h));
        }

        [Test]
        public void Gmst_MeeusExample12a() {
            // 1987 April 10, 0h UT: mean sidereal time 13h10m46.3668s
            var hours = AstroTime.GreenwichMeanSiderealTimeHours(Utc(1987, 4, 10));
            (hours * 3600).Should().BeApproximately(Hms(13, 10, 46.3668) * 3600, 0.00005 + 1e-6);
        }

        [Test]
        public void Gmst_MeeusExample12b() {
            // 1987 April 10, 19h21m00s UT: mean sidereal time 8h34m57.0896s (128.7378734 deg)
            var jd = AstroTime.JulianDate(Utc(1987, 4, 10, 19, 21));
            (AstroTime.GreenwichMeanSiderealTimeDegrees(jd) / 15.0 * 3600).Should().BeApproximately(Hms(8, 34, 57.0896) * 3600, 0.00005 + 1e-6);
        }

        [Test]
        public void LocalSiderealTime_AddsEastLongitude() {
            var t = Utc(2026, 10, 4, 12);
            var gmst = AstroTime.GreenwichMeanSiderealTimeHours(t);
            AstroTime.LocalMeanSiderealTimeHours(t, 114.18).Should().BeApproximately(AngleMath.Normalize24(gmst + 114.18 / 15), 1e-12);
            Site.DeepWaterBay.LocalSiderealTimeHours(t).Should().BeApproximately(AngleMath.Normalize24(gmst + 114.18 / 15), 1e-12);
        }

        [Test]
        public void AltAz_MeeusExample13b_WithMeeusHourAngle() {
            // Venus from the USNO, Washington (38d55'17" N): H = 64.352133 deg, delta = -6d43'11.61"
            // -> A = 68.0337 deg (Meeus measures A from the south), h = 15.1249 deg.
            var lat = 38 + 55 / 60.0 + 17 / 3600.0;
            var dec = -(6 + 43 / 60.0 + 11.61 / 3600.0);

            var hz = SphericalAstronomy.EquatorialToHorizontal(64.352133 / 15.0, dec, lat);

            hz.AzimuthDeg.Should().BeApproximately(68.0337 + 180.0, 0.00005 + 1e-9, "north-based azimuth = Meeus' south-based + 180");
            hz.AltitudeDeg.Should().BeApproximately(15.1249, 0.00005 + 1e-9);
        }

        [Test]
        public void AltAz_MeeusExample13b_EndToEndWithOwnSiderealTime() {
            // Same example from the date, longitude 77d03'56" W and apparent alpha 23h09m16.641s. We use MEAN
            // sidereal time; Meeus uses apparent, 0.24 s smaller here (nutation in RA), which moves H by 0.0009 deg.
            // Tolerance 0.002 deg covers exactly that and nothing else.
            var lat = 38 + 55 / 60.0 + 17 / 3600.0;
            var lon = -(77 + 3 / 60.0 + 56 / 3600.0);
            var site = new Site("USNO", lat, lon, TimeSpan.Zero);
            var venus = new EquatorialCoordinates(Hms(23, 9, 16.641), -(6 + 43 / 60.0 + 11.61 / 3600.0));

            var hz = site.ToHorizontal(venus, Utc(1987, 4, 10, 19, 21));

            hz.AzimuthDeg.Should().BeApproximately(68.0337 + 180.0, 0.002);
            hz.AltitudeDeg.Should().BeApproximately(15.1249, 0.002);
        }

        [Test]
        public void Precession_MeeusExample21b_ThetaPersei() {
            // Mean position at J2000.0 after Meeus' proper-motion step: alpha0 = 41.054063, delta0 = +49.227750;
            // precessed to JDE 2462088.69 (2028 Nov 13.19): alpha = 41.547214, delta = +49.348483.
            var j2000 = new EquatorialCoordinates(41.054063 / 15.0, 49.227750);

            var ofDate = Precession.FromJ2000(j2000, 2462088.69);

            (ofDate.RaHours * 15.0).Should().BeApproximately(41.547214, 0.0000005 + 1e-9);
            ofDate.DecDeg.Should().BeApproximately(49.348483, 0.0000005 + 1e-9);
        }

        [Test]
        public void Precession_IsIdentityAtJ2000_AndWellBehavedAtThePole() {
            var c = new EquatorialCoordinates(5.5880, -5.3911);
            var same = Precession.FromJ2000(c, AstroTime.J2000);
            same.RaHours.Should().BeApproximately(c.RaHours, 1e-12);
            same.DecDeg.Should().BeApproximately(c.DecDeg, 1e-12);

            var pole = Precession.FromJ2000(new EquatorialCoordinates(0, 90), AstroTime.J2000 + 26 * 365.25);
            pole.DecDeg.Should().BeLessThan(90).And.BeGreaterThan(89.8);
        }

        [Test]
        public void Sun_MeeusExample25a_WithinTheAlmanacFormulaAccuracy() {
            // 1992 October 13.0 TD (JDE 2448908.5): alpha = 198.38083 deg (13h13m31.4s), delta = -7.78507 deg.
            // The Astronomical Almanac low-precision formula is quoted to 0.01 deg, so that is the tolerance.
            var sun = LowPrecisionSun.Position(AstroTime.FromJulianDate(2448908.5));

            (sun.RaHours * 15.0).Should().BeApproximately(198.38083, 0.01);
            sun.DecDeg.Should().BeApproximately(-7.78507, 0.01);
        }

        [Test]
        public void Sun_DeclinationAtSolsticesAndEquinox_Sanity() {
            // Sanity, not an oracle: 2026 solstices/equinox to the nearest hour; obliquity 23.44 deg.
            LowPrecisionSun.Position(Utc(2026, 6, 21, 8)).DecDeg.Should().BeApproximately(23.44, 0.02);
            LowPrecisionSun.Position(Utc(2026, 12, 21, 21)).DecDeg.Should().BeApproximately(-23.44, 0.02);
            LowPrecisionSun.Position(Utc(2026, 3, 20, 15)).DecDeg.Should().BeApproximately(0, 0.05);
        }

        [TestCase(LowPrecisionSun.AstronomicalTwilightDeg)]
        [TestCase(LowPrecisionSun.NauticalTwilightDeg)]
        [TestCase(LowPrecisionSun.CivilTwilightDeg)]
        public void DarkInterval_HongKong_EdgesSitOnTheSunAltitudeLimit(double limit) {
            var site = Site.DeepWaterBay;

            var dark = LowPrecisionSun.DarkInterval(new DateOnly(2026, 10, 4), site, limit);

            dark.Should().NotBeNull();
            LowPrecisionSun.AltitudeDeg(dark.Value.Dusk, site).Should().BeApproximately(limit, 0.01);
            LowPrecisionSun.AltitudeDeg(dark.Value.Dawn, site).Should().BeApproximately(limit, 0.01);
            dark.Value.Dusk.Should().BeBefore(dark.Value.Dawn);
            // Sanity bounds only (not an almanac oracle): HK sunset ~18:05 and sunrise ~06:15 local in early October.
            var dusk = site.ToLocal(dark.Value.Dusk);
            var dawn = site.ToLocal(dark.Value.Dawn);
            dusk.Hour.Should().BeInRange(18, 19);
            dawn.Hour.Should().BeInRange(4, 6);
        }

        [Test]
        public void DarkInterval_DeeperLimitGivesShorterNight() {
            var date = new DateOnly(2026, 10, 4);
            var astro = LowPrecisionSun.DarkInterval(date, Site.DeepWaterBay, -18).Value;
            var nautical = LowPrecisionSun.DarkInterval(date, Site.DeepWaterBay, -12).Value;
            astro.Dusk.Should().BeAfter(nautical.Dusk);
            astro.Dawn.Should().BeBefore(nautical.Dawn);
        }

        [Test]
        public void DarkInterval_NullWhenTheSunNeverGetsLowEnough_AndFullDayInPolarNight() {
            var tromso = new Site("Tromso", 69.65, 18.96, TimeSpan.FromHours(2));
            LowPrecisionSun.DarkInterval(new DateOnly(2026, 6, 21), tromso, -18).Should().BeNull();

            var polarNight = LowPrecisionSun.DarkInterval(new DateOnly(2026, 12, 21), new Site("Svalbard", 78.2, 15.6, TimeSpan.FromHours(1)), -6);
            polarNight.Should().NotBeNull();
            (polarNight.Value.Dawn - polarNight.Value.Dusk).Should().Be(TimeSpan.FromDays(1));
        }

        [Test]
        public void Culmination_AtHongKong() {
            const double lat = 22.25;
            SphericalAstronomy.TransitAltitudeDeg(-30, lat).Should().Be(37.75);
            SphericalAstronomy.TransitAltitudeDeg(22.25, lat).Should().Be(90);
            SphericalAstronomy.TransitAzimuthDeg(-30, lat).Should().Be(180);
            SphericalAstronomy.TransitAzimuthDeg(40, lat).Should().Be(0);
            SphericalAstronomy.NeverRises(-67.8, lat).Should().BeTrue();
            SphericalAstronomy.NeverRises(-67.7, lat).Should().BeFalse();
            SphericalAstronomy.IsCircumpolar(67.8, lat).Should().BeTrue();
            SphericalAstronomy.IsCircumpolar(67.7, lat).Should().BeFalse();
            SphericalAstronomy.LowerCulminationAltitudeDeg(89.264, lat).Should().BeApproximately(21.514, 1e-9);
        }

        [Test]
        public void TimeAboveAltitude_EquatorialStarAboveHorizonForHalfASiderealDay() {
            // H0 = 6 sidereal hours exactly for delta = 0, h0 = 0 -> 12 / 1.00273790935 solar hours.
            SphericalAstronomy.TimeAboveAltitude(0, 22.25, 0).TotalHours.Should().BeApproximately(12 / 1.00273790935, 1e-9);
            SphericalAstronomy.TimeAboveAltitude(-80, 22.25, 0).Should().Be(TimeSpan.Zero);
            SphericalAstronomy.TimeAboveAltitude(80, 22.25, 0).TotalHours.Should().BeApproximately(24 / 1.00273790935, 1e-9);
        }

        [TestCase(-30.0, 20.0)]
        [TestCase(-5.0, 15.0)]
        [TestCase(10.0, 75.0)]
        public void HourAngleAtAltitude_GivesThatAltitude(double dec, double alt) {
            var h0 = SphericalAstronomy.HourAngleAtAltitudeHours(dec, 22.25, alt);
            h0.Should().BeGreaterThan(0).And.BeLessThan(12);
            SphericalAstronomy.EquatorialToHorizontal(h0, dec, 22.25).AltitudeDeg.Should().BeApproximately(alt, 1e-9);
            SphericalAstronomy.EquatorialToHorizontal(-h0, dec, 22.25).AltitudeDeg.Should().BeApproximately(alt, 1e-9);
        }

        [Test]
        public void NearestTransit_PutsTheObjectOnTheMeridian() {
            var site = Site.DeepWaterBay;
            var reference = new DateTimeOffset(2026, 10, 5, 0, 0, 0, site.UtcOffset);
            var ngc253 = Precession.FromJ2000(new EquatorialCoordinates(Hms(0, 47, 33.1), -25.2883), reference);

            var transit = SphericalAstronomy.NearestTransit(reference, ngc253.RaHours, site.LongitudeDeg);

            site.HourAngleHours(ngc253, transit).Should().BeApproximately(0, 1e-6);
            Math.Abs((transit - reference).TotalHours).Should().BeLessThanOrEqualTo(12);
            site.ToHorizontal(ngc253, transit).AzimuthDeg.Should().BeApproximately(180, 1e-6);
        }

        [Test]
        public void Azimuth_IsNorthBasedEastPositive() {
            // Rising in the east (H < 0) gives azimuth < 180, setting in the west gives > 180.
            SphericalAstronomy.EquatorialToHorizontal(-3, 0, 22.25).AzimuthDeg.Should().BeInRange(90, 180);
            SphericalAstronomy.EquatorialToHorizontal(3, 0, 22.25).AzimuthDeg.Should().BeInRange(180, 270);
            SphericalAstronomy.EquatorialToHorizontal(0, 60, 22.25).AzimuthDeg.Should().BeApproximately(0, 1e-9);
        }

        [TestCase("05:35:17.3", 5.588138889)]
        [TestCase("05h35m17.3s", 5.588138889)]
        [TestCase("05 35 17.3", 5.588138889)]
        [TestCase("-05:23:28", -5.391111111)]
        [TestCase("+22d00m52s", 22.014444444)]
        [TestCase("-0:30", -0.5)]
        [TestCase("13.6169", 13.6169)]
        public void Sexagesimal_Parses(string text, double expected) {
            AngleMath.ParseSexagesimal(text).Should().BeApproximately(expected, 1e-8);
        }

        [TestCase("")]
        [TestCase("12:xx")]
        [TestCase("1:2:3:4")]
        public void Sexagesimal_RejectsGarbage(string text) {
            FluentActions.Invoking(() => AngleMath.ParseSexagesimal(text)).Should().Throw<FormatException>();
        }

        [Test]
        public void Site_NormalisesLongitudeAndPrints() {
            new Site("x", 22.25, 245.82, TimeSpan.Zero).LongitudeDeg.Should().BeApproximately(-114.18, 1e-9);
            Site.DeepWaterBay.ToString().Should().Be("Deep Water Bay, Hong Kong (22.25N 114.18E, UTC+08:00)");
            FluentActions.Invoking(() => new Site("x", 91, 0, TimeSpan.Zero)).Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    /// <summary>research/rig_verify_solve.md corrected Table 1 (FOV / pixel scale), to the printed precision.</summary>
    [TestFixture]
    public class ImagingTrainTest {

        [Test]
        public void Native_MatchesResearchFovTable() {
            var bin1 = ImagingTrain.Asi585Native(1);
            var bin2 = ImagingTrain.Asi585Native(2);

            bin1.PixelScaleArcsec.Should().BeApproximately(0.239, 0.0005);
            bin2.PixelScaleArcsec.Should().BeApproximately(0.479, 0.0005);
            bin2.BinnedWidthPx.Should().Be(1920);
            bin2.BinnedHeightPx.Should().Be(1080);
            bin2.BinnedPixelSizeUm.Should().BeApproximately(5.8, 1e-12);
            bin2.FieldWidthArcmin.Should().BeApproximately(15.31, 0.005);
            bin2.FieldHeightArcmin.Should().BeApproximately(8.61, 0.005);
            bin2.FieldDiagonalArcmin.Should().BeApproximately(17.57, 0.005);
            (bin2.FieldHeightArcmin / 60).Should().BeApproximately(0.1436, 0.00005);
            bin1.FieldWidthArcmin.Should().BeApproximately(bin2.FieldWidthArcmin, 1e-9);
        }

        [Test]
        public void ReducerNominal_MatchesResearchFovTable() {
            var bin1 = ImagingTrain.Asi585Reducer(1);
            var bin2 = ImagingTrain.Asi585Reducer(2);

            bin1.PixelScaleArcsec.Should().BeApproximately(0.380, 0.0005);
            bin2.PixelScaleArcsec.Should().BeApproximately(0.760, 0.0005);
            bin2.FieldWidthArcmin.Should().BeApproximately(24.31, 0.005);
            bin2.FieldHeightArcmin.Should().BeApproximately(13.67, 0.005);
            bin2.FieldDiagonalArcmin.Should().BeApproximately(27.89, 0.005);
            (bin2.FieldHeightArcmin / 60).Should().BeApproximately(0.2279, 0.00005);
        }

        [Test]
        public void Binning_DropsRemainderLikeZwo() {
            var train = new ImagingTrain("odd", 3841, 2161, 2.9, 2500, 2);
            train.BinnedWidthPx.Should().Be(1920);
            train.BinnedHeightPx.Should().Be(1080);
            FluentActions.Invoking(() => ImagingTrain.Asi585Native(0)).Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
