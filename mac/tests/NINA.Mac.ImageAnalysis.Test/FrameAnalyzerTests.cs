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

namespace NINA.Mac.ImageAnalysis.Test {

    /// <summary>
    /// When FrameAnalyzer stretches the display image. Upstream ImageControlVM.ProcessImage (ImageControlVM.cs:667-677)
    /// stretches when <c>detectStars || AutoStretch</c>; otherwise the displayed image, which is also the Bahtinov
    /// input (ImageControlVM.cs:278-282), is the rendered linear bitmap: the raw array for Gray16 and the debayered
    /// data for Rgb48.
    /// </summary>
    [TestFixture]
    public class FrameAnalyzerTests {

        private static PixelBuffer StarFrame(BayerPattern pattern) {
            var sky = new SkyFrame(640, 480) { Background = 1500, ReadNoise = 8, Gain = 1, Seed = 81, BayerPattern = pattern };
            sky.AddStarField(20, _ => Psf.Gaussian(5.0), 5e4, 5e5, 40, 30, 82);
            return sky.Render();
        }

        [Test]
        public void AutoStretchOff_WithoutStarDetection_DisplaysTheLinearMonoFrame() {
            var frame = StarFrame(BayerPattern.None);
            var analysis = FrameAnalyzer.Analyze(frame, new FrameAnalysisOptions { DetectStars = false, AutoStretch = false });

            analysis.IsStretched.Should().BeFalse();
            analysis.Stars.Should().BeNull();
            analysis.DisplayRgb48.Should().BeNull();
            analysis.Display16.Should().Equal(frame.Data);
            // BahtinovAnalysis.cs:50: a Gray16 display image goes through Convert16BppTo8Bpp as is
            analysis.GetDisplayGray8().Pixels.Should().Equal(PixelConversions.Convert16To8(frame.Data, frame.Width, frame.Height).Pixels);
        }

        [Test]
        public void AutoStretchOff_WithoutStarDetection_DisplaysTheLinearDebayeredFrame() {
            var frame = StarFrame(BayerPattern.RGGB);
            var analysis = FrameAnalyzer.Analyze(frame, new FrameAnalysisOptions { DetectStars = false, AutoStretch = false });

            analysis.IsStretched.Should().BeFalse();
            analysis.Display16.Should().BeNull();
            analysis.DisplayRgb48.Should().Equal(Debayer.Apply(frame, saveColorChannels: true, saveLumChannel: false).Rgb);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void Stretch_IsApplied_WhenEnabled_OrForcedOnByStarDetection(bool autoStretch, bool detectStars) {
            var frame = StarFrame(BayerPattern.None);
            var analysis = FrameAnalyzer.Analyze(frame, new FrameAnalysisOptions { DetectStars = detectStars, AutoStretch = autoStretch });

            analysis.IsStretched.Should().BeTrue();
            analysis.Display16.Should().Equal(AutoStretch.Apply(frame.Data, AutoStretch.GetStretchMap(ImageStatistics.Create(frame))));
            (analysis.Stars != null).Should().Be(detectStars);
        }

        [Test]
        public void AnalyzeBahtinov_WithAutoStretchOff_AnalysesTheLinearImage() {
            // a linear Bahtinov frame, as in BahtinovTests.ThroughTheFramePipeline_On16BitLinearData
            var pattern = BahtinovPattern.Render(400, 300, 200.4, 149.6, 62, 17, 5, seed: 3, background: 0, noise: 0);
            var data = new ushort[400 * 300];
            for (int i = 0; i < data.Length; i++) {
                data[i] = (ushort)(1200 + (pattern.Pixels[i] * 120));
            }
            var crop = new PixelRect(100, 50, 200, 200);
            var linear = FrameAnalyzer.Analyze(new PixelBuffer(data, 400, 300), new FrameAnalysisOptions { DetectStars = false, AutoStretch = false });
            var stretched = FrameAnalyzer.Analyze(new PixelBuffer(data, 400, 300), new FrameAnalysisOptions { DetectStars = false });

            var expected = BahtinovAnalyzer.Analyze(PixelConversions.Convert16To8(data, 400, 300).Crop(crop.X, crop.Y, crop.Width, crop.Height));
            var actual = FrameAnalyzer.AnalyzeBahtinov(linear, crop);
            actual.Success.Should().Be(expected.Success);
            actual.HoughLines.Should().BeEquivalentTo(expected.HoughLines, o => o.WithStrictOrdering());
            actual.SignedOffset.Should().Be(expected.SignedOffset);
            // and the stretched display image is a different input
            stretched.GetDisplayGray8().Pixels.Should().NotEqual(linear.GetDisplayGray8().Pixels);
        }
    }
}
