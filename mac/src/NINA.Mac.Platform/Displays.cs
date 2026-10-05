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
using System.Linq;

namespace NINA.Mac.Platform {

    /// <summary>One display as CoreGraphics reports it. An online display that is asleep is not active.</summary>
    public sealed record DisplayInfo(uint Id, bool IsMain, bool IsBuiltin, bool IsActive, bool IsAsleep);

    /// <summary>
    /// Online and active displays (CoreGraphics). The GUI needs an active one: Avalonia.Native starts its render timer
    /// with CVDisplayLinkCreateWithActiveCGDisplays, and AppBuilder.Setup() throws "Avalonia.Native was not able to
    /// start the RenderTimer. Native error code is: -6661" (kCVReturnInvalidArgument) while every display is asleep,
    /// or the lid is closed without an external display. The startup checks use this to say so instead.
    /// </summary>
    public static class MacDisplays {
        private const int MaxDisplays = 32;

        /// <summary>Number of active (awake, drawable) displays.</summary>
        public static unsafe int ActiveCount() {
            PlatformGuard.EnsureMacOS();
            var ids = stackalloc uint[MaxDisplays];
            var error = CoreGraphics.CGGetActiveDisplayList(MaxDisplays, ids, out var count);
            if (error != CoreGraphics.kCGErrorSuccess) {
                throw new PlatformServiceException($"CGGetActiveDisplayList failed: CGError {error}");
            }
            return (int)count;
        }

        /// <summary>Every online display (connected, possibly asleep or mirrored).</summary>
        public static unsafe IReadOnlyList<DisplayInfo> Online() {
            PlatformGuard.EnsureMacOS();
            var ids = stackalloc uint[MaxDisplays];
            var error = CoreGraphics.CGGetOnlineDisplayList(MaxDisplays, ids, out var count);
            if (error != CoreGraphics.kCGErrorSuccess) {
                throw new PlatformServiceException($"CGGetOnlineDisplayList failed: CGError {error}");
            }
            var displays = new List<DisplayInfo>();
            for (var i = 0; i < count; i++) {
                var id = ids[i];
                displays.Add(new DisplayInfo(id, CoreGraphics.CGDisplayIsMain(id) != 0, CoreGraphics.CGDisplayIsBuiltin(id) != 0,
                    CoreGraphics.CGDisplayIsActive(id) != 0, CoreGraphics.CGDisplayIsAsleep(id) != 0));
            }
            return displays;
        }

        /// <summary>Null when a display is active; otherwise why the GUI cannot start now.</summary>
        public static string NoActiveDisplayReason() {
            if (ActiveCount() > 0) {
                return null;
            }
            var online = Online();
            var state = online.Count == 0 ? "no display is online"
                : string.Join(", ", online.Select(d => $"display {d.Id}{(d.IsBuiltin ? " (built-in)" : "")} {(d.IsAsleep ? "asleep" : "inactive")}"));
            return $"no active display ({state}): the display is asleep or the lid is closed without an external display, " +
                "and Avalonia.Native cannot start its render timer then. Wake the display and try again";
        }
    }
}
