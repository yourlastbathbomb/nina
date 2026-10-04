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

    /// <summary>NSActivityOptions (Foundation/NSProcessInfo.h).</summary>
    [Flags]
    public enum ActivityOptions : ulong {
        IdleDisplaySleepDisabled = 1UL << 40,
        IdleSystemSleepDisabled = 1UL << 20,
        SuddenTerminationDisabled = 1UL << 14,
        AutomaticTerminationDisabled = 1UL << 15,

        /// <summary>NSActivityUserInitiated: includes IdleSystemSleepDisabled. Disables App Nap and timer coalescing.</summary>
        UserInitiated = 0x00FFFFFFUL | IdleSystemSleepDisabled,

        /// <summary>NSActivityUserInitiatedAllowingIdleSystemSleep: no App Nap, but idle sleep is still allowed.</summary>
        UserInitiatedAllowingIdleSystemSleep = UserInitiated & ~IdleSystemSleepDisabled,

        Background = 0x000000FFUL,
        LatencyCritical = 0xFF00000000UL,
    }

    /// <summary>
    /// An NSProcessInfo activity (-beginActivityWithOptions:reason: / -endActivity:), Apple's documented way to keep
    /// App Nap from throttling timers and I/O during long work. With <see cref="ActivityOptions.IdleSystemSleepDisabled"/>
    /// macOS also creates a PreventUserIdleSystemSleep assertion named after <see cref="Reason"/>, which is how tests
    /// observe it in <c>pmset -g assertions</c>. Dispose to end the activity.
    /// </summary>
    public sealed class ProcessActivity : IDisposable {
        private readonly object lockobj = new();
        private IntPtr token;

        private ProcessActivity(ActivityOptions options, string reason, IntPtr token) {
            Options = options;
            Reason = reason;
            this.token = token;
        }

        public ActivityOptions Options { get; }

        public string Reason { get; }

        public bool IsActive {
            get {
                lock (lockobj) {
                    return token != IntPtr.Zero;
                }
            }
        }

        public static ProcessActivity Begin(ActivityOptions options, string reason) {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            PlatformGuard.EnsureMacOS();
            var token = ObjC.WithAutoreleasePool(() => {
                var processInfo = ObjC.MsgSend(ObjC.Class("NSProcessInfo"), ObjC.sel_registerName("processInfo"));
                using var cfReason = CoreFoundation.CreateString(reason); // CFString is toll-free bridged to NSString
                var activity = ObjC.MsgSend(processInfo, ObjC.sel_registerName("beginActivityWithOptions:reason:"), (ulong)options, cfReason.Handle);
                // The token is autoreleased (no alloc/new/copy in the selector): retain it beyond the pool.
                return activity == IntPtr.Zero ? IntPtr.Zero : ObjC.MsgSend(activity, ObjC.sel_registerName("retain"));
            });
            if (token == IntPtr.Zero) {
                throw new PlatformServiceException("-[NSProcessInfo beginActivityWithOptions:reason:] returned nil");
            }
            return new ProcessActivity(options, reason, token);
        }

        public void Dispose() {
            IntPtr toEnd;
            lock (lockobj) {
                toEnd = token;
                token = IntPtr.Zero;
            }
            if (toEnd == IntPtr.Zero) {
                return;
            }
            ObjC.WithAutoreleasePool(() => {
                var processInfo = ObjC.MsgSend(ObjC.Class("NSProcessInfo"), ObjC.sel_registerName("processInfo"));
                ObjC.MsgSend(processInfo, ObjC.sel_registerName("endActivity:"), toEnd);
                ObjC.MsgSend(toEnd, ObjC.sel_registerName("release"));
                return 0;
            });
        }
    }
}
