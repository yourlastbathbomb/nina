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

    /// <summary>Output of <see cref="Debayer"/>.</summary>
    public sealed class DebayeredImage {

        internal DebayeredImage(int width, int height, ushort[] rgb, ushort[] lum, ushort[] red, ushort[] green, ushort[] blue) {
            Width = width;
            Height = height;
            Rgb = rgb;
            Lum = lum;
            Red = red;
            Green = green;
            Blue = blue;
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>
        /// Interleaved 48 bit RGB, three ushorts per pixel in R, G, B order. This is the memory layout of NINA's
        /// debayered BitmapSource (WPF Rgb48). NINA's GDI+ based code addresses the same memory as BGR, which
        /// matters for <see cref="StarDetector"/>'s grayscale step.
        /// </summary>
        public ushort[] Rgb { get; }

        /// <summary>(R + G + B) / 3 per pixel, or null when not requested (NINA's LRGBArrays.Lum).</summary>
        public ushort[] Lum { get; }

        /// <summary>Red plane, or null when colour channels were not requested.</summary>
        public ushort[] Red { get; }

        public ushort[] Green { get; }

        public ushort[] Blue { get; }
    }

    /// <summary>
    /// Port of NINA.Image/ImageAnalysis/BayerFilter16bpp.cs (Demosaic, ProcessBorders, ExtractLrgba) together with
    /// the pattern tables of ImageUtility.Debayer (ImageUtility.cs:250-285). Each output channel of a pixel is the
    /// integer mean of the same-colour samples in its in-bounds 3x3 neighbourhood. Integer arithmetic, so the
    /// parallel/managed implementation is bit-identical to the pointer code.
    /// </summary>
    public static class Debayer {

        /// <summary>
        /// Channel slot (0 = R, 1 = G, 2 = B of the output) for [y &amp; 1, x &amp; 1]. Transcribed from NINA's tables, which are
        /// written with Accord's RGB constants (B = 0, G = 1, R = 2) and land in WPF Rgb48 memory order.
        /// </summary>
        public static int[,] GetPattern(BayerPattern bayerPattern) {
            const int B = 0, G = 1, R = 2; // Accord.Imaging.RGB constants, as used by ImageUtility.Debayer
            return bayerPattern switch {
                BayerPattern.RGGB => new int[,] { { B, G }, { G, R } },
                BayerPattern.RGBG => new int[,] { { G, B }, { G, R } },
                BayerPattern.GRGB => new int[,] { { B, G }, { R, G } },
                BayerPattern.GRBG => new int[,] { { G, B }, { R, G } },
                BayerPattern.GBGR => new int[,] { { R, G }, { B, G } },
                BayerPattern.GBRG => new int[,] { { G, R }, { B, G } },
                BayerPattern.BGRG => new int[,] { { G, R }, { G, B } },
                BayerPattern.BGGR => new int[,] { { R, G }, { G, B } },
                _ => throw new ArgumentException($"Unsupported CFA pattern {bayerPattern}", nameof(bayerPattern))
            };
        }

        public static DebayeredImage Apply(PixelBuffer buffer, bool saveColorChannels, bool saveLumChannel) {
            if (!buffer.IsBayered) {
                throw new ArgumentException("Buffer has no Bayer pattern", nameof(buffer));
            }
            return Apply(buffer.Data, buffer.Width, buffer.Height, buffer.BayerPattern, saveColorChannels, saveLumChannel);
        }

        public static DebayeredImage Apply(ushort[] src, int width, int height, BayerPattern bayerPattern, bool saveColorChannels, bool saveLumChannel) {
            if (width < 2 || height < 2) {
                throw new ArgumentException("Image must be at least 2x2 to debayer");
            }
            int[,] pattern = GetPattern(bayerPattern);
            int[] flat = { pattern[0, 0], pattern[0, 1], pattern[1, 0], pattern[1, 1] };
            var dst = new ushort[width * height * 3];

            // interior: same-colour counts depend only on the pixel's parity
            var interiorCounts = new int[4, 3];
            for (int py = 0; py < 2; py++) {
                for (int px = 0; px < 2; px++) {
                    for (int dy = -1; dy <= 1; dy++) {
                        for (int dx = -1; dx <= 1; dx++) {
                            interiorCounts[(py * 2) + px, flat[(((py + dy) & 1) * 2) + ((px + dx) & 1)]]++;
                        }
                    }
                }
            }

            Parallel.For(0, height, y => {
                Span<int> sums = stackalloc int[3];
                Span<int> counts = stackalloc int[3];
                bool interiorRow = y > 0 && y < height - 1;
                for (int x = 0; x < width; x++) {
                    sums.Clear();
                    if (interiorRow && x > 0 && x < width - 1) {
                        for (int dy = -1; dy <= 1; dy++) {
                            int ny = y + dy;
                            int rowBase = ((ny & 1) * 2);
                            int rowOffset = ny * width;
                            for (int dx = -1; dx <= 1; dx++) {
                                int nx = x + dx;
                                sums[flat[rowBase + (nx & 1)]] += src[rowOffset + nx];
                            }
                        }
                        int parity = ((y & 1) * 2) + (x & 1);
                        counts[0] = interiorCounts[parity, 0];
                        counts[1] = interiorCounts[parity, 1];
                        counts[2] = interiorCounts[parity, 2];
                    } else {
                        counts.Clear();
                        for (int dy = -1; dy <= 1; dy++) {
                            int ny = y + dy;
                            if (ny < 0 || ny >= height) {
                                continue;
                            }
                            for (int dx = -1; dx <= 1; dx++) {
                                int nx = x + dx;
                                if (nx < 0 || nx >= width) {
                                    continue;
                                }
                                int ch = flat[((ny & 1) * 2) + (nx & 1)];
                                sums[ch] += src[(ny * width) + nx];
                                counts[ch]++;
                            }
                        }
                    }
                    int o = ((y * width) + x) * 3;
                    dst[o] = (ushort)(sums[0] / counts[0]);
                    dst[o + 1] = (ushort)(sums[1] / counts[1]);
                    dst[o + 2] = (ushort)(sums[2] / counts[2]);
                }
            });

            ushort[] lum = null, red = null, green = null, blue = null;
            if (saveColorChannels || saveLumChannel) {
                int count = width * height;
                if (saveColorChannels) {
                    red = new ushort[count];
                    green = new ushort[count];
                    blue = new ushort[count];
                }
                if (saveLumChannel) {
                    lum = new ushort[count];
                }
                Parallel.For(0, height, y => {
                    int start = y * width;
                    int end = start + width;
                    for (int i = start; i < end; i++) {
                        int s = i * 3;
                        ushort r = dst[s];
                        ushort g = dst[s + 1];
                        ushort b = dst[s + 2];
                        if (red != null) {
                            red[i] = r;
                            green[i] = g;
                            blue[i] = b;
                        }
                        if (lum != null) {
                            lum[i] = (ushort)((r + g + b) / 3.0);
                        }
                    }
                });
            }
            return new DebayeredImage(width, height, dst, lum, red, green, blue);
        }
    }
}
