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
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using System.Windows.Media.Imaging;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// The seam through which macOS star detection plugs into upstream NINA.Image without editing it: an IStarDetection (and
    /// IStarAnnotator) handed to ImageDataFactory through IPluggableBehaviorSelector, exactly where Windows NINA puts its own
    /// StarDetection or a plugin such as Hocus Focus. RenderedImage.DetectStars then calls it with the raw frame and writes its
    /// result into the image's StarDetectionAnalysis, which the HFR/star-count file patterns and the sequencer read.
    /// <para>
    /// <see cref="RecordingStarDetection"/> stands in for the planned adapter to NINA.Mac.ImageAnalysis (design in
    /// mac/src/README-engine.md): it reads what that adapter will read (raw pixels, size, Bayer pattern, parameters) and returns
    /// a result, so the test proves the data flow, not the detection.
    /// </para>
    /// </summary>
    [TestFixture]
    public class StarDetectionSeamTest {

        /// <summary>Counts single bright pixels above a threshold and reports a fixed HFR: enough to see the data arrive.</summary>
        private sealed class RecordingStarDetection : IStarDetection {
            public List<(IRenderedImage Image, System.Windows.Media.PixelFormat Format, StarDetectionParams Params)> Calls { get; } = new();

            public string Name => "macOS test detection";

            public string ContentId => GetType().FullName!;

            public Task<StarDetectionResult> Detect(IRenderedImage image, System.Windows.Media.PixelFormat pf, StarDetectionParams p, IProgress<ApplicationStatus> progress, CancellationToken token) {
                Calls.Add((image, pf, p));
                var raw = image.RawImageData;
                var pixels = raw.Data.FlatArray;
                var width = raw.Properties.Width;
                var stars = new List<DetectedStar>();
                for (var i = 0; i < pixels.Length; i++) {
                    if (pixels[i] > 50000) {
                        stars.Add(new DetectedStar { HFR = 2.5, Position = new Accord.Point(i % width, i / width), MaxBrightness = pixels[i] });
                    }
                }
                return Task.FromResult(new StarDetectionResult {
                    AverageHFR = 2.5,
                    AverageFWHM = 4.25,
                    AverageEccentricity = 0.1,
                    HFRStdDev = 0.0,
                    DetectedStars = stars.Count,
                    StarList = stars,
                    Params = p,
                });
            }

            public IStarDetectionAnalysis CreateAnalysis() {
                return new StarDetectionAnalysis();
            }

            public void UpdateAnalysis(IStarDetectionAnalysis analysis, StarDetectionParams p, StarDetectionResult result) {
                // As upstream StarDetection.UpdateAnalysis (StarDetection.cs:989-999)
                analysis.HFR = result.AverageHFR;
                analysis.FWHM = result.AverageFWHM;
                analysis.Eccentricity = result.AverageEccentricity;
                analysis.HFRStDev = result.HFRStdDev;
                analysis.HFRUnit = StarMeasurementUnit.Pixels;
                analysis.FWHMUnit = StarMeasurementUnit.Pixels;
                analysis.HFRStDevUnit = StarMeasurementUnit.Pixels;
                analysis.DetectedStars = result.DetectedStars;
                analysis.StarList = result.StarList;
            }
        }

        private sealed class RecordingAnnotator : IStarAnnotator {
            public int Calls { get; private set; }

            public string Name => "macOS test annotator";

            public string ContentId => GetType().FullName!;

            public Task<BitmapSource> GetAnnotatedImage(StarDetectionParams p, StarDetectionResult result, BitmapSource imageToAnnotate, int maxStars = 200, CancellationToken token = default) {
                Calls++;
                return Task.FromResult(imageToAnnotate);
            }
        }

        private static ushort[] FrameWithStars(out int stars) {
            var pixels = Enumerable.Repeat((ushort)1000, RigFrame.Width * RigFrame.Height).ToArray();
            var positions = new[] { (100, 100), (960, 540), (1800, 1000), (10, 1070), (1919, 0) };
            foreach (var (x, y) in positions) {
                pixels[y * RigFrame.Width + x] = 60000;
            }
            stars = positions.Length;
            return pixels;
        }

        [Test]
        public async Task DetectStars_CallsTheSelectedBehaviour_WithTheRawFrame_AndRecordsItsResult() {
            var detection = new RecordingStarDetection();
            var annotator = new RecordingAnnotator();
            var (_, exposureDataFactory, _) = RigFrame.Factories(detection, annotator);
            var pixels = FrameWithStars(out var stars);
            var image = await exposureDataFactory.CreateImageArrayExposureData(pixels, RigFrame.Width, RigFrame.Height, 16, true, RigFrame.LightFrameMetaData()).ToImageData();

            var rendered = await image.RenderImage().DetectStars(false, StarSensitivityEnum.High, NoiseReductionEnum.Median);

            detection.Calls.Should().ContainSingle();
            var call = detection.Calls[0];
            call.Image.Should().BeSameAs(rendered);
            call.Image.RawImageData.Data.FlatArray.Should().BeSameAs(pixels);
            call.Image.RawImageData.Properties.IsBayered.Should().BeTrue();
            call.Image.RawImageData.MetaData.Camera.SensorType.Should().Be(SensorType.RGGB);
            call.Format.Should().Be(System.Windows.Media.PixelFormats.Gray16);
            call.Params.Sensitivity.Should().Be(StarSensitivityEnum.High);
            call.Params.NoiseReduction.Should().Be(NoiseReductionEnum.Median);
            call.Params.IsAutoFocus.Should().BeFalse();
            call.Params.UseROI.Should().BeFalse("the profile's crop ratios are 1");
            annotator.Calls.Should().Be(0, "annotateImage was false");

            image.StarDetectionAnalysis.DetectedStars.Should().Be(stars);
            image.StarDetectionAnalysis.HFR.Should().Be(2.5);
            image.StarDetectionAnalysis.FWHM.Should().Be(4.25);
        }

        [Test]
        public async Task DetectStars_PassesTheProfilesAutofocusCrop_AndAnnotatesThroughTheSelectedAnnotator() {
            var detection = new RecordingStarDetection();
            var annotator = new RecordingAnnotator();
            var (_, exposureDataFactory, profile) = RigFrame.Factories(detection, annotator);
            profile.SetupGet(x => x.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio).Returns(0.5);
            profile.SetupGet(x => x.ActiveProfile.FocuserSettings.AutoFocusUseBrightestStars).Returns(3);
            var image = await exposureDataFactory.CreateImageArrayExposureData(FrameWithStars(out _), RigFrame.Width, RigFrame.Height, 16, true, RigFrame.LightFrameMetaData()).ToImageData();

            await image.RenderImage().DetectStars(true, StarSensitivityEnum.Normal, NoiseReductionEnum.None);

            var p = detection.Calls.Single().Params;
            p.UseROI.Should().BeTrue();
            p.InnerCropRatio.Should().Be(0.5);
            p.OuterCropRatio.Should().Be(1.0);
            p.NumberOfAFStars.Should().Be(3);
            annotator.Calls.Should().Be(1);
        }

        [Test]
        public async Task DetectionResult_ReachesTheFilePatterns() {
            var detection = new RecordingStarDetection();
            var (_, exposureDataFactory, _) = RigFrame.Factories(detection, new RecordingAnnotator());
            var image = await exposureDataFactory.CreateImageArrayExposureData(FrameWithStars(out var stars), RigFrame.Width, RigFrame.Height, 16, true, RigFrame.LightFrameMetaData()).ToImageData();
            await image.RenderImage().DetectStars(false, StarSensitivityEnum.Normal, NoiseReductionEnum.None);
            var folder = TestHost.NewImageFolder("seam");
            var saveInfo = RigFrame.SaveInfo(folder);
            saveInfo.FilePattern = "$$IMAGETYPE$$\\HFR $$HFR$$ stars $$STARCOUNT$$";

            var path = await image.SaveToDisk(saveInfo);

            path.Should().Be(Path.Combine(folder, "LIGHT", $"HFR 2.50 stars {stars}.fits"));
        }

        [Test]
        public async Task WithoutDetection_TheAnalysisStaysEmpty_AndThePatternsAreBlank() {
            // What a frame saved before any detection carries, on Windows as here
            var (_, exposureDataFactory, _) = RigFrame.Factories(new RecordingStarDetection(), new RecordingAnnotator());
            var image = await exposureDataFactory.CreateImageArrayExposureData(FrameWithStars(out _), RigFrame.Width, RigFrame.Height, 16, true, RigFrame.LightFrameMetaData()).ToImageData();

            image.StarDetectionAnalysis.DetectedStars.Should().Be(-1);
            double.IsNaN(image.StarDetectionAnalysis.HFR).Should().BeTrue();
            image.GetImagePatterns().GetImageFileString("a$$HFR$$b$$STARCOUNT$$c").Should().Be("abc");
        }
    }
}
