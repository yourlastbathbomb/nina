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
    /// Stand-in for WindowsBase's Point (a pair of doubles). NINA.Astrometry uses it for sky projections
    /// (Coordinates.XYProjection, ViewportFoV, WorldCoordinateSystem). Equality, hashing and ToString follow WPF:
    /// == compares with ==, Equals with double.Equals (so NaN equals NaN), and ToString separates X and Y with ','
    /// (';' when the culture's decimal separator is ','). Parse, Offset and the Point/Vector/Matrix operators are absent.
    /// </summary>
    public struct Point : IFormattable {
        internal double _x;
        internal double _y;

        public Point(double x, double y) {
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

        public static bool operator ==(Point point1, Point point2) {
            return point1.X == point2.X && point1.Y == point2.Y;
        }

        public static bool operator !=(Point point1, Point point2) {
            return !(point1 == point2);
        }

        public static bool Equals(Point point1, Point point2) {
            return point1.X.Equals(point2.X) && point1.Y.Equals(point2.Y);
        }

        public override bool Equals(object o) {
            return o is Point value && Equals(this, value);
        }

        public bool Equals(Point value) {
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
