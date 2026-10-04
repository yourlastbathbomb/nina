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
    /// Stand-in for WindowsBase's Vector (a 2D displacement). NINA.Astrometry uses it for ViewportFoV.Shift.
    /// Equality, hashing and ToString follow WPF, as for <see cref="Point"/>. Length, arithmetic, Parse and the
    /// Point/Matrix operators are absent.
    /// </summary>
    public struct Vector : IFormattable {
        internal double _x;
        internal double _y;

        public Vector(double x, double y) {
            _x = x;
            _y = y;
        }

        public double X {
            get => _x;
            set => _x = value;
        }

        public double Y {
            get => _y;
            set => _y = value;
        }

        public static bool operator ==(Vector vector1, Vector vector2) {
            return vector1.X == vector2.X && vector1.Y == vector2.Y;
        }

        public static bool operator !=(Vector vector1, Vector vector2) {
            return !(vector1 == vector2);
        }

        public static bool Equals(Vector vector1, Vector vector2) {
            return vector1.X.Equals(vector2.X) && vector1.Y.Equals(vector2.Y);
        }

        public override bool Equals(object o) {
            return o is Vector value && Equals(this, value);
        }

        public bool Equals(Vector value) {
            return Equals(this, value);
        }

        public override int GetHashCode() {
            return X.GetHashCode() ^ Y.GetHashCode();
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
            var separator = NumericList.Separator(provider);
            return string.Format(provider, "{1:" + format + "}{0}{2:" + format + "}", separator, _x, _y);
        }
    }
}
