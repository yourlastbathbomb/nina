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
using NINA.Mac.ImageAnalysis.AccordPort;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using OBinaryDilation3x3 = Accord.Imaging.Filters.BinaryDilation3x3;
using OBlobCounter = Accord.Imaging.BlobCounter;
using OCanny = Accord.Imaging.Filters.CannyEdgeDetector;
using OGaussianBlur = Accord.Imaging.Filters.GaussianBlur;
using OGrayscale = Accord.Imaging.Filters.Grayscale;
using OHough = Accord.Imaging.HoughLineTransformation;
using OIntPoint = Accord.IntPoint;
using OLine = Accord.Math.Geometry.Line;
using OMedian = Accord.Imaging.Filters.Median;
using ONoBlurCanny = NINA.Image.ImageAnalysis.NoBlurCannyEdgeDetector;
using OPoint = Accord.Point;
using OResizeBicubic = Accord.Imaging.Filters.ResizeBicubic;
using OShapeChecker = Accord.Math.Geometry.SimpleShapeChecker;
using OSisThreshold = Accord.Imaging.Filters.SISThreshold;

namespace NINA.Mac.ImageAnalysis.Test {

    /// <summary>
    /// Bit-for-bit comparison of the managed ports against the original code N.I.N.A. runs on Windows: the in-repo
    /// Accord.Imaging sources (compiled by link into the test-only oracle, UnmanagedImage paths), NINA's
    /// BayerFilter16bpp / NoBlurCannyEdgeDetector, and the Accord.Math 3.8.2 NuGet assembly.
    /// </summary>
    [TestFixture]
    public class AccordParityTests {

        private static IEnumerable<TestCaseData> Images() {
            yield return new TestCaseData("noise 97x61", RandomImage(97, 61, 1, 0, 255)).SetName("{m}(noise 97x61)");
            yield return new TestCaseData("low noise 128x128", RandomImage(128, 128, 2, 20, 40)).SetName("{m}(low noise 128x128)");
            yield return new TestCaseData("stars 400x300", StarImage(400, 300, 3)).SetName("{m}(stars 400x300)");
            yield return new TestCaseData("stars 333x251", StarImage(333, 251, 4)).SetName("{m}(stars 333x251)");
            yield return new TestCaseData("flat 50x40", Flat(50, 40, 77)).SetName("{m}(flat 50x40)");
            yield return new TestCaseData("bahtinov 200x200", BahtinovImage8()).SetName("{m}(bahtinov 200x200)");
        }

        private static Gray8Image RandomImage(int w, int h, int seed, int min, int max) {
            var rng = new Random(seed);
            var img = new Gray8Image(w, h);
            for (int i = 0; i < img.Pixels.Length; i++) {
                img.Pixels[i] = (byte)rng.Next(min, max + 1);
            }
            return img;
        }

        private static Gray8Image Flat(int w, int h, byte value) {
            var img = new Gray8Image(w, h);
            Array.Fill(img.Pixels, value);
            return img;
        }

        /// <summary>A stretched-looking 8 bit star field.</summary>
        internal static Gray8Image StarImage(int w, int h, int seed) {
            var sky = new SkyFrame(w, h) { Background = 6000, ReadNoise = 300, Gain = 0, Seed = seed, Supersample = 1 };
            sky.AddStarField(40, i => Psf.Gaussian(2 + (i % 4)), 5e4, 3e6, 12, 8, seed);
            var buffer = sky.Render();
            return PixelConversions.Convert16To8(buffer.Data, w, h);
        }

        internal static Gray8Image BahtinovImage8() {
            var pattern = BahtinovPattern.Render(200, 200, centerX: 101.3, centerY: 98.7, centerAngleDeg: 62, outerHalfAngleDeg: 17, offsetPx: 4, seed: 5);
            return pattern;
        }

        [TestCaseSource(nameof(Images))]
        public void GaussianBlur_KernelAndOutput_MatchAccord(string name, Gray8Image image) {
            foreach (var (sigma, size) in new[] { (1.4, 5), (1.4, 11), (2.0, 7), (0.7, 3) }) {
                var mine = new GaussianBlur(sigma, size);
                var oracle = new OGaussianBlur(sigma, size);
                mine.Kernel.Cast<int>().Should().Equal(oracle.Kernel.Cast<int>(), $"kernel sigma {sigma} size {size}");
                mine.Kernel.GetLength(0).Should().Be(oracle.Kernel.GetLength(0));
                mine.Divisor.Should().Be(oracle.Divisor);

                using var u = OracleImages.ToUnmanaged8(image);
                using var blurred = oracle.Apply(u);
                mine.Apply(image).Pixels.Should().Equal(OracleImages.FromUnmanaged8(blurred).Pixels, $"{name} sigma {sigma} size {size}");
            }
        }

        [TestCaseSource(nameof(Images))]
        public void Canny_MatchesAccord(string name, Gray8Image image) {
            foreach (var (low, high) in new[] { ((byte)10, (byte)80), ((byte)20, (byte)100) }) {
                var mine = image.Clone();
                new CannyEdgeDetector(low, high).ApplyInPlace(mine);
                using var u = OracleImages.ToUnmanaged8(image);
                new OCanny(low, high).ApplyInPlace(u);
                mine.Pixels.Should().Equal(OracleImages.FromUnmanaged8(u).Pixels, $"{name} {low}/{high}");
                if (name.StartsWith("stars") || name.StartsWith("bahtinov")) {
                    mine.Pixels.Count(v => v != 0).Should().BeGreaterThan(100, "the comparison must not be vacuous");
                }
            }
        }

        [TestCaseSource(nameof(Images))]
        public void Canny_GaussianSize10_MatchesAccord(string name, Gray8Image image) {
            var mine = image.Clone();
            new CannyEdgeDetector { GaussianSize = 10 }.ApplyInPlace(mine);
            using var u = OracleImages.ToUnmanaged8(image);
            new OCanny { GaussianSize = 10 }.ApplyInPlace(u);
            mine.Pixels.Should().Equal(OracleImages.FromUnmanaged8(u).Pixels, name);
        }

        [TestCaseSource(nameof(Images))]
        public void NoBlurCanny_MatchesNinaFilter(string name, Gray8Image image) {
            var mine = image.Clone();
            new CannyEdgeDetector(10, 80) { ApplyBlur = false }.ApplyInPlace(mine);
            using var u = OracleImages.ToUnmanaged8(image);
            new ONoBlurCanny(10, 80).ApplyInPlace(u);
            mine.Pixels.Should().Equal(OracleImages.FromUnmanaged8(u).Pixels, name);
        }

        [TestCaseSource(nameof(Images))]
        public void SisThresholdAndDilation_MatchAccord(string name, Gray8Image image) {
            var mine = image.Clone();
            new CannyEdgeDetector(10, 80).ApplyInPlace(mine);
            int myThreshold = SisThreshold.ApplyInPlace(mine);
            BinaryDilation3x3.ApplyInPlace(mine);

            using var u = OracleImages.ToUnmanaged8(image);
            new OCanny(10, 80).ApplyInPlace(u);
            var sis = new OSisThreshold();
            sis.ApplyInPlace(u);
            new OBinaryDilation3x3().ApplyInPlace(u);

            myThreshold.Should().Be(sis.ThresholdValue, name);
            mine.Pixels.Should().Equal(OracleImages.FromUnmanaged8(u).Pixels, name);
        }

        [TestCaseSource(nameof(Images))]
        public void BlobCounter_RectanglesAndEdgePoints_MatchAccord(string name, Gray8Image image) {
            // the binary structure image the star detector feeds to the blob counter
            var binary = image.Clone();
            new CannyEdgeDetector(10, 80).ApplyInPlace(binary);
            SisThreshold.ApplyInPlace(binary);
            BinaryDilation3x3.ApplyInPlace(binary);

            var mine = new BlobCounter();
            mine.ProcessImage(binary);
            var myBlobs = mine.GetObjectsInformation();

            using var u = OracleImages.ToUnmanaged8(binary);
            var oracle = new OBlobCounter();
            oracle.ProcessImage(u);
            var oBlobs = oracle.GetObjectsInformation();

            myBlobs.Length.Should().Be(oBlobs.Length, name);
            if (name.StartsWith("stars")) {
                myBlobs.Length.Should().BeGreaterThanOrEqualTo(10, "the comparison must not be vacuous");
            }
            mine.ObjectLabels.Should().Equal(oracle.ObjectLabels, name);
            for (int i = 0; i < myBlobs.Length; i++) {
                var r = oBlobs[i].Rectangle;
                myBlobs[i].ID.Should().Be(oBlobs[i].ID);
                myBlobs[i].Rectangle.Should().Be(new PixelRect(r.X, r.Y, r.Width, r.Height), $"{name} blob {i}");
                myBlobs[i].Area.Should().Be(oBlobs[i].Area);
                var myEdges = mine.GetBlobsEdgePoints(myBlobs[i]).Select(p => (p.X, p.Y)).ToList();
                var oEdges = oracle.GetBlobsEdgePoints(oBlobs[i]).Select(p => (p.X, p.Y)).ToList();
                myEdges.Should().Equal(oEdges, $"{name} blob {i} edge points");
            }
        }

        [TestCaseSource(nameof(Images))]
        public void ResizeBicubic_MatchesAccord(string name, Gray8Image image) {
            foreach (var factor in new[] { 0.25, 1 / 3d, 0.5, 2 / 3d, 1552d / 1920d, 1.7 }) {
                int nw = Math.Max(1, (int)Math.Floor(image.Width * factor));
                int nh = Math.Max(1, (int)Math.Floor(image.Height * factor));
                var mine = ResizeBicubic.Apply(image, nw, nh);
                using var u = OracleImages.ToUnmanaged8(image);
                using var resized = new OResizeBicubic(nw, nh).Apply(u);
                mine.Pixels.Should().Equal(OracleImages.FromUnmanaged8(resized).Pixels, $"{name} factor {factor}");
            }
        }

        [TestCaseSource(nameof(Images))]
        public void Median_MatchesAccord(string name, Gray8Image image) {
            var mine = MedianFilter.Apply(image);
            using var u = OracleImages.ToUnmanaged8(image);
            using var filtered = new OMedian().Apply(u);
            mine.Pixels.Should().Equal(OracleImages.FromUnmanaged8(filtered).Pixels, name);
        }

        [TestCaseSource(nameof(Images))]
        public void HoughLines_MatchAccord(string name, Gray8Image image) {
            var edges = image.Clone();
            new CannyEdgeDetector { GaussianSize = 10 }.ApplyInPlace(edges);

            var mine = new HoughLineTransformation();
            mine.ProcessImage(edges);
            using var u = OracleImages.ToUnmanaged8(edges);
            var oracle = new OHough();
            oracle.ProcessImage(u);

            mine.MaxIntensity.Should().Be(oracle.MaxIntensity, name);
            mine.LinesCount.Should().Be(oracle.LinesCount, name);
            if (name.StartsWith("bahtinov")) {
                mine.LinesCount.Should().BeGreaterThan(6, "the comparison must not be vacuous");
            }
            var myLines = mine.GetMostIntensiveLines(int.MaxValue);
            var oLines = oracle.GetMostIntensiveLines(int.MaxValue);
            for (int i = 0; i < myLines.Length; i++) {
                myLines[i].Theta.Should().Be(oLines[i].Theta, $"{name} line {i}");
                myLines[i].Radius.Should().Be(oLines[i].Radius, $"{name} line {i}");
                myLines[i].Intensity.Should().Be(oLines[i].Intensity, $"{name} line {i}");
                myLines[i].RelativeIntensity.Should().Be(oLines[i].RelativeIntensity, $"{name} line {i}");
            }
        }

        [Test]
        public void Grayscale48_MatchesAccord() {
            var rng = new Random(11);
            int w = 37, h = 23;
            var rgb = new ushort[w * h * 3];
            for (int i = 0; i < rgb.Length; i++) {
                rgb[i] = (ushort)rng.Next(0, 65536);
            }
            rgb[0] = rgb[1] = rgb[2] = 65535;
            var mine = PixelConversions.Grayscale48To16(rgb, w, h, 0.2125, 0.7154, 0.0721);
            using var u = OracleImages.ToUnmanaged48(rgb, w, h);
            using var gray = new OGrayscale(0.2125, 0.7154, 0.0721).Apply(u);
            mine.Should().Equal(OracleImages.FromUnmanaged16(gray, 1));
        }

        [TestCase(BayerPattern.RGGB, 64, 48)]
        [TestCase(BayerPattern.BGGR, 63, 47)]
        [TestCase(BayerPattern.GRBG, 32, 33)]
        [TestCase(BayerPattern.GBRG, 17, 9)]
        [TestCase(BayerPattern.GRGB, 20, 20)]
        [TestCase(BayerPattern.RGBG, 21, 22)]
        [TestCase(BayerPattern.GBGR, 2, 2)]
        [TestCase(BayerPattern.BGRG, 3, 5)]
        public void Debayer_MatchesNinaBayerFilter16bpp(BayerPattern pattern, int w, int h) {
            var rng = new Random((int)pattern + w);
            var raw = new ushort[w * h];
            for (int i = 0; i < raw.Length; i++) {
                raw[i] = (ushort)rng.Next(0, 65536);
            }
            var mine = Debayer.Apply(raw, w, h, pattern, saveColorChannels: true, saveLumChannel: true);

            var filter = new NINA.Image.ImageAnalysis.BayerFilter16bpp { SaveColorChannels = true, SaveLumChannel = true };
            int[,] ninaPattern = Debayer.GetPattern(pattern); // same constants NINA writes with Accord's RGB.* values
            filter.BayerPattern = ninaPattern;
            using var u = OracleImages.ToUnmanaged16(raw, w, h);
            using var rgb = filter.Apply(u);

            mine.Rgb.Should().Equal(OracleImages.FromUnmanaged16(rgb, 3), "interleaved RGB48 memory");
            mine.Lum.Should().Equal(filter.LRGBArrays.Lum);
            mine.Red.Should().Equal(filter.LRGBArrays.Red);
            mine.Green.Should().Equal(filter.LRGBArrays.Green);
            mine.Blue.Should().Equal(filter.LRGBArrays.Blue);
        }

        [Test]
        public void ShapeChecker_MatchesAccordMath() {
            var rng = new Random(21);
            var mine = new SimpleShapeChecker();
            var oracle = new OShapeChecker();
            int circles = 0;
            for (int trial = 0; trial < 3000; trial++) {
                var points = new List<IntPoint>();
                int n = rng.Next(0, 60);
                double cx = rng.Next(0, 500), cy = rng.Next(0, 500), r = 1 + (rng.NextDouble() * 20), squash = trial % 3 == 0 ? 1 + rng.NextDouble() : 1;
                for (int k = 0; k < n; k++) {
                    double a = rng.NextDouble() * 2 * Math.PI;
                    points.Add(new IntPoint((int)Math.Round(cx + (r * squash * Math.Cos(a))), (int)Math.Round(cy + (r * Math.Sin(a)))));
                }
                bool m = mine.IsCircle(points, out var mc, out var mr);
                bool o = oracle.IsCircle(points.Select(p => new OIntPoint(p.X, p.Y)).ToList(), out OPoint oc, out float or);
                m.Should().Be(o, $"trial {trial}");
                mc.X.Should().Be(oc.X);
                mc.Y.Should().Be(oc.Y);
                mr.Should().Be(or);
                if (m) { circles++; }
            }
            circles.Should().BeGreaterThan(100, "the sample should exercise both outcomes");
        }

        [Test]
        public void PointDistance_MatchesAccord() {
            var rng = new Random(3);
            for (int i = 0; i < 1000; i++) {
                float ax = (float)(rng.NextDouble() * 4000), ay = (float)(rng.NextDouble() * 4000), bx = (float)(rng.NextDouble() * 4000), by = (float)(rng.NextDouble() * 4000);
                new AccordPoint(ax, ay).DistanceTo(new AccordPoint(bx, by)).Should().Be(new OPoint(ax, ay).DistanceTo(new OPoint(bx, by)));
            }
        }

        [Test]
        public void Line_MatchesAccordMath() {
            var rng = new Random(9);
            for (int i = 0; i < 2000; i++) {
                var p1 = new IntPoint(rng.Next(-300, 300), rng.Next(-300, 300));
                var p2 = new IntPoint(rng.Next(-300, 300), rng.Next(-300, 300));
                if (i % 7 == 0) { p2 = new IntPoint(p1.X, p2.Y); } // vertical
                if (p1.Equals(p2)) { continue; }
                var p3 = new IntPoint(rng.Next(-300, 300), rng.Next(-300, 300));
                var p4 = new IntPoint(rng.Next(-300, 300), rng.Next(-300, 300));
                if (p3.Equals(p4)) { continue; }

                var m1 = Line.FromPoints(p1, p2);
                var o1 = OLine.FromPoints(new OPoint(p1.X, p1.Y), new OPoint(p2.X, p2.Y));
                m1.Slope.Should().Be(o1.Slope);
                m1.Intercept.Should().Be(o1.Intercept);

                float slope = (float)((rng.NextDouble() - 0.5) * 10), intercept = (float)((rng.NextDouble() - 0.5) * 400);
                var m2 = Line.FromSlopeIntercept(slope, intercept);
                var o2 = OLine.FromSlopeIntercept(slope, intercept);
                var m3 = Line.FromPoints(p3, p4);
                var o3 = OLine.FromPoints(new OPoint(p3.X, p3.Y), new OPoint(p4.X, p4.Y));

                foreach (var (a, b, oa, ob) in new[] { (m1, m2, o1, o2), (m1, m3, o1, o3), (m3, m2, o3, o2) }) {
                    var mi = a.GetIntersectionWith(b);
                    var oi = oa.GetIntersectionWith(ob);
                    mi.HasValue.Should().Be(oi.HasValue);
                    if (mi.HasValue) {
                        mi.Value.X.Should().Be(oi.Value.X);
                        mi.Value.Y.Should().Be(oi.Value.Y);
                    }
                }
            }
        }
    }
}
