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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.ViewModels {

    /// <summary>File pickers for import and export; the view supplies Avalonia's, tests a fake. Null results mean "cancelled".</summary>
    public interface IFileDialogs {

        Task<string> PickOpenAsync(string title, string extension);

        Task<string> PickSaveAsync(string title, string suggestedName, string extension);
    }

    /// <summary>One editable horizon point.</summary>
    public sealed partial class HorizonRowViewModel : ViewModelBase {
        private readonly Action<HorizonRowViewModel> changed;

        public HorizonRowViewModel(double azimuth, double altitude, Action<HorizonRowViewModel> changed) {
            Azimuth = azimuth;
            Altitude = altitude;
            this.changed = changed;
        }

        [ObservableProperty]
        public partial double Azimuth { get; set; }

        [ObservableProperty]
        public partial double Altitude { get; set; }

        partial void OnAzimuthChanged(double value) => changed?.Invoke(this);

        partial void OnAltitudeChanged(double value) => changed?.Invoke(this);
    }

    /// <summary>
    /// Target › Horizon: the site's local horizon as a table of azimuth/altitude points. Points can be typed, recorded from the
    /// mount (point the scope at the skyline, then "Record point from mount" takes its current alt/az), imported from or exported
    /// to a NINA .hrz file. Save writes the app's horizon file (NINA's plain format), which the slew guard, the sky view and,
    /// with Real devices, NINA's sequence (horizon waits and conditions) then use. Until a measured horizon is saved the
    /// built-in estimate is used, and preflight says so.
    /// </summary>
    public sealed partial class HorizonEditorViewModel : ViewModelBase {
        private readonly AppServices services;
        private bool loading;

        public HorizonEditorViewModel(AppServices services) {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            services.Mount.Changed += (_, _) => OnPropertyChanged(nameof(CanRecord));
            Load(services.Horizon);
            if (services.HorizonError != null) {
                Status = services.HorizonError;
            }
        }

        public ObservableCollection<HorizonRowViewModel> Rows { get; } = new();

        /// <summary>Set by the view (Avalonia's storage provider); null in headless use.</summary>
        public IFileDialogs Dialogs { get; set; }

        /// <summary>Marked as an estimate (not measured); saved with the marker preflight looks for.</summary>
        [ObservableProperty]
        public partial bool IsEstimate { get; set; }

        [ObservableProperty]
        public partial bool IsDirty { get; set; }

        [ObservableProperty]
        public partial string Status { get; set; }

        [ObservableProperty]
        public partial string Error { get; set; }

        /// <summary>The table as a horizon (null while it is not a valid one); the sky view draws it.</summary>
        [ObservableProperty]
        public partial HorizonProfile Draft { get; set; }

        public string FilePath => services.DataPaths.HorizonFile;

        public string SourceText => services.Horizon.IsEstimate
            ? $"In use: {services.Horizon.Describe()}. Measure the real horizon and save it."
            : $"In use: {services.Horizon.Describe()}";

        public bool CanRecord => services.Mount.State == DeviceConnectionState.Connected && services.Mount.Altitude != null && services.Mount.Azimuth != null;

        /// <summary>Raised when the table (and so <see cref="Draft"/>) changes.</summary>
        public event EventHandler DraftChanged;

        partial void OnIsEstimateChanged(bool value) {
            if (!loading) {
                IsDirty = true;
                Rebuild();
            }
        }

        private void Load(HorizonProfile horizon) {
            loading = true;
            try {
                Rows.Clear();
                foreach (var p in horizon.Points) {
                    Rows.Add(new HorizonRowViewModel(p.Azimuth, p.Altitude, OnRowChanged));
                }
                IsEstimate = horizon.IsEstimate;
                IsDirty = false;
            } finally {
                loading = false;
            }
            Rebuild();
            OnPropertyChanged(nameof(SourceText));
        }

        private void OnRowChanged(HorizonRowViewModel row) {
            if (loading) {
                return;
            }
            MarkMeasured();
            IsDirty = true;
            Rebuild();
        }

        /// <summary>A manual edit or a recorded point is a measurement: the estimate mark goes (it can be ticked again).</summary>
        private void MarkMeasured() {
            loading = true;
            try {
                IsEstimate = false;
            } finally {
                loading = false;
            }
        }

        private void Rebuild() {
            try {
                Draft = new HorizonProfile(Rows.Select(r => new HorizonPoint(r.Azimuth, r.Altitude)), IsEstimate, services.Horizon.Source);
                Error = null;
            } catch (ArgumentException ex) {
                Draft = null;
                Error = ex.Message;
            }
            DraftChanged?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        private void AddRow() {
            var last = Rows.Count == 0 ? 0 : Rows.Max(r => r.Azimuth);
            var az = Math.Min(360, last + 15);
            if (Rows.Any(r => Math.Abs(r.Azimuth - az) < 0.01)) {
                az = Enumerable.Range(0, 24).Select(i => i * 15.0).FirstOrDefault(a => !Rows.Any(r => Math.Abs(r.Azimuth - a) < 0.01), 0);
            }
            Rows.Add(new HorizonRowViewModel(az, Draft?.GetAltitude(az) ?? 15, OnRowChanged));
            MarkMeasured();
            Sort();
            IsDirty = true;
            Rebuild();
        }

        [RelayCommand]
        private void RemoveRow(HorizonRowViewModel row) {
            if (row == null || !Rows.Remove(row)) {
                return;
            }
            MarkMeasured();
            IsDirty = true;
            Rebuild();
        }

        /// <summary>Takes the mount's current altitude and azimuth as a horizon point (replacing one within 1° of azimuth).</summary>
        [RelayCommand]
        private void RecordFromMount() {
            Error = null;
            if (services.Mount.State != DeviceConnectionState.Connected || services.Mount.Altitude is not double alt || services.Mount.Azimuth is not double az) {
                Error = "Connect the mount first, then point the scope at the top of the skyline";
                return;
            }
            alt = Math.Round(Math.Clamp(alt, 0, 90), 1);
            az = Math.Round(((az % 360) + 360) % 360, 1);
            var existing = Rows.FirstOrDefault(r => Math.Abs(r.Azimuth - az) < 1.0);
            loading = true;
            try {
                if (existing != null) {
                    existing.Azimuth = az;
                    existing.Altitude = alt;
                } else {
                    Rows.Add(new HorizonRowViewModel(az, alt, OnRowChanged));
                }
            } finally {
                loading = false;
            }
            MarkMeasured();
            Sort();
            IsDirty = true;
            Rebuild();
            Status = string.Create(CultureInfo.InvariantCulture, $"Recorded azimuth {az:0.0}°, altitude {alt:0.0}° from the mount{(existing != null ? " (replaced the point there)" : "")}. Save when done.");
        }

        [RelayCommand]
        private void Save() {
            Error = null;
            if (Draft == null) {
                Error ??= "The table is not a valid horizon";
                return;
            }
            try {
                var note = services.SaveHorizon(new HorizonProfile(Draft.Points, IsEstimate));
                Load(services.Horizon);
                Status = $"Saved to {services.Horizon.Source}{(IsEstimate ? " (still marked as an estimate)" : "")}. The slew guard uses it now." + (note != null ? " " + note : "");
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) {
                Error = $"Could not save the horizon: {ex.Message}";
            }
        }

        [RelayCommand]
        private void Revert() {
            Load(services.Horizon);
            Error = null;
            Status = "Back to the saved horizon";
        }

        /// <summary>Replaces the table with the built-in estimate (not saved until Save).</summary>
        [RelayCommand]
        private void UseEstimate() {
            Load(HorizonProfile.SiteEstimate());
            IsDirty = true;
            Status = "The built-in estimate (south open to about 15°, north blocked at 80°); Save to use it";
        }

        [RelayCommand]
        private async Task Import() {
            var path = Dialogs == null ? null : await Dialogs.PickOpenAsync("Import a horizon file", "hrz");
            if (path != null) {
                ImportFrom(path);
            }
        }

        [RelayCommand]
        private async Task Export() {
            var path = Dialogs == null ? null : await Dialogs.PickSaveAsync("Export the horizon", "horizon.hrz", "hrz");
            if (path != null) {
                ExportTo(path);
            }
        }

        /// <summary>Loads a NINA .hrz file into the table (not saved until Save). Inline comments are accepted.</summary>
        public void ImportFrom(string path) {
            Error = null;
            try {
                var horizon = HorizonProfile.Load(path, out var warnings);
                Load(horizon);
                IsDirty = true;
                Status = $"Imported {horizon.Points.Count} points from {Path.GetFileName(path)}" +
                    (warnings.Count > 0 ? $"; skipped {warnings.Count} line{(warnings.Count == 1 ? "" : "s")} ({warnings[0]})" : "") + ". Save to use it.";
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is ArgumentException) {
                Error = $"Could not import {Path.GetFileName(path)}: {ex.Message}";
            }
        }

        /// <summary>Writes the table to a .hrz file in NINA's plain format.</summary>
        public void ExportTo(string path) {
            Error = null;
            if (Draft == null) {
                Error = "The table is not a valid horizon";
                return;
            }
            try {
                Draft.Save(path, services.Settings.Current.Site.Name, services.Clock.Now);
                Status = $"Exported to {path}";
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) {
                Error = $"Could not export: {ex.Message}";
            }
        }

        private void Sort() {
            var sorted = Rows.OrderBy(r => r.Azimuth).ToList();
            for (var i = 0; i < sorted.Count; i++) {
                var current = Rows.IndexOf(sorted[i]);
                if (current != i) {
                    Rows.Move(current, i);
                }
            }
        }
    }
}
