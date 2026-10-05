#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.Platform.Interop;
using System;

namespace NINA.Mac.Platform {

    /// <summary>What Foundation thinks the main bundle is (NSBundle.mainBundle). Used to cross-check <see cref="ResourcePaths"/>.</summary>
    public static class MacBundle {

        /// <summary>-[NSBundle mainBundle].bundlePath: the .app inside a bundle; the executable's folder otherwise.</summary>
        public static string MainBundlePath() => Query("bundlePath");

        /// <summary>-[NSBundle mainBundle].resourcePath: Contents/Resources inside a bundle.</summary>
        public static string MainResourcePath() => Query("resourcePath");

        /// <summary>-[NSBundle mainBundle].bundleIdentifier: CFBundleIdentifier, or null outside a bundle.</summary>
        public static string MainBundleIdentifier() => Query("bundleIdentifier");

        /// <summary>
        /// -[NSBundle mainBundle] objectForInfoDictionaryKey: for a string entry of Info.plist (CFBundleName,
        /// CFBundleExecutable, ...). Null when the key is missing or its value is not a string.
        /// </summary>
        public static string MainBundleInfoString(string key) {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            PlatformGuard.EnsureMacOS();
            return ObjC.WithAutoreleasePool(() => {
                var bundle = ObjC.MsgSend(ObjC.Class("NSBundle"), ObjC.sel_registerName("mainBundle"));
                if (bundle == IntPtr.Zero) {
                    return null;
                }
                using var cfKey = CoreFoundation.CreateString(key); // toll-free bridged to NSString
                var value = ObjC.MsgSend(bundle, ObjC.sel_registerName("objectForInfoDictionaryKey:"), cfKey.Handle);
                return CoreFoundation.ToManagedString(value); // null unless the value is a string
            });
        }

        private static string Query(string selector) {
            PlatformGuard.EnsureMacOS();
            return ObjC.WithAutoreleasePool(() => {
                var bundle = ObjC.MsgSend(ObjC.Class("NSBundle"), ObjC.sel_registerName("mainBundle"));
                if (bundle == IntPtr.Zero) {
                    return null;
                }
                return ObjC.ToManagedString(ObjC.MsgSend(bundle, ObjC.sel_registerName(selector)));
            });
        }
    }
}
