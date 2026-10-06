#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.ImageAnalysis;
using NINA.Mac.ImageAnalysis.AccordPort;
using System;
using System.Linq;
using System.Threading;

namespace NINA.Mac.App.Engine {

    /// <summary>What the Focus and Run screens show for one frame. NaN HFR means no star was measured.</summary>
    public sealed record FrameMeasurement(double Hfr, int Stars, double Mean, double MeanFraction, double? BahtinovOffsetPixels);

    /// <summary>
    /// HFR, star count, mean level and the Bahtinov offset of a raw frame, through NINA.Mac.ImageAnalysis (the managed ports of
    /// NINA's star detection and Bahtinov analyser; upstream's GDI+ versions cannot run on macOS). Debayers RGGB frames itself.
    /// </summary>
    public static class FrameAnalysisService {

        /// <summary>Side of the square around the brightest star that the Bahtinov analyser looks at (NINA's default crop).</summary>
        public const int BahtinovCrop = 200;

        /// <param name="data">Raw 16-bit pixels, row major (NINA's ImageArray.FlatArray).</param>
        /// <param name="bayered">True for a colour (RGGB) frame that was not mono-binned.</param>
        /// <param name="detectStars">Lights and focus frames; calibration frames only need the statistics.</param>
        /// <param name="bahtinov">Also run the Bahtinov analyser on a crop around the brightest star.</param>
        public static FrameMeasurement Measure(ushort[] data, int width, int height, int bitDepth, bool bayered, bool detectStars, bool bahtinov,
                double pixelSizeMicrons = double.NaN, double focalLengthMm = double.NaN, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(data);
            if (width <= 0 || height <= 0 || data.Length != (long)width * height) {
                throw new ArgumentException($"Frame of {data.Length} pixels does not hold {width} x {height}");
            }
            var buffer = new PixelBuffer(data, width, height, bitDepth <= 0 ? 16 : bitDepth, bayered ? BayerPattern.RGGB : BayerPattern.None);
            var options = new FrameAnalysisOptions {
                DetectStars = detectStars,
                AutoStretch = detectStars,
                DebayerImage = detectStars && bayered,
                PixelSizeMicrons = pixelSizeMicrons,
                FocalLengthMm = focalLengthMm,
            };
            var analysis = FrameAnalyzer.Analyze(buffer, options, ct);
            var full = (1 << Math.Clamp(buffer.BitDepth, 1, 16)) - 1;
            var mean = analysis.Statistics.Mean;
            var hfr = double.NaN;
            var stars = 0;
            if (analysis.Stars != null && analysis.Stars.DetectedStars > 0) {
                hfr = analysis.Stars.AverageHFR;
                stars = analysis.Stars.DetectedStars;
            }
            double? offset = null;
            if (bahtinov && detectStars) {
                offset = BahtinovOffset(analysis, width, height);
            }
            return new FrameMeasurement(hfr, stars, mean, Math.Clamp(mean / full, 0, 1), offset);
        }

        private static double? BahtinovOffset(FrameAnalysis analysis, int width, int height) {
            var side = Math.Min(BahtinovCrop, Math.Min(width, height));
            var centre = (X: width / 2.0, Y: height / 2.0);
            var brightest = analysis.Stars?.StarList?.OrderByDescending(s => s.MaxBrightness).FirstOrDefault();
            if (brightest != null) {
                centre = (brightest.Position.X, brightest.Position.Y);
            }
            var x = (int)Math.Clamp(Math.Round(centre.X - (side / 2.0)), 0, width - side);
            var y = (int)Math.Clamp(Math.Round(centre.Y - (side / 2.0)), 0, height - side);
            try {
                var result = FrameAnalyzer.AnalyzeBahtinov(analysis, new PixelRect(x, y, side, side));
                return result.Success && !double.IsNaN(result.SignedOffset) ? result.SignedOffset : null;
            } catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is IndexOutOfRangeException) {
                // No mask in front of the scope: the analyser finds no three lines
                return null;
            }
        }
    }
}
