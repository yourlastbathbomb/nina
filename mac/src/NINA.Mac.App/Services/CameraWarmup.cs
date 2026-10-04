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

    /// <summary>
    /// IProgress that reports synchronously on the caller's thread. System.Progress posts to the captured context
    /// (or the thread pool), so a late report could overwrite a final status line.
    /// </summary>
    public sealed class InlineProgress<T> : IProgress<T> {
        private readonly Action<T> handler;

        public InlineProgress(Action<T> handler) {
            this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Report(T value) => handler(value);
    }

    /// <summary>
    /// Warm-before-disconnect guard: ramps the cooler setpoint up at a fixed rate, then switches the cooler off,
    /// so the sensor never jumps from 0 °C to a humid 26 °C night (condensation).
    /// </summary>
    public static class CameraWarmup {
        public static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

        /// <summary>Ramp ends here (or as soon as the cooler idles at 0 % power).</summary>
        public const double EndTemperature = 20.0;

        public static async Task WarmAsync(ICameraService camera, IClock clock, double ratePerMinute, IProgress<string> progress = null, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(camera);
            ArgumentNullException.ThrowIfNull(clock);
            if (ratePerMinute <= 0) {
                throw new ArgumentOutOfRangeException(nameof(ratePerMinute));
            }
            if (camera.State != DeviceConnectionState.Connected) {
                throw new InvalidOperationException("Camera is not connected");
            }
            if (!camera.CoolerOn) {
                progress?.Report("Cooler already off");
                return;
            }
            var setpoint = Math.Max(camera.TargetTemperature, camera.SensorTemperature ?? camera.TargetTemperature);
            var increment = ratePerMinute * Step.TotalMinutes;
            while (setpoint < EndTemperature) {
                ct.ThrowIfCancellationRequested();
                setpoint = Math.Min(EndTemperature, setpoint + increment);
                camera.SetCooler(true, setpoint);
                progress?.Report($"Warming: setpoint {setpoint:0.0} °C, sensor {camera.SensorTemperature:0.0} °C");
                await clock.Delay(Step, ct);
                if (camera.CoolerPowerPercent is <= 1) {
                    break;
                }
            }
            camera.SetCooler(false, setpoint);
            progress?.Report($"Cooler off at {camera.SensorTemperature:0.0} °C");
        }
    }
}
