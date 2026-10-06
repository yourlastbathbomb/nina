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
using NINA.Mac.App.Diagnostics;
using NINA.Mac.App.Services;
using NINA.Mac.Platform;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.ViewModels {

    /// <summary>One preflight line on the Connect screen.</summary>
    public sealed class PreflightLineViewModel {

        public PreflightLineViewModel(PreflightItem item) {
            Item = item;
        }

        public PreflightItem Item { get; }

        public string Status => Item.StatusText;

        public string Name => Item.Name;

        public string Detail => Item.Detail;

        public string Fix => string.IsNullOrWhiteSpace(Item.Fix) ? null : "Fix: " + Item.Fix;

        public bool HasFix => Fix != null;

        public bool IsFail => Item.Status == PreflightStatus.Fail;

        public bool IsWarn => Item.Status == PreflightStatus.Warn;

        public bool IsOk => Item.Status is PreflightStatus.Pass or PreflightStatus.Info;
    }

    /// <summary>
    /// Step 1: camera over USB, mount over the FTDI serial cable, focuser through the mount; the optical train in use; and the
    /// preflight (the same checks as <c>--preflight</c>) to run in daylight or at dusk.
    /// </summary>
    public sealed partial class ConnectViewModel : PageViewModel {

        /// <summary>Below this sensor temperature a disconnect asks for confirmation (condensation risk).</summary>
        public const double ColdDisconnectThreshold = 10.0;

        private readonly AppServices services;
        private bool refreshingPorts;

        public ConnectViewModel(AppServices services) : base(PageKind.Connect, "Connect", "Camera, mount and focuser") {
            this.services = services;
            Camera = new DeviceStatusViewModel("ZWO ASI585MC Pro");
            Mount = new DeviceStatusViewModel("Meade LX200GPS (alt-az)");
            Focuser = new DeviceStatusViewModel("#1209 focuser (through the mount)");
            CoolAfterConnect = true;
            services.Settings.Changed += (_, _) => OnPropertyChanged(nameof(UseReducer));
            services.Settings.Changed += (_, _) => RefreshOptics();
            services.Camera.Changed += (_, _) => Refresh();
            services.Mount.Changed += (_, _) => Refresh();
            Refresh();
        }

        public DeviceStatusViewModel Camera { get; }

        public DeviceStatusViewModel Mount { get; }

        public DeviceStatusViewModel Focuser { get; }

        public ObservableCollection<SerialPortInfo> Ports { get; } = new();

        [ObservableProperty]
        public partial SerialPortInfo SelectedPort { get; set; }

        [ObservableProperty]
        public partial bool CoolAfterConnect { get; set; }

        /// <summary>Set after a first disconnect click on a cold sensor; the second click disconnects.</summary>
        [ObservableProperty]
        public partial string ColdDisconnectWarning { get; set; }

        public double CoolingTarget => services.Settings.Current.CoolingTargetCelsius;

        public string SimulationNote => services.DevicesSimulated
            ? services.SettingsLoadWarning != null
                ? $"{services.SettingsLoadWarning} Devices are simulated until the settings are fixed (Settings › Devices › Device source: Real, Save, restart)."
                : services.EngineError != null
                ? $"Real devices could not start ({services.EngineError}); the simulators run instead. The serial port list is real; nothing is opened."
                : "Devices are simulated (Settings › Devices switches to Real). The serial port list is real; nothing is opened."
            : null;

        public ObservableCollection<PreflightLineViewModel> PreflightLines { get; } = new();

        [ObservableProperty]
        public partial string PreflightSummary { get; set; }

        [ObservableProperty]
        public partial bool IsPreflightRunning { get; set; }

        /// <summary>Also ask the ZWO SDK whether a camera is on USB (only when the camera is not connected here).</summary>
        [ObservableProperty]
        public partial bool PreflightWithDevices { get; set; }

        [ObservableProperty]
        public partial bool PreflightHasFailures { get; set; }

        /// <summary>The last preflight report, for tests and diagnostics.</summary>
        public PreflightReport LastPreflight { get; private set; }

        [ObservableProperty]
        public partial string NativeTrainText { get; set; }

        [ObservableProperty]
        public partial string ReducerTrainText { get; set; }

        /// <summary>
        /// Which optical train is fitted (Settings › Optics › Reducer fitted): it sets the focal length NINA's plate solves, FITS
        /// headers and pixel scale use. Changing it here saves the settings.
        /// </summary>
        public bool UseReducer {
            get => services.Settings.Current.Optics.UseReducer;
            set {
                if (value == services.Settings.Current.Optics.UseReducer) {
                    return;
                }
                var s = services.Settings.Current.Clone();
                s.Optics.UseReducer = value;
                try {
                    services.Settings.Save(s);
                } catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) {
                    ShowError(ex);
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(UseNative));
            }
        }

        public bool UseNative {
            get => !UseReducer;
            set => UseReducer = !value;
        }

        public bool CanStartNight => services.Camera.State != DeviceConnectionState.Connected || services.Mount.State != DeviceConnectionState.Connected;

        private void RefreshOptics() {
            var o = services.Settings.Current.Optics;
            NativeTrainText = TrainText(o, false);
            ReducerTrainText = TrainText(o, true);
            OnPropertyChanged(nameof(UseNative));
        }

        private static string TrainText(OpticsSettings optics, bool reducer) {
            var o = optics.Clone();
            o.UseReducer = reducer;
            var scale1 = 206.264806 * o.PixelSizeMicrons / o.EffectiveFocalLengthMm;
            var heightDeg = o.SensorHeight * scale1 / 3600;
            var name = reducer ? "f/6.3 reducer" : "Native f/10";
            var note = heightDeg < Preflight.AstapD80MinimumFieldDegrees ? " · below ASTAP D80's 0.15° field: solves may fail" : "";
            return string.Create(CultureInfo.InvariantCulture,
                $"{name}, {o.EffectiveFocalLengthMm:0} mm: {o.PixelScaleArcsec:0.00}″/px at bin {o.Bin}, field {o.SensorWidth * scale1 / 3600:0.00}° × {heightDeg:0.00}°{note}");
        }

        /// <summary>Runs the preflight off the UI thread and lists its lines (FAIL first, then WARN, then the rest).</summary>
        [RelayCommand]
        private async Task RunPreflight() {
            if (IsPreflightRunning) {
                return;
            }
            IsPreflightRunning = true;
            PreflightSummary = "Checking…";
            try {
                var context = PreflightRunner.ForServices(services, PreflightWithDevices);
                var report = await Task.Run(() => Preflight.Run(context));
                LastPreflight = report;
                PreflightLines.Clear();
                foreach (var item in report.Items.OrderByDescending(i => i.Status == PreflightStatus.Fail).ThenByDescending(i => i.Status == PreflightStatus.Warn)) {
                    PreflightLines.Add(new PreflightLineViewModel(item));
                }
                PreflightHasFailures = report.Failures > 0;
                PreflightSummary = $"{report.Summary} · {report.RanAt.ToOffset(TimeSpan.FromHours(services.Settings.Current.Site.UtcOffsetHours)):HH:mm}";
            } catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.IOException || ex is UnauthorizedAccessException) {
                PreflightSummary = $"Preflight could not run: {ex.Message}";
            } finally {
                IsPreflightRunning = false;
            }
        }

        public override void Refresh() {
            RefreshOptics();
            Camera.State = services.Camera.State;
            Camera.Detail = services.Camera.Info is { } info
                ? $"{info.Width} × {info.Height}, {info.PixelSizeMicrons} µm, {info.BayerPattern}, bins {string.Join("/", info.Bins)}"
                : services.Camera.LastError ?? "USB 3";
            Mount.State = services.Mount.State;
            Mount.Detail = services.Mount.State == DeviceConnectionState.Connected
                ? $"{services.Mount.PortName ?? "simulator"} · 9600 baud"
                : services.Mount.LastError ?? (services.Mount.PortName ?? "No USB serial adapter found");
            Focuser.State = services.Focuser.State;
            Focuser.Detail = services.Focuser.State == DeviceConnectionState.Connected
                ? $"virtual position {services.Focuser.Position} ms, speed {services.Focuser.Speed}"
                : services.Mount.State == DeviceConnectionState.Connected ? "did not answer through the mount" : "follows the mount";
            RefreshPortList();
            OnPropertyChanged(nameof(CanStartNight));
        }

        partial void OnSelectedPortChanged(SerialPortInfo value) {
            if (refreshingPorts || value == null || value.Path == services.Mount.PortName) {
                return;
            }
            try {
                services.Mount.SelectPort(value.Path);
            } catch (InvalidOperationException ex) {
                ShowError(ex);
            }
        }

        [RelayCommand]
        private void RefreshPorts() {
            services.Mount.RefreshPorts();
        }

        /// <summary>
        /// One click: connect camera and mount together, and start cooling as soon as the camera is connected. The two are
        /// independent: a mount that fails or hangs (no serial adapter in daytime, a hung Autostar) never stops the camera from
        /// cooling, and each failure is reported on its own.
        /// </summary>
        [RelayCommand]
        private async Task StartNight() {
            ErrorMessage = null;
            var mountTask = Begin(() => services.Mount.ConnectAsync());
            var problems = new List<string>();
            try {
                await services.Camera.ConnectAsync();
            } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException || ex is OperationCanceledException) {
                if (ex is not OperationCanceledException) {
                    problems.Add($"Camera: {ex.Message}");
                }
            }
            var cooling = false;
            if (CoolAfterConnect && services.Camera.State == DeviceConnectionState.Connected) {
                try {
                    services.Camera.SetCooler(true, CoolingTarget);
                    cooling = true;
                } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException) {
                    problems.Add($"Cooler: {ex.Message}");
                }
            }
            try {
                await mountTask;
            } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException || ex is OperationCanceledException) {
                if (ex is not OperationCanceledException) {
                    problems.Add($"Mount: {ex.Message}" + (cooling ? " The camera is connected and cooling; connect the mount again with its own Connect button." : ""));
                }
            }
            ErrorMessage = problems.Count == 0 ? null : string.Join("\n", problems);
        }

        /// <summary>Starts an operation, turning a synchronous throw into a faulted task so it is reported like an asynchronous one.</summary>
        private static Task Begin(Func<Task> start) {
            try {
                return start();
            } catch (Exception ex) {
                return Task.FromException(ex);
            }
        }

        [RelayCommand]
        private async Task ConnectCamera() {
            ErrorMessage = null;
            try {
                await services.Camera.ConnectAsync();
            } catch (Exception ex) when (ex is InvalidOperationException || ex is OperationCanceledException) {
                ShowError(ex);
            }
        }

        [RelayCommand]
        private async Task DisconnectCamera() {
            var temperature = services.Camera.SensorTemperature;
            if (ColdDisconnectWarning == null && services.Camera.CoolerOn && temperature is < ColdDisconnectThreshold) {
                ColdDisconnectWarning = $"The sensor is at {temperature:0.0} °C. Warm it first (Cool or Teardown) to avoid condensation, or click Disconnect again.";
                return;
            }
            ColdDisconnectWarning = null;
            await services.Camera.DisconnectAsync();
        }

        [RelayCommand]
        private async Task ConnectMount() {
            ErrorMessage = null;
            try {
                await services.Mount.ConnectAsync();
            } catch (Exception ex) when (ex is InvalidOperationException || ex is OperationCanceledException) {
                ShowError(ex);
            }
        }

        [RelayCommand]
        private async Task DisconnectMount() {
            await services.Mount.DisconnectAsync();
        }

        private void RefreshPortList() {
            var ports = services.Mount.AvailablePorts;
            if (!Ports.SequenceEqual(ports)) {
                refreshingPorts = true;
                try {
                    Ports.Clear();
                    foreach (var p in ports) {
                        Ports.Add(p);
                    }
                } finally {
                    refreshingPorts = false;
                }
            }
            var selected = Ports.FirstOrDefault(p => p.Path == services.Mount.PortName);
            if (!Equals(SelectedPort, selected)) {
                refreshingPorts = true;
                try {
                    SelectedPort = selected;
                } finally {
                    refreshingPorts = false;
                }
            }
        }
    }
}
