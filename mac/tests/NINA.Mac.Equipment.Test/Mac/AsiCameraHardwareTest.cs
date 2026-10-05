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
using NINA.Core.Model.Equipment;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Mac;
using NINA.Equipment.Model;
using NINA.Image.FileFormat;
using NINA.Image.Interfaces;
using System.Globalization;
using ZWOptical.ASISDK;

namespace NINA.Mac.Equipment.Test {

    /// <summary>
    /// M3 "headless capture" on the real rig camera: NINA's own ASICamera (upstream NINA.Equipment, through NINA.Equipment's
    /// resolver) is found by the mac CameraChooser, wrapped in PersistSettingsCameraDecorator and connected as CameraVM does,
    /// reports the properties zwoprobe saw in M1, and takes a 1 s bin-2 light the way CameraVM.Capture / Download and
    /// ImagingVM.AddMetaData drive it (StartExposure, WaitUntilExposureIsReady, DownloadExposure, then ToImageData and
    /// SaveToDisk with the profile's FileSaveInfo). The FITS is read back by an independent reader, checked by fitsverify when
    /// installed, and opened and debayered by siril-cli 1.4.4.
    /// Explicit: run by hand with the camera on USB. The cooler is never switched on, and it is switched off (and read back) on
    /// every exit path before the camera is closed.
    /// </summary>
    [TestFixture]
    [Category("Hardware")]
    [NonParallelizable]
    public class AsiCameraHardwareTest {

        [Test]
        [Explicit("Needs the ZWO ASI585MC Pro on USB; run by hand")]
        public async Task NinasAsiCamera_TakesABin2Light_ThroughNinasPipeline_AndSirilOpensItAsRggb() {
            var cameraCount = ASICameras.Count;
            if (cameraCount == 0) {
                Assert.Inconclusive("No ZWO camera connected (ASICameras.Count = 0)");
            }

            var folder = TestHost.NewImageFolder("asi-capture");
            var (profileService, profile) = RigEngine.Profile(folder);
            var notifications = new List<string>();
            EventHandler<NotificationPostedEventArgs> onNotification = (_, e) => notifications.Add($"{e.Kind}: {e.Header} {e.Message}");
            Notification.Posted += onNotification;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var token = timeout.Token;

            // The device list the mac chooser builds (ASI block of CameraChooserVM): No camera, then every ASI camera
            var chooser = new CameraChooser(profileService, RigEngine.ExposureDataFactory(profileService));
            await chooser.GetEquipment();
            TestContext.Out.WriteLine("Chooser: " + string.Join(", ", chooser.Devices.Select(d => $"{d.Id} [{d.GetType().Name}]")));
            var asi = chooser.Devices.OfType<ASICamera>().SingleOrDefault(c => c.Name.Contains("ASI585MC"));
            asi.Should().NotBeNull("the chooser must list the ASI585MC Pro");
            var cameraId = Enumerable.Range(0, cameraCount).Select(ASICameraDll.GetCameraProperties).Single(i => i.Name == asi!.Name).CameraID;

            // CameraVM.ChooseCamera: every camera is wrapped in the profile decorator before Connect
            var camera = new PersistSettingsCameraDecorator(profileService, asi!);
            int? coolerAfterSwitchOff = null;
            IImageData image;
            string path;
            try {
                (await camera.Connect(token)).Should().BeTrue("Connect must succeed; notifications: " + string.Join(" | ", notifications));
                TestContext.Out.WriteLine($"Connected {camera.Id} ({camera.DisplayName}), driver {camera.DriverInfo} {camera.DriverVersion}, cooler on at connect: {camera.CoolerOn}");

                // The properties zwoprobe read in M1 (mac/docs/m1-camera-results.md), now through ICamera
                camera.Name.Should().Be("ZWO ASI585MC Pro");
                camera.CameraXSize.Should().Be(3840);
                camera.CameraYSize.Should().Be(2160);
                camera.SensorType.Should().Be(SensorType.RGGB);
                camera.BayerOffsetX.Should().Be(0);
                camera.BayerOffsetY.Should().Be(0);
                camera.PixelSizeX.Should().Be(2.9);
                camera.PixelSizeY.Should().Be(2.9);
                camera.BinningModes.Select(b => (b.X, b.Y)).Should().Equal(((short)1, (short)1), ((short)2, (short)2), ((short)3, (short)3), ((short)4, (short)4));
                camera.MaxBinX.Should().Be(4);
                camera.GainMin.Should().Be(0);
                camera.GainMax.Should().Be(600);
                camera.OffsetMin.Should().Be(0);
                camera.OffsetMax.Should().Be(200);
                camera.USBLimitMin.Should().Be(40);
                camera.USBLimitMax.Should().Be(100);
                camera.ExposureMin.Should().Be(32e-6);
                camera.ExposureMax.Should().Be(2000);
                camera.CanSetTemperature.Should().BeTrue("the Pro has a cooler (ASI_COOLER_ON is writable)");
                camera.HasShutter.Should().BeFalse();
                camera.BitDepth.Should().Be(16);

                // The SDK reports 0.0 °C until its first temperature poll, about 2 s after opening (M1 finding 2)
                await Task.Delay(TimeSpan.FromSeconds(3), token);

                // CameraVM.Capture: gain and offset of the sequence, its binning, no sub-frame; then ASICamera.StartExposure
                var sequence = new CaptureSequence(1, CaptureSequence.ImageTypes.LIGHT, null, new BinningMode(2, 2), 1) {
                    Gain = 200,
                    ProgressExposureCount = 1,
                };
                camera.Gain = sequence.Gain;
                camera.SetBinning(sequence.Binning.X, sequence.Binning.Y);
                camera.EnableSubSample = false;
                var exposureStart = DateTime.UtcNow;
                camera.StartExposure(sequence);
                using (var ready = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                    ready.CancelAfter(TimeSpan.FromSeconds(sequence.ExposureTime + profile.CameraSettings.Timeout));
                    await camera.WaitUntilExposureIsReady(ready.Token);
                }
                var midpoint = exposureStart + TimeSpan.FromTicks((DateTime.UtcNow - exposureStart).Ticks / 2);

                // CameraVM.Download
                var exposure = await camera.DownloadExposure(token);
                exposure.Should().NotBeNull("DownloadExposure returns null after a driver error; notifications: " + string.Join(" | ", notifications));
                exposure!.MetaData.FromProfile(profile);
                if (double.IsNaN(exposure.MetaData.Image.ExposureTime)) {
                    exposure.MetaData.Image.ExposureTime = sequence.ExposureTime;
                }
                // ImagingVM.AddMetaData (no mount, filter wheel, focuser or rotator connected)
                exposure.MetaData.Image.Id = 1;
                if (exposure.MetaData.Image.ExposureMidPoint == DateTime.MinValue) {
                    exposure.MetaData.Image.ExposureMidPoint = midpoint;
                }
                exposure.MetaData.Image.Binning = sequence.Binning.Name;
                exposure.MetaData.Image.ExposureNumber = sequence.ProgressExposureCount;
                exposure.MetaData.Image.ImageType = sequence.ImageType;
                exposure.MetaData.Target.Name = "Headless capture";

                image = await exposure.ToImageData(null, token);
                path = await image.SaveToDisk(new FileSaveInfo(profileService), token);
            } finally {
                coolerAfterSwitchOff = CoolerOffAndDisconnect(camera, cameraId);
                Notification.Posted -= onNotification;
            }

            coolerAfterSwitchOff.Should().Be(0, "the cooler must be off when the test leaves the camera");
            TestContext.Out.WriteLine($"Saved {path}");
            notifications.Should().BeEmpty();

            // Where NINA's default pattern put it: <folder>/<date>/LIGHT/<datetime>_<filter>_<temp>_1.00s_0001.fits
            Path.GetRelativePath(folder, path).Split('/').Should().HaveCount(3).And.HaveElementAt(1, "LIGHT");
            path.Should().EndWith("_1.00s_0001.fits");

            // Independent FITS reader (FITS 4.0 standard, no NINA code)
            var file = NINA.Mac.Image.Test.FitsFile.Read(path);
            var meta = image.MetaData;
            TestContext.Out.WriteLine(string.Join("\n", file.Cards));
            file.Width.Should().Be(1920);
            file.Height.Should().Be(1080);
            file.Pixels.Should().Equal((ushort[])image.Data.FlatArray, "the FITS holds exactly the downloaded frame");
            file.Value("BAYERPAT").Should().Be("RGGB");
            file.Value("ROWORDER").Should().Be("TOP-DOWN");
            file.Integer("XBINNING").Should().Be(2);
            file.Integer("YBINNING").Should().Be(2);
            file.Double("XPIXSZ").Should().Be(5.8);
            file.Integer("GAIN").Should().Be(200);
            file.Double("EXPTIME").Should().Be(1.0);
            file.Value("IMAGETYP").Should().Be("LIGHT");
            file.Value("INSTRUME").Should().Be("ZWO ASI585MC Pro");
            file.Double("CCD-TEMP").Should().Be(meta.Camera.Temperature);
            file.Value("EGAIN").Should().Be(((double)meta.Camera.ElectronsPerADU).ToString("0.0##############", CultureInfo.InvariantCulture));
            var median = file.Pixels.OrderBy(p => p).ElementAt(file.Pixels.Length / 2);
            TestContext.Out.WriteLine($"CCD-TEMP {meta.Camera.Temperature} C, offset {meta.Camera.Offset}, median {median} ADU");

            if (File.Exists(SirilCli.Fitsverify)) {
                var (exitCode, output) = SirilCli.Run(SirilCli.Fitsverify, folder, TimeSpan.FromMinutes(1), path);
                TestContext.Out.WriteLine(output.Trim().Split('\n').Last());
                exitCode.Should().Be(0, output);
                output.Should().Contain("0 warning(s) and 0 error(s)");
            }

            if (!File.Exists(SirilCli.Path)) {
                Assert.Fail($"{SirilCli.Path} is not installed; the Siril check is part of this test");
            }
            var log = SirilCli.LoadAndDebayer(path, folder);
            TestContext.Out.WriteLine(string.Join("\n", log.Split('\n').Where(l => l.Contains("FITS") || l.Contains("Pattern") || l.Contains("layer:") || l.Contains("siril 1."))));
            log.Should().Contain("Welcome to siril 1.4.4");
            log.Should().Contain("Reading FITS: file frame.fits, 1 layer(s), 1920x1080 pixels, 16 bits");
            log.Should().Contain("Filter Pattern: RGGB from header, Orientation: top-down from header");
            log.Should().Contain("Saving FITS: file deb/frame_00001.fit, 3 layer(s), 1920x1080 pixels, 16 bits");
        }

        /// <summary>
        /// Switches the cooler off and reads it back, then closes the camera. The raw SDK call works even if Connect failed half
        /// way (opened, not initialised), when NINA's ASICamera has no control list; NINA's own setter runs as well when connected.
        /// Returns the read-back ASI_COOLER_ON value, or null if the camera was not open (then this session cannot have switched
        /// the cooler on).
        /// </summary>
        private static int? CoolerOffAndDisconnect(ICamera camera, int cameraId) {
            int? cooler = null;
            try {
                try {
                    if (camera.Connected) {
                        camera.CoolerOn = false;
                    }
                } catch (Exception ex) {
                    TestContext.Out.WriteLine($"ICamera.CoolerOn = false failed: {ex.Message}");
                }
                try {
                    if (ASICameraDll.GetControlValue(cameraId, ASICameraDll.ASI_CONTROL_TYPE.ASI_COOLER_ON, out _) != 0) {
                        ASICameraDll.SetControlValue(cameraId, ASICameraDll.ASI_CONTROL_TYPE.ASI_COOLER_ON, 0, false);
                    }
                    cooler = ASICameraDll.GetControlValue(cameraId, ASICameraDll.ASI_CONTROL_TYPE.ASI_COOLER_ON, out _);
                    TestContext.Out.WriteLine($"ASI_COOLER_ON read back: {cooler}");
                } catch (ASICameraException ex) {
                    TestContext.Out.WriteLine($"Cooler not reachable (camera not open): {ex.Message}");
                }
            } finally {
                try {
                    camera.Disconnect();
                } catch (Exception ex) {
                    TestContext.Out.WriteLine($"Disconnect failed: {ex.Message}");
                }
            }
            return cooler;
        }
    }
}
