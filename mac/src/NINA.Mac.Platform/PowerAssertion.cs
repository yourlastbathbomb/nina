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
using System.Collections.Generic;

namespace NINA.Mac.Platform {

    /// <summary>The IOKit power assertion types the app uses.</summary>
    public enum PowerAssertionType {

        /// <summary>
        /// kIOPMAssertionTypePreventUserIdleSystemSleep: the Mac does not idle-sleep. Closing the lid on battery
        /// still sleeps it; macOS gives apps no way to override that.
        /// </summary>
        PreventUserIdleSystemSleep,

        /// <summary>kIOPMAssertionTypePreventUserIdleDisplaySleep: the display stays on (implies no idle system sleep).</summary>
        PreventUserIdleDisplaySleep,
    }

    /// <summary>
    /// One IOKit power assertion (IOPMAssertionCreateWithName / IOPMAssertionRelease). It shows up in
    /// <c>pmset -g assertions</c> under this process with its <see cref="Name"/>. Dispose to release.
    /// </summary>
    public sealed class PowerAssertion : IDisposable {
        private readonly object lockobj = new();
        private uint id;
        private bool released;

        private PowerAssertion(PowerAssertionType type, string name, uint id) {
            Type = type;
            Name = name;
            this.id = id;
        }

        public PowerAssertionType Type { get; }

        public string Name { get; }

        /// <summary>The IOPMAssertionID; 0 once released.</summary>
        public uint Id {
            get {
                lock (lockobj) {
                    return released ? 0 : id;
                }
            }
        }

        public bool IsHeld => Id != 0;

        public static string ToIOKitType(PowerAssertionType type) => type switch {
            PowerAssertionType.PreventUserIdleSystemSleep => IOKit.kIOPMAssertionTypePreventUserIdleSystemSleep,
            PowerAssertionType.PreventUserIdleDisplaySleep => IOKit.kIOPMAssertionTypePreventUserIdleDisplaySleep,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        /// <summary>Creates and takes the assertion. Throws <see cref="PlatformServiceException"/> if IOKit refuses.</summary>
        public static PowerAssertion Create(PowerAssertionType type, string name) {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            PlatformGuard.EnsureMacOS();
            using var cfType = CoreFoundation.CreateString(ToIOKitType(type));
            using var cfName = CoreFoundation.CreateString(name);
            var result = IOKit.IOPMAssertionCreateWithName(cfType.Handle, IOKit.kIOPMAssertionLevelOn, cfName.Handle, out var assertionId);
            if (result != IOKit.kIOReturnSuccess || assertionId == 0) {
                throw new PlatformServiceException($"IOPMAssertionCreateWithName({ToIOKitType(type)}) failed: IOReturn 0x{result:X8}");
            }
            return new PowerAssertion(type, name, assertionId);
        }

        public void Dispose() {
            uint toRelease;
            lock (lockobj) {
                if (released) {
                    return;
                }
                released = true;
                toRelease = id;
                id = 0;
            }
            IOKit.IOPMAssertionRelease(toRelease);
        }

        /// <summary>
        /// The power assertions macOS currently lists for <paramref name="pid"/> (IOPMCopyAssertionsByProcess), the
        /// in-process equivalent of reading <c>pmset -g assertions</c>. Includes assertions created on the process's
        /// behalf, such as the one behind an NSProcessInfo activity that disables idle sleep.
        /// </summary>
        public static IReadOnlyList<(string Type, string Name)> ListForProcess(int pid) {
            PlatformGuard.EnsureMacOS();
            var list = new List<(string, string)>();
            if (IOKit.IOPMCopyAssertionsByProcess(out var byPid) != IOKit.kIOReturnSuccess || byPid == IntPtr.Zero) {
                return list;
            }
            try {
                long pidValue = pid;
                var cfPid = CoreFoundationExtra.CFNumberCreate(IntPtr.Zero, CoreFoundation.kCFNumberSInt64Type, ref pidValue);
                try {
                    var assertions = CoreFoundation.CFDictionaryGetValue(byPid, cfPid);
                    if (!CoreFoundation.IsArray(assertions)) {
                        return list;
                    }
                    var count = CoreFoundation.CFArrayGetCount(assertions);
                    for (nint i = 0; i < count; i++) {
                        var entry = CoreFoundation.CFArrayGetValueAtIndex(assertions, i);
                        list.Add((CoreFoundation.GetString(entry, IOKit.kIOPMAssertionTypeKey), CoreFoundation.GetString(entry, IOKit.kIOPMAssertionNameKey)));
                    }
                } finally {
                    CoreFoundation.CFRelease(cfPid);
                }
            } finally {
                CoreFoundation.CFRelease(byPid);
            }
            return list;
        }
    }
}
