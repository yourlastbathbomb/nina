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
using System.Collections.Generic;
using System.IO;

namespace NINA.Mac.App.ViewModels {

    /// <summary>Step 8: devices (Simulated or Real), plate solving, site, optics, limits, storage, keep-awake, night vision, and the simulator switches.</summary>
    public sealed partial class SettingsViewModel : PageViewModel {
        private readonly AppServices services;
        private readonly IThemeController theme;

        public SettingsViewModel(AppServices services, IThemeController theme, AboutViewModel about) : base(PageKind.Settings, "Settings", "Site, optics, limits, storage") {
            this.services = services;
            this.theme = theme;
            About = about;
            Load(services.Settings.Current);
        }

        public AboutViewModel About { get; }

        /// <summary>Working copy; saved on <see cref="SaveCommand"/>.</summary>
        [ObservableProperty]
        public partial AppSettings Draft { get; set; }

        [ObservableProperty]
        public partial string ImagesRootText { get; set; }

        [ObservableProperty]
        public partial string StorageWarning { get; set; }

        [ObservableProperty]
        public partial string SavedMessage { get; set; }

        public string SettingsFile => services.DataPaths.SettingsFile;

        public string LogsDirectory => services.DataPaths.LogsDirectory;

        public string DefaultImagesRoot => services.DataPaths.DefaultImagesRoot;

        public string ResourcesDirectory => services.Paths.ResourcesDirectory;

        public string FrameworksDirectory => services.Paths.FrameworksDirectory;

        public bool IsAppBundle => services.Paths.IsAppBundle;

        public bool DevicesSimulated => services.DevicesSimulated;

        public IReadOnlyList<DeviceSource> DeviceSources { get; } = Enum.GetValues<DeviceSource>();

        /// <summary>What runs now; the device source setting takes effect at the next start.</summary>
        public string ActiveDevicesText => services.DevicesSimulated
            ? services.EngineError != null
                ? $"Running with the simulators: Real devices could not start ({services.EngineError})."
                : "Running with the simulators. Nothing is opened (the serial port list is real)."
            : "Running with Real devices: NINA's ZWO camera driver, the LX200 driver on the selected serial port, ASTAP and solve-field.";

        public bool HasEngineError => services.EngineError != null;

        public string EngineDataDirectory => services.Engine != null ? NINA.Mac.App.Engine.EngineRuntime.DataDirectory : services.DataPaths.EngineDataDirectory;

        public string SolverProblem => services.Engine?.Solvers?.Problem;

        public string PixelScaleText => $"{Draft.Optics.PixelScaleArcsec:0.000}″/px at bin {Draft.Optics.Bin}, field {FieldText()}";

        partial void OnImagesRootTextChanged(string value) {
            try {
                var paths = new UserDataPaths(services.Info.Identity, services.HomeDirectory, string.IsNullOrWhiteSpace(value) ? null : value);
                StorageWarning = paths.CloudSyncWarning(paths.ImagesRoot);
            } catch (ArgumentException ex) {
                StorageWarning = ex.Message;
            }
        }

        public override void Refresh() {
            OnPropertyChanged(nameof(PixelScaleText));
        }

        [RelayCommand]
        private void Save() {
            ErrorMessage = null;
            try {
                var images = string.IsNullOrWhiteSpace(ImagesRootText) ? null : ImagesRootText.Trim();
                _ = new UserDataPaths(services.Info.Identity, services.HomeDirectory, images);
                Draft.ImagesRoot = images;
                Validate(Draft);
                services.Settings.Save(Draft);
                theme?.Apply(Draft.NightVision);
                SavedMessage = Draft.DeviceSource != services.ActiveDeviceSource
                    ? $"Saved to {SettingsFile}. Restart {services.Info.ShortName} to switch to {(Draft.DeviceSource == DeviceSource.Real ? "Real" : "simulated")} devices."
                    : services.EngineSettingsError is { } engineError
                        ? $"Saved to {SettingsFile}, but the engine could not take the new settings: {engineError}"
                        : services.Engine?.SettingsPending == true
                            ? $"Saved to {SettingsFile}. The run in progress keeps its settings; the new ones apply when it ends."
                            : $"Saved to {SettingsFile}";
                Load(services.Settings.Current);
            } catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) {
                ShowError(ex);
            }
        }

        [RelayCommand]
        private void Revert() {
            Load(services.Settings.Current);
            SavedMessage = null;
            ErrorMessage = null;
        }

        [RelayCommand]
        private void SimulateCameraLoss() => services.SimCamera?.SimulateConnectionLoss();

        [RelayCommand]
        private void SimulateMountLoss() => services.SimMount?.SimulateConnectionLoss();

        private void Load(AppSettings settings) {
            Draft = settings.Clone();
            ImagesRootText = settings.ImagesRoot ?? "";
            OnPropertyChanged(nameof(PixelScaleText));
        }

        private string FieldText() {
            var o = Draft.Optics;
            var arcminPerPixel = o.PixelScaleArcsec / 60.0;
            return $"{o.SensorWidth / o.Bin * arcminPerPixel:0.0}′ × {o.SensorHeight / o.Bin * arcminPerPixel:0.0}′";
        }

        internal static void Validate(AppSettings s) {
            if (s.Site.LatitudeDegrees is < -90 or > 90) {
                throw new ArgumentException("Latitude must be between -90 and 90");
            }
            if (s.Site.LongitudeDegrees is < -180 or > 180) {
                throw new ArgumentException("Longitude must be between -180 and 180 (east positive)");
            }
            if (s.MaxAltitudeDegrees <= s.MinAltitudeDegrees || s.MaxAltitudeDegrees > 90 || s.MinAltitudeDegrees < 0) {
                throw new ArgumentException("Altitude limits must satisfy 0 ≤ minimum < maximum ≤ 90");
            }
            if (s.Optics.Bin is < 1 or > 4 || s.Optics.FocalLengthMm <= 0 || s.Optics.ReducerFocalLengthMm <= 0 || s.Optics.PixelSizeMicrons <= 0) {
                throw new ArgumentException("Optics: bin 1-4 and positive focal lengths and pixel size");
            }
            if (s.FocuserSpeed is < 1 or > 4 || s.FocuserSmallNudgeMs <= 0 || s.FocuserLargeNudgeMs <= 0) {
                throw new ArgumentException("Focuser: speed 1-4 and positive nudges");
            }
            if (s.WarmupRateCelsiusPerMinute <= 0 || s.FieldRotationBlurPixels <= 0) {
                throw new ArgumentException("Warm-up rate and field-rotation blur must be positive");
            }
            if (s.Solver != null && (s.Solver.CentreThresholdArcmin <= 0 || s.Solver.RecenterArcmin < 0)) {
                throw new ArgumentException("Plate solving: the centring threshold must be positive and the drift recentring 0 (off) or more");
            }
        }
    }

    /// <summary>About: names the fork, credits N.I.N.A. and states the licences.</summary>
    public sealed class AboutViewModel : ViewModelBase {

        public AboutViewModel(AppInfo info) {
            Info = info;
        }

        public AppInfo Info { get; }

        public string AppName => Info.DisplayName;

        public string VersionText => $"Version {Info.Version} (macOS arm64)";

        public string BasedOnText => $"Based on N.I.N.A. – Nighttime Imaging 'N' Astronomy ({Info.NinaBaseVersion}), © 2016–2026 Stefan Berg and the N.I.N.A. contributors.";

        public string LicenseText => "Licensed under the Mozilla Public License, v. 2.0 (MPL-2.0). Source files keep their MPL headers; the full licence text ships in the app bundle (Contents/Resources/LICENSE.txt).";

        public string DisclaimerText => "An independent fork for one rig. Not affiliated with or endorsed by the N.I.N.A. project.";

        public string[] ThirdParty { get; } = {
            "Avalonia UI 12 — MIT",
            "CommunityToolkit.Mvvm — MIT",
            "SkiaSharp, HarfBuzzSharp — MIT",
            ".NET runtime — MIT",
            "ZWO ASI Camera SDK — MIT-style (ZWO Company)",
            "libusb 1.0 — LGPL-2.1 (dynamically linked)",
            "Accord.NET / AForge.NET imaging (N.I.N.A.'s Accord.Imaging fork, NINA.Mac.ImageAnalysis.Accord) — LGPL-2.1 (separate assemblies)",
            "N.I.N.A. engine packages (Newtonsoft.Json, Serilog, Entity Framework 6, System.Data.SQLite, NCalc and others) — their own licences, listed in the notices",
            // SOFA licence clause 3(a): the work must carry this statement
            "IAU SOFA (libsofa) — SOFA Software License. This application uses routines and computations derived by its developers from software provided by SOFA under license to them, and does not itself constitute software provided by and/or endorsed by SOFA.",
            "NOVAS C3.1 (libnovas31) — Astronomical Applications Department, U.S. Naval Observatory",
            "JPL DE421 planetary ephemeris — Jet Propulsion Laboratory, California Institute of Technology",
            "Inter typeface — SIL Open Font License 1.1; Roboto — Apache-2.0",
        };

        public string NoticesText => "Full notices: Contents/Resources/THIRD-PARTY-NOTICES.txt and Contents/Resources/licenses/.";
    }
}
