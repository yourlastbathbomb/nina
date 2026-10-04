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
using NINA.Mac.App.Astro;
using NINA.Mac.App.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace NINA.Mac.App.ViewModels {

    /// <summary>Step 5: run the plan from the Target screen; show progress, limits and where frames go.</summary>
    public sealed partial class RunViewModel : PageViewModel {
        private readonly AppServices services;

        public RunViewModel(AppServices services) : base(PageKind.Run, "Run", "Expose, dither, stop at the limits") {
            this.services = services;
            services.Session.Changed += (_, _) => Refresh();
            services.Camera.Changed += (_, _) => ExposureProgress = services.Camera.ExposureProgress;
            Refresh();
        }

        public ObservableCollection<string> Log { get; } = new();

        [ObservableProperty]
        public partial SessionPlan Plan { get; set; }

        [ObservableProperty]
        public partial string PlanText { get; set; }

        [ObservableProperty]
        public partial string StateText { get; set; }

        [ObservableProperty]
        public partial bool IsRunning { get; set; }

        [ObservableProperty]
        public partial bool IsPaused { get; set; }

        [ObservableProperty]
        public partial double OverallProgress { get; set; }

        [ObservableProperty]
        public partial double ExposureProgress { get; set; }

        [ObservableProperty]
        public partial string FramesText { get; set; }

        [ObservableProperty]
        public partial string LastHfrText { get; set; }

        [ObservableProperty]
        public partial string LastFileText { get; set; }

        [ObservableProperty]
        public partial string LimitsText { get; set; }

        [ObservableProperty]
        public partial string OutputFolder { get; set; }

        [ObservableProperty]
        public partial string StopReason { get; set; }

        public bool HasPlan => Plan != null;

        public bool CanStart => HasPlan && !IsRunning;

        public void LoadPlan(SessionPlan plan) {
            Plan = plan;
            Refresh();
        }

        partial void OnPlanChanged(SessionPlan value) {
            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(CanStart));
        }

        partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanStart));

        public override void Refresh() {
            var session = services.Session;
            var p = Plan ?? session.Plan;
            PlanText = p == null
                ? "No plan yet: choose a target on the Target screen and press \"Use for Run\"."
                : $"{p.TargetName}: {p.FrameCount} × {p.ExposureSeconds:0.#} s, gain {p.Gain}, bin {p.Bin}, dither every {p.DitherEvery}";
            IsRunning = session.State is SessionState.Running or SessionState.Paused or SessionState.Stopping;
            IsPaused = session.State == SessionState.Paused;
            StateText = session.State.ToString();
            var progress = session.Progress;
            FramesText = progress.FrameCount > 0 ? $"{progress.FramesDone} / {progress.FrameCount}" : "—";
            OverallProgress = progress.FrameCount > 0 ? (double)progress.FramesDone / progress.FrameCount : 0;
            LastHfrText = progress.LastHfr is { } hfr ? $"{hfr:0.00} px" : "—";
            LastFileText = progress.LastFile ?? "—";
            StopReason = progress.StopReason;
            if (p != null) {
                var layout = services.CurrentLayout();
                OutputFolder = (session.Layout ?? layout).LightsDirectory(p.TargetName);
                var site = services.Settings.Current.Site;
                var dawn = SkyMath.NextAstronomicalDawn(services.Clock.Now, new GeoSite(site.LatitudeDegrees, site.LongitudeDegrees));
                var dawnText = p.StopAtDawn
                    ? dawn is { } d ? $", at astronomical dawn ({d.ToOffset(TimeSpan.FromHours(site.UtcOffsetHours)):HH:mm})" : ", at astronomical dawn"
                    : "";
                LimitsText = $"Stops above {p.MaxAltitude:0}° (keyhole), below {p.MinAltitude:0}°{dawnText}.";
            } else {
                OutputFolder = null;
                LimitsText = null;
            }
            var lines = session.Log;
            if (lines.Count != Log.Count || (lines.Count > 0 && Log.Count > 0 && lines[^1] != Log[^1])) {
                Log.Clear();
                foreach (var line in lines) {
                    Log.Add(line);
                }
            }
        }

        [RelayCommand]
        private async Task Start() {
            if (Plan == null) {
                ErrorMessage = "Choose a target first.";
                return;
            }
            ErrorMessage = null;
            try {
                await services.Session.RunAsync(Plan);
            } catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException) {
                ShowError(ex);
            }
            Refresh();
        }

        [RelayCommand]
        private void PauseOrResume() {
            if (services.Session.State == SessionState.Paused) {
                services.Session.Resume();
            } else {
                services.Session.Pause();
            }
        }

        [RelayCommand]
        private void Stop() => services.Session.RequestStop("Stopped by user");
    }
}
