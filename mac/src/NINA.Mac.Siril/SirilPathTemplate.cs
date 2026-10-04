#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace NINA.Mac.Siril {

    public sealed class SirilPathTemplateException : Exception {

        public SirilPathTemplateException(string expression, string key, string message) : base(message) {
            Expression = expression;
            Key = key;
        }

        public string Expression { get; }

        /// <summary>The header keyword that could not be resolved, if that was the problem.</summary>
        public string Key { get; }
    }

    /// <summary>
    /// Siril 1.4 "path parsing": <c>$KEY:fmt$</c> tokens in -bias=/-dark=/-flat= (and stack -out=, save) are replaced
    /// with values from a FITS header. For calibrate, the header is the lights sequence's reference frame
    /// (Siril 1.4.4 src/core/command.c:9168-9170, used at :9230/:9254/:9277); if the resulting file does not exist the
    /// command fails and the script stops (verified with siril-cli 1.4.4, see README).
    ///
    /// This is an independent C# re-implementation of the subset of src/io/path_parse.c:310-560 (Siril 1.4.4) the port
    /// needs, so the port can predict the file Siril will look for: numeric <c>%d</c>/<c>%f</c> printf formats (the
    /// value is cast with C <c>(int)</c>, i.e. truncated toward zero, then all whitespace removed), <c>%s</c> (stripped,
    /// whitespace runs to '_', characters forbidden by Siril to '_') and the <c>dmN</c> date formatter (date of the
    /// header date minus N hours). Wildcards, <c>$def...</c> library keywords, <c>dt</c> and RA/Dec formatters are not
    /// supported and throw. Parity with siril-cli is covered by SirilParityTest.
    /// </summary>
    public static class SirilPathTemplate {

        /// <summary>
        /// Master-dark file name template (research rig_verify_siril.md SIR-M1, tested there with siril-cli 1.4.4):
        /// one master per exposure, gain, offset, cooler set-point and binning.
        /// </summary>
        public const string MasterDarkFileName = "dark_$EXPTIME:%d$s_G$GAIN:%d$_O$OFFSET:%d$_T$SET-TEMP:%d$_B$XBINNING:%d$.fit";

        /// <summary>Characters Siril replaces with '_' in %s values (Siril 1.4.4 src/core/utils.c:380).</summary>
        private static readonly char[] ForbiddenInFilename = { '/', '\\', '"', '\'', '?', '%', '*', ':', '|', '<', '>', ';', '=' };

        private static readonly Regex TokenSplit = new(@"\$(.+?)\$", RegexOptions.CultureInvariant);

        private static readonly Regex PrintfSpec = new(@"^%(?<flags>[-+ 0#]*)(?<width>\d*)(?:\.(?<prec>\d*))?(?<conv>[dif])$", RegexOptions.CultureInvariant);

        public static bool HasTokens(string expression) => expression != null && expression.Contains('$');

        /// <summary>Header keywords referenced by <c>$KEY:fmt$</c> tokens, in order of appearance.</summary>
        public static IReadOnlyList<string> GetKeys(string expression) {
            var keys = new List<string>();
            if (!HasTokens(expression)) {
                return keys;
            }
            foreach (var piece in TokenSplit.Split(expression)) {
                var subs = piece.Split(':', 2);
                if (subs.Length == 2 && !(subs[0].Length == 1 && (subs[1].StartsWith('/') || subs[1].StartsWith('\\')))) {
                    var key = subs[0].TrimStart('*');
                    if (key.Length > 8) {
                        key = key.Substring(0, 8);
                    }
                    if (!keys.Contains(key)) {
                        keys.Add(key);
                    }
                }
            }
            return keys;
        }

        public static string Resolve(string expression, FitsHeader header) {
            return Resolve(expression, key => header.TryGetCard(key, out var card) ? card : null);
        }

        /// <summary>Resolves against plain values (double, int, string or DateTime) keyed by FITS keyword.</summary>
        public static string Resolve(string expression, IReadOnlyDictionary<string, object> values) {
            return Resolve(expression, key => values.TryGetValue(key, out var v) && v != null ? ToCard(key, v) : null);
        }

        public static string Resolve(string expression, Func<string, FitsCard> lookup) {
            if (expression == null) {
                throw new ArgumentNullException(nameof(expression));
            }
            if (!HasTokens(expression)) {
                return expression;
            }
            if (expression.StartsWith("$def", StringComparison.Ordinal)) {
                throw new SirilPathTemplateException(expression, null, "Siril library keywords ($defdark, ...) depend on the Siril GUI preferences and are not supported");
            }

            var result = new StringBuilder();
            // Like the C code, every piece of the split (literal or captured) goes through the same token handling
            foreach (var piece in TokenSplit.Split(expression)) {
                if (piece.Length == 0) {
                    continue;
                }
                var subs = piece.Split(':', 2);
                if (subs.Length == 1) {
                    if (subs[0] == "seqname") {
                        throw new SirilPathTemplateException(expression, subs[0], "$seqname$ depends on the loaded sequence and is not supported");
                    }
                    result.Append(subs[0]);
                    continue;
                }
                if (subs[0].Length == 1 && (subs[1].StartsWith('/') || subs[1].StartsWith('\\'))) {
                    result.Append(piece); // Windows drive letter "C:" left alone (path_parse.c:430)
                    continue;
                }
                if (subs[0].StartsWith('*')) {
                    throw new SirilPathTemplateException(expression, subs[0], "Siril wildcards ($*KEY:...$) select files on disk and are not supported");
                }
                var key = subs[0].Length > 8 ? subs[0].Substring(0, 8) : subs[0]; // gchar key[9]
                var format = subs[1];
                var card = lookup(key);
                if (format.StartsWith('%') && (format.EndsWith('d') || format.EndsWith('f'))) {
                    if (card == null) {
                        throw MissingKey(expression, key);
                    }
                    // g_ascii_strtod of the raw value: quoted strings and logicals give 0
                    double value = 0;
                    if (!card.IsString) {
                        FitsHeader.TryParseNumber(card.Value, out value);
                        if (double.IsNaN(value)) {
                            value = 0;
                        }
                    }
                    var formatted = format.EndsWith('d') ? FormatInteger(expression, format, value) : FormatFloat(expression, format, value);
                    result.Append(RemoveWhitespace(formatted));
                } else if (format.StartsWith('%') && format.EndsWith('s')) {
                    if (card == null) {
                        throw MissingKey(expression, key);
                    }
                    if (format != "%s") {
                        throw new SirilPathTemplateException(expression, key, $"Only plain %s is supported, got '{format}'");
                    }
                    // Siril runs the raw card value through g_shell_unquote (path_parse.c:126), so FITS's doubled
                    // quote in 'Thor''s Helmet' closes and reopens a shell quote: the apostrophe disappears
                    var unquoted = ShellUnquote(card.RawValue ?? card.Value);
                    if (unquoted == null) {
                        throw new SirilPathTemplateException(expression, key, $"{key} = {card.RawValue} cannot be unquoted");
                    }
                    result.Append(SanitizeString(unquoted));
                } else if (format.StartsWith("dm", StringComparison.Ordinal)) {
                    if (card == null) {
                        throw MissingKey(expression, key);
                    }
                    if (!TryParseFitsDate(card.Value, out var date)) {
                        throw new SirilPathTemplateException(expression, key, $"{key} = '{card.Value}' is not a FITS date");
                    }
                    var hours = (int)ParseLeadingDouble(format.Substring(2));
                    result.Append(date.AddHours(-hours).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                } else {
                    throw new SirilPathTemplateException(expression, key, $"Unsupported Siril path-parse format '{format}'");
                }
            }
            return result.ToString();
        }

        private static SirilPathTemplateException MissingKey(string expression, string key) {
            // Siril: "Error code: 4 - Key not found: <KEY> - aborting"
            return new SirilPathTemplateException(expression, key, $"Key not found: {key}");
        }

        private static FitsCard ToCard(string key, object value) {
            switch (value) {
                case string s:
                    return new FitsCard(key, "'" + s.Replace("'", "''") + "'", true, s, string.Empty, string.Empty);

                case DateTime d:
                    var text = d.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                    return new FitsCard(key, "'" + text + "'", true, text, string.Empty, string.Empty);

                case IFormattable f:
                    var number = f.ToString(null, CultureInfo.InvariantCulture);
                    return new FitsCard(key, number, false, number, string.Empty, string.Empty);

                default:
                    throw new ArgumentException($"Unsupported value type {value.GetType()} for {key}");
            }
        }

        /// <summary>Siril's %s handling: g_strstrip, whitespace runs to '_', forbidden characters to '_' (path_parse.c:470-490).</summary>
        internal static string SanitizeString(string value) {
            var stripped = (value ?? string.Empty).Trim();
            var sb = new StringBuilder(stripped.Length);
            var inSpace = false;
            foreach (var c in stripped) {
                if (char.IsWhiteSpace(c)) {
                    if (!inSpace) {
                        sb.Append('_');
                    }
                    inSpace = true;
                    continue;
                }
                inSpace = false;
                sb.Append(Array.IndexOf(ForbiddenInFilename, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }

        private static string RemoveWhitespace(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

        /// <summary>GLib g_shell_unquote: '...' literal, "..." with \ escapes for $ ` " \ newline, \ escapes outside quotes. Null on an unmatched quote.</summary>
        internal static string ShellUnquote(string s) {
            var sb = new StringBuilder(s.Length);
            var i = 0;
            while (i < s.Length) {
                var c = s[i];
                if (c == '\'') {
                    var end = s.IndexOf('\'', i + 1);
                    if (end < 0) {
                        return null;
                    }
                    sb.Append(s, i + 1, end - i - 1);
                    i = end + 1;
                } else if (c == '"') {
                    i++;
                    var closed = false;
                    while (i < s.Length) {
                        var d = s[i];
                        if (d == '"') {
                            closed = true;
                            i++;
                            break;
                        }
                        if (d == '\\' && i + 1 < s.Length && "\"\\`$\n".IndexOf(s[i + 1]) >= 0) {
                            if (s[i + 1] != '\n') {
                                sb.Append(s[i + 1]);
                            }
                            i += 2;
                            continue;
                        }
                        sb.Append(d);
                        i++;
                    }
                    if (!closed) {
                        return null;
                    }
                } else if (c == '\\' && i + 1 < s.Length) {
                    if (s[i + 1] != '\n') {
                        sb.Append(s[i + 1]);
                    }
                    i += 2;
                } else {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        private static double ParseLeadingDouble(string s) {
            var end = 0;
            while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.' || (end == 0 && (s[end] == '-' || s[end] == '+')))) {
                end++;
            }
            return double.TryParse(s.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        /// <summary>Siril's FITS_date_to_date_time: "%04d-%02d-%02dT%02d:%02d:%lf" (siril_date.c:201-217).</summary>
        internal static bool TryParseFitsDate(string text, out DateTime value) {
            value = default;
            if (string.IsNullOrEmpty(text)) {
                return false;
            }
            var m = Regex.Match(text, @"^(\d{4})-(\d{2})-(\d{2})T(\d{2})[:-](\d{2})[:-](\d+(?:\.\d*)?)");
            if (!m.Success) {
                return false;
            }
            try {
                var seconds = double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);
                value = new DateTime(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture), 0, DateTimeKind.Unspecified).AddSeconds(seconds);
                return true;
            } catch (ArgumentOutOfRangeException) {
                return false;
            }
        }

        /// <summary>C <c>sprintf(buf, fmt, (int)value)</c> for a %[flags][width][.prec]d spec.</summary>
        internal static string FormatInteger(string expression, string format, double value) {
            var spec = ParseSpec(expression, format);
            if (double.IsNaN(value) || value >= 2147483648.0 || value <= -2147483649.0) {
                throw new SirilPathTemplateException(expression, null, $"Value {value} does not fit a C int");
            }
            var integer = (long)Math.Truncate(value); // C (int) cast truncates toward zero
            var digits = Math.Abs(integer).ToString(CultureInfo.InvariantCulture);
            if (spec.Precision.HasValue) {
                if (spec.Precision.Value == 0 && integer == 0) {
                    digits = string.Empty;
                }
                digits = digits.PadLeft(spec.Precision.Value, '0');
            }
            return Pad(spec, integer < 0, digits, allowZeroPad: !spec.Precision.HasValue);
        }

        /// <summary>C <c>sprintf(buf, fmt, value)</c> for a %[flags][width][.prec]f spec, rounding the exact binary value half-to-even like libc.</summary>
        internal static string FormatFloat(string expression, string format, double value) {
            var spec = ParseSpec(expression, format);
            if (double.IsNaN(value) || double.IsInfinity(value)) {
                return Pad(spec, double.IsNegative(value), double.IsNaN(value) ? "nan" : "inf", allowZeroPad: false);
            }
            var precision = spec.Precision ?? 6;
            var digits = ExactFixed(Math.Abs(value), precision);
            if (precision == 0 && spec.Flags.Contains('#')) {
                digits += ".";
            }
            return Pad(spec, double.IsNegative(value), digits, allowZeroPad: true);
        }

        private sealed class Spec {
            public string Flags = string.Empty;
            public int Width;
            public int? Precision;
        }

        private static Spec ParseSpec(string expression, string format) {
            var m = PrintfSpec.Match(format);
            if (!m.Success) {
                throw new SirilPathTemplateException(expression, null, $"Unsupported printf format '{format}'");
            }
            var spec = new Spec { Flags = m.Groups["flags"].Value };
            if (m.Groups["width"].Value.Length > 0) {
                spec.Width = int.Parse(m.Groups["width"].Value, CultureInfo.InvariantCulture);
            }
            if (m.Groups["prec"].Success) {
                spec.Precision = m.Groups["prec"].Value.Length > 0 ? int.Parse(m.Groups["prec"].Value, CultureInfo.InvariantCulture) : 0;
            }
            return spec;
        }

        private static string Pad(Spec spec, bool negative, string digits, bool allowZeroPad) {
            var sign = negative ? "-" : spec.Flags.Contains('+') ? "+" : spec.Flags.Contains(' ') ? " " : string.Empty;
            var length = sign.Length + digits.Length;
            if (length >= spec.Width) {
                return sign + digits;
            }
            if (spec.Flags.Contains('-')) {
                return sign + digits + new string(' ', spec.Width - length);
            }
            if (spec.Flags.Contains('0') && allowZeroPad) {
                return sign + new string('0', spec.Width - length) + digits;
            }
            return new string(' ', spec.Width - length) + sign + digits;
        }

        /// <summary>Exact decimal expansion of a non-negative double, rounded half-to-even at <paramref name="precision"/> decimals.</summary>
        private static string ExactFixed(double value, int precision) {
            var bits = BitConverter.DoubleToInt64Bits(value);
            var exponent = (int)((bits >> 52) & 0x7FF);
            var mantissa = bits & 0xFFFFFFFFFFFFFL;
            if (exponent == 0) {
                exponent = 1;
            } else {
                mantissa |= 1L << 52;
            }
            exponent -= 1075; // value = mantissa * 2^exponent

            // scaled = value * 10^precision as an exact fraction numerator / denominator
            BigInteger numerator = mantissa;
            BigInteger denominator = BigInteger.One;
            if (exponent > 0) {
                numerator <<= exponent;
            } else {
                denominator <<= -exponent;
            }
            numerator *= BigInteger.Pow(10, precision);
            var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
            var twice = remainder * 2;
            if (twice > denominator || (twice == denominator && !quotient.IsEven)) {
                quotient += 1;
            }
            var text = quotient.ToString(CultureInfo.InvariantCulture);
            if (precision == 0) {
                return text;
            }
            text = text.PadLeft(precision + 1, '0');
            return text.Substring(0, text.Length - precision) + "." + text.Substring(text.Length - precision);
        }
    }
}
