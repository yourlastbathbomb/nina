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
using Moq;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Equipment.Utility;
using NINA.Image.FileFormat;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Mac.Equipment.Test {

    /// <summary>
    /// The headless capture path without hardware: a stand-in ICamera behind NINA's PersistSettingsCameraDecorator, driven as
    /// CameraVM drives a camera, returning the exposure data ASICamera.DownloadExposure would build (FromCamera, then the
    /// ExposureDataFactory), then ToImageData and SaveToDisk with the profile's FileSaveInfo. AsiCameraHardwareTest runs the
    /// same steps on the real ASI585MC.
    /// </summary>
    [TestFixture]
    public class HeadlessCapturePathTest {

        private static Mock<ICamera> StandInAsi585(ushort[] frame, IExposureDataFactory factory) {
            var camera = new Mock<ICamera>();
            short bin = 1;
            camera.SetupGet(x => x.Connected).Returns(true);
            camera.SetupGet(x => x.Id).Returns("ZWOptical_ZWO ASI585MC Pro_");
            camera.SetupGet(x => x.Name).Returns("ZWO ASI585MC Pro");
            camera.SetupGet(x => x.SensorType).Returns(SensorType.RGGB);
            camera.SetupGet(x => x.PixelSizeX).Returns(2.9);
            camera.SetupGet(x => x.ElectronsPerADU).Returns(0.24f);
            camera.SetupGet(x => x.CanGetGain).Returns(true);
            camera.SetupGet(x => x.CanSetGain).Returns(true);
            camera.SetupGet(x => x.CanSetOffset).Returns(true);
            camera.SetupProperty(x => x.Gain);
            camera.SetupProperty(x => x.Offset);
            camera.SetupGet(x => x.Temperature).Returns(-9.8);
            camera.SetupGet(x => x.TemperatureSetPoint).Returns(-10);
            camera.SetupGet(x => x.ReadoutModes).Returns(new List<string> { "Default" });
            camera.Setup(x => x.SetBinning(It.IsAny<short>(), It.IsAny<short>())).Callback<short, short>((x, _) => bin = x);
            camera.SetupGet(x => x.BinX).Returns(() => bin);
            camera.SetupGet(x => x.BinY).Returns(() => bin);
            camera.Setup(x => x.Connect(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            camera.Setup(x => x.WaitUntilExposureIsReady(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            camera.Setup(x => x.DownloadExposure(It.IsAny<CancellationToken>())).Returns(() => {
                // ASICamera.DownloadExposure: metadata from the camera, then the factory's ImageArrayExposureData
                var metaData = new ImageMetaData();
                metaData.FromCamera(camera.Object);
                metaData.Image.SetExposureTimes(DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow);
                return Task.FromResult<IExposureData>(factory.CreateImageArrayExposureData((ushort[])frame.Clone(), 1920, 1080, 16, true, metaData));
            });
            return camera;
        }

        [Test]
        public async Task DecoratedCamera_RestoresProfileSettings_AndItsFrameIsSavedWhereThePatternSays() {
            var folder = TestHost.NewImageFolder("headless");
            var (profileService, profile) = RigEngine.Profile(folder);
            profile.CameraSettings.Gain = 200;
            profile.CameraSettings.Offset = 3;
            profile.CameraSettings.BinningX = 2;
            profile.CameraSettings.BinningY = 2;
            var frame = Enumerable.Range(0, 1920 * 1080).Select(i => (ushort)(i * 31 % 65536)).ToArray();
            var standIn = StandInAsi585(frame, RigEngine.ExposureDataFactory(profileService));
            var camera = new PersistSettingsCameraDecorator(profileService, standIn.Object);

            (await camera.Connect(CancellationToken.None)).Should().BeTrue();

            // RestoreCameraProfileDefaults: the profile's binning, gain and offset are applied on connect
            standIn.Verify(x => x.SetBinning(2, 2), Times.Once);
            camera.Gain.Should().Be(200);
            camera.Offset.Should().Be(3);

            var sequence = new CaptureSequence(1, CaptureSequence.ImageTypes.LIGHT, null, new BinningMode(2, 2), 1) { ProgressExposureCount = 7 };
            camera.StartExposure(sequence);
            await camera.WaitUntilExposureIsReady(CancellationToken.None);
            var exposure = await camera.DownloadExposure(CancellationToken.None);
            exposure.MetaData.FromProfile(profile);
            exposure.MetaData.Image.ExposureTime = sequence.ExposureTime;
            exposure.MetaData.Image.Binning = sequence.Binning.Name;
            exposure.MetaData.Image.ExposureNumber = sequence.ProgressExposureCount;
            exposure.MetaData.Image.ImageType = sequence.ImageType;
            var image = await exposure.ToImageData();
            var path = await image.SaveToDisk(new FileSaveInfo(profileService), CancellationToken.None);

            standIn.Verify(x => x.StartExposure(sequence), Times.Once);
            Path.GetRelativePath(folder, path).Split('/').Should().HaveCount(3).And.HaveElementAt(1, "LIGHT");
            path.Should().EndWith("_-9.80_1.00s_0007.fits");
            var file = NINA.Mac.Image.Test.FitsFile.Read(path);
            file.Pixels.Should().Equal(frame);
            file.Value("BAYERPAT").Should().Be("RGGB");
            file.Integer("XBINNING").Should().Be(2);
            file.Double("XPIXSZ").Should().Be(5.8);
            file.Integer("GAIN").Should().Be(200);
            file.Integer("OFFSET").Should().Be(3);
            file.Double("FOCALLEN").Should().Be(2500);
            file.Value("TELESCOP").Should().Be("Meade LX200GPS 10in");
        }
    }
}
