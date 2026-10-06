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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using SirilFrameInfo = NINA.Mac.Siril.FrameInfo;
using SirilLayout = NINA.Mac.Siril.SessionLayout;
using SirilLayoutOptions = NINA.Mac.Siril.SessionLayoutOptions;

namespace NINA.Mac.App.Services {

    public enum FrameType {
        Light,
        Dark,
        Flat,
        Bias,
        Snapshot,
    }

    /// <summary>
    /// The simulators' view of NINA.Mac.Siril's per-night layout: the same folders and file names the Real devices write through
    /// NINA's file patterns, so a simulated night reports the paths a real one would use.
    /// <code>
    /// &lt;root&gt;/&lt;night&gt;/flats|biases/            calibration taken without a target, shared by the night
    /// &lt;root&gt;/&lt;night&gt;/&lt;target&gt;/lights/          lights (snapshots in &lt;target&gt;/snapshots/, never in lights/)
    /// &lt;root&gt;/library/darks/&lt;exp&gt;_g&lt;gain&gt;_o&lt;offset&gt;_&lt;setpoint&gt;C_&lt;bin&gt;/   the dark library
    /// </code>
    /// The night is the site's local date 12 hours earlier (NINA's $$DATEMINUS12$$), so a session that crosses midnight stays
    /// in one folder.
    /// </summary>
    public sealed class SessionLayout : IImageFolders {
        private readonly SirilLayout siril;
        private readonly DateOnly night;
        private readonly DateTimeOffset sessionTime;

        public SessionLayout(string imagesRoot, DateTimeOffset sessionTime, double utcOffsetHours) {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagesRoot);
            if (!Path.IsPathFullyQualified(imagesRoot)) {
                throw new ArgumentException($"Images root must be absolute: '{imagesRoot}'", nameof(imagesRoot));
            }
            ImagesRoot = imagesRoot;
            this.sessionTime = sessionTime;
            var offset = TimeSpan.FromHours(utcOffsetHours);
            var zone = TimeZoneInfo.CreateCustomTimeZone($"site{offset}", offset, "Site", "Site");
            siril = new SirilLayout(new SirilLayoutOptions { Root = imagesRoot, SiteTimeZone = zone });
            night = siril.NightOf(sessionTime.UtcDateTime);
            NightName = SirilLayout.NightLabel(night);
        }

        public string ImagesRoot { get; }

        public string NightName { get; }

        public string NightDirectory => siril.NightDirectory(night);

        public string BiasesDirectory => Path.Combine(NightDirectory, SirilLayout.BiasesFolder);

        /// <summary>The dark library (one sub-folder per exposure, gain, offset, set point and binning).</summary>
        public string DarksDirectory => siril.Library.DarksDirectory;

        public string FlatsDirectory => Path.Combine(NightDirectory, SirilLayout.FlatsFolder);

        public string LightsDirectory(string target) => Target(target).Lights;

        public string SnapshotsDirectory(string target) => Target(target).Snapshots;

        public string DirectoryFor(FrameType type, string target) => type switch {
            FrameType.Light => LightsDirectory(target),
            FrameType.Dark => DarksDirectory,
            FrameType.Flat => FlatsDirectory,
            FrameType.Bias => BiasesDirectory,
            FrameType.Snapshot => SnapshotsDirectory(target),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        /// <summary>
        /// Where NINA's patterns would save this frame, file name included (e.g. NGC 253/lights/2026-10-10_21-00-00_10.00s_2x2_g252_-0.20C_0001.fits);
        /// darks go to their dark-library set folder. <paramref name="exposureStart"/> defaults to the session time.
        /// </summary>
        public string FramePath(FrameType type, string target, double exposureSeconds, int gain, int bin, double? sensorTemperature, int index,
                DateTimeOffset? exposureStart = null, int offset = -1, double? setPoint = null) {
            var frame = new SirilFrameInfo {
                ImageType = type switch {
                    FrameType.Light => "LIGHT",
                    FrameType.Dark => "DARK",
                    FrameType.Flat => "FLAT",
                    FrameType.Bias => "BIAS",
                    _ => "SNAPSHOT",
                },
                ExposureStart = (exposureStart ?? sessionTime).UtcDateTime,
                ExposureNumber = index,
                ExposureTime = exposureSeconds,
                // Calibration frames are taken without a target (night-level folders); lights and snapshots belong to one
                TargetName = type is FrameType.Light or FrameType.Snapshot ? NINA.Mac.App.Engine.EngineSessionService.SequenceTargetName(target) : null,
                Gain = gain,
                Offset = offset,
                BinX = bin,
                BinY = bin,
                SensorTemperature = sensorTemperature ?? double.NaN,
                SetPoint = setPoint ?? sensorTemperature ?? double.NaN,
            };
            return siril.GetFramePath(frame);
        }

        /// <summary>Siril command line that would stack a target from this night (shown, not run, in this build).</summary>
        public string SirilCommand(string target) =>
            $"siril-cli -d \"{Target(target).WorkingDirectory}\" -s OSC_Preprocessing.ssf";

        private NINA.Mac.Siril.TargetFolders Target(string target) => siril.GetTargetFolders(night, NINA.Mac.App.Engine.EngineSessionService.SequenceTargetName(target));

        /// <summary>Folder-safe target name: "NGC 253" becomes "NGC_253". Never empty (empty would drop a folder level).</summary>
        public static string SafeName(string name) {
            if (string.IsNullOrWhiteSpace(name)) {
                return "untitled";
            }
            var sb = new StringBuilder(name.Length);
            foreach (var c in name.Trim()) {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '+' || c == '.' ? c : '_');
            }
            var safe = string.Join("_", sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
            return safe.Trim('.').Length == 0 || safe.All(c => c == '.') ? "untitled" : safe;
        }
    }
}
