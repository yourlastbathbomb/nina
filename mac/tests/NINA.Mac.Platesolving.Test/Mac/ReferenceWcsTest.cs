#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NINA.Mac.Platesolving.Test.LocalData;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>
    /// The independent TAN (+SIP) evaluator that the LocalData tests use as their reference (no file needed): known values of the
    /// gnomonic projection (Calabretta and Greisen 2002, eq. 54-55 with the TAN native-to-celestial rotation for a reference point
    /// on the equator), and the inverse used by RealCenteringTest's camera.
    /// </summary>
    [TestFixture]
    public class ReferenceWcsTest {

        [Test]
        public void PixelToSky_AtTheReferencePixel_IsCrval() {
            var wcs = ReferenceWcs.FromValues(202.4696, 47.1952, 960.5, 540.5, -1e-4, 0, 0, 1e-4);
            var (ra, dec) = wcs.PixelToSky(960.5, 540.5);
            ra.Should().BeApproximately(202.4696, 1e-12);
            dec.Should().BeApproximately(47.1952, 1e-12);
        }

        [Test]
        public void PixelToSky_OnTheEquator_FollowsTheGnomonicProjection() {
            // Reference point (0, 0); one pixel is 0.01 deg along each intermediate axis, so (101, 101) is xi = eta = 1 deg.
            // For a tangent point on the equator: tan(ra) = xi, tan(dec) = eta / sqrt(1 + xi^2) (xi, eta in radians)
            var wcs = ReferenceWcs.FromValues(0, 0, 1, 1, 0.01, 0, 0, 0.01);
            var xi = Math.PI / 180;
            var eta = Math.PI / 180;

            var (ra, dec) = wcs.PixelToSky(101, 101);

            ra.Should().BeApproximately(Math.Atan(xi) * 180 / Math.PI, 1e-10);
            dec.Should().BeApproximately(Math.Atan(eta / Math.Sqrt(1 + xi * xi)) * 180 / Math.PI, 1e-10);
            ReferenceWcs.SeparationArcsec(0, 0, ra, dec).Should().BeApproximately(Math.Acos(1 / Math.Sqrt(1 + xi * xi + eta * eta)) * 180 / Math.PI * 3600, 1e-6,
                "a point at gnomonic radius r lies atan(r) from the tangent point");
        }

        [Test]
        public void LocalCd_IsTheHeadersCdAtTheReferencePixel_AndRotatesWithNorthAwayFromIt() {
            var scale = 3.66 / 3600;
            var wcs = ReferenceWcs.FromValues(200.88, 46.768, 1368.0, 786.0, 0, scale, -scale, 0);

            var atCrpix = wcs.LocalCd(1368.0, 786.0);
            atCrpix.CD11.Should().BeApproximately(0, 1e-12);
            atCrpix.CD12.Should().BeApproximately(scale, 1e-12);
            atCrpix.CD21.Should().BeApproximately(-scale, 1e-12);
            atCrpix.CD22.Should().BeApproximately(0, 1e-12);

            // 1200 px along x is about 1.2 deg of declination; 1200 px along y about 1.2 deg along the parallel, where north turns
            // by about dRA sin(Dec) (meridian convergence)
            var (ra, dec) = wcs.PixelToSky(1368.0, 786.0 + 1200);
            var local = wcs.LocalCd(1368.0, 786.0 + 1200);
            var rotation = Math.Atan2(local.CD11, local.CD12) * 180 / Math.PI;
            rotation.Should().BeApproximately(-(ra - 200.88) * Math.Sin(dec * Math.PI / 180), 0.02);
            Math.Sqrt(Math.Abs(local.CD11 * local.CD22 - local.CD12 * local.CD21)).Should().BeApproximately(scale, scale * 1e-3, "TAN keeps the scale near the tangent point to second order");
        }

        [Test]
        public void SkyToPixel_InvertsPixelToSky_WithSipDistortion() {
            // A rotated 3.66"/px TAN-SIP solution like a Seestar stack's, with second- and third-order distortion terms
            var scale = 3.66 / 3600;
            var theta = 50.0 * Math.PI / 180;
            var a = new double[4, 4];
            var b = new double[4, 4];
            a[2, 0] = 2e-6; a[1, 1] = -1.5e-6; a[0, 2] = 8e-7; a[3, 0] = 4e-10; a[1, 2] = -3e-10;
            b[2, 0] = -6e-7; b[1, 1] = 1.2e-6; b[0, 2] = 1.8e-6; b[0, 3] = 5e-10; b[2, 1] = 2e-10;
            var wcs = ReferenceWcs.FromValues(84.9192504985, -2.70249514851, 1120.8, 2343.2,
                -scale * Math.Cos(theta), -scale * Math.Sin(theta), scale * Math.Sin(theta), -scale * Math.Cos(theta), a, b);

            foreach (var (x, y) in new[] { (1.0, 1.0), (1080.5, 1920.5), (2160.0, 3840.0), (17.0, 3500.0), (2000.0, 40.0) }) {
                var (ra, dec) = wcs.PixelToSky(x, y);
                var (px, py) = wcs.SkyToPixel(ra, dec);
                px.Should().BeApproximately(x, 1e-6);
                py.Should().BeApproximately(y, 1e-6);
            }
            // The distortion is not negligible here: it moves the corner by 7.4 px (1.37, 7.25), about 27"
            var plain = ReferenceWcs.FromValues(84.9192504985, -2.70249514851, 1120.8, 2343.2,
                -scale * Math.Cos(theta), -scale * Math.Sin(theta), scale * Math.Sin(theta), -scale * Math.Cos(theta));
            var corner = wcs.PixelToSky(2160, 3840);
            var plainCorner = plain.PixelToSky(2160, 3840);
            ReferenceWcs.SeparationArcsec(corner.Ra, corner.Dec, plainCorner.Ra, plainCorner.Dec).Should().BeApproximately(7.38 * 3.66, 0.5);
        }
    }
}
