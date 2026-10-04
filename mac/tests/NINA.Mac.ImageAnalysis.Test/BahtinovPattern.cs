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

namespace NINA.Mac.ImageAnalysis.Test {

    /// <summary>
    /// Synthetic Bahtinov-mask image as seen in a stretched display crop: a star core, two outer spikes at
    /// centerAngle ± outerHalfAngle crossing at the star, and a central spike at centerAngle displaced by
    /// <c>-offset * n</c>, with n = (-sin a, cos a) the central spike normal for a in [0, 180). The intersection of
    /// the outer spikes is therefore <c>+offset</c> from the central spike along n, which is the sign convention of
    /// <see cref="BahtinovResult.SignedOffset"/>.
    /// </summary>
    internal static class BahtinovPattern {

        public static Gray8Image Render(int width, int height, double centerX, double centerY, double centerAngleDeg, double outerHalfAngleDeg, double offsetPx, int seed,
            double spikeSigma = 2.2, double spikePeak = 170, double background = 28, double noise = 6, double coreSigma = 4, double centralSpikeGain = 1.0) {
            var rng = new Random(seed);
            var img = new Gray8Image(width, height);
            double a0 = centerAngleDeg * Math.PI / 180;
            double nx = -Math.Sin(a0), ny = Math.Cos(a0);
            double cxCentral = centerX - (offsetPx * nx);
            double cyCentral = centerY - (offsetPx * ny);

            var spikes = new[] {
                (angle: a0, px: cxCentral, py: cyCentral, gain: centralSpikeGain),
                (angle: a0 - (outerHalfAngleDeg * Math.PI / 180), px: centerX, py: centerY, gain: 1.0),
                (angle: a0 + (outerHalfAngleDeg * Math.PI / 180), px: centerX, py: centerY, gain: 1.0)
            };
            double length = Math.Max(width, height) * 0.9;

            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    double v = background;
                    foreach (var s in spikes) {
                        double dx = x - s.px, dy = y - s.py;
                        double along = (dx * Math.Cos(s.angle)) + (dy * Math.Sin(s.angle));
                        double across = (-dx * Math.Sin(s.angle)) + (dy * Math.Cos(s.angle));
                        double profile = Math.Exp(-(across * across) / (2 * spikeSigma * spikeSigma));
                        double fade = 1.0 / (1.0 + (Math.Abs(along) / length));
                        v += s.gain * spikePeak * profile * fade;
                    }
                    double rx = x - centerX, ry = y - centerY;
                    v += 400 * Math.Exp(-((rx * rx) + (ry * ry)) / (2 * coreSigma * coreSigma));
                    v += noise * Gaussian(rng);
                    img[x, y] = (byte)Math.Max(0, Math.Min(255, Math.Round(v)));
                }
            }
            return img;
        }

        private static double Gaussian(Random rng) {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
