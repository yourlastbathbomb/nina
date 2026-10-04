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
using NUnit.Framework;
using System;
using System.Linq;

namespace NINA.Mac.ImageAnalysis.Test {

    [TestFixture]
    public class ImageStatisticsTests {

        private static ushort[] RandomData(int n, int seed, int min, int max) {
            var rng = new Random(seed);
            var data = new ushort[n];
            for (int i = 0; i < n; i++) {
                data[i] = (ushort)rng.Next(min, max + 1);
            }
            return data;
        }

        [TestCase(10001, 1)]
        [TestCase(4097, 2)]
        [TestCase(1, 3)]
        public void MeanStdevMinMaxMedianMad_MatchTextbookDefinitions_ForOddCounts(int n, int seed) {
            var data = RandomData(n, seed, 900, 1500);
            data[0] = 17;
            var stats = ImageStatistics.Create(data, 16);

            double mean = data.Average(v => (double)v);
            double stdev = Math.Sqrt(data.Sum(v => (v - mean) * (v - mean)) / n); // population (NINA divides by N)
            var sorted = data.OrderBy(v => v).ToArray();
            double median = sorted[n / 2];
            var deviations = data.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToArray();
            double mad = deviations[n / 2];

            stats.Mean.Should().BeApproximately(mean, 1e-9);
            stats.StDev.Should().BeApproximately(stdev, 1e-6);
            stats.Min.Should().Be(sorted[0]);
            stats.Max.Should().Be(sorted[n - 1]);
            stats.MinOccurrences.Should().Be(data.Count(v => v == sorted[0]));
            stats.MaxOccurrences.Should().Be(data.Count(v => v == sorted[n - 1]));
            stats.Median.Should().Be(median);
            stats.MedianAbsoluteDeviation.Should().Be(mad);
        }

        [Test]
        public void Median_EvenCount_IsMeanOfMiddleValues() {
            var data = new ushort[] { 10, 20, 30, 40, 1000, 7 };
            ImageStatistics.Create(data, 16).Median.Should().Be((20 + 30) / 2.0);
        }

        [Test]
        public void Mad_EvenCount_IsNinaUpperMedianOfDeviations() {
            // NINA walks outwards from the median until more than half the pixels are enclosed: for an even count that
            // is the upper of the two middle deviations, never less than the textbook MAD.
            var data = RandomData(10000, 7, 1000, 1200);
            var stats = ImageStatistics.Create(data, 16);
            var deviations = data.Select(v => Math.Abs(v - stats.Median)).OrderBy(v => v).ToArray();
            double textbook = (deviations[4999] + deviations[5000]) / 2;
            stats.MedianAbsoluteDeviation.Should().BeGreaterThanOrEqualTo(textbook);
            stats.MedianAbsoluteDeviation.Should().BeLessThanOrEqualTo(deviations[5000] + 0.5);
        }

        [Test]
        public void Mad_OfGaussianNoise_ScalesToSigma() {
            var sky = new SkyFrame(512, 512) { Background = 1000, ReadNoise = 25, Gain = 0, Seed = 3 };
            var stats = ImageStatistics.Create(sky.Render());
            (stats.MedianAbsoluteDeviation * 1.4826).Should().BeApproximately(25, 1.0);
            stats.StDev.Should().BeApproximately(25, 0.5);
            stats.Median.Should().BeApproximately(1000, 1);
        }

        [Test]
        public void Histogram_Has101BinsCoveringAllPixels() {
            var data = RandomData(50000, 9, 0, 65535);
            var stats = ImageStatistics.Create(data, 16);
            stats.Histogram.Should().HaveCount(ImageStatistics.HISTOGRAMRESOLUTION + 1);
            stats.Histogram.Sum(h => h.Count).Should().Be(data.Length);
            stats.Histogram.Select(h => h.X).Should().BeInAscendingOrder();
        }

        [Test]
        public void Histogram_LowerBitDepth_ClampsAboveMaximum() {
            var data = new ushort[] { 0, 2047, 4095, 4095, 60000 };
            var stats = ImageStatistics.Create(data, 12);
            stats.Histogram.Last().X.Should().Be(100);
            stats.Histogram.Last().Count.Should().Be(3, "4095, 4095 and the clamped 60000 land in the last bin");
            stats.Histogram.Sum(h => h.Count).Should().Be(5);
        }
    }

    [TestFixture]
    public class AutoStretchTests {

        [Test]
        public void Mtf_MapsBalancePointToHalf() {
            foreach (var m in new[] { 0.05, 0.2, 0.5, 0.8 }) {
                AutoStretch.MidtonesTransferFunction(m, m).Should().BeApproximately(0.5, 1e-12);
            }
            AutoStretch.MidtonesTransferFunction(0.3, 0).Should().Be(0);
            AutoStretch.MidtonesTransferFunction(0.3, 1).Should().Be(1);
            AutoStretch.MidtonesTransferFunction(0.3, -0.1).Should().Be(0);
        }

        [Test]
        public void StretchMap_IsMonotonic_AndMapsMedianNearTarget() {
            var sky = new SkyFrame(400, 300) { Background = 1200, ReadNoise = 20, Gain = 1, Seed = 2 };
            var stats = ImageStatistics.Create(sky.Render());
            var map = AutoStretch.GetStretchMap(stats);
            for (int i = 1; i < map.Length; i++) {
                map[i].Should().BeGreaterThanOrEqualTo(map[i - 1]);
            }
            // shadows = median - 2.8 * 1.4826 * MAD, and the midtones balance is chosen to send the median to 20 %
            (map[(int)stats.Median] / 65535.0).Should().BeApproximately(AutoStretch.DefaultFactor, 0.01);
            map[0].Should().Be(0);
            // highlights stay at 1, so full scale maps to MTF(m, 1 - shadows), just below white
            double shadows = (stats.Median / 65535.0) + (AutoStretch.DefaultBlackClipping * (stats.MedianAbsoluteDeviation / 65535.0) * 1.4826);
            double midtones = AutoStretch.MidtonesTransferFunction(AutoStretch.DefaultFactor, (stats.Median / 65535.0) - shadows);
            map[65535].Should().Be(AutoStretch.DenormalizeUShort(AutoStretch.MidtonesTransferFunction(midtones, 1 - shadows)));
            map[65535].Should().BeGreaterThan(65000);
        }

        [Test]
        public void StretchMap_InvertedImageBranch_KeepsMonotonic() {
            var data = Enumerable.Range(0, 10001).Select(i => (ushort)(50000 + (i % 200))).ToArray();
            var stats = ImageStatistics.Create(data, 16);
            var map = AutoStretch.GetStretchMap(stats);
            for (int i = 1; i < map.Length; i++) {
                map[i].Should().BeGreaterThanOrEqualTo(map[i - 1]);
            }
        }

        [Test]
        public void DenormalizeUShort_RoundsLowHalfOnly() {
            AutoStretch.DenormalizeUShort(0).Should().Be(0);
            AutoStretch.DenormalizeUShort(1).Should().Be(65535);
            AutoStretch.DenormalizeUShort(0.25).Should().Be((ushort)((0.25 * 65535) + 0.5));
            AutoStretch.DenormalizeUShort(0.75).Should().Be((ushort)(0.75 * 65535));
        }
    }

    [TestFixture]
    public class DebayerTests {

        [TestCase(BayerPattern.RGGB)]
        [TestCase(BayerPattern.BGGR)]
        [TestCase(BayerPattern.GRBG)]
        [TestCase(BayerPattern.GBRG)]
        public void FlatColourMosaic_ReconstructsTheColourEverywhere(BayerPattern pattern) {
            const ushort R = 1000, G = 2000, B = 3000;
            int w = 31, h = 20;
            var raw = new ushort[w * h];
            string name = pattern.ToString();
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    char c = name[((y & 1) * 2) + (x & 1)];
                    raw[(y * w) + x] = c == 'R' ? R : c == 'G' ? G : B;
                }
            }
            var d = Debayer.Apply(new PixelBuffer(raw, w, h, 16, pattern), saveColorChannels: true, saveLumChannel: true);
            d.Red.Should().OnlyContain(v => v == R);
            d.Green.Should().OnlyContain(v => v == G);
            d.Blue.Should().OnlyContain(v => v == B);
            d.Lum.Should().OnlyContain(v => v == 2000);
            d.Rgb.Where((v, i) => i % 3 == 0).Should().OnlyContain(v => v == R, "slot 0 is red (WPF Rgb48 order)");
        }

        [Test]
        public void OddOffsets_ShiftThePattern() {
            BayerPatternUtility.ApplyOffsets(BayerPattern.RGGB, 1, 0).Should().Be(BayerPattern.GRBG);
            BayerPatternUtility.ApplyOffsets(BayerPattern.RGGB, 0, 1).Should().Be(BayerPattern.GBRG);
            BayerPatternUtility.ApplyOffsets(BayerPattern.RGGB, 1, 1).Should().Be(BayerPattern.BGGR);
            BayerPatternUtility.ApplyOffsets(BayerPattern.RGGB, 2, 4).Should().Be(BayerPattern.RGGB);
            BayerPatternUtility.Parse("RGGB    ").Should().Be(BayerPattern.RGGB);
            BayerPatternUtility.Parse("").Should().Be(BayerPattern.None);
        }

        [Test]
        public void CropAndFlip_KeepTheColourOfEachPixel() {
            var raw = Enumerable.Range(0, 8 * 6).Select(i => (ushort)i).ToArray();
            var buffer = new PixelBuffer(raw, 8, 6, 16, BayerPattern.RGGB);
            var crop = buffer.Crop(3, 1, 4, 4);
            crop.BayerPattern.Should().Be(BayerPattern.BGGR);
            crop[0, 0].Should().Be(buffer[3, 1]);
            var flipped = buffer.FlipVertical();
            flipped[2, 0].Should().Be(buffer[2, 5]);
            flipped.BayerPattern.Should().Be(BayerPattern.GBRG, "row 5 of an RGGB sensor starts with G B");
        }
    }
}
