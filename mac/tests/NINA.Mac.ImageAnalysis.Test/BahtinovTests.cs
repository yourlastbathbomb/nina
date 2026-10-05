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
    /// Bahtinov analysers on synthetic mask patterns with a known central-spike displacement (sign convention of
    /// <see cref="BahtinovResult.SignedOffset"/>). Upstream's pairing needs all three spikes on the same side of
    /// horizontal, so the faithful port is tested at 30-150 degrees; the robust variant at all orientations.
    /// </summary>
    [TestFixture]
    public class BahtinovTests {

        private static readonly double[] Angles = { 30, 62, 85, 120, 150 };
        private static readonly double[] Offsets = { -8, -4, -2, 0, 2, 4, 8 };

        private static BahtinovResult Analyze(double angle, double offset, int width = 200, int height = 200) {
            var img = BahtinovPattern.Render(width, height, (width / 2.0) + 0.4, (height / 2.0) - 0.4, angle, 17, offset, seed: (int)((angle * 10) + offset + 100));
            return BahtinovAnalyzer.Analyze(img);
        }

        /// <summary>
        /// The faithful port reproduces upstream's weaknesses, so it is held to population criteria: on these 35
        /// synthetic patterns a few frames fail because two equal neighbouring Hough cells both survive Accord's peak
        /// search and push one edge of the weakest spike out of the top six (a plateau tie that depends on the noise).
        /// </summary>
        [Test]
        public void Nina_KnownOffsets_SignAndMagnitude() {
            var errors = new List<double>();
            int signChecks = 0, signCorrect = 0;
            foreach (var angle in Angles) {
                foreach (var offset in Offsets) {
                    var r = Analyze(angle, offset);
                    r.Success.Should().BeTrue($"angle {angle} offset {offset}");
                    r.Lines.Should().HaveCount(3);
                    r.Distance.Should().BeApproximately(Math.Abs(r.SignedOffset), 1e-3);
                    if (Math.Abs(offset) >= 2) {
                        signChecks++;
                        if (Math.Sign(r.SignedOffset) == Math.Sign(offset)) {
                            signCorrect++;
                        }
                    }
                    double error = r.SignedOffset - offset;
                    errors.Add(Math.Abs(error));
                    TestContext.Out.WriteLine($"NINA   angle {angle,4} offset {offset,3}: measured {r.SignedOffset,7:F3} (error {error,6:F3}), central {r.Lines[1].AngleDegrees:F1} deg");
                }
            }
            double within1 = errors.Count(e => e <= 1.0) / (double)errors.Count;
            TestContext.Out.WriteLine($"NINA: within 1 px {within1:P0}, sign correct {signCorrect}/{signChecks}, mean |error| {errors.Average():F3} px, max {errors.Max():F3} px");
            within1.Should().BeGreaterThanOrEqualTo(0.85);
            (signCorrect / (double)signChecks).Should().BeGreaterThanOrEqualTo(0.9);
        }

        private static readonly double[] RobustAngles = { 0, 10, 30, 62, 85, 90, 120, 150, 170 };

        /// <summary>The robust variant: every case, including spikes near horizontal and vertical.</summary>
        [Test]
        public void Robust_KnownOffsets_SignAndMagnitude() {
            var errors = new List<double>();
            foreach (var angle in RobustAngles) {
                var measured = new List<double>();
                foreach (var offset in Offsets) {
                    var img = BahtinovPattern.Render(200, 200, 100.4, 99.6, angle, 17, offset, seed: (int)((angle * 10) + offset + 100));
                    var r = BahtinovAnalyzer.AnalyzeRobust(img);
                    r.Success.Should().BeTrue($"angle {angle} offset {offset}");
                    r.Lines.Should().HaveCount(3);
                    double centralError = Math.Abs(r.Lines[1].AngleDegrees - angle);
                    Math.Min(centralError, 180 - centralError).Should().BeLessThan(1.0, $"central spike, angle {angle}");
                    if (Math.Abs(offset) >= 2) {
                        Math.Sign(r.SignedOffset).Should().Be(Math.Sign(offset), $"angle {angle} offset {offset} measured {r.SignedOffset:F2}");
                    }
                    double error = r.SignedOffset - offset;
                    errors.Add(Math.Abs(error));
                    measured.Add(r.SignedOffset);
                    TestContext.Out.WriteLine($"Robust angle {angle,4} offset {offset,3}: measured {r.SignedOffset,7:F3} (error {error,6:F3})");
                }
                measured.Should().BeInAscendingOrder($"angle {angle}");
            }
            TestContext.Out.WriteLine($"Robust: mean |error| {errors.Average():F3} px, max {errors.Max():F3} px");
            errors.Should().OnlyContain(e => e <= 1.0);
        }

        private static BahtinovResult RenderAndAnalyzeRobust(int crop, double spikeSigma, double outerHalfAngle, double angle, double offset) {
            var img = BahtinovPattern.Render(crop, crop, (crop / 2.0) + 0.4, (crop / 2.0) - 0.4, angle, outerHalfAngle, offset, seed: (int)((angle * 10) + offset + 100), spikeSigma: spikeSigma);
            return BahtinovAnalyzer.AnalyzeRobust(img);
        }

        /// <summary>
        /// The true offset expressed along the result's <see cref="BahtinovResult.Normal"/>. The pattern's offset is
        /// along n = (-sin a, cos a). Without a previous result, a central spike measured across the 0/180 degree wrap
        /// gets the opposite normal, which is a convention flip, not an error.
        /// </summary>
        private static double ExpectedSignedOffset(double angle, double offset, BahtinovResult r) {
            double a = angle * Math.PI / 180;
            return offset * ((-Math.Sin(a) * r.Normal.X) + (Math.Cos(a) * r.Normal.Y));
        }

        /// <summary>
        /// Validated range of AnalyzeRobust beyond the pattern family above: spike sigma 1.5-3 px with 200 px crops and
        /// 3.5-4 px with 300 px crops, and outer half-angles 12-28 degrees. Every pattern must succeed within 1 px.
        /// </summary>
        [TestCase(200, 1.5, 17)]
        [TestCase(200, 3.0, 17)]
        [TestCase(300, 3.5, 17)]
        [TestCase(300, 4.0, 17)]
        [TestCase(300, 2.2, 12)]
        [TestCase(300, 2.2, 22)]
        [TestCase(300, 2.2, 28)]
        public void Robust_ValidatedRange_IsAccurate(int crop, double spikeSigma, double outerHalfAngle) {
            var results = (from angle in RobustAngles from offset in Offsets select (angle, offset)).AsParallel().AsOrdered()
                .Select(c => (c.angle, c.offset, r: RenderAndAnalyzeRobust(crop, spikeSigma, outerHalfAngle, c.angle, c.offset))).ToList();
            foreach (var (angle, offset, r) in results) {
                r.Success.Should().BeTrue($"angle {angle} offset {offset}");
                r.SignedOffset.Should().BeApproximately(ExpectedSignedOffset(angle, offset, r), 1.0, $"angle {angle} offset {offset}");
            }
            TestContext.Out.WriteLine($"crop {crop}, spike sigma {spikeSigma}, half-angle {outerHalfAngle}: max |error| {results.Max(x => Math.Abs(x.r.SignedOffset - ExpectedSignedOffset(x.angle, x.offset, x.r))):F3} px over {results.Count} patterns");
        }

        /// <summary>
        /// Outside the validated range (spikes as wide as 3.5-5 px sigma, i.e. bin 1 in 2-3" seeing on this rig, and
        /// 150-300 px crops), AnalyzeRobust loses spike edges. It must then report Success = false rather than a
        /// confident wrong offset: every result it does accept has the right sign and is within 1.5 px.
        /// </summary>
        [Test]
        public void Robust_OutsideTheValidatedRange_FailsRatherThanReportingAWrongOffset() {
            var cases = (from crop in new[] { 150, 200, 300 }
                         from sigma in new[] { 3.5, 4.0, 5.0 }
                         from half in new[] { 12.0, 17.0 }
                         from angle in new double[] { 0, 30, 62, 90, 120, 170 }
                         from offset in new double[] { -8, -4, -2, 2, 4, 8 }
                         select (crop, sigma, half, angle, offset)).ToList();
            var results = cases.AsParallel().AsOrdered().Select(c => (c, r: RenderAndAnalyzeRobust(c.crop, c.sigma, c.half, c.angle, c.offset))).ToList();
            var accepted = results.Where(x => x.r.Success).ToList();
            double worst = accepted.Select(x => Math.Abs(x.r.SignedOffset - ExpectedSignedOffset(x.c.angle, x.c.offset, x.r))).DefaultIfEmpty(0).Max();
            TestContext.Out.WriteLine($"{results.Count} patterns: {accepted.Count} accepted (max |error| {worst:F3} px), {results.Count - accepted.Count} refused");

            foreach (var (c, r) in accepted) {
                double expected = ExpectedSignedOffset(c.angle, c.offset, r);
                Math.Sign(r.SignedOffset).Should().Be(Math.Sign(expected), $"{c}: measured {r.SignedOffset:F2}");
                r.SignedOffset.Should().BeApproximately(expected, 1.5, $"{c}");
            }
            accepted.Should().NotBeEmpty();
            accepted.Count.Should().BeLessThan(results.Count, "wide spikes in small crops must be refused");
        }

        [Test]
        public void Robust_AgreesWithNina_WhenUpstreamSucceeds() {
            int compared = 0;
            foreach (var angle in Angles) {
                foreach (var offset in Offsets) {
                    var img = BahtinovPattern.Render(200, 200, 100.4, 99.6, angle, 17, offset, seed: (int)((angle * 10) + offset + 100));
                    var nina = BahtinovAnalyzer.Analyze(img);
                    var robust = BahtinovAnalyzer.AnalyzeRobust(img);
                    if (Math.Abs(nina.SignedOffset - offset) <= 1.0) {
                        compared++;
                        robust.SignedOffset.Should().BeApproximately(nina.SignedOffset, 0.6, $"angle {angle} offset {offset}");
                    }
                }
            }
            compared.Should().BeGreaterThan(25);
        }

        [Test]
        public void SignStaysContinuous_WhenPreviousResultIsPassed() {
            var first = Analyze(62, 5);
            first.Success.Should().BeTrue();
            var second = BahtinovAnalyzer.Analyze(BahtinovPattern.Render(200, 200, 100.4, 99.6, 62.4, 17, -3, seed: 9), first);
            second.Success.Should().BeTrue();
            ((first.Normal.X * second.Normal.X) + (first.Normal.Y * second.Normal.Y)).Should().BeGreaterThan(0);
            second.SignedOffset.Should().BeNegative();
        }

        [Test]
        public void OutputGeometry_IsConsistent() {
            var r = Analyze(120, 6);
            var focus = r.CenterPoint.Value;
            var outer = r.OuterIntersection.Value;
            // the foot point lies on the central spike and the outer intersection is near the star
            var central = r.Lines[1];
            (central.Slope * focus.X + central.Intercept).Should().BeApproximately(focus.Y, 0.05f);
            outer.DistanceTo(new AccordPoint(100.4f, 99.6f)).Should().BeLessThan(2.5f);
            // upstream's red marker sits 4x the distance from the outer intersection
            outer.DistanceTo(r.ExaggeratedErrorPoint.Value).Should().BeApproximately((float)(4 * r.Distance), 0.05f);
        }

        [Test]
        public void ThroughTheFramePipeline_On16BitLinearData() {
            // a linear frame: the pattern scaled into ADU over a sky background, then NINA's render + auto-stretch + crop
            var pattern = BahtinovPattern.Render(400, 300, 200.4, 149.6, 62, 17, 5, seed: 3, background: 0, noise: 0);
            var rng = new Random(4);
            var data = new ushort[400 * 300];
            for (int i = 0; i < data.Length; i++) {
                double v = 1200 + (pattern.Pixels[i] * 120) + (Math.Sqrt(1200 + (pattern.Pixels[i] * 120)) * Gaussian(rng));
                data[i] = (ushort)Math.Max(0, Math.Min(65535, v));
            }
            var analysis = FrameAnalyzer.Analyze(new PixelBuffer(data, 400, 300), new FrameAnalysisOptions { DetectStars = false });
            var r = FrameAnalyzer.AnalyzeBahtinov(analysis, new PixelRect(100, 50, 200, 200));
            r.Success.Should().BeTrue();
            TestContext.Out.WriteLine($"pipeline Bahtinov offset {r.SignedOffset:F3} px (expected 5)");
            r.SignedOffset.Should().BeApproximately(5, 1.0);
        }

        [Test]
        public void EmptyImage_Fails_WithZeroDistance_LikeUpstream() {
            var img = new Gray8Image(200, 200);
            Array.Fill(img.Pixels, (byte)30);
            var r = BahtinovAnalyzer.Analyze(img);
            r.Success.Should().BeFalse();
            r.Distance.Should().Be(0);
            double.IsNaN(r.SignedOffset).Should().BeTrue();
        }

        [Test]
        public void PatternStraddlingHorizontal_IsMispaired_LikeUpstream() {
            // Characterisation: upstream orders the six Hough lines by 1/slope, which wraps at horizontal. With the
            // central spike at 10 degrees the -7 degree outer spike sorts to the wrong end and the pairing breaks, so
            // the offset is wrong. Rotate the mask so no spike is within ~20 degrees of horizontal.
            var r = Analyze(10, 4);
            TestContext.Out.WriteLine($"central 10 deg, offset 4: measured {r.SignedOffset:F2}, line angles {string.Join(", ", r.Lines.Select(l => l.AngleDegrees.ToString("F1")))}");
            Math.Abs(r.SignedOffset - 4).Should().BeGreaterThan(2);
        }

        private static double Gaussian(Random rng) {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
