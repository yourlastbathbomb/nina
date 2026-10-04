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
        public double AutoStretchFactor { get; set; } = AutoStretch.DefaultFactor;
        public double BlackClipping { get; set; } = AutoStretch.DefaultBlackClipping;
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

        /// <summary>The stretched display image star detection looked at: Gray16 (mono) or null when Rgb48 is set.</summary>
        public ushort[] Stretched16 { get; internal set; }

        /// <summary>The stretched debayered display image (R, G, B order), or null for mono.</summary>
        public ushort[] StretchedRgb48 { get; internal set; }

        public int Width { get; internal set; }
        public int Height { get; internal set; }

        public TimeSpan StatisticsTime { get; internal set; }
        public TimeSpan RenderTime { get; internal set; }
        public TimeSpan DetectionTime { get; internal set; }
        public TimeSpan TotalTime { get; internal set; }

        /// <summary>The 8 bit view of the stretched image (what the Bahtinov analyser and upstream's display use).</summary>
        public Gray8Image GetDisplayGray8() {
            var gray16 = StretchedRgb48 != null
                ? PixelConversions.Grayscale48To16(StretchedRgb48, Width, Height, 0.2125, 0.7154, 0.0721)
                : Stretched16;
            return PixelConversions.Convert16To8(gray16, Width, Height);
        }
    }

    /// <summary>
    /// Replays N.I.N.A.'s image preparation for one frame: ImageControlVM.PrepareImage (ImageControlVM.cs:604-631:
    /// render, debayer when bayered and enabled), ProcessImage (:663-689: auto stretch, forced on when stars are
    /// detected; then RenderedImage.DetectStars) and the statistics NINA computes for every frame
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
                if (options.UnlinkedStretch && debayered.Red != null) {
                    var mapR = AutoStretch.GetStretchMap(ImageStatistics.Create(debayered.Red, frame.BitDepth), options.AutoStretchFactor, options.BlackClipping);
                    var mapG = AutoStretch.GetStretchMap(ImageStatistics.Create(debayered.Green, frame.BitDepth), options.AutoStretchFactor, options.BlackClipping);
                    var mapB = AutoStretch.GetStretchMap(ImageStatistics.Create(debayered.Blue, frame.BitDepth), options.AutoStretchFactor, options.BlackClipping);
                    analysis.StretchedRgb48 = AutoStretch.ApplyRgb(debayered.Rgb, mapR, mapG, mapB);
                } else {
                    // linked: one map from the raw (mosaic) statistics for all channels (ImageUtility.Stretch)
                    var map = AutoStretch.GetStretchMap(statistics, options.AutoStretchFactor, options.BlackClipping);
                    analysis.StretchedRgb48 = AutoStretch.ApplyRgb(debayered.Rgb, map, map, map);
                }
            } else {
                var map = AutoStretch.GetStretchMap(statistics, options.AutoStretchFactor, options.BlackClipping);
                analysis.Stretched16 = AutoStretch.Apply(frame.Data, map);
            }
            analysis.RenderTime = sw.Elapsed;

            if (options.DetectStars) {
                sw.Restart();
                var input = new StarDetectionInput {
                    Width = frame.Width,
                    Height = frame.Height,
                    DetectionImage16 = analysis.Stretched16,
                    DetectionImageRgb48 = analysis.StretchedRgb48,
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
        /// The Bahtinov analyser as N.I.N.A. runs it: on a crop (default 200 x 200 in ImageControlVM) of the stretched
        /// display image. <paramref name="analysis"/> must come from <see cref="Analyze"/> on the same frame.
        /// </summary>
        /// <param name="robust">False (default) runs the faithful N.I.N.A. analyser; true runs <see cref="BahtinovAnalyzer.AnalyzeRobust"/>.</param>
        public static BahtinovResult AnalyzeBahtinov(FrameAnalysis analysis, PixelRect crop, BahtinovResult previous = null, bool robust = false) {
            var gray8 = analysis.GetDisplayGray8().Crop(crop.X, crop.Y, crop.Width, crop.Height);
            return robust ? BahtinovAnalyzer.AnalyzeRobust(gray8, previous) : BahtinovAnalyzer.Analyze(gray8, previous);
        }
    }
}
