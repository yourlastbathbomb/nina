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
using NINA.Mac.ImageAnalysis.AccordPort;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Mac.ImageAnalysis.Test {

    /// <summary>
    /// The star measurement maths (StarDetection.Star.Calculate) on noise-free, pixel-integrated stars with known
    /// centre and background, against closed-form values. NINA measures the half-flux radius inside an aperture of
    /// 1.5 x the detected radius, so the reference is the analytic half-flux radius of the flux inside that aperture.
    /// </summary>
    [TestFixture]
    public class HfrMeasurementTests {

        private const int Size = 121;

        private static StarDetector.Star Measure(double[] signal, double cx, double cy, double detectionRadius, double background = 0) {
            var pixels = new List<StarDetector.PixelData>(Size * Size);
            for (int x = 0; x < Size; x++) {
                for (int y = 0; y < Size; y++) {
                    pixels.Add(new StarDetector.PixelData(x, y, signal[(y * Size) + x] + background));
                }
            }
            var star = new StarDetector.Star {
                // start the centroid slightly off to exercise the windowed centroid iteration
                Position = new AccordPoint((float)(cx + 0.3), (float)(cy - 0.2)),
                Radius = detectionRadius,
                Rectangle = new PixelRect(0, 0, Size, Size)
            };
            star.SurroundingMean = background;
            star.MaxPixelValue = signal.Max() + background;
            star.Calculate(pixels);
            return star;
        }

        private static double[] Render(Psf psf, double cx, double cy) {
            var sky = new SkyFrame(Size, Size) { Supersample = 5 };
            sky.Stars.Add(new SyntheticStar { X = cx, Y = cy, Flux = 1e6, Psf = psf });
            return sky.RenderStarSignal();
        }

        private static IEnumerable<TestCaseData> WellSampledPsfs() {
            foreach (var fwhm in new[] { 4.0, 6.0, 8.0, 12.0 }) {
                yield return new TestCaseData(Psf.Gaussian(fwhm)).SetName($"{{m}}(Gaussian FWHM {fwhm})");
                yield return new TestCaseData(Psf.Moffat(fwhm, 3.0)).SetName($"{{m}}(Moffat b=3 FWHM {fwhm})");
                yield return new TestCaseData(Psf.Moffat(fwhm, 4.765)).SetName($"{{m}}(Moffat b=4.765 FWHM {fwhm})");
            }
        }

        /// <summary>
        /// HFR averaged over a 5 x 5 grid of sub-pixel star positions (what a frame average sees) is within 2% of the
        /// analytic value. Individual stars scatter more: upstream's curve of growth interpolates between successive
        /// pixel-centre distances, and near the core single pixels carry several percent of the flux, so the result
        /// depends on the sub-pixel phase (characterised in <see cref="Hfr_SubpixelPhaseScatter"/>).
        /// </summary>
        [TestCaseSource(nameof(WellSampledPsfs))]
        public void Hfr_MeanOverSubpixelPhases_WithinTwoPercentOfAnalytic(Psf psf) {
            var ratios = PhaseRatios(psf);
            TestContext.Out.WriteLine($"HFR/analytic over 25 phases: mean {ratios.Average():F4} min {ratios.Min():F4} max {ratios.Max():F4}");
            ratios.Average().Should().BeApproximately(1, 0.02);
        }

        /// <summary>
        /// Characterisation of upstream's HFR definition: worst single-star deviation over 25 sub-pixel phases is
        /// within 12% for FWHM 4-6 px, 4% for 8 px and 2% for 12 px.
        /// </summary>
        [TestCaseSource(nameof(WellSampledPsfs))]
        public void Hfr_SubpixelPhaseScatter(Psf psf) {
            var ratios = PhaseRatios(psf);
            double worst = ratios.Max(r => Math.Abs(r - 1));
            TestContext.Out.WriteLine($"worst single-phase deviation {worst:P1}");
            double limit = psf.Fwhm >= 12 ? 0.02 : psf.Fwhm >= 8 ? 0.04 : 0.12;
            worst.Should().BeLessThanOrEqualTo(limit);
        }

        private static List<double> PhaseRatios(Psf psf) {
            var ratios = new List<double>();
            for (int k = 0; k < 25; k++) {
                double cx = 60 + ((k % 5) * 0.2), cy = 60 + ((k / 5) * 0.2);
                var star = Measure(Render(psf, cx, cy), cx, cy, detectionRadius: 1.5 * psf.Fwhm, background: 500);
                ratios.Add(star.HFR / psf.HalfFluxRadiusWithin(star.MeasurementRadius));
                star.Position.DistanceTo(new AccordPoint((float)cx, (float)cy)).Should().BeLessThan(0.02f, "windowed centroid of a symmetric star");
            }
            return ratios;
        }

        [TestCaseSource(nameof(WellSampledPsfs))]
        public void Fwhm_MatchesProfile(Psf psf) {
            var star = Measure(Render(psf, 60.25, 59.6), 60.25, 59.6, detectionRadius: 1.5 * psf.Fwhm);
            // radial profile in 0.5 px bins on a pixel-integrated star: within 5% from 6 px, 10% at 4 px
            double tolerance = psf.Fwhm >= 6 ? 0.05 : 0.10;
            star.FWHM.Should().BeApproximately(psf.Fwhm, tolerance * psf.Fwhm);
            star.Eccentricity.Should().BeLessThan(0.15, "round star");
        }

        [TestCase(6.0, 0.6)]
        [TestCase(8.0, 0.8)]
        public void Eccentricity_OfEllipticalGaussian(double fwhmMajor, double axisRatio) {
            double sx = fwhmMajor / 2.3548200450309493, sy = sx * axisRatio;
            var signal = new double[Size * Size];
            for (int y = 0; y < Size; y++) {
                for (int x = 0; x < Size; x++) {
                    double dx = x - 60.0, dy = y - 60.0;
                    signal[(y * Size) + x] = 1e4 * Math.Exp(-((dx * dx) / (2 * sx * sx)) - ((dy * dy) / (2 * sy * sy)));
                }
            }
            var star = Measure(signal, 60, 60, detectionRadius: 1.5 * fwhmMajor);
            double expected = Math.Sqrt(1 - (axisRatio * axisRatio));
            // truncation by the circular aperture makes the measured moments slightly rounder
            star.Eccentricity.Should().BeApproximately(expected, 0.06);
        }

        [Test]
        public void BackgroundBelowStar_IsIgnored_NegativeResidualsClamped() {
            var psf = Psf.Gaussian(6);
            var signal = Render(psf, 60, 60);
            var rng = new Random(4);
            var noisy = signal.Select(v => v + (rng.NextDouble() - 0.5) * 2).ToArray();
            var star = Measure(noisy, 60, 60, detectionRadius: 9, background: 1000);
            var clean = Measure(signal, 60, 60, detectionRadius: 9, background: 1000);
            // +/-1 ADU noise on a 1e6 ADU star: positive clamping of the wings must not move the HFR noticeably
            star.HFR.Should().BeApproximately(clean.HFR, 0.01 * clean.HFR);
        }
    }
}
