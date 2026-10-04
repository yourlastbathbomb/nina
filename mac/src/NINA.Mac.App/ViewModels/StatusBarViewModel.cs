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
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.ViewModels {

    /// <summary>Bottom bar: device states, sensor temperature/cooler, mount alt/az, battery, keep-awake.</summary>
    public sealed partial class StatusBarViewModel : ViewModelBase {
        private readonly AppServices services;

        public StatusBarViewModel(AppServices services) {
            this.services = services;
            Camera = new DeviceStatusViewModel("Camera");
            Mount = new DeviceStatusViewModel("Mount");
            Focuser = new DeviceStatusViewModel("Focuser");
            services.Camera.Changed += (_, _) => RefreshCamera();
            services.Mount.Changed += (_, _) => RefreshMount();
            services.Focuser.Changed += (_, _) => RefreshFocuser();
            services.Power.Changed += (_, _) => RefreshPower();
            services.KeepAwake.Changed += (_, _) => RefreshKeepAwake();
            services.Session.Changed += (_, _) => RefreshSession();
            Refresh();
        }

        public DeviceStatusViewModel Camera { get; }

        public DeviceStatusViewModel Mount { get; }

        public DeviceStatusViewModel Focuser { get; }

        [ObservableProperty]
        public partial string PowerText { get; set; }

        [ObservableProperty]
        public partial bool IsPowerLow { get; set; }

        [ObservableProperty]
        public partial string KeepAwakeText { get; set; }

        [ObservableProperty]
        public partial bool IsKeepAwakeEngaged { get; set; }

        [ObservableProperty]
        public partial string SessionText { get; set; }

        public string SimulationNote => services.DevicesSimulated ? "Simulated devices" : null;

        public void Refresh() {
            RefreshCamera();
            RefreshMount();
            RefreshFocuser();
            RefreshPower();
            RefreshKeepAwake();
            RefreshSession();
        }

        private void RefreshCamera() {
            var c = services.Camera;
            Camera.State = c.State;
            Camera.Detail = c.State == DeviceConnectionState.Connected
                ? $"{Format(c.SensorTemperature, "0.0")} °C · cooler {(c.CoolerOn ? $"{Format(c.CoolerPowerPercent, "0")} %" : "off")}"
                : c.State == DeviceConnectionState.Lost ? "lost" : "—";
        }

        private void RefreshMount() {
            var m = services.Mount;
            Mount.State = m.State;
            Mount.Detail = m.State == DeviceConnectionState.Connected
                ? $"Alt {Format(m.Altitude, "0.0")}° Az {Format(m.Azimuth, "0.0")}°{(m.IsSlewing ? " · slewing" : m.IsTracking ? "" : " · not tracking")}"
                : m.State == DeviceConnectionState.Lost ? "lost" : "—";
        }

        private void RefreshFocuser() {
            var f = services.Focuser;
            Focuser.State = f.State;
            Focuser.Detail = f.State == DeviceConnectionState.Connected ? $"{f.Position} ms · speed {f.Speed}{(f.IsMoving ? " · moving" : "")}" : "—";
        }

        private void RefreshPower() {
            var p = services.Power.Current;
            PowerText = p.Summary;
            IsPowerLow = p.OnBattery && p.BatteryPercent is < 20;
        }

        private void RefreshKeepAwake() {
            var k = services.KeepAwake.State;
            KeepAwakeText = k.Summary;
            IsKeepAwakeEngaged = k.IsEngaged;
        }

        private void RefreshSession() {
            var s = services.Session;
            SessionText = s.State switch {
                SessionState.Running => $"Running {s.Progress.FramesDone}/{s.Progress.FrameCount}",
                SessionState.Paused => $"Paused {s.Progress.FramesDone}/{s.Progress.FrameCount}",
                SessionState.Stopping => "Stopping…",
                SessionState.Finished => $"Finished {s.Progress.FramesDone}/{s.Progress.FrameCount}",
                SessionState.Failed => "Session failed",
                _ => "No session",
            };
        }

        internal static string Format(double? value, string format) => value?.ToString(format) ?? "—";
    }

    /// <summary>"Connection lost" banner with a reconnect button. Restores the cooler setpoint after a camera reconnect.</summary>
    public sealed partial class ConnectionBannerViewModel : ViewModelBase {
        private readonly AppServices services;
        private bool cameraCoolerWasOn;
        private double cameraSetpoint;

        public ConnectionBannerViewModel(AppServices services) {
            this.services = services;
            services.Camera.Changed += (_, _) => Refresh();
            services.Mount.Changed += (_, _) => Refresh();
            Refresh();
        }

        [ObservableProperty]
        public partial bool IsVisible { get; set; }

        [ObservableProperty]
        public partial string Message { get; set; }

        [ObservableProperty]
        public partial string ReconnectError { get; set; }

        public void Refresh() {
            var camera = services.Camera;
            if (camera.State == DeviceConnectionState.Connected) {
                cameraCoolerWasOn = camera.CoolerOn;
                cameraSetpoint = camera.TargetTemperature;
            }
            var lost = new List<string>();
            if (camera.State == DeviceConnectionState.Lost) {
                lost.Add($"Camera: {camera.LastError ?? "connection lost"}. Exposures stopped{(cameraCoolerWasOn ? "; the cooler is no longer managed" : "")}.");
            }
            if (services.Mount.State == DeviceConnectionState.Lost) {
                lost.Add($"Mount: {services.Mount.LastError ?? "connection lost"}. Tracking state unknown; the focuser is unavailable.");
            }
            IsVisible = lost.Count > 0;
            Message = lost.Count == 0 ? null : string.Join("\n", lost);
            if (!IsVisible) {
                ReconnectError = null;
            }
        }

        [RelayCommand]
        private async Task Reconnect() {
            ReconnectError = null;
            var errors = new List<string>();
            // Snapshot first: reconnecting raises Changed with a fresh (cooler off) camera, which would overwrite it
            var restoreCooler = cameraCoolerWasOn;
            var setpoint = cameraSetpoint;
            foreach (var device in new IDeviceService[] { services.Camera, services.Mount }.Where(d => d.State == DeviceConnectionState.Lost)) {
                try {
                    await device.ConnectAsync();
                    if (ReferenceEquals(device, services.Camera) && restoreCooler) {
                        services.Camera.SetCooler(true, setpoint);
                    }
                } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException || ex is OperationCanceledException) {
                    errors.Add($"{device.DisplayName}: {ex.Message}");
                }
            }
            ReconnectError = errors.Count == 0 ? null : string.Join("\n", errors);
            Refresh();
        }
    }
}
