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
using NINA.Mac.App.Services;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine.Test {

    /// <summary>
    /// The Real camera: NINA's CameraVM and ImagingVM over the fake ZWO camera. Connect, cooling, exposures with gain, offset
    /// and binning, frames saved through NINA's file patterns into the Siril layout, HFR from NINA.Mac.ImageAnalysis,
    /// mono-bin for focus frames only, the first-reading guard, a lost USB link and the stuck-temperature guard.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class CameraServiceTests {

        [Test]
        public async Task Connect_ReportsTheCamera_AndDisconnectSwitchesTheCoolerOff() {
            await using var rig = new EngineRig("camera connect");
            var camera = rig.Engine.Camera;
            var changes = 0;
            camera.Changed += (_, _) => changes++;

            await camera.ConnectAsync();

            camera.State.Should().Be(DeviceConnectionState.Connected);
            camera.IsSimulated.Should().BeFalse();
            camera.Info.Model.Should().Be("ZWO ASI585MC Pro (fake)");
            camera.Info.Width.Should().Be(1920);
            camera.Info.Height.Should().Be(1080);
            camera.Info.PixelSizeMicrons.Should().Be(2.9);
            camera.Info.BayerPattern.Should().Be("RGGB");
            camera.Info.Bins.Should().Equal(1, 2, 3, 4);
            camera.Info.HasCooler.Should().BeTrue();
            changes.Should().BeGreaterThan(0);

            camera.SetCooler(true, 0);
            rig.Camera.CoolerOn.Should().BeTrue();
            rig.Camera.TemperatureSetPoint.Should().Be(0);
            camera.CoolerOn.Should().BeTrue();
            camera.TargetTemperature.Should().Be(0);
            // CameraVM polls the device every 0.5 s here; the app's tick then picks the readings up
            (await EngineRig.Eventually(() => { camera.Tick(); return camera.SensorTemperature == 0 && camera.CoolerPowerPercent == 41; }, TimeSpan.FromSeconds(5)))
                .Should().BeTrue("the fake holds the set point at 41 % power");

            await camera.DisconnectAsync();
            camera.State.Should().Be(DeviceConnectionState.Disconnected);
            rig.Camera.CoolerOn.Should().BeFalse("NINA's ASICamera.Disconnect would leave the cooler running; the service switches it off first");
            rig.Camera.CoolerWrites.Last().Should().BeFalse();
            rig.Camera.Connected.Should().BeFalse();
        }

        [Test]
        public async Task TemperatureReadings_AreIgnoredJustAfterConnect() {
            var clock = new ManualClock(DateTimeOffset.Now);
            await using var rig = new EngineRig("camera first reading", clock: clock, firstReadingDelay: TimeSpan.FromSeconds(3));
            await rig.Engine.Camera.ConnectAsync();
            rig.Engine.Camera.SensorTemperature.Should().BeNull("the SDK reports 0.0 °C before its first poll (M1 finding 2)");
            clock.Advance(TimeSpan.FromSeconds(4));
            rig.Engine.Camera.SensorTemperature.Should().Be(26, "the fake sits at ambient with the cooler off");
        }

        [Test]
        public async Task Light_IsSavedThroughNinasPatterns_IntoTheSirilLayout_WithHfr() {
            await using var rig = new EngineRig("camera light", s => {
                s.Gain = 252;
                s.Offset = 15;
            });
            await rig.Engine.Camera.ConnectAsync();

            var frame = await rig.Engine.Camera.ExposeAsync(new ExposureRequest(FrameType.Light, 0.2, 252, 15, 2) { TargetName = "NGC 253" });

            var sequence = rig.Camera.Exposures.Last();
            sequence.ImageType.Should().Be("LIGHT");
            sequence.Gain.Should().Be(252);
            sequence.Offset.Should().Be(15);
            sequence.Binning.X.Should().Be(2);
            rig.Camera.BinX.Should().Be(2, "NINA's CameraVM applied the binning");
            frame.FilePath.Should().NotBeNull();
            File.Exists(frame.FilePath).Should().BeTrue();
            var folders = rig.Engine.CurrentFolders();
            Path.GetDirectoryName(frame.FilePath).Should().Be(folders.LightsDirectory("NGC 253"), "NINA's patterns write NINA.Mac.Siril's layout");
            Path.GetFileName(frame.FilePath).Should().MatchRegex(@"^\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d_0\.20s_2x2_g252_26\.00C_\d{4}\.fits$");
            frame.Stars.Should().BeGreaterThan(20, "the synthetic field has 40 stars");
            frame.Hfr.Should().BeInRange(1.0, 4.0);
            frame.MeanAduFraction.Should().BeInRange(0.01, 0.1);
            frame.SensorTemperature.Should().Be(26);
            var header = FitsHeader(frame.FilePath);
            header.Should().Contain("IMAGETYP= 'LIGHT");
            header.Should().Contain("OBJECT  = 'NGC 253");
            header.Should().Contain("BAYERPAT= 'RGGB");
            header.Should().MatchRegex(@"XBINNING=\s+2");
            header.Should().MatchRegex(@"GAIN    =\s+252");
        }

        [Test]
        public async Task CalibrationFrames_GoToTheNightAndTheDarkLibrary_TrialFramesAndSnapshotsAreNotSaved() {
            await using var rig = new EngineRig("camera calibration");
            var camera = rig.Engine.Camera;
            await camera.ConnectAsync();
            camera.SetCooler(true, 0);
            var folders = rig.Engine.CurrentFolders();

            var flat = await camera.ExposeAsync(new ExposureRequest(FrameType.Flat, 0.1, 252, 8, 2));
            var trial = await camera.ExposeAsync(new ExposureRequest(FrameType.Flat, 0.1, 252, 8, 2) { Keep = false });
            var bias = await camera.ExposeAsync(new ExposureRequest(FrameType.Bias, 0.000032, 252, 8, 2));
            var dark = await camera.ExposeAsync(new ExposureRequest(FrameType.Dark, 0.2, 252, 8, 2));
            var snapshot = await camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.1, 252, 8, 2));

            Path.GetDirectoryName(flat.FilePath).Should().Be(folders.FlatsDirectory, "flats without a target are shared by the night");
            Path.GetDirectoryName(bias.FilePath).Should().Be(folders.BiasesDirectory);
            Path.GetDirectoryName(Path.GetDirectoryName(dark.FilePath)).Should().Be(folders.DarksDirectory);
            Path.GetFileName(Path.GetDirectoryName(dark.FilePath)).Should().Be("0.20s_g252_o8_0.00C_2x2", "one library folder per exposure, gain, offset, set point and binning");
            trial.FilePath.Should().BeNull();
            snapshot.FilePath.Should().BeNull("focus snapshots are never saved");
            flat.Stars.Should().Be(0, "calibration frames are not searched for stars");
            Directory.GetFiles(rig.ImagesRoot, "*.fits", SearchOption.AllDirectories).Should().HaveCount(3);
            await camera.DisconnectAsync();
        }

        [Test]
        public async Task FocusFrames_UseMonoBin_OnlyForThemselves() {
            await using var rig = new EngineRig("camera mono-bin");
            await rig.Engine.Camera.ConnectAsync();

            var focus = await rig.Engine.Camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.1, 252, 8, 2) { MonoBin = true });
            var light = await rig.Engine.Camera.ExposeAsync(new ExposureRequest(FrameType.Light, 0.1, 252, 8, 2) { TargetName = "M42" });

            rig.Camera.MonoBinAtExposure.Should().Equal(true, false);
            rig.Camera.ZwoAsiMonoBinMode.Should().BeFalse("lights must keep their Bayer pattern");
            focus.Stars.Should().BeGreaterThan(20);
            FitsHeader(light.FilePath).Should().Contain("BAYERPAT= 'RGGB");
            rig.Engine.Profile.ActiveProfile.CameraSettings.ZwoAsiMonoBinMode.Should().NotBe(true);
        }

        [Test]
        public async Task MonoBin_LeftOnByAUsbDrop_DoesNotComeBackWithTheReconnect() {
            await using var rig = new EngineRig("camera mono-bin usb drop");
            var camera = rig.Engine.Camera;
            await camera.ConnectAsync();

            // USB drops during a mono-bin focus frame: switching mono-bin off afterwards fails like the SDK write would
            var focus = camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 1.0, 252, 8, 2) { MonoBin = true });
            (await EngineRig.Eventually(() => rig.Camera.Exposures.Count == 1, TimeSpan.FromSeconds(5))).Should().BeTrue();
            rig.Camera.ZwoAsiMonoBinMode.Should().BeTrue("the focus frame is a mono-bin frame");
            rig.Camera.Unplug();
            await FluentActions.Awaiting(() => focus).Should().ThrowAsync<DeviceLostException>();
            rig.Engine.Profile.ActiveProfile.CameraSettings.ZwoAsiMonoBinMode.Should().NotBe(true,
                "NINA's ASICamera switches mono-bin on at connect when the profile says so");

            // The banner's Reconnect, then a light at bin 2
            await camera.ConnectAsync();
            camera.State.Should().Be(DeviceConnectionState.Connected);
            rig.Camera.ZwoAsiMonoBinMode.Should().BeFalse("a reconnect must come back in colour");
            var light = await camera.ExposeAsync(new ExposureRequest(FrameType.Light, 0.1, 252, 8, 2) { TargetName = "M42" });
            rig.Camera.MonoBinAtExposure.Last().Should().BeFalse();
            FitsHeader(light.FilePath).Should().Contain("BAYERPAT= 'RGGB");
        }

        [Test]
        public async Task Hfr_FollowsFocus() {
            await using var rig = new EngineRig("camera hfr");
            await rig.Engine.Camera.ConnectAsync();
            var sharp = await rig.Engine.Camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.1, 252, 8, 2));
            rig.Camera.FocusErrorPixels = 6;
            var soft = await rig.Engine.Camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.1, 252, 8, 2));
            TestContext.Out.WriteLine($"HFR in focus {sharp.Hfr:0.00} px ({sharp.Stars} stars), defocused {soft.Hfr:0.00} px ({soft.Stars} stars)");
            soft.Hfr.Should().BeGreaterThan(sharp.Hfr * 1.5);
            sharp.BahtinovOffsetPixels.Should().BeNull("there is no Bahtinov mask in front of a plain star field");
        }

        [Test]
        public async Task FocusFrames_ReportTheBahtinovOffset() {
            await using var rig = new EngineRig("camera bahtinov");
            await rig.Engine.Camera.ConnectAsync();
            var results = new System.Collections.Generic.List<(double Given, double? Measured)>();
            foreach (var offset in new[] { 6.0, -6.0 }) {
                rig.Camera.BahtinovOffsetPixels = offset;
                var frame = await rig.Engine.Camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.1, 252, 8, 2) { MonoBin = true });
                results.Add((offset, frame.BahtinovOffsetPixels));
            }
            TestContext.Out.WriteLine(string.Join(", ", results.Select(r => $"{r.Given:+0;-0} px -> {r.Measured?.ToString("0.00") ?? "none"}")));
            results.Should().OnlyContain(r => r.Measured.HasValue, "the mask pattern is found in the crop around the brightest point");
            results.Should().OnlyContain(r => Math.Sign(r.Measured.Value) == Math.Sign(r.Given), "the sign tells which way to focus");
            results.Should().OnlyContain(r => Math.Abs(r.Measured.Value - r.Given) < 2.5);
        }

        [Test]
        public async Task UsbLoss_TurnsTheCameraLost_AndReconnectRecovers() {
            await using var rig = new EngineRig("camera lost");
            var camera = rig.Engine.Camera;
            await camera.ConnectAsync();
            camera.SetCooler(true, 0);

            rig.Camera.Unplug();
            (await EngineRig.Eventually(() => { camera.Tick(); return camera.State == DeviceConnectionState.Lost; }, TimeSpan.FromSeconds(5)))
                .Should().BeTrue("CameraVM's poll sees the driver disconnected");
            camera.LastError.Should().Contain("stopped answering");
            await FluentActions.Awaiting(() => camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.1, 252, 8, 2))).Should().ThrowAsync<DeviceLostException>();

            await camera.ConnectAsync();
            camera.State.Should().Be(DeviceConnectionState.Connected);
            rig.Camera.Connects.Should().Be(2);
            (await camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.1, 252, 8, 2))).Stars.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task FailedDownload_TurnsTheCameraLost() {
            await using var rig = new EngineRig("camera download");
            await rig.Engine.Camera.ConnectAsync();
            rig.Camera.FailNextDownload = true;
            await FluentActions.Awaiting(() => rig.Engine.Camera.ExposeAsync(new ExposureRequest(FrameType.Light, 0.1, 252, 8, 2))).Should().ThrowAsync<DeviceLostException>();
            rig.Engine.Camera.State.Should().Be(DeviceConnectionState.Lost);
            rig.Engine.Camera.LastError.Should().Contain("Reconnect");
        }

        [Test]
        public async Task FrozenTemperature_WhileThePowerClimbs_RaisesTheHealthWarning() {
            var clock = new ManualClock(DateTimeOffset.Now);
            await using var rig = new EngineRig("camera stuck", clock: clock, stuckSensor: new StuckSensorGuardOptions { Window = TimeSpan.FromSeconds(60), PowerRisePercent = 15 });
            var camera = rig.Engine.Camera;
            await camera.ConnectAsync();
            camera.SetCooler(true, 0);
            clock.Advance(TimeSpan.FromSeconds(5));
            rig.Camera.FrozenTemperature = 20.0;
            rig.Camera.FrozenCoolerPower = 0;
            camera.HealthWarning.Should().BeNull();

            // As in the M1 session: the reading stays at 20.0 °C while the cooler power climbs 0 -> 54 % in 90 s
            for (var step = 0; step <= 9; step++) {
                rig.Camera.FrozenCoolerPower = step * 6;
                await WaitForPoll(rig, step * 6);
                camera.Tick();
                clock.Advance(TimeSpan.FromSeconds(10));
            }
            camera.HealthWarning.Should().NotBeNull();
            camera.HealthWarning.Should().Contain("20.0 °C").And.Contain("reconnect");

            // A sensor that is alive again clears it
            rig.Camera.FrozenTemperature = null;
            await WaitForPoll(rig, 41);
            camera.Tick();
            camera.HealthWarning.Should().BeNull();
        }

        [Test]
        public void StuckSensorGuard_DoesNotFlagAHeldSetPoint() {
            var guard = new StuckSensorGuard(new StuckSensorGuardOptions { Window = TimeSpan.FromSeconds(90), PowerRisePercent = 15 });
            var t0 = DateTimeOffset.Now;
            // At the set point for an hour: reading steady at 0.0 °C, power creeping from 35 to 50 % with the night
            for (var s = 0; s <= 3600; s += 5) {
                guard.Update(t0.AddSeconds(s), 0.0, true, 35 + (15.0 * s / 3600)).Should().BeNull();
            }
            // Cooling normally: the reading moves
            guard.Reset();
            for (var s = 0; s <= 180; s += 5) {
                guard.Update(t0.AddSeconds(s), 20 - (s / 60.0 * 3.6), true, Math.Min(100, s)).Should().BeNull();
            }
            // Frozen while the power climbs fast: flagged once the window is covered, not before
            guard.Reset();
            guard.Update(t0, 20.0, true, 0).Should().BeNull();
            guard.Update(t0.AddSeconds(60), 20.0, true, 36).Should().BeNull("only 60 of 90 s");
            guard.Update(t0.AddSeconds(90), 20.0, true, 54).Should().NotBeNull();
            // Cooler off clears it
            guard.Update(t0.AddSeconds(95), 20.0, false, 0).Should().BeNull();
            guard.Warning.Should().BeNull();
        }

        private static async Task WaitForPoll(EngineRig rig, double power) {
            (await EngineRig.Eventually(() => Math.Abs(rig.Engine.Host.Camera.CameraInfo.CoolerPower - power) < 0.01, TimeSpan.FromSeconds(5)))
                .Should().BeTrue("CameraVM polls the device every 0.5 s");
        }

        /// <summary>The primary header of a FITS file (2880-byte blocks of 80-character cards up to END).</summary>
        internal static string FitsHeader(string path) {
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2880 * 4));
            var end = text.IndexOf("END" + new string(' ', 77), StringComparison.Ordinal);
            return end < 0 ? text : text[..end];
        }
    }
}
