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

namespace NINA.Mac.ImageAnalysis {

    /// <summary>
    /// Colour filter array layout at the buffer origin (pixel 0,0 = top-left of the first row). Values and names follow
    /// NINA.Core.Enum.SensorType for the patterns NINA can debayer.
    /// </summary>
    public enum BayerPattern {
        /// <summary>Monochrome data, or a colour camera in ZWO mono-bin mode (NINA clears the pattern then).</summary>
        None = 0,
        RGGB = 2,
        BGGR = 20,
        GBRG = 21,
        GRBG = 22,
        GRGB = 23,
        GBGR = 24,
        RGBG = 25,
        BGRG = 26
    }

    public static class BayerPatternUtility {

        /// <summary>
        /// Shifts the pattern for an odd X/Y offset of the readout origin (subframes, BAYERPAT with X/YBAYROFF).
        /// Port of NINA.Image/ImageAnalysis/BayerPatternUtility.cs:21-51.
        /// </summary>
        public static BayerPattern ApplyOffsets(BayerPattern bayerPattern, int offsetX, int offsetY) {
            if (offsetX % 2 != 0) {
                bayerPattern = bayerPattern switch {
                    BayerPattern.RGGB => BayerPattern.GRBG,
                    BayerPattern.GRBG => BayerPattern.RGGB,
                    BayerPattern.GBRG => BayerPattern.BGGR,
                    BayerPattern.BGGR => BayerPattern.GBRG,
                    BayerPattern.GRGB => BayerPattern.RGBG,
                    BayerPattern.RGBG => BayerPattern.GRGB,
                    BayerPattern.GBGR => BayerPattern.BGRG,
                    BayerPattern.BGRG => BayerPattern.GBGR,
                    _ => bayerPattern
                };
            }

            if (offsetY % 2 != 0) {
                bayerPattern = bayerPattern switch {
                    BayerPattern.RGGB => BayerPattern.GBRG,
                    BayerPattern.GBRG => BayerPattern.RGGB,
                    BayerPattern.GRBG => BayerPattern.BGGR,
                    BayerPattern.BGGR => BayerPattern.GRBG,
                    BayerPattern.GRGB => BayerPattern.GBGR,
                    BayerPattern.GBGR => BayerPattern.GRGB,
                    BayerPattern.RGBG => BayerPattern.BGRG,
                    BayerPattern.BGRG => BayerPattern.RGBG,
                    _ => bayerPattern
                };
            }

            return bayerPattern;
        }

        /// <summary>Parses a FITS BAYERPAT value ("RGGB", "GRBG", ...); null/blank gives None.</summary>
        public static BayerPattern Parse(string bayerPat) {
            if (string.IsNullOrWhiteSpace(bayerPat)) {
                return BayerPattern.None;
            }
            return Enum.TryParse<BayerPattern>(bayerPat.Trim().ToUpperInvariant(), out var pattern) ? pattern : BayerPattern.None;
        }
    }

    /// <summary>
    /// A camera frame as NINA holds it (IImageArray.FlatArray + ImageProperties): 16 bit samples, row-major,
    /// <b>top-down</b> (row 0 is the first row the camera reads out, like the ZWO SDK buffer and FITS ROWORDER =
    /// TOP-DOWN). <see cref="BitDepth"/> is what NINA uses to normalise statistics and the stretch; ZWO cameras
    /// report 16 because the SDK scales samples to 16 bit (ASICamera.cs BitDepth =&gt; 16).
    /// </summary>
    public sealed class PixelBuffer {

        public PixelBuffer(ushort[] data, int width, int height, int bitDepth = 16, BayerPattern bayerPattern = BayerPattern.None) {
            if (data == null) { throw new ArgumentNullException(nameof(data)); }
            if (width <= 0) { throw new ArgumentOutOfRangeException(nameof(width)); }
            if (height <= 0) { throw new ArgumentOutOfRangeException(nameof(height)); }
            if (data.Length != width * height) { throw new ArgumentException("Data length must equal width * height", nameof(data)); }
            if (bitDepth < 1 || bitDepth > 16) { throw new ArgumentOutOfRangeException(nameof(bitDepth)); }
            Data = data;
            Width = width;
            Height = height;
            BitDepth = bitDepth;
            BayerPattern = bayerPattern;
        }

        public ushort[] Data { get; }

        public int Width { get; }

        public int Height { get; }

        public int BitDepth { get; }

        /// <summary>Pattern at pixel (0,0) after any readout offsets have been applied.</summary>
        public BayerPattern BayerPattern { get; }

        /// <summary>NINA's ImageProperties.IsBayered for camera data.</summary>
        public bool IsBayered => BayerPattern != BayerPattern.None;

        public ushort this[int x, int y] => Data[(y * Width) + x];

        /// <summary>Copies a sub-frame. An odd origin shifts the Bayer pattern accordingly.</summary>
        public PixelBuffer Crop(int x, int y, int width, int height) {
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height) {
                throw new ArgumentOutOfRangeException(nameof(x), "Crop rectangle must lie inside the frame");
            }
            var data = new ushort[width * height];
            for (int row = 0; row < height; row++) {
                Array.Copy(Data, ((y + row) * Width) + x, data, row * width, width);
            }
            return new PixelBuffer(data, width, height, BitDepth, BayerPatternUtility.ApplyOffsets(BayerPattern, x, y));
        }

        /// <summary>Returns the same samples with rows reversed (for BOTTOM-UP sources).</summary>
        public PixelBuffer FlipVertical() {
            var data = new ushort[Data.Length];
            for (int row = 0; row < Height; row++) {
                Array.Copy(Data, row * Width, data, (Height - 1 - row) * Width, Width);
            }
            return new PixelBuffer(data, Width, Height, BitDepth, BayerPatternUtility.ApplyOffsets(BayerPattern, 0, Height - 1));
        }
    }
}
