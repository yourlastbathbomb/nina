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
using NINA.Image.ImageData;
using System.Drawing;
using System.Globalization;
using ZWOptical.ASISDK;
using static ZWOptical.ASISDK.ASICameraDll;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// A real frame from the rig's ZWO ASI585MC Pro through NINA.Image: upstream ASICameraDll (linked into this test assembly,
    /// as the M1 probe does) captures a short bin-2 RAW16 frame the way ASICamera does, the metadata is filled the way
    /// ImageMetaDataExtension.FromCamera fills it from that camera, and the frame goes through ImageArrayExposureData,
    /// SaveToDisk (managed FITS writer), the independent reader, fitsverify and Siril.
    /// Explicit: run by hand with the camera on USB. The cooler is never switched on, and it is switched off on every exit path.
    /// </summary>
    [TestFixture]
    [Category("Hardware")]
    public class AsiFrameHardwareTest {

        [Test]
        [Explicit("Needs the ZWO ASI585MC Pro on USB; run by hand")]
        public async Task RealBin2Frame_SavesThroughNinaImage_WithTheBayerHeaders_AndSirilDebayersIt() {
            if (GetNumOfConnectedCameras() == 0) {
                Assert.Inconclusive("No ZWO camera connected");
            }
            var info = GetCameraProperties(0);
            var id = info.CameraID;
            OpenCamera(id);
            ushort[] pixels;
            ImageMetaData metaData;
            int width;
            int height;
            try {
                InitCamera(id);
                var controls = Enumerable.Range(0, GetNumOfControls(id)).Select(i => GetControlCaps(id, i)).ToDictionary(c => c.ControlType);
                void Set(ASI_CONTROL_TYPE type, int value) {
                    if (controls.TryGetValue(type, out var caps) && caps.IsWritable == ASI_BOOL.ASI_TRUE) {
                        SetControlValue(id, type, value, false);
                    }
                }
                int Get(ASI_CONTROL_TYPE type) => GetControlValue(id, type, out _);

                Set(ASI_CONTROL_TYPE.ASI_COOLER_ON, 0);
                // ASICamera.Initialize (NINA.Equipment/Equipment/MyCamera/ASICamera.cs:640-660) with mono bin off
                Set(ASI_CONTROL_TYPE.ASI_FLIP, (int)ASI_FLIP_STATUS.ASI_FLIP_NONE);
                Set(ASI_CONTROL_TYPE.ASI_BANDWIDTHOVERLOAD, 40);
                Set(ASI_CONTROL_TYPE.ASI_WB_B, 50);
                Set(ASI_CONTROL_TYPE.ASI_WB_R, 50);
                Set(ASI_CONTROL_TYPE.ASI_GAMMA, 50);
                Set(ASI_CONTROL_TYPE.ASI_HIGH_SPEED_MODE, 0);
                Set(ASI_CONTROL_TYPE.ASI_HARDWARE_BIN, 0);
                Set(ASI_CONTROL_TYPE.ASI_OVERCLOCK, 0);
                Set(ASI_CONTROL_TYPE.ASI_PATTERN_ADJUST, 0);
                Set(ASI_CONTROL_TYPE.ASI_MONO_BIN, 0);
                Set(ASI_CONTROL_TYPE.ASI_GAIN, 200);

                // ASICamera.StartExposure: bin 2, width a multiple of 8, height a multiple of 2, RAW16
                width = info.MaxWidth / 2 - info.MaxWidth / 2 % 8;
                height = info.MaxHeight / 2 - info.MaxHeight / 2 % 2;
                SetROIFormat(id, new Size(width, height), 2, ASI_IMG_TYPE.ASI_IMG_RAW16);
                SetStartPos(id, new Point(0, 0));
                Set(ASI_CONTROL_TYPE.ASI_EXPOSURE, 100_000);
                var start = DateTime.UtcNow;
                StartExposure(id, false);
                var deadline = DateTime.UtcNow.AddSeconds(30);
                ASI_EXPOSURE_STATUS status;
                while ((status = GetExposureStatus(id)) == ASI_EXPOSURE_STATUS.ASI_EXP_WORKING) {
                    if (DateTime.UtcNow > deadline) {
                        StopExposure(id);
                        Assert.Fail("Exposure did not finish within 30 s");
                    }
                    await Task.Delay(10);
                }
                var end = DateTime.UtcNow;
                status.Should().Be(ASI_EXPOSURE_STATUS.ASI_EXP_SUCCESS);
                pixels = new ushort[width * height];
                GetDataAfterExp(id, pixels, pixels.Length * 2).Should().BeTrue();

                // ImageMetaDataExtension.FromCamera for this camera (BayerPattern Auto: the driver's pattern, offsets 0)
                metaData = new ImageMetaData();
                metaData.Camera.Id = $"ZWOptical_{info.Name}_{GetId(id)}";
                metaData.Camera.Name = info.Name;
                metaData.Camera.Temperature = Get(ASI_CONTROL_TYPE.ASI_TEMPERATURE) / 10.0;
                metaData.Camera.Gain = Get(ASI_CONTROL_TYPE.ASI_GAIN);
                metaData.Camera.Offset = Get(ASI_CONTROL_TYPE.ASI_OFFSET);
                metaData.Camera.SetPoint = Get(ASI_CONTROL_TYPE.ASI_TARGET_TEMP);
                metaData.Camera.BinX = 2;
                metaData.Camera.BinY = 2;
                metaData.Camera.ElectronsPerADU = info.ElecPerADU;
                metaData.Camera.PixelSize = info.PixelSize;
                metaData.Camera.USBLimit = Get(ASI_CONTROL_TYPE.ASI_BANDWIDTHOVERLOAD);
                metaData.Camera.SensorType = info.IsColorCam == ASI_BOOL.ASI_TRUE && info.BayerPattern == ASI_BAYER_PATTERN.ASI_BAYER_RG ? SensorType.RGGB : SensorType.Monochrome;
                metaData.Image.SetExposureTimes(start, end);
                metaData.Image.ExposureTime = 0.1;
                metaData.Image.ExposureNumber = 1;
                metaData.Image.ImageType = "LIGHT";
                metaData.Target.Name = "Hardware test";
            } finally {
                try {
                    if (GetControlValue(id, ASI_CONTROL_TYPE.ASI_COOLER_ON, out _) != 0) {
                        SetControlValue(id, ASI_CONTROL_TYPE.ASI_COOLER_ON, 0, false);
                    }
                } finally {
                    CloseCamera(id);
                }
            }

            TestContext.Out.WriteLine($"{metaData.Camera.Name}: {width} x {height}, {metaData.Camera.Temperature} C, gain {metaData.Camera.Gain}, offset {metaData.Camera.Offset}, " +
                $"{metaData.Camera.ElectronsPerADU} e/ADU, pixel {metaData.Camera.PixelSize} um, median {pixels.OrderBy(p => p).ElementAt(pixels.Length / 2)}");
            info.Name.Should().Contain("ASI585MC");
            metaData.Camera.SensorType.Should().Be(SensorType.RGGB);
            width.Should().Be(1920);
            height.Should().Be(1080);

            var folder = TestHost.NewImageFolder("asi");
            var image = await RigFrame.Capture((ushort[])pixels.Clone(), width, height, metaData);
            var path = await image.SaveToDisk(RigFrame.SaveInfo(folder));
            var file = FitsFile.Read(path);

            file.Pixels.Should().Equal(pixels);
            file.Value("BAYERPAT").Should().Be("RGGB");
            file.Value("ROWORDER").Should().Be("TOP-DOWN");
            file.Integer("XBINNING").Should().Be(2);
            file.Double("XPIXSZ").Should().Be(metaData.Camera.PixelSize * 2);
            file.Integer("GAIN").Should().Be(200);
            file.Value("EGAIN").Should().Be(((double)info.ElecPerADU).ToString("0.0##############", CultureInfo.InvariantCulture));
            file.Double("CCD-TEMP").Should().Be(metaData.Camera.Temperature);

            if (File.Exists(ExternalTools.Fitsverify)) {
                var (exitCode, output) = ExternalTools.Run(ExternalTools.Fitsverify, folder, TimeSpan.FromMinutes(1), path);
                exitCode.Should().Be(0, output);
                output.Should().Contain("0 warning(s) and 0 error(s)");
            }
            if (File.Exists(ExternalTools.SirilCli)) {
                var (red, green, blue, log) = ExternalTools.SirilDebayerMeans(path, folder);
                TestContext.Out.WriteLine($"Siril debayered means: R {red}, G {green}, B {blue}");
                log.Should().Contain("RGGB from header").And.Contain("top-down from header");
            }
        }
    }
}
