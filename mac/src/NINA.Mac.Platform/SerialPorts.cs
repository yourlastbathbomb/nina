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
using System.IO;
using System.Linq;

namespace NINA.Mac.Platform {

    /// <summary>A serial device node. <see cref="IsUsbSerial"/> marks the FTDI-style adapters the LX200 cable uses.</summary>
    public sealed record SerialPortInfo(string Path, bool IsUsbSerial) {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    /// <summary>
    /// Lists /dev/cu.* call-out nodes (the ones to open; tty.* blocks on carrier detect). Upstream discovers ports
    /// through WMI, which does not exist on macOS (research rig_verify_mvp.md MVP-15). USB adapters
    /// (cu.usbserial-*, cu.usbmodem*) sort first so the mount cable is the default. VID/PID matching is future work.
    /// </summary>
    public static class SerialPorts {

        public static IReadOnlyList<SerialPortInfo> List(string devDirectory = "/dev") {
            if (!Directory.Exists(devDirectory)) {
                return Array.Empty<SerialPortInfo>();
            }
            return Directory.EnumerateFileSystemEntries(devDirectory, "cu.*")
                .Select(p => new SerialPortInfo(p, IsUsb(System.IO.Path.GetFileName(p))))
                .OrderByDescending(p => p.IsUsbSerial)
                .ThenBy(p => p.Path, StringComparer.Ordinal)
                .ToArray();
        }

        internal static bool IsUsb(string name) =>
            name.StartsWith("cu.usbserial", StringComparison.Ordinal) || name.StartsWith("cu.usbmodem", StringComparison.Ordinal);
    }
}
