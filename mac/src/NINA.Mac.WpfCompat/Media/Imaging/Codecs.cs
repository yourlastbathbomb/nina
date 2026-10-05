#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace System.Windows.Media.Imaging {

    // WPF's image codecs are Windows Imaging Component (WIC) wrappers. NINA.Image uses them for TIFF saving
    // (BaseImageData.SaveTiff) and for loading GIF/TIFF/JPEG/PNG files (BaseImageData.FromFile). On macOS the types exist
    // so that code compiles; the operations that need a codec throw PlatformNotSupportedException: BitmapFrame.Create,
    // BitmapEncoder.Save and every decoder constructor. FITS and XISF, which the macOS engine saves, never reach them.

    internal static class Wic {

        public static PlatformNotSupportedException Unavailable(string what) {
            return new PlatformNotSupportedException($"{what} needs the Windows Imaging Component, which does not exist on macOS. Save and load FITS or XISF instead.");
        }
    }

    /// <summary>
    /// Stand-in for PresentationCore's BitmapMetadata as a value holder: NINA writes the container format, the
    /// application name and a title (its FITS header as text) before a TIFF save. Queries and the other properties are absent.
    /// </summary>
    public class BitmapMetadata : ImageMetadata {
        private string title;
        private string applicationName;

        public BitmapMetadata(string containerFormat) {
            ArgumentNullException.ThrowIfNull(containerFormat, nameof(containerFormat));
            Format = containerFormat;
        }

        public string Format { get; }

        public string Title {
            get => title;
            set {
                WritePreamble();
                title = value;
            }
        }

        public string ApplicationName {
            get => applicationName;
            set {
                WritePreamble();
                applicationName = value;
            }
        }
    }

    /// <summary>Stand-in for PresentationCore's abstract BitmapFrame. <see cref="Create(BitmapSource, BitmapSource, BitmapMetadata, ReadOnlyCollection{ColorContext})"/> throws.</summary>
    public abstract class BitmapFrame : BitmapSource {

        protected BitmapFrame() {
        }

        public static BitmapFrame Create(BitmapSource source, BitmapSource thumbnail, BitmapMetadata metadata, ReadOnlyCollection<ColorContext> colorContexts) {
            throw Wic.Unavailable("BitmapFrame.Create (TIFF saving)");
        }
    }

    /// <summary>Stand-in for PresentationCore's abstract BitmapEncoder: <see cref="Frames"/> is a list, <see cref="Save"/> throws.</summary>
    public abstract class BitmapEncoder {
        private IList<BitmapFrame> frames = new List<BitmapFrame>();

        protected BitmapEncoder() {
        }

        public virtual IList<BitmapFrame> Frames {
            get => frames;
            set {
                ArgumentNullException.ThrowIfNull(value, nameof(value));
                frames = value;
            }
        }

        public virtual void Save(Stream stream) {
            throw Wic.Unavailable($"{GetType().Name}.Save");
        }
    }

    /// <summary>Stand-in for PresentationCore's TiffBitmapEncoder: the compression setting only; saving throws.</summary>
    public sealed class TiffBitmapEncoder : BitmapEncoder {

        public TiffBitmapEncoder() {
        }

        public TiffCompressOption Compression { get; set; }
    }

    /// <summary>WPF's TIFF compression options (same members and values).</summary>
    public enum TiffCompressOption {
        Default = 0,
        None = 1,
        Ccitt3 = 2,
        Ccitt4 = 3,
        Lzw = 4,
        Rle = 5,
        Zip = 6
    }

    /// <summary>Stand-in for PresentationCore's abstract BitmapDecoder. No decoder can be created on macOS.</summary>
    public abstract class BitmapDecoder {

        protected BitmapDecoder() {
        }

        public virtual ReadOnlyCollection<BitmapFrame> Frames => throw Wic.Unavailable($"{GetType().Name}.Frames");
    }

    /// <summary>Stand-in for PresentationCore's GifBitmapDecoder; the constructor throws.</summary>
    public sealed class GifBitmapDecoder : BitmapDecoder {

        public GifBitmapDecoder(Uri bitmapUri, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption) {
            throw Wic.Unavailable("Loading GIF images");
        }
    }

    /// <summary>Stand-in for PresentationCore's TiffBitmapDecoder; the constructor throws.</summary>
    public sealed class TiffBitmapDecoder : BitmapDecoder {

        public TiffBitmapDecoder(Uri bitmapUri, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption) {
            throw Wic.Unavailable("Loading TIFF images");
        }
    }

    /// <summary>Stand-in for PresentationCore's JpegBitmapDecoder; the constructor throws.</summary>
    public sealed class JpegBitmapDecoder : BitmapDecoder {

        public JpegBitmapDecoder(Uri bitmapUri, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption) {
            throw Wic.Unavailable("Loading JPEG images");
        }
    }

    /// <summary>Stand-in for PresentationCore's PngBitmapDecoder; the constructor throws.</summary>
    public sealed class PngBitmapDecoder : BitmapDecoder {

        public PngBitmapDecoder(Uri bitmapUri, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption) {
            throw Wic.Unavailable("Loading PNG images");
        }
    }

    /// <summary>WPF's bitmap creation flags (same members and values).</summary>
    [Flags]
    public enum BitmapCreateOptions {
        None = 0,
        PreservePixelFormat = 1,
        DelayCreation = 2,
        IgnoreColorProfile = 4,
        IgnoreImageCache = 8
    }

    /// <summary>WPF's bitmap cache options (same members and values; Default and OnDemand are both 0).</summary>
    public enum BitmapCacheOption {
        Default = 0,
        OnDemand = 0,
        OnLoad = 1,
        None = 2
    }
}
