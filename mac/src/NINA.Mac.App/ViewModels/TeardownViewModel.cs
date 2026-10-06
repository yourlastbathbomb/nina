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

    public enum TeardownStepStatus {
        Pending,
        Running,
        Done,
        Skipped,
        Failed,
    }

    public sealed partial class TeardownStepViewModel : ViewModelBase {

        public TeardownStepViewModel(string title) {
            Title = title;
            Status = TeardownStepStatus.Pending;
        }

        public string Title { get; }

        [ObservableProperty]
        public partial TeardownStepStatus Status { get; set; }

        [ObservableProperty]
        public partial string Detail { get; set; }

        public bool IsDone => Status == TeardownStepStatus.Done;

        public bool IsFailed => Status == TeardownStepStatus.Failed;

        public bool IsRunning => Status == TeardownStepStatus.Running;

        public string StatusText => Status switch {
            TeardownStepStatus.Running => "…",
            TeardownStepStatus.Done => "done",
            TeardownStepStatus.Skipped => "skipped",
            TeardownStepStatus.Failed => "failed",
            _ => "",
        };

        partial void OnStatusChanged(TeardownStepStatus value) {
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>
    /// Step 7: end of night in a safe order: stop the session, warm the sensor, soft park (never the Dec +89 hard
    /// park), disconnect, and let the Mac sleep again. Shows where tonight's frames are for Siril.
    /// </summary>
    public sealed partial class TeardownViewModel : PageViewModel {
        private readonly AppServices services;
        private CancellationTokenSource cts;

        public TeardownViewModel(AppServices services) : base(PageKind.Teardown, "Teardown", "Warm, park, disconnect") {
            this.services = services;
            StopSession = new TeardownStepViewModel("Stop the imaging session");
            WarmCamera = new TeardownStepViewModel($"Warm the camera ({services.Settings.Current.WarmupRateCelsiusPerMinute:0.#} °C/min), then cooler off");
            SoftPark = new TeardownStepViewModel(services.DevicesSimulated
                ? "Soft park: low in the south, tracking off"
                : "Soft park (the LX200 driver's: tracking off; never the Autostar's park)");
            DisconnectCamera = new TeardownStepViewModel("Disconnect the camera");
            DisconnectMount = new TeardownStepViewModel("Disconnect the mount and focuser");
            ReleaseKeepAwake = new TeardownStepViewModel("Let the Mac sleep again");
            Steps = new ObservableCollection<TeardownStepViewModel> { StopSession, WarmCamera, SoftPark, DisconnectCamera, DisconnectMount, ReleaseKeepAwake };
            services.KeepAwake.Changed += (_, _) => Refresh();
            Refresh();
        }

        public ObservableCollection<TeardownStepViewModel> Steps { get; }

        public TeardownStepViewModel StopSession { get; }

        public TeardownStepViewModel WarmCamera { get; }

        public TeardownStepViewModel SoftPark { get; }

        public TeardownStepViewModel DisconnectCamera { get; }

        public TeardownStepViewModel DisconnectMount { get; }

        public TeardownStepViewModel ReleaseKeepAwake { get; }

        [ObservableProperty]
        public partial bool IsRunning { get; set; }

        [ObservableProperty]
        public partial string NightFolder { get; set; }

        [ObservableProperty]
        public partial string SirilCommand { get; set; }

        [ObservableProperty]
        public partial string KeepAwakeText { get; set; }

        public override void Refresh() {
            var layout = services.Session.Layout ?? services.CurrentLayout();
            NightFolder = layout.NightDirectory;
            SirilCommand = services.Session.Plan is { } plan
                ? layout.SirilCommand(plan.TargetName)
                : $"siril-cli -d \"{System.IO.Path.Combine(layout.NightDirectory, "siril", "<target>")}\" -s OSC_Preprocessing.ssf";
            KeepAwakeText = services.KeepAwake.State.Summary;
        }

        [RelayCommand]
        private async Task RunTeardown() {
            ErrorMessage = null;
            cts = new CancellationTokenSource();
            IsRunning = true;
            foreach (var step in Steps) {
                step.Status = TeardownStepStatus.Pending;
                step.Detail = null;
            }
            try {
                await Do(StopSession, async () => {
                    if (services.Session.State is not (SessionState.Running or SessionState.Paused)) {
                        return "no session running";
                    }
                    services.Session.RequestStop("Teardown");
                    while (services.Session.State is SessionState.Running or SessionState.Paused or SessionState.Stopping) {
                        await services.Clock.Delay(TimeSpan.FromMilliseconds(200), cts.Token);
                    }
                    return "stopped";
                });
                await Do(WarmCamera, async () => {
                    if (services.Camera.State != DeviceConnectionState.Connected) {
                        return "camera not connected";
                    }
                    await CameraWarmup.WarmAsync(services.Camera, services.Clock, services.Settings.Current.WarmupRateCelsiusPerMinute,
                        new InlineProgress<string>(s => WarmCamera.Detail = s), cts.Token);
                    return $"cooler off, sensor {services.Camera.SensorTemperature:0.0} °C";
                });
                await Do(SoftPark, async () => {
                    if (services.Mount.State != DeviceConnectionState.Connected) {
                        return "mount not connected";
                    }
                    await services.Mount.SoftParkAsync(cts.Token);
                    return $"Alt {services.Mount.Altitude:0}° Az {services.Mount.Azimuth:0}°, tracking off";
                });
                await Do(DisconnectCamera, async () => {
                    await services.Camera.DisconnectAsync();
                    return "disconnected";
                });
                await Do(DisconnectMount, async () => {
                    await services.Mount.DisconnectAsync();
                    return "disconnected";
                });
                await Do(ReleaseKeepAwake, () => {
                    services.KeepAwake.Evaluate();
                    return Task.FromResult(services.KeepAwake.State.Summary);
                });
            } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException || ex is OperationCanceledException) {
                ShowError(ex);
            } finally {
                IsRunning = false;
                Refresh();
            }
        }

        [RelayCommand]
        private void Cancel() => cts?.Cancel();

        private static async Task Do(TeardownStepViewModel step, Func<Task<string>> action) {
            step.Status = TeardownStepStatus.Running;
            try {
                var detail = await action();
                step.Detail = detail;
                step.Status = TeardownStepStatus.Done;
            } catch {
                step.Status = TeardownStepStatus.Failed;
                throw;
            }
        }
    }
}
