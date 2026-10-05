#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Runtime.InteropServices;

namespace NINA.Mac.Platform.Interop {

    /// <summary>CoreGraphics display queries (CGDirectDisplay.h). Display ids are CGDirectDisplayID; boolean_t is a 32-bit int.</summary>
    internal static unsafe partial class CoreGraphics {
        private const string Lib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        public const int kCGErrorSuccess = 0;

        [LibraryImport(Lib)]
        public static partial int CGGetActiveDisplayList(uint maxDisplays, uint* activeDisplays, out uint displayCount);

        [LibraryImport(Lib)]
        public static partial int CGGetOnlineDisplayList(uint maxDisplays, uint* onlineDisplays, out uint displayCount);

        [LibraryImport(Lib)]
        public static partial int CGDisplayIsActive(uint display);

        [LibraryImport(Lib)]
        public static partial int CGDisplayIsAsleep(uint display);

        [LibraryImport(Lib)]
        public static partial int CGDisplayIsMain(uint display);

        [LibraryImport(Lib)]
        public static partial int CGDisplayIsBuiltin(uint display);
    }
}
