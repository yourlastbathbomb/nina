#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.ImageAnalysis.AccordPort;
using System;
using System.Runtime.InteropServices;
using AccordPixelFormat = System.Drawing.Imaging.PixelFormat;
using UnmanagedImage = Accord.Imaging.UnmanagedImage;

namespace NINA.Mac.ImageAnalysis.Test {

    /// <summary>Copies pixel buffers into and out of the original Accord UnmanagedImage (test oracle).</summary>
    internal static class OracleImages {

        public static UnmanagedImage ToUnmanaged8(Gray8Image image) {
            var u = UnmanagedImage.Create(image.Width, image.Height, AccordPixelFormat.Format8bppIndexed);
            for (int y = 0; y < image.Height; y++) {
                Marshal.Copy(image.Pixels, y * image.Width, IntPtr.Add(u.ImageData, y * u.Stride), image.Width);
            }
            return u;
        }

        public static Gray8Image FromUnmanaged8(UnmanagedImage u) {
            if (u.PixelFormat != AccordPixelFormat.Format8bppIndexed) {
                throw new ArgumentException("Expected 8 bpp");
            }
            var image = new Gray8Image(u.Width, u.Height);
            for (int y = 0; y < u.Height; y++) {
                Marshal.Copy(IntPtr.Add(u.ImageData, y * u.Stride), image.Pixels, y * u.Width, u.Width);
            }
            return image;
        }

        public static UnmanagedImage ToUnmanaged16(ushort[] data, int width, int height) {
            var u = UnmanagedImage.Create(width, height, AccordPixelFormat.Format16bppGrayScale);
            var shorts = new short[width];
            for (int y = 0; y < height; y++) {
                Buffer.BlockCopy(data, y * width * 2, shorts, 0, width * 2);
                Marshal.Copy(shorts, 0, IntPtr.Add(u.ImageData, y * u.Stride), width);
            }
            return u;
        }

        public static UnmanagedImage ToUnmanaged48(ushort[] interleaved, int width, int height) {
            var u = UnmanagedImage.Create(width, height, AccordPixelFormat.Format48bppRgb);
            var shorts = new short[width * 3];
            for (int y = 0; y < height; y++) {
                Buffer.BlockCopy(interleaved, y * width * 6, shorts, 0, width * 6);
                Marshal.Copy(shorts, 0, IntPtr.Add(u.ImageData, y * u.Stride), width * 3);
            }
            return u;
        }

        /// <summary>Reads a 16 bit per sample image (channels = 1 or 3) into a packed ushort array.</summary>
        public static ushort[] FromUnmanaged16(UnmanagedImage u, int channels) {
            var result = new ushort[u.Width * u.Height * channels];
            var shorts = new short[u.Width * channels];
            for (int y = 0; y < u.Height; y++) {
                Marshal.Copy(IntPtr.Add(u.ImageData, y * u.Stride), shorts, 0, u.Width * channels);
                Buffer.BlockCopy(shorts, 0, result, y * u.Width * channels * 2, u.Width * channels * 2);
            }
            return result;
        }
    }
}
