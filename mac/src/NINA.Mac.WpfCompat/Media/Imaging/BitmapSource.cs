#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Runtime.InteropServices;

namespace System.Windows.Media.Imaging {

    /// <summary>
    /// Stand-in for PresentationCore's BitmapSource with a managed pixel buffer instead of a WIC bitmap.
    /// NINA.Image renders every frame through it (BaseImageData.RenderBitmapSource is a Gray16 BitmapSource.Create over
    /// the raw array) and reads pixels back with CopyPixels (ImageArrayExposureData.FromBitmapSource, ImageUtility).
    /// NINA.Astrometry only uses it to type the optional sky-survey preview of a deep-sky object.
    /// <para>
    /// <see cref="Create(int, int, double, double, PixelFormat, BitmapPalette, Array, int)"/> copies the pixels, as WIC's
    /// CreateBitmapFromMemory does, into rows packed at the minimum stride; the bitmap never changes afterwards.
    /// Argument checks and exception types follow WPF's CachedBitmap and CriticalCopyPixels: the element types WPF accepts
    /// (byte, short, ushort, int, uint, float, double), CopyPixels' offset counted in array elements, an empty or
    /// zero-sized source rectangle meaning the whole bitmap, and the stride and buffer-size minimums. Indexed formats need a
    /// palette, as in WPF, and palettes do not exist here, so Indexed8 bitmaps cannot be created.
    /// </para>
    /// </summary>
    public abstract class BitmapSource : ImageSource {
        private PixelFormat format;
        private int pixelWidth;
        private int pixelHeight;
        private double dpiX;
        private double dpiY;
        private byte[] pixels;

        protected BitmapSource() {
        }

        public virtual PixelFormat Format {
            get {
                EnsureInitialized();
                return format;
            }
        }

        public virtual int PixelWidth {
            get {
                EnsureInitialized();
                return pixelWidth;
            }
        }

        public virtual int PixelHeight {
            get {
                EnsureInitialized();
                return pixelHeight;
            }
        }

        public virtual double DpiX {
            get {
                EnsureInitialized();
                return dpiX;
            }
        }

        public virtual double DpiY {
            get {
                EnsureInitialized();
                return dpiY;
            }
        }

        /// <summary>Always null: palettes (indexed formats) do not exist on macOS.</summary>
        public virtual BitmapPalette Palette => null;

        public override double Width => PixelsToDIPs(DpiX, PixelWidth);

        public override double Height => PixelsToDIPs(DpiY, PixelHeight);

        /// <summary>Null, as for a WPF bitmap created from memory: there is no container metadata.</summary>
        public override ImageMetadata Metadata => null;

        public static BitmapSource Create(int pixelWidth, int pixelHeight, double dpiX, double dpiY, PixelFormat pixelFormat, BitmapPalette palette, Array pixels, int stride) {
            ArgumentNullException.ThrowIfNull(pixels, nameof(pixels));
            if (pixels.Rank != 1) {
                throw new ArgumentException("The collection must be one-dimensional.", nameof(pixels));
            }
            var elementSize = ElementSize(pixels);
            var bufferSize = (long)elementSize * pixels.Length;
            var source = new MemorySpan(pixels, 0, bufferSize);
            return new CachedBitmap(pixelWidth, pixelHeight, dpiX, dpiY, pixelFormat, palette, source, stride);
        }

        public static BitmapSource Create(int pixelWidth, int pixelHeight, double dpiX, double dpiY, PixelFormat pixelFormat, BitmapPalette palette, IntPtr buffer, int bufferSize, int stride) {
            if (buffer == IntPtr.Zero) {
                throw new ArgumentNullException(nameof(buffer));
            }
            var source = new MemorySpan(buffer, bufferSize);
            return new CachedBitmap(pixelWidth, pixelHeight, dpiX, dpiY, pixelFormat, palette, source, stride);
        }

        public virtual void CopyPixels(Array pixels, int stride, int offset) {
            CopyPixels(Int32Rect.Empty, pixels, stride, offset);
        }

        public virtual void CopyPixels(Int32Rect sourceRect, Array pixels, int stride, int offset) {
            EnsureInitialized();
            ArgumentNullException.ThrowIfNull(pixels, nameof(pixels));
            if (pixels.Rank != 1) {
                throw new ArgumentException("The collection must be one-dimensional.", nameof(pixels));
            }
            if (offset < 0) {
                throw new OverflowException("The image data generated an overflow during processing.");
            }
            var elementSize = ElementSize(pixels);
            var destinationSize = checked((uint)elementSize * (uint)(pixels.Length - offset));
            if (offset >= pixels.Length) {
                throw new IndexOutOfRangeException();
            }
            CopyPixelsCore(sourceRect, new MemorySpan(pixels, (long)offset * elementSize, destinationSize), destinationSize, stride);
        }

        public virtual void CopyPixels(Int32Rect sourceRect, IntPtr buffer, int bufferSize, int stride) {
            EnsureInitialized();
            if (buffer == IntPtr.Zero) {
                throw new ArgumentNullException(nameof(buffer));
            }
            CopyPixelsCore(sourceRect, new MemorySpan(buffer, (uint)bufferSize), (uint)bufferSize, stride);
        }

        /// <summary>Bytes per row of the packed pixel buffer.</summary>
        internal int PackedStride => (pixelWidth * format.BitsPerPixel + 7) / 8;

        /// <summary>The packed pixel rows (read-only by convention; never handed out to callers).</summary>
        internal byte[] PackedPixels {
            get {
                EnsureInitialized();
                return pixels;
            }
        }

        internal bool IsInitialized => pixels != null;

        /// <summary>Sets the bitmap once, from rows already packed at the minimum stride.</summary>
        internal void InitializePixels(PixelFormat format, int pixelWidth, int pixelHeight, double dpiX, double dpiY, byte[] packedPixels) {
            this.format = format;
            this.pixelWidth = pixelWidth;
            this.pixelHeight = pixelHeight;
            this.dpiX = dpiX;
            this.dpiY = dpiY;
            pixels = packedPixels;
        }

        /// <summary>
        /// WIC's CreateBitmapFromMemory as CachedBitmap.InitFromMemoryPtr calls it: validates the arguments and copies the
        /// rows into a packed buffer.
        /// </summary>
        internal static byte[] PackFromMemory(int pixelWidth, int pixelHeight, PixelFormat pixelFormat, BitmapPalette palette, MemorySpan source, int stride) {
            if (pixelFormat.Palettized && palette == null) {
                throw new InvalidOperationException("Image format requires a palette.");
            }
            var bitsPerPixel = pixelFormat.BitsPerPixel;
            if (bitsPerPixel == 0) {
                throw new ArgumentException($"'{pixelFormat}' PixelFormat is not supported for this operation.", nameof(pixelFormat));
            }
            if (bitsPerPixel % 8 != 0) {
                throw new NotSupportedException($"Pixel format {pixelFormat} is not supported on macOS.");
            }
            if (pixelWidth <= 0 || pixelHeight <= 0) {
                throw new ArgumentException("Value does not fall within the expected range.");
            }
            var rowBytes = checked((pixelWidth * bitsPerPixel + 7) / 8);
            if (stride < rowBytes) {
                throw new ArgumentException("Value does not fall within the expected range.");
            }
            var required = (long)stride * (pixelHeight - 1) + rowBytes;
            if (source.Length < required) {
                throw new ArgumentException("Buffer size is not sufficient.");
            }
            var packed = new byte[checked(rowBytes * pixelHeight)];
            for (var row = 0; row < pixelHeight; row++) {
                source.CopyTo((long)row * stride, packed, row * rowBytes, rowBytes);
            }
            return packed;
        }

        private void CopyPixelsCore(Int32Rect sourceRect, MemorySpan destination, uint bufferSize, int stride) {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride, nameof(stride));
            if (sourceRect.Width <= 0) {
                sourceRect.Width = pixelWidth;
            }
            if (sourceRect.Height <= 0) {
                sourceRect.Height = pixelHeight;
            }
            ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceRect.Width, pixelWidth, "sourceRect.Width");
            ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceRect.Height, pixelHeight, "sourceRect.Height");
            var bitsPerPixel = format.BitsPerPixel;
            var rowBytes = checked(sourceRect.Width * bitsPerPixel + 7) / 8;
            ArgumentOutOfRangeException.ThrowIfLessThan(stride, rowBytes, nameof(stride));
            var required = checked((uint)stride * (uint)(sourceRect.Height - 1) + (uint)rowBytes);
            ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, required, nameof(bufferSize));
            // WIC IWICBitmapSource::CopyPixels: the rectangle must lie inside the bitmap (E_INVALIDARG)
            if (sourceRect.X < 0 || sourceRect.Y < 0 || (long)sourceRect.X + sourceRect.Width > pixelWidth || (long)sourceRect.Y + sourceRect.Height > pixelHeight) {
                throw new ArgumentException("Value does not fall within the expected range.");
            }
            var packedStride = PackedStride;
            var xOffset = sourceRect.X * bitsPerPixel / 8;
            for (var row = 0; row < sourceRect.Height; row++) {
                destination.CopyFrom(pixels, (sourceRect.Y + row) * packedStride + xOffset, (long)row * stride, rowBytes);
            }
        }

        private void EnsureInitialized() {
            if (pixels == null) {
                throw new InvalidOperationException("Must complete initialization (BeginInit/EndInit or a constructor that creates the bitmap) before the bitmap can be used.");
            }
        }

        /// <summary>Element sizes WPF accepts for pixel arrays (CachedBitmap and CriticalCopyPixels).</summary>
        private static int ElementSize(Array pixels) {
            switch (pixels) {
                case byte[]:
                    return 1;
                case short[]:
                case ushort[]:
                    return 2;
                case int[]:
                case uint[]:
                case float[]:
                    return 4;
                case double[]:
                    return 8;
                default:
                    throw new ArgumentException("Pixel array must be of a primitive numeric type (byte, short, ushort, int, uint, float or double).");
            }
        }

        /// <summary>A byte range in a primitive array or in unmanaged memory.</summary>
        internal readonly struct MemorySpan {
            private readonly Array array;
            private readonly long arrayOffset;
            private readonly IntPtr pointer;

            public MemorySpan(Array array, long byteOffset, long length) {
                this.array = array;
                arrayOffset = byteOffset;
                pointer = IntPtr.Zero;
                Length = length;
            }

            public MemorySpan(IntPtr pointer, long length) {
                array = null;
                arrayOffset = 0;
                this.pointer = pointer;
                Length = length;
            }

            public long Length { get; }

            public void CopyTo(long sourceOffset, byte[] destination, int destinationOffset, int count) {
                if (array != null) {
                    Buffer.BlockCopy(array, checked((int)(arrayOffset + sourceOffset)), destination, destinationOffset, count);
                } else {
                    Marshal.Copy(IntPtr.Add(pointer, checked((int)sourceOffset)), destination, destinationOffset, count);
                }
            }

            public void CopyFrom(byte[] source, int sourceOffset, long destinationOffset, int count) {
                if (array != null) {
                    Buffer.BlockCopy(source, sourceOffset, array, checked((int)(arrayOffset + destinationOffset)), count);
                } else {
                    Marshal.Copy(source, sourceOffset, IntPtr.Add(pointer, checked((int)destinationOffset)), count);
                }
            }
        }
    }

    /// <summary>The bitmap BitmapSource.Create returns (WPF's internal CachedBitmap): a copy of the caller's pixels.</summary>
    internal sealed class CachedBitmap : BitmapSource {

        internal CachedBitmap(int pixelWidth, int pixelHeight, double dpiX, double dpiY, PixelFormat pixelFormat, BitmapPalette palette, MemorySpan source, int stride) {
            var packed = PackFromMemory(pixelWidth, pixelHeight, pixelFormat, palette, source, stride);
            InitializePixels(pixelFormat, pixelWidth, pixelHeight, dpiX, dpiY, packed);
        }
    }

    /// <summary>
    /// Placeholder for PresentationCore's BitmapPalette (the colour table of indexed formats). NINA passes null wherever
    /// a palette is asked for; no palette can be created on macOS.
    /// </summary>
    public sealed class BitmapPalette {

        private BitmapPalette() {
        }
    }
}
