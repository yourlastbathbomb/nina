#region "copyright"

/*
    Part of the LGPL Accord.NET / AForge.NET port used by the N.I.N.A. macOS image-analysis library.

    Copyright © Andrew Kirillov (AForge.NET framework), 2005-2009
    Copyright © César Souza (Accord.NET Framework), 2009-2017
    Managed port for the N.I.N.A. macOS fork, 2026.

    This library is free software; you can redistribute it and/or modify it under the terms of the GNU Lesser
    General Public License as published by the Free Software Foundation; either version 2.1 of the License, or
    (at your option) any later version.

    This library is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the
    implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public
    License for more details: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
*/

#endregion "copyright"

using System;
using System.Collections.Generic;

namespace NINA.Mac.ImageAnalysis.AccordPort {

    /// <summary>Port of Accord.Imaging.HoughLine (AForge.Imaging/HoughLineTransformation.cs).</summary>
    public sealed class HoughLine : IComparable<HoughLine> {
        public readonly double Theta;
        public readonly short Radius;
        public readonly short Intensity;
        public readonly double RelativeIntensity;

        public HoughLine(double theta, short radius, short intensity, double relativeIntensity) {
            Theta = theta;
            Radius = radius;
            Intensity = intensity;
            RelativeIntensity = relativeIntensity;
        }

        /// <summary>Descending intensity, then descending radius (as Accord).</summary>
        public int CompareTo(HoughLine other) {
            if (Intensity == other.Intensity) {
                return -Radius.CompareTo(other.Radius);
            }
            return -Intensity.CompareTo(other.Intensity);
        }

        public override string ToString() => FormattableString.Invariant($"Theta: {Theta}, radius: {Radius}, intensity: {Intensity}");
    }

    /// <summary>
    /// Port of Accord.Imaging.HoughLineTransformation (AForge.Imaging/HoughLineTransformation.cs): 8 bpp input,
    /// every non-zero pixel votes, coordinates relative to the image centre, local peak search with cyclic theta.
    /// Defaults: 1 step per degree, local peak radius 4, minimum line intensity 10.
    /// </summary>
    public sealed class HoughLineTransformation {
        private int stepsPerDegree;
        private int houghHeight;
        private double thetaStep;
        private double[] sinMap;
        private double[] cosMap;
        private short[,] houghMap;
        private short maxMapIntensity = 0;
        private int localPeakRadius = 4;
        private short minLineIntensity = 10;
        private readonly List<HoughLine> lines = new List<HoughLine>();

        public HoughLineTransformation() {
            StepsPerDegree = 1;
        }

        public int StepsPerDegree {
            get => stepsPerDegree;
            set {
                stepsPerDegree = Math.Max(1, Math.Min(10, value));
                houghHeight = 180 * stepsPerDegree;
                thetaStep = Math.PI / houghHeight;
                sinMap = new double[houghHeight];
                cosMap = new double[houghHeight];
                for (int i = 0; i < houghHeight; i++) {
                    sinMap[i] = Math.Sin(i * thetaStep);
                    cosMap[i] = Math.Cos(i * thetaStep);
                }
            }
        }

        public short MinLineIntensity {
            get => minLineIntensity;
            set => minLineIntensity = value;
        }

        public int LocalPeakRadius {
            get => localPeakRadius;
            set => localPeakRadius = Math.Max(1, Math.Min(10, value));
        }

        public short MaxIntensity => maxMapIntensity;

        public int LinesCount => lines.Count;

        public void ProcessImage(Gray8Image image) {
            int width = image.Width;
            int height = image.Height;
            int halfWidth = width / 2;
            int halfHeight = height / 2;
            byte[] src = image.Pixels;

            // rect = whole image
            int startX = -halfWidth;
            int startY = -halfHeight;
            int stopX = width - halfWidth;
            int stopY = height - halfHeight;

            int halfHoughWidth = (int)Math.Sqrt((halfWidth * halfWidth) + (halfHeight * halfHeight));
            int houghWidth = halfHoughWidth * 2;

            houghMap = new short[houghHeight, houghWidth];

            int p = 0;
            for (int y = startY; y < stopY; y++) {
                for (int x = startX; x < stopX; x++, p++) {
                    if (src[p] != 0) {
                        for (int theta = 0; theta < houghHeight; theta++) {
                            int radius = (int)Math.Round((cosMap[theta] * x) - (sinMap[theta] * y)) + halfHoughWidth;
                            if ((radius < 0) || (radius >= houghWidth)) {
                                continue;
                            }
                            houghMap[theta, radius]++;
                        }
                    }
                }
            }

            maxMapIntensity = 0;
            for (int i = 0; i < houghHeight; i++) {
                for (int j = 0; j < houghWidth; j++) {
                    if (houghMap[i, j] > maxMapIntensity) {
                        maxMapIntensity = houghMap[i, j];
                    }
                }
            }

            CollectLines();
        }

        public HoughLine[] GetMostIntensiveLines(int count) {
            int n = Math.Min(count, lines.Count);
            var dst = new HoughLine[n];
            lines.CopyTo(0, dst, 0, n);
            return dst;
        }

        private void CollectLines() {
            int maxTheta = houghMap.GetLength(0);
            int maxRadius = houghMap.GetLength(1);
            int halfHoughWidth = maxRadius >> 1;

            lines.Clear();

            for (int theta = 0; theta < maxTheta; theta++) {
                for (int radius = 0; radius < maxRadius; radius++) {
                    short intensity = houghMap[theta, radius];
                    if (intensity < minLineIntensity) {
                        continue;
                    }

                    bool foundGreater = false;

                    // check neighbourhood
                    for (int tt = theta - localPeakRadius, ttMax = theta + localPeakRadius; tt < ttMax; tt++) {
                        if (foundGreater) {
                            break;
                        }

                        int cycledTheta = tt;
                        int cycledRadius = radius;

                        if (cycledTheta < 0) {
                            cycledTheta = maxTheta + cycledTheta;
                            cycledRadius = maxRadius - cycledRadius;
                        }
                        if (cycledTheta >= maxTheta) {
                            cycledTheta -= maxTheta;
                            cycledRadius = maxRadius - cycledRadius;
                        }

                        for (int tr = cycledRadius - localPeakRadius, trMax = cycledRadius + localPeakRadius; tr < trMax; tr++) {
                            if (tr < 0) {
                                continue;
                            }
                            if (tr >= maxRadius) {
                                break;
                            }
                            if (houghMap[cycledTheta, tr] > intensity) {
                                foundGreater = true;
                                break;
                            }
                        }
                    }

                    if (!foundGreater) {
                        lines.Add(new HoughLine(theta / (double)stepsPerDegree, (short)(radius - halfHoughWidth), intensity, (double)intensity / maxMapIntensity));
                    }
                }
            }

            // List<T>.Sort (introsort, unstable) with the same comparer, as Accord
            lines.Sort();
        }
    }
}
