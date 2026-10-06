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
    /// Flats (auto-exposure to a target mean, like NINA's AutoExposureFlat), the dark library (only at the cooler
    /// setpoint) and biases. Uses whatever <see cref="ICameraService"/> it is given: the simulator, or the engine camera,
    /// which saves the kept frames through NINA's file patterns (the trial flats are not kept).
    /// </summary>
    public sealed class SimulatedCalibration : ICalibrationService {

        /// <summary>ASI585MC minimum exposure (32 µs), used for biases.</summary>
        public const double BiasExposureSeconds = 0.000032;

        private readonly ICameraService camera;
        private readonly object lockobj = new();
        private bool running;
        private string status = "Idle";
        private double progress;
        private double? lastFlatExposure;

        public SimulatedCalibration(ICameraService camera) {
            this.camera = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        public event EventHandler Changed;

        public bool IsRunning {
            get {
                lock (lockobj) {
                    return running;
                }
            }
        }

        public string Status {
            get {
                lock (lockobj) {
                    return status;
                }
            }
        }

        public double Progress {
            get {
                lock (lockobj) {
                    return progress;
                }
            }
        }

        public double? LastFlatExposure {
            get {
                lock (lockobj) {
                    return lastFlatExposure;
                }
            }
        }

        public Task RunFlatsAsync(CalibrationPlan plan, CancellationToken ct = default) => Run(async () => {
            var exposure = 1.0;
            for (var attempt = 1; attempt <= 8; attempt++) {
                Report($"Flat auto-exposure: trying {exposure:0.###} s", 0);
                // Trial flats (panel on), like AutoExposureFlat; they are not kept
                var test = await camera.ExposeAsync(new ExposureRequest(FrameType.Flat, exposure, plan.Gain, plan.Offset, plan.Bin) { Keep = false }, ct);
                var mean = Math.Max(1e-4, test.MeanAduFraction);
                if (Math.Abs(mean - plan.FlatTargetFraction) <= 0.03) {
                    break;
                }
                exposure = Math.Clamp(exposure * plan.FlatTargetFraction / mean, 0.001, 30);
            }
            lock (lockobj) {
                lastFlatExposure = exposure;
            }
            for (var i = 1; i <= plan.FlatCount; i++) {
                await camera.ExposeAsync(new ExposureRequest(FrameType.Flat, exposure, plan.Gain, plan.Offset, plan.Bin), ct);
                Report($"Flat {i}/{plan.FlatCount} at {exposure:0.###} s", i / (double)plan.FlatCount);
            }
            return $"{plan.FlatCount} flats at {exposure:0.###} s";
        });

        public Task RunDarksAsync(CalibrationPlan plan, CancellationToken ct = default) => Run(async () => {
            if (!camera.CoolerOn || camera.SensorTemperature is not { } t || Math.Abs(t - camera.TargetTemperature) > 1.0) {
                throw new InvalidOperationException($"Darks need the sensor at the cooler setpoint ({camera.TargetTemperature:0} °C); cool the camera first");
            }
            var total = plan.DarkExposures.Count * plan.DarkCount;
            var done = 0;
            foreach (var seconds in plan.DarkExposures) {
                for (var i = 1; i <= plan.DarkCount; i++) {
                    await camera.ExposeAsync(new ExposureRequest(FrameType.Dark, seconds, plan.Gain, plan.Offset, plan.Bin), ct);
                    done++;
                    Report($"Dark {seconds:0.#} s: {i}/{plan.DarkCount}", done / (double)total);
                }
            }
            return $"{total} darks ({string.Join(", ", plan.DarkExposures)} s)";
        });

        public Task RunBiasesAsync(CalibrationPlan plan, CancellationToken ct = default) => Run(async () => {
            for (var i = 1; i <= plan.BiasCount; i++) {
                await camera.ExposeAsync(new ExposureRequest(FrameType.Bias, BiasExposureSeconds, plan.Gain, plan.Offset, plan.Bin), ct);
                Report($"Bias {i}/{plan.BiasCount}", i / (double)plan.BiasCount);
            }
            return $"{plan.BiasCount} biases";
        });

        private async Task Run(Func<Task<string>> body) {
            lock (lockobj) {
                if (running) {
                    throw new InvalidOperationException("Calibration is already running");
                }
                if (camera.State != DeviceConnectionState.Connected) {
                    throw new InvalidOperationException("Connect the camera first");
                }
                running = true;
                progress = 0;
            }
            Changed?.Invoke(this, EventArgs.Empty);
            string final;
            try {
                final = "Done: " + await body();
            } catch (OperationCanceledException) {
                final = "Cancelled";
            } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException) {
                final = "Failed: " + ex.Message;
                lock (lockobj) {
                    running = false;
                    status = final;
                }
                Changed?.Invoke(this, EventArgs.Empty);
                throw;
            }
            lock (lockobj) {
                running = false;
                status = final;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Report(string message, double fraction) {
            lock (lockobj) {
                status = message;
                progress = fraction;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
