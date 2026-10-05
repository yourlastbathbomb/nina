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
    /// Stand-in for PresentationCore's PixelFormat for the predefined formats NINA uses (<see cref="PixelFormats"/>).
    /// As in WPF a format is identified by its WIC pixel-format GUID ({6fddc324-4e03-4bfe-b185-3d77768dc9nn}, nn being
    /// WPF's internal PixelFormatEnum value): equality compares the GUID, the hash code is the GUID's, ToString is the
    /// format name and BitsPerPixel comes from WPF's table. Masks and custom (GUID or string) formats are absent.
    /// </summary>
    public struct PixelFormat : IEquatable<PixelFormat> {
        private readonly byte format;
        private readonly Guid guid;

        internal PixelFormat(PixelFormatId format) {
            this.format = (byte)format;
            guid = new Guid(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, (byte)format);
        }

        public int BitsPerPixel {
            get {
                switch ((PixelFormatId)format) {
                    case PixelFormatId.Indexed8:
                    case PixelFormatId.Gray8:
                        return 8;

                    case PixelFormatId.Bgr565:
                    case PixelFormatId.Gray16:
                        return 16;

                    case PixelFormatId.Bgr24:
                        return 24;

                    case PixelFormatId.Bgr32:
                    case PixelFormatId.Bgra32:
                    case PixelFormatId.Pbgra32:
                        return 32;

                    case PixelFormatId.Rgb48:
                        return 48;

                    default:
                        return 0;
                }
            }
        }

        /// <summary>True for the indexed formats, which WPF only creates together with a palette.</summary>
        internal bool Palettized => (PixelFormatId)format == PixelFormatId.Indexed8;

        public static bool operator ==(PixelFormat left, PixelFormat right) {
            return left.guid == right.guid;
        }

        public static bool operator !=(PixelFormat left, PixelFormat right) {
            return !(left == right);
        }

        public static bool Equals(PixelFormat left, PixelFormat right) {
            return left.guid == right.guid;
        }

        public bool Equals(PixelFormat pixelFormat) {
            return this == pixelFormat;
        }

        public override bool Equals(object obj) {
            return obj is PixelFormat pixelFormat && Equals(pixelFormat);
        }

        public override int GetHashCode() {
            return guid.GetHashCode();
        }

        public override string ToString() {
            return ((PixelFormatId)format).ToString();
        }
    }

    /// <summary>The predefined formats NINA uses, with WPF's names and bit depths.</summary>
    public static class PixelFormats {
        public static PixelFormat Indexed8 => new PixelFormat(PixelFormatId.Indexed8);
        public static PixelFormat Gray8 => new PixelFormat(PixelFormatId.Gray8);
        public static PixelFormat Bgr565 => new PixelFormat(PixelFormatId.Bgr565);
        public static PixelFormat Gray16 => new PixelFormat(PixelFormatId.Gray16);
        public static PixelFormat Bgr24 => new PixelFormat(PixelFormatId.Bgr24);
        public static PixelFormat Bgr32 => new PixelFormat(PixelFormatId.Bgr32);
        public static PixelFormat Bgra32 => new PixelFormat(PixelFormatId.Bgra32);
        public static PixelFormat Pbgra32 => new PixelFormat(PixelFormatId.Pbgra32);
        public static PixelFormat Rgb48 => new PixelFormat(PixelFormatId.Rgb48);
    }

    /// <summary>WPF's internal MS.Internal.PixelFormatEnum values for the formats above (the last byte of the WIC GUID).</summary>
    internal enum PixelFormatId : byte {
        Default = 0,
        Indexed8 = 4,
        Gray8 = 8,
        Bgr565 = 10,
        Gray16 = 11,
        Bgr24 = 12,
        Bgr32 = 14,
        Bgra32 = 15,
        Pbgra32 = 16,
        Rgb48 = 21
    }
}
