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

    public static partial class BahtinovAnalyzer {

        /// <summary>
        /// Fork addition (not in upstream N.I.N.A.): same edge detection and Hough transform as
        /// <see cref="Analyze(Gray8Image, BahtinovResult)"/>, with three fixes for failure modes of the upstream line
        /// handling found with synthetic patterns:
        /// <list type="number">
        /// <item>Plateau peaks: Accord keeps two neighbouring Hough cells of equal height as two "lines", which can push
        /// one edge of the weakest spike out of the top six. Peaks within 1 degree and 1 px of a stronger accepted peak
        /// are merged.</item>
        /// <item>Pairing: upstream sorts by 1/slope, which wraps at horizontal, so a pattern with a spike near
        /// horizontal is paired wrongly. Here the six lines are sorted by direction angle and the circular sequence is
        /// cut at its largest angular gap.</item>
        /// <item>Averaging: upstream averages slopes, which breaks if the two edges of a near-vertical spike come out
        /// with slopes of opposite sign (a code-reading concern; vertical spikes worked in the tests). Here each spike is
        /// the mean of its two edges in normal form (unit normal, distance).</item>
        /// </list>
        /// It also checks itself: if the two edges paired into any spike are more than
        /// <see cref="MaxEdgePairAngleDegrees"/> apart, an edge was lost and a spurious line took its place, so the
        /// result has <c>Success = false</c> instead of a wrong offset. Validated on synthetic patterns only: with the
        /// generator's 17 degree outer half-angle it is within 1 px for spike sigma up to about 3 px in 200 px crops and
        /// about 4 px in 300 px crops (bin 2 or mono-bin on this rig gives about 1.7-2.7 px). Wider spikes and smaller
        /// crops mostly return Success = false. Readings within about 1.5 px of focus can still have the wrong sign
        /// (README, Bahtinov).
        /// The geometry afterwards (outer-spike intersection, perpendicular to the central spike) is upstream's.
        /// </summary>
        public static BahtinovResult AnalyzeRobust(Gray8Image image, BahtinovResult previous = null) {
            var result = new BahtinovResult { Algorithm = "Robust" };
            var edges = image.Clone();
            var filter = new CannyEdgeDetector { GaussianSize = 10 };
            filter.ApplyInPlace(edges);

            var transform = new HoughLineTransformation();
            transform.ProcessImage(edges);
            var all = transform.GetMostIntensiveLines(int.MaxValue);

            // 1. merge plateau duplicates
            var accepted = new List<HoughLine>();
            foreach (var line in all) {
                if (accepted.Any(a => IsSamePeak(a, line))) {
                    continue;
                }
                accepted.Add(line);
                if (accepted.Count == 6) {
                    break;
                }
            }
            result.HoughLines = accepted.ToArray();
            if (accepted.Count < 6) {
                return result;
            }

            int halfWidth = image.Width / 2;
            int halfHeight = image.Height / 2;

            // Hough cell (theta, r): x cos(theta) - y sin(theta) = r with x = col - halfWidth, y = row - halfHeight
            var normalForms = accepted.Select(h => {
                double t = h.Theta * Math.PI / 180;
                return new NormalLine(Math.Cos(t), -Math.Sin(t), h.Radius);
            }).ToList();

            // 2. order by direction angle, cut the circle at the largest gap
            var ordered = normalForms.OrderBy(l => l.DirectionDegrees).ToList();
            int cut = 0;
            double largestGap = -1;
            for (int i = 0; i < ordered.Count; i++) {
                double a = ordered[i].DirectionDegrees;
                double b = ordered[(i + 1) % ordered.Count].DirectionDegrees + (i + 1 == ordered.Count ? 180 : 0);
                if (b - a > largestGap) {
                    largestGap = b - a;
                    cut = (i + 1) % ordered.Count;
                }
            }
            var sequence = Enumerable.Range(0, ordered.Count).Select(i => ordered[(cut + i) % ordered.Count]).ToList();

            // 3. each spike must be a pair of parallel edges: directions within one Hough step. Otherwise one edge of a
            // spike was lost and a spurious line took its place (wide spikes, small crops); report no measurement
            // rather than a confident wrong offset. The two edges are then also >= 2 px apart, because step 1 merged
            // peaks within 1 degree and 1 px.
            for (int i = 0; i < 6; i += 2) {
                if (AngleBetweenDegrees(sequence[i], sequence[i + 1]) > MaxEdgePairAngleDegrees + 1e-6) {
                    return result;
                }
            }

            // 4. average each pair of edges in normal form
            var spikes = new List<NormalLine>();
            for (int i = 0; i < 6; i += 2) {
                spikes.Add(NormalLine.Average(sequence[i], sequence[i + 1]));
            }

            // outer intersection
            var outer0 = spikes[0];
            var outer2 = spikes[2];
            var central = spikes[1];
            double det = (outer0.Nx * outer2.Ny) - (outer0.Ny * outer2.Nx);
            if (Math.Abs(det) < 1e-9) {
                return result;
            }
            double px = ((outer0.R * outer2.Ny) - (outer0.Ny * outer2.R)) / det;
            double py = ((outer0.Nx * outer2.R) - (outer0.R * outer2.Nx)) / det;

            // perpendicular foot on the central spike
            double s = (central.Nx * px) + (central.Ny * py) - central.R;
            double fx = px - (s * central.Nx);
            double fy = py - (s * central.Ny);

            var intersection = new AccordPoint((float)(px + halfWidth), (float)(py + halfHeight));
            var foot = new AccordPoint((float)(fx + halfWidth), (float)(fy + halfHeight));
            result.OuterIntersection = intersection;
            result.CenterPoint = foot;
            result.Distance = Math.Abs(s);
            result.ExaggeratedErrorPoint = new AccordPoint((float)(intersection.X + (4 * (foot.X - intersection.X))), (float)(intersection.Y + (4 * (foot.Y - intersection.Y))));
            result.Lines = spikes.Select(l => l.ToBahtinovLine(halfWidth, halfHeight, image.Width)).ToList();

            // sign convention shared with Analyze: normal (-sin a, cos a) of the central spike direction a in [0, 180)
            double angle = central.DirectionDegrees * Math.PI / 180;
            var normal = (X: -Math.Sin(angle), Y: Math.Cos(angle));
            if (previous != null && previous.Success && ((normal.X * previous.Normal.X) + (normal.Y * previous.Normal.Y)) < 0) {
                normal = (-normal.X, -normal.Y);
            }
            result.Normal = normal;
            result.SignedOffset = ((px - fx) * normal.X) + ((py - fy) * normal.Y);
            result.Success = true;
            return result;
        }

        /// <summary>Largest angle (degrees) between the two edges paired into one spike; one Hough step (1 degree).</summary>
        public const double MaxEdgePairAngleDegrees = 1.0;

        private static double AngleBetweenDegrees(NormalLine a, NormalLine b) {
            double d = Math.Abs(a.DirectionDegrees - b.DirectionDegrees) % 180;
            return Math.Min(d, 180 - d);
        }

        private static bool IsSamePeak(HoughLine a, HoughLine b) {
            double dTheta = Math.Abs(a.Theta - b.Theta);
            int ra = a.Radius, rb = b.Radius;
            if (dTheta > 90) {
                // across the 0/180 wrap the same line has the opposite radius
                dTheta = 180 - dTheta;
                rb = -rb;
            }
            return dTheta <= 1.0 && Math.Abs(ra - rb) <= 1;
        }

        /// <summary>Line n . p = R with unit normal n, in centred image coordinates (y down).</summary>
        private readonly struct NormalLine {

            public NormalLine(double nx, double ny, double r) {
                Nx = nx;
                Ny = ny;
                R = r;
            }

            public double Nx { get; }
            public double Ny { get; }
            public double R { get; }

            /// <summary>Direction angle in [0, 180), image coordinates (x right, y down).</summary>
            public double DirectionDegrees {
                get {
                    // direction is the normal rotated by -90 degrees: (-Ny, Nx)
                    double a = Math.Atan2(Nx, -Ny) * 180 / Math.PI;
                    a %= 180;
                    if (a < 0) {
                        a += 180;
                    }
                    return a;
                }
            }

            public static NormalLine Average(NormalLine a, NormalLine b) {
                // orient b like a before averaging (same line, opposite normal => negate)
                double sign = ((a.Nx * b.Nx) + (a.Ny * b.Ny)) < 0 ? -1 : 1;
                double nx = a.Nx + (sign * b.Nx);
                double ny = a.Ny + (sign * b.Ny);
                double len = Math.Sqrt((nx * nx) + (ny * ny));
                return new NormalLine(nx / len, ny / len, (a.R + (sign * b.R)) / 2 * (2 / len));
            }

            public BahtinovLine ToBahtinovLine(int halfWidth, int halfHeight, int width) {
                // two points on the line, far apart, converted to crop coordinates
                double px = Nx * R, py = Ny * R;
                double dx = -Ny, dy = Nx;
                return BahtinovLine.FromPoints(px - (1000 * dx) + halfWidth, py - (1000 * dy) + halfHeight, px + (1000 * dx) + halfWidth, py + (1000 * dy) + halfHeight, width);
            }
        }
    }
}
