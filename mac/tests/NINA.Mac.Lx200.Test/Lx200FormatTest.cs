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
    public class Lx200FormatTest {
        private const double Sec = 1.0 / 3600.0;
        private const string Df = "ß";   // the 0xDF degree byte as decoded with Latin-1

        // ---- RA --------------------------------------------------------------------------------

        [TestCase("05:35.3", 5 + (35.3 / 60))]
        [TestCase("05:35.3#", 5 + (35.3 / 60))]
        [TestCase("05:35:17", 5 + (35 / 60.0) + (17 / 3600.0))]
        [TestCase("00:00:00", 0)]
        [TestCase("23:59:59", 24 - (1 / 3600.0))]
        public void ParseRaHours_ShortAndLong(string text, double expected) {
            Lx200Format.ParseRaHours(text).Should().BeApproximately(expected, 1e-9);
        }

        [TestCase("")]
        [TestCase("24:00:00")]
        [TestCase("12:60:00")]
        [TestCase("12:30:60")]
        [TestCase("12-30-00")]
        [TestCase("ÿx")]
        public void ParseRaHours_Garbage_Throws(string text) {
            FluentActions.Invoking(() => Lx200Format.ParseRaHours(text)).Should().Throw<FormatException>();
        }

        [Test]
        public void DetectPrecision_DotIsLow_SecondColonIsHigh() {
            Lx200Format.DetectPrecision("18:59.5#").Should().Be(CoordinatePrecision.Low);
            Lx200Format.DetectPrecision("18:59:28#").Should().Be(CoordinatePrecision.High);
        }

        [TestCase(5.5, CoordinatePrecision.High, "05:30:00")]
        [TestCase(5.5, CoordinatePrecision.Low, "05:30.0")]
        [TestCase(23.99999, CoordinatePrecision.High, "00:00:00")]   // 23:59:59.96 rounds into the next day
        [TestCase(23.9999, CoordinatePrecision.Low, "00:00.0")]      // 23:59.994 rounds up, carries
        [TestCase(12.99999, CoordinatePrecision.High, "13:00:00")]   // seconds carry into minutes and hours
        [TestCase(-0.5, CoordinatePrecision.High, "23:30:00")]
        [TestCase(24.25, CoordinatePrecision.High, "00:15:00")]
        public void FormatRa_RoundsThenWraps(double hours, CoordinatePrecision p, string expected) {
            Lx200Format.FormatRa(hours, p).Should().Be(expected);
        }

        // ---- Angles ----------------------------------------------------------------------------

        [TestCase("-22" + Df + "45", -22.75)]
        [TestCase("-22*45'00", -22.75)]
        [TestCase("-22*45:00", -22.75)]
        [TestCase("-22:45:00", -22.75)]
        [TestCase("+45" + Df + "00'30#", 45 + (30 / 3600.0))]
        [TestCase("45*00", 45.0)]
        [TestCase("-00*30", -0.5)]                         // the sign applies to the whole value
        [TestCase("-00" + Df + "00'30", -30 / 3600.0)]
        [TestCase("+00*00'00", 0.0)]
        [TestCase("180" + Df + "01'43", 180 + (1 / 60.0) + (43 / 3600.0))]
        [TestCase("245*49", 245 + (49 / 60.0))]
        [TestCase("-114" + Df + "11", -(114 + (11 / 60.0)))]
        [TestCase("+90*00'00", 90.0)]
        public void ParseDegrees_AcceptsBothDegreeGlyphsAndSeparators(string text, double expected) {
            Lx200Format.ParseDegrees(text).Should().BeApproximately(expected, 1e-9);
        }

        [TestCase("")]
        [TestCase("22")]
        [TestCase("22*60")]
        [TestCase("22*30'60")]
        [TestCase("x22*30")]
        [TestCase("22#30")]
        public void ParseDegrees_Garbage_Throws(string text) {
            FluentActions.Invoking(() => Lx200Format.ParseDegrees(text)).Should().Throw<FormatException>();
        }

        [Test]
        public void DetectAnglePrecision() {
            Lx200Format.DetectAnglePrecision("-22" + Df + "45#").Should().Be(CoordinatePrecision.Low);
            Lx200Format.DetectAnglePrecision("-22" + Df + "45'00#").Should().Be(CoordinatePrecision.High);
        }

        [TestCase(-22.75, CoordinatePrecision.High, "-22*45:00")]
        [TestCase(-22.75, CoordinatePrecision.Low, "-22*45")]
        [TestCase(22.25, CoordinatePrecision.High, "+22*15:00")]
        [TestCase(89.99999, CoordinatePrecision.High, "+90*00:00")]     // 89°59'59.96" carries to 90
        [TestCase(95.0, CoordinatePrecision.High, "+90*00:00")]         // clamped
        [TestCase(-0.0001, CoordinatePrecision.High, "+00*00:00")]      // rounds to zero: no "-00"
        [TestCase(-0.01, CoordinatePrecision.High, "-00*00:36")]        // negative below one degree keeps its sign
        [TestCase(-0.01, CoordinatePrecision.Low, "-00*01")]
        [TestCase(-0.005, CoordinatePrecision.Low, "+00*00")]       // -0.3' rounds to 0': no sign
        [TestCase(-0.2, CoordinatePrecision.Low, "-00*12")]
        [TestCase(10.99999, CoordinatePrecision.Low, "+11*00")]
        public void FormatDec_SignAndRollover(double degrees, CoordinatePrecision p, string expected) {
            Lx200Format.FormatDec(degrees, p).Should().Be(expected);
        }

        [Test]
        public void FormatDec_WithHandboxDegreeByte_RoundTrips() {
            var text = Lx200Format.FormatDec(-5.391, CoordinatePrecision.High, Lx200Format.DegreeChar);
            text.Should().Be("-05" + Df + "23:28");
            Lx200Format.ParseDegrees(text).Should().BeApproximately(-5.391, 0.5 * Sec);
        }

        [Test]
        public void FormatAltitude_UsesApostropheForSeconds() {
            Lx200Format.FormatAltitude(45.5, CoordinatePrecision.High).Should().Be("+45*30'00");
        }

        [TestCase(180.0, CoordinatePrecision.High, "180*00:00")]
        [TestCase(359.99999, CoordinatePrecision.High, "000*00:00")]
        [TestCase(-10.0, CoordinatePrecision.High, "350*00:00")]
        [TestCase(359.995, CoordinatePrecision.Low, "000*00")]
        [TestCase(7.5, CoordinatePrecision.Low, "007*30")]
        public void FormatAzimuth_Wraps(double degrees, CoordinatePrecision p, string expected) {
            Lx200Format.FormatAzimuth(degrees, p).Should().Be(expected);
        }

        [Test]
        public void FormatThenParse_RoundTripsAcrossTheRange() {
            var rng = new Random(42);
            for (var i = 0; i < 2000; i++) {
                var ra = rng.NextDouble() * 24;
                var dec = (rng.NextDouble() * 180) - 90;
                var az = rng.NextDouble() * 360;
                var raBack = Lx200Format.ParseRaHours(Lx200Format.FormatRa(ra, CoordinatePrecision.High));
                Lx200Astro.HourDifference(raBack, ra).Should().BeApproximately(0, 0.5 * Sec + 1e-12);
                Lx200Format.ParseDegrees(Lx200Format.FormatDec(dec, CoordinatePrecision.High)).Should().BeApproximately(dec, 0.5 * Sec + 1e-12);
                Lx200Format.ParseDegrees(Lx200Format.FormatDec(dec, CoordinatePrecision.Low)).Should().BeApproximately(dec, (0.5 / 60) + 1e-12);
                var azBack = Lx200Format.ParseDegrees(Lx200Format.FormatAzimuth(az, CoordinatePrecision.High));
                Lx200Astro.DegreeDifference(azBack, az).Should().BeApproximately(0, 0.5 * Sec + 1e-12);
            }
        }

        // ---- Site ------------------------------------------------------------------------------

        [Test]
        public void HongKong_Longitude_BothCommandForms() {
            // MAC_PORT_PLAN.md section 5 step 3: 114.18 E is :Sg245*49#; research says also try -114*11
            Lx200Format.FormatLongitudeWest360(114.18).Should().Be("245*49");
            Lx200Format.FormatLongitudeSigned(114.18).Should().Be("-114*11");
        }

        [TestCase(-74.0, "074*00", "074*00")]     // 74 W: both forms are the same number
        [TestCase(0.0, "000*00", "000*00")]
        [TestCase(-0.004, "000*00", "000*00")]    // rounds to the meridian: no "-000"
        [TestCase(179.99, "180*01", "-179*59")]
        [TestCase(-180.0, "180*00", "180*00")]
        public void Longitude_Forms_EdgeCases(double east, string west360, string signed) {
            Lx200Format.FormatLongitudeWest360(east).Should().Be(west360);
            Lx200Format.FormatLongitudeSigned(east).Should().Be(signed);
        }

        [TestCase("-114*11", 114 + (11 / 60.0))]
        [TestCase("-114" + Df + "11#", 114 + (11 / 60.0))]
        [TestCase("245*49", 114 + (11 / 60.0))]           // 0-360 westward form reads back as the same East longitude
        [TestCase("+074*00", -74.0)]
        [TestCase("114*11", -(114 + (11 / 60.0)))]        // unsigned small value: West, per P07's East-negative rule
        [TestCase("000*00", 0.0)]
        public void ParseLongitudeEast(string text, double expectedEast) {
            Lx200Format.ParseLongitudeEast(text).Should().BeApproximately(expectedEast, 1e-9);
        }

        [TestCase(22.25, "+22*15")]
        [TestCase(-33.8, "-33*48")]
        [TestCase(-0.001, "+00*00")]
        public void FormatLatitude(double degrees, string expected) {
            Lx200Format.FormatLatitude(degrees).Should().Be(expected);
        }

        [TestCase(8.0, false, "-08")]          // Hong Kong: add -8 h to local time to get UTC
        [TestCase(8.0, true, "-08.0")]
        [TestCase(-5.0, false, "+05")]
        [TestCase(5.5, true, "-05.5")]
        [TestCase(0.0, false, "+00")]
        public void FormatHoursToUtc_SignConvention(double utcOffset, bool withDecimal, string expected) {
            Lx200Format.FormatHoursToUtc(utcOffset, withDecimal).Should().Be(expected);
        }

        [Test]
        public void FormatHoursToUtc_FractionalNeedsDecimalForm() {
            FluentActions.Invoking(() => Lx200Format.FormatHoursToUtc(5.5, false)).Should().Throw<ArgumentException>();
        }

        [TestCase("-08", -8.0)]
        [TestCase("-08#", -8.0)]
        [TestCase("+05.5", 5.5)]
        [TestCase("-8.0", -8.0)]
        [TestCase("05", 5.0)]
        public void ParseHoursToUtc(string text, double expected) {
            Lx200Format.ParseHoursToUtc(text).Should().Be(expected);
        }

        // ---- Time, date, commands ------------------------------------------------------------------

        [Test]
        public void TimeAndDate() {
            Lx200Format.FormatTime(new TimeSpan(1, 0, 0)).Should().Be("01:00:00");
            Lx200Format.FormatTime(new TimeSpan(23, 59, 59) + TimeSpan.FromMilliseconds(999)).Should().Be("23:59:59");
            Lx200Format.FormatDate(new DateTime(2026, 10, 4)).Should().Be("10/04/26");
            Lx200Format.FormatStartupDateTime(new DateTime(2026, 10, 4, 21, 30, 5)).Should().Be("261004213005");
            Lx200Format.ParseTime("01:00:02#").Should().Be(new TimeSpan(1, 0, 2));
            Lx200Format.ParseDate("10/03/26#").Should().Be(new DateTime(2026, 10, 3));
            FluentActions.Invoking(() => Lx200Format.ParseTime("24:00:00")).Should().Throw<FormatException>();
            FluentActions.Invoking(() => Lx200Format.ParseDate("13/01/26")).Should().Throw<FormatException>();
            FluentActions.Invoking(() => Lx200Format.ParseDate("02/30/26")).Should().Throw<FormatException>();
        }

        [Test]
        public void PulseGuideCommand_FourDigitsOnly() {
            Lx200Format.PulseGuideCommand('n', 2000).Should().Be(":Mgn2000#");
            Lx200Format.PulseGuideCommand('w', 5).Should().Be(":Mgw0005#");
            FluentActions.Invoking(() => Lx200Format.PulseGuideCommand('n', 10000)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => Lx200Format.PulseGuideCommand('n', 0)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => Lx200Format.PulseGuideCommand('x', 100)).Should().Throw<ArgumentException>();
        }

        [Test]
        public void FocusPulseCommand_SignedPositiveInward() {
            Lx200Format.FocusPulseCommand(1000).Should().Be(":FP+1000#");
            Lx200Format.FocusPulseCommand(-500).Should().Be(":FP-0500#");
            Lx200Format.FocusPulseCommand(65000).Should().Be(":FP+65000#");
            FluentActions.Invoking(() => Lx200Format.FocusPulseCommand(65001)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => Lx200Format.FocusPulseCommand(0)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Test]
        public void GuideRateCommand_InvariantAndCapped() {
            Lx200Format.GuideRateCommand(10).Should().Be(":Rg10.0#");
            Lx200Format.GuideRateCommand(7.52).Should().Be(":Rg07.5#");
            Lx200Format.GuideRateCommand(15.0417).Should().Be(":Rg15.0#");
            FluentActions.Invoking(() => Lx200Format.GuideRateCommand(16)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Test]
        public void DescribeDegreeGlyph() {
            Lx200Format.DescribeDegreeGlyph(new byte[] { 0x2D, 0x32, 0x32, 0xDF, 0x34, 0x35, 0x23 }).Should().Be("0xDF");
            Lx200Format.DescribeDegreeGlyph("-22*45#"u8).Should().Be("'*' (0x2A)");
            Lx200Format.DescribeDegreeGlyph("12:34:56#"u8).Should().Be("none");
        }

        [Test]
        public void Printable_EscapesNonAscii() {
            Lx200Format.Printable("-22" + Df + "45\u0006").Should().Be("-22\\xDF45\\x06");
        }
    }
}
