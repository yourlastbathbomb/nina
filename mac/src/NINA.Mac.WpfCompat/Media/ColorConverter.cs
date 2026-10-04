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
using System.ComponentModel.Design.Serialization;
using System.Globalization;

namespace System.Windows.Media {

    /// <summary>
    /// Stand-in for WPF's ColorConverter: parses "#RGB", "#ARGB", "#RRGGBB", "#AARRGGBB", "sc#a,r,g,b" / "sc#r,g,b" and the
    /// 141 colour names exactly like WPF. It is also Color's TypeConverter, so Newtonsoft.Json writes Color as a string
    /// as it does on Windows. "ContextColor" (ICC profile) strings are not supported and throw PlatformNotSupportedException.
    /// </summary>
    public sealed class ColorConverter : TypeConverter {

        public override bool CanConvertFrom(ITypeDescriptorContext td, Type t) {
            return t == typeof(string);
        }

        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType) {
            if (destinationType == typeof(InstanceDescriptor)) {
                return true;
            }
            return base.CanConvertTo(context, destinationType);
        }

        public static new object ConvertFromString(string value) {
            if (value == null) {
                return null;
            }
            return Parse(value, null);
        }

        public override object ConvertFrom(ITypeDescriptorContext td, CultureInfo ci, object value) {
            if (value == null) {
                throw GetConvertFromException(value);
            }
            if (value is not string text) {
                throw new ArgumentException($"Expected object of type '{typeof(string)}'.", nameof(value));
            }
            return Parse(text, ci);
        }

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType) {
            ArgumentNullException.ThrowIfNull(destinationType);
            if (value is Color color) {
                if (destinationType == typeof(InstanceDescriptor)) {
                    var fromArgb = typeof(Color).GetMethod(nameof(Color.FromArgb), new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) });
                    return new InstanceDescriptor(fromArgb, new object[] { color.A, color.R, color.G, color.B });
                }
                if (destinationType == typeof(string)) {
                    return color.ToString(culture);
                }
            }
            return base.ConvertTo(context, culture, value, destinationType);
        }

        internal static Color Parse(string color, IFormatProvider formatProvider) {
            var trimmed = color.Trim();
            if ((trimmed.Length == 4 || trimmed.Length == 5 || trimmed.Length == 7 || trimmed.Length == 9) && trimmed[0] == '#') {
                return ParseHexColor(trimmed);
            }
            if (trimmed.StartsWith("ContextColor ", StringComparison.OrdinalIgnoreCase)) {
                throw new PlatformNotSupportedException("ContextColor (ICC profile) colours are not supported by the macOS engine.");
            }
            if (trimmed.StartsWith("sc#", StringComparison.Ordinal)) {
                return ParseScRgbColor(trimmed, formatProvider);
            }
            if (Colors.ByName.TryGetValue(trimmed, out var argb)) {
                return Colors.FromUInt32(argb);
            }
            throw new FormatException("Token is not valid.");
        }

        private static Color ParseHexColor(string trimmedColor) {
            int a = 255, r, g, b;
            if (trimmedColor.Length > 7) {
                a = (ParseHexChar(trimmedColor[1]) * 16) + ParseHexChar(trimmedColor[2]);
                r = (ParseHexChar(trimmedColor[3]) * 16) + ParseHexChar(trimmedColor[4]);
                g = (ParseHexChar(trimmedColor[5]) * 16) + ParseHexChar(trimmedColor[6]);
                b = (ParseHexChar(trimmedColor[7]) * 16) + ParseHexChar(trimmedColor[8]);
            } else if (trimmedColor.Length > 5) {
                r = (ParseHexChar(trimmedColor[1]) * 16) + ParseHexChar(trimmedColor[2]);
                g = (ParseHexChar(trimmedColor[3]) * 16) + ParseHexChar(trimmedColor[4]);
                b = (ParseHexChar(trimmedColor[5]) * 16) + ParseHexChar(trimmedColor[6]);
            } else if (trimmedColor.Length > 4) {
                a = ParseHexChar(trimmedColor[1]);
                a += a * 16;
                r = ParseHexChar(trimmedColor[2]);
                r += r * 16;
                g = ParseHexChar(trimmedColor[3]);
                g += g * 16;
                b = ParseHexChar(trimmedColor[4]);
                b += b * 16;
            } else {
                r = ParseHexChar(trimmedColor[1]);
                r += r * 16;
                g = ParseHexChar(trimmedColor[2]);
                g += g * 16;
                b = ParseHexChar(trimmedColor[3]);
                b += b * 16;
            }
            return Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b);
        }

        private static int ParseHexChar(char c) {
            if (c >= '0' && c <= '9') {
                return c - '0';
            }
            if (c >= 'a' && c <= 'f') {
                return c - 'a' + 10;
            }
            if (c >= 'A' && c <= 'F') {
                return c - 'A' + 10;
            }
            throw new FormatException("Token is not valid.");
        }

        private static Color ParseScRgbColor(string trimmedColor, IFormatProvider formatProvider) {
            var tokenizer = new NumericListTokenizer(trimmedColor.Substring(3), formatProvider);
            var values = new float[4];
            for (var i = 0; i < 3; i++) {
                values[i] = Convert.ToSingle(tokenizer.NextTokenRequired(), formatProvider);
            }
            if (tokenizer.NextToken()) {
                values[3] = Convert.ToSingle(tokenizer.CurrentToken, formatProvider);
                if (tokenizer.NextToken()) {
                    throw new FormatException("Token is not valid.");
                }
                return Color.FromScRgb(values[0], values[1], values[2], values[3]);
            }
            return Color.FromScRgb(1.0f, values[0], values[1], values[2]);
        }

        /// <summary>WPF's numeric-list tokenizer (no quoted tokens): whitespace and/or one list separator between tokens.</summary>
        private sealed class NumericListTokenizer {
            private readonly string text;
            private readonly char separator;
            private int index;

            public NumericListTokenizer(string text, IFormatProvider formatProvider) {
                this.text = text;
                separator = Color.GetNumericListSeparator(formatProvider);
                while (index < text.Length && char.IsWhiteSpace(text, index)) {
                    index++;
                }
            }

            public string CurrentToken { get; private set; }

            public string NextTokenRequired() {
                if (!NextToken()) {
                    throw new InvalidOperationException($"Premature string termination encountered while parsing '{text}'.");
                }
                return CurrentToken;
            }

            public bool NextToken() {
                CurrentToken = null;
                if (index >= text.Length) {
                    return false;
                }
                var start = index;
                while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != separator) {
                    index++;
                }
                var length = index - start;
                ScanToNextToken();
                if (length == 0) {
                    throw new InvalidOperationException($"Empty token encountered at position {index} while parsing '{text}'.");
                }
                CurrentToken = text.Substring(start, length);
                return true;
            }

            private void ScanToNextToken() {
                if (index >= text.Length) {
                    return;
                }
                var separators = 0;
                while (index < text.Length) {
                    var c = text[index];
                    if (c == separator) {
                        separators++;
                        index++;
                        if (separators > 1) {
                            throw new InvalidOperationException($"Empty token encountered at position {index} while parsing '{text}'.");
                        }
                    } else if (char.IsWhiteSpace(c)) {
                        index++;
                    } else {
                        break;
                    }
                }
                if (separators > 0 && index >= text.Length) {
                    throw new InvalidOperationException($"Empty token encountered at position {index} while parsing '{text}'.");
                }
            }
        }
    }
}
