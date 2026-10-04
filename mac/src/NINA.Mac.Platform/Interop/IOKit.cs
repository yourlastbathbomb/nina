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

namespace NINA.Mac.Platform.Interop {

    /// <summary>
    /// IOKit power management (IOPMLib.h) and power sources (IOPowerSources.h / IOPSKeys.h).
    /// </summary>
    internal static partial class IOKit {
        private const string Lib = "/System/Library/Frameworks/IOKit.framework/IOKit";

        public const int kIOReturnSuccess = 0;

        /// <summary>kIOPMAssertionLevelOn</summary>
        public const uint kIOPMAssertionLevelOn = 255;

        // IOPMLib.h assertion type strings
        public const string kIOPMAssertionTypePreventUserIdleSystemSleep = "PreventUserIdleSystemSleep";
        public const string kIOPMAssertionTypePreventUserIdleDisplaySleep = "PreventUserIdleDisplaySleep";
        public const string kIOPMAssertionTypePreventSystemSleep = "PreventSystemSleep";

        // IOPMLib.h assertion dictionary keys (IOPMCopyAssertionsByProcess)
        public const string kIOPMAssertionTypeKey = "AssertType";
        public const string kIOPMAssertionNameKey = "AssertName";

        // IOPSKeys.h
        public const string kIOPSTypeKey = "Type";
        public const string kIOPSInternalBatteryType = "InternalBattery";
        public const string kIOPSCurrentCapacityKey = "Current Capacity";
        public const string kIOPSMaxCapacityKey = "Max Capacity";
        public const string kIOPSIsChargingKey = "Is Charging";
        public const string kIOPSIsChargedKey = "Is Charged";
        public const string kIOPSIsPresentKey = "Is Present";
        public const string kIOPSPowerSourceStateKey = "Power Source State";
        public const string kIOPSTimeToEmptyKey = "Time to Empty";
        public const string kIOPSTimeToFullChargeKey = "Time to Full Charge";
        public const string kIOPSACPowerValue = "AC Power";
        public const string kIOPSBatteryPowerValue = "Battery Power";
        public const string kIOPSUPSPowerValue = "UPS Power";

        /// <summary>IOPSGetTimeRemainingEstimate: still calculating.</summary>
        public const double kIOPSTimeRemainingUnknown = -1.0;

        /// <summary>IOPSGetTimeRemainingEstimate: on external power.</summary>
        public const double kIOPSTimeRemainingUnlimited = -2.0;

        [LibraryImport(Lib)]
        public static partial int IOPMAssertionCreateWithName(IntPtr assertionType, uint assertionLevel, IntPtr assertionName, out uint assertionId);

        [LibraryImport(Lib)]
        public static partial int IOPMAssertionRelease(uint assertionId);

        /// <summary>Returns an owned CFDictionary: pid (CFNumber) to CFArray of assertion dictionaries.</summary>
        [LibraryImport(Lib)]
        public static partial int IOPMCopyAssertionsByProcess(out IntPtr assertionsByPid);

        /// <summary>Owned blob; release with CFRelease.</summary>
        [LibraryImport(Lib)]
        public static partial IntPtr IOPSCopyPowerSourcesInfo();

        /// <summary>Owned CFArray of power source handles; release with CFRelease.</summary>
        [LibraryImport(Lib)]
        public static partial IntPtr IOPSCopyPowerSourcesList(IntPtr blob);

        /// <summary>Borrowed CFDictionary describing <paramref name="powerSource"/>.</summary>
        [LibraryImport(Lib)]
        public static partial IntPtr IOPSGetPowerSourceDescription(IntPtr blob, IntPtr powerSource);

        /// <summary>Borrowed CFString: "AC Power", "Battery Power" or "UPS Power".</summary>
        [LibraryImport(Lib)]
        public static partial IntPtr IOPSGetProvidingPowerSourceType(IntPtr snapshot);

        [LibraryImport(Lib)]
        public static partial double IOPSGetTimeRemainingEstimate();
    }

    internal static partial class CoreFoundationExtra {
        private const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [LibraryImport(Lib)]
        public static partial IntPtr CFNumberCreate(IntPtr allocator, nint theType, ref long value);
    }
}
