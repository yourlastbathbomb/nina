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
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NINA.Mac.ImageAnalysis.Test {

    /// <summary>
    /// Accord 3.8.2's SystemTools P/Invokes memcpy/memset from "ntdll.dll" (Windows only). For the test oracle only,
    /// route that name to macOS libSystem, which exports the same C functions. The production library never calls
    /// Accord.
    /// </summary>
    internal static class OracleNativeShim {

        [ModuleInitializer]
        internal static void Install() {
            NativeLibrary.SetDllImportResolver(typeof(Accord.SystemTools).Assembly, Resolve);
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
            if (string.Equals(libraryName, "ntdll.dll", StringComparison.OrdinalIgnoreCase) || string.Equals(libraryName, "ntdll", StringComparison.OrdinalIgnoreCase)) {
                return NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
            }
            return IntPtr.Zero;
        }
    }
}
