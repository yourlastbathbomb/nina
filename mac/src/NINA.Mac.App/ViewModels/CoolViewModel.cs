#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Mac.App.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.ViewModels {

    /// <summary>Step 2: TEC to the dark-library temperature (0 °C by default) and the warm-up ramp.</summary>
    public sealed partial class CoolViewModel : PageViewModel {
        public const int HistoryLength = 180;
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

        private readonly AppServices services;
        private DateTimeOffset lastSample = DateTimeOffset.MinValue;
        private CancellationTokenSource warmCts;

        public CoolViewModel(AppServices services) : base(PageKind.Cool, "Cool", "Sensor to the dark-library temperature") {
            this.services = services;
            TargetTemperature = services.Settings.Current.CoolingTargetCelsius;
            services.Camera.Changed += (_, _) => Refresh();
            Refresh();
        }

        [ObservableProperty]
        public partial double TargetTemperature { get; set; }

        [ObservableProperty]
        public partial string SensorTemperatureText { get; set; }

        [ObservableProperty]
        public partial double CoolerPower { get; set; }

        [ObservableProperty]
        public partial bool CoolerOn { get; set; }

        [ObservableProperty]
        public partial bool IsConnected { get; set; }

        [ObservableProperty]
        public partial string StatusText { get; set; }

        [ObservableProperty]
        public partial bool IsWarming { get; set; }

        /// <summary>Sensor temperature samples (every 5 s) for the chart.</summary>
        public ObservableCollection<double> History { get; } = new();

        public double WarmupRate => services.Settings.Current.WarmupRateCelsiusPerMinute;

        public override void Refresh() {
            var c = services.Camera;
            IsConnected = c.State == DeviceConnectionState.Connected;
            CoolerOn = c.CoolerOn;
            var t = c.SensorTemperature;
            SensorTemperatureText = t is { } temp ? $"{temp:0.0} °C" : "—";
            CoolerPower = c.CoolerPowerPercent ?? 0;
            StatusText = Describe(c, t);
            var now = services.Clock.Now;
            if (t is { } sample && now - lastSample >= SampleInterval) {
                lastSample = now;
                History.Add(sample);
                while (History.Count > HistoryLength) {
                    History.RemoveAt(0);
                }
            }
        }

        [RelayCommand]
        private void StartCooling() {
            ErrorMessage = null;
            try {
                services.Camera.SetCooler(true, TargetTemperature);
            } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException) {
                ShowError(ex);
            }
        }

        [RelayCommand]
        private async Task WarmUp() {
            ErrorMessage = null;
            warmCts?.Cancel();
            warmCts = new CancellationTokenSource();
            IsWarming = true;
            try {
                await CameraWarmup.WarmAsync(services.Camera, services.Clock, WarmupRate, new InlineProgress<string>(s => StatusText = s), warmCts.Token);
            } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException || ex is OperationCanceledException) {
                ShowError(ex);
            } finally {
                IsWarming = false;
                Refresh();
            }
        }

        [RelayCommand]
        private void StopWarmUp() => warmCts?.Cancel();

        private string Describe(ICameraService c, double? t) {
            if (c.State != DeviceConnectionState.Connected) {
                return "Connect the camera first.";
            }
            if (!c.CoolerOn) {
                return "Cooler off.";
            }
            if (t is not { } temp) {
                return "Cooler on.";
            }
            if (Math.Abs(temp - c.TargetTemperature) <= 0.3) {
                return $"At setpoint {c.TargetTemperature:0.0} °C (cooler {c.CoolerPowerPercent:0} %).";
            }
            var rate = ObservedRatePerMinute();
            var eta = rate is > 0.05 && temp > c.TargetTemperature ? $", about {Math.Ceiling((temp - c.TargetTemperature) / rate.Value):0} min left" : "";
            return $"{(temp > c.TargetTemperature ? "Cooling" : "Warming")} to {c.TargetTemperature:0.0} °C (cooler {c.CoolerPowerPercent:0} %){eta}.";
        }

        /// <summary>Cooling rate seen over the last minute of samples, °C/min (positive when cooling).</summary>
        private double? ObservedRatePerMinute() {
            var samplesPerMinute = (int)(TimeSpan.FromMinutes(1) / SampleInterval);
            if (History.Count <= samplesPerMinute) {
                return null;
            }
            return History[History.Count - 1 - samplesPerMinute] - History[History.Count - 1];
        }
    }
}
