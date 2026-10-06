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
using NINA.Astrometry;
using NINA.Mac.App.Engine.Test.Fakes;
using NINA.Mac.App.Services;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine.Test {

    /// <summary>
    /// Robustness at the scope (wave-7 follow-ups) on the Real services: the camera is not marked Lost while NINA reconnects it
    /// during a run, a run waits for a long calibration frame, the status bar can see a goto from its command, the local
    /// horizon reaches the slew guard and NINA's profile, and the plan check reports tonight's window.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class FirstLightEngineTests {

        private static SessionPlan Plan(Coordinates target, int frames, double seconds = 0.5) =>
            new("NGC 253 test", target.RA, target.Dec, seconds, frames, 252, 15, 2, 0, 75, 10, true);

        /// <summary>Starts a long run, waits for its first frame and pauses it: the run stays active, the camera idle.</summary>
        private static async Task<Task> StartAndPause(EngineRig rig) {
            rig.Settings.CentreBeforeRun = false;
            var session = rig.Engine.Session;
            var run = session.RunAsync(Plan(Sky.At(45, 140), 50));
            (await EngineRig.Eventually(() => session.Progress.FramesDone >= 1, TimeSpan.FromSeconds(30))).Should().BeTrue();
            session.Pause();
            (await EngineRig.Eventually(() => !rig.Engine.Host.Camera.CameraInfo.IsExposing && session.State == SessionState.Paused, TimeSpan.FromSeconds(10))).Should().BeTrue();
            await Task.Delay(300);
            return run;
        }

        [Test]
        public async Task Camera_ReconnectedByNinaDuringARun_IsNotMarkedLost() {
            await using var rig = new EngineRig("camera reconnect grace");
            await rig.ConnectAll();
            var camera = rig.Engine.Camera;
            var states = new ConcurrentQueue<DeviceConnectionState>();
            camera.Changed += (_, _) => states.Enqueue(camera.State);
            var run = await StartAndPause(rig);

            // What NINA's ReconnectOnDownloadFailure does between two lights: CameraVM disconnects, rescans and reconnects
            await rig.Engine.Host.Camera.Disconnect();
            rig.Engine.Host.Camera.CameraInfo.Connected.Should().BeFalse();
            camera.Tick();
            camera.State.Should().Be(DeviceConnectionState.Connected, "the app's 1 Hz tick must not raise the banner during NINA's own reconnect");
            camera.ReconnectNote.Should().Contain("reconnecting");
            (await rig.Engine.Host.Camera.Connect()).Should().BeTrue();
            (await EngineRig.Eventually(() => { camera.Tick(); return camera.ReconnectNote == null; }, TimeSpan.FromSeconds(5))).Should().BeTrue();
            camera.State.Should().Be(DeviceConnectionState.Connected);
            states.Should().NotContain(DeviceConnectionState.Lost);

            rig.Engine.Session.RequestStop("test");
            await run;
        }

        [Test]
        public async Task Camera_NotBackWithinTheGrace_IsLost_AndRecoversWhenNinaReconnectsIt() {
            await using var rig = new EngineRig("camera reconnect late", configureOptions: o => o.CameraReconnectGrace = TimeSpan.FromMilliseconds(300));
            await rig.ConnectAll();
            var camera = rig.Engine.Camera;
            var run = await StartAndPause(rig);

            await rig.Engine.Host.Camera.Disconnect();
            camera.Tick();
            camera.State.Should().Be(DeviceConnectionState.Connected);
            await Task.Delay(400);
            camera.Tick();
            camera.State.Should().Be(DeviceConnectionState.Lost, "past the grace window the banner must show");
            camera.LastError.Should().Contain("stopped answering");

            (await rig.Engine.Host.Camera.Connect()).Should().BeTrue();
            (await EngineRig.Eventually(() => { camera.Tick(); return camera.State == DeviceConnectionState.Connected; }, TimeSpan.FromSeconds(5)))
                .Should().BeTrue("a camera NINA reconnected late comes back without the operator");
            camera.LastError.Should().BeNull();

            rig.Engine.Session.RequestStop("test");
            await run;
        }

        [Test]
        public async Task KeyholeLimit_FromTheAppSettings_ReachesTheDriversSlewGuard() {
            await using var rig = new EngineRig("keyhole limit", s => s.MaxAltitudeDegrees = 70);
            rig.Engine.Lx200.Telescope.Settings.MaxAltitudeDegrees.Should().Be(70, "the driver refuses gotos above the same limit as the app");
            rig.Settings.MaxAltitudeDegrees = 80;
            rig.Engine.ApplySettings().Should().BeTrue();
            rig.Engine.Lx200.Telescope.Settings.MaxAltitudeDegrees.Should().Be(80, "a saved change reaches the driver without a restart");
        }

        [Test]
        public async Task Camera_DroppingWhileIdle_IsLostAtOnce() {
            await using var rig = new EngineRig("camera idle drop");
            await rig.ConnectAll();
            await rig.Engine.Host.Camera.Disconnect();
            rig.Engine.Camera.Tick();
            rig.Engine.Camera.State.Should().Be(DeviceConnectionState.Lost, "no run is reconnecting it: the operator must know now");
        }

        [Test]
        public async Task Run_StartedDuringALongDark_WaitsForIt_InsteadOfFailingAfter15s() {
            await using var rig = new EngineRig("run waits for a dark");
            await rig.ConnectAll();
            rig.Settings.CentreBeforeRun = false;
            var camera = rig.Engine.Camera;
            var dark = camera.ExposeAsync(new ExposureRequest(FrameType.Dark, 16.5, 252, 15, 2));
            (await EngineRig.Eventually(() => camera.IsExposing, TimeSpan.FromSeconds(5))).Should().BeTrue();

            await rig.Engine.Session.RunAsync(Plan(Sky.At(45, 140), 1));

            (await dark).FilePath.Should().NotBeNull("the dark was kept");
            rig.Engine.Session.State.Should().Be(SessionState.Finished, string.Join(Environment.NewLine, rig.Engine.Session.Log));
            rig.Engine.Session.Progress.FramesDone.Should().Be(1);
            rig.Engine.Session.Log.Should().Contain(l => l.Contains("Waiting for the camera's current frame"));
        }

        [Test]
        public async Task Goto_IsVisibleFromItsCommand_BeforeThePollSeesIt() {
            await using var rig = new EngineRig("mount motion command");
            var mount = rig.Engine.Mount;
            await mount.ConnectAsync();
            mount.IsMotionCommandActive.Should().BeFalse();
            var target = Sky.At(45, 150);
            var slew = mount.SlewToAsync(target.RA, target.Dec);
            mount.IsMotionCommandActive.Should().BeTrue("the status bar's Stop shows from the command, before the 1 s poll reports slewing");
            await slew;
            mount.IsMotionCommandActive.Should().BeFalse();

            var centring = rig.Engine.Centring.SlewAndCentreAsync(target.RA, target.Dec);
            mount.IsMotionCommandActive.Should().BeTrue();
            await centring;
            mount.IsMotionCommandActive.Should().BeFalse();
        }

        [Test]
        public async Task Horizon_GuardsGotos_AndReachesNinasProfile() {
            HorizonProfile horizon = HorizonProfile.SiteEstimate();
            await using var rig = new EngineRig("horizon", configureOptions: o => o.Horizon = () => horizon);
            var mount = rig.Engine.Mount;
            await mount.ConnectAsync();
            var low = Sky.At(12, 180);
            await FluentActions.Awaiting(() => mount.SlewToAsync(low.RA, low.Dec)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*local horizon*");
            rig.Received.Should().NotContain(c => c.StartsWith(":MS", StringComparison.Ordinal), "nothing is sent for a refused goto");

            var a = rig.Engine.Profile.ActiveProfile.AstrometrySettings;
            a.HorizonFilePath.Should().Be(EngineDevices.HorizonFilePath);
            a.Horizon.Should().NotBeNull("NINA's CustomHorizon is loaded for the sequence's horizon waits and conditions");
            a.Horizon.GetAltitude(180).Should().BeApproximately(15, 0.01);
            a.Horizon.GetAltitude(0).Should().BeApproximately(80, 0.01);
            File.ReadAllLines(a.HorizonFilePath).Where(l => !l.StartsWith('#')).Should().OnlyContain(l => !l.Contains('#'), "upstream's parser drops inline comments");

            // A horizon saved on the Target screen reaches the profile at once when no run is active
            horizon = new HorizonProfile(new[] { new HorizonPoint(0, 60), new HorizonPoint(180, 8) });
            rig.Engine.ApplyHorizon().Should().BeTrue();
            a.Horizon.GetAltitude(180).Should().BeApproximately(8, 0.01);
            await mount.SlewToAsync(low.RA, low.Dec);
        }

        [Test]
        public async Task PlanCheck_ReportsTonightsWindow() {
            await using var rig = new EngineRig("plan check", dawn: DateTime.Now.AddHours(3));
            var check = rig.Engine.Session.CheckPlan(Plan(Sky.At(45, 140), 20, 10));
            check.HasErrors.Should().BeFalse(string.Join("; ", check.Errors));
            check.Summary.Should().Contain("Tonight").And.Contain("expected to image");

            var never = rig.Engine.Session.CheckPlan(new SessionPlan("Far south", 6, -85, 10, 20, 252, 15, 2, 0, 75, 20, true));
            (never.Warnings.Concat(never.Errors)).Should().NotBeEmpty("a target that never rises here is flagged before Start");
        }
    }
}
