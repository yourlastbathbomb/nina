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

namespace NINA.Mac.Siril.Test.Synthetic {

    /// <summary>Writes synthetic frames with the header NINA's FITSHeader.PopulateFromMetaData would write for this rig.</summary>
    public static class NinaFrames {

        public static FrameInfo Info(string imageType, DateTime utc, int number, double exposure, string target, SyntheticRig rig, bool darkFlat = false) {
            return new FrameInfo {
                ImageType = imageType,
                IsDarkFlat = darkFlat,
                ExposureStart = DateTime.SpecifyKind(utc, DateTimeKind.Utc),
                ExposureNumber = number,
                ExposureTime = exposure,
                TargetName = target,
                Gain = rig.Gain,
                Offset = rig.Offset,
                BinX = 2,
                BinY = 2,
                SensorTemperature = 0.1,
                SetPoint = 0.0,
            };
        }

        /// <summary>Header in NINA's order (NINA.Image/FileFormat/FITS/FITSHeader.cs:503-773), values for the ASI585MC at 2x2.</summary>
        public static FitsWriter Header(FrameInfo info, SyntheticRig rig) {
            var type = info.ImageType == "SNAPSHOT" ? "LIGHT" : info.ImageType == "DARKFLAT" ? "DARK" : info.ImageType;
            var utc = info.ExposureStartUtc;
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, SessionLayout.HongKong);
            var w = new FitsWriter()
                .Add("IMAGETYP", type, "Type of exposure")
                .Add("EXPOSURE", info.ExposureTime, "[s] Exposure duration")
                .Add("EXPTIME", info.ExposureTime, "[s] Exposure duration")
                .Add("DATE-LOC", local, "Time of observation (local)")
                .Add("DATE-OBS", utc, "Time of observation (UTC)")
                .Add("XBINNING", info.BinX, "X axis binning factor")
                .Add("YBINNING", info.BinY, "Y axis binning factor")
                .Add("GAIN", info.Gain, "Sensor gain")
                .Add("OFFSET", info.Offset, "Sensor gain offset")
                .Add("EGAIN", 0.25, "[e-/ADU] Electrons per A/D unit")
                .Add("XPIXSZ", 2.9 * info.BinX, "[um] Pixel X axis size")
                .Add("YPIXSZ", 2.9 * info.BinY, "[um] Pixel Y axis size")
                .Add("INSTRUME", "ZWO ASI585MC Pro", "Imaging instrument name")
                .Add("SET-TEMP", info.SetPoint, "[degC] CCD temperature setpoint")
                .Add("CCD-TEMP", info.SensorTemperature, "[degC] CCD temperature")
                .Add("BAYERPAT", "RGGB", "Sensor Bayer pattern")
                .Add("XBAYROFF", 0, "Bayer pattern X axis offset")
                .Add("YBAYROFF", 0, "Bayer pattern Y axis offset")
                .Add("USBLIMIT", 80, "Camera-specific USB setting")
                .Add("TELESCOP", "Meade LX200GPS 10in", "Name of telescope")
                .Add("FOCALLEN", 2500.0, "[mm] Focal length")
                .Add("FOCRATIO", 10.0, "Focal ratio");
            if (!string.IsNullOrEmpty(info.TargetName)) {
                w.Add("OBJECT", info.TargetName, "Name of the object of interest");
            }
            return w
                .Add("SITEELEV", 40.0, "[m] Observation site elevation")
                .Add("SITELAT", 22.25, "[deg] Observation site latitude")
                .Add("SITELONG", 114.18, "[deg] Observation site longitude")
                .Add("ROWORDER", "TOP-DOWN", "FITS Image Orientation")
                .Add("EQUINOX", 2000.0, "Equinox of celestial coordinate system")
                .Add("SWCREATE", "N.I.N.A. 3.3.0.1064 (arm64)", "Software that created this file");
        }

        /// <summary>Saves a frame where <paramref name="layout"/> routes it and returns the path.</summary>
        public static string Save(SessionLayout layout, FrameInfo info, SyntheticRig rig, ushort[] data) {
            var path = layout.GetFramePath(info);
            Header(info, rig).Write(path, data, rig.Width, rig.Height);
            return path;
        }
    }
}
