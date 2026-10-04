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

namespace NINA.Mac.Platform {

    /// <summary>A macOS service call failed (IOKit error, nil Objective-C result, ...).</summary>
    public sealed class PlatformServiceException : Exception {

        public PlatformServiceException(string message) : base(message) {
        }

        public PlatformServiceException(string message, Exception inner) : base(message, inner) {
        }
    }

    internal static class PlatformGuard {

        public static void EnsureMacOS() {
            if (!OperatingSystem.IsMacOS()) {
                throw new PlatformNotSupportedException("NINA.Mac.Platform services require macOS");
            }
        }
    }
}
