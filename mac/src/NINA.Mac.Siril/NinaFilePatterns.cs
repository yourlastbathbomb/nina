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
using System.Collections.Generic;
using System.IO;

namespace NINA.Mac.Siril {

    /// <summary>
    /// NINA profile file patterns (Options › Imaging › File patterns) that make NINA write the <see cref="SessionLayout"/>
    /// tree for targets whose names pass <see cref="SessionLayout.KeepsNinaFolderName"/>, plus <see cref="Expand"/>: a
    /// port of NINA's ImagePatterns.GetImageFileString with the separator fix and the $$IMAGETYPEDIR$$ token proposed
    /// upstream (see README "Proposed upstream patch"). Image file path (profile) = <see cref="SessionLayout.Root"/>.
    /// </summary>
    public static class NinaFilePatterns {

        /// <summary>Proposed token: LIGHT→lights, SNAPSHOT→snapshots, FLAT→flats, DARK→darks, BIAS→biases.</summary>
        public const string ImageTypeDirToken = "$$IMAGETYPEDIR$$";

        /// <summary>File name part shared by all frame types; starts with the local date-time so name order is time order (SIR-03).</summary>
        public const string FileName = "$$DATETIME$$_$$EXPOSURETIME$$s_$$BINNING$$_g$$GAIN$$_$$SENSORTEMP$$C_$$FRAMENR$$";

        /// <summary>FilePattern (lights). Stock NINA also routes SNAPSHOT frames through this pattern, i.e. into lights/.</summary>
        public const string Light = "$$DATEMINUS12$$/$$TARGETNAME$$/lights/" + FileName;

        /// <summary>FilePatternFLAT. Flats taken without a target collapse to the night folder (empty segments are dropped).</summary>
        public const string Flat = "$$DATEMINUS12$$/$$TARGETNAME$$/flats/" + FileName;

        /// <summary>FilePatternBIAS.</summary>
        public const string Bias = "$$DATEMINUS12$$/$$TARGETNAME$$/biases/" + FileName;

        /// <summary>FilePatternDARK. Stock NINA types dark flats as DARK, so they land here too (as a short-exposure set).</summary>
        public const string Dark = "library/darks/$$EXPOSURETIME$$s_g$$GAIN$$_o$$OFFSET$$_$$TEMPERATURESETPOINT$$C_$$BINNING$$/" + FileName;

        /// <summary>With the proposed token, one FilePattern covers lights, snapshots, flats and biases.</summary>
        public const string WithImageTypeDir = "$$DATEMINUS12$$/$$TARGETNAME$$/" + ImageTypeDirToken + "/" + FileName;

        /// <summary>Upstream default (NINA.Profile/ImageFileSettings.cs:34): backslash separators, which macOS .NET treats as file-name characters.</summary>
        public const string UpstreamDefault = "$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$SENSORTEMP$$_$$EXPOSURETIME$$s_$$FRAMENR$$";

        /// <summary>Every token upstream registers (NINA.Core/Model/ImagePattern.cs:242-277); unset ones expand to "".</summary>
        private static readonly string[] UpstreamTokens = {
            "$$FILTER$$", "$$DATE$$", "$$DATEUTC$$", "$$DATEMINUS12$$", "$$DATETIME$$", "$$TIME$$", "$$TIMEUTC$$", "$$MJD$$",
            "$$FRAMENR$$", "$$IMAGETYPE$$", "$$BINNING$$", "$$SENSORTEMP$$", "$$TEMPERATURESETPOINT$$", "$$EXPOSURETIME$$",
            "$$TARGETNAME$$", "$$GAIN$$", "$$OFFSET$$", "$$RMS$$", "$$RMSARCSEC$$", "$$PEAKRA$$", "$$PEAKRAARCSEC$$", "$$PEAKDEC$$",
            "$$PEAKDECARCSEC$$", "$$FOCUSERPOSITION$$", "$$FOCUSERTEMP$$", "$$APPLICATIONSTARTDATE$$", "$$HFR$$", "$$SQM$$",
            "$$READOUTMODE$$", "$$USBLIMIT$$", "$$CAMERA$$", "$$TELESCOPE$$", "$$ROTATORANGLE$$", "$$STARCOUNT$$", "$$SEQUENCETITLE$$",
        };

        /// <summary>Folder name for the proposed $$IMAGETYPEDIR$$ token.</summary>
        public static string ImageTypeDirectory(string imageType) {
            switch ((imageType ?? string.Empty).Trim().ToUpperInvariant()) {
                case "LIGHT": return SessionLayout.LightsFolder;
                case "SNAPSHOT": return SessionLayout.SnapshotsFolder;
                case "FLAT": return SessionLayout.FlatsFolder;
                case "DARK": return SessionLayout.DarksFolder;
                case "BIAS": return SessionLayout.BiasesFolder;
                case "DARKFLAT": return SessionLayout.BiasesFolder;
                default: return (imageType ?? string.Empty).Trim().ToLowerInvariant();
            }
        }

        /// <summary>Upstream ImageFileSettings.GetFilePattern (NINA.Profile/ImageFileSettings.cs:238-252) with these patterns filled in.</summary>
        public static string PatternFor(string imageType) {
            switch ((imageType ?? string.Empty).Trim()) {
                case "DARK": return Dark;
                case "FLAT": return Flat;
                case "BIAS": return Bias;
                default: return Light;
            }
        }

        /// <summary>
        /// Relative path (no extension) NINA would build from <paramref name="pattern"/>: a port of
        /// ImagePatterns.GetImageFileString (NINA.Core/Model/ImagePattern.cs:181-200) as this fork builds it: split on
        /// both '/' and '\' (CoreUtil.PATHSEPARATORS, patched; upstream gets only '/' on macOS), Windows' invalid
        /// file-name characters replaced on every OS, plus the proposed $$IMAGETYPEDIR$$ token. Values are formatted as
        /// BaseImageData.GetImagePatterns does (BaseImageData.cs:223-283), times in <paramref name="zone"/>.
        /// </summary>
        public static string Expand(string pattern, FrameInfo frame, TimeZoneInfo zone) {
            var v = NinaTokenValues.From(frame, zone);
            var values = new Dictionary<string, string> {
                ["$$DATE$$"] = v.Date,
                ["$$DATEMINUS12$$"] = v.DateMinus12,
                ["$$DATETIME$$"] = v.DateTime,
                ["$$FRAMENR$$"] = v.FrameNr,
                ["$$IMAGETYPE$$"] = v.ImageType,
                ["$$TARGETNAME$$"] = v.TargetName,
                ["$$EXPOSURETIME$$"] = v.ExposureTime,
                ["$$BINNING$$"] = v.Binning,
                ["$$SENSORTEMP$$"] = v.SensorTemp,
                ["$$TEMPERATURESETPOINT$$"] = v.SetPoint,
                ["$$GAIN$$"] = v.Gain,
                ["$$OFFSET$$"] = v.Offset,
                [ImageTypeDirToken] = NinaTokenValues.Sanitize(ImageTypeDirectory(frame.IsDarkFlat ? "DARKFLAT" : frame.ImageType)),
            };

            var s = pattern;
            foreach (var token in UpstreamTokens) {
                s = s.Replace(token, values.TryGetValue(token, out var value) ? value : string.Empty);
            }
            s = s.Replace(ImageTypeDirToken, values[ImageTypeDirToken]);

            var segments = s.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var result = string.Empty;
            foreach (var segment in segments) {
                result = Path.Combine(result, string.Join("_", segment.Split(NinaTokenValues.PortableInvalidFileNameChars)).Trim());
            }
            return result;
        }
    }
}
