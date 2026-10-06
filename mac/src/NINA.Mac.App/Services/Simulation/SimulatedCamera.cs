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
    /// ASI585MC Pro stand-in: TEC cooling with a 35 °C maximum delta, exposures that take (clock) time, HFR that
    /// follows the simulated focuser. Temperature is integrated lazily from the clock, so any time advance counts.
    /// </summary>
    public sealed class SimulatedCamera : ICameraService {
        public const double CoolingRatePerMinute = 4.0;
        public const double PassiveWarmingRatePerMinute = 2.0;
        public const double MaxCoolingDelta = 35.0;

        private static readonly CameraInfo info = new("ZWO ASI585MC Pro (simulated)", 3840, 2160, 2.9, "RGGB", new[] { 1, 2, 3, 4 }, true, MaxCoolingDelta);

        private readonly IClock clock;
        private readonly Func<double> focusErrorMicrons;
        private readonly Random random;
        private readonly object lockobj = new();
        private DeviceConnectionState state = DeviceConnectionState.Disconnected;
        private string lastError;
        private double sensorTemperature;
        private DateTimeOffset lastUpdate;
        private bool coolerOn;
        private double targetTemperature;
        private bool isExposing;
        private double exposureProgress;
        private CancellationTokenSource exposureCts;

        public SimulatedCamera(IClock clock, Func<double> focusErrorMicrons = null, int seed = 585) {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.focusErrorMicrons = focusErrorMicrons ?? (() => 0.0);
            random = new Random(seed);
            sensorTemperature = AmbientCelsius;
            lastUpdate = clock.Now;
        }

        /// <summary>Night-time ambient at the site (Hong Kong, so warm).</summary>
        public double AmbientCelsius { get; set; } = 26.0;

        public TimeSpan ConnectDelay { get; set; } = TimeSpan.FromSeconds(1.5);

        /// <summary>Exposure progress granularity.</summary>
        public TimeSpan ProgressStep { get; set; } = TimeSpan.FromMilliseconds(250);

        public string DisplayName => "Camera";

        public bool IsSimulated => true;

        /// <summary>The simulated sensor never freezes.</summary>
        public string HealthWarning => null;

        public event EventHandler Changed;

        public DeviceConnectionState State {
            get {
                lock (lockobj) {
                    return state;
                }
            }
        }

        public string LastError {
            get {
                lock (lockobj) {
                    return lastError;
                }
            }
        }

        public CameraInfo Info => State == DeviceConnectionState.Connected ? info : null;

        public double? SensorTemperature {
            get {
                lock (lockobj) {
                    UpdateTo(clock.Now);
                    return state == DeviceConnectionState.Connected ? Math.Round(sensorTemperature, 1) : null;
                }
            }
        }

        public double? CoolerPowerPercent {
            get {
                lock (lockobj) {
                    UpdateTo(clock.Now);
                    if (state != DeviceConnectionState.Connected) {
                        return null;
                    }
                    if (!coolerOn) {
                        return 0;
                    }
                    var goal = Math.Max(targetTemperature, AmbientCelsius - MaxCoolingDelta);
                    if (sensorTemperature > goal + 0.3) {
                        return 100;
                    }
                    return Math.Round(Math.Clamp((AmbientCelsius - sensorTemperature) / MaxCoolingDelta * 100.0, 0, 100));
                }
            }
        }

        public bool CoolerOn {
            get {
                lock (lockobj) {
                    return coolerOn && state == DeviceConnectionState.Connected;
                }
            }
        }

        public double TargetTemperature {
            get {
                lock (lockobj) {
                    return targetTemperature;
                }
            }
        }

        public bool IsExposing {
            get {
                lock (lockobj) {
                    return isExposing;
                }
            }
        }

        public double ExposureProgress {
            get {
                lock (lockobj) {
                    return exposureProgress;
                }
            }
        }

        public async Task ConnectAsync(CancellationToken ct = default) {
            lock (lockobj) {
                if (state == DeviceConnectionState.Connected || state == DeviceConnectionState.Connecting) {
                    return;
                }
                state = DeviceConnectionState.Connecting;
                lastError = null;
            }
            RaiseChanged();
            try {
                await clock.Delay(ConnectDelay, ct);
            } catch (OperationCanceledException) {
                SetState(DeviceConnectionState.Disconnected);
                throw;
            }
            lock (lockobj) {
                UpdateTo(clock.Now);
                lastUpdate = clock.Now;
                state = DeviceConnectionState.Connected;
            }
            RaiseChanged();
        }

        public Task DisconnectAsync() {
            lock (lockobj) {
                exposureCts?.Cancel();
                UpdateTo(clock.Now);
                // ZWO cameras keep the TEC state while powered, but a disconnected app can no longer manage it:
                // turn it off like NINA's disconnect does (the Teardown screen warms the sensor first).
                coolerOn = false;
                state = DeviceConnectionState.Disconnected;
                lastError = null;
            }
            RaiseChanged();
            return Task.CompletedTask;
        }

        public void SetCooler(bool on, double targetCelsius) {
            lock (lockobj) {
                EnsureConnected();
                UpdateTo(clock.Now);
                coolerOn = on;
                targetTemperature = targetCelsius;
            }
            RaiseChanged();
        }

        public async Task<FrameResult> ExposeAsync(ExposureRequest request, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Seconds < 0) {
                throw new ArgumentOutOfRangeException(nameof(request), "Exposure must be >= 0");
            }
            CancellationTokenSource linked;
            lock (lockobj) {
                EnsureConnected();
                if (isExposing) {
                    throw new InvalidOperationException("Camera is already exposing");
                }
                isExposing = true;
                exposureProgress = 0;
                exposureCts = new CancellationTokenSource();
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct, exposureCts.Token);
            }
            RaiseChanged();
            try {
                var total = TimeSpan.FromSeconds(request.Seconds);
                var elapsed = TimeSpan.Zero;
                while (elapsed < total) {
                    var step = total - elapsed < ProgressStep ? total - elapsed : ProgressStep;
                    await clock.Delay(step, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    elapsed += step;
                    lock (lockobj) {
                        exposureProgress = elapsed.TotalSeconds / total.TotalSeconds;
                    }
                    RaiseChanged();
                }
                // Download (bin 2 frames are ~4 MB; the ASI585 over USB3 takes well under a second)
                await clock.Delay(TimeSpan.FromMilliseconds(request.Bin >= 2 ? 300 : 900), linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                return MakeFrame(request);
            } catch (OperationCanceledException) when (State == DeviceConnectionState.Lost) {
                throw new DeviceLostException(LastError ?? "Camera connection lost");
            } finally {
                lock (lockobj) {
                    isExposing = false;
                    exposureProgress = 0;
                    exposureCts?.Dispose();
                    exposureCts = null;
                }
                linked.Dispose();
                RaiseChanged();
            }
        }

        /// <summary>Called by the app's 1 Hz timer so temperature readouts refresh.</summary>
        public void Tick() {
            lock (lockobj) {
                UpdateTo(clock.Now);
            }
            RaiseChanged();
        }

        /// <summary>Simulates a USB unplug: state Lost, exposure aborted, cooler state unknown.</summary>
        public void SimulateConnectionLoss(string reason = "USB device disappeared (simulated unplug)") {
            lock (lockobj) {
                if (state != DeviceConnectionState.Connected) {
                    return;
                }
                state = DeviceConnectionState.Lost;
                lastError = reason;
                coolerOn = false;
                exposureCts?.Cancel();
            }
            RaiseChanged();
        }

        private FrameResult MakeFrame(ExposureRequest request) {
            double temperature;
            double noise;
            lock (lockobj) {
                UpdateTo(clock.Now);
                temperature = Math.Round(sensorTemperature, 1);
                noise = 1.0 + ((random.NextDouble() - 0.5) * 0.06);
            }
            var error = focusErrorMicrons();
            var hfr = Math.Sqrt((1.9 * 1.9) + Math.Pow(0.02 * error, 2)) * noise;
            var stars = request.Type == FrameType.Light || request.Type == FrameType.Snapshot ? (int)Math.Round(150 * Math.Pow(1.9 / hfr, 2)) : 0;
            var mean = request.Type switch {
                FrameType.Flat => Math.Min(1.0, request.Seconds * 0.6),
                FrameType.Light or FrameType.Snapshot => Math.Min(1.0, 0.03 + (request.Seconds * 0.004)),
                FrameType.Dark => 0.008 + (request.Seconds * 0.00005),
                _ => 0.008,
            };
            double? bahtinov = request.Type == FrameType.Light || request.Type == FrameType.Snapshot ? Math.Round((error * 0.03) + ((noise - 1.0) * 0.5), 2) : null;
            return new FrameResult(request.Type, request.Seconds, request.Gain, request.Bin, temperature,
                request.Type == FrameType.Light || request.Type == FrameType.Snapshot ? Math.Round(hfr, 2) : 0, stars, mean, bahtinov, clock.Now);
        }

        private void UpdateTo(DateTimeOffset now) {
            var minutes = (now - lastUpdate).TotalMinutes;
            if (minutes <= 0) {
                return;
            }
            lastUpdate = now;
            if (coolerOn && state == DeviceConnectionState.Connected) {
                var goal = Math.Max(targetTemperature, AmbientCelsius - MaxCoolingDelta);
                sensorTemperature = Approach(sensorTemperature, goal, CoolingRatePerMinute * minutes);
            } else {
                sensorTemperature = Approach(sensorTemperature, AmbientCelsius, PassiveWarmingRatePerMinute * minutes);
            }
        }

        private static double Approach(double value, double goal, double maxStep) =>
            Math.Abs(goal - value) <= maxStep ? goal : value + (Math.Sign(goal - value) * maxStep);

        private void EnsureConnected() {
            if (state == DeviceConnectionState.Lost) {
                throw new DeviceLostException(lastError ?? "Camera connection lost");
            }
            if (state != DeviceConnectionState.Connected) {
                throw new InvalidOperationException("Camera is not connected");
            }
        }

        private void SetState(DeviceConnectionState newState) {
            lock (lockobj) {
                state = newState;
            }
            RaiseChanged();
        }

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
