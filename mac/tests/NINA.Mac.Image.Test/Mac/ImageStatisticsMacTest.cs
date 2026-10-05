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
using NINA.Image.ImageData;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// NINA.Image's ImageStatistics on the rig frame and on noisy frames, against a reference computed here by sorting
    /// (no histogram walk). The upstream ImageStatisticsTest fixture runs as well (linked under Upstream/).
    /// </summary>
    [TestFixture]
    public class ImageStatisticsMacTest {

        [Test]
        public async Task RigMosaic_StatisticsThroughTheCapturePath() {
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(), RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());

            var statistics = await image.Statistics;

            const int n = RigFrame.Width * RigFrame.Height;
            statistics.BitDepth.Should().Be(16);
            statistics.Mean.Should().Be(15500, "(40000 + 2 x 10000 + 2000) / 4");
            statistics.Median.Should().Be(10000);
            statistics.StDev.Should().BeApproximately(Math.Sqrt(210_750_000.0), 1e-6, "E[x^2] 451e6 minus mean^2 240.25e6");
            statistics.Min.Should().Be(2000);
            statistics.MinOccurrences.Should().Be(n / 4);
            statistics.Max.Should().Be(40000);
            statistics.MaxOccurrences.Should().Be(n / 4);
            // NINA's MAD: the smallest d with more than half the pixels within d of the median. Half the pixels sit at the
            // median exactly, which is not more than half, so d grows to the blue pixels: |2000 - 10000|
            statistics.MedianAbsoluteDeviation.Should().Be(8000);
            // HISTOGRAMRESOLUTION 100 buckets over 0..65535: value v lands in floor(v * 100 / 65535)
            statistics.Histogram.Where(p => p.Y > 0).Select(p => (p.X, p.Y)).Should().Equal(
                (3.0, n / 4.0), (15.0, n / 2.0), (61.0, n / 4.0));
            statistics.Histogram.Sum(p => p.Y).Should().Be(n);
        }

        [TestCase(1, 1920, 1080, 340, 25)]
        [TestCase(2, 1920, 1080, 2000, 600)]
        [TestCase(3, 37, 23, 30000, 20000)]
        [TestCase(4, 3840, 2160, 340, 25)]
        public void NoisyFrame_MatchesASortingReference(int seed, int width, int height, int level, int sigma) {
            var random = new Random(seed);
            var pixels = new ushort[width * height];
            for (var i = 0; i < pixels.Length; i++) {
                // Box-Muller normal noise around the level, clipped to 16 bits, plus a few saturated and zero pixels
                var u1 = 1.0 - random.NextDouble();
                var u2 = random.NextDouble();
                var value = level + sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                pixels[i] = (ushort)Math.Clamp(Math.Round(value), 0, 65535);
            }
            pixels[0] = 65535;
            pixels[^1] = 0;

            var statistics = ImageStatistics.Create(new ImageProperties(width, height, 16, false, 0, 0), pixels);
            var reference = Reference(pixels);

            statistics.Mean.Should().BeApproximately(reference.Mean, 1e-9);
            statistics.StDev.Should().BeApproximately(reference.StDev, 1e-6 * Math.Max(1, reference.StDev));
            statistics.Median.Should().Be(reference.Median);
            statistics.MedianAbsoluteDeviation.Should().Be(reference.Mad);
            statistics.Min.Should().Be(reference.Min);
            statistics.MinOccurrences.Should().Be(reference.MinCount);
            statistics.Max.Should().Be(reference.Max);
            statistics.MaxOccurrences.Should().Be(reference.MaxCount);
        }

        /// <summary>
        /// Statistics by sorting. Median: the middle value, or the mean of the two middle values for an even count. MAD as
        /// NINA defines it for an integer median: the smallest d for which more than half the pixels lie within d of the
        /// median, i.e. element n/2 (0-based) of the sorted absolute deviations. For a half-integer median (two different
        /// middle values) NINA walks outward from both middle values; that case gives |upper middle value + k - median|.
        /// </summary>
        private static (double Mean, double StDev, double Median, double Mad, int Min, long MinCount, int Max, long MaxCount) Reference(ushort[] pixels) {
            var n = pixels.Length;
            var sorted = pixels.Select(p => (int)p).OrderBy(p => p).ToArray();
            var mean = sorted.Sum(p => (double)p) / n;
            var stdev = Math.Sqrt(sorted.Sum(p => (p - mean) * (p - mean)) / n);
            double median = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
            double mad;
            if (median == Math.Floor(median)) {
                var deviations = sorted.Select(p => Math.Abs(p - median)).OrderBy(d => d).ToArray();
                mad = deviations[n / 2];
            } else {
                // Walk as NINA does: start at the two middle values, widen by one each step on both sides
                var low = sorted[n / 2 - 1];
                var high = sorted[n / 2];
                var k = 0;
                while (true) {
                    var lo = low - k;
                    var hi = high + k;
                    var count = sorted.Count(p => p >= lo && p <= hi);
                    if (count > n / 2.0) {
                        mad = Math.Abs(hi - median);
                        break;
                    }
                    k++;
                }
            }
            return (mean, stdev, median, mad, sorted[0], sorted.Count(p => p == sorted[0]), sorted[^1], sorted.Count(p => p == sorted[^1]));
        }
    }
}
