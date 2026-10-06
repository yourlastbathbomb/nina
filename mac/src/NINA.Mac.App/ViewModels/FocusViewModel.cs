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
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.ViewModels {

    /// <summary>
    /// Step 3: manual focus. Live loop with HFR and a Bahtinov readout, and the timed-focuser panel: in/out
    /// nudges in milliseconds, speed 1-4 and "recentre virtual position". No autofocus: the #1209 has no
    /// position readout (MAC_PORT_PLAN.md section 6). The loop stops when a run starts: the run owns the camera.
    /// </summary>
    public sealed partial class FocusViewModel : PageViewModel {
        public const int HistoryLength = 60;

        private readonly AppServices services;
        private CancellationTokenSource loopCts;
        private bool stoppedForRun;

        public FocusViewModel(AppServices services) : base(PageKind.Focus, "Focus", "Live HFR + Bahtinov, timed focuser") {
            this.services = services;
            var s = services.Settings.Current;
            ExposureSeconds = 2;
            Gain = s.Gain;
            SmallNudgeMs = s.FocuserSmallNudgeMs;
            LargeNudgeMs = s.FocuserLargeNudgeMs;
            CustomMs = 1000;
            SelectedSpeed = services.Focuser.Speed;
            services.Focuser.Changed += (_, _) => Refresh();
            services.Camera.Changed += (_, _) => RefreshCamera();
            services.Session.Changed += (_, _) => StopLoopForRun();
            Refresh();
        }

        public IReadOnlyList<int> SpeedOptions { get; } = new[] { 1, 2, 3, 4 };

        [ObservableProperty]
        public partial double ExposureSeconds { get; set; }

        [ObservableProperty]
        public partial int Gain { get; set; }

        [ObservableProperty]
        public partial int SmallNudgeMs { get; set; }

        [ObservableProperty]
        public partial int LargeNudgeMs { get; set; }

        [ObservableProperty]
        public partial int CustomMs { get; set; }

        [ObservableProperty]
        public partial int SelectedSpeed { get; set; }

        [ObservableProperty]
        public partial int Position { get; set; }

        [ObservableProperty]
        public partial bool IsMoving { get; set; }

        [ObservableProperty]
        public partial bool FocuserReady { get; set; }

        [ObservableProperty]
        public partial bool CameraReady { get; set; }

        [ObservableProperty]
        public partial bool IsLooping { get; set; }

        [ObservableProperty]
        public partial double ExposureProgress { get; set; }

        [ObservableProperty]
        public partial string HfrText { get; set; }

        [ObservableProperty]
        public partial string BestHfrText { get; set; }

        [ObservableProperty]
        public partial string StarsText { get; set; }

        [ObservableProperty]
        public partial string BahtinovText { get; set; }

        [ObservableProperty]
        public partial string PositionNote { get; set; }

        public ObservableCollection<double> HfrHistory { get; } = new();

        public override void Refresh() {
            var f = services.Focuser;
            FocuserReady = f.State == DeviceConnectionState.Connected;
            Position = f.Position;
            IsMoving = f.IsMoving;
            if (SelectedSpeed != f.Speed) {
                SelectedSpeed = f.Speed;
            }
            RefreshCamera();
        }

        private void RefreshCamera() {
            CameraReady = services.Camera.State == DeviceConnectionState.Connected;
            ExposureProgress = services.Camera.ExposureProgress;
        }

        partial void OnSelectedSpeedChanged(int value) {
            if (value < 1 || value > 4 || value == services.Focuser.Speed) {
                return;
            }
            services.Focuser.SetSpeed(value);
            PositionNote = $"Speed {value}: virtual position recentred to {services.Focuser.Position} ms (positions only compare at one speed).";
        }

        [RelayCommand]
        private Task MoveInLarge() => Move(-LargeNudgeMs);

        [RelayCommand]
        private Task MoveInSmall() => Move(-SmallNudgeMs);

        [RelayCommand]
        private Task MoveOutSmall() => Move(SmallNudgeMs);

        [RelayCommand]
        private Task MoveOutLarge() => Move(LargeNudgeMs);

        [RelayCommand]
        private Task MoveInCustom() => Move(-Math.Abs(CustomMs));

        [RelayCommand]
        private Task MoveOutCustom() => Move(Math.Abs(CustomMs));

        [RelayCommand]
        private void RecenterVirtualPosition() {
            services.Focuser.RecenterVirtualPosition();
            PositionNote = $"Virtual position recentred to {services.Focuser.Position} ms.";
        }

        /// <summary>One frame, then update HFR/Bahtinov.</summary>
        [RelayCommand]
        private async Task TakeOne() {
            ErrorMessage = null;
            try {
                await ExposeOnce(CancellationToken.None);
            } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException || ex is OperationCanceledException) {
                ShowError(ex);
            }
        }

        [RelayCommand]
        private async Task ToggleLoop() {
            if (IsLooping) {
                loopCts?.Cancel();
                return;
            }
            ErrorMessage = null;
            stoppedForRun = false;
            loopCts = new CancellationTokenSource();
            IsLooping = true;
            try {
                while (!loopCts.IsCancellationRequested) {
                    await ExposeOnce(loopCts.Token);
                }
            } catch (Exception ex) when (stoppedForRun && (ex is OperationCanceledException || ex is InvalidOperationException)) {
                ErrorMessage = "Loop stopped: a run started and uses the camera.";
            } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException || ex is OperationCanceledException) {
                ShowError(ex);
            } finally {
                IsLooping = false;
            }
        }

        /// <summary>A run that starts takes the camera: the loop ends (its current frame is aborted) instead of competing for it.</summary>
        private void StopLoopForRun() {
            if (!IsLooping || services.Session.State is not (SessionState.Running or SessionState.Paused)) {
                return;
            }
            stoppedForRun = true;
            loopCts?.Cancel();
        }

        internal async Task ExposeOnce(CancellationToken ct) {
            var settings = services.Settings.Current;
            var request = new ExposureRequest(FrameType.Snapshot, ExposureSeconds, Gain, settings.Offset, settings.Optics.Bin) {
                MonoBin = settings.FocusMonoBin,
            };
            var frame = await services.Camera.ExposeAsync(request, ct);
            StarsText = $"{frame.Stars} stars";
            if (double.IsNaN(frame.Hfr) || frame.Stars == 0) {
                // No star measured (clouds, cap on, far out of focus): keep the history and the best value as they were
                HfrText = "no stars";
            } else {
                HfrHistory.Add(frame.Hfr);
                while (HfrHistory.Count > HistoryLength) {
                    HfrHistory.RemoveAt(0);
                }
                HfrText = $"{frame.Hfr:0.00} px";
                BestHfrText = $"best {HfrHistory.Min():0.00} px";
            }
            BahtinovText = frame.BahtinovOffsetPixels is { } b ? $"{b:+0.00;-0.00;0.00} px {(Math.Abs(b) < 0.3 ? "(in focus)" : b > 0 ? "(move in)" : "(move out)")}" : "—";
        }

        private async Task Move(int milliseconds) {
            ErrorMessage = null;
            try {
                await services.Focuser.MoveAsync(milliseconds);
            } catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentOutOfRangeException || ex is DeviceLostException) {
                ShowError(ex);
            }
        }
    }
}
