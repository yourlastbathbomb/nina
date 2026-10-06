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
using System.Runtime.InteropServices;

namespace NINA.Mac.Platform {

    /// <summary>
    /// macOS's download quarantine (the <c>com.apple.quarantine</c> extended attribute). Archive Utility copies it onto files it
    /// extracts from a downloaded zip, and macOS then kills an unsigned command-line tool such as ASTAP's <c>astap_cli</c> when
    /// it is started (exit 137), so a solver that "is installed" never solves. Read-only: nothing here removes the attribute.
    /// </summary>
    public static partial class FileQuarantine {
        public const string AttributeName = "com.apple.quarantine";

        /// <summary>The fix the operator types in Terminal.</summary>
        public static string RemoveCommand(string path) => $"xattr -d {AttributeName} \"{path}\"";

        /// <summary>True when <paramref name="path"/> (following symlinks) carries the quarantine attribute; false otherwise or off macOS.</summary>
        public static bool IsQuarantined(string path) {
            if (string.IsNullOrEmpty(path) || !OperatingSystem.IsMacOS()) {
                return false;
            }
            try {
                return getxattr(path, AttributeName, IntPtr.Zero, 0, 0, 0) >= 0;
            } catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) {
                return false;
            }
        }

        // ssize_t getxattr(const char *path, const char *name, void *value, size_t size, u_int32_t position, int options);
        // With value = NULL it returns the attribute's size, or -1 (ENOATTR) when the file has no such attribute.
        [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint getxattr(string path, string name, IntPtr value, nuint size, uint position, int options);
    }
}
