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
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NINA.Mac.Lx200 {

    /// <summary>Low = "HH:MM.T sDD*MM", High = "HH:MM:SS sDD*MM:SS" (P07 l.897-901, toggled by :U#).</summary>
    public enum CoordinatePrecision {
        Low,
        High
    }

    /// <summary>
    /// Parsers and formatters for LX200 sexagesimal values. Replies are decoded as Latin-1 so the Autostar's
    /// 0xDF degree byte arrives as U+00DF (RIM MNT-10: .NET's default ASCII would turn it into '?').
    /// All formatting is culture-invariant.
    /// </summary>
    public static class Lx200Format {

        /// <summary>The handbox degree glyph that Autostar replies may use instead of '*'.</summary>
        public const byte DegreeByte = 0xDF;

        public const char DegreeChar = 'ß';

        /// <summary>Latin-1 maps every byte to the code point of the same value, so no byte is lost.</summary>
        public static readonly Encoding Latin1 = Encoding.Latin1;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static readonly Regex RaRegex = new(@"^\s*(\d{1,2}):(\d{1,2})(?:\.(\d)|:(\d{1,2}(?:\.\d+)?))\s*$", RegexOptions.CultureInvariant);

        // sign, degrees, separator (* or 0xDF or ':' or U+00B0), minutes, optional (' or :) seconds
        private static readonly Regex DegRegex = new(@"^\s*([+-])?\s*(\d{1,3})[*ß:°](\d{1,2}(?:\.\d+)?)(?:['’:](\d{1,2}(?:\.\d+)?))?\s*$", RegexOptions.CultureInvariant);

        private static readonly Regex TimeRegex = new(@"^\s*(\d{1,2}):(\d{2}):(\d{2})\s*$", RegexOptions.CultureInvariant);

        private static readonly Regex DateRegex = new(@"^\s*(\d{2})/(\d{2})/(\d{2})\s*$", RegexOptions.CultureInvariant);

        private static readonly Regex OffsetRegex = new(@"^\s*([+-])?(\d{1,2}(?:\.\d+)?)\s*$", RegexOptions.CultureInvariant);

        // ---------------------------------------------------------------------------------------------
        // Parsing (input: reply text without the trailing '#')
        // ---------------------------------------------------------------------------------------------

        /// <summary>"HH:MM.T" (low) or "HH:MM:SS" (high) to hours in [0, 24).</summary>
        public static double ParseRaHours(string text) {
            var m = RaRegex.Match(StripHash(text));
            if (!m.Success) {
                throw new FormatException($"Not an LX200 RA: '{Printable(text)}'");
            }
            var h = int.Parse(m.Groups[1].Value, Inv);
            var min = int.Parse(m.Groups[2].Value, Inv);
            if (h > 23 || min > 59) {
                throw new FormatException($"RA out of range: '{Printable(text)}'");
            }
            double hours = h + (min / 60.0);
            if (m.Groups[3].Success) {
                hours += int.Parse(m.Groups[3].Value, Inv) / 600.0;
            } else {
                var s = double.Parse(m.Groups[4].Value, Inv);
                if (s >= 60) {
                    throw new FormatException($"RA seconds out of range: '{Printable(text)}'");
                }
                hours += s / 3600.0;
            }
            return hours;
        }

        /// <summary>Precision of an RA reply: '.' means low (HH:MM.T), a second ':' means high.</summary>
        public static CoordinatePrecision DetectPrecision(string raText) {
            var t = StripHash(raText);
            if (RaRegex.Match(t) is { Success: true } m) {
                return m.Groups[3].Success ? CoordinatePrecision.Low : CoordinatePrecision.High;
            }
            throw new FormatException($"Not an LX200 RA: '{Printable(raText)}'");
        }

        /// <summary>
        /// Signed degrees from "sDD*MM", "sDD*MM'SS", "sDD*MM:SS", "DDD*MM'SS" or "sDDD*MM", with '*', 0xDF or ':'
        /// after the degrees. The sign applies to the whole value, so "-00*30" is -0.5.
        /// </summary>
        public static double ParseDegrees(string text) {
            var m = DegRegex.Match(StripHash(text));
            if (!m.Success) {
                throw new FormatException($"Not an LX200 angle: '{Printable(text)}'");
            }
            var deg = int.Parse(m.Groups[2].Value, Inv);
            var min = double.Parse(m.Groups[3].Value, Inv);
            var sec = m.Groups[4].Success ? double.Parse(m.Groups[4].Value, Inv) : 0;
            if (min >= 60 || sec >= 60) {
                throw new FormatException($"Angle minutes/seconds out of range: '{Printable(text)}'");
            }
            var value = deg + (min / 60.0) + (sec / 3600.0);
            return m.Groups[1].Value == "-" ? -value : value;
        }

        /// <summary>Precision of an angle reply: seconds present means high.</summary>
        public static CoordinatePrecision DetectAnglePrecision(string text) {
            var m = DegRegex.Match(StripHash(text));
            if (!m.Success) {
                throw new FormatException($"Not an LX200 angle: '{Printable(text)}'");
            }
            return m.Groups[4].Success ? CoordinatePrecision.High : CoordinatePrecision.Low;
        }

        /// <summary>
        /// East-positive longitude in (-180, 180] from a :Gg# reply. P07 l.303 says East is negative, so both
        /// "-114*11" and the 0-360 westward "245*49" (MN/IGO practice, RVM MNT-15) mean 114°11' E.
        /// </summary>
        public static double ParseLongitudeEast(string text) => NormalizeLongitude(-ParseDegrees(text));

        /// <summary>
        /// :GG#/:SG value = hours ADDED to local time to give UTC (P07 l.298-301, l.743-744). Hong Kong (UTC+8) is -8.
        /// </summary>
        public static double ParseHoursToUtc(string text) {
            var m = OffsetRegex.Match(StripHash(text));
            if (!m.Success) {
                throw new FormatException($"Not an LX200 UTC offset: '{Printable(text)}'");
            }
            var v = double.Parse(m.Groups[2].Value, Inv);
            return m.Groups[1].Value == "-" ? -v : v;
        }

        /// <summary>"HH:MM:SS" (24 h) to a time of day.</summary>
        public static TimeSpan ParseTime(string text) {
            var m = TimeRegex.Match(StripHash(text));
            if (!m.Success) {
                throw new FormatException($"Not an LX200 time: '{Printable(text)}'");
            }
            int h = int.Parse(m.Groups[1].Value, Inv), mi = int.Parse(m.Groups[2].Value, Inv), s = int.Parse(m.Groups[3].Value, Inv);
            if (h > 23 || mi > 59 || s > 59) {
                throw new FormatException($"Time out of range: '{Printable(text)}'");
            }
            return new TimeSpan(h, mi, s);
        }

        /// <summary>"MM/DD/YY" to a date; two-digit years are 2000-2099.</summary>
        public static DateTime ParseDate(string text) {
            var m = DateRegex.Match(StripHash(text));
            if (!m.Success) {
                throw new FormatException($"Not an LX200 date: '{Printable(text)}'");
            }
            int month = int.Parse(m.Groups[1].Value, Inv), day = int.Parse(m.Groups[2].Value, Inv), year = 2000 + int.Parse(m.Groups[3].Value, Inv);
            if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) {
                throw new FormatException($"Date out of range: '{Printable(text)}'");
            }
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        }

        /// <summary>Which degree glyph a raw reply uses: 0xDF, '*', ':' or none.</summary>
        public static string DescribeDegreeGlyph(ReadOnlySpan<byte> raw) {
            if (raw.IndexOf(DegreeByte) >= 0) {
                return "0xDF";
            }
            if (raw.IndexOf((byte)'*') >= 0) {
                return "'*' (0x2A)";
            }
            return "none";
        }

        // ---------------------------------------------------------------------------------------------
        // Formatting (command arguments). Values are rounded first, then split, so 23:59:59.7 becomes
        // 00:00:00 and 89°59'59.7" becomes 90*00:00 instead of "60" fields.
        // ---------------------------------------------------------------------------------------------

        /// <summary>"HH:MM:SS" (high) or "HH:MM.T" (low), wrapped into [0, 24 h).</summary>
        public static string FormatRa(double hours, CoordinatePrecision precision) {
            if (double.IsNaN(hours) || double.IsInfinity(hours)) {
                throw new ArgumentOutOfRangeException(nameof(hours));
            }
            if (precision == CoordinatePrecision.High) {
                var total = Mod((long)Math.Round(hours * 3600.0, MidpointRounding.AwayFromZero), 86400);
                return string.Create(Inv, $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}");
            }
            var tenths = Mod((long)Math.Round(hours * 600.0, MidpointRounding.AwayFromZero), 14400);
            return string.Create(Inv, $"{tenths / 600:00}:{tenths / 10 % 60:00}.{tenths % 10}");
        }

        /// <summary>"sDD*MM:SS" (high) or "sDD*MM" (low), clamped to ±90°. A value that rounds to zero is "+".</summary>
        public static string FormatDec(double degrees, CoordinatePrecision precision, char degreeChar = '*') =>
            FormatSigned(Math.Clamp(degrees, -90, 90), 2, precision, degreeChar, ':');

        /// <summary>Altitude for ":Sa": "sDD*MM'SS" style (P07 l.672-673 shows the apostrophe form).</summary>
        public static string FormatAltitude(double degrees, CoordinatePrecision precision, char degreeChar = '*') =>
            FormatSigned(Math.Clamp(degrees, -90, 90), 2, precision, degreeChar, '\'');

        /// <summary>Azimuth "DDD*MM:SS" (high) or "DDD*MM" (low), wrapped into [0, 360).</summary>
        public static string FormatAzimuth(double degrees, CoordinatePrecision precision, char degreeChar = '*') {
            if (precision == CoordinatePrecision.High) {
                var total = Mod((long)Math.Round(degrees * 3600.0, MidpointRounding.AwayFromZero), 360 * 3600);
                return string.Create(Inv, $"{total / 3600:000}{degreeChar}{total / 60 % 60:00}:{total % 60:00}");
            }
            var minutes = Mod((long)Math.Round(degrees * 60.0, MidpointRounding.AwayFromZero), 360 * 60);
            return string.Create(Inv, $"{minutes / 60:000}{degreeChar}{minutes % 60:00}");
        }

        /// <summary>":St" argument "sDD*MM" (P07 l.839), e.g. 22.25 gives "+22*15".</summary>
        public static string FormatLatitude(double degrees) => FormatSigned(Math.Clamp(degrees, -90, 90), 2, CoordinatePrecision.Low, '*', ':');

        /// <summary>
        /// ":Sg" argument in the 0-360 westward form that Meade.net and INDIGO send: 114.18 E gives "245*49"
        /// (RVM MNT-15: driver practice, not P07 text).
        /// </summary>
        public static string FormatLongitudeWest360(double eastDegrees) {
            var minutes = Mod((long)Math.Round((360.0 - eastDegrees) * 60.0, MidpointRounding.AwayFromZero), 360 * 60);
            return string.Create(Inv, $"{minutes / 60:000}*{minutes % 60:00}");
        }

        /// <summary>
        /// ":Sg" argument in the signed form with East negative (P07's :Gg# convention; INDI sends this but
        /// truncates the minutes): 114.18 E gives "-114*11".
        /// </summary>
        public static string FormatLongitudeSigned(double eastDegrees) {
            var west = NormalizeLongitude(-eastDegrees);
            var minutes = (long)Math.Round(Math.Abs(west) * 60.0, MidpointRounding.AwayFromZero);
            var sign = west < 0 && minutes > 0 ? "-" : "";
            return string.Create(Inv, $"{sign}{minutes / 60:000}*{minutes % 60:00}");
        }

        /// <summary>
        /// ":SG" argument: hours added to local time to give UTC. <paramref name="utcOffsetHours"/> is the usual
        /// "UTC+8" number; Hong Kong gives "-08" (integer form, IGO) or "-08.0" (P07's sHH.H form).
        /// </summary>
        public static string FormatHoursToUtc(double utcOffsetHours, bool withDecimal) {
            var toUtc = -utcOffsetHours;
            var sign = toUtc < 0 ? "-" : "+";
            var abs = Math.Abs(toUtc);
            if (withDecimal) {
                return string.Create(Inv, $"{sign}{abs:00.0}");
            }
            if (Math.Abs(abs - Math.Round(abs)) > 1e-9) {
                throw new ArgumentException("A fractional UTC offset needs the decimal form", nameof(utcOffsetHours));
            }
            return string.Create(Inv, $"{sign}{(int)Math.Round(abs):00}");
        }

        /// <summary>":SL" argument "HH:MM:SS".</summary>
        public static string FormatTime(TimeSpan timeOfDay) {
            var total = Mod((long)Math.Floor(timeOfDay.TotalSeconds), 86400);
            return string.Create(Inv, $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}");
        }

        /// <summary>":SC" argument "MM/DD/YY".</summary>
        public static string FormatDate(DateTime date) => date.ToString("MM'/'dd'/'yy", Inv);

        /// <summary>":hI" argument "YYMMDDHHMMSS" (P07 l.430).</summary>
        public static string FormatStartupDateTime(DateTime local) => local.ToString("yyMMddHHmmss", Inv);

        /// <summary>":Mg" command; P07 l.544 shows four digits, so 1..9999 ms only.</summary>
        public static string PulseGuideCommand(char direction, int milliseconds) {
            if ("nsew".IndexOf(direction) < 0) {
                throw new ArgumentException("direction must be n, s, e or w", nameof(direction));
            }
            if (milliseconds < 1 || milliseconds > 9999) {
                throw new ArgumentOutOfRangeException(nameof(milliseconds), "P07 :MgnDDDD# allows 1..9999 ms");
            }
            return string.Create(Inv, $":Mg{direction}{milliseconds:0000}#");
        }

        /// <summary>":FP" command, P07 l.194 "sDDDD": signed, at least four digits, +/-65000 ms, positive = inward.</summary>
        public static string FocusPulseCommand(int milliseconds) {
            if (milliseconds == 0 || Math.Abs(milliseconds) > 65000) {
                throw new ArgumentOutOfRangeException(nameof(milliseconds), "P07 :FPsDDDD# allows -65000..65000 ms, not 0");
            }
            return string.Create(Inv, $":FP{(milliseconds > 0 ? "+" : "-")}{Math.Abs(milliseconds):0000}#");
        }

        /// <summary>":Rg" command "SS.S" arcsec/s, at most sidereal 15.0417 (P07 l.664-667).</summary>
        public static string GuideRateCommand(double arcsecPerSecond) {
            if (!(arcsecPerSecond > 0) || arcsecPerSecond > 15.0417) {
                throw new ArgumentOutOfRangeException(nameof(arcsecPerSecond), "P07 :RgSS.S# allows up to 15.0417\"/s");
            }
            return string.Create(Inv, $":Rg{arcsecPerSecond:00.0}#");
        }

        // ---------------------------------------------------------------------------------------------

        /// <summary>Longitude wrapped into (-180, 180].</summary>
        public static double NormalizeLongitude(double degrees) {
            var d = degrees % 360.0;
            if (d <= -180) {
                d += 360;
            } else if (d > 180) {
                d -= 360;
            }
            return d;
        }

        /// <summary>Bytes as printable ASCII: 0x20-0x7E as is, everything else as \xNN.</summary>
        public static string Printable(string text) {
            if (text == null) {
                return "";
            }
            var sb = new StringBuilder(text.Length);
            foreach (var c in text) {
                if (c >= 0x20 && c <= 0x7E) {
                    sb.Append(c);
                } else {
                    sb.Append(string.Create(Inv, $"\\x{(int)c:X2}"));
                }
            }
            return sb.ToString();
        }

        private static string FormatSigned(double degrees, int degreeDigits, CoordinatePrecision precision, char degreeChar, char secondsSeparator) {
            var negative = degrees < 0;
            var abs = Math.Abs(degrees);
            string body;
            long rounded;
            if (precision == CoordinatePrecision.High) {
                rounded = (long)Math.Round(abs * 3600.0, MidpointRounding.AwayFromZero);
                var d = (rounded / 3600).ToString(new string('0', degreeDigits), Inv);
                body = string.Create(Inv, $"{d}{degreeChar}{rounded / 60 % 60:00}{secondsSeparator}{rounded % 60:00}");
            } else {
                rounded = (long)Math.Round(abs * 60.0, MidpointRounding.AwayFromZero);
                var d = (rounded / 60).ToString(new string('0', degreeDigits), Inv);
                body = string.Create(Inv, $"{d}{degreeChar}{rounded % 60:00}");
            }
            return (negative && rounded > 0 ? "-" : "+") + body;
        }

        private static long Mod(long value, long modulus) => ((value % modulus) + modulus) % modulus;

        private static string StripHash(string text) {
            if (text == null) {
                throw new FormatException("No reply text");
            }
            return text.EndsWith('#') ? text[..^1] : text;
        }
    }
}
