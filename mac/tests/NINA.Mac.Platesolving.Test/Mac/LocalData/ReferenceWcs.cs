#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Globalization;

namespace NINA.Mac.Platesolving.Test.LocalData {

    /// <summary>
    /// The reference for a real solve: the WCS already in the file's header (written by the Seestar or by Siril), evaluated with
    /// an independent TAN (+SIP) implementation from the FITS WCS papers (Greisen and Calabretta 2002, Shupe et al. 2005), which
    /// shares no code with NINA, ASTAP or astrometry.net. Pixel coordinates are FITS 1-based.
    /// </summary>
    internal sealed class ReferenceWcs {

        private readonly double crval1, crval2, crpix1, crpix2, cd11, cd12, cd21, cd22;
        private readonly double[,]? a, b;

        private ReferenceWcs(double crval1, double crval2, double crpix1, double crpix2, double cd11, double cd12, double cd21, double cd22, double[,]? a, double[,]? b) {
            this.crval1 = crval1;
            this.crval2 = crval2;
            this.crpix1 = crpix1;
            this.crpix2 = crpix2;
            this.cd11 = cd11;
            this.cd12 = cd12;
            this.cd21 = cd21;
            this.cd22 = cd22;
            this.a = a;
            this.b = b;
        }

        public bool HasSip => a != null;

        /// <summary>A WCS from its values (CD matrix in degrees per pixel; SIP coefficient arrays indexed [p, q], or null).</summary>
        public static ReferenceWcs FromValues(double crval1, double crval2, double crpix1, double crpix2, double cd11, double cd12, double cd21, double cd22,
            double[,]? a = null, double[,]? b = null) {
            return new ReferenceWcs(crval1, crval2, crpix1, crpix2, cd11, cd12, cd21, cd22, a, b);
        }

        /// <summary>Degrees per pixel, from the CD matrix (sqrt |det|).</summary>
        public double Scale => Math.Sqrt(Math.Abs(cd11 * cd22 - cd12 * cd21));

        public (double CD11, double CD12, double CD21, double CD22) CD => (cd11, cd12, cd21, cd22);

        /// <summary>The header's WCS, or null when it has none (no CRVAL/CRPIX, or not a TAN projection).</summary>
        public static ReferenceWcs? FromHeader(FitsCube fits) {
            var ctype1 = fits.String("CTYPE1");
            if (!fits.Has("CRVAL1") || !fits.Has("CRPIX1")) {
                return null;
            }
            if (ctype1 != null && !ctype1.Contains("TAN", StringComparison.Ordinal)) {
                return null;
            }
            double cd11, cd12, cd21, cd22;
            if (fits.Has("CD1_1")) {
                cd11 = fits.Double("CD1_1");
                cd12 = fits.OptionalDouble("CD1_2") ?? 0;
                cd21 = fits.OptionalDouble("CD2_1") ?? 0;
                cd22 = fits.Double("CD2_2");
            } else if (fits.Has("CDELT1")) {
                var cdelt1 = fits.Double("CDELT1");
                var cdelt2 = fits.Double("CDELT2");
                if (fits.Has("PC1_1")) {
                    cd11 = cdelt1 * fits.Double("PC1_1");
                    cd12 = cdelt1 * (fits.OptionalDouble("PC1_2") ?? 0);
                    cd21 = cdelt2 * (fits.OptionalDouble("PC2_1") ?? 0);
                    cd22 = cdelt2 * fits.Double("PC2_2");
                } else {
                    var rho = (fits.OptionalDouble("CROTA2") ?? 0) * Math.PI / 180;
                    cd11 = cdelt1 * Math.Cos(rho);
                    cd12 = -cdelt2 * Math.Sin(rho);
                    cd21 = cdelt1 * Math.Sin(rho);
                    cd22 = cdelt2 * Math.Cos(rho);
                }
            } else {
                return null;
            }
            double[,]? a = null, b = null;
            if (ctype1 != null && ctype1.EndsWith("-SIP", StringComparison.Ordinal) && fits.Has("A_ORDER") && fits.Has("B_ORDER")) {
                a = Sip(fits, "A", (int)fits.Double("A_ORDER"));
                b = Sip(fits, "B", (int)fits.Double("B_ORDER"));
            }
            return new ReferenceWcs(fits.Double("CRVAL1"), fits.Double("CRVAL2"), fits.Double("CRPIX1"), fits.Double("CRPIX2"), cd11, cd12, cd21, cd22, a, b);
        }

        /// <summary>RA and Dec in degrees (J2000/ICRS, as the header's) of a FITS 1-based pixel position.</summary>
        public (double Ra, double Dec) PixelToSky(double x, double y) {
            var u = x - crpix1;
            var v = y - crpix2;
            if (a != null && b != null) {
                double f = Polynomial(a, u, v), g = Polynomial(b, u, v);
                u += f;
                v += g;
            }
            // Intermediate world coordinates (degrees), then the gnomonic deprojection about CRVAL
            var xi = (cd11 * u + cd12 * v) * Math.PI / 180;
            var eta = (cd21 * u + cd22 * v) * Math.PI / 180;
            var dec0 = crval2 * Math.PI / 180;
            var denominator = Math.Cos(dec0) - eta * Math.Sin(dec0);
            var ra = crval1 + Math.Atan2(xi, denominator) * 180 / Math.PI;
            var dec = Math.Atan2(Math.Sin(dec0) + eta * Math.Cos(dec0), Math.Sqrt(xi * xi + denominator * denominator)) * 180 / Math.PI;
            return ((ra % 360 + 360) % 360, dec);
        }

        /// <summary>
        /// The FITS 1-based pixel position of a sky position (degrees): the gnomonic projection about CRVAL, the inverse CD matrix,
        /// then the SIP distortion removed by fixed-point iteration (it is a small correction, so this converges in a few steps).
        /// </summary>
        public (double X, double Y) SkyToPixel(double ra, double dec) {
            double ra0 = crval1 * Math.PI / 180, dec0 = crval2 * Math.PI / 180, r = ra * Math.PI / 180, d = dec * Math.PI / 180;
            var cosC = Math.Sin(dec0) * Math.Sin(d) + Math.Cos(dec0) * Math.Cos(d) * Math.Cos(r - ra0);
            var xi = Math.Cos(d) * Math.Sin(r - ra0) / cosC * 180 / Math.PI;
            var eta = (Math.Cos(dec0) * Math.Sin(d) - Math.Sin(dec0) * Math.Cos(d) * Math.Cos(r - ra0)) / cosC * 180 / Math.PI;
            var determinant = cd11 * cd22 - cd12 * cd21;
            var uDistorted = (cd22 * xi - cd12 * eta) / determinant;
            var vDistorted = (-cd21 * xi + cd11 * eta) / determinant;
            double u = uDistorted, v = vDistorted;
            if (a != null && b != null) {
                for (var i = 0; i < 50; i++) {
                    var nextU = uDistorted - Polynomial(a, u, v);
                    var nextV = vDistorted - Polynomial(b, u, v);
                    var change = Math.Abs(nextU - u) + Math.Abs(nextV - v);
                    u = nextU;
                    v = nextV;
                    if (change < 1e-9) {
                        break;
                    }
                }
            }
            return (u + crpix1, v + crpix2);
        }

        /// <summary>
        /// The CD matrix (degrees per pixel) of the TAN projection whose tangent point is the sky position of pixel (x, y): what a
        /// solver that puts its reference point at that pixel reports. It differs from the header's CD matrix by the distortion and
        /// by the rotation of north between CRVAL and (x, y) (about dRA sin(Dec) degrees), from central differences over 1 px.
        /// </summary>
        public (double CD11, double CD12, double CD21, double CD22) LocalCd(double x, double y) {
            var (ra0, dec0) = PixelToSky(x, y);
            (double Xi, double Eta) Tangent(double px, double py) {
                var (ra, dec) = PixelToSky(px, py);
                double r = (ra - ra0) * Math.PI / 180, d = dec * Math.PI / 180, d0 = dec0 * Math.PI / 180;
                var cosC = Math.Sin(d0) * Math.Sin(d) + Math.Cos(d0) * Math.Cos(d) * Math.Cos(r);
                return (Math.Cos(d) * Math.Sin(r) / cosC * 180 / Math.PI, (Math.Cos(d0) * Math.Sin(d) - Math.Sin(d0) * Math.Cos(d) * Math.Cos(r)) / cosC * 180 / Math.PI);
            }
            var xPlus = Tangent(x + 0.5, y);
            var xMinus = Tangent(x - 0.5, y);
            var yPlus = Tangent(x, y + 0.5);
            var yMinus = Tangent(x, y - 0.5);
            return (xPlus.Xi - xMinus.Xi, yPlus.Xi - yMinus.Xi, xPlus.Eta - xMinus.Eta, yPlus.Eta - yMinus.Eta);
        }

        /// <summary>Great-circle distance in arcseconds.</summary>
        public static double SeparationArcsec(double ra1, double dec1, double ra2, double dec2) {
            double r1 = ra1 * Math.PI / 180, d1 = dec1 * Math.PI / 180, r2 = ra2 * Math.PI / 180, d2 = dec2 * Math.PI / 180;
            var h = Math.Pow(Math.Sin((d2 - d1) / 2), 2) + Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin((r2 - r1) / 2), 2);
            return 2 * Math.Asin(Math.Min(1, Math.Sqrt(h))) * 180 / Math.PI * 3600;
        }

        private static double[,] Sip(FitsCube fits, string name, int order) {
            var coefficients = new double[order + 1, order + 1];
            for (var p = 0; p <= order; p++) {
                for (var q = 0; q <= order - p; q++) {
                    coefficients[p, q] = fits.OptionalDouble(string.Create(CultureInfo.InvariantCulture, $"{name}_{p}_{q}")) ?? 0;
                }
            }
            return coefficients;
        }

        private static double Polynomial(double[,] c, double u, double v) {
            var sum = 0d;
            for (var p = 0; p < c.GetLength(0); p++) {
                for (var q = 0; q < c.GetLength(1); q++) {
                    if (c[p, q] != 0) {
                        sum += c[p, q] * Math.Pow(u, p) * Math.Pow(v, q);
                    }
                }
            }
            return sum;
        }
    }
}
