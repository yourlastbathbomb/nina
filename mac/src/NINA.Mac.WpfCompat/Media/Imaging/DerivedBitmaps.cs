#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.ComponentModel;

namespace System.Windows.Media.Imaging {

    /// <summary>
    /// Stand-in for PresentationCore's FormatConvertedBitmap, with WPF's initialisation protocol (properties are set
    /// between BeginInit and EndInit, or through the four-argument constructor). Only a conversion to the source's own
    /// format (a copy) is implemented. Other conversions throw NotSupportedException from EndInit: WIC's converters
    /// apply gamma and colour-space rules that cannot be checked here without Windows, and no macOS engine path converts
    /// (the callers are ImageUtility.Convert16BppTo8BppSource, which nothing calls, and ImageArrayExposureData.FromBitmapSource
    /// for Bgr24/Bgr32/Pbgra32 input, which only the WPF simulator camera produces).
    /// </summary>
    public sealed class FormatConvertedBitmap : BitmapSource, ISupportInitialize {
        private readonly BitmapInitialization init = new BitmapInitialization();
        private BitmapSource source;
        private PixelFormat destinationFormat;
        private BitmapPalette destinationPalette;
        private double alphaThreshold;

        public FormatConvertedBitmap() {
        }

        public FormatConvertedBitmap(BitmapSource source, PixelFormat destinationFormat, BitmapPalette destinationPalette, double alphaThreshold) {
            ArgumentNullException.ThrowIfNull(source, nameof(source));
            BeginInit();
            Source = source;
            DestinationFormat = destinationFormat;
            DestinationPalette = destinationPalette;
            AlphaThreshold = alphaThreshold;
            EndInit();
        }

        public BitmapSource Source {
            get => source;
            set {
                WritePreamble();
                init.SetPrologue();
                source = value;
            }
        }

        public PixelFormat DestinationFormat {
            get => destinationFormat;
            set {
                WritePreamble();
                init.SetPrologue();
                destinationFormat = value;
            }
        }

        public BitmapPalette DestinationPalette {
            get => destinationPalette;
            set {
                WritePreamble();
                init.SetPrologue();
                destinationPalette = value;
            }
        }

        public double AlphaThreshold {
            get => alphaThreshold;
            set {
                WritePreamble();
                init.SetPrologue();
                alphaThreshold = value;
            }
        }

        public void BeginInit() {
            WritePreamble();
            init.BeginInit();
        }

        public void EndInit() {
            WritePreamble();
            init.EndInit();
            if (source == null) {
                throw new InvalidOperationException("Property 'Source' must be set.");
            }
            if (destinationFormat != source.Format) {
                throw new NotSupportedException($"Converting {source.Format} to {destinationFormat} is not supported on macOS (WIC pixel-format conversion is not available).");
            }
            InitializePixels(source.Format, source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY, (byte[])source.PackedPixels.Clone());
        }
    }

    /// <summary>
    /// Stand-in for PresentationCore's WriteableBitmap: the copy constructor only (NINA wraps a TransformedBitmap or a
    /// FormatConvertedBitmap in one). The back buffer, Lock/Unlock and WritePixels are absent.
    /// </summary>
    public sealed class WriteableBitmap : BitmapSource {

        public WriteableBitmap(BitmapSource source) {
            ArgumentNullException.ThrowIfNull(source, nameof(source));
            InitializePixels(source.Format, source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY, (byte[])source.PackedPixels.Clone());
        }
    }

    /// <summary>
    /// Stand-in for PresentationCore's TransformedBitmap for positive <see cref="ScaleTransform"/>s, which is what
    /// RenderedImage.GetThumbnail uses (the plate-solve progress thumbnail). The size follows WPF exactly:
    /// max(1, (uint)(scale × pixels + 0.5)) in each direction, with the source's dpi. WPF resamples with WIC's Fant
    /// interpolation; here each output pixel is the area-weighted mean of the source pixels it covers, rounded, per
    /// channel. That is close to Fant for downscaling but not bit-identical, which only matters for display.
    /// Supported formats: Gray8, Gray16, Bgr24, Bgr32, Bgra32, Pbgra32 and Rgb48. Other transforms (rotation, flips)
    /// and formats throw NotSupportedException.
    /// </summary>
    public sealed class TransformedBitmap : BitmapSource, ISupportInitialize {
        private readonly BitmapInitialization init = new BitmapInitialization();
        private BitmapSource source;
        private Transform transform;

        public TransformedBitmap() {
        }

        public TransformedBitmap(BitmapSource source, Transform newTransform) {
            ArgumentNullException.ThrowIfNull(source, nameof(source));
            ArgumentNullException.ThrowIfNull(newTransform, nameof(newTransform));
            BeginInit();
            Source = source;
            Transform = newTransform;
            EndInit();
        }

        public BitmapSource Source {
            get => source;
            set {
                WritePreamble();
                init.SetPrologue();
                source = value;
            }
        }

        public Transform Transform {
            get => transform;
            set {
                WritePreamble();
                init.SetPrologue();
                transform = value;
            }
        }

        public void BeginInit() {
            WritePreamble();
            init.BeginInit();
        }

        public void EndInit() {
            WritePreamble();
            init.EndInit();
            if (source == null) {
                throw new InvalidOperationException("Property 'Source' must be set.");
            }
            if (transform == null) {
                throw new InvalidOperationException("Property 'Transform' must be set.");
            }
            if (transform is not ScaleTransform scale || !(scale.ScaleX > 0) || !(scale.ScaleY > 0)) {
                throw new NotSupportedException("Only positive ScaleTransforms are supported by TransformedBitmap on macOS.");
            }
            var width = (int)Math.Max(1u, (uint)(scale.ScaleX * source.PixelWidth + 0.5));
            var height = (int)Math.Max(1u, (uint)(scale.ScaleY * source.PixelHeight + 0.5));
            var packed = AreaResampler.Resample(source, width, height);
            InitializePixels(source.Format, width, height, source.DpiX, source.DpiY, packed);
        }
    }

    /// <summary>The BeginInit/EndInit state machine of WPF's internal BitmapInitialize.</summary>
    internal sealed class BitmapInitialization {
        private bool inInit;
        private bool completed;

        public void BeginInit() {
            if (completed) {
                throw new InvalidOperationException("Cannot set the initializing state more than once.");
            }
            if (inInit) {
                throw new InvalidOperationException("Cannot have nested BeginInit calls on the same instance.");
            }
            inInit = true;
        }

        public void EndInit() {
            if (!inInit) {
                throw new InvalidOperationException("Must call BeginInit before EndInit.");
            }
            inInit = false;
            completed = true;
        }

        public void SetPrologue() {
            if (!inInit) {
                throw new InvalidOperationException("Can set properties only between BeginInit and EndInit calls.");
            }
        }
    }

    /// <summary>Area-weighted (box) resampling of byte and ushort channel formats.</summary>
    internal static class AreaResampler {

        public static byte[] Resample(BitmapSource source, int width, int height) {
            int channels;
            int bytesPerChannel;
            var format = source.Format;
            if (format == PixelFormats.Gray8) {
                (channels, bytesPerChannel) = (1, 1);
            } else if (format == PixelFormats.Gray16) {
                (channels, bytesPerChannel) = (1, 2);
            } else if (format == PixelFormats.Bgr24) {
                (channels, bytesPerChannel) = (3, 1);
            } else if (format == PixelFormats.Bgr32 || format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32) {
                (channels, bytesPerChannel) = (4, 1);
            } else if (format == PixelFormats.Rgb48) {
                (channels, bytesPerChannel) = (3, 2);
            } else {
                throw new NotSupportedException($"Scaling {format} bitmaps is not supported on macOS.");
            }

            var sourcePixels = source.PackedPixels;
            var sourceStride = source.PackedStride;
            var xs = Weights(source.PixelWidth, width);
            var ys = Weights(source.PixelHeight, height);
            var stride = width * channels * bytesPerChannel;
            var output = new byte[stride * height];
            var sums = new double[channels];
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    Array.Clear(sums);
                    var total = 0.0;
                    foreach (var (sy, wy) in ys[y]) {
                        var rowStart = sy * sourceStride;
                        foreach (var (sx, wx) in xs[x]) {
                            var w = wx * wy;
                            total += w;
                            var pixel = rowStart + sx * channels * bytesPerChannel;
                            for (var c = 0; c < channels; c++) {
                                sums[c] += w * Read(sourcePixels, pixel + c * bytesPerChannel, bytesPerChannel);
                            }
                        }
                    }
                    var target = y * stride + x * channels * bytesPerChannel;
                    for (var c = 0; c < channels; c++) {
                        Write(output, target + c * bytesPerChannel, bytesPerChannel, sums[c] / total);
                    }
                }
            }
            return output;
        }

        /// <summary>For each output index, the source indices it covers and the length of each overlap.</summary>
        private static (int Index, double Weight)[][] Weights(int sourceSize, int targetSize) {
            var result = new (int, double)[targetSize][];
            var ratio = (double)sourceSize / targetSize;
            for (var i = 0; i < targetSize; i++) {
                var start = i * ratio;
                var end = Math.Min(sourceSize, (i + 1) * ratio);
                var first = (int)Math.Floor(start);
                var last = Math.Min(sourceSize - 1, (int)Math.Ceiling(end) - 1);
                var list = new (int, double)[last - first + 1];
                for (var s = first; s <= last; s++) {
                    var overlap = Math.Min(end, s + 1) - Math.Max(start, s);
                    list[s - first] = (s, Math.Max(overlap, 0.0));
                }
                result[i] = list;
            }
            return result;
        }

        private static double Read(byte[] pixels, int offset, int bytesPerChannel) {
            return bytesPerChannel == 1 ? pixels[offset] : BitConverter.ToUInt16(pixels, offset);
        }

        private static void Write(byte[] pixels, int offset, int bytesPerChannel, double value) {
            if (bytesPerChannel == 1) {
                pixels[offset] = (byte)Math.Min(byte.MaxValue, Math.Floor(value + 0.5));
            } else {
                var v = (ushort)Math.Min(ushort.MaxValue, Math.Floor(value + 0.5));
                pixels[offset] = (byte)v;
                pixels[offset + 1] = (byte)(v >> 8);
            }
        }
    }
}
