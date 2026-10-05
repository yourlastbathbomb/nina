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
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using System.Windows.Media;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// What the macOS build of NINA.Image cannot do yet, pinned so that a change is noticed and so that each failure is the
    /// clear one documented in mac/src/README-engine.md, not a crash or a corrupt file:
    /// <list type="bullet">
    /// <item>GDI+ (System.Drawing.Common has no macOS backend): stretch, debayer, upstream star detection and annotation,
    /// Bahtinov, contrast detection. The engine never calls them; star detection goes through the IStarDetection seam.</item>
    /// <item>CFITSIO is not mapped by NINA.Mac.Native (and needs the C long fix, plan P5): reading FITS files and the
    /// non-default CFITSIO writer (compressed FITS) fail with DllNotFoundException. The default managed writer is unaffected.</item>
    /// <item>WIC codecs: TIFF saving throws PlatformNotSupportedException.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class MacLimitsTest {

        private static void ShouldBeMissingNativeLibrary(Exception exception, string library) {
            var chain = new List<Exception>();
            for (var e = exception; e != null; e = e.InnerException) {
                chain.Add(e);
            }
            chain.OfType<DllNotFoundException>().Should().ContainSingle()
                .Which.Message.Should().Contain(library);
        }

        [Test]
        public async Task Stretch_FailsForWantOfGdiPlus() {
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(64, 64), 64, 64, RigFrame.LightFrameMetaData());

            var exception = await FluentActions.Awaiting(() => image.RenderImage().Stretch(0.2, -2.8, false)).Should().ThrowAsync<Exception>();

            ShouldBeMissingNativeLibrary(exception.Which, "gdiplus");
        }

        [Test]
        public async Task Debayer_FailsForWantOfGdiPlus() {
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(64, 64), 64, 64, RigFrame.LightFrameMetaData());

            var exception = FluentActions.Invoking(() => image.RenderImage().Debayer(saveColorChannels: true, saveLumChannel: true, bayerPattern: SensorType.RGGB)).Should().Throw<Exception>();

            ShouldBeMissingNativeLibrary(exception.Which, "gdiplus");
        }

        [Test]
        public async Task UpstreamStarDetection_FailsForWantOfGdiPlus() {
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(64, 64), 64, 64, RigFrame.LightFrameMetaData());
            var rendered = image.RenderImage();

            var exception = await FluentActions.Awaiting(() => new StarDetection().Detect(rendered, PixelFormats.Gray16,
                new StarDetectionParams { Sensitivity = StarSensitivityEnum.Normal, NoiseReduction = NoiseReductionEnum.None }, null!, CancellationToken.None))
                .Should().ThrowAsync<Exception>();

            ShouldBeMissingNativeLibrary(exception.Which, "gdiplus");
        }

        [Test]
        public void ConstructingTheGdiPlusBehaviours_DoesNotTouchGdiPlus() {
            // ImageDataFactory and the pluggable-behaviour selectors construct these for every image; that keeps working
            var detection = new StarDetection();
            var annotator = new StarAnnotator();

            detection.Name.Should().Be("NINA");
            detection.CreateAnalysis().Should().BeOfType<StarDetectionAnalysis>();
            annotator.Name.Should().NotBeNullOrEmpty();
        }

        [Test]
        public async Task ReadingFits_NeedsCfitsio_WhichIsNotMappedYet() {
            var folder = TestHost.NewImageFolder("fits-read");
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(64, 64), 64, 64, RigFrame.LightFrameMetaData());
            var path = await image.SaveToDisk(RigFrame.SaveInfo(folder));
            var (imageDataFactory, _, _) = RigFrame.Factories();

            var exception = await FluentActions.Awaiting(() => imageDataFactory.CreateFromFile(path, 16, true)).Should().ThrowAsync<Exception>();

            ShouldBeMissingNativeLibrary(exception.Which, "cfitsionative");
        }

        [Test]
        public async Task CfitsioWriter_NeedsCfitsio_AndLeavesNoFile() {
            var folder = TestHost.NewImageFolder("fits-cfitsio");
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(64, 64), 64, 64, RigFrame.LightFrameMetaData());
            var saveInfo = RigFrame.SaveInfo(folder);
            saveInfo.FITSUseLegacyWriter = false;
            saveInfo.FITSCompressionType = FITSCompressionTypeEnum.RICE;

            var exception = await FluentActions.Awaiting(() => image.SaveToDisk(saveInfo)).Should().ThrowAsync<Exception>();

            ShouldBeMissingNativeLibrary(exception.Which, "cfitsionative");
            Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }

        [Test]
        public async Task TiffSaving_NeedsWic_AndLeavesAnEmptyFileBehind() {
            // SaveTiff opens the FileStream before encoding (BaseImageData.cs:427-430), so the failed save leaves a 0-byte .tif,
            // as any encoder failure would on Windows. The macOS UI must not offer TIFF.
            var folder = TestHost.NewImageFolder("tiff");
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(64, 64), 64, 64, RigFrame.LightFrameMetaData());

            await FluentActions.Awaiting(() => image.SaveToDisk(RigFrame.SaveInfo(folder, FileTypeEnum.TIFF))).Should().ThrowAsync<PlatformNotSupportedException>();

            Directory.GetFiles(folder, "*.tif", SearchOption.AllDirectories).Should().ContainSingle()
                .Which.Should().Match(p => new FileInfo(p).Length == 0);
        }
    }
}
