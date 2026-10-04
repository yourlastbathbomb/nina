#region "copyright"

/*
    Part of the LGPL Accord.NET / AForge.NET port used by the N.I.N.A. macOS image-analysis library.

    Copyright © Andrew Kirillov (AForge.NET framework), 2005-2008
    Copyright © César Souza (Accord.NET Framework), 2009-2017
    The no-blur variant mirrors NINA.Image/ImageAnalysis/NoBlurCannyEdgeDetector.cs
    (Copyright © 2016 - 2026 Stefan Berg and the N.I.N.A. contributors), itself a copy of the Accord filter.
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
    /// Port of Accord.Imaging.Filters.CannyEdgeDetector
    /// (Accord.Imaging/AForge.Imaging/Filters/Edge Detectors/CannyEdgeDetector.cs, ProcessFilter) and of N.I.N.A.'s
    /// NoBlurCannyEdgeDetector (NINA.Image/ImageAnalysis/NoBlurCannyEdgeDetector.cs:39-183), which is the same filter
    /// without step 1. Only the in-place 8 bpp path that N.I.N.A. uses is ported.
    /// </summary>
    public sealed class CannyEdgeDetector {
        private readonly GaussianBlur gaussianFilter = new GaussianBlur();

        /// <summary>Accord defaults: low 20, high 100, Gaussian sigma 1.4, size 5, blur on.</summary>
        public CannyEdgeDetector() {
        }

        public CannyEdgeDetector(byte lowThreshold, byte highThreshold) {
            LowThreshold = lowThreshold;
            HighThreshold = highThreshold;
        }

        public CannyEdgeDetector(byte lowThreshold, byte highThreshold, double sigma) : this(lowThreshold, highThreshold) {
            gaussianFilter.Sigma = sigma;
        }

        public byte LowThreshold { get; set; } = 20;

        public byte HighThreshold { get; set; } = 100;

        /// <summary>False reproduces NINA's NoBlurCannyEdgeDetector (the caller already blurred the image).</summary>
        public bool ApplyBlur { get; set; } = true;

        public double GaussianSigma {
            get => gaussianFilter.Sigma;
            set => gaussianFilter.Sigma = value;
        }

        public int GaussianSize {
            get => gaussianFilter.Size;
            set => gaussianFilter.Size = value;
        }

        /// <summary>
        /// BaseUsingCopyPartialFilter.ApplyInPlace semantics: the filter reads from a copy of the image and writes into
        /// the image itself. Pixels that are not written (the 1 px frame) keep their original values until the final
        /// step blacks out the frame; the hysteresis step reads those original values, as upstream does.
        /// </summary>
        public void ApplyInPlace(Gray8Image image) {
            int imageWidth = image.Width;
            int imageHeight = image.Height;
            byte[] dst = image.Pixels;
            byte[] source = (byte[])dst.Clone();

            // processing start and stop X,Y positions (rect = whole image)
            int startX = 1;
            int startY = 1;
            int stopX = startX + imageWidth - 2;
            int stopY = startY + imageHeight - 2;

            byte lowThreshold = LowThreshold;
            byte highThreshold = HighThreshold;
            double toAngle = 180.0 / Math.PI;

            // STEP 1 - blur image (CannyEdgeDetector only)
            byte[] src = ApplyBlur ? gaussianFilter.Apply(new Gray8Image(imageWidth, imageHeight, source)).Pixels : source;

            int interiorWidth = Math.Max(0, imageWidth - 2);
            int interiorHeight = Math.Max(0, imageHeight - 2);
            var orients = new byte[interiorWidth * interiorHeight];
            // gradients[x, y] in Accord; zero outside the processed interior
            var gradients = new float[imageWidth * imageHeight];

            // STEP 2 - calculate magnitude and edge orientation (rows in parallel; the maximum is order independent)
            var rowMax = new float[imageHeight];
            Parallel.For(startY, stopY, y => {
                int row = y * imageWidth;
                int p = (y - startY) * interiorWidth;
                float localMax = float.NegativeInfinity;
                for (int x = startX; x < stopX; x++, p++) {
                    int i = row + x;
                    int gx = src[i - imageWidth + 1] + src[i + imageWidth + 1]
                           - src[i - imageWidth - 1] - src[i + imageWidth - 1]
                           + (2 * (src[i + 1] - src[i - 1]));

                    int gy = src[i - imageWidth - 1] + src[i - imageWidth + 1]
                           - src[i + imageWidth - 1] - src[i + imageWidth + 1]
                           + (2 * (src[i - imageWidth] - src[i + imageWidth]));

                    float gradient = (float)Math.Sqrt((gx * gx) + (gy * gy));
                    gradients[i] = gradient;
                    if (gradient > localMax) {
                        localMax = gradient;
                    }

                    double orientation;
                    if (gx == 0) {
                        orientation = (gy == 0) ? 0 : 90;
                    } else {
                        double div = (double)gy / gx;
                        if (div < 0) {
                            orientation = 180 - (Math.Atan(-div) * toAngle);
                        } else {
                            orientation = Math.Atan(div) * toAngle;
                        }

                        if (orientation < 22.5) {
                            orientation = 0;
                        } else if (orientation < 67.5) {
                            orientation = 45;
                        } else if (orientation < 112.5) {
                            orientation = 90;
                        } else if (orientation < 157.5) {
                            orientation = 135;
                        } else {
                            orientation = 0;
                        }
                    }
                    orients[p] = (byte)orientation;
                }
                rowMax[y] = localMax;
            });
            float maxGradient = float.NegativeInfinity;
            for (int y = startY; y < stopY; y++) {
                if (rowMax[y] > maxGradient) {
                    maxGradient = rowMax[y];
                }
            }

            // STEP 3 - suppress non maximums (reads gradients only, writes each pixel once)
            Parallel.For(startY, stopY, y => {
                int row = y * imageWidth;
                int p = (y - startY) * interiorWidth;
                float leftPixel = 0, rightPixel = 0;
                for (int x = startX; x < stopX; x++, p++) {
                    int i = row + x;
                    switch (orients[p]) {
                        case 0:
                            leftPixel = gradients[i - 1];
                            rightPixel = gradients[i + 1];
                            break;

                        case 45:
                            leftPixel = gradients[i + imageWidth - 1];
                            rightPixel = gradients[i - imageWidth + 1];
                            break;

                        case 90:
                            leftPixel = gradients[i + imageWidth];
                            rightPixel = gradients[i - imageWidth];
                            break;

                        case 135:
                            leftPixel = gradients[i + imageWidth + 1];
                            rightPixel = gradients[i - imageWidth - 1];
                            break;
                    }
                    float g = gradients[i];
                    if ((g < leftPixel) || (g < rightPixel)) {
                        dst[i] = 0;
                    } else {
                        dst[i] = (byte)(g / maxGradient * 255);
                    }
                }
            });

            // STEP 4 - hysteresis, in place. A pixel is only ever lowered from a value below highThreshold to 0, and the
            // test only asks whether neighbours are >= highThreshold, so the result does not depend on the visiting
            // order (or on other rows being processed concurrently); the untouched frame still holds original values.
            Parallel.For(startY, stopY, y => {
                int row = y * imageWidth;
                for (int x = startX; x < stopX; x++) {
                    int i = row + x;
                    if (dst[i] < highThreshold) {
                        if (dst[i] < lowThreshold) {
                            dst[i] = 0;
                        } else {
                            if ((dst[i - 1] < highThreshold) &&
                                (dst[i + 1] < highThreshold) &&
                                (dst[i - imageWidth - 1] < highThreshold) &&
                                (dst[i - imageWidth] < highThreshold) &&
                                (dst[i - imageWidth + 1] < highThreshold) &&
                                (dst[i + imageWidth - 1] < highThreshold) &&
                                (dst[i + imageWidth] < highThreshold) &&
                                (dst[i + imageWidth + 1] < highThreshold)) {
                                dst[i] = 0;
                            }
                        }
                    }
                }
            });

            // STEP 5 - draw black rectangle (Drawing.Rectangle outline of the processed rect)
            BlackFrame(dst, imageWidth, imageHeight);
        }

        internal static void BlackFrame(byte[] pixels, int width, int height) {
            for (int x = 0; x < width; x++) {
                pixels[x] = 0;
                pixels[((height - 1) * width) + x] = 0;
            }
            for (int y = 0; y < height; y++) {
                pixels[y * width] = 0;
                pixels[(y * width) + width - 1] = 0;
            }
        }
    }
}
