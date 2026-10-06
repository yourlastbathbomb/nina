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
using NINA.Mac.Platform;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.ViewModels {

    /// <summary>Step 1: camera over USB, mount over the FTDI serial cable, focuser through the mount.</summary>
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
            ? services.EngineError != null
                ? $"Real devices could not start ({services.EngineError}); the simulators run instead. The serial port list is real; nothing is opened."
                : "Devices are simulated (Settings › Devices switches to Real). The serial port list is real; nothing is opened."
            : null;

        public bool CanStartNight => services.Camera.State != DeviceConnectionState.Connected || services.Mount.State != DeviceConnectionState.Connected;

        public override void Refresh() {
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

        /// <summary>One click: connect camera and mount together, then start cooling.</summary>
        [RelayCommand]
        private async Task StartNight() {
            ErrorMessage = null;
            try {
                await Task.WhenAll(services.Camera.ConnectAsync(), services.Mount.ConnectAsync());
                if (CoolAfterConnect && services.Camera.State == DeviceConnectionState.Connected) {
                    services.Camera.SetCooler(true, CoolingTarget);
                }
            } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException || ex is OperationCanceledException) {
                ShowError(ex);
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
