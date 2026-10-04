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
using System.Threading.Tasks;

namespace NINA.Mac.ImageAnalysis.AccordPort {

    /// <summary>
    /// Port of Accord.Imaging.Filters.GaussianBlur (Accord.Imaging/AForge.Imaging/Filters/Convolution/GaussianBlur.cs)
    /// together with the 8 bpp path of its base class Convolution (Convolution.cs, Process8bppImage): integer kernel
    /// normalised to the corner value, integer division, dynamic divisor on the edges, threshold 0.
    /// </summary>
    public sealed class GaussianBlur {
        private double sigma = 1.4;
        private int size = 5;

        public GaussianBlur() {
            CreateFilter();
        }

        public GaussianBlur(double sigma) {
            Sigma = sigma;
        }

        public GaussianBlur(double sigma, int size) {
            Sigma = sigma;
            Size = size;
        }

        /// <summary>Gaussian sigma, clamped to [0.5, 5] (default 1.4).</summary>
        public double Sigma {
            get => sigma;
            set {
                sigma = Math.Max(0.5, Math.Min(5.0, value));
                CreateFilter();
            }
        }

        /// <summary>Kernel size, forced odd and clamped to [3, 21] (default 5).</summary>
        public int Size {
            get => size;
            set {
                size = Math.Max(3, Math.Min(21, value | 1));
                CreateFilter();
            }
        }

        public int[,] Kernel { get; private set; }

        public int Divisor { get; private set; }

        /// <summary>
        /// Accord.Math.Normal.Kernel2D(sigma², size): exp((x² + y²) / (-2 sigma²)) / (2 pi sigma²) sampled on the
        /// integer grid, rows indexed by y.
        /// </summary>
        public static double[,] Kernel2D(double sigma2, int kernelSize) {
            if (((kernelSize % 2) == 0) || (kernelSize < 3) || (kernelSize > 101)) {
                throw new ArgumentException("Wrong kernal size.", nameof(kernelSize));
            }
            int r = kernelSize / 2;
            var kernel = new double[kernelSize, kernelSize];
            for (int y = -r, i = 0; i < kernelSize; y++, i++) {
                for (int x = -r, j = 0; j < kernelSize; x++, j++) {
                    kernel[i, j] = Function2D(sigma2, x, y);
                }
            }
            return kernel;
        }

        private static double Function2D(double sigma2, double x, double y) {
            return Math.Exp(((x * x) + (y * y)) / (-2 * sigma2)) / (2 * Math.PI * sigma2);
        }

        private void CreateFilter() {
            double[,] kernel = Kernel2D(sigma * sigma, size);
            var intKernel = new int[size, size];
            int divisor = 0;
            double min = kernel[0, 0];

            for (int i = 0; i < size; i++) {
                for (int j = 0; j < size; j++) {
                    double v = kernel[i, j] / min;
                    if (v > ushort.MaxValue) {
                        v = ushort.MaxValue;
                    }
                    intKernel[i, j] = (int)v;
                    divisor += intKernel[i, j];
                }
            }
            Kernel = intKernel;
            Divisor = divisor;
        }

        /// <summary>Returns a new blurred image (Accord's non in-place Apply).</summary>
        public Gray8Image Apply(Gray8Image source) {
            var destination = new Gray8Image(source.Width, source.Height);
            Convolve8bpp(source.Pixels, destination.Pixels, source.Width, source.Height, Kernel, Divisor);
            return destination;
        }

        /// <summary>
        /// Convolution.Process8bppImage over the full image: long accumulators, edge pixels divide by the sum of the
        /// kernel elements that fell inside the image (dynamicDivisorForEdges = true), threshold 0, clamp to [0, 255].
        /// Integer arithmetic, so the evaluation order (and the row parallelism) does not affect the result.
        /// </summary>
        internal static void Convolve8bpp(byte[] src, byte[] dst, int width, int height, int[,] kernel, int divisor) {
            int size = kernel.GetLength(0);
            int radius = size >> 1;
            int kernelSize = size * size;
            var flat = new int[kernelSize];
            for (int i = 0; i < size; i++) {
                for (int j = 0; j < size; j++) {
                    flat[(i * size) + j] = kernel[i, j];
                }
            }

            Parallel.For(0, height, y => {
                bool rowInterior = y >= radius && y < height - radius;
                for (int x = 0; x < width; x++) {
                    long g;
                    long div;
                    if (rowInterior && x >= radius && x < width - radius) {
                        g = 0;
                        int k = 0;
                        for (int i = 0; i < size; i++) {
                            int rowStart = ((y + i - radius) * width) + (x - radius);
                            for (int j = 0; j < size; j++, k++) {
                                g += (long)flat[k] * src[rowStart + j];
                            }
                        }
                        div = divisor;
                    } else {
                        g = 0;
                        div = 0;
                        int processed = 0;
                        for (int i = 0; i < size; i++) {
                            int t = y + i - radius;
                            if (t < 0) {
                                continue;
                            }
                            if (t >= height) {
                                break;
                            }
                            for (int j = 0; j < size; j++) {
                                int tx = x + j - radius;
                                if (tx < 0) {
                                    continue;
                                }
                                if (tx < width) {
                                    int kv = flat[(i * size) + j];
                                    div += kv;
                                    g += (long)kv * src[(t * width) + tx];
                                    processed++;
                                }
                            }
                        }
                        if (processed == kernelSize) {
                            div = divisor;
                        }
                    }
                    if (div != 0) {
                        g /= div;
                    }
                    dst[(y * width) + x] = (byte)((g > 255) ? 255 : ((g < 0) ? 0 : g));
                }
            });
        }
    }
}
