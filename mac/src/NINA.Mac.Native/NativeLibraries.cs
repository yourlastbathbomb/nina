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
using System.Reflection;
using System.Runtime.InteropServices;

namespace NINA.Mac.Native {

    /// <summary>
    /// macOS replacement for NINA.Core.Utility.DllLoader. Upstream P/Invokes name Windows DLLs
    /// (e.g. "ASICamera2.dll"); this resolver maps them to the dylibs shipped next to the app
    /// (or in Contents/Frameworks of an .app bundle).
    /// </summary>
    public static class NativeLibraries {

        /// <summary>
        /// DllImport name, without a trailing ".dll", to dylib file name. "X.dll" and "X" both map to the same entry.
        /// </summary>
        private static readonly Dictionary<string, string> dylibByImportName = new(StringComparer.OrdinalIgnoreCase) {
            // ZWO ASI camera SDK (mac/scripts/stage-zwo.sh)
            ["ASICamera2"] = "libASICamera2.dylib",
            // IAU SOFA and USNO NOVAS 3.1 for NINA.Astrometry SOFA.cs / NOVAS.cs (mac/scripts/build-astrometry-natives.sh)
            ["SOFA_2023_10_11"] = "libsofa.dylib",
            ["NOVAS31lib"] = "libnovas31.dylib",
        };

        private static readonly HashSet<Assembly> registered = new();
        private static readonly object lockobj = new();

        /// <summary>Installs the resolver for every P/Invoke declared in <paramref name="assembly"/>. Safe to call repeatedly.</summary>
        public static void Register(Assembly assembly) {
            lock (lockobj) {
                if (registered.Add(assembly)) {
                    NativeLibrary.SetDllImportResolver(assembly, Resolve);
                }
            }
        }

        public static IEnumerable<string> SearchDirectories() {
            var baseDir = AppContext.BaseDirectory;
            yield return baseDir;
            // <App>.app/Contents/MacOS/ -> <App>.app/Contents/Frameworks/
            yield return Path.GetFullPath(Path.Combine(baseDir, "..", "Frameworks"));
        }

        /// <summary>File name of the dylib mapped to <paramref name="importName"/> ("X.dll" or "X"), or null if unmapped.</summary>
        public static string DylibName(string importName) {
            if (importName == null) {
                return null;
            }
            var bareName = importName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? importName[..^4] : importName;
            return dylibByImportName.TryGetValue(bareName, out var fileName) ? fileName : null;
        }

        /// <summary>Full path of the dylib that would be loaded for <paramref name="importName"/>, or null if unmapped or missing.</summary>
        public static string Locate(string importName) {
            var fileName = DylibName(importName);
            if (fileName == null) {
                return null;
            }
            foreach (var dir in SearchDirectories()) {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) {
                    return candidate;
                }
            }
            return null;
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
            var fileName = DylibName(libraryName);
            if (fileName == null) {
                // Not ours: fall back to the runtime's default probing
                return IntPtr.Zero;
            }
            var path = Locate(libraryName);
            if (path == null) {
                throw new DllNotFoundException($"{fileName} (for {libraryName}) not found in: {string.Join(", ", SearchDirectories())}");
            }
            return NativeLibrary.Load(path);
        }
    }
}
