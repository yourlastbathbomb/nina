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

namespace NINA.Mac.ImageAnalysis.Test {

    public enum PsfKind {
        Gaussian,
        Moffat
    }

    /// <summary>Circular PSF with closed-form flux profile and half-flux radius.</summary>
    public sealed class Psf {

        private Psf(PsfKind kind, double fwhm, double beta) {
            Kind = kind;
            Fwhm = fwhm;
            Beta = beta;
        }

        public static Psf Gaussian(double fwhm) => new Psf(PsfKind.Gaussian, fwhm, double.NaN);

        /// <summary>Moffat with exponent beta (&gt; 1). beta = 4.765 is the Trujillo et al. (2001) turbulence value.</summary>
        public static Psf Moffat(double fwhm, double beta) => new Psf(PsfKind.Moffat, fwhm, beta);

        public PsfKind Kind { get; }
        public double Fwhm { get; }
        public double Beta { get; }

        public double Sigma => Fwhm / (2 * Math.Sqrt(2 * Math.Log(2)));

        public double Alpha => Fwhm / (2 * Math.Sqrt(Math.Pow(2, 1 / Beta) - 1));

        /// <summary>Surface brightness per unit area for unit total flux.</summary>
        public double Profile(double r2) {
            if (Kind == PsfKind.Gaussian) {
                var s2 = Sigma * Sigma;
                return Math.Exp(-r2 / (2 * s2)) / (2 * Math.PI * s2);
            }
            var a2 = Alpha * Alpha;
            return (Beta - 1) / (Math.PI * a2) * Math.Pow(1 + (r2 / a2), -Beta);
        }

        /// <summary>Fraction of the total flux inside radius r.</summary>
        public double EnclosedFraction(double r) {
            if (Kind == PsfKind.Gaussian) {
                return 1 - Math.Exp(-r * r / (2 * Sigma * Sigma));
            }
            return 1 - Math.Pow(1 + (r * r / (Alpha * Alpha)), 1 - Beta);
        }

        /// <summary>Analytic half-flux radius of the untruncated profile.</summary>
        public double HalfFluxRadius => HalfFluxRadiusWithin(double.PositiveInfinity);

        /// <summary>Radius enclosing half of the flux that lies inside the aperture radius R (NINA measures within an aperture).</summary>
        public double HalfFluxRadiusWithin(double apertureRadius) {
            double target = 0.5 * (double.IsPositiveInfinity(apertureRadius) ? 1 : EnclosedFraction(apertureRadius));
            if (Kind == PsfKind.Gaussian) {
                return Sigma * Math.Sqrt(-2 * Math.Log(1 - target));
            }
            return Alpha * Math.Sqrt(Math.Pow(1 - target, 1 / (1 - Beta)) - 1);
        }

        /// <summary>Radius beyond which the profile is below 1e-5 of its peak.</summary>
        public double RenderRadius {
            get {
                if (Kind == PsfKind.Gaussian) {
                    return Math.Ceiling(Sigma * Math.Sqrt(2 * Math.Log(1e5))) + 1;
                }
                return Math.Ceiling(Alpha * Math.Sqrt(Math.Pow(1e5, 1 / Beta) - 1)) + 1;
            }
        }

        public double PeakFraction => Profile(0);
    }

    public sealed class SyntheticStar {
        public double X { get; init; }
        public double Y { get; init; }

        /// <summary>Total flux in ADU (summed over all pixels, before clipping).</summary>
        public double Flux { get; init; }

        public Psf Psf { get; init; }

        public double Peak => Flux * Psf.PeakFraction;
    }

    /// <summary>
    /// Deterministic synthetic frame generator: background with linear gradient, stars with Gaussian or Moffat PSFs
    /// (pixel-integrated by supersampling), Gaussian read noise plus Poisson-like shot noise, hot pixels, 16 bit clipping,
    /// optional Bayer mosaic with per-channel response. Pixel (x, y) has its centre at integer (x, y), the convention
    /// NINA's star measurement uses.
    /// </summary>
    public sealed class SkyFrame {

        public SkyFrame(int width, int height) {
            Width = width;
            Height = height;
        }

        public int Width { get; }
        public int Height { get; }
        public double Background { get; set; } = 1000;

        /// <summary>Background change across the full width / height (ADU).</summary>
        public double GradientX { get; set; }

        public double GradientY { get; set; }

        public double ReadNoise { get; set; } = 10;

        /// <summary>Electrons per ADU for shot noise; 0 disables shot noise.</summary>
        public double Gain { get; set; } = 1;

        public int Supersample { get; set; } = 5;
        public int Seed { get; set; } = 1;
        public List<SyntheticStar> Stars { get; } = new List<SyntheticStar>();
        public List<(int X, int Y, ushort Value)> HotPixels { get; } = new List<(int, int, ushort)>();

        public BayerPattern BayerPattern { get; set; } = BayerPattern.None;

        /// <summary>Per-channel response to star light for Bayer frames (R, G, B).</summary>
        public (double R, double G, double B) StarColor { get; set; } = (1, 1, 1);

        /// <summary>Per-channel sky level multipliers for Bayer frames (R, G, B).</summary>
        public (double R, double G, double B) SkyColor { get; set; } = (1, 1, 1);

        /// <summary>Noise-free star signal per pixel (before channel response), for tests that need the truth image.</summary>
        public double[] RenderStarSignal() {
            var signal = new double[Width * Height];
            int ss = Math.Max(1, Supersample);
            double step = 1.0 / ss;
            double offset = (step / 2) - 0.5;
            foreach (var star in Stars) {
                int rr = (int)star.Psf.RenderRadius;
                int x0 = Math.Max(0, (int)Math.Floor(star.X) - rr);
                int x1 = Math.Min(Width - 1, (int)Math.Ceiling(star.X) + rr);
                int y0 = Math.Max(0, (int)Math.Floor(star.Y) - rr);
                int y1 = Math.Min(Height - 1, (int)Math.Ceiling(star.Y) + rr);
                for (int y = y0; y <= y1; y++) {
                    for (int x = x0; x <= x1; x++) {
                        double acc = 0;
                        for (int sy = 0; sy < ss; sy++) {
                            double dy = y + offset + (sy * step) - star.Y;
                            for (int sx = 0; sx < ss; sx++) {
                                double dx = x + offset + (sx * step) - star.X;
                                acc += star.Psf.Profile((dx * dx) + (dy * dy));
                            }
                        }
                        signal[(y * Width) + x] += star.Flux * acc / (ss * ss);
                    }
                }
            }
            return signal;
        }

        public PixelBuffer Render() {
            var signal = RenderStarSignal();
            var rng = new Random(Seed);
            var data = new ushort[Width * Height];
            var pattern = BayerPattern == BayerPattern.None ? null : Debayer.GetPattern(BayerPattern);
            for (int y = 0; y < Height; y++) {
                for (int x = 0; x < Width; x++) {
                    int i = (y * Width) + x;
                    double sky = Background + (GradientX * ((x / (double)Width) - 0.5)) + (GradientY * ((y / (double)Height) - 0.5));
                    double star = signal[i];
                    if (pattern != null) {
                        // slot 0 = R, 1 = G, 2 = B (Debayer.GetPattern)
                        int slot = pattern[y & 1, x & 1];
                        var (cs, cr) = slot switch {
                            0 => (SkyColor.R, StarColor.R),
                            1 => (SkyColor.G, StarColor.G),
                            _ => (SkyColor.B, StarColor.B)
                        };
                        sky *= cs;
                        star *= cr;
                    }
                    double mean = sky + star;
                    double variance = (ReadNoise * ReadNoise) + (Gain > 0 ? Math.Max(0, mean) / Gain : 0);
                    double value = mean + (Math.Sqrt(variance) * NextGaussian(rng));
                    data[i] = (ushort)Math.Max(0, Math.Min(65535, Math.Round(value)));
                }
            }
            foreach (var hp in HotPixels) {
                data[(hp.Y * Width) + hp.X] = hp.Value;
            }
            return new PixelBuffer(data, Width, Height, 16, BayerPattern);
        }

        private static double NextGaussian(Random rng) {
            // Box-Muller
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        /// <summary>
        /// Scatters stars on a jittered grid (keeps them separated by at least <paramref name="minSeparation"/>),
        /// fluxes log-uniform between the limits, away from the frame edge by <paramref name="margin"/>.
        /// </summary>
        public void AddStarField(int count, Func<int, Psf> psf, double minFlux, double maxFlux, double minSeparation, int margin, int seed) {
            var rng = new Random(seed);
            int attempts = 0;
            while (Stars.Count < count && attempts < count * 200) {
                attempts++;
                double x = margin + (rng.NextDouble() * (Width - (2 * margin)));
                double y = margin + (rng.NextDouble() * (Height - (2 * margin)));
                if (Stars.Any(s => ((s.X - x) * (s.X - x)) + ((s.Y - y) * (s.Y - y)) < minSeparation * minSeparation)) {
                    continue;
                }
                double flux = Math.Exp(Math.Log(minFlux) + (rng.NextDouble() * (Math.Log(maxFlux) - Math.Log(minFlux))));
                Stars.Add(new SyntheticStar { X = x, Y = y, Flux = flux, Psf = psf(Stars.Count) });
            }
        }

        /// <summary>Peak signal-to-noise of a star against the local sky noise.</summary>
        public double PeakSnr(SyntheticStar star) {
            double sky = Background;
            double noise = Math.Sqrt((ReadNoise * ReadNoise) + (Gain > 0 ? sky / Gain : 0));
            return star.Peak / noise;
        }
    }

    public static class StarMatching {

        /// <summary>Greedy nearest-neighbour match of detections to truth within a radius.</summary>
        public static List<(SyntheticStar Truth, DetectedStar Detected, double Distance)> Match(IReadOnlyList<SyntheticStar> truth, IReadOnlyList<DetectedStar> detected, double maxDistance) {
            var pairs = new List<(int t, int d, double dist)>();
            for (int t = 0; t < truth.Count; t++) {
                for (int d = 0; d < detected.Count; d++) {
                    double dx = truth[t].X - detected[d].Position.X;
                    double dy = truth[t].Y - detected[d].Position.Y;
                    double dist = Math.Sqrt((dx * dx) + (dy * dy));
                    if (dist <= maxDistance) {
                        pairs.Add((t, d, dist));
                    }
                }
            }
            var usedT = new HashSet<int>();
            var usedD = new HashSet<int>();
            var result = new List<(SyntheticStar, DetectedStar, double)>();
            foreach (var p in pairs.OrderBy(p => p.dist)) {
                if (usedT.Contains(p.t) || usedD.Contains(p.d)) {
                    continue;
                }
                usedT.Add(p.t);
                usedD.Add(p.d);
                result.Add((truth[p.t], detected[p.d], p.dist));
            }
            return result;
        }
    }
}
