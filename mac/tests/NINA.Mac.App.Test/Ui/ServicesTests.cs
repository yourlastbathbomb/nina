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
using NINA.Mac.App.Astro;
using NINA.Mac.App.Services;
using NINA.Mac.App.Services.Simulation;
using NINA.Mac.Platform;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.Test.Ui {

    [TestFixture]
    public class SessionLayoutTests {

        [TestCase(2026, 10, 10, 23, 0)]
        [TestCase(2026, 10, 11, 2, 30)]
        [TestCase(2026, 10, 11, 11, 59)]
        public void NightCrossingMidnight_StaysInOneFolder(int y, int mo, int d, int h, int mi) {
            var layout = new SessionLayout("/Users/astro/Astro/Nightglass", new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.FromHours(8)), 8);
            layout.NightName.Should().Be("2026-10-10");
        }

        [Test]
        public void NightUsesTheSiteOffset_NotTheMachineZone() {
            // 2026-10-10 17:30 UTC = 2026-10-11 01:30 HKT -> night of the 10th
            new SessionLayout("/a", new DateTimeOffset(2026, 10, 10, 17, 30, 0, TimeSpan.Zero), 8).NightName.Should().Be("2026-10-10");
        }

        [Test]
        public void SirilFolders() {
            var layout = new SessionLayout("/Users/astro/Astro/Nightglass", TestTimes.EveningOct10, 8);
            layout.BiasesDirectory.Should().Be("/Users/astro/Astro/Nightglass/2026-10-10/biases");
            layout.DarksDirectory.Should().Be("/Users/astro/Astro/Nightglass/2026-10-10/darks");
            layout.FlatsDirectory.Should().Be("/Users/astro/Astro/Nightglass/2026-10-10/flats");
            layout.LightsDirectory("NGC 253").Should().Be("/Users/astro/Astro/Nightglass/2026-10-10/lights/NGC_253");
            layout.SnapshotsDirectory.Should().Be("/Users/astro/Astro/Nightglass/2026-10-10/snapshots");
            layout.DirectoryFor(FrameType.Snapshot, "M42").Should().NotContain("lights", "snapshots must never land in lights/ (research MVP-M2)");
            layout.SirilCommand("NGC 253").Should().Be("siril-cli -d \"/Users/astro/Astro/Nightglass/2026-10-10/siril/NGC_253\" -s OSC_Preprocessing.ssf");
        }

        [Test]
        public void FramePaths() {
            var layout = new SessionLayout("/r", TestTimes.EveningOct10, 8);
            layout.FramePath(FrameType.Light, "NGC 253", 10, 252, 2, -0.2, 1).Should().Be("/r/2026-10-10/lights/NGC_253/NGC_253_L_10s_G252_B2_0C_0001.fits");
            layout.FramePath(FrameType.Dark, "NGC 253", 20, 252, 2, 0.4, 12).Should().Be("/r/2026-10-10/darks/D_20s_G252_B2_0C_0012.fits");
            layout.FramePath(FrameType.Bias, null, 0.000032, 252, 2, null, 3).Should().Be("/r/2026-10-10/biases/B_0s_G252_B2_0003.fits");
            layout.FramePath(FrameType.Flat, "x", 0.833, 252, 2, -1.6, 1).Should().Be("/r/2026-10-10/flats/F_0.833s_G252_B2_-2C_0001.fits");
        }

        [TestCase("NGC 253", "NGC_253")]
        [TestCase("  M42 / Orion: core ", "M42_Orion_core")]
        [TestCase("", "untitled")]
        [TestCase(null, "untitled")]
        [TestCase("..", "untitled")]
        [TestCase("Sh2-155", "Sh2-155")]
        public void SafeName(string input, string expected) => SessionLayout.SafeName(input).Should().Be(expected);
    }

    [TestFixture]
    public class SettingsStoreTests {
        private string dir;

        [SetUp]
        public void SetUp() {
            dir = Path.Combine(Path.GetTempPath(), "ninamac-settings-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(dir)) {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void MissingFile_GivesRigDefaults() {
            var store = new JsonSettingsStore(Path.Combine(dir, "settings.json"));
            store.LoadWarning.Should().BeNull();
            var s = store.Current;
            s.Site.LatitudeDegrees.Should().Be(22.25);
            s.Site.LongitudeDegrees.Should().Be(114.18);
            s.Site.UtcOffsetHours.Should().Be(8);
            s.Optics.FocalLengthMm.Should().Be(2500);
            s.Optics.Bin.Should().Be(2);
            s.Optics.PixelScaleArcsec.Should().BeApproximately(0.4785, 0.0005);
            s.MaxAltitudeDegrees.Should().Be(75);
            s.KeepSystemAwake.Should().BeTrue();
        }

        [Test]
        public void SaveAndReload_RoundTrips() {
            var file = Path.Combine(dir, "settings.json");
            var store = new JsonSettingsStore(file);
            var changed = 0;
            store.Changed += (_, _) => changed++;
            var s = store.Current.Clone();
            s.NightVision = true;
            s.Optics.UseReducer = true;
            s.ImagesRoot = "~/AstroData";
            store.Save(s);
            changed.Should().Be(1);
            File.Exists(file + ".tmp").Should().BeFalse();
            var reloaded = new JsonSettingsStore(file).Current;
            reloaded.NightVision.Should().BeTrue();
            reloaded.Optics.UseReducer.Should().BeTrue();
            reloaded.Optics.EffectiveFocalLengthMm.Should().Be(1575);
            reloaded.ImagesRoot.Should().Be("~/AstroData");
            File.ReadAllText(file).Should().Contain("\"nightVision\": true");
        }

        [Test]
        public void CorruptFile_FallsBackToDefaults_AndKeepsTheBadCopy() {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, "{ not json");
            var store = new JsonSettingsStore(file);
            store.LoadWarning.Should().Contain("using defaults");
            store.Current.Site.LatitudeDegrees.Should().Be(22.25);
            File.ReadAllText(file + ".bad").Should().Be("{ not json");
        }

        [TestCase(0, true)]
        [TestCase(330, true)]
        [TestCase(45, true)]
        [TestCase(61, false)]
        [TestCase(180, false)]
        [TestCase(299, false)]
        public void BlockedNorthWrapsThroughZero(double az, bool blocked) => new AppSettings().IsAzimuthBlocked(az).Should().Be(blocked);
    }

    [TestFixture]
    public class SimulationTests {
        private ManualClock clock;
        private AppSettings settings;

        [SetUp]
        public void SetUp() {
            clock = new ManualClock(TestTimes.EveningOct10);
            settings = new AppSettings();
        }

        private SimulatedMount Mount() => new(clock, () => settings, () => new[] { new SerialPortInfo("/dev/cu.usbserial-X", true) }) { ConnectDelay = TimeSpan.Zero };

        [Test]
        public async Task Camera_CoolsAtTheModelledRate_AndHoldsSetpoint() {
            var camera = new SimulatedCamera(clock) { ConnectDelay = TimeSpan.Zero };
            await camera.ConnectAsync();
            camera.Info.Width.Should().Be(3840);
            camera.Info.BayerPattern.Should().Be("RGGB");
            camera.SensorTemperature.Should().Be(26);
            camera.SetCooler(true, 0);
            clock.Advance(TimeSpan.FromMinutes(3));
            camera.SensorTemperature.Should().Be(14);
            camera.CoolerPowerPercent.Should().Be(100);
            clock.Advance(TimeSpan.FromMinutes(10));
            camera.SensorTemperature.Should().Be(0);
            camera.CoolerPowerPercent.Should().Be(Math.Round(26 / 35.0 * 100));
        }

        [Test]
        public async Task Camera_CannotCoolBelowAmbientMinus35() {
            var camera = new SimulatedCamera(clock) { ConnectDelay = TimeSpan.Zero, AmbientCelsius = 30 };
            await camera.ConnectAsync();
            camera.SetCooler(true, -20);
            clock.Advance(TimeSpan.FromHours(1));
            camera.SensorTemperature.Should().Be(-5);
        }

        [Test]
        public async Task Camera_ExposureTakesClockTime() {
            var camera = new SimulatedCamera(clock) { ConnectDelay = TimeSpan.Zero };
            await camera.ConnectAsync();
            var start = clock.Now;
            var frame = await camera.ExposeAsync(new ExposureRequest(FrameType.Light, 10, 252, 8, 2));
            (clock.Now - start).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10));
            frame.Type.Should().Be(FrameType.Light);
            frame.Hfr.Should().BeGreaterThan(1.5);
            frame.Stars.Should().BeGreaterThan(0);
            camera.IsExposing.Should().BeFalse();
        }

        [Test]
        public async Task Camera_UnpluggedMidExposure_ThrowsDeviceLost() {
            var gated = new GatedClock(TestTimes.EveningOct10);
            var camera = new SimulatedCamera(gated) { ConnectDelay = TimeSpan.Zero };
            var connect = camera.ConnectAsync();
            gated.ReleaseOne();
            await connect;
            var exposure = camera.ExposeAsync(new ExposureRequest(FrameType.Light, 10, 252, 8, 2));
            camera.IsExposing.Should().BeTrue();
            gated.ReleaseOne();
            camera.SimulateConnectionLoss();
            camera.State.Should().Be(DeviceConnectionState.Lost);
            await FluentActions.Awaiting(() => exposure).Should().ThrowAsync<DeviceLostException>().WithMessage("*unplug*");
            camera.IsExposing.Should().BeFalse();
            camera.CoolerOn.Should().BeFalse();
        }

        [Test]
        public async Task Mount_SlewGuard_RefusesBelowHorizonAndKeyhole() {
            var mount = Mount();
            await mount.ConnectAsync();
            mount.Altitude.Should().BeApproximately(SimulatedMount.SoftParkAltitude, 1e-6);
            mount.Azimuth.Should().BeApproximately(SimulatedMount.SoftParkAzimuth, 1e-6);
            var m42 = BuiltInTargetCatalog.Targets.Single(t => t.Name == "M42"); // below the horizon at 21:00 HKT
            await FluentActions.Awaiting(() => mount.SlewToAsync(m42.RightAscensionHours, m42.DeclinationDegrees)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*below the horizon*");
            // Put a target exactly at the zenith now
            var (ra, dec) = SkyMath.FromAltAz(clock.Now, 89, 180, new GeoSite(22.25, 114.18));
            await FluentActions.Awaiting(() => mount.SlewToAsync(ra, dec)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*keyhole*");
        }

        [Test]
        public async Task Mount_SlewTracksTheTarget_SoftParkPointsSouthAndStopsTracking() {
            var mount = Mount();
            await mount.ConnectAsync();
            var ngc253 = BuiltInTargetCatalog.Targets.Single(t => t.Name == "NGC 253");
            await mount.SlewToAsync(ngc253.RightAscensionHours, ngc253.DeclinationDegrees);
            mount.RightAscensionHours.Should().BeApproximately(ngc253.RightAscensionHours, 1e-9);
            var alt1 = mount.Altitude.Value;
            clock.Advance(TimeSpan.FromMinutes(30));
            mount.Altitude.Should().BeGreaterThan(alt1, "a rising target climbs while tracking");
            await mount.SoftParkAsync();
            mount.IsTracking.Should().BeFalse();
            mount.Altitude.Should().BeApproximately(30, 1e-9);
            mount.Azimuth.Should().BeApproximately(180, 1e-9);
            clock.Advance(TimeSpan.FromMinutes(30));
            mount.Altitude.Should().BeApproximately(30, 1e-9, "a parked, non-tracking mount stays put in alt/az");
        }

        [Test]
        public async Task Mount_DitherMovesByAFewPixels() {
            var mount = Mount();
            await mount.ConnectAsync();
            var (ra0, dec0) = (mount.RightAscensionHours.Value, mount.DeclinationDegrees.Value);
            await mount.DitherAsync(5);
            var dDec = Math.Abs(mount.DeclinationDegrees.Value - dec0) * 3600;
            dDec.Should().BeLessThanOrEqualTo(5 * settings.Optics.PixelScaleArcsec + 1e-6);
            (mount.RightAscensionHours.Value != ra0 || mount.DeclinationDegrees.Value != dec0).Should().BeTrue();
        }

        [Test]
        public async Task Focuser_FollowsTheMount_AndKeepsAVirtualMillisecondPosition() {
            var mount = Mount();
            var focuser = new SimulatedFocuser(clock, mount);
            focuser.State.Should().Be(DeviceConnectionState.Disconnected);
            await FluentActions.Awaiting(() => focuser.MoveAsync(100)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*mount*");
            await mount.ConnectAsync();
            focuser.State.Should().Be(DeviceConnectionState.Connected);
            focuser.Position.Should().Be(SimulatedFocuser.DefaultMaxStep / 2);
            var error0 = focuser.FocusErrorMicrons;

            await focuser.MoveAsync(-250);
            focuser.Position.Should().Be(29750);
            focuser.FocusErrorMicrons.Should().BeApproximately(error0 - (0.25 * SimulatedFocuser.TravelMicronsPerSecond[2]), 1e-9);

            focuser.SetSpeed(4);
            focuser.Speed.Should().Be(4);
            focuser.Position.Should().Be(30000, "changing speed recentres the virtual position");
            await focuser.MoveAsync(1000);
            focuser.FocusErrorMicrons.Should().BeApproximately(error0 - 6.25 + 400, 1e-9);
            focuser.RecenterVirtualPosition();
            focuser.Position.Should().Be(30000);
            focuser.Invoking(f => f.SetSpeed(5)).Should().Throw<ArgumentOutOfRangeException>();
            await FluentActions.Awaiting(() => focuser.MoveAsync(40000)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        [Test]
        public async Task Hfr_ImprovesTowardsFocus() {
            var mount = Mount();
            var focuser = new SimulatedFocuser(clock, mount, speed: 3);
            var camera = new SimulatedCamera(clock, () => focuser.FocusErrorMicrons) { ConnectDelay = TimeSpan.Zero };
            await mount.ConnectAsync();
            await camera.ConnectAsync();
            var before = (await camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 2, 252, 8, 2))).Hfr;
            await focuser.MoveAsync(-1200); // 120 µm at speed 3 = the simulated starting error
            var after = await camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 2, 252, 8, 2));
            after.Hfr.Should().BeLessThan(before);
            after.Hfr.Should().BeApproximately(1.9, 0.1);
            after.BahtinovOffsetPixels.Should().BeApproximately(0, 0.1);
        }

        private (SimulatedCamera Camera, SimulatedMount Mount, SimulatedSession Session) SessionRig() {
            var mount = Mount();
            var camera = new SimulatedCamera(clock) { ConnectDelay = TimeSpan.Zero };
            var session = new SimulatedSession(camera, mount, clock, () => settings, () => "/Users/astro/Astro/Nightglass");
            return (camera, mount, session);
        }

        private static SessionPlan Plan(CatalogTarget t, int frames, int ditherEvery = 2, double max = 75, bool dawn = true) =>
            new(t.Name, t.RightAscensionHours, t.DeclinationDegrees, 10, frames, 252, 8, 2, ditherEvery, max, 20, dawn);

        [Test]
        public async Task Session_TakesFramesAndDithers() {
            var (camera, mount, session) = SessionRig();
            await camera.ConnectAsync();
            await mount.ConnectAsync();
            var ngc253 = BuiltInTargetCatalog.Targets.Single(t => t.Name == "NGC 253");
            await session.RunAsync(Plan(ngc253, 5));
            session.State.Should().Be(SessionState.Finished);
            session.Progress.FramesDone.Should().Be(5);
            session.Progress.StopReason.Should().Be("All frames taken");
            session.Progress.LastFile.Should().Be("/Users/astro/Astro/Nightglass/2026-10-10/lights/NGC_253/NGC_253_L_10s_G252_B2_26C_0005.fits");
            session.Log.Count(l => l.Contains("Dithered")).Should().Be(2);
        }

        [Test]
        public async Task Session_RefusesWithoutDevices() {
            var (_, _, session) = SessionRig();
            var ngc253 = BuiltInTargetCatalog.Targets.Single(t => t.Name == "NGC 253");
            await FluentActions.Awaiting(() => session.RunAsync(Plan(ngc253, 3))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*camera*");
        }

        [Test]
        public async Task Session_StopsAtTheKeyhole() {
            var (camera, mount, session) = SessionRig();
            await camera.ConnectAsync();
            await mount.ConnectAsync();
            // A target at 70° now, climbing towards the zenith: with a 72° limit it must stop part-way
            var (ra, dec) = SkyMath.FromAltAz(clock.Now, 70, 120, new GeoSite(22.25, 114.18));
            var target = new CatalogTarget("High", "", ra, dec, "test");
            await session.RunAsync(Plan(target, 500, ditherEvery: 0, max: 72));
            session.State.Should().Be(SessionState.Finished);
            session.Progress.StopReason.Should().Contain("keyhole");
            session.Progress.FramesDone.Should().BeInRange(1, 499);
        }

        [Test]
        public async Task Session_StopsAtAstronomicalDawn() {
            clock.Set(new DateTimeOffset(2026, 10, 11, 4, 50, 0, TimeSpan.FromHours(8)));
            var (camera, mount, session) = SessionRig();
            await camera.ConnectAsync();
            await mount.ConnectAsync();
            var (ra, dec) = SkyMath.FromAltAz(clock.Now, 40, 120, new GeoSite(22.25, 114.18));
            await session.RunAsync(Plan(new CatalogTarget("East", "", ra, dec, "test"), 500, ditherEvery: 0));
            session.Progress.StopReason.Should().StartWith("Astronomical twilight");
            var stopLocal = clock.Now.ToOffset(TimeSpan.FromHours(8));
            stopLocal.TimeOfDay.Should().BeGreaterThan(TimeSpan.FromHours(4.75)).And.BeLessThan(TimeSpan.FromHours(5.5));
        }

        [Test]
        public async Task Session_CameraUnpluggedMidRun_Fails() {
            var gated = new GatedClock(TestTimes.EveningOct10);
            var mount = new SimulatedMount(gated, () => settings, () => Array.Empty<SerialPortInfo>()) { ConnectDelay = TimeSpan.Zero };
            var camera = new SimulatedCamera(gated) { ConnectDelay = TimeSpan.Zero };
            var session = new SimulatedSession(camera, mount, gated, () => settings, () => "/tmp/x");
            var c1 = camera.ConnectAsync();
            var c2 = mount.ConnectAsync();
            while (gated.ReleaseOne()) {
            }
            await Task.WhenAll(c1, c2);
            var ngc253 = BuiltInTargetCatalog.Targets.Single(t => t.Name == "NGC 253");
            var run = session.RunAsync(Plan(ngc253, 3));
            gated.ReleaseOne(); // slew
            await WaitFor(() => camera.IsExposing);
            camera.SimulateConnectionLoss();
            await run;
            session.State.Should().Be(SessionState.Failed);
            session.Progress.StopReason.Should().Contain("unplug");
        }

        [Test]
        public async Task Calibration_FlatsAutoExposeToTheTargetMean() {
            var camera = new SimulatedCamera(clock) { ConnectDelay = TimeSpan.Zero };
            await camera.ConnectAsync();
            var calibration = new SimulatedCalibration(camera);
            await calibration.RunFlatsAsync(new CalibrationPlan(5, 0.5, 1, new[] { 10.0 }, 1, 252, 8, 2));
            calibration.LastFlatExposure.Should().BeApproximately(0.5 / 0.6, 0.05);
            calibration.Status.Should().StartWith("Done: 5 flats");
            calibration.Progress.Should().Be(1);
            calibration.IsRunning.Should().BeFalse();
        }

        [Test]
        public async Task Calibration_DarksNeedTheCoolerAtSetpoint() {
            var camera = new SimulatedCamera(clock) { ConnectDelay = TimeSpan.Zero };
            await camera.ConnectAsync();
            var calibration = new SimulatedCalibration(camera);
            var plan = new CalibrationPlan(1, 0.5, 2, new[] { 10.0, 20.0 }, 1, 252, 8, 2);
            await FluentActions.Awaiting(() => calibration.RunDarksAsync(plan)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*setpoint*");
            calibration.Status.Should().StartWith("Failed");
            camera.SetCooler(true, 0);
            clock.Advance(TimeSpan.FromMinutes(10));
            await calibration.RunDarksAsync(plan);
            calibration.Status.Should().Be("Done: 4 darks (10, 20 s)");
            await calibration.RunBiasesAsync(plan);
            calibration.Status.Should().Be("Done: 1 biases");
        }

        [Test]
        public async Task Warmup_RampsTheSetpointThenSwitchesOff() {
            var camera = new SimulatedCamera(clock) { ConnectDelay = TimeSpan.Zero };
            await camera.ConnectAsync();
            camera.SetCooler(true, 0);
            clock.Advance(TimeSpan.FromMinutes(10));
            var start = clock.Now;
            await CameraWarmup.WarmAsync(camera, clock, 3);
            camera.CoolerOn.Should().BeFalse();
            // 0 -> 20 °C at 3 °C/min takes ~6.7 min of ramp
            (clock.Now - start).Should().BeCloseTo(TimeSpan.FromMinutes(20 / 3.0), TimeSpan.FromSeconds(11));
            camera.SensorTemperature.Should().BeApproximately(20, 0.6);
        }

        internal static async Task WaitFor(Func<bool> condition) {
            for (var i = 0; i < 500 && !condition(); i++) {
                await Task.Delay(10);
            }
            condition().Should().BeTrue();
        }
    }

    [TestFixture]
    public class KeepAwakeCoordinatorTests {

        [Test]
        public async Task HeldFromFirstConnectionUntilEverythingIsDisconnected() {
            var keepAwake = new RecordingKeepAwake();
            using var services = AppServices.Create(new AppServicesOptions {
                Clock = new ManualClock(TestTimes.EveningOct10),
                Settings = new MemorySettingsStore(),
                KeepAwake = keepAwake,
                PowerSource = new FakePowerSource(),
                HomeDirectory = "/Users/test",
                SerialPortLister = () => Array.Empty<SerialPortInfo>(),
                FastSimulation = true,
            });
            services.KeepAwake.NightInProgress.Should().BeFalse();
            keepAwake.Calls.Should().BeEmpty();

            await services.Camera.ConnectAsync();
            keepAwake.State.IsEngaged.Should().BeTrue();
            keepAwake.LastOptions.Should().Be(new KeepAwakeOptions(true, true, true));
            keepAwake.State.Reason.Should().Contain("imaging session");
            await services.Mount.ConnectAsync();
            keepAwake.Calls.Count(c => c == "engage").Should().Be(1, "re-engaging with unchanged options is a no-op");

            var s = services.Settings.Current.Clone();
            s.KeepDisplayAwake = false;
            services.Settings.Save(s);
            keepAwake.LastOptions.Should().Be(new KeepAwakeOptions(true, false, true));

            await services.Camera.DisconnectAsync();
            keepAwake.State.IsEngaged.Should().BeTrue("the mount is still connected");
            await services.Mount.DisconnectAsync();
            keepAwake.State.IsEngaged.Should().BeFalse();
            keepAwake.Calls.Last().Should().Be("release");
        }

        [Test]
        public async Task LostDeviceKeepsTheMacAwake() {
            var keepAwake = new RecordingKeepAwake();
            using var services = AppServices.Create(new AppServicesOptions {
                Clock = new ManualClock(TestTimes.EveningOct10), Settings = new MemorySettingsStore(), KeepAwake = keepAwake,
                PowerSource = new FakePowerSource(), HomeDirectory = "/Users/test", SerialPortLister = () => Array.Empty<SerialPortInfo>(), FastSimulation = true,
            });
            await services.Camera.ConnectAsync();
            services.SimCamera.SimulateConnectionLoss();
            keepAwake.State.IsEngaged.Should().BeTrue("a lost device awaits reconnect; sleeping now would end the night");
        }

        [Test]
        public void PowerMonitor_ReportsChanges() {
            var source = new FakePowerSource();
            var monitor = new PowerMonitor(source);
            var changes = 0;
            monitor.Changed += (_, _) => changes++;
            monitor.Refresh();
            monitor.Current.BatteryPercent.Should().Be(76);
            monitor.Refresh();
            changes.Should().Be(1, "an identical reading is not a change");
            source.Next = source.Next with { BatteryPercent = 15 };
            monitor.Refresh();
            changes.Should().Be(2);
            monitor.Current.Summary.Should().Be("Battery 15% (on battery, 2:00 left)");
        }
    }
}
