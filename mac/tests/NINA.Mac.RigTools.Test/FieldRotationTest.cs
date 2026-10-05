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
using NINA.Mac.RigTools.Rotation;
using NUnit.Framework;
using System;

namespace NINA.Mac.RigTools.Test {

    /// <summary>
    /// Oracles: MAC_PORT_PLAN.md section 6 table and research/rig_investigate_solve.md tables 4-5 (re-verified in
    /// research/rig_verify_solve.md). Those values are printed to 0.1 s and whole degrees, so the tolerance is
    /// half the last printed digit (plus 1e-9 for floating point): +/-0.05 s for times, +/-0.5 deg for whole-degree
    /// altitudes and session rotation, +/-0.05 deg for altitudes printed to 0.1 deg.
    /// </summary>
    [TestFixture]
    public class FieldRotationTest {
        private const double Lat = 22.25;
        private const double SecondsTolerance = 0.05 + 1e-9;
        private const double DegreesTolerance = 0.5 + 1e-9;
        private const double TenthDegreeTolerance = 0.05 + 1e-9;
        private const double WholeSecondTolerance = 0.5 + 1e-9;

        [TestCase(-30.0, 0.0, 10.6, 38.0, TestName = "Plan table: Dec -30 at transit 10.6 s, alt 38")]
        [TestCase(-30.0, 3.0, 16.6, double.NaN, TestName = "Plan table: Dec -30 at HA +3h 16.6 s")]
        [TestCase(-30.0, -3.0, 16.6, double.NaN, TestName = "Plan table: Dec -30 at HA -3h 16.6 s")]
        [TestCase(0.0, 0.0, 5.1, 68.0, TestName = "Plan table: Dec 0 at transit 5.1 s, alt 68")]
        [TestCase(0.0, 3.0, 28.7, double.NaN, TestName = "Plan table: Dec 0 at HA +3h 28.7 s")]
        [TestCase(0.0, -3.0, 28.7, double.NaN, TestName = "Plan table: Dec 0 at HA -3h 28.7 s")]
        [TestCase(10.0, 0.0, 2.9, 78.0, TestName = "Plan table: Dec +10 at transit 2.9 s, alt 78")]
        [TestCase(10.0, 3.0, 64.7, double.NaN, TestName = "Plan table: Dec +10 at HA +3h 64.7 s")]
        [TestCase(10.0, -3.0, 64.7, double.NaN, TestName = "Plan table: Dec +10 at HA -3h 64.7 s")]
        public void MaxSub_MatchesPlanSection6Table(double dec, double haHours, double expectedSeconds, double expectedAltitude) {
            var train = ImagingTrain.Asi585Native(2);

            var seconds = FieldRotation.MaxSubSecondsAtHourAngle(Lat, haHours, dec, train, 1.0);

            seconds.Should().BeApproximately(expectedSeconds, SecondsTolerance);
            if (!double.IsNaN(expectedAltitude)) {
                SphericalAstronomy.EquatorialToHorizontal(haHours, dec, Lat).AltitudeDeg.Should().BeApproximately(expectedAltitude, DegreesTolerance);
            }
        }

        [TestCase(-45.0, 0.0, 12.4, 22.8)]
        [TestCase(-45.0, 3.0, 15.3, 11.2)]
        [TestCase(-30.0, 1.0, 11.4, 35.8)]
        [TestCase(-30.0, 2.0, 13.4, 30.3)]
        [TestCase(-5.0, 0.0, 6.2, 62.8)]
        [TestCase(-5.0, 2.0, 13.7, 50.0)]
        [TestCase(-5.0, 3.0, 23.9, 38.2)]
        [TestCase(10.0, 1.0, 7.1, 71.1)]
        [TestCase(10.0, 2.0, 22.3, 58.8)]
        [TestCase(20.0, 0.0, 0.5, double.NaN)]
        [TestCase(20.0, 1.0, 29.7, 75.8)]
        public void MaxSub_MatchesResearchHourAngleTable(double dec, double haHours, double expectedSeconds, double expectedAltitude) {
            // research/rig_investigate_solve.md table 5 (times to 0.1 s, altitudes to 0.1 deg).
            var seconds = FieldRotation.MaxSubSecondsAtHourAngle(Lat, haHours, dec, ImagingTrain.Asi585Native(2));

            seconds.Should().BeApproximately(expectedSeconds, SecondsTolerance);
            if (!double.IsNaN(expectedAltitude)) {
                SphericalAstronomy.EquatorialToHorizontal(haHours, dec, Lat).AltitudeDeg.Should().BeApproximately(expectedAltitude, TenthDegreeTolerance);
            }
        }

        // Each case carries the tolerance of its source: half the last printed digit.
        // 10.3 s: rig_verify_solve.md:96 ("Alt 40/Az 180 10.3 s", tenths; table 4 prints the same cell as "10").
        // 13 s: research/rig_investigate_solve.md table 4, which prints whole seconds above 10 s.
        // The rest: table 4 cells printed to 0.1 s.
        [TestCase(40.0, 180.0, 10.3, SecondsTolerance)]
        [TestCase(20.0, 180.0, 13.0, WholeSecondTolerance)]
        [TestCase(60.0, 150.0, 7.8, SecondsTolerance)]
        [TestCase(75.0, 120.0, 7.0, SecondsTolerance)]
        [TestCase(85.0, 180.0, 1.2, SecondsTolerance)]
        public void MaxSub_MatchesResearchAltAzGrid(double alt, double az, double expectedSeconds, double tolerance) {
            FieldRotation.MaxSubSeconds(Lat, alt, az, ImagingTrain.Asi585Native(2)).Should().BeApproximately(expectedSeconds, tolerance);
            // Symmetric about the meridian.
            FieldRotation.MaxSubSeconds(Lat, alt, 360 - az, ImagingTrain.Asi585Native(2)).Should().BeApproximately(expectedSeconds, tolerance);
        }

        [TestCase(-45.0, 58.0)]
        [TestCase(-30.0, 65.0)]
        [TestCase(-5.0, 92.0)]
        [TestCase(10.0, 126.0)]
        public void SessionRotation_OverPlusMinusTwoHours_Matches58To126DegreeClaim(double dec, double expectedDeg) {
            // MAC_PORT_PLAN.md section 6: "Over +/-2 h the frame rotates 58-126 deg"; per-Dec values from
            // research/rig_investigate_solve.md SLV-17 (whole degrees).
            var rotation = FieldRotation.OverHourAngles(Lat, dec, -2, 2);

            rotation.SpanDeg.Should().BeApproximately(expectedDeg, DegreesTolerance);
            Math.Abs(rotation.NetDeg).Should().BeApproximately(rotation.SpanDeg, 1e-9, "q is monotonic for these southern transits");
        }

        [Test]
        public void HalfDiagonal_IsThe1101Point5PixelCornerRadiusAtBin2() {
            ImagingTrain.Asi585Native(2).HalfDiagonalPx.Should().BeApproximately(1101.45, 0.01);
            // 1 binned pixel of corner blur = 187.3" of rotation (research table 4 caption).
            (AngleMath.ArcsecPerRadian / ImagingTrain.Asi585Native(2).HalfDiagonalPx).Should().BeApproximately(187.3, 0.05);
        }

        [Test]
        public void MaxSub_IsTheSameWithOrWithoutTheReducer() {
            // Focal length scales sky-arcsec per pixel but not the rotation angle or the corner radius in pixels.
            var native = ImagingTrain.Asi585Native(2);
            foreach (var reducer in new[] { ImagingTrain.Asi585Reducer(2), ImagingTrain.Asi585Reducer(2, 1617) }) {
                reducer.PixelScaleArcsec.Should().NotBeApproximately(native.PixelScaleArcsec, 0.1);
                foreach (var dec in new[] { -45.0, -30.0, 0.0, 10.0 }) {
                    foreach (var ha in new[] { -3.0, -1.0, 0.0, 2.0 }) {
                        FieldRotation.MaxSubSecondsAtHourAngle(Lat, ha, dec, reducer)
                            .Should().Be(FieldRotation.MaxSubSecondsAtHourAngle(Lat, ha, dec, native));
                    }
                }
            }
        }

        [Test]
        public void MaxSub_ScalesWithBlurAndInverselyWithCornerRadius() {
            var bin2 = FieldRotation.MaxSubSecondsAtHourAngle(Lat, 0, -30, ImagingTrain.Asi585Native(2), 1.0);

            FieldRotation.MaxSubSecondsAtHourAngle(Lat, 0, -30, ImagingTrain.Asi585Native(2), 2.0).Should().BeApproximately(2 * bin2, 1e-9);
            // Bin 1 doubles the corner radius in pixels, so 1 px of blur comes twice as fast.
            FieldRotation.MaxSubSecondsAtHourAngle(Lat, 0, -30, ImagingTrain.Asi585Native(1), 1.0).Should().BeApproximately(bin2 / 2, 1e-9);
        }

        [Test]
        public void Rate_IsZeroDueEastAndWestAndUnlimitedSubThere() {
            FieldRotation.RateRadPerSec(Lat, 45, 90).Should().BeApproximately(0, 1e-18);
            FieldRotation.MaxSubSeconds(Lat, 45, 90, ImagingTrain.Asi585Native(2)).Should().Be(double.PositiveInfinity);
            FieldRotation.MaxSubSeconds(Lat, 45, 270, ImagingTrain.Asi585Native(2)).Should().Be(double.PositiveInfinity);
        }

        [Test]
        public void Rate_DueSouthAt22N_Is13Point92DegPerHourOverCosAlt() {
            // research table 4: rate = 13.92 deg/h * cos A / cos h at 22.25 N; e.g. 18.2 deg/h at alt 40, az 180.
            Math.Abs(FieldRotation.RateDegPerHour(Lat, 0, 180)).Should().BeApproximately(13.92, 0.005);
            Math.Abs(FieldRotation.RateDegPerHour(Lat, 40, 180)).Should().BeApproximately(18.2, 0.05);
            Math.Abs(FieldRotation.RateDegPerHour(Lat, 85, 180)).Should().BeApproximately(159.7, 0.05);
        }

        [TestCase(-30.0, -2.5)]
        [TestCase(-30.0, 0.0)]
        [TestCase(0.0, 1.7)]
        [TestCase(10.0, -0.4)]
        [TestCase(45.0, 3.0)]
        [TestCase(70.0, 8.0)]
        public void Rate_EqualsNumericalDerivativeOfMeeusParallacticAngle(double dec, double haHours) {
            // dq/dt from Meeus eq. 14.1 by central difference over +/-1 s of sidereal time.
            const double dt = 1.0;
            var dHours = dt * FieldRotation.SiderealRateRadPerSec * AngleMath.RadToDeg / 15.0;
            var q1 = SphericalAstronomy.ParallacticAngleDeg(haHours - dHours, dec, Lat);
            var q2 = SphericalAstronomy.ParallacticAngleDeg(haHours + dHours, dec, Lat);
            var numerical = AngleMath.NormalizeSigned180(q2 - q1) * AngleMath.DegToRad / (2 * dt);

            var analytic = FieldRotation.RateRadPerSecAtHourAngle(Lat, haHours, dec);

            analytic.Should().BeApproximately(numerical, Math.Abs(numerical) * 1e-6 + 1e-12);
        }

        [Test]
        public void CornerBlur_IsRateTimesExposureTimesRadius() {
            var rate = FieldRotation.RateRadPerSecAtHourAngle(Lat, 0, -30);
            var limit = FieldRotation.MaxSubSeconds(rate, 1101.45, 1.0);

            FieldRotation.CornerBlurPx(rate, limit, 1101.45).Should().BeApproximately(1.0, 1e-9);
        }

        [Test]
        public void FrameRotation_UnwrapsAcrossTheMinus180Seam() {
            // North of the zenith (Dec > latitude) q passes through +/-180 at transit.
            var rotation = FieldRotation.OverHourAngles(Lat, 40, -1, 1);
            rotation.SpanDeg.Should().BeLessThan(180);
            rotation.SpanDeg.Should().BeGreaterThan(0);
            FrameRotation.FromParallacticAngles(new[] { 170.0, 179.0, -179.0, -170.0 }).NetDeg.Should().BeApproximately(20, 1e-9);
        }
    }
}
