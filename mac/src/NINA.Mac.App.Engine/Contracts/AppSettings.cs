#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Mac.App.Services {

    /// <summary>Observing site. Defaults: Deep Water Bay, Hong Kong.</summary>
    public sealed class SiteSettings {
        public string Name { get; set; } = "Deep Water Bay, Hong Kong";
        public double LatitudeDegrees { get; set; } = 22.25;

        /// <summary>East positive.</summary>
        public double LongitudeDegrees { get; set; } = 114.18;

        public double ElevationMeters { get; set; } = 40;
        public double UtcOffsetHours { get; set; } = 8;

        public SiteSettings Clone() => (SiteSettings)MemberwiseClone();
    }

    /// <summary>Optical train. Defaults: Meade 10" LX200R f/10 + ZWO ASI585MC Pro, bin 2.</summary>
    public sealed class OpticsSettings {
        public double FocalLengthMm { get; set; } = 2500;
        public double ReducerFocalLengthMm { get; set; } = 1575;
        public bool UseReducer { get; set; }
        public double PixelSizeMicrons { get; set; } = 2.9;
        public int SensorWidth { get; set; } = 3840;
        public int SensorHeight { get; set; } = 2160;
        public int Bin { get; set; } = 2;

        [JsonIgnore]
        public double EffectiveFocalLengthMm => UseReducer ? ReducerFocalLengthMm : FocalLengthMm;

        /// <summary>Arcseconds per (binned) pixel.</summary>
        [JsonIgnore]
        public double PixelScaleArcsec => 206.264806 * PixelSizeMicrons * Bin / EffectiveFocalLengthMm;

        public OpticsSettings Clone() => (OpticsSettings)MemberwiseClone();
    }

    /// <summary>Which implementation backs the device services (Settings › Devices). Read at start-up.</summary>
    public enum DeviceSource {

        /// <summary>The built-in simulators: nothing is opened (indoor development). The default until the hardware path is proven.</summary>
        Simulated,

        /// <summary>The headless engine: NINA's ZWO camera driver, the LX200 driver on the selected serial port, the plate solvers.</summary>
        Real,
    }

    /// <summary>Plate solving with Real devices (NINA.Platesolving.Mac): ASTAP near solves, solve-field as the blind fallback.</summary>
    public sealed class SolverSettings {

        /// <summary>The ASTAP command-line solver; "~/" is the home folder.</summary>
        public string AstapExecutable { get; set; } = "~/Astro/astap/cli/astap_cli";

        /// <summary>ASTAP's star database folder (D80), passed as -d through a launcher script.</summary>
        public string AstapDatabase { get; set; } = "~/Astro/astap/d80";

        /// <summary>Folder holding astrometry.net's solve-field and wcsinfo (Homebrew).</summary>
        public string AstrometryBinDirectory { get; set; } = "/opt/homebrew/bin";

        /// <summary>Centring is done when the solved centre is within this many arcminutes of the target.</summary>
        public double CentreThresholdArcmin { get; set; } = 1.0;

        /// <summary>During a run, recentre when a drift check finds the frame this far off; 0 switches drift checks off.</summary>
        public double RecenterArcmin { get; set; } = 1.5;

        public SolverSettings Clone() => (SolverSettings)MemberwiseClone();
    }

    /// <summary>User settings, stored as JSON in ~/Library/Application Support/&lt;app&gt;/settings.json.</summary>
    public sealed class AppSettings {
        public int SchemaVersion { get; set; } = 1;
        public SiteSettings Site { get; set; } = new();
        public OpticsSettings Optics { get; set; } = new();

        /// <summary>Simulated (default) or Real devices; takes effect at the next start of the app.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter<DeviceSource>))]
        public DeviceSource DeviceSource { get; set; } = DeviceSource.Simulated;

        public SolverSettings Solver { get; set; } = new();

        /// <summary>Focus frames use the ZWO mono-bin mode at bin 2+ (Real camera only).</summary>
        public bool FocusMonoBin { get; set; } = true;

        /// <summary>A run first centres the target by plate solving (Real devices); otherwise it only slews.</summary>
        public bool CentreBeforeRun { get; set; } = true;

        /// <summary>Null means the default ~/Astro/&lt;app&gt;.</summary>
        public string ImagesRoot { get; set; }

        public bool KeepSystemAwake { get; set; } = true;
        public bool KeepDisplayAwake { get; set; } = true;
        public bool PreventAppNap { get; set; } = true;
        public bool NightVision { get; set; }

        /// <summary>Zenith keyhole for the alt-az fork (MAC_PORT_PLAN.md decision 5).</summary>
        public double MaxAltitudeDegrees { get; set; } = 75;

        public double MinAltitudeDegrees { get; set; } = 20;

        /// <summary>Northern sky is blocked at the site: azimuths from this value through north to <see cref="NorthBlockedToAzimuth"/>.</summary>
        public double NorthBlockedFromAzimuth { get; set; } = 300;

        public double NorthBlockedToAzimuth { get; set; } = 60;

        /// <summary>Field-rotation policy: warn when a sub would smear the corner by this many pixels (decision 4).</summary>
        public double FieldRotationBlurPixels { get; set; } = 1.0;

        public int FocuserSpeed { get; set; } = 2;
        public int FocuserSmallNudgeMs { get; set; } = 50;
        public int FocuserLargeNudgeMs { get; set; } = 250;
        public double CoolingTargetCelsius { get; set; } = 0;
        public double WarmupRateCelsiusPerMinute { get; set; } = 3;
        public int Gain { get; set; } = 252;
        public int Offset { get; set; } = 8;

        public AppSettings Clone() {
            var copy = (AppSettings)MemberwiseClone();
            copy.Site = Site.Clone();
            copy.Optics = Optics.Clone();
            copy.Solver = (Solver ?? new SolverSettings()).Clone();
            return copy;
        }

        /// <summary>True when <paramref name="azimuth"/> falls in the blocked northern wedge.</summary>
        public bool IsAzimuthBlocked(double azimuth) {
            var az = ((azimuth % 360) + 360) % 360;
            var from = ((NorthBlockedFromAzimuth % 360) + 360) % 360;
            var to = ((NorthBlockedToAzimuth % 360) + 360) % 360;
            return from <= to ? az >= from && az <= to : az >= from || az <= to;
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AppSettings))]
    internal sealed partial class SettingsJsonContext : JsonSerializerContext {
    }

    public interface ISettingsStore {

        AppSettings Current { get; }

        event EventHandler Changed;

        /// <summary>Replaces the settings and persists them.</summary>
        void Save(AppSettings settings);
    }

    /// <summary>JSON file store. A missing file gives defaults; an unreadable one is kept aside as settings.json.bad.</summary>
    public sealed class JsonSettingsStore : ISettingsStore {
        private readonly object lockobj = new();
        private AppSettings current;

        public JsonSettingsStore(string filePath) {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            FilePath = filePath;
            current = Load(filePath, out var warning);
            LoadWarning = warning;
        }

        public string FilePath { get; }

        /// <summary>Set when the file existed but could not be read.</summary>
        public string LoadWarning { get; }

        public event EventHandler Changed;

        public AppSettings Current {
            get {
                lock (lockobj) {
                    return current;
                }
            }
        }

        public void Save(AppSettings settings) {
            ArgumentNullException.ThrowIfNull(settings);
            var copy = settings.Clone();
            lock (lockobj) {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(copy, SettingsJsonContext.Default.AppSettings));
                File.Move(temp, FilePath, overwrite: true);
                current = copy;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        internal static AppSettings Load(string filePath, out string warning) {
            warning = null;
            if (!File.Exists(filePath)) {
                return new AppSettings();
            }
            try {
                var settings = JsonSerializer.Deserialize(File.ReadAllText(filePath), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
                settings.Site ??= new SiteSettings();
                settings.Optics ??= new OpticsSettings();
                settings.Solver ??= new SolverSettings();
                return settings;
            } catch (Exception ex) when (ex is JsonException || ex is IOException || ex is NotSupportedException) {
                var bad = filePath + ".bad";
                try {
                    File.Copy(filePath, bad, overwrite: true);
                } catch (IOException) {
                    // keep going with defaults
                }
                warning = $"Settings file could not be read ({ex.Message}); using defaults. The old file was kept as {Path.GetFileName(bad)}.";
                return new AppSettings();
            }
        }
    }

    /// <summary>In-memory store for tests and the smoke test.</summary>
    public sealed class MemorySettingsStore : ISettingsStore {

        public MemorySettingsStore(AppSettings settings = null) {
            Current = settings ?? new AppSettings();
        }

        public AppSettings Current { get; private set; }

        public event EventHandler Changed;

        public void Save(AppSettings settings) {
            Current = settings.Clone();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
