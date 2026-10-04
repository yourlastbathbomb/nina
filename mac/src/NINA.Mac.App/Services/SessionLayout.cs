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

namespace NINA.Mac.App.Services {

    public enum FrameType {
        Light,
        Dark,
        Flat,
        Bias,
        Snapshot,
    }

    /// <summary>
    /// Per-night folder layout that Siril's OSC_Preprocessing / OSC_Extract_HaOIII scripts can use (research
    /// rig_verify_mvp.md MVP-M2): calibration shared per night, lights per target, snapshots kept out of lights.
    /// <code>
    /// &lt;root&gt;/&lt;night&gt;/biases|darks|flats/
    /// &lt;root&gt;/&lt;night&gt;/lights/&lt;target&gt;/
    /// &lt;root&gt;/&lt;night&gt;/snapshots/
    /// </code>
    /// The night is the local date 12 hours earlier, so a session that crosses midnight stays in one folder.
    /// </summary>
    public sealed class SessionLayout {

        public SessionLayout(string imagesRoot, DateTimeOffset sessionTime, double utcOffsetHours) {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagesRoot);
            if (!Path.IsPathFullyQualified(imagesRoot)) {
                throw new ArgumentException($"Images root must be absolute: '{imagesRoot}'", nameof(imagesRoot));
            }
            ImagesRoot = imagesRoot;
            var local = sessionTime.ToOffset(TimeSpan.FromHours(utcOffsetHours));
            NightName = local.AddHours(-12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public string ImagesRoot { get; }

        public string NightName { get; }

        public string NightDirectory => Path.Combine(ImagesRoot, NightName);

        public string BiasesDirectory => Path.Combine(NightDirectory, "biases");

        public string DarksDirectory => Path.Combine(NightDirectory, "darks");

        public string FlatsDirectory => Path.Combine(NightDirectory, "flats");

        public string SnapshotsDirectory => Path.Combine(NightDirectory, "snapshots");

        public string LightsDirectory(string target) => Path.Combine(NightDirectory, "lights", SafeName(target));

        public string DirectoryFor(FrameType type, string target) => type switch {
            FrameType.Light => LightsDirectory(target),
            FrameType.Dark => DarksDirectory,
            FrameType.Flat => FlatsDirectory,
            FrameType.Bias => BiasesDirectory,
            FrameType.Snapshot => SnapshotsDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        /// <summary>e.g. lights/NGC_253/NGC_253_L_10s_G252_B2_0C_0001.fits</summary>
        public string FramePath(FrameType type, string target, double exposureSeconds, int gain, int bin, double? sensorTemperature, int index) {
            var prefix = type == FrameType.Light || type == FrameType.Snapshot ? SafeName(target) + "_" : "";
            var code = type switch {
                FrameType.Light => "L",
                FrameType.Dark => "D",
                FrameType.Flat => "F",
                FrameType.Bias => "B",
                _ => "S",
            };
            var temp = sensorTemperature is { } t ? $"_{(Math.Round(t) + 0.0).ToString("0", CultureInfo.InvariantCulture)}C" : ""; // + 0.0 turns -0 into 0
            var exposure = exposureSeconds.ToString(exposureSeconds >= 1 ? "0.#" : "0.###", CultureInfo.InvariantCulture);
            var name = $"{prefix}{code}_{exposure}s_G{gain}_B{bin}{temp}_{index:0000}.fits";
            return Path.Combine(DirectoryFor(type, target), name);
        }

        /// <summary>Siril command line that would stack a target from this night (shown, not run, in this build).</summary>
        public string SirilCommand(string target) =>
            $"siril-cli -d \"{Path.Combine(NightDirectory, "siril", SafeName(target))}\" -s OSC_Preprocessing.ssf";

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
