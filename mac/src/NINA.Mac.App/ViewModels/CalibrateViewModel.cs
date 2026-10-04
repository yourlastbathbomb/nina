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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.ViewModels {

    /// <summary>
    /// Step 6: flats from the LED panel (auto-exposure to a target mean), the dark library at the cooler setpoint
    /// and biases, into the night's shared Siril folders (MAC_PORT_PLAN.md decision 7).
    /// </summary>
    public sealed partial class CalibrateViewModel : PageViewModel {
        private readonly AppServices services;
        private CancellationTokenSource cts;

        public CalibrateViewModel(AppServices services) : base(PageKind.Calibrate, "Calibrate", "Flats, dark library, biases") {
            this.services = services;
            FlatCount = 30;
            FlatTargetPercent = 50;
            DarkCount = 20;
            DarkExposuresText = "10, 20, 30";
            BiasCount = 50;
            services.Calibration.Changed += (_, _) => Refresh();
            services.Camera.Changed += (_, _) => RefreshCamera();
            Refresh();
        }

        [ObservableProperty]
        public partial int FlatCount { get; set; }

        [ObservableProperty]
        public partial double FlatTargetPercent { get; set; }

        [ObservableProperty]
        public partial int DarkCount { get; set; }

        [ObservableProperty]
        public partial string DarkExposuresText { get; set; }

        [ObservableProperty]
        public partial int BiasCount { get; set; }

        [ObservableProperty]
        public partial string Status { get; set; }

        [ObservableProperty]
        public partial double Progress { get; set; }

        [ObservableProperty]
        public partial bool IsRunning { get; set; }

        [ObservableProperty]
        public partial string DarkTemperatureNote { get; set; }

        [ObservableProperty]
        public partial string FlatsFolder { get; set; }

        [ObservableProperty]
        public partial string DarksFolder { get; set; }

        [ObservableProperty]
        public partial string BiasesFolder { get; set; }

        public override void Refresh() {
            var c = services.Calibration;
            Status = c.Status;
            Progress = c.Progress;
            IsRunning = c.IsRunning;
            var layout = services.CurrentLayout();
            FlatsFolder = layout.FlatsDirectory;
            DarksFolder = layout.DarksDirectory;
            BiasesFolder = layout.BiasesDirectory;
            RefreshCamera();
        }

        private void RefreshCamera() {
            var cam = services.Camera;
            DarkTemperatureNote = cam.State != DeviceConnectionState.Connected
                ? "Connect the camera first."
                : cam.CoolerOn
                    ? $"Darks are taken at the cooler setpoint ({cam.TargetTemperature:0} °C); sensor now {cam.SensorTemperature:0.0} °C."
                    : "Darks need the cooler on and at its setpoint (Cool screen).";
        }

        /// <summary>Parses "10, 20, 30" into exposures; null when invalid.</summary>
        public static IReadOnlyList<double> ParseExposures(string text) {
            if (string.IsNullOrWhiteSpace(text)) {
                return null;
            }
            var values = new List<double>();
            foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                if (!double.TryParse(part.TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0 || v > 3600) {
                    return null;
                }
                values.Add(v);
            }
            return values.Count == 0 ? null : values.Distinct().ToArray();
        }

        private CalibrationPlan BuildPlan() {
            var exposures = ParseExposures(DarkExposuresText) ?? throw new InvalidOperationException("Dark exposures must be a list of seconds, e.g. \"10, 20, 30\"");
            var s = services.Settings.Current;
            return new CalibrationPlan(FlatCount, FlatTargetPercent / 100.0, DarkCount, exposures, BiasCount, s.Gain, s.Offset, s.Optics.Bin);
        }

        [RelayCommand]
        private Task RunFlats() => Run((plan, ct) => services.Calibration.RunFlatsAsync(plan, ct));

        [RelayCommand]
        private Task RunDarks() => Run((plan, ct) => services.Calibration.RunDarksAsync(plan, ct));

        [RelayCommand]
        private Task RunBiases() => Run((plan, ct) => services.Calibration.RunBiasesAsync(plan, ct));

        [RelayCommand]
        private void Cancel() => cts?.Cancel();

        private async Task Run(Func<CalibrationPlan, CancellationToken, Task> action) {
            ErrorMessage = null;
            cts = new CancellationTokenSource();
            try {
                await action(BuildPlan(), cts.Token);
            } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException || ex is OperationCanceledException) {
                ShowError(ex);
            }
            Refresh();
        }
    }
}
