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
using System.Collections.Generic;

namespace NINA.Mac.ImageAnalysis {

    /// <summary>NINA.Core.Enum.StarSensitivityEnum (same order/values).</summary>
    public enum StarSensitivity {
        Normal,
        High,
        Highest
    }

    /// <summary>NINA.Core.Enum.NoiseReductionEnum (same order/values).</summary>
    public enum NoiseReduction {
        None,
        Median,
        Normal,
        High,
        Highest
    }

    /// <summary>
    /// Port of NINA.Image/ImageAnalysis/IStarDetection.cs:36-45 (StarDetectionParams). Property defaults are the
    /// class defaults; RenderedImage.DetectStars (RenderedImage.cs:87-102) fills Sensitivity/NoiseReduction from the
    /// profile (defaults High / None) and the crop ratios from the focuser settings (defaults 1 = off).
    /// </summary>
    public sealed class StarDetectionParams {
        public StarSensitivity Sensitivity { get; set; } = StarSensitivity.Normal;
        public NoiseReduction NoiseReduction { get; set; } = NoiseReduction.None;
        public bool IsAutoFocus { get; set; }
        public bool UseROI { get; set; } = false;
        public double InnerCropRatio { get; set; } = 1.0d;
        public double OuterCropRatio { get; set; } = 1.0d;
        public int NumberOfAFStars { get; set; } = 0;
        public List<AccordPoint> MatchStarPositions { get; set; } = new List<AccordPoint>();

        /// <summary>
        /// Parameters exactly as N.I.N.A. builds them for the imaging/snapshot pipeline with a default profile
        /// (ImageSettings.StarSensitivity = High, NoiseReduction = None, AutoFocus crop ratios 1, brightest stars 0).
        /// </summary>
        public static StarDetectionParams NinaProfileDefaults() {
            return new StarDetectionParams {
                IsAutoFocus = false,
                Sensitivity = StarSensitivity.High,
                NoiseReduction = NoiseReduction.None
            };
        }

        /// <summary>RenderedImage.DetectStars: crop ratios &lt; 1 switch UseROI on.</summary>
        public StarDetectionParams WithCropRatios(double innerCropRatio, double outerCropRatio) {
            if (innerCropRatio < 1) {
                InnerCropRatio = innerCropRatio;
                UseROI = true;
            }
            if (outerCropRatio < 1) {
                OuterCropRatio = outerCropRatio;
                UseROI = true;
            }
            return this;
        }
    }

    /// <summary>Port of NINA's DetectedStar (IStarDetection.cs:47-56). Coordinates are full-frame pixels.</summary>
    public sealed class DetectedStar {
        public double HFR { get; set; }
        public double FWHM { get; set; }
        public double Eccentricity { get; set; }
        public AccordPoint Position { get; set; }
        public double AverageBrightness { get; set; }
        public double MaxBrightness { get; set; }
        public double Background { get; set; }
        public PixelRect BoundingBox { get; set; }

        /// <summary>Star radius from the blob/circle fit, full-frame pixels (fork diagnostic, upstream Star.Radius).</summary>
        public double DetectionRadius { get; set; } = double.NaN;

        /// <summary>Aperture radius HFR and FWHM were measured in (fork diagnostic).</summary>
        public double MeasurementRadius { get; set; } = double.NaN;
    }

    /// <summary>Port of NINA's StarDetectionResult (IStarDetection.cs:58-67) plus timing/diagnostics.</summary>
    public sealed class StarDetectionResult {
        public double AverageHFR { get; set; } = double.NaN;
        public double AverageFWHM { get; set; } = double.NaN;
        public double AverageEccentricity { get; set; } = double.NaN;
        public int DetectedStars { get; set; }
        public double HFRStdDev { get; set; } = double.NaN;
        public List<DetectedStar> StarList { get; set; } = new List<DetectedStar>();
        public List<AccordPoint> BrightestStarPositions { get; set; }
        public StarDetectionParams Params { get; set; }

        /// <summary>Resize factor that was applied to the detection image (diagnostic, NINA logs it).</summary>
        public double ResizeFactor { get; set; } = 1.0;

        /// <summary>Number of blobs found before any filtering (diagnostic).</summary>
        public int BlobCount { get; set; }

        /// <summary>
        /// Fork diagnostic: the band of star radii (full-frame pixels) the final radius filter kept, mean - 1.5 sigma to
        /// mean + 1.5 sigma (+ 2 sigma at Highest sensitivity), as upstream computes it (StarDetection.cs:774-782).
        /// NaN when no star reached the filter, or when upstream's one-pass sigma is NaN (see README, proposed patch 1).
        /// </summary>
        public (double Low, double High) RadiusFilterBand { get; set; } = (double.NaN, double.NaN);

        /// <summary>
        /// Fork diagnostic: stars that passed every per-blob check and were then removed by the radius filter, in blob
        /// order. Does not change any result.
        /// </summary>
        public List<DetectedStar> RadiusFilterRejected { get; set; } = new List<DetectedStar>();

        /// <summary>Wall time per detection stage (diagnostic; upstream logs the same stages via MyStopWatch).</summary>
        public List<(string Stage, System.TimeSpan Elapsed)> StageTimes { get; } = new List<(string, System.TimeSpan)>();
    }
}
