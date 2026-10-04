#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.App.Services;
using NINA.Mac.Platform;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Test {

    /// <summary>Keep-awake that records calls instead of taking real IOKit assertions.</summary>
    public sealed class RecordingKeepAwake : IKeepAwake {
        public List<string> Calls { get; } = new();

        public KeepAwakeOptions LastOptions { get; private set; }

        public KeepAwakeState State { get; private set; } = KeepAwakeState.Released;

        public event EventHandler StateChanged;

        public KeepAwakeState Engage(string reason, KeepAwakeOptions options) {
            Calls.Add("engage");
            LastOptions = options;
            State = new KeepAwakeState(options.PreventSystemSleep, options.PreventDisplaySleep, options.PreventAppNap, reason, null);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return State;
        }

        public KeepAwakeState Release() {
            Calls.Add("release");
            State = KeepAwakeState.Released;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return State;
        }

        public void Dispose() {
        }
    }

    public sealed class FakePowerSource : IPowerSource {
        public PowerSourceInfo Next { get; set; } = new(PowerSourceKind.Battery, true, 76, false, false, TimeSpan.FromMinutes(120));

        public PowerSourceInfo Read() => Next;
    }

    /// <summary>
    /// A clock whose delays complete only when the test says so, to interleave events with an operation in flight
    /// (e.g. unplug the camera mid-exposure).
    /// </summary>
    public sealed class GatedClock : IClock {
        private readonly object lockobj = new();
        private readonly List<(TaskCompletionSource Tcs, TimeSpan Delay, CancellationTokenRegistration Reg)> pending = new();
        private DateTimeOffset now;

        public GatedClock(DateTimeOffset start) {
            now = start;
        }

        public DateTimeOffset Now {
            get {
                lock (lockobj) {
                    return now;
                }
            }
        }

        public int Pending {
            get {
                lock (lockobj) {
                    return pending.Count;
                }
            }
        }

        public Task Delay(TimeSpan delay, CancellationToken ct = default) {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            lock (lockobj) {
                pending.Add((tcs, delay, reg));
            }
            return tcs.Task;
        }

        /// <summary>Completes the oldest pending delay and advances time by it.</summary>
        public bool ReleaseOne() {
            (TaskCompletionSource Tcs, TimeSpan Delay, CancellationTokenRegistration Reg) item;
            lock (lockobj) {
                if (pending.Count == 0) {
                    return false;
                }
                item = pending[0];
                pending.RemoveAt(0);
                now += item.Delay;
            }
            item.Reg.Dispose();
            item.Tcs.TrySetResult();
            return true;
        }
    }
}
