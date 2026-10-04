#region "copyright"

/*
    Part of the LGPL Accord.NET / AForge.NET port used by the N.I.N.A. macOS image-analysis library.

    Copyright © Andrew Kirillov (AForge.NET framework), 2005-2009
    Copyright © César Souza (Accord.NET Framework), 2009-2017
    Managed port for the N.I.N.A. macOS fork, 2026.

    This library is free software; you can redistribute it and/or modify it under the terms of the GNU Lesser
    General Public License as published by the Free Software Foundation; either version 2.1 of the License, or
    (at your option) any later version.

    This library is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the
    implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public
    License for more details: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
*/

#endregion "copyright"

using System;

namespace NINA.Mac.ImageAnalysis.AccordPort {

    /// <summary>
    /// Port of Accord.Imaging.Filters.SISThreshold (Accord.Imaging/AForge.Imaging/Filters/Adaptive Binarization/
    /// SISThreshold.cs) with its Threshold filter (Filters/Binarization/Threshold.cs, 8 bpp: value &gt;= threshold
    /// -&gt; 255, else 0).
    /// </summary>
    public static class SisThreshold {

        /// <summary>Simple Image Statistics threshold over the interior (1 px frame excluded).</summary>
        public static int CalculateThreshold(Gray8Image image) {
            int width = image.Width;
            int height = image.Height;
            byte[] px = image.Pixels;
            double weightTotal = 0, total = 0;

            for (int y = 1; y < height - 1; y++) {
                int row = y * width;
                for (int x = 1; x < width - 1; x++) {
                    int i = row + x;
                    double ex = Math.Abs(px[i + 1] - px[i - 1]);
                    double ey = Math.Abs(px[i + width] - px[i - width]);
                    double weight = (ex > ey) ? ex : ey;
                    weightTotal += weight;
                    total += weight * px[i];
                }
            }
            return (weightTotal == 0) ? (byte)0 : (byte)(total / weightTotal);
        }

        /// <summary>Calculates the threshold and binarises the whole image in place; returns the threshold.</summary>
        public static int ApplyInPlace(Gray8Image image) {
            int threshold = CalculateThreshold(image);
            ThresholdInPlace(image, threshold);
            return threshold;
        }

        public static void ThresholdInPlace(Gray8Image image, int threshold) {
            byte[] px = image.Pixels;
            for (int i = 0; i < px.Length; i++) {
                px[i] = (byte)((px[i] >= threshold) ? 255 : 0);
            }
        }
    }

    /// <summary>
    /// Port of Accord.Imaging.Filters.BinaryDilation3x3 (Filters/Morphology/Specific Optimizations/
    /// BinaryDilation3x3.cs): each output pixel is the OR of the in-bounds 3x3 neighbourhood of the input.
    /// </summary>
    public static class BinaryDilation3x3 {

        public static void ApplyInPlace(Gray8Image image) {
            int width = image.Width;
            int height = image.Height;
            if ((width < 3) || (height < 3)) {
                throw new InvalidOperationException("Processing rectangle mast be at least 3x3 in size.");
            }
            byte[] dst = image.Pixels;
            byte[] src = (byte[])dst.Clone();

            for (int y = 0; y < height; y++) {
                int y0 = Math.Max(0, y - 1);
                int y1 = Math.Min(height - 1, y + 1);
                for (int x = 0; x < width; x++) {
                    int x0 = Math.Max(0, x - 1);
                    int x1 = Math.Min(width - 1, x + 1);
                    int v = 0;
                    for (int yy = y0; yy <= y1; yy++) {
                        int row = yy * width;
                        for (int xx = x0; xx <= x1; xx++) {
                            v |= src[row + xx];
                        }
                    }
                    dst[(y * width) + x] = (byte)v;
                }
            }
        }
    }
}
