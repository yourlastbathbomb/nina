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

namespace NINA.Mac.RigTools.Astronomy {

    /// <summary>Angle constants, normalisation and sexagesimal parsing/formatting.</summary>
    public static class AngleMath {
        public const double DegToRad = Math.PI / 180.0;
        public const double RadToDeg = 180.0 / Math.PI;

        /// <summary>Arcseconds per radian (180 * 3600 / pi).</summary>
        public const double ArcsecPerRadian = 206264.80624709636;

        /// <summary>Normalises to [0, 360).</summary>
        public static double Normalize360(double degrees) {
            var r = degrees % 360.0;
            if (r < 0) { r += 360.0; }
            if (r >= 360.0) { r -= 360.0; }
            return r;
        }

        /// <summary>Normalises to (-180, 180].</summary>
        public static double NormalizeSigned180(double degrees) {
            var r = Normalize360(degrees);
            return r > 180.0 ? r - 360.0 : r;
        }

        /// <summary>Normalises to [0, 24).</summary>
        public static double Normalize24(double hours) {
            var r = hours % 24.0;
            if (r < 0) { r += 24.0; }
            if (r >= 24.0) { r -= 24.0; }
            return r;
        }

        /// <summary>Normalises to (-12, 12]. Negative hour angles are east of the meridian.</summary>
        public static double NormalizeSigned12(double hours) {
            var r = Normalize24(hours);
            return r > 12.0 ? r - 24.0 : r;
        }

        /// <summary>
        /// Parses decimal ("5.588", "-5.39") or sexagesimal ("05:35:17.3", "05 35 17.3", "-05:23:28",
        /// "05h35m17.3s", "-05d23m28s") values. The sign of the first field applies to the whole value.
        /// </summary>
        public static double ParseSexagesimal(string text) {
            if (string.IsNullOrWhiteSpace(text)) {
                throw new FormatException("Empty angle");
            }
            var s = text.Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain)) {
                return plain;
            }
            var negative = s.StartsWith("-", StringComparison.Ordinal);
            if (negative || s.StartsWith("+", StringComparison.Ordinal)) { s = s.Substring(1); }
            var parts = s.Split(new[] { ':', ' ', 'h', 'H', 'd', 'D', 'm', 'M', 's', 'S', '°', '\'', '"', '′', '″' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || parts.Length > 3) {
                throw new FormatException($"Cannot parse angle '{text}'");
            }
            double value = 0;
            double scale = 1;
            foreach (var part in parts) {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var field) || field < 0) {
                    throw new FormatException($"Cannot parse angle '{text}'");
                }
                value += field / scale;
                scale *= 60.0;
            }
            return negative ? -value : value;
        }

        /// <summary>Formats hours as "05h35m17s".</summary>
        public static string FormatHours(double hours) {
            var totalSeconds = (long)Math.Round(Normalize24(hours) * 3600.0);
            totalSeconds %= 24 * 3600;
            return string.Format(CultureInfo.InvariantCulture, "{0:00}h{1:00}m{2:00}s", totalSeconds / 3600, totalSeconds / 60 % 60, totalSeconds % 60);
        }

        /// <summary>Formats degrees as "-05°23'28\"".</summary>
        public static string FormatDegrees(double degrees) {
            var sign = degrees < 0 ? "-" : "+";
            var totalSeconds = (long)Math.Round(Math.Abs(degrees) * 3600.0);
            return string.Format(CultureInfo.InvariantCulture, "{0}{1:00}°{2:00}'{3:00}\"", sign, totalSeconds / 3600, totalSeconds / 60 % 60, totalSeconds % 60);
        }
    }
}
