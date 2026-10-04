#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Media.Media3D {

    /// <summary>
    /// Stand-in for PresentationCore's Vector3D (three doubles). NINA.Astrometry returns it from
    /// AstroUtil.Polar3DToCartesian. Equality, hashing and ToString follow WPF, as for <see cref="Point"/>.
    /// Vector arithmetic (Length, Normalize, CrossProduct, operators) and Parse are absent.
    /// </summary>
    public struct Vector3D : IFormattable {
        internal double _x;
        internal double _y;
        internal double _z;

        public Vector3D(double x, double y, double z) {
            _x = x;
            _y = y;
            _z = z;
        }

        public double X {
            get => _x;
            set => _x = value;
        }

        public double Y {
            get => _y;
            set => _y = value;
        }

        public double Z {
            get => _z;
            set => _z = value;
        }

        public static bool operator ==(Vector3D vector1, Vector3D vector2) {
            return vector1.X == vector2.X && vector1.Y == vector2.Y && vector1.Z == vector2.Z;
        }

        public static bool operator !=(Vector3D vector1, Vector3D vector2) {
            return !(vector1 == vector2);
        }

        public static bool Equals(Vector3D vector1, Vector3D vector2) {
            return vector1.X.Equals(vector2.X) && vector1.Y.Equals(vector2.Y) && vector1.Z.Equals(vector2.Z);
        }

        public override bool Equals(object o) {
            return o is Vector3D value && Equals(this, value);
        }

        public bool Equals(Vector3D value) {
            return Equals(this, value);
        }

        public override int GetHashCode() {
            return X.GetHashCode() ^ Y.GetHashCode() ^ Z.GetHashCode();
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
            return string.Format(provider, "{1:" + format + "}{0}{2:" + format + "}{0}{3:" + format + "}", separator, _x, _y, _z);
        }
    }
}
