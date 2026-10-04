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
using System.Threading;

namespace NINA.Mac.ImageAnalysis.Test {

    /// <summary>
    /// Full N.I.N.A. pipeline (statistics, debayer, auto stretch, detection, measurement) on synthetic 1920 x 1080
    /// frames, the size of an ASI585MC bin-2 frame. Acceptance criteria:
    /// precision &gt;= 0.98, no detections on hot pixels (mono), recall &gt;= 0.9 for stars with peak SNR 80-500,
    /// positions within 0.1 px (median), and the frame's average HFR within 2% of the analytic value.
    /// </summary>
    [TestFixture]
    public class StarDetectionTests {

        private const double MatchRadius = 3.0;

        private sealed class Scenario {
            public SkyFrame Sky;
            public PixelBuffer Frame;
            public FrameAnalysis Analysis;
            public List<(SyntheticStar Truth, DetectedStar Detected, double Distance)> Matches;
            public StarDetectionResult Stars => Analysis.Stars;
        }

        private static Scenario Run(Func<int, Psf> psf, FrameAnalysisOptions options, BayerPattern pattern = BayerPattern.None, int hotPixels = 0, int seed = 42,
            (double R, double G, double B)? starColor = null, (double R, double G, double B)? skyColor = null, double maxFlux = 2e6, int count = 150) {
            var sky = new SkyFrame(1920, 1080) {
                Background = 1500, GradientX = 400, GradientY = -250, ReadNoise = 8, Gain = 1, Seed = seed, BayerPattern = pattern,
                StarColor = starColor ?? (1, 1, 1), SkyColor = skyColor ?? (1, 1, 1)
            };
            sky.AddStarField(count, psf, 2e3, maxFlux, 40, 30, seed + 1);
            var rng = new Random(seed + 2);
            for (int k = 0; k < hotPixels; k++) {
                sky.HotPixels.Add((rng.Next(20, 1900), rng.Next(20, 1060), (ushort)(k % 2 == 0 ? 65535 : 20000)));
            }
            var frame = sky.Render();
            var analysis = FrameAnalyzer.Analyze(frame, options);
            return new Scenario {
                Sky = sky,
                Frame = frame,
                Analysis = analysis,
                Matches = StarMatching.Match(sky.Stars, analysis.Stars.StarList, MatchRadius)
            };
        }

        private static void Report(Scenario s, string label) {
            var r = s.Stars;
            TestContext.Out.WriteLine($"{label}: resize {r.ResizeFactor:F3}, blobs {r.BlobCount}, stars {r.StarList.Count} (DetectedStars {r.DetectedStars}), matched {s.Matches.Count}/{s.Sky.Stars.Count}, " +
                $"avg HFR {r.AverageHFR:F3} (sd {r.HFRStdDev:F3}), avg FWHM {r.AverageFWHM:F3}, ecc {r.AverageEccentricity:F3}, {s.Analysis.TotalTime.TotalMilliseconds:F0} ms");
        }

        private static double Precision(Scenario s) => s.Stars.StarList.Count == 0 ? 0 : s.Matches.Count / (double)s.Stars.StarList.Count;

        private static double Recall(Scenario s, double minSnr, double maxSnr) {
            var band = s.Sky.Stars.Where(t => s.Sky.PeakSnr(t) >= minSnr && s.Sky.PeakSnr(t) <= maxSnr).ToList();
            band.Should().NotBeEmpty();
            return band.Count(t => s.Matches.Any(m => m.Truth == t)) / (double)band.Count;
        }

        /// <summary>Mean analytic HFR of the detected stars, each truncated to its own measurement aperture.</summary>
        private static double ExpectedAverageHfr(Scenario s, Func<SyntheticStar, DetectedStar, double> expected) {
            return s.Matches.Average(m => expected(m.Truth, m.Detected));
        }

        private static readonly FrameAnalysisOptions DefaultsWithoutOptics = new FrameAnalysisOptions();

        [TestCase(5.0)]
        [TestCase(8.0)]
        public void Mono_Gaussian_PrecisionRecallHfr(double fwhm) {
            var s = Run(_ => Psf.Gaussian(fwhm), DefaultsWithoutOptics, hotPixels: 40);
            Report(s, $"mono Gaussian FWHM {fwhm}");

            s.Stars.ResizeFactor.Should().BeApproximately(1552.0 / 1920.0, 1e-12, "High sensitivity without optics metadata");
            Precision(s).Should().BeGreaterThanOrEqualTo(0.98);
            s.Stars.StarList.Should().NotContain(d => s.Sky.HotPixels.Any(h => Math.Abs(h.X - d.Position.X) < 3 && Math.Abs(h.Y - d.Position.Y) < 3), "hot pixels are not stars");
            Recall(s, 80, 500).Should().BeGreaterThanOrEqualTo(0.9);
            s.Matches.Select(m => m.Distance).OrderBy(d => d).ElementAt(s.Matches.Count / 2).Should().BeLessThan(0.1);

            double expected = ExpectedAverageHfr(s, (t, d) => t.Psf.HalfFluxRadiusWithin(d.MeasurementRadius));
            TestContext.Out.WriteLine($"  expected avg HFR {expected:F3}, analytic untruncated {Psf.Gaussian(fwhm).HalfFluxRadius:F3}");
            s.Stars.AverageHFR.Should().BeApproximately(expected, 0.02 * expected);
            s.Stars.AverageFWHM.Should().BeApproximately(fwhm, 0.05 * fwhm);
        }

        [TestCase(3.0)]
        [TestCase(4.765)]
        public void Mono_Moffat_PrecisionRecallHfr(double beta) {
            var psf = Psf.Moffat(6.0, beta);
            var s = Run(_ => psf, DefaultsWithoutOptics, hotPixels: 40, seed: 7);
            Report(s, $"mono Moffat beta {beta} FWHM 6");

            Precision(s).Should().BeGreaterThanOrEqualTo(0.98);
            Recall(s, 80, 500).Should().BeGreaterThanOrEqualTo(0.9);
            double expected = ExpectedAverageHfr(s, (t, d) => t.Psf.HalfFluxRadiusWithin(d.MeasurementRadius));
            TestContext.Out.WriteLine($"  expected avg HFR {expected:F3} (aperture-truncated), untruncated {psf.HalfFluxRadius:F3}");
            s.Stars.AverageHFR.Should().BeApproximately(expected, 0.02 * expected);
        }

        [TestCase(BayerPattern.RGGB)]
        [TestCase(BayerPattern.GRBG)]
        public void Osc_DefaultPipeline_DebayeredHfr(BayerPattern pattern) {
            // warm star colour and red-heavy Bortle 8-9 sky
            var color = (R: 1.0, G: 0.9, B: 0.6);
            var s = Run(_ => Psf.Gaussian(6.0), DefaultsWithoutOptics, pattern, starColor: color, skyColor: (1.3, 1.0, 0.8), seed: 11);
            Report(s, $"OSC {pattern} Gaussian FWHM 6");

            Precision(s).Should().BeGreaterThanOrEqualTo(0.98);
            Recall(s, 80, 500).Should().BeGreaterThanOrEqualTo(0.9);

            // HFR is measured on NINA's debayered luminance, (R+G+B)/3 of a 3x3 same-colour-mean demosaic. That blurs the
            // star by the colour-weighted per-axis variance of the interpolation kernels: R and B 0.5 px^2, G 0.65 px^2.
            double v = ((color.R * 0.5) + (color.G * 0.65) + (color.B * 0.5)) / (color.R + color.G + color.B);
            double sigma = Psf.Gaussian(6.0).Sigma;
            var broadened = Psf.Gaussian(Math.Sqrt((sigma * sigma) + v) * 2.3548200450309493);
            double expected = ExpectedAverageHfr(s, (t, d) => broadened.HalfFluxRadiusWithin(d.MeasurementRadius));
            TestContext.Out.WriteLine($"  expected avg HFR {expected:F3} (demosaic-broadened), PSF alone {Psf.Gaussian(6.0).HalfFluxRadius:F3}");
            s.Stars.AverageHFR.Should().BeApproximately(expected, 0.02 * expected);
        }

        [Test]
        public void MonoBin_IsTheMonoPath() {
            // ASICamera clears the Bayer pattern in mono-bin mode, so the frame is analysed without debayering
            var s = Run(_ => Psf.Gaussian(6.0), DefaultsWithoutOptics, BayerPattern.None, seed: 13);
            s.Analysis.StretchedRgb48.Should().BeNull();
            s.Analysis.Stretched16.Should().NotBeNull();
            s.Stars.StarList.Should().NotBeEmpty();
        }

        [TestCase(5.0)]
        [TestCase(8.0)]
        public void RigOptics_HighSensitivity_DownsizesByFour(double fwhm) {
            // ASI585 2.9 um at 2500 mm = 0.24"/px unbinned. NINA's High sensitivity uses the unbinned scale, so even a
            // bin-2 frame is detected at 1/4 size (StarDetection.cs:86-88).
            var s = Run(_ => Psf.Gaussian(fwhm), FrameAnalysisOptions.ForRig(), hotPixels: 40);
            Report(s, $"rig optics Gaussian FWHM {fwhm}");
            TestContext.Out.WriteLine($"  recall SNR 80-500: {Recall(s, 80, 500):F2}, SNR 200-500: {Recall(s, 200, 500):F2}");

            s.Stars.ResizeFactor.Should().Be(0.25);
            Precision(s).Should().BeGreaterThanOrEqualTo(0.98);
            s.Stars.StarList.Count.Should().BeGreaterThanOrEqualTo(25);
            double expected = ExpectedAverageHfr(s, (t, d) => t.Psf.HalfFluxRadiusWithin(d.MeasurementRadius));
            s.Stars.AverageHFR.Should().BeApproximately(expected, 0.02 * expected);
        }

        [Test]
        public void Osc_HotPixels_SurviveTheDemosaic_AndAreCountedAsStars_LikeUpstream() {
            // Characterisation of upstream behaviour (no hot-pixel rejection before detection): on OSC frames a single
            // hot pixel becomes a 3x3 bump after the demosaic and passes the size filter at 0.81 resize. Calibrate with
            // darks / a bad-pixel map before relying on HFR from uncalibrated OSC frames.
            var s = Run(_ => Psf.Gaussian(6.0), DefaultsWithoutOptics, BayerPattern.RGGB, hotPixels: 60, seed: 11, starColor: (1.0, 0.9, 0.6), skyColor: (1.3, 1.0, 0.8));
            int hotHits = s.Stars.StarList.Count(d => s.Sky.HotPixels.Any(h => Math.Abs(h.X - d.Position.X) < 3 && Math.Abs(h.Y - d.Position.Y) < 3));
            Report(s, "OSC with 60 hot pixels");
            TestContext.Out.WriteLine($"  detections on hot pixels: {hotHits}");
            hotHits.Should().BeGreaterThan(0);
        }

        [Test]
        public void SaturatedStars_DoNotBreakDetection() {
            var sky = new SkyFrame(1920, 1080) { Background = 1500, ReadNoise = 8, Gain = 1, Seed = 21 };
            sky.AddStarField(100, _ => Psf.Gaussian(6.0), 5e4, 8e5, 40, 30, 22);
            sky.Stars.Add(new SyntheticStar { X = 500.4, Y = 400.7, Flux = 3e7, Psf = Psf.Gaussian(6.0) });
            sky.Stars.Add(new SyntheticStar { X = 1400.2, Y = 700.1, Flux = 5e7, Psf = Psf.Gaussian(6.0) });
            var analysis = FrameAnalyzer.Analyze(sky.Render(), DefaultsWithoutOptics);
            var matches = StarMatching.Match(sky.Stars, analysis.Stars.StarList, MatchRadius);
            var saturated = matches.Where(m => m.Truth.Peak > 65535).ToList();
            TestContext.Out.WriteLine($"saturated stars kept: {saturated.Count}, their HFR: {string.Join(", ", saturated.Select(m => m.Detected.HFR.ToString("F2")))}; avg HFR {analysis.Stars.AverageHFR:F3}");
            // NINA has no saturation check: clipped stars measure too large and are only dropped if the radius filter
            // (mean +/- 1.5 sigma of the blob radii) rejects them.
            foreach (var m in saturated) {
                m.Detected.HFR.Should().BeGreaterThan(m.Truth.Psf.HalfFluxRadius);
            }
            var unsaturated = matches.Where(m => m.Truth.Peak <= 65535).ToList();
            unsaturated.Count.Should().BeGreaterThan(30);
            unsaturated.Average(m => m.Detected.HFR / m.Truth.Psf.HalfFluxRadiusWithin(m.Detected.MeasurementRadius)).Should().BeApproximately(1, 0.02);
        }

        [Test]
        public void InnerCropRatio_KeepsOnlyCentralStars() {
            var options = new FrameAnalysisOptions { InnerCropRatio = 0.5 };
            var full = Run(_ => Psf.Gaussian(6.0), DefaultsWithoutOptics, seed: 31);
            var cropped = Run(_ => Psf.Gaussian(6.0), options, seed: 31);
            cropped.Stars.StarList.Should().NotBeEmpty();
            cropped.Stars.StarList.Count.Should().BeLessThan(full.Stars.StarList.Count);
            // the ROI test runs on blob rectangles in the downsized detection image, so allow a few pixels of slack
            cropped.Stars.StarList.Should().OnlyContain(d => d.Position.X > (1920 * 0.25) - 10 && d.Position.X < (1920 * 0.75) + 10
                && d.Position.Y > (1080 * 0.25) - 10 && d.Position.Y < (1080 * 0.75) + 10);
        }

        [Test]
        public void OuterCropRatio_MakesADonut() {
            var options = new FrameAnalysisOptions { InnerCropRatio = 0.3, OuterCropRatio = 0.8 };
            var s = Run(_ => Psf.Gaussian(6.0), options, seed: 31);
            s.Stars.StarList.Should().NotBeEmpty();
            s.Stars.StarList.Should().NotContain(d => Math.Abs(d.Position.X - 960) < (1920 * 0.15) - 10 && Math.Abs(d.Position.Y - 540) < (1080 * 0.15) - 10, "inside the inner box");
        }

        [Test]
        public void BrightestStars_AreSelectedThenTracked() {
            var sky = new SkyFrame(1920, 1080) { Background = 1500, ReadNoise = 8, Gain = 1, Seed = 41 };
            sky.AddStarField(80, _ => Psf.Gaussian(6.0), 5e4, 8e5, 40, 30, 42);
            var frame = sky.Render();
            var input = new StarDetectionInput { Width = frame.Width, Height = frame.Height, MeasurementData = frame.Data };
            input.DetectionImage16 = AutoStretch.Apply(frame.Data, AutoStretch.GetStretchMap(ImageStatistics.Create(frame)));

            var p = new StarDetectionParams { Sensitivity = StarSensitivity.High, NumberOfAFStars = 5 };
            var first = new StarDetector().Detect(input, p);
            first.StarList.Should().HaveCount(5);
            first.BrightestStarPositions.Should().HaveCount(5);
            first.DetectedStars.Should().BeGreaterThan(5, "DetectedStars counts all stars even when only the brightest are returned");

            p.MatchStarPositions = first.BrightestStarPositions;
            var second = new StarDetector().Detect(input, p);
            second.StarList.Select(st => st.Position).Should().Equal(first.BrightestStarPositions);
        }

        [Test]
        public void PureNoiseAndFlatFrames_GiveNoStars() {
            var sky = new SkyFrame(1920, 1080) { Background = 1500, ReadNoise = 8, Gain = 1, Seed = 51 };
            var noise = FrameAnalyzer.Analyze(sky.Render());
            noise.Stars.StarList.Should().BeEmpty();
            double.IsNaN(noise.Stars.AverageHFR).Should().BeTrue();

            var flat = new PixelBuffer(Enumerable.Repeat((ushort)1000, 640 * 480).ToArray(), 640, 480);
            var flatResult = FrameAnalyzer.Analyze(flat);
            flatResult.Stars.StarList.Should().BeEmpty();
        }

        [Test]
        public void Cancellation_ReturnsEmptyResult() {
            var sky = new SkyFrame(800, 600) { Seed = 61 };
            sky.AddStarField(30, _ => Psf.Gaussian(5.0), 5e4, 5e5, 30, 20, 62);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var result = FrameAnalyzer.Analyze(sky.Render(), null, cts.Token);
            result.Stars.StarList.Should().BeEmpty();
        }

        [Test]
        public void ParallelMeasurement_IsIdenticalToSequential() {
            var sky = new SkyFrame(1920, 1080) { Background = 1500, GradientX = 400, ReadNoise = 8, Gain = 1, Seed = 71 };
            sky.AddStarField(150, i => Psf.Moffat(4 + (i % 5), 3.5), 2e3, 2e6, 30, 30, 72);
            var frame = sky.Render();
            var input = new StarDetectionInput { Width = frame.Width, Height = frame.Height, MeasurementData = frame.Data };
            input.DetectionImage16 = AutoStretch.Apply(frame.Data, AutoStretch.GetStretchMap(ImageStatistics.Create(frame)));
            var p = StarDetectionParams.NinaProfileDefaults();
            var sequential = new StarDetector(parallel: false).Detect(input, p);
            var parallel = new StarDetector(parallel: true).Detect(input, p);
            sequential.StarList.Should().NotBeEmpty();
            parallel.StarList.Select(st => (st.Position, st.HFR, st.FWHM, st.Eccentricity, st.Background))
                .Should().Equal(sequential.StarList.Select(st => (st.Position, st.HFR, st.FWHM, st.Eccentricity, st.Background)));
            parallel.AverageHFR.Should().Be(sequential.AverageHFR);
        }

        [Test]
        public void ResizeFactor_FollowsNinaRules() {
            StarDetector.GetResizeFactor(1500, StarSensitivity.Normal, double.NaN, double.NaN).Should().Be(1.0);
            StarDetector.GetResizeFactor(1920, StarSensitivity.Normal, double.NaN, double.NaN).Should().Be(1552.0 / 1920);
            StarDetector.GetResizeFactor(3840, StarSensitivity.Normal, double.NaN, double.NaN).Should().Be(1552.0 / 3840);
            StarDetector.GetResizeFactor(3840, StarSensitivity.Highest, double.NaN, double.NaN).Should().Be(2 / 3d);
            StarDetector.GetResizeFactor(3840, StarSensitivity.High, double.NaN, double.NaN).Should().Be(0.5);
            StarDetector.GetResizeFactor(1920, StarSensitivity.High, 2.9, 2500).Should().Be(0.25, "0.24\"/px");
            StarDetector.GetResizeFactor(1920, StarSensitivity.High, 2.9, 1575).Should().Be(0.25, "0.38\"/px with the f/6.3 reducer");
            StarDetector.GetResizeFactor(3840, StarSensitivity.High, 3.76, 800).Should().Be(1 / 3d, "0.97\"/px");
            StarDetector.GetResizeFactor(3840, StarSensitivity.High, 3.76, 400).Should().Be(2 / 3d, "1.94\"/px: the > 1.5 test overrides the 1.5-2.5 band");
            StarDetector.ArcsecPerPixel(2.9, 2500).Should().BeApproximately(0.2393, 1e-4);
        }
    }
}
