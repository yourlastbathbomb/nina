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

    public enum PowerSourceKind {
        Unknown,
        AC,
        Battery,
        UPS,
    }

    /// <summary>One reading of the Mac's power state.</summary>
    /// <param name="Source">What the Mac is drawing from now.</param>
    /// <param name="HasBattery">An internal battery is present.</param>
    /// <param name="BatteryPercent">Charge in percent (Current/Max Capacity), or null without a battery.</param>
    /// <param name="IsCharging">Battery is charging.</param>
    /// <param name="IsCharged">Battery reports fully charged.</param>
    /// <param name="TimeToEmpty">macOS estimate on battery; null while it is still calculating or on AC.</param>
    public sealed record PowerSourceInfo(PowerSourceKind Source, bool HasBattery, int? BatteryPercent, bool IsCharging, bool IsCharged, TimeSpan? TimeToEmpty) {
        public static PowerSourceInfo Unavailable { get; } = new(PowerSourceKind.Unknown, false, null, false, false, null);

        public bool OnBattery => Source == PowerSourceKind.Battery;

        public string Summary {
            get {
                if (!HasBattery || BatteryPercent == null) {
                    return Source == PowerSourceKind.Unknown ? "Power: unknown" : $"Power: {Describe(Source)}";
                }
                var state = IsCharging ? "charging" : OnBattery ? "on battery" : IsCharged ? "charged" : "on AC";
                var remaining = OnBattery && TimeToEmpty is { } t ? $", {(int)t.TotalHours}:{t.Minutes:00} left" : "";
                return $"Battery {BatteryPercent}% ({state}{remaining})";
            }
        }

        private static string Describe(PowerSourceKind kind) => kind switch {
            PowerSourceKind.AC => "AC",
            PowerSourceKind.Battery => "battery",
            PowerSourceKind.UPS => "UPS",
            _ => "unknown",
        };
    }

    public interface IPowerSource {

        PowerSourceInfo Read();
    }

    /// <summary>Reads IOKit power sources (IOPSCopyPowerSourcesInfo), the same data <c>pmset -g batt</c> prints.</summary>
    public sealed class MacPowerSource : IPowerSource {

        public PowerSourceInfo Read() {
            PlatformGuard.EnsureMacOS();
            var blob = IOKit.IOPSCopyPowerSourcesInfo();
            if (blob == IntPtr.Zero) {
                return PowerSourceInfo.Unavailable;
            }
            try {
                var source = ParseSource(CoreFoundation.ToManagedString(IOKit.IOPSGetProvidingPowerSourceType(blob)));
                var list = IOKit.IOPSCopyPowerSourcesList(blob);
                if (list == IntPtr.Zero) {
                    return PowerSourceInfo.Unavailable with { Source = source };
                }
                try {
                    var count = CoreFoundation.CFArrayGetCount(list);
                    for (nint i = 0; i < count; i++) {
                        var description = IOKit.IOPSGetPowerSourceDescription(blob, CoreFoundation.CFArrayGetValueAtIndex(list, i));
                        if (CoreFoundation.GetString(description, IOKit.kIOPSTypeKey) != IOKit.kIOPSInternalBatteryType) {
                            continue;
                        }
                        if (CoreFoundation.GetBoolean(description, IOKit.kIOPSIsPresentKey) == false) {
                            continue;
                        }
                        var current = CoreFoundation.GetInt64(description, IOKit.kIOPSCurrentCapacityKey);
                        var max = CoreFoundation.GetInt64(description, IOKit.kIOPSMaxCapacityKey);
                        int? percent = current != null && max is > 0 ? (int)Math.Round(100.0 * current.Value / max.Value) : null;
                        var minutesToEmpty = CoreFoundation.GetInt64(description, IOKit.kIOPSTimeToEmptyKey);
                        TimeSpan? toEmpty = minutesToEmpty is > 0 ? TimeSpan.FromMinutes(minutesToEmpty.Value) : null;
                        var batteryState = ParseSource(CoreFoundation.GetString(description, IOKit.kIOPSPowerSourceStateKey));
                        return new PowerSourceInfo(
                            source == PowerSourceKind.Unknown ? batteryState : source,
                            true,
                            percent,
                            CoreFoundation.GetBoolean(description, IOKit.kIOPSIsChargingKey) == true,
                            CoreFoundation.GetBoolean(description, IOKit.kIOPSIsChargedKey) == true,
                            toEmpty);
                    }
                    return PowerSourceInfo.Unavailable with { Source = source };
                } finally {
                    CoreFoundation.CFRelease(list);
                }
            } finally {
                CoreFoundation.CFRelease(blob);
            }
        }

        internal static PowerSourceKind ParseSource(string value) => value switch {
            IOKit.kIOPSACPowerValue => PowerSourceKind.AC,
            IOKit.kIOPSBatteryPowerValue => PowerSourceKind.Battery,
            IOKit.kIOPSUPSPowerValue => PowerSourceKind.UPS,
            _ => PowerSourceKind.Unknown,
        };
    }
}
