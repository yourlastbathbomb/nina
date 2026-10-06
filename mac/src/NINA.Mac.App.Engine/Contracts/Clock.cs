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
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Services {

    /// <summary>Time source for the simulated devices, so tests and the smoke test can run a night in milliseconds.</summary>
    public interface IClock {

        DateTimeOffset Now { get; }

        Task Delay(TimeSpan delay, CancellationToken ct = default);
    }

    public sealed class SystemClock : IClock {
        public static SystemClock Instance { get; } = new();

        public DateTimeOffset Now => DateTimeOffset.Now;

        public Task Delay(TimeSpan delay, CancellationToken ct = default) => delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
    }

    /// <summary>A clock that only moves when told to. <see cref="Delay"/> advances it and completes at once.</summary>
    public sealed class ManualClock : IClock {
        private readonly object lockobj = new();
        private DateTimeOffset now;

        public ManualClock(DateTimeOffset start) {
            now = start;
        }

        public DateTimeOffset Now {
            get {
                lock (lockobj) {
                    return now;
                }
            }
        }

        public void Advance(TimeSpan by) {
            lock (lockobj) {
                now += by;
            }
        }

        public void Set(DateTimeOffset value) {
            lock (lockobj) {
                now = value;
            }
        }

        public Task Delay(TimeSpan delay, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            if (delay > TimeSpan.Zero) {
                Advance(delay);
            }
            return Task.CompletedTask;
        }
    }
}
