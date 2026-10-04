#region "copyright"

/*
    Part of the LGPL Accord.NET / AForge.NET port used by the N.I.N.A. macOS image-analysis library.

    Copyright © Andrew Kirillov (AForge.NET framework), 2005-2011
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
using System.Threading.Tasks;

namespace NINA.Mac.ImageAnalysis.AccordPort {

    /// <summary>
    /// Port of Accord.Imaging.Filters.ResizeBicubic, 8 bpp path (Filters/Transform/ResizeBicubic.cs) with
    /// Accord.Imaging.Interpolation.BiCubicKernel (AForge.Imaging/Interpolation.cs).
    /// </summary>
    public static class ResizeBicubic {

        public static double BiCubicKernel(double x) {
            if (x < 0) {
                x = -x;
            }
            double biCoef = 0;
            if (x <= 1) {
                biCoef = (((1.5 * x) - 2.5) * x * x) + 1;
            } else if (x < 2) {
                biCoef = ((((-0.5 * x) + 2.5) * x) - 4) * x + 2;
            }
            return biCoef;
        }

        /// <summary>
        /// Same arithmetic as Accord (same operands, same summation order), so the result is bit-identical; the kernel
        /// weights are only precomputed per row/column instead of being re-evaluated per pixel. Rows run in parallel.
        /// </summary>
        public static Gray8Image Apply(Gray8Image source, int newWidth, int newHeight) {
            if (newWidth <= 0 || newHeight <= 0) {
                throw new ArgumentOutOfRangeException(nameof(newWidth), "New size must be positive");
            }
            int width = source.Width;
            int height = source.Height;
            byte[] src = source.Pixels;
            var destination = new Gray8Image(newWidth, newHeight);
            byte[] dst = destination.Pixels;

            double xFactor = (double)width / newWidth;
            double yFactor = (double)height / newHeight;
            int ymax = height - 1;
            int xmax = width - 1;

            // per output column: ox1 and the four horizontal kernel weights BiCubicKernel(m - dx), m = -1..2
            var ox1s = new int[newWidth];
            var kx = new double[newWidth * 4];
            for (int x = 0; x < newWidth; x++) {
                // note: Accord subtracts the float literal 0.5f here (exactly 0.5 as a double)
                double ox = ((double)x * xFactor) - 0.5f;
                int ox1 = (int)ox;
                double dx = ox - (double)ox1;
                ox1s[x] = ox1;
                for (int m = -1; m < 3; m++) {
                    kx[(x * 4) + m + 1] = BiCubicKernel((double)m - dx);
                }
            }

            Parallel.For(0, newHeight, y => {
                double oy = ((double)y * yFactor) - 0.5;
                int oy1 = (int)oy;
                double dy = oy - (double)oy1;
                var k1s = new double[4];
                var rows = new int[4];
                for (int n = -1; n < 3; n++) {
                    k1s[n + 1] = BiCubicKernel(dy - (double)n);
                    int oy2 = oy1 + n;
                    if (oy2 < 0) {
                        oy2 = 0;
                    }
                    if (oy2 > ymax) {
                        oy2 = ymax;
                    }
                    rows[n + 1] = oy2 * width;
                }

                int dstRow = y * newWidth;
                for (int x = 0; x < newWidth; x++) {
                    int ox1 = ox1s[x];
                    int kxBase = x * 4;
                    double g = 0;
                    for (int n = 0; n < 4; n++) {
                        double k1 = k1s[n];
                        int row = rows[n];
                        for (int m = 0; m < 4; m++) {
                            double k2 = k1 * kx[kxBase + m];
                            int ox2 = ox1 + m - 1;
                            if (ox2 < 0) {
                                ox2 = 0;
                            }
                            if (ox2 > xmax) {
                                ox2 = xmax;
                            }
                            g += k2 * src[row + ox2];
                        }
                    }
                    dst[dstRow + x] = (byte)Math.Max(0, Math.Min(255, g));
                }
            });
            return destination;
        }
    }

    /// <summary>Port of Accord.Imaging.Filters.Median, 8 bpp path (Filters/Smooting/Median.cs), size 3 by default.</summary>
    public static class MedianFilter {

        public static Gray8Image Apply(Gray8Image source, int size = 3) {
            size = Math.Max(3, Math.Min(25, size | 1));
            int width = source.Width;
            int height = source.Height;
            byte[] src = source.Pixels;
            var destination = new Gray8Image(width, height);
            byte[] dst = destination.Pixels;
            int radius = size >> 1;

            Parallel.For(0, height, y => {
                var g = new byte[size * size];
                for (int x = 0; x < width; x++) {
                    int c = 0;
                    for (int i = -radius; i <= radius; i++) {
                        int t = y + i;
                        if (t < 0) {
                            continue;
                        }
                        if (t >= height) {
                            break;
                        }
                        for (int j = -radius; j <= radius; j++) {
                            t = x + j;
                            if (t < 0) {
                                continue;
                            }
                            if (t < width) {
                                g[c++] = src[((y + i) * width) + t];
                            }
                        }
                    }
                    Array.Sort(g, 0, c);
                    dst[(y * width) + x] = g[c >> 1];
                }
            });
            return destination;
        }
    }

    /// <summary>Pixel format conversions used by N.I.N.A.'s detection front end.</summary>
    public static class PixelConversions {

        /// <summary>
        /// Accord.Imaging.Image.Convert16bppTo8bpp (AForge.Imaging/Image.cs:502-536): keeps the high byte.
        /// </summary>
        public static Gray8Image Convert16To8(ushort[] gray16, int width, int height) {
            if (gray16.Length != width * height) {
                throw new ArgumentException("Buffer size does not match width * height", nameof(gray16));
            }
            var result = new Gray8Image(width, height);
            byte[] dst = result.Pixels;
            for (int i = 0; i < dst.Length; i++) {
                dst[i] = (byte)(gray16[i] >> 8);
            }
            return result;
        }

        /// <summary>
        /// Accord.Imaging.Filters.Grayscale, 48 bpp -&gt; 16 bpp path (Filters/Color Filters/Grayscale.cs):
        /// <c>(ushort)(cr * px[RGB.R] + cg * px[RGB.G] + cb * px[RGB.B])</c> where, in GDI+ memory order,
        /// RGB.R = 2, RGB.G = 1, RGB.B = 0. <paramref name="interleaved"/> holds three ushorts per pixel in memory
        /// order (slot 0, 1, 2); the slot interpretation is the caller's business.
        /// </summary>
        public static ushort[] Grayscale48To16(ushort[] interleaved, int width, int height, double redCoefficient, double greenCoefficient, double blueCoefficient) {
            int count = width * height;
            if (interleaved.Length != count * 3) {
                throw new ArgumentException("Buffer size does not match width * height * 3", nameof(interleaved));
            }
            var result = new ushort[count];
            Parallel.For(0, height, y => {
                int start = y * width;
                int end = start + width;
                for (int i = start; i < end; i++) {
                    int s = i * 3;
                    result[i] = (ushort)((redCoefficient * interleaved[s + 2]) + (greenCoefficient * interleaved[s + 1]) + (blueCoefficient * interleaved[s]));
                }
            });
            return result;
        }
    }
}
