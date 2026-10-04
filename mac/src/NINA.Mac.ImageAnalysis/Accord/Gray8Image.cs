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
    /// An 8 bit grayscale image (row-major, top-down, stride == width). Stands in for Accord's
    /// <c>UnmanagedImage</c>/<c>Bitmap</c> in Format8bppIndexed. GDI+ pads 8 bpp rows to 4 bytes; none of the
    /// ported filters depend on the padding, so a packed buffer gives identical pixel results.
    /// </summary>
    public sealed class Gray8Image {

        public Gray8Image(int width, int height) : this(width, height, new byte[checked(width * height)]) {
        }

        public Gray8Image(int width, int height, byte[] pixels) {
            if (width <= 0) { throw new ArgumentOutOfRangeException(nameof(width)); }
            if (height <= 0) { throw new ArgumentOutOfRangeException(nameof(height)); }
            if (pixels == null) { throw new ArgumentNullException(nameof(pixels)); }
            if (pixels.Length != width * height) { throw new ArgumentException("Pixel buffer size does not match width * height", nameof(pixels)); }
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; }

        public int Height { get; }

        public byte[] Pixels { get; }

        public byte this[int x, int y] {
            get => Pixels[(y * Width) + x];
            set => Pixels[(y * Width) + x] = value;
        }

        public Gray8Image Clone() {
            return new Gray8Image(Width, Height, (byte[])Pixels.Clone());
        }

        /// <summary>Copies a sub rectangle (must lie inside the image).</summary>
        public Gray8Image Crop(int x, int y, int width, int height) {
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height) {
                throw new ArgumentOutOfRangeException(nameof(x), "Crop rectangle must lie inside the image");
            }
            var result = new Gray8Image(width, height);
            for (int row = 0; row < height; row++) {
                Buffer.BlockCopy(Pixels, ((y + row) * Width) + x, result.Pixels, row * width, width);
            }
            return result;
        }
    }
}
