#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.ImageAnalysis.AccordPort;
using System;
using System.Diagnostics;
using System.Threading;

namespace NINA.Mac.ImageAnalysis {

    /// <summary>
    /// The profile settings that shape N.I.N.A.'s image preparation and star detection. Defaults are the profile
    /// defaults: NINA.Profile/ImageSettings.cs:34-42 and FocuserSettings.cs:43-45.
    /// </summary>
    public sealed class FrameAnalysisOptions {
        /// <summary>
        /// The image panel's auto-stretch toggle (ImageSettings.AutoStretch, profile default true). As upstream
        /// (ImageControlVM.cs:668), the stretch is forced on whenever <see cref="DetectStars"/> is set; with both off,
        /// the display image and the Bahtinov input are the linear data.
        /// </summary>
        public bool AutoStretch { get; set; } = true;

        public double AutoStretchFactor { get; set; } = NINA.Mac.ImageAnalysis.AutoStretch.DefaultFactor;
        public double BlackClipping { get; set; } = NINA.Mac.ImageAnalysis.AutoStretch.DefaultBlackClipping;
        public bool DebayerImage { get; set; } = true;
        public bool DebayeredHFR { get; set; } = true;
        public bool UnlinkedStretch { get; set; } = true;
        public StarSensitivity StarSensitivity { get; set; } = StarSensitivity.High;
        public NoiseReduction NoiseReduction { get; set; } = NoiseReduction.None;
        public double InnerCropRatio { get; set; } = 1;
        public double OuterCropRatio { get; set; } = 1;
        public int UseBrightestStars { get; set; } = 0;

        /// <summary>Whether to detect stars at all (the HFR toggle in the imaging/snapshot panels).</summary>
        public bool DetectStars { get; set; } = true;

        /// <summary>Unbinned pixel size (µm). Only High sensitivity uses it; NaN = unknown.</summary>
        public double PixelSizeMicrons { get; set; } = double.NaN;

        /// <summary>Focal length (mm). Only High sensitivity uses it; NaN = unknown.</summary>
        public double FocalLengthMm { get; set; } = double.NaN;

        /// <summary>ASI585MC Pro (2.9 µm) on the 10" LX200R at f/10 (2500 mm).</summary>
        public static FrameAnalysisOptions ForRig(bool reducer = false) {
            return new FrameAnalysisOptions {
                PixelSizeMicrons = 2.9,
                FocalLengthMm = reducer ? 2500 * 0.63 : 2500
            };
        }

        public StarDetectionParams ToStarDetectionParams() {
            var p = new StarDetectionParams {
                IsAutoFocus = false,
                Sensitivity = StarSensitivity,
                NoiseReduction = NoiseReduction
            };
            p.WithCropRatios(InnerCropRatio, OuterCropRatio);
            if (UseBrightestStars > 0) {
                p.NumberOfAFStars = UseBrightestStars;
            }
            return p;
        }
    }

    /// <summary>Everything one analysis pass produced.</summary>
    public sealed class FrameAnalysis {
        public ImageStatistics Statistics { get; internal set; }

        /// <summary>Null when star detection was off.</summary>
        public StarDetectionResult Stars { get; internal set; }

        /// <summary>
        /// True when the display image was auto-stretched: always when stars were detected, otherwise when
        /// <see cref="FrameAnalysisOptions.AutoStretch"/> is set (ImageControlVM.cs:668).
        /// </summary>
        public bool IsStretched { get; internal set; }

        /// <summary>
        /// Upstream's displayed image (ImageControlVM.Image) as Gray16, or null when <see cref="DisplayRgb48"/> is set.
        /// Star detection runs on it. Stretched when <see cref="IsStretched"/> is true; otherwise it is the raw frame
        /// data itself (not a copy), as upstream's unstretched bitmap is.
        /// </summary>
        public ushort[] Display16 { get; internal set; }

        /// <summary>
        /// The debayered display image (R, G, B order), or null for mono. Stretched when <see cref="IsStretched"/> is
        /// true, otherwise the linear debayered data.
        /// </summary>
        public ushort[] DisplayRgb48 { get; internal set; }

        public int Width { get; internal set; }
        public int Height { get; internal set; }

        public TimeSpan StatisticsTime { get; internal set; }
        public TimeSpan RenderTime { get; internal set; }
        public TimeSpan DetectionTime { get; internal set; }
        public TimeSpan TotalTime { get; internal set; }

        /// <summary>
        /// The 8 bit view of the display image, as BahtinovAnalysis.cs:41-51 converts it: Gray16 by the top byte, Rgb48
        /// through Grayscale(0.2125, 0.7154, 0.0721) on the GDI+ view first.
        /// </summary>
        public Gray8Image GetDisplayGray8() {
            var gray16 = DisplayRgb48 != null
                ? PixelConversions.Grayscale48To16(DisplayRgb48, Width, Height, 0.2125, 0.7154, 0.0721)
                : Display16;
            return PixelConversions.Convert16To8(gray16, Width, Height);
        }
    }

    /// <summary>
    /// Replays N.I.N.A.'s image preparation for one frame: ImageControlVM.PrepareImage (ImageControlVM.cs:604-631:
    /// render, debayer when bayered and enabled), ProcessImage (:663-689: auto stretch when enabled, forced on when
    /// stars are detected; then RenderedImage.DetectStars) and the statistics NINA computes for every frame
    /// (BaseImageData.Statistics). Pure managed code, safe to call from any thread.
    /// </summary>
    public static class FrameAnalyzer {

        public static FrameAnalysis Analyze(PixelBuffer frame, FrameAnalysisOptions options = null, CancellationToken token = default) {
            options ??= new FrameAnalysisOptions();
            var total = Stopwatch.StartNew();
            var analysis = new FrameAnalysis { Width = frame.Width, Height = frame.Height };

            var sw = Stopwatch.StartNew();
            var statistics = ImageStatistics.Create(frame);
            analysis.Statistics = statistics;
            analysis.StatisticsTime = sw.Elapsed;

            sw.Restart();
            // ImageControlVM.cs:667-677: stretch when detecting stars or when the auto-stretch toggle is on
            bool stretch = options.DetectStars || options.AutoStretch;
            analysis.IsStretched = stretch;
            ushort[] measurement = frame.Data;
            if (frame.IsBayered && options.DebayerImage) {
                // ImageControlVM.cs:605-631: saveColorChannels = unlinked stretch, saveLumChannel = debayered HFR && detect
                bool saveLum = options.DebayeredHFR && options.DetectStars;
                var debayered = Debayer.Apply(frame, saveColorChannels: options.UnlinkedStretch, saveLumChannel: saveLum);
                if (saveLum && debayered.Lum != null && debayered.Lum.Length > 0) {
                    // StarDetection.cs:60-67: HFR and local maxima on the debayered luminance
                    measurement = debayered.Lum;
                }

                // ImageControlVM.cs:672 / DebayeredImage.Stretch: unlinked only when the colour channels were kept
                if (!stretch) {
                    // the debayered bitmap is displayed as is
                    analysis.DisplayRgb48 = debayered.Rgb;
                } else if (options.UnlinkedStretch && debayered.Red != null) {
                    var mapR = AutoStretch.GetStretchMap(ImageStatistics.Create(debayered.Red, frame.BitDepth), options.AutoStretchFactor, options.BlackClipping);
                    var mapG = AutoStretch.GetStretchMap(ImageStatistics.Create(debayered.Green, frame.BitDepth), options.AutoStretchFactor, options.BlackClipping);
                    var mapB = AutoStretch.GetStretchMap(ImageStatistics.Create(debayered.Blue, frame.BitDepth), options.AutoStretchFactor, options.BlackClipping);
                    analysis.DisplayRgb48 = AutoStretch.ApplyRgb(debayered.Rgb, mapR, mapG, mapB);
                } else {
                    // linked: one map from the raw (mosaic) statistics for all channels (ImageUtility.Stretch)
                    var map = AutoStretch.GetStretchMap(statistics, options.AutoStretchFactor, options.BlackClipping);
                    analysis.DisplayRgb48 = AutoStretch.ApplyRgb(debayered.Rgb, map, map, map);
                }
            } else if (!stretch) {
                // RenderBitmapSource (BaseImageData.cs:83-85) wraps the raw array
                analysis.Display16 = frame.Data;
            } else {
                var map = AutoStretch.GetStretchMap(statistics, options.AutoStretchFactor, options.BlackClipping);
                analysis.Display16 = AutoStretch.Apply(frame.Data, map);
            }
            analysis.RenderTime = sw.Elapsed;

            if (options.DetectStars) {
                sw.Restart();
                var input = new StarDetectionInput {
                    Width = frame.Width,
                    Height = frame.Height,
                    DetectionImage16 = analysis.Display16,
                    DetectionImageRgb48 = analysis.DisplayRgb48,
                    MeasurementData = measurement,
                    PixelSizeMicrons = options.PixelSizeMicrons,
                    FocalLengthMm = options.FocalLengthMm
                };
                analysis.Stars = new StarDetector().Detect(input, options.ToStarDetectionParams(), token);
                analysis.DetectionTime = sw.Elapsed;
            }
            analysis.TotalTime = total.Elapsed;
            return analysis;
        }

        /// <summary>
        /// The Bahtinov analyser as N.I.N.A. runs it (ImageControlVM.cs:278-282): on a crop (default 200 x 200) of the
        /// display image, stretched or linear depending on <see cref="FrameAnalysis.IsStretched"/>.
        /// <paramref name="analysis"/> must come from <see cref="Analyze"/> on the same frame.
        /// </summary>
        /// <param name="robust">False (default) runs the faithful N.I.N.A. analyser; true runs <see cref="BahtinovAnalyzer.AnalyzeRobust"/>.</param>
        public static BahtinovResult AnalyzeBahtinov(FrameAnalysis analysis, PixelRect crop, BahtinovResult previous = null, bool robust = false) {
            var gray8 = analysis.GetDisplayGray8().Crop(crop.X, crop.Y, crop.Width, crop.Height);
            return robust ? BahtinovAnalyzer.AnalyzeRobust(gray8, previous) : BahtinovAnalyzer.Analyze(gray8, previous);
        }
    }
}
