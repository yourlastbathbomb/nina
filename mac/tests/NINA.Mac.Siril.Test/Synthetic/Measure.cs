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
using System.Linq;

namespace NINA.Mac.Siril.Test.Synthetic {

    /// <summary>A single image plane addressed in picture coordinates (y = 0 at the top).</summary>
    public sealed class Plane {

        public Plane(int width, int height, Func<int, int, double> at) {
            Width = width;
            Height = height;
            At = at;
        }

        public int Width { get; }

        public int Height { get; }

        public Func<int, int, double> At { get; }

        public static Plane FromImage(FitsImage image, int plane) => new(image.Width, image.Height, (x, y) => image.AtTopDown(plane, x, y));

        /// <summary>Super-pixel channel of a raw RGGB frame (half size): 0 = R, 1 = mean of the two G, 2 = B.</summary>
        public static Plane SuperPixel(FitsImage raw, int channel) {
            return new Plane(raw.Width / 2, raw.Height / 2, (x, y) => channel switch {
                0 => raw.AtTopDown(0, 2 * x, 2 * y),
                2 => raw.AtTopDown(0, (2 * x) + 1, (2 * y) + 1),
                _ => 0.5 * (raw.AtTopDown(0, (2 * x) + 1, 2 * y) + raw.AtTopDown(0, 2 * x, (2 * y) + 1)),
            });
        }
    }

    public readonly record struct StarMeasurement(double X, double Y, double Flux, double Background, double Hfr, double Peak);

    public static class Measure {

        public static double Median(List<double> values) {
            if (values.Count == 0) {
                return double.NaN;
            }
            values.Sort();
            var n = values.Count;
            return (n & 1) == 1 ? values[n / 2] : 0.5 * (values[(n / 2) - 1] + values[n / 2]);
        }

        /// <summary>Median of a box (clipped to the plane).</summary>
        public static double BoxMedian(Plane p, int x0, int y0, int w, int h) {
            var values = new List<double>(w * h);
            for (var y = Math.Max(0, y0); y < Math.Min(p.Height, y0 + h); y++) {
                for (var x = Math.Max(0, x0); x < Math.Min(p.Width, x0 + w); x++) {
                    values.Add(p.At(x, y));
                }
            }
            return Median(values);
        }

        /// <summary>Median of a box centred on (cx, cy).</summary>
        public static double BoxMedianAt(Plane p, double cx, double cy, int size) => BoxMedian(p, (int)Math.Round(cx - (size / 2.0)), (int)Math.Round(cy - (size / 2.0)), size, size);

        /// <summary>
        /// Brightest star within <paramref name="search"/> pixels of the guess: centroid, aperture flux (radius
        /// <paramref name="aperture"/>) above the median of the annulus [aperture + 4, aperture + 10], HFR and peak.
        /// </summary>
        public static StarMeasurement Star(Plane p, double guessX, double guessY, int search, double aperture = 6) {
            var bestX = -1;
            var bestY = -1;
            var best = double.MinValue;
            for (var y = (int)guessY - search; y <= (int)guessY + search; y++) {
                for (var x = (int)guessX - search; x <= (int)guessX + search; x++) {
                    if (x < 1 || y < 1 || x >= p.Width - 1 || y >= p.Height - 1) {
                        continue;
                    }
                    double sum = 0;
                    for (var dy = -1; dy <= 1; dy++) {
                        for (var dx = -1; dx <= 1; dx++) {
                            sum += p.At(x + dx, y + dy);
                        }
                    }
                    if (sum > best) {
                        best = sum;
                        bestX = x;
                        bestY = y;
                    }
                }
            }
            if (bestX < 0) {
                throw new InvalidOperationException("Search box outside the image");
            }
            var annulus = new List<double>();
            var rIn = aperture + 4;
            var rOut = aperture + 10;
            for (var y = bestY - (int)rOut; y <= bestY + (int)rOut; y++) {
                for (var x = bestX - (int)rOut; x <= bestX + (int)rOut; x++) {
                    if (x < 0 || y < 0 || x >= p.Width || y >= p.Height) {
                        continue;
                    }
                    var d = Math.Sqrt(((x - bestX) * (x - bestX)) + ((y - bestY) * (y - bestY)));
                    if (d >= rIn && d <= rOut) {
                        annulus.Add(p.At(x, y));
                    }
                }
            }
            var bg = Median(annulus);
            // centroid, then flux and HFR around it
            double sx = 0, sy = 0, sw = 0;
            for (var y = bestY - 3; y <= bestY + 3; y++) {
                for (var x = bestX - 3; x <= bestX + 3; x++) {
                    var v = Math.Max(0, p.At(x, y) - bg);
                    sx += v * x;
                    sy += v * y;
                    sw += v;
                }
            }
            var cx = sw > 0 ? sx / sw : bestX;
            var cy = sw > 0 ? sy / sw : bestY;
            double flux = 0, moment = 0, peak = double.MinValue;
            for (var y = (int)(cy - aperture) - 1; y <= (int)(cy + aperture) + 1; y++) {
                for (var x = (int)(cx - aperture) - 1; x <= (int)(cx + aperture) + 1; x++) {
                    if (x < 0 || y < 0 || x >= p.Width || y >= p.Height) {
                        continue;
                    }
                    var d = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                    if (d > aperture) {
                        continue;
                    }
                    var v = p.At(x, y) - bg;
                    flux += v;
                    if (v > 0) {
                        moment += v * d;
                    }
                    peak = Math.Max(peak, v);
                }
            }
            var positive = 0.0;
            for (var y = (int)(cy - aperture) - 1; y <= (int)(cy + aperture) + 1; y++) {
                for (var x = (int)(cx - aperture) - 1; x <= (int)(cx + aperture) + 1; x++) {
                    if (x < 0 || y < 0 || x >= p.Width || y >= p.Height) {
                        continue;
                    }
                    var d = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                    if (d <= aperture) {
                        positive += Math.Max(0, p.At(x, y) - bg);
                    }
                }
            }
            return new StarMeasurement(cx, cy, flux, bg, positive > 0 ? moment / positive : double.NaN, peak);
        }

        public static (double Min, double Max, double Mean, int NonFinite) Stats(FitsImage image) {
            double min = double.MaxValue, max = double.MinValue, sum = 0;
            var nonFinite = 0;
            long n = 0;
            foreach (var plane in image.Planes) {
                foreach (var v in plane) {
                    if (!float.IsFinite(v)) {
                        nonFinite++;
                        continue;
                    }
                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                    sum += v;
                    n++;
                }
            }
            return (min, max, n > 0 ? sum / n : double.NaN, nonFinite);
        }

        public static double MeanOf(IEnumerable<double> values) => values.Average();
    }
}
