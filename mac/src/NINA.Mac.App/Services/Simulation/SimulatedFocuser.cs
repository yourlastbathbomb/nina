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

namespace NINA.Mac.App.Services.Simulation {

    /// <summary>
    /// #1209 timed focuser stand-in. It lives on the mount's aux port, so its connection follows the mount.
    /// A hidden physical position (microns of travel) drives the simulated HFR; the visible position is the
    /// virtual millisecond counter of the real driver.
    /// </summary>
    public sealed class SimulatedFocuser : IFocuserService {
        public const int DefaultMaxStep = 60000;
        public const int MaxSingleMoveMs = 30000;

        /// <summary>Travel per second of motor time at speeds 1-4 (µm/s). Illustrative; M2 measures the real ones.</summary>
        public static readonly double[] TravelMicronsPerSecond = { 0, 5, 25, 100, 400 };

        private readonly IClock clock;
        private readonly IMountService mount;
        private readonly object lockobj = new();
        private int speed;
        private int position;
        private bool moving;
        private double physicalMicrons;

        public SimulatedFocuser(IClock clock, IMountService mount, int speed = 2, double startFocusErrorMicrons = 120) {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.mount = mount ?? throw new ArgumentNullException(nameof(mount));
            if (speed < 1 || speed > 4) {
                throw new ArgumentOutOfRangeException(nameof(speed));
            }
            this.speed = speed;
            position = MaxStep / 2;
            physicalMicrons = startFocusErrorMicrons;
            mount.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }

        public int MaxStep => DefaultMaxStep;

        public event EventHandler Changed;

        public DeviceConnectionState State => mount.State;

        public int Speed {
            get {
                lock (lockobj) {
                    return speed;
                }
            }
        }

        public int Position {
            get {
                lock (lockobj) {
                    return position;
                }
            }
        }

        public bool IsMoving {
            get {
                lock (lockobj) {
                    return moving;
                }
            }
        }

        /// <summary>Distance from best focus in microns (simulation only; drives the camera's HFR).</summary>
        public double FocusErrorMicrons {
            get {
                lock (lockobj) {
                    return physicalMicrons;
                }
            }
        }

        public void SetSpeed(int newSpeed) {
            if (newSpeed < 1 || newSpeed > 4) {
                throw new ArgumentOutOfRangeException(nameof(newSpeed), "Speed is 1 (slowest) to 4");
            }
            lock (lockobj) {
                if (speed == newSpeed) {
                    return;
                }
                speed = newSpeed;
                position = MaxStep / 2;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public async Task MoveAsync(int milliseconds, CancellationToken ct = default) {
            if (milliseconds == 0) {
                return;
            }
            if (Math.Abs(milliseconds) > MaxSingleMoveMs) {
                throw new ArgumentOutOfRangeException(nameof(milliseconds), $"A single move is at most {MaxSingleMoveMs} ms");
            }
            int currentSpeed;
            lock (lockobj) {
                if (mount.State != DeviceConnectionState.Connected) {
                    throw new InvalidOperationException("Focuser is driven through the mount; connect the mount first");
                }
                if (moving) {
                    throw new InvalidOperationException("Focuser is already moving");
                }
                moving = true;
                currentSpeed = speed;
            }
            Changed?.Invoke(this, EventArgs.Empty);
            try {
                await clock.Delay(TimeSpan.FromMilliseconds(Math.Abs(milliseconds)), ct);
                lock (lockobj) {
                    physicalMicrons += milliseconds / 1000.0 * TravelMicronsPerSecond[currentSpeed];
                    position = Math.Clamp(position + milliseconds, 0, MaxStep);
                }
            } finally {
                lock (lockobj) {
                    moving = false;
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void RecenterVirtualPosition() {
            lock (lockobj) {
                position = MaxStep / 2;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
