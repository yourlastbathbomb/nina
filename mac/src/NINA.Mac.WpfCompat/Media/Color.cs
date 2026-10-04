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
using System.Globalization;
using System.Text;

namespace System.Windows.Media {

    /// <summary>
    /// Stand-in for WPF's Color (sRGB bytes plus scRGB floats, kept in step like WPF).
    /// Behaves like WPF for every member here, including equality (on the scRGB floats), ToString and
    /// DataContractSerializer output (public A/B/G/R/ScA/ScB/ScG/ScR, as stored in NINA .profile files).
    /// ICC colour contexts (FromAValues/FromValues/ColorContext) are not supported and are absent.
    /// GetHashCode is consistent with equality but its values differ from WPF's.
    /// </summary>
    [TypeConverter(typeof(ColorConverter))]
    public struct Color : IFormattable, IEquatable<Color> {
        private byte sA, sR, sG, sB;
        private float scA, scR, scG, scB;
        private bool isFromScRgb;

        public static Color FromScRgb(float a, float r, float g, float b) {
            var color = new Color();
            color.scR = r;
            color.scG = g;
            color.scB = b;
            color.scA = a;
            if (a < 0.0f) {
                a = 0.0f;
            } else if (a > 1.0f) {
                a = 1.0f;
            }
            color.sA = (byte)((a * 255.0f) + 0.5f);
            color.sR = ScRgbTosRgb(color.scR);
            color.sG = ScRgbTosRgb(color.scG);
            color.sB = ScRgbTosRgb(color.scB);
            color.isFromScRgb = true;
            return color;
        }

        public static Color FromArgb(byte a, byte r, byte g, byte b) {
            var color = new Color();
            color.scA = a / 255.0f;
            color.scR = sRgbToScRgb(r);
            color.scG = sRgbToScRgb(g);
            color.scB = sRgbToScRgb(b);
            color.sA = a;
            color.sR = ScRgbTosRgb(color.scR);
            color.sG = ScRgbTosRgb(color.scG);
            color.sB = ScRgbTosRgb(color.scB);
            color.isFromScRgb = false;
            return color;
        }

        public static Color FromRgb(byte r, byte g, byte b) {
            return FromArgb(0xff, r, g, b);
        }

        public byte A {
            get => sA;
            set {
                scA = value / 255.0f;
                sA = value;
            }
        }

        public byte R {
            get => sR;
            set {
                scR = sRgbToScRgb(value);
                sR = value;
            }
        }

        public byte G {
            get => sG;
            set {
                scG = sRgbToScRgb(value);
                sG = value;
            }
        }

        public byte B {
            get => sB;
            set {
                scB = sRgbToScRgb(value);
                sB = value;
            }
        }

        public float ScA {
            get => scA;
            set {
                scA = value;
                if (value < 0.0f) {
                    sA = 0;
                } else if (value > 1.0f) {
                    sA = 255;
                } else {
                    sA = (byte)(value * 255f);
                }
            }
        }

        public float ScR {
            get => scR;
            set {
                scR = value;
                sR = ScRgbTosRgb(value);
            }
        }

        public float ScG {
            get => scG;
            set {
                scG = value;
                sG = ScRgbTosRgb(value);
            }
        }

        public float ScB {
            get => scB;
            set {
                scB = value;
                sB = ScRgbTosRgb(value);
            }
        }

        public override int GetHashCode() {
            return HashCode.Combine(scA, scR, scG, scB);
        }

        public override string ToString() {
            return ConvertToString(isFromScRgb ? "R" : null, null);
        }

        public string ToString(IFormatProvider provider) {
            return ConvertToString(isFromScRgb ? "R" : null, provider);
        }

        string IFormattable.ToString(string format, IFormatProvider provider) {
            return ConvertToString(format, provider);
        }

        internal string ConvertToString(string format, IFormatProvider provider) {
            var sb = new StringBuilder();
            if (format == null) {
                sb.AppendFormat(provider, "#{0:X2}", sA);
                sb.AppendFormat(provider, "{0:X2}", sR);
                sb.AppendFormat(provider, "{0:X2}", sG);
                sb.AppendFormat(provider, "{0:X2}", sB);
            } else {
                var separator = GetNumericListSeparator(provider);
                sb.AppendFormat(provider, "sc#{1:" + format + "}{0} {2:" + format + "}{0} {3:" + format + "}{0} {4:" + format + "}",
                    separator, scA, scR, scG, scB);
            }
            return sb.ToString();
        }

        public static bool Equals(Color color1, Color color2) {
            return color1 == color2;
        }

        public bool Equals(Color color) {
            return this == color;
        }

        public override bool Equals(object o) {
            return o is Color color && this == color;
        }

        public static bool operator ==(Color color1, Color color2) {
            return color1.scR == color2.scR
                && color1.scG == color2.scG
                && color1.scB == color2.scB
                && color1.scA == color2.scA;
        }

        public static bool operator !=(Color color1, Color color2) {
            return !(color1 == color2);
        }

        internal static char GetNumericListSeparator(IFormatProvider provider) {
            var separator = ',';
            var numberFormat = NumberFormatInfo.GetInstance(provider);
            if (numberFormat.NumberDecimalSeparator.Length > 0 && separator == numberFormat.NumberDecimalSeparator[0]) {
                separator = ';';
            }
            return separator;
        }

        private static float sRgbToScRgb(byte bval) {
            var val = bval / 255.0f;
            if (!(val > 0.0)) {
                return 0.0f;
            } else if (val <= 0.04045) {
                return val / 12.92f;
            } else if (val < 1.0f) {
                return (float)Math.Pow((val + 0.055) / 1.055, 2.4);
            } else {
                return 1.0f;
            }
        }

        private static byte ScRgbTosRgb(float val) {
            if (!(val > 0.0)) {
                return 0;
            } else if (val <= 0.0031308) {
                return (byte)((255.0f * val * 12.92f) + 0.5f);
            } else if (val < 1.0) {
                return (byte)((255.0f * ((1.055f * (float)Math.Pow(val, 1.0 / 2.4)) - 0.055f)) + 0.5f);
            } else {
                return 255;
            }
        }
    }
}
