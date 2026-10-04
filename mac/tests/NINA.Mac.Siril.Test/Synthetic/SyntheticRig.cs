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

namespace NINA.Mac.Siril.Test.Synthetic {

    public readonly record struct Star(double X, double Y, double Amplitude);

    /// <summary>Pointing of one light relative to the reference star field: rotation about the frame centre, then a shift.</summary>
    public readonly record struct FrameOffset(double Dx, double Dy, double RotationDegrees);

    /// <summary>
    /// Synthetic ASI585MC at bin 2 (scaled-down frame) behind a vignetting telescope. Every effect has a distinct
    /// spatial signature so the tests can tell which calibration step worked:
    /// bias = uniform 16 x OFFSET; dark current = left-to-right ramp (plus hot pixels) scaling with exposure;
    /// vignetting = radial fall-off to <see cref="VignettingDepth"/> at the corners; RGGB colour response 1.0/0.6/0.3.
    /// Raw = bias + dark + hot + V * CFA * (sky + stars) + noise.
    /// </summary>
    public sealed class SyntheticRig {
        public const int Seed = 585;

        public SyntheticRig(int width = 640, int height = 480) {
            Width = width;
            Height = height;
            var rng = new Random(Seed);
            for (var i = 0; i < 60; i++) {
                HotPixels.Add((rng.Next(8, width - 8), rng.Next(8, height - 8), 3000 + (rng.NextDouble() * 9000)));
            }
            CenterStar = new Star(width / 2.0 + 0.3, height / 2.0 - 0.4, 10000);
            CornerStar = new Star(62.6, 52.2, 10000);
            var stars = new List<Star> { CenterStar, CornerStar };
            var guard = 0;
            while (stars.Count < 160 && guard++ < 100000) {
                var s = new Star(25 + (rng.NextDouble() * (width - 50)), 25 + (rng.NextDouble() * (height - 50)), 700 + (rng.NextDouble() * 5000));
                var ok = true;
                foreach (var other in stars) {
                    var d2 = ((other.X - s.X) * (other.X - s.X)) + ((other.Y - s.Y) * (other.Y - s.Y));
                    var minimum = other.Amplitude >= 10000 ? 45 : 7; // photometry stars stay isolated
                    if (d2 < minimum * minimum) {
                        ok = false;
                        break;
                    }
                }
                if (ok) {
                    stars.Add(s);
                }
            }
            Stars = stars;
        }

        public int Width { get; }

        public int Height { get; }

        public int Gain { get; set; } = 252;

        public int Offset { get; set; } = 50;

        /// <summary>Bias level in ADU: 16 x OFFSET, so -bias="=16*$OFFSET" is the exact synthetic offset.</summary>
        public double BiasLevel => 16.0 * Offset;

        public double ReadNoise { get; set; } = 4;

        public double Sky { get; set; } = 2000;

        public double VignettingDepth { get; set; } = 0.45;

        /// <summary>Dark current ramp for a 20 s exposure: <see cref="DarkRampLeft"/> at x = 0 to <see cref="DarkRampRight"/> at the right edge.</summary>
        public double DarkRampLeft { get; set; } = 100;

        public double DarkRampRight { get; set; } = 400;

        public double StarSigma { get; set; } = 1.4;

        public double FlatLevel { get; set; } = 35000;

        public double[] ChannelResponse { get; } = { 1.0, 0.6, 0.3 };

        public List<(int X, int Y, double Amplitude)> HotPixels { get; } = new();

        public IReadOnlyList<Star> Stars { get; }

        /// <summary>Bright isolated star at the centre (vignetting 1).</summary>
        public Star CenterStar { get; }

        /// <summary>Bright isolated star near the top-left corner, same flux as <see cref="CenterStar"/>.</summary>
        public Star CornerStar { get; }

        public double CenterX => (Width - 1) / 2.0;

        public double CenterY => (Height - 1) / 2.0;

        public double Vignetting(double x, double y) {
            var rmax2 = (CenterX * CenterX) + (CenterY * CenterY);
            var r2 = ((x - CenterX) * (x - CenterX)) + ((y - CenterY) * (y - CenterY));
            return 1.0 - (VignettingDepth * r2 / rmax2);
        }

        /// <summary>RGGB with row 0 = top row: 0 = R, 1 = G, 2 = B.</summary>
        public static int Channel(int x, int y) => (y & 1) == 0 ? ((x & 1) == 0 ? 0 : 1) : ((x & 1) == 0 ? 1 : 2);

        public double DarkCurrent(int x, double exposure) => (DarkRampLeft + ((DarkRampRight - DarkRampLeft) * x / (Width - 1))) * exposure / 20.0;

        public (double X, double Y) Transform(Star s, FrameOffset o) {
            var a = o.RotationDegrees * Math.PI / 180.0;
            var dx = s.X - CenterX;
            var dy = s.Y - CenterY;
            return (CenterX + (dx * Math.Cos(a)) - (dy * Math.Sin(a)) + o.Dx, CenterY + (dx * Math.Sin(a)) + (dy * Math.Cos(a)) + o.Dy);
        }

        public ushort[] Light(double exposure, FrameOffset offset, int frameSeed) {
            var scene = new double[Width * Height];
            Array.Fill(scene, Sky);
            var r = (int)Math.Ceiling(StarSigma * 6);
            var inv = 1.0 / (2 * StarSigma * StarSigma);
            foreach (var star in Stars) {
                var (sx, sy) = Transform(star, offset);
                for (var y = Math.Max(0, (int)sy - r); y <= Math.Min(Height - 1, (int)sy + r); y++) {
                    for (var x = Math.Max(0, (int)sx - r); x <= Math.Min(Width - 1, (int)sx + r); x++) {
                        var d2 = ((x - sx) * (x - sx)) + ((y - sy) * (y - sy));
                        scene[(y * Width) + x] += star.Amplitude * Math.Exp(-d2 * inv);
                    }
                }
            }
            var rng = new Random(frameSeed);
            var data = new ushort[Width * Height];
            for (var y = 0; y < Height; y++) {
                for (var x = 0; x < Width; x++) {
                    var i = (y * Width) + x;
                    var photons = Vignetting(x, y) * ChannelResponse[Channel(x, y)] * scene[i];
                    var dark = DarkCurrent(x, exposure);
                    data[i] = Clip(BiasLevel + dark + photons + Noise(rng, photons + dark));
                }
            }
            AddHotPixels(data, exposure);
            return data;
        }

        public ushort[] Flat(int frameSeed, double exposure = 1.0) {
            var rng = new Random(frameSeed);
            var data = new ushort[Width * Height];
            for (var y = 0; y < Height; y++) {
                for (var x = 0; x < Width; x++) {
                    var photons = Vignetting(x, y) * ChannelResponse[Channel(x, y)] * FlatLevel;
                    var dark = DarkCurrent(x, exposure);
                    data[(y * Width) + x] = Clip(BiasLevel + dark + photons + Noise(rng, photons + dark));
                }
            }
            AddHotPixels(data, exposure);
            return data;
        }

        /// <summary>Dark (or dark flat / bias with a tiny exposure): bias + ramp + hot pixels + noise.</summary>
        public ushort[] Dark(double exposure, int frameSeed) {
            var rng = new Random(frameSeed);
            var data = new ushort[Width * Height];
            for (var y = 0; y < Height; y++) {
                for (var x = 0; x < Width; x++) {
                    var dark = DarkCurrent(x, exposure);
                    data[(y * Width) + x] = Clip(BiasLevel + dark + Noise(rng, dark));
                }
            }
            AddHotPixels(data, exposure);
            return data;
        }

        private void AddHotPixels(ushort[] data, double exposure) {
            foreach (var (x, y, amplitude) in HotPixels) {
                var i = (y * Width) + x;
                data[i] = Clip(data[i] + (amplitude * exposure / 20.0));
            }
        }

        private double Noise(Random rng, double signal) {
            var sigma = Math.Sqrt((ReadNoise * ReadNoise) + Math.Max(signal, 0));
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static ushort Clip(double v) => (ushort)Math.Clamp(Math.Round(v), 0, 65535);

        /// <summary>Mean vignetting over the frame (what a mean-normalised flat divides by).</summary>
        public double MeanVignetting() {
            double sum = 0;
            for (var y = 0; y < Height; y++) {
                for (var x = 0; x < Width; x++) {
                    sum += Vignetting(x, y);
                }
            }
            return sum / (Width * Height);
        }
    }
}
