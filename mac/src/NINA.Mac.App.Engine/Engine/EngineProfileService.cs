#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Mac.App.Services;
using NINA.Mac.Equipment.Lx200;
using NINA.Mac.Siril;
using NINA.PlateSolving.Mac;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NINA.Mac.App.Engine {

    /// <summary>
    /// The one NINA profile of this rig, kept as a NINA profile file (<c>&lt;engine data&gt;/Profiles/&lt;id&gt;.profile</c>, NINA's
    /// own DataContract format with its journal and backup) so the engine's settings (the LX200 driver's port and bench values
    /// in the plugin store, NINA's camera defaults, solver settings) persist between nights. Upstream's ProfileService is not
    /// used: it manages several profiles, file watchers and instance hand-over, and raises its events through the WPF
    /// dispatcher. The app's own settings (settings.json) stay the source of truth for what the app shows; <see cref="RigProfile"/>
    /// copies them in at every start.
    /// </summary>
    public sealed class EngineProfileService : IProfileService, IDisposable {

        /// <summary>Fixed id, so every start opens the same file.</summary>
        public static readonly Guid RigProfileId = new("6e1f4c1a-3b9d-4f52-9a77-0c3e5d8b2a91");

        private readonly object lockobj = new();
        private global::NINA.Profile.Profile profile;
        private bool released;

        public EngineProfileService() {
            var path = ProfilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) {
                try {
                    profile = (global::NINA.Profile.Profile)global::NINA.Profile.Profile.Load(path);
                    LoadedFromFile = true;
                } catch (Exception ex) {
                    // Profile.Load already tried the journal and the backup; keep the unreadable file for inspection
                    Logger.Error($"Nightglass engine: the rig profile {path} could not be read; starting from defaults", ex);
                    LoadWarning = $"The engine profile could not be read ({ex.Message}); defaults were used and the old file was kept as {Path.GetFileName(path)}.bad";
                    TryCopy(path, path + ".bad");
                    profile = null;
                }
            }
            profile ??= new global::NINA.Profile.Profile("Nightglass rig") { Id = RigProfileId };
            Profiles = new AsyncObservableCollection<ProfileMeta> {
                new ProfileMeta { Id = profile.Id, Name = profile.Name, Location = profile.Location, LastUsed = DateTime.Now, IsActive = true }
            };
        }

        /// <summary>Where the rig profile lives (under <see cref="CoreUtil.APPLICATIONTEMPPATH"/>, as NINA's Profile.Location).</summary>
        public static string ProfilePath => Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Profiles", $"{RigProfileId}.profile");

        public bool LoadedFromFile { get; }

        /// <summary>Set when the file existed but could not be read.</summary>
        public string LoadWarning { get; }

        public bool ProfileWasSpecifiedFromCommandLineArgs => false;

        public AsyncObservableCollection<ProfileMeta> Profiles { get; }

        public IProfile ActiveProfile {
            get {
                lock (lockobj) {
                    return profile;
                }
            }
        }

        public event EventHandler LocaleChanged;

        public event EventHandler LocationChanged;

        /// <summary>Never raised: there is one profile.</summary>
        public event EventHandler BeforeProfileChanging {
            add { }
            remove { }
        }

        /// <summary>Never raised: there is one profile.</summary>
        public event EventHandler ProfileChanged {
            add { }
            remove { }
        }

        public event EventHandler HorizonChanged;

        /// <summary>Writes the profile file (NINA's journal, backup and replace).</summary>
        public void Save() {
            lock (lockobj) {
                if (!released) {
                    profile?.Save();
                }
            }
        }

        // One fixed profile: the multi-profile operations of NINA's profile manager are not offered
        public bool Clone(ProfileMeta profileInfos) => false;

        public void Add() {
        }

        public bool SelectProfile(ProfileMeta profileInfo) => profileInfo?.Id == RigProfileId;

        public bool RemoveProfile(ProfileMeta profileInfo) => false;

        public void ChangeLocale(CultureInfo language) {
            ActiveProfile.ApplicationSettings.Language = language;
            LocaleChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ChangeLatitude(double latitude) {
            ActiveProfile.AstrometrySettings.Latitude = latitude;
            LocationChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ChangeLongitude(double longitude) {
            ActiveProfile.AstrometrySettings.Longitude = longitude;
            LocationChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ChangeElevation(double elevation) {
            ActiveProfile.AstrometrySettings.Elevation = elevation;
            LocationChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// As upstream's ProfileService: sets the path and loads the horizon from it, here with the engine's reader
        /// (NINA.Mac.Sequencing's HorizonFile: inline comments allowed, NINA's CustomHorizon built from the points); cleared when
        /// empty or unreadable. HorizonChanged then lets HeadlessHost's ProfileHorizonWatcher reload it the same way.
        /// </summary>
        public void ChangeHorizon(string horizonFilePath) {
            var a = ActiveProfile.AstrometrySettings;
            a.HorizonFilePath = horizonFilePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(horizonFilePath)) {
                a.Horizon = null;
            } else {
                var loaded = NINA.Mac.Sequencing.Horizon.HorizonFile.Load(horizonFilePath);
                if (!loaded.Succeeded) {
                    Logger.Error($"Nightglass engine: {loaded.Summary}; the sequence uses no custom horizon");
                }
                a.Horizon = loaded.Horizon;
            }
            HorizonChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Saves and releases the file lock (NINA keeps the profile file open while it is active).</summary>
        public void Release() {
            lock (lockobj) {
                if (profile == null || released) {
                    return;
                }
                released = true;
                try {
                    profile.Save();
                } catch (Exception ex) {
                    Logger.Error("Nightglass engine: saving the rig profile failed", ex);
                }
                profile.Dispose();
            }
        }

        public void Dispose() => Release();

        private static void TryCopy(string from, string to) {
            try {
                File.Copy(from, to, overwrite: true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }
    }

    /// <summary>Paths of the plate solvers after resolving "~/" and ASTAP's database launcher.</summary>
    public sealed record SolverSetup(string AstapExecutable, string AstapDatabase, string AstapLocation, string AstrometryBinDirectory, string Problem);

    /// <summary>
    /// Copies the app's settings into the rig profile the engine runs on: site, optical train, camera defaults, NINA's file
    /// patterns for NINA.Mac.Siril's layout, the mount and focuser ids of the LX200 driver, the mount dither (DirectGuider),
    /// and the rig's plate-solve settings (RigPlateSolveDefaults: ASTAP near, solve-field blind).
    /// </summary>
    public static class RigProfile {

        /// <summary>NINA's DirectGuider ("Mount Dither") in GuiderChooser.</summary>
        public const string DirectGuiderName = "Direct_Guider";

        /// <summary>Default dither distance in (unbinned) pixels; DitherAsync overrides it per call.</summary>
        public const double DitherPixels = 5;

        /// <summary>The engine's copy of the app's horizon, in NINA's plain format (see <see cref="ApplyHorizon"/>).</summary>
        public const string HorizonFileName = "horizon.hrz";

        /// <summary>
        /// Gives the sequencer the app's horizon: writes it in NINA's plain format (no inline comments, points at 0° and 360°)
        /// to <paramref name="horizonFilePath"/> and points the profile at it, which loads NINA's CustomHorizon for
        /// WaitUntilAboveHorizon, AboveHorizonCondition and the plan validator. A null horizon clears the profile's.
        /// Returns the problem, or null.
        /// </summary>
        public static string ApplyHorizon(IProfileService profileService, HorizonProfile horizon, string horizonFilePath, string siteName = null) {
            ArgumentNullException.ThrowIfNull(profileService);
            if (horizon == null || string.IsNullOrWhiteSpace(horizonFilePath)) {
                profileService.ChangeHorizon(string.Empty);
                return null;
            }
            try {
                // Written with the engine's writer (plain format upstream reads the same), with explicit 0° and 360° points so the
                // grooming of a missing end never changes the shape across north
                var points = horizon.Points.Select(p => (p.Azimuth, p.Altitude)).ToList();
                if (!points.Any(p => p.Azimuth == 0)) {
                    points.Add((0, horizon.GetAltitude(0)));
                }
                if (!points.Any(p => p.Azimuth == 360)) {
                    points.Add((360, horizon.GetAltitude(0)));
                }
                var comments = new List<string> { $"Nightglass local horizon{(string.IsNullOrWhiteSpace(siteName) ? "" : " for " + siteName)}, from {horizon.Source ?? "the app"}; rewritten at every start and save" };
                if (horizon.IsEstimate) {
                    comments.Add($"{HorizonProfile.EstimateMarker}: ESTIMATE ONLY, not measured");
                }
                NINA.Mac.Sequencing.Horizon.HorizonFile.Save(horizonFilePath, points, comments);
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) {
                profileService.ChangeHorizon(string.Empty);
                return $"Could not write the engine's horizon file {horizonFilePath}: {ex.Message}";
            }
            profileService.ChangeHorizon(horizonFilePath);
            return profileService.ActiveProfile.AstrometrySettings.Horizon == null ? $"NINA could not read the horizon file {horizonFilePath}" : null;
        }

        public static SolverSetup Apply(IProfileService profileService, AppSettings settings, string imagesRoot, string home = null) {
            ArgumentNullException.ThrowIfNull(profileService);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentException.ThrowIfNullOrWhiteSpace(imagesRoot);
            var p = profileService.ActiveProfile;
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var site = settings.Site;
            p.AstrometrySettings.Latitude = site.LatitudeDegrees;
            p.AstrometrySettings.Longitude = site.LongitudeDegrees;
            p.AstrometrySettings.Elevation = site.ElevationMeters;

            var optics = settings.Optics;
            var t = p.TelescopeSettings;
            t.Id = Lx200Telescope.DeviceId;
            t.Name = "Meade LX200GPS 10\"";
            t.FocalLength = optics.EffectiveFocalLengthMm;
            t.FocalRatio = Math.Round(optics.EffectiveFocalLengthMm / 254.0, 2);
            // The lat/long prompt is a dialog (HeadlessWindowServiceFactory.ShowDialog throws); the LX200 driver keeps the site
            t.TelescopeLocationSyncDirection = TelescopeLocationSyncDirection.NOSYNC;

            var c = p.CameraSettings;
            c.PixelSize = optics.PixelSizeMicrons;
            c.BinningX = (short)optics.Bin;
            c.BinningY = (short)optics.Bin;
            c.Gain = settings.Gain;
            c.Offset = settings.Offset;
            c.Temperature = settings.CoolingTargetCelsius;
            // Focus frames switch ZWO mono-bin on only for themselves; lights must keep their Bayer pattern
            c.ZwoAsiMonoBinMode = false;

            p.FocuserSettings.Id = Lx200Focuser.DeviceId;

            var g = p.GuiderSettings;
            g.GuiderName = DirectGuiderName;
            if (g.DitherPixels <= 0) {
                g.DitherPixels = DitherPixels;
            }

            var files = p.ImageFileSettings;
            files.FilePath = imagesRoot;
            files.FilePattern = NinaFilePatterns.Light;
            files.FilePatternFLAT = NinaFilePatterns.Flat;
            files.FilePatternBIAS = NinaFilePatterns.Bias;
            files.FilePatternDARK = NinaFilePatterns.Dark;
            files.FileType = FileTypeEnum.FITS;

            p.ApplicationSettings.DevicePollingInterval = 1;

            var lx = new Lx200Settings(profileService);
            lx.FocuserSpeed = Math.Clamp(settings.FocuserSpeed, 1, 4);
            // The driver's own slew guard uses the same keyhole limit as the app's (Settings › Limits), not its 75° default
            lx.MaxAltitudeDegrees = settings.MaxAltitudeDegrees;

            var solver = settings.Solver ?? new SolverSettings();
            var astapExecutable = ExpandHome(solver.AstapExecutable, home);
            var astapDatabase = ExpandHome(solver.AstapDatabase, home);
            var binDirectory = ExpandHome(solver.AstrometryBinDirectory, home);
            string astapLocation = string.Empty;
            string problem = null;
            if (string.IsNullOrWhiteSpace(astapExecutable) || !File.Exists(astapExecutable)) {
                problem = $"ASTAP not found at '{astapExecutable}' (Settings › Plate solving)";
            } else {
                try {
                    astapLocation = AstapSetup.Resolve(astapExecutable, astapDatabase);
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) {
                    problem = $"ASTAP launcher: {ex.Message}";
                }
            }
            RigPlateSolveDefaults.Apply(p.PlateSolveSettings, astapLocation, binDirectory);
            if (solver.CentreThresholdArcmin > 0) {
                p.PlateSolveSettings.Threshold = solver.CentreThresholdArcmin;
            }
            return new SolverSetup(astapExecutable, astapDatabase, astapLocation, binDirectory, problem);
        }

        internal static string ExpandHome(string path, string home) {
            if (string.IsNullOrWhiteSpace(path)) {
                return path;
            }
            var trimmed = path.Trim();
            if (trimmed == "~") {
                return home;
            }
            return trimmed.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, trimmed[2..]) : trimmed;
        }
    }
}
