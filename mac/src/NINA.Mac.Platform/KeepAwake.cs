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

namespace NINA.Mac.Platform {

    /// <summary>What to hold while an imaging session is active.</summary>
    public sealed record KeepAwakeOptions(bool PreventSystemSleep = true, bool PreventDisplaySleep = true, bool PreventAppNap = true) {
        public static KeepAwakeOptions Default { get; } = new();
    }

    /// <summary>Snapshot of what is currently held. Shown in the status bar.</summary>
    public sealed record KeepAwakeState(bool SystemSleepPrevented, bool DisplaySleepPrevented, bool AppNapPrevented, string Reason, string Error) {
        public static KeepAwakeState Released { get; } = new(false, false, false, null, null);

        public bool IsEngaged => SystemSleepPrevented || DisplaySleepPrevented || AppNapPrevented;

        public string Summary {
            get {
                if (!IsEngaged) {
                    return Error == null ? "Sleep allowed" : "Sleep allowed (error)";
                }
                var parts = new List<string>();
                if (SystemSleepPrevented) {
                    parts.Add("no idle sleep");
                }
                if (DisplaySleepPrevented) {
                    parts.Add("display on");
                }
                if (AppNapPrevented) {
                    parts.Add("no App Nap");
                }
                return "Awake: " + string.Join(", ", parts) + (Error == null ? "" : " (partial)");
            }
        }
    }

    public interface IKeepAwake : IDisposable {

        KeepAwakeState State { get; }

        event EventHandler StateChanged;

        /// <summary>Takes (or adjusts) the assertions for <paramref name="options"/>. Idempotent. Never throws for macOS refusals; see <see cref="KeepAwakeState.Error"/>.</summary>
        KeepAwakeState Engage(string reason, KeepAwakeOptions options);

        /// <summary>Releases everything. Idempotent.</summary>
        KeepAwakeState Release();
    }

    /// <summary>
    /// Session keep-awake for a laptop at the scope: IOKit assertions against idle system sleep and display sleep,
    /// plus an NSProcessInfo activity against App Nap (MAC_PORT_PLAN.md section 6, "Laptop at the scope").
    /// </summary>
    public sealed class MacKeepAwake : IKeepAwake {
        private readonly object lockobj = new();
        private PowerAssertion systemSleep;
        private PowerAssertion displaySleep;
        private ProcessActivity appNap;
        private string reason;
        private string error;

        public event EventHandler StateChanged;

        public KeepAwakeState State {
            get {
                lock (lockobj) {
                    return Snapshot();
                }
            }
        }

        public KeepAwakeState Engage(string reason, KeepAwakeOptions options) {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            ArgumentNullException.ThrowIfNull(options);
            KeepAwakeState state;
            lock (lockobj) {
                var errors = new List<string>();
                if (reason != this.reason) {
                    // The assertion names carry the reason; recreate them so pmset shows the current one
                    ReleaseAll();
                    this.reason = reason;
                }
                systemSleep = Adjust(systemSleep, options.PreventSystemSleep, () => PowerAssertion.Create(PowerAssertionType.PreventUserIdleSystemSleep, reason), errors);
                displaySleep = Adjust(displaySleep, options.PreventDisplaySleep, () => PowerAssertion.Create(PowerAssertionType.PreventUserIdleDisplaySleep, reason), errors);
                var napOptions = options.PreventSystemSleep ? ActivityOptions.UserInitiated : ActivityOptions.UserInitiatedAllowingIdleSystemSleep;
                if (appNap != null && appNap.Options != napOptions) {
                    appNap.Dispose();
                    appNap = null;
                }
                appNap = Adjust(appNap, options.PreventAppNap, () => ProcessActivity.Begin(napOptions, reason), errors);
                error = errors.Count == 0 ? null : string.Join("; ", errors);
                state = Snapshot();
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            return state;
        }

        public KeepAwakeState Release() {
            KeepAwakeState state;
            lock (lockobj) {
                ReleaseAll();
                reason = null;
                error = null;
                state = Snapshot();
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            return state;
        }

        public void Dispose() {
            lock (lockobj) {
                ReleaseAll();
            }
        }

        private KeepAwakeState Snapshot() => new(systemSleep?.IsHeld == true, displaySleep?.IsHeld == true, appNap?.IsActive == true, reason, error);

        private void ReleaseAll() {
            systemSleep?.Dispose();
            displaySleep?.Dispose();
            appNap?.Dispose();
            systemSleep = null;
            displaySleep = null;
            appNap = null;
        }

        private static T Adjust<T>(T current, bool wanted, Func<T> create, List<string> errors) where T : class, IDisposable {
            if (!wanted) {
                current?.Dispose();
                return null;
            }
            if (current != null) {
                return current;
            }
            try {
                return create();
            } catch (Exception ex) when (ex is PlatformServiceException || ex is PlatformNotSupportedException || ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is InvalidOperationException) {
                errors.Add(ex.Message);
                return null;
            }
        }
    }
}
