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
using System.Collections.Generic;
using System.Linq;

namespace NINA.Mac.ImageAnalysis {

    /// <summary>One of the three averaged spike lines, in crop pixel coordinates (x right, y down).</summary>
    public sealed class BahtinovLine {

        internal static BahtinovLine FromPoints(double x1, double y1, double x2, double y2, int width) {
            var line = Line.FromPoints(new AccordPoint((float)x1, (float)y1), new AccordPoint((float)x2, (float)y2));
            return new BahtinovLine(line.Slope, line.Intercept, width);
        }

        internal BahtinovLine(float slope, float intercept, int width) {
            Slope = slope;
            Intercept = intercept;
            // drawing end points, as BahtinovAnalysis.cs:98-101 computes them
            X1 = 0;
            X2 = width;
            Y1 = float.IsInfinity(slope) ? intercept : slope + intercept;
            Y2 = float.IsInfinity(slope) ? intercept : (slope * width) + intercept;
        }

        /// <summary>dy/dx in image coordinates; infinite for a vertical line.</summary>
        public float Slope { get; }

        /// <summary>y at x = 0, or x for a vertical line (Accord Line convention).</summary>
        public float Intercept { get; }

        public float X1 { get; }
        public float Y1 { get; }
        public float X2 { get; }
        public float Y2 { get; }

        /// <summary>Line direction angle in degrees, in [0, 180), image coordinates.</summary>
        public double AngleDegrees {
            get {
                if (float.IsInfinity(Slope)) {
                    return 90d;
                }
                var angle = Math.Atan(Slope) * 180d / Math.PI;
                return angle < 0 ? angle + 180d : angle;
            }
        }
    }

    /// <summary>Result of <see cref="BahtinovAnalyzer.Analyze(Gray8Image, BahtinovResult)"/>.</summary>
    public sealed class BahtinovResult {

        /// <summary>"NINA" for the faithful port, "Robust" for <see cref="BahtinovAnalyzer.AnalyzeRobust"/>.</summary>
        public string Algorithm { get; internal set; } = "NINA";

        /// <summary>
        /// True when six Hough lines were found and both intersections exist (upstream draws the overlay).
        /// <see cref="BahtinovAnalyzer.AnalyzeRobust"/> also requires each spike's two edges to be parallel.
        /// </summary>
        public bool Success { get; internal set; }

        /// <summary>
        /// Upstream BahtinovImage.Distance: distance in pixels between the intersection of the two outer spikes and the
        /// central spike (always &gt;= 0; 0 when the analysis failed, as upstream).
        /// </summary>
        public double Distance { get; internal set; }

        /// <summary>
        /// Distance with a sign (fork addition; upstream only reports the magnitude): the offset of the outer-spike
        /// intersection from the central spike, measured along <see cref="Normal"/>. Crossing focus flips the sign. NaN
        /// when the analysis failed.
        /// </summary>
        public double SignedOffset { get; internal set; } = double.NaN;

        /// <summary>
        /// Unit normal of the central spike that defines the sign of <see cref="SignedOffset"/>: (-sin a, cos a) for the
        /// central spike angle a in [0, 180), unless a previous result was passed in, in which case it is oriented to
        /// agree with the previous normal so the sign stays continuous frame to frame.
        /// </summary>
        public (double X, double Y) Normal { get; internal set; }

        /// <summary>The six strongest Hough lines (theta degrees, radius, intensity), strongest first.</summary>
        public IReadOnlyList<HoughLine> HoughLines { get; internal set; } = Array.Empty<HoughLine>();

        /// <summary>The three averaged spikes ordered as upstream pairs them; index 1 is the central spike.</summary>
        public IReadOnlyList<BahtinovLine> Lines { get; internal set; } = Array.Empty<BahtinovLine>();

        /// <summary>Intersection of the two outer spikes (upstream x1, y1; drawn with the red pen).</summary>
        public AccordPoint? OuterIntersection { get; internal set; }

        /// <summary>Foot of the perpendicular from <see cref="OuterIntersection"/> on the central spike (upstream x2, y2; green circle).</summary>
        public AccordPoint? CenterPoint { get; internal set; }

        /// <summary>Upstream's red marker: the offset exaggerated 4x from the outer intersection (x3, y3).</summary>
        public AccordPoint? ExaggeratedErrorPoint { get; internal set; }
    }

    /// <summary>
    /// Port of NINA.Image/ImageAnalysis/BahtinovAnalysis.cs:38-157 (GrabBahtinov) and :159-201
    /// (TranslateHughLineToLine), without the GDI+ drawing: Canny (default thresholds 20/100, Gaussian size 10 -&gt; 11,
    /// sigma 1.4), Hough lines (1 step/degree), the six most intense lines ordered by 1/slope and averaged pairwise into
    /// three spikes, the outer spikes intersected and projected onto the central spike.
    /// The input is what upstream analyses: a crop of the displayed (auto-stretched) image.
    /// </summary>
    public static partial class BahtinovAnalyzer {

        /// <summary>Analyses an 8 bit image (upstream's Gray8 path).</summary>
        public static BahtinovResult Analyze(Gray8Image image, BahtinovResult previous = null) {
            var convertedSource = image.Clone();
            var result = new BahtinovResult();

            /* Apply filters and detection*/
            var filter = new CannyEdgeDetector();
            filter.GaussianSize = 10;
            filter.ApplyInPlace(convertedSource);

            var lineTransform = new HoughLineTransformation();
            lineTransform.ProcessImage(convertedSource);

            HoughLine[] lines = lineTransform.GetMostIntensiveLines(6);
            result.HoughLines = lines;

            var bahtinovLines = new List<Line>();
            foreach (HoughLine line in lines) {
                var k = TranslateHughLineToLine(line, image.Width, image.Height);
                bahtinovLines.Add(k);
            }

            if (bahtinovLines.Count == 6) {
                var orderedPoints = bahtinovLines.OrderBy(x => 1.0d / x.Slope).ToList();
                var threeLines = new List<Line>();

                for (var i = 0; i < orderedPoints.Count; i += 2) {
                    var l1 = orderedPoints[i];
                    var l2 = orderedPoints[i + 1];

                    var inter = (l1.Intercept + l2.Intercept) / 2.0f;
                    var slope = (l1.Slope + l2.Slope) / 2.0f;
                    var centerLine = Line.FromSlopeIntercept(slope, inter);
                    threeLines.Add(centerLine);
                }
                result.Lines = threeLines.Select(l => new BahtinovLine(l.Slope, l.Intercept, image.Width)).ToList();

                /* Intersect outer bahtinov lines */
                AccordPoint? intersection;
                try {
                    intersection = threeLines[0].GetIntersectionWith(threeLines[2]);
                } catch (InvalidOperationException) {
                    // identical outer lines: upstream would throw out of GrabBahtinov; report no result instead
                    intersection = null;
                }
                if (intersection.HasValue) {
                    /* get orthogonale to center line through intersection */
                    var centerBahtinovLine = threeLines[1];
                    var orthogonalSlope = -1.0f / centerBahtinovLine.Slope;
                    var orthogonalIntercept = intersection.Value.Y - orthogonalSlope * intersection.Value.X;

                    var orthogonalCenter = Line.FromSlopeIntercept(orthogonalSlope, orthogonalIntercept);
                    AccordPoint? intersection2;
                    try {
                        intersection2 = centerBahtinovLine.GetIntersectionWith(orthogonalCenter);
                    } catch (InvalidOperationException) {
                        intersection2 = null;
                    }
                    if (intersection2.HasValue && !double.IsInfinity(intersection2.Value.X)) {
                        float x1 = intersection.Value.X;
                        float y1 = intersection.Value.Y;
                        float x2 = intersection2.Value.X;
                        float y2 = intersection2.Value.Y;

                        result.Distance = intersection.Value.DistanceTo(intersection2.Value);

                        var t = result.Distance * 4 / result.Distance;
                        var x3 = (float)((1 - t) * x1 + t * x2);
                        var y3 = (float)((1 - t) * y1 + t * y2);

                        result.OuterIntersection = intersection;
                        result.CenterPoint = intersection2;
                        result.ExaggeratedErrorPoint = new AccordPoint(x3, y3);
                        result.Success = true;

                        var normal = CentralNormal(centerBahtinovLine);
                        if (previous != null && previous.Success && ((normal.X * previous.Normal.X) + (normal.Y * previous.Normal.Y)) < 0) {
                            normal = (-normal.X, -normal.Y);
                        }
                        result.Normal = normal;
                        result.SignedOffset = ((x1 - (double)x2) * normal.X) + ((y1 - (double)y2) * normal.Y);
                    }
                }
            }
            return result;
        }

        /// <summary>Analyses a 16 bit gray image (upstream's Gray16 path: Convert16BppTo8Bpp first).</summary>
        public static BahtinovResult Analyze(ushort[] gray16, int width, int height, BahtinovResult previous = null, bool robust = false) {
            var gray8 = PixelConversions.Convert16To8(gray16, width, height);
            return robust ? AnalyzeRobust(gray8, previous) : Analyze(gray8, previous);
        }

        /// <summary>
        /// Analyses an Rgb48 image (R, G, B memory order, as the debayered display image): upstream converts it with
        /// Grayscale(0.2125, 0.7154, 0.0721) on the GDI+ view of the memory (BGR), then to 8 bit.
        /// </summary>
        public static BahtinovResult AnalyzeRgb48(ushort[] rgb48, int width, int height, BahtinovResult previous = null, bool robust = false) {
            var gray = PixelConversions.Grayscale48To16(rgb48, width, height, 0.2125, 0.7154, 0.0721);
            return Analyze(gray, width, height, previous, robust);
        }

        private static (double X, double Y) CentralNormal(Line center) {
            double angle;
            if (float.IsInfinity(center.Slope)) {
                angle = Math.PI / 2;
            } else {
                angle = Math.Atan(center.Slope);
                if (angle < 0) {
                    angle += Math.PI;
                }
            }
            return (-Math.Sin(angle), Math.Cos(angle));
        }

        /// <summary>BahtinovAnalysis.TranslateHughLineToLine (BahtinovAnalysis.cs:159-201).</summary>
        internal static Line TranslateHughLineToLine(HoughLine line, int width, int height) {
            // get line's radius and theta values
            int r = line.Radius;
            double t = line.Theta;

            // check if line is in lower part of the image
            if (r < 0) {
                t += 180;
                r = -r;
            }

            // convert degrees to radians
            t = (t / 180) * Math.PI;

            // get image centers (all coordinate are measured relative to center)
            int w2 = width / 2;
            int h2 = height / 2;

            double x0 = 0, x1 = 0, y0 = 0, y1 = 0;

            if (line.Theta != 0) {
                // none-vertical line
                x0 = -w2; // most left point
                x1 = w2;  // most right point

                // calculate corresponding y values
                y0 = (-Math.Cos(t) * x0 + r) / Math.Sin(t);
                y1 = (-Math.Cos(t) * x1 + r) / Math.Sin(t);
            } else {
                // vertical line
                x0 = line.Radius;
                x1 = line.Radius;

                y0 = h2;
                y1 = -h2;
            }

            return Line.FromPoints(
                new IntPoint((int)x0 + w2, h2 - (int)y0),
                new IntPoint((int)x1 + w2, h2 - (int)y1));
        }
    }
}
