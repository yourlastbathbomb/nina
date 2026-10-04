#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;

namespace NINA.Mac.ImageAnalysis {

    /// <summary>
    /// Port of NINA.Image/ImageData/ImageStatistics.cs:46-170 (ImageStatistics.Create), without the OxyPlot/INPC
    /// plumbing. Definitions are NINA's:
    /// <list type="bullet">
    /// <item>Mean and StDev are population values (divide by N) from integer sums.</item>
    /// <item>Median comes from the 16 bit histogram; for an even count with the halfway point between two occupied
    /// values it is their average.</item>
    /// <item>MedianAbsoluteDeviation is NINA's histogram walk: it steps outwards from the median one ADU at a time on
    /// both sides simultaneously and returns the distance at which more than half of the pixels are enclosed. This
    /// is the MAD for symmetric data but can differ from the textbook MAD for skewed data; it is ported as is
    /// because the auto-stretch (and therefore star detection) depends on it.</item>
    /// <item>The histogram has 101 bins (HISTOGRAMRESOLUTION = 100) over [0, 2^BitDepth - 1].</item>
    /// </list>
    /// </summary>
    public sealed class ImageStatistics {
        public const int HISTOGRAMRESOLUTION = 100;

        private ImageStatistics() {
        }

        public int BitDepth { get; private set; }
        public double StDev { get; private set; }
        public double Mean { get; private set; }
        public double Median { get; private set; }
        public double MedianAbsoluteDeviation { get; private set; }
        public int Max { get; private set; }
        public long MaxOccurrences { get; private set; }
        public int Min { get; private set; }
        public long MinOccurrences { get; private set; }

        /// <summary>(bin, count) pairs in ascending bin order, as NINA feeds its histogram chart.</summary>
        public IReadOnlyList<(double X, long Count)> Histogram { get; private set; }

        public static ImageStatistics Create(PixelBuffer buffer) {
            return Create(buffer.Data, buffer.BitDepth);
        }

        public static ImageStatistics Create(ushort[] array, int bitDepth) {
            if (array == null) { throw new ArgumentNullException(nameof(array)); }
            if (array.Length == 0) { throw new ArgumentException("Empty array", nameof(array)); }

            long sum = 0;
            long squareSum = 0;
            int count = array.Length;
            ushort min = ushort.MaxValue;
            ushort oldmin = min;
            ushort max = 0;
            ushort oldmax = max;
            long maxOccurrences = 0;
            long minOccurrences = 0;

            /* Array mapping: pixel value -> total number of occurrences of that pixel value */
            int[] pixelValueCounts = new int[ushort.MaxValue + 1];
            for (var i = 0; i < array.Length; i++) {
                ushort val = array[i];

                sum += val;
                squareSum += (long)val * val;

                pixelValueCounts[val]++;

                min = Math.Min(min, val);
                if (min != oldmin) {
                    minOccurrences = 0;
                }
                if (val == min) {
                    minOccurrences += 1;
                }

                max = Math.Max(max, val);
                if (max != oldmax) {
                    maxOccurrences = 0;
                }
                if (val == max) {
                    maxOccurrences += 1;
                }

                oldmin = min;
                oldmax = max;
            }

            double mean = sum / (double)count;
            double variance = (squareSum - (count * mean * mean)) / count;
            double stdev = Math.Sqrt(variance);

            var occurrences = 0;
            double median = 0d;
            int median1 = 0, median2 = 0;
            var medianlength = array.Length / 2.0;

            /* Determine median out of histogram array (note: upstream stops before 65535) */
            for (ushort i = 0; i < ushort.MaxValue; i++) {
                occurrences += pixelValueCounts[i];
                if (occurrences > medianlength) {
                    median1 = i;
                    median2 = i;
                    break;
                } else if (occurrences == medianlength) {
                    median1 = i;
                    for (int j = i + 1; j <= ushort.MaxValue; j++) {
                        if (pixelValueCounts[j] > 0) {
                            median2 = j;
                            break;
                        }
                    }
                    break;
                }
            }
            median = (median1 + median2) / 2.0;

            /* Determine median absolute deviation by walking up and down from the median (NINA's algorithm) */
            var medianAbsoluteDeviation = 0.0d;
            occurrences = 0;
            var idxDown = median1;
            var idxUp = median2;
            while (true) {
                if (idxDown >= 0 && idxDown != idxUp) {
                    occurrences += pixelValueCounts[idxDown] + pixelValueCounts[idxUp];
                } else {
                    occurrences += pixelValueCounts[idxUp];
                }

                if (occurrences > medianlength) {
                    medianAbsoluteDeviation = Math.Abs(idxUp - median);
                    break;
                }

                idxUp++;
                idxDown--;
                if (idxUp > ushort.MaxValue) {
                    break;
                }
            }

            var maxPossibleValue = (ushort)((1 << bitDepth) - 1);
            var factor = (double)HISTOGRAMRESOLUTION / maxPossibleValue;
            var bins = new SortedDictionary<double, long>();
            for (int index = 0; index < pixelValueCounts.Length; index++) {
                var key = Math.Floor((double)Math.Min(maxPossibleValue, index) * factor);
                bins.TryGetValue(key, out var binCount);
                bins[key] = binCount + pixelValueCounts[index];
            }
            var histogram = new List<(double, long)>(bins.Count);
            foreach (var kv in bins) {
                histogram.Add((kv.Key, kv.Value));
            }

            return new ImageStatistics {
                BitDepth = bitDepth,
                StDev = stdev,
                Mean = mean,
                Median = median,
                MedianAbsoluteDeviation = medianAbsoluteDeviation,
                Max = max,
                MaxOccurrences = maxOccurrences,
                Min = min,
                MinOccurrences = minOccurrences,
                Histogram = histogram
            };
        }
    }
}
