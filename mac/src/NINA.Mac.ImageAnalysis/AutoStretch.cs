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
using System.Threading.Tasks;

namespace NINA.Mac.ImageAnalysis {

    /// <summary>
    /// NINA's screen-transfer-function auto stretch. Port of NINA.Image/ImageAnalysis/ImageUtility.cs:75-133
    /// (MidtonesTransferFunction, NormalizeUShort, DenormalizeUShort, GetStretchMap) and of the remapping done by
    /// ColorRemappingGeneral.cs:101-130. Star detection runs on the stretched image (ImageControlVM.cs:667-686
    /// forces the stretch whenever stars are detected), so this is part of the detection front end.
    /// </summary>
    public static class AutoStretch {

        /// <summary>Profile default ImageSettings.AutoStretchFactor (NINA.Profile/ImageSettings.cs:34).</summary>
        public const double DefaultFactor = 0.2;

        /// <summary>Profile default ImageSettings.BlackClipping (NINA.Profile/ImageSettings.cs:35).</summary>
        public const double DefaultBlackClipping = -2.8;

        public static double MidtonesTransferFunction(double midToneBalance, double x) {
            if (x > 0) {
                if (x < 1) {
                    return (midToneBalance - 1) * x / ((2 * midToneBalance - 1) * x - midToneBalance);
                }
                return 1;
            }
            return 0;
        }

        public static double NormalizeUShort(double val, int bitDepth) {
            return val / (double)((1 << bitDepth) - 1);
        }

        public static ushort DenormalizeUShort(double val) {
            return (ushort)(val * ushort.MaxValue + (val < 0.5 ? 0.5 : 0.0));
        }

        /// <summary>65536-entry lookup table (ImageUtility.GetStretchMap).</summary>
        public static ushort[] GetStretchMap(double median, double medianAbsoluteDeviation, int bitDepth, double targetHistogramMedianPercent, double shadowsClipping) {
            ushort[] map = new ushort[ushort.MaxValue + 1];

            var normalizedMedian = NormalizeUShort(median, bitDepth);
            var normalizedMAD = NormalizeUShort(medianAbsoluteDeviation, bitDepth);

            var scaleFactor = 1.4826; // see https://en.wikipedia.org/wiki/Median_absolute_deviation

            double shadows = 0d;
            double midtones = 0.5d;
            double highlights = 1d;

            //Assume the image is inverted or overexposed when median is higher than half of the possible value
            if (normalizedMedian > 0.5) {
                shadows = 0.0d;
                highlights = normalizedMedian - shadowsClipping * normalizedMAD * scaleFactor;
                midtones = MidtonesTransferFunction(targetHistogramMedianPercent, 1.0 - (highlights - normalizedMedian));
            } else {
                shadows = normalizedMedian + shadowsClipping * normalizedMAD * scaleFactor;
                midtones = MidtonesTransferFunction(targetHistogramMedianPercent, normalizedMedian - shadows);
                highlights = 1;
            }

            for (int i = 0; i < map.Length; i++) {
                double value = NormalizeUShort(i, bitDepth);

                map[i] = DenormalizeUShort(MidtonesTransferFunction(midtones, 1 - highlights + value - shadows));
            }

            return map;
        }

        public static ushort[] GetStretchMap(ImageStatistics statistics, double factor = DefaultFactor, double blackClipping = DefaultBlackClipping) {
            return GetStretchMap(statistics.Median, statistics.MedianAbsoluteDeviation, statistics.BitDepth, factor, blackClipping);
        }

        /// <summary>Applies a map to a 16 bit plane, returning a new plane.</summary>
        public static ushort[] Apply(ushort[] data, ushort[] map) {
            var result = new ushort[data.Length];
            Parallel.For(0, (data.Length + 65535) / 65536, block => {
                int start = block * 65536;
                int end = Math.Min(data.Length, start + 65536);
                for (int i = start; i < end; i++) {
                    result[i] = map[data[i]];
                }
            });
            return result;
        }

        /// <summary>
        /// Applies per-slot maps to interleaved RGB data (three ushorts per pixel, slot 0/1/2 = R/G/B as produced by
        /// <see cref="Debayer"/>), returning a new buffer.
        /// </summary>
        public static ushort[] ApplyRgb(ushort[] interleaved, ushort[] mapR, ushort[] mapG, ushort[] mapB) {
            var result = new ushort[interleaved.Length];
            int pixels = interleaved.Length / 3;
            Parallel.For(0, (pixels + 65535) / 65536, block => {
                int start = block * 65536;
                int end = Math.Min(pixels, start + 65536);
                for (int i = start; i < end; i++) {
                    int s = i * 3;
                    result[s] = mapR[interleaved[s]];
                    result[s + 1] = mapG[interleaved[s + 1]];
                    result[s + 2] = mapB[interleaved[s + 2]];
                }
            });
            return result;
        }
    }
}
