#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Media {

    /// <summary>
    /// Stand-in for PresentationCore's ImageSource, the base of BitmapSource. <see cref="Width"/> and <see cref="Height"/>
    /// are device-independent units (1/96 inch), which RenderedImage.GetThumbnail uses to compute its scale factor.
    /// As in WPF only the framework derives from it (the constructor is internal).
    /// </summary>
    public abstract class ImageSource : Freezable {

        internal ImageSource() {
        }

        public abstract double Width { get; }

        public abstract double Height { get; }

        public abstract ImageMetadata Metadata { get; }

        /// <summary>
        /// WPF's pixel to DIP conversion, in single precision as WPF computes it: pixels * (96 / dpi), or the pixel
        /// count itself when the dpi is negative or too close to 0 to divide by.
        /// </summary>
        protected static double PixelsToDIPs(double dpi, int pixels) {
            var dpiF = (float)dpi;
            if (dpiF < 0.0f || IsCloseToDivideByZero(96.0f, dpiF)) {
                return pixels;
            }
            return (float)pixels * (96.0f / dpiF);
        }

        /// <summary>MS.Internal.FloatUtil.IsCloseToDivideByZero (same constant and comparison as PresentationCore's IL).</summary>
        private static bool IsCloseToDivideByZero(float numerator, float denominator) {
            return Math.Abs(denominator) <= Math.Abs(numerator) * 5.960465E-08f;
        }
    }

    /// <summary>Stand-in for PresentationCore's abstract ImageMetadata (base of BitmapMetadata). Signature only.</summary>
    public abstract class ImageMetadata : Freezable {

        internal ImageMetadata() {
        }
    }
}
