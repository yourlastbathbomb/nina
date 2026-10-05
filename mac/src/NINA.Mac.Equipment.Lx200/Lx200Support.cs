#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.Lx200;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Mac.Equipment.Lx200 {

    /// <summary>The driver's view of time: the Mac's UTC clock and its UTC offset. Tests pin both.</summary>
    public sealed class Lx200Clock {

        public Lx200Clock(Func<DateTime> utcNow = null, Func<DateTime, TimeSpan> utcOffset = null) {
            UtcNow = utcNow ?? (() => DateTime.UtcNow);
            UtcOffset = utcOffset ?? (utc => TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));
        }

        public static Lx200Clock System { get; } = new Lx200Clock();

        public Func<DateTime> UtcNow { get; }

        /// <summary>Local time minus UTC at a UTC instant (Hong Kong: +8 h).</summary>
        public Func<DateTime, TimeSpan> UtcOffset { get; }
    }

    /// <summary>Firmware and status decoding.</summary>
    public static class Lx200Firmware {

        /// <summary>
        /// StarPatch builds report the firmware letter in upper case ("4.2G"); stock Meade firmware in lower case ("4.2g", "4.2k").
        /// Meade.net's IsStarPatch rule (Telescope.cs:631-660, RVM MNT-M5).
        /// </summary>
        public static bool IsStarPatch(string gvn) {
            if (string.IsNullOrWhiteSpace(gvn)) {
                return false;
            }
            var last = gvn.Trim()[^1];
            return char.IsLetter(last) && char.IsUpper(last);
        }

        /// <summary>Stock 4.2g lacks the 2019 GPS week-rollover fix: the GPS date after Automatic Align can be wrong (RVM MNT-08).</summary>
        public static bool LacksRolloverFix(string gvn) => string.Equals(gvn?.Trim(), "4.2g", StringComparison.Ordinal);

        /// <summary>
        /// ":GW#" reply (undocumented; RVM T1): mount (A alt-az, P polar, G German, L land), tracking (T, or N/S not tracking),
        /// alignment ('0' not aligned, '1'-'3' stars, 'H'/'P'). Null unless it is exactly three characters.
        /// </summary>
        public static (char Mode, bool Tracking, char Alignment)? ParseGw(string value) {
            if (value == null || value.Length != 3) {
                return null;
            }
            return (value[0], value[1] == 'T', value[2]);
        }
    }

    /// <summary>Finds the mount's serial port.</summary>
    public static class Lx200Ports {

        /// <summary>
        /// The configured port, or the only USB serial adapter present (/dev/cu.usbserial-*, /dev/cu.usbmodem*). Throws with a
        /// message for the user when there is none or more than one.
        /// </summary>
        public static string Resolve(string configured, Func<IReadOnlyList<string>> listPorts = null) {
            if (!string.IsNullOrWhiteSpace(configured)) {
                return configured.Trim();
            }
            var usb = (listPorts ?? (() => Lx200Serial.ListCalloutPorts()))().Where(Lx200Serial.IsUsbSerial).ToList();
            return usb.Count switch {
                1 => usb[0],
                0 => throw new InvalidOperationException("No USB serial adapter (/dev/cu.usbserial-*) is connected. Plug in the LX200 cable, or set the port in the LX200 settings."),
                _ => throw new InvalidOperationException($"Several USB serial ports are connected ({string.Join(", ", usb)}); choose the LX200's in the LX200 settings.")
            };
        }
    }
}
