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
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.ViewModels {

    /// <summary>
    /// Step 4: pick a target, see whether this alt-az site can take it right now (blocked north, zenith keyhole,
    /// field-rotation sub limit), slew and centre (plate solving with Real devices), and hand a plan to Run. Stop halts the
    /// goto (':Q#' on the LX200) and ends the centring.
    /// </summary>
    public sealed partial class TargetViewModel : PageViewModel {
        private readonly AppServices services;
        private readonly INavigator navigator;
        private readonly RunViewModel run;
        private CancellationTokenSource slewCts;

        public TargetViewModel(AppServices services, INavigator navigator, RunViewModel run) : base(PageKind.Target, "Target", "Where to point, and for how long") {
            this.services = services;
            this.navigator = navigator;
            this.run = run;
            ExposureSeconds = 10;
            FrameCount = 60;
            DitherEvery = 5;
            StopAtDawn = true;
            SearchText = "";
            services.Mount.Changed += (_, _) => Refresh();
            Search();
        }

        public ObservableCollection<CatalogTarget> Results { get; } = new();

        public ObservableCollection<string> Warnings { get; } = new();

        [ObservableProperty]
        public partial string SearchText { get; set; }

        [ObservableProperty]
        public partial CatalogTarget SelectedTarget { get; set; }

        [ObservableProperty]
        public partial double ExposureSeconds { get; set; }

        [ObservableProperty]
        public partial int FrameCount { get; set; }

        [ObservableProperty]
        public partial int DitherEvery { get; set; }

        [ObservableProperty]
        public partial bool StopAtDawn { get; set; }

        [ObservableProperty]
        public partial string CoordinatesText { get; set; }

        [ObservableProperty]
        public partial string AltAzText { get; set; }

        [ObservableProperty]
        public partial string TransitText { get; set; }

        [ObservableProperty]
        public partial string MaxSubText { get; set; }

        [ObservableProperty]
        public partial bool ExceedsFieldRotationLimit { get; set; }

        [ObservableProperty]
        public partial bool IsObservable { get; set; }

        [ObservableProperty]
        public partial string SlewStatus { get; set; }

        /// <summary>A slew (and centring) started here is under way; Stop is offered.</summary>
        [ObservableProperty]
        public partial bool IsSlewing { get; set; }

        [ObservableProperty]
        public partial TargetVisibility Visibility { get; set; }

        public bool HasTarget => SelectedTarget != null;

        public string OpticsText {
            get {
                var o = services.Settings.Current.Optics;
                return $"{o.EffectiveFocalLengthMm:0} mm, bin {o.Bin}: {o.PixelScaleArcsec:0.00}″/px";
            }
        }

        partial void OnSearchTextChanged(string value) => Search();

        partial void OnSelectedTargetChanged(CatalogTarget value) {
            OnPropertyChanged(nameof(HasTarget));
            SlewStatus = null;
            Refresh();
        }

        partial void OnExposureSecondsChanged(double value) => Refresh();

        public override void Refresh() {
            Warnings.Clear();
            var target = SelectedTarget;
            if (target == null) {
                CoordinatesText = AltAzText = TransitText = MaxSubText = null;
                ExceedsFieldRotationLimit = false;
                IsObservable = false;
                Visibility = null;
                return;
            }
            var now = services.Clock.Now;
            var v = TargetPlanner.Evaluate(target, now, services.Settings.Current, ExposureSeconds);
            Visibility = v;
            var offset = TimeSpan.FromHours(services.Settings.Current.Site.UtcOffsetHours);
            CoordinatesText = $"RA {SkyMath.FormatHours(target.RightAscensionHours)}  Dec {SkyMath.FormatDegrees(target.DeclinationDegrees)} (J2000)";
            AltAzText = $"Alt {v.Altitude:0.0}°  Az {v.Azimuth:0.0}°  ({(v.HourAngleHours < 0 ? "rising, east" : "setting, west")} of the meridian)";
            TransitText = v.NeverRises ? "Never rises here" : $"Transit {v.NextTransit.ToOffset(offset):HH:mm} at {v.TransitAltitude:0.0}°";
            MaxSubText = v.Altitude > 0 ? $"Max sub now: {TargetPlanner.FormatSeconds(v.MaxSubSecondsNow)} (field rotation)" : "Below the horizon";
            ExceedsFieldRotationLimit = v.Altitude > 0 && ExposureSeconds > v.MaxSubSecondsNow;
            IsObservable = v.Observable;
            foreach (var w in v.Warnings) {
                Warnings.Add(w);
            }
        }

        [RelayCommand]
        private void Search() {
            Results.Clear();
            foreach (var t in services.Catalog.Search(SearchText)) {
                Results.Add(t);
            }
            if (SelectedTarget == null || !Results.Contains(SelectedTarget)) {
                SelectedTarget = Results.Count > 0 ? Results[0] : null;
            }
        }

        [RelayCommand]
        private async Task SlewAndCentre() {
            if (SelectedTarget == null || IsSlewing) {
                return;
            }
            ErrorMessage = null;
            var target = SelectedTarget;
            using var cts = new CancellationTokenSource();
            slewCts = cts;
            IsSlewing = true;
            SlewStatus = $"Slewing to {target.Name}…";
            try {
                var result = await services.Centring.SlewAndCentreAsync(target.RightAscensionHours, target.DeclinationDegrees,
                    new InlineProgress<string>(s => {
                        if (!cts.IsCancellationRequested) {
                            SlewStatus = $"{target.Name}: {s}";
                        }
                    }), cts.Token);
                SlewStatus = $"{target.Name}: {result.Message}";
            } catch (OperationCanceledException) {
                // Stop here, the status bar's Stop, or a disconnect halted the mount
                SlewStatus = $"{target.Name}: stopped; the mount is where the halt left it";
            } catch (Exception ex) when (ex is InvalidOperationException || ex is DeviceLostException) {
                SlewStatus = null;
                ShowError(ex);
            } finally {
                slewCts = null;
                IsSlewing = false;
            }
        }

        /// <summary>Halts the goto (the mount's Abort: ':Q#') and ends the slew-and-centre started here.</summary>
        [RelayCommand]
        private void StopSlew() {
            SlewStatus = "Stopping…";
            try {
                slewCts?.Cancel();
            } catch (ObjectDisposedException) {
                // the slew has just ended
            }
            services.Mount.Abort();
        }

        [RelayCommand]
        private void UseForRun() {
            if (SelectedTarget == null) {
                return;
            }
            var s = services.Settings.Current;
            run.LoadPlan(new SessionPlan(SelectedTarget.Name, SelectedTarget.RightAscensionHours, SelectedTarget.DeclinationDegrees,
                ExposureSeconds, FrameCount, s.Gain, s.Offset, s.Optics.Bin, DitherEvery, s.MaxAltitudeDegrees, s.MinAltitudeDegrees, StopAtDawn));
            navigator.NavigateTo(PageKind.Run);
        }
    }
}
