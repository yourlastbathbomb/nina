#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows {

    /// <summary>
    /// Stand-in for WindowsBase's Int32Rect, the pixel rectangle of BitmapSource.CopyPixels (NINA passes
    /// <see cref="Empty"/>, meaning the whole bitmap). Value semantics follow WPF: an all-zero rectangle is empty,
    /// equality compares the four fields (any two empty rectangles are equal), the hash code is 0 when empty and the
    /// XOR of the fields otherwise, and ToString gives "Empty" or "x,y,width,height" with WPF's numeric list separator.
    /// Parse is absent.
    /// </summary>
    public struct Int32Rect : IFormattable {
        internal int _x;
        internal int _y;
        internal int _width;
        internal int _height;

        public Int32Rect(int x, int y, int width, int height) {
            _x = x;
            _y = y;
            _width = width;
            _height = height;
        }

        public int X {
            get => _x;
            set => _x = value;
        }

        public int Y {
            get => _y;
            set => _y = value;
        }

        public int Width {
            get => _width;
            set => _width = value;
        }

        public int Height {
            get => _height;
            set => _height = value;
        }

        public static Int32Rect Empty => default;

        public bool IsEmpty => _x == 0 && _y == 0 && _width == 0 && _height == 0;

        public bool HasArea => _width > 0 && _height > 0;

        public static bool operator ==(Int32Rect int32Rect1, Int32Rect int32Rect2) {
            return int32Rect1.X == int32Rect2.X && int32Rect1.Y == int32Rect2.Y
                && int32Rect1.Width == int32Rect2.Width && int32Rect1.Height == int32Rect2.Height;
        }

        public static bool operator !=(Int32Rect int32Rect1, Int32Rect int32Rect2) {
            return !(int32Rect1 == int32Rect2);
        }

        public static bool Equals(Int32Rect int32Rect1, Int32Rect int32Rect2) {
            if (int32Rect1.IsEmpty) {
                return int32Rect2.IsEmpty;
            }
            return int32Rect1.X.Equals(int32Rect2.X) && int32Rect1.Y.Equals(int32Rect2.Y)
                && int32Rect1.Width.Equals(int32Rect2.Width) && int32Rect1.Height.Equals(int32Rect2.Height);
        }

        public override bool Equals(object o) {
            return o is Int32Rect value && Equals(this, value);
        }

        public bool Equals(Int32Rect value) {
            return Equals(this, value);
        }

        public override int GetHashCode() {
            if (IsEmpty) {
                return 0;
            }
            return X.GetHashCode() ^ Y.GetHashCode() ^ Width.GetHashCode() ^ Height.GetHashCode();
        }

        public override string ToString() {
            return ConvertToString(null, null);
        }

        public string ToString(IFormatProvider provider) {
            return ConvertToString(null, provider);
        }

        string IFormattable.ToString(string format, IFormatProvider provider) {
            return ConvertToString(format, provider);
        }

        internal string ConvertToString(string format, IFormatProvider provider) {
            if (IsEmpty) {
                return "Empty";
            }
            var separator = NumericList.Separator(provider);
            return string.Format(provider, "{1:" + format + "}{0}{2:" + format + "}{0}{3:" + format + "}{0}{4:" + format + "}",
                separator, _x, _y, _width, _height);
        }
    }
}
