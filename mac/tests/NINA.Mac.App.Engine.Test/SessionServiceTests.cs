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
using NINA.Mac.App.Engine.Test.Fakes;
using NINA.Mac.App.Services;
using NINA.Mac.Sequencing.Planning;
using NINA.Mac.Sequencing.Runner;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.Trigger.Guider;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine.Test {

    /// <summary>
    /// Runs through NINA's own sequencer: the Target form's plan becomes NINA.Mac.Sequencing's NightPlan and sequence tree, NINA's
    /// Sequencer runs it against the fake camera, the LX200 driver on the Autostar simulator and the fake solver. Centring,
    /// lights, dithers, frames in the Siril layout, HFR on the Run screen, pause between frames and stop.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class SessionServiceTests {

        private static SessionPlan Plan(double ra, double dec, int frames = 3, double seconds = 1, int ditherEvery = 2, string name = "NGC 253 test") =>
            new(name, ra, dec, seconds, frames, 252, 15, 2, ditherEvery, 75, 10, true);

        [Test]
        public void TargetForm_BecomesTheRigsNightPlan() {
            var rig = new EngineRig("session plan");
            try {
                var night = rig.Engine.Session.ToNightPlan(new SessionPlan("M31:Core", 0.712, 41.27, 30, 40, 252, 15, 2, 5, 75, 20, true));
                night.CoolToC.Should().BeNull("the Cool screen owns the cooler");
                night.WarmAtEnd.Should().BeFalse("Teardown warms the camera");
                night.ParkAtEnd.Should().BeFalse("Teardown parks");
                night.Dawn.Should().Be(DawnStop.Astronomical);
                var t = night.Targets.Single();
                t.Name.Should().Be("M31:Core", "NINA itself makes M31_Core of it, as the Siril layout does");
                t.ExposureSeconds.Should().Be(30);
                t.Count.Should().Be(40);
                t.Gain.Should().Be(252);
                t.Offset.Should().Be(15);
                t.Binning.Should().Be(2);
                t.DitherEvery.Should().Be(5);
                t.MaxAltitudeDeg.Should().Be(75);
                t.MinAltitudeDeg.Should().Be(20);
                t.CenterFirst.Should().BeTrue("the fake solver factory makes centring available");
                t.RecenterArcmin.Should().Be(0, "the rig's settings switch drift checks off");
                t.Coordinates.RA.Should().BeApproximately(0.712, 1e-9);
                rig.Engine.Session.ToNightPlan(new SessionPlan("x", 1, 1, 10, 1, 252, 8, 2, 0, 75, 20, false)).Dawn.Should().Be(DawnStop.Civil);
            } finally {
                rig.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        [Test]
        public async Task Run_CentresExposesDithers_AndSavesIntoTheSirilLayout() {
            await using var rig = new EngineRig("session run");
            await rig.ConnectAll();
            var target = Sky.At(45, 140);
            var session = rig.Engine.Session;
            var changes = 0;
            session.Changed += (_, _) => changes++;

            await session.RunAsync(Plan(target.RA, target.Dec, frames: 3, ditherEvery: 2));

            TestContext.Out.WriteLine(string.Join(Environment.NewLine, session.Log));
            session.State.Should().Be(SessionState.Finished);
            session.Progress.StopReason.Should().Be("All frames taken");
            session.Progress.FramesDone.Should().Be(3);
            session.Progress.LastFile.Should().StartWith(session.Layout.LightsDirectory("NGC 253 test"));
            (await EngineRig.Eventually(() => session.Progress.LastHfr.HasValue, TimeSpan.FromSeconds(10))).Should().BeTrue("lights are measured off the UI thread");
            session.Progress.LastHfr.Should().BeInRange(0.5, 5);
            var lights = Directory.GetFiles(session.Layout.LightsDirectory("NGC 253 test"), "*.fits");
            lights.Should().HaveCount(3);
            CameraServiceTests.FitsHeader(lights[0]).Should().Contain("OBJECT  = 'NGC 253 test");

            // NINA's Center: solve, sync (:CM), re-slew; the fake solver saw the frames
            rig.Solver.Calls.Should().BeGreaterThanOrEqualTo(2);
            rig.Received.Count(c => c.StartsWith(":CM", StringComparison.Ordinal)).Should().Be(1);
            (rig.Truth() - target).Distance.ArcMinutes.Should().BeLessThan(1.5, "after the sync and re-slew the mount points at the target (one dither later)");
            // DirectGuider dithered after the second light
            rig.Received.Should().Contain(c => c.StartsWith(":Mg", StringComparison.Ordinal) || c.StartsWith(":Mn", StringComparison.Ordinal) || c.StartsWith(":Ms", StringComparison.Ordinal)
                || c.StartsWith(":Me", StringComparison.Ordinal) || c.StartsWith(":Mw", StringComparison.Ordinal));
            rig.Camera.Exposures.Count(e => e.ImageType == "LIGHT").Should().Be(3);
            rig.Camera.Exposures.Where(e => e.ImageType == "LIGHT").Should().OnlyContain(e => e.Gain == 252 && e.Offset == 15 && e.Binning.X == 2 && e.ExposureTime == 1);
            changes.Should().BeGreaterThan(3);

            // The generated tree is NINA's: centre, lights, dither trigger, the keyhole condition; nothing parks or cools
            var entities = new List<object>();
            HeadlessSequenceRunner.Walk(session.LastSequence, e => entities.Add(e));
            entities.OfType<Center>().Should().HaveCount(1);
            entities.OfType<TakeExposure>().Should().HaveCount(1);
            entities.OfType<DitherAfterExposures>().Should().HaveCount(1);
            entities.OfType<NINA.Mac.Sequencing.Conditions.MaxAltitudeCondition>().Should().NotBeEmpty();
            entities.OfType<ParkScope>().Should().BeEmpty();
            entities.OfType<NINA.Sequencer.SequenceItem.Camera.CoolCamera>().Should().BeEmpty();
            entities.OfType<LoopCondition>().Should().NotBeEmpty();
        }

        [Test]
        public async Task Pause_HoldsBetweenFrames_ResumeContinues_StopEndsTheRun() {
            await using var rig = new EngineRig("session pause");
            await rig.ConnectAll();
            rig.Settings.CentreBeforeRun = false;
            var target = Sky.At(45, 140);
            var session = rig.Engine.Session;
            var run = session.RunAsync(Plan(target.RA, target.Dec, frames: 20, seconds: 0.5, ditherEvery: 0));

            (await EngineRig.Eventually(() => session.Progress.FramesDone >= 1, TimeSpan.FromSeconds(30))).Should().BeTrue();
            session.Pause();
            session.State.Should().Be(SessionState.Paused);
            // The light already started finishes; then the loop holds at the gate
            await Task.Delay(1500);
            var held = rig.Camera.Exposures.Count(e => e.ImageType == "LIGHT");
            await Task.Delay(2000);
            rig.Camera.Exposures.Count(e => e.ImageType == "LIGHT").Should().Be(held, "no new light starts while paused");

            session.Resume();
            session.State.Should().Be(SessionState.Running);
            (await EngineRig.Eventually(() => rig.Camera.Exposures.Count(e => e.ImageType == "LIGHT") > held, TimeSpan.FromSeconds(10))).Should().BeTrue("resume lets the loop go on");

            session.RequestStop("Stopped by user");
            await run;
            session.State.Should().Be(SessionState.Finished);
            session.Progress.StopReason.Should().Be("Stopped by user");
            session.Progress.FramesDone.Should().BeLessThan(20);
            session.Log.Should().Contain(l => l.Contains("Paused")).And.Contain(l => l.Contains("Resumed"));
            rig.Received.Should().NotContain(c => c.StartsWith(":CM", StringComparison.Ordinal), "centring was off");
        }

        [Test]
        public async Task Run_NeedsTheDevices_AndRefusesASecondRun() {
            await using var rig = new EngineRig("session preconditions");
            var target = Sky.At(45, 140);
            await FluentActions.Awaiting(() => rig.Engine.Session.RunAsync(Plan(target.RA, target.Dec))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*camera*");
            await rig.Engine.Camera.ConnectAsync();
            await FluentActions.Awaiting(() => rig.Engine.Session.RunAsync(Plan(target.RA, target.Dec))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*mount*");
            await rig.Engine.Mount.ConnectAsync();
            rig.Settings.CentreBeforeRun = false;
            var run = rig.Engine.Session.RunAsync(Plan(target.RA, target.Dec, frames: 5, seconds: 0.5, ditherEvery: 0));
            (await EngineRig.Eventually(() => rig.Engine.Session.State == SessionState.Running, TimeSpan.FromSeconds(5))).Should().BeTrue();
            await FluentActions.Awaiting(() => rig.Engine.Session.RunAsync(Plan(target.RA, target.Dec))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*already running*");
            rig.Engine.Session.RequestStop("test");
            await run;
        }

        [Test]
        public async Task Run_EndsAtTheKeyhole_WithTheReason() {
            await using var rig = new EngineRig("session keyhole");
            await rig.ConnectAll();
            rig.Settings.CentreBeforeRun = false;
            // Above the 75° limit already: NINA's MaxAltitudeCondition (Skip policy) skips the target
            var target = Sky.At(80, 140);
            await rig.Engine.Session.RunAsync(Plan(target.RA, target.Dec, frames: 3, seconds: 0.5, ditherEvery: 0));
            TestContext.Out.WriteLine(string.Join(Environment.NewLine, rig.Engine.Session.Log));
            rig.Engine.Session.State.Should().Be(SessionState.Finished);
            rig.Engine.Session.Progress.FramesDone.Should().Be(0);
            rig.Engine.Session.Progress.StopReason.Should().Contain("keyhole");
            rig.Camera.Exposures.Count(e => e.ImageType == "LIGHT").Should().Be(0);
        }

        [Test]
        public async Task Run_StartedDuringAFocusLoop_WaitsForTheFrame_AndTakesColourLights() {
            await using var rig = new EngineRig("session focus loop");
            await rig.ConnectAll();
            rig.Settings.CentreBeforeRun = false;
            var camera = rig.Engine.Camera;
            var refused = new ConcurrentQueue<string>();
            using var stopLoop = new CancellationTokenSource();
            // The Focus screen's loop, left running: mono-bin focus frames back to back
            var focusRequest = new ExposureRequest(FrameType.Snapshot, 0.3, 252, 8, 2) { MonoBin = true };
            var loop = Task.Run(async () => {
                while (!stopLoop.IsCancellationRequested) {
                    try {
                        await camera.ExposeAsync(focusRequest, stopLoop.Token);
                    } catch (OperationCanceledException) {
                        break;
                    } catch (InvalidOperationException ex) {
                        refused.Enqueue(ex.Message);
                        await Task.Delay(50);
                    }
                }
            });
            (await EngineRig.Eventually(() => rig.Camera.Exposures.Count >= 2, TimeSpan.FromSeconds(10))).Should().BeTrue();

            var target = Sky.At(45, 140);
            await rig.Engine.Session.RunAsync(Plan(target.RA, target.Dec, frames: 3, seconds: 0.5, ditherEvery: 0));
            stopLoop.Cancel();
            await loop;

            TestContext.Out.WriteLine(string.Join(Environment.NewLine, rig.Engine.Session.Log));
            rig.Engine.Session.Progress.StopReason.Should().Be("All frames taken");
            var started = rig.Camera.Exposures.Zip(rig.Camera.MonoBinAtExposure, (e, monoBin) => (e.ImageType, MonoBin: monoBin)).ToList();
            TestContext.Out.WriteLine(string.Join(Environment.NewLine, started.Select((x, i) => $"{i}: {x.ImageType} monoBin={x.MonoBin}")));
            started.Where(x => x.ImageType == "LIGHT").Should().HaveCount(3).And.OnlyContain(x => !x.MonoBin, "lights must keep their Bayer pattern");
            var first = started.FindIndex(x => x.ImageType == "LIGHT");
            var last = started.FindLastIndex(x => x.ImageType == "LIGHT");
            started.Skip(first).Take(last - first + 1).Should().OnlyContain(x => x.ImageType == "LIGHT", "no focus frame slips in between the run's lights");
            refused.Should().Contain(m => m.Contains("run"), "the camera refuses its own frames while a run has it");
            var lights = Directory.GetFiles(rig.Engine.Session.Layout.LightsDirectory("NGC 253 test"), "*.fits");
            lights.Should().HaveCount(3);
            lights.Should().OnlyContain(f => CameraServiceTests.FitsHeader(f).Contains("BAYERPAT= 'RGGB"));
            rig.Engine.Profile.ActiveProfile.CameraSettings.ZwoAsiMonoBinMode.Should().NotBe(true);
        }

        [Test]
        public async Task Run_AfterAFailedDownload_RetriesTheFrameOnce_AndTakesThemAll() {
            await using var rig = new EngineRig("session download retry");
            await rig.ConnectAll();
            rig.Settings.CentreBeforeRun = false;
            var target = Sky.At(45, 140);
            var session = rig.Engine.Session;
            var run = session.RunAsync(Plan(target.RA, target.Dec, frames: 4, seconds: 0.5, ditherEvery: 0));
            (await EngineRig.Eventually(() => session.Progress.FramesDone >= 1, TimeSpan.FromSeconds(30))).Should().BeTrue();
            rig.Camera.FailNextDownload = true;
            await run;

            TestContext.Out.WriteLine(string.Join(Environment.NewLine, session.Log));
            session.State.Should().Be(SessionState.Finished);
            session.Progress.FramesDone.Should().Be(4, "NINA's trigger reconnects the camera and the failed frame is taken again");
            session.Progress.StopReason.Should().Be("All frames taken");
            rig.Camera.Connects.Should().BeGreaterThanOrEqualTo(2, "ReconnectOnDownloadFailure reconnected the camera");
            session.Log.Should().Contain(l => l.Contains("download failed", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public async Task Run_WhenARetriedDownloadFailsAgain_EndsShort_AndSaysWhy() {
            await using var rig = new EngineRig("session download fails twice");
            await rig.ConnectAll();
            rig.Settings.CentreBeforeRun = false;
            var target = Sky.At(45, 140);
            var session = rig.Engine.Session;
            var run = session.RunAsync(Plan(target.RA, target.Dec, frames: 4, seconds: 0.5, ditherEvery: 0));
            (await EngineRig.Eventually(() => session.Progress.FramesDone >= 1, TimeSpan.FromSeconds(30))).Should().BeTrue();
            rig.Camera.FailNextDownloads = 2;
            await run;

            TestContext.Out.WriteLine(string.Join(Environment.NewLine, session.Log));
            session.Progress.FramesDone.Should().Be(3, "one retry per failed frame: the frame whose retry failed too is not taken again");
            session.Progress.StopReason.Should().Contain("3 of 4").And.Contain("download");
        }

        [Test]
        public async Task Settings_SavedWhileIdle_ReachTheEngineProfile_AndTheNextRun() {
            await using var rig = new EngineRig("session settings");
            await rig.ConnectAll();
            rig.Settings.CentreBeforeRun = false;
            var p = rig.Engine.Profile.ActiveProfile;
            p.TelescopeSettings.FocalLength.Should().Be(2500);

            // Settings › Save: the reducer, the default gain, the site's elevation and a new images root
            rig.Settings.Optics.UseReducer = true;
            rig.Settings.Gain = 100;
            rig.Settings.Site.ElevationMeters = 300;
            var newRoot = Path.Combine(rig.Folder, "new images");
            rig.Settings.ImagesRoot = newRoot;
            rig.Engine.ApplySettings().Should().BeTrue("nothing is running");

            p.TelescopeSettings.FocalLength.Should().Be(1575, "CenteringSolver hands the focal length to ASTAP as its scale hint");
            p.CameraSettings.Gain.Should().Be(100);
            p.AstrometrySettings.Elevation.Should().Be(300);
            p.ImageFileSettings.FilePath.Should().Be(newRoot);

            var target = Sky.At(45, 140);
            await rig.Engine.Session.RunAsync(Plan(target.RA, target.Dec, frames: 1, seconds: 0.5, ditherEvery: 0));
            rig.Engine.Session.Progress.StopReason.Should().Be("All frames taken");
            rig.Engine.Session.Progress.LastFile.Should().StartWith(newRoot);
        }

        [Test]
        public async Task Settings_SavedDuringARun_ApplyWhenTheRunEnds() {
            await using var rig = new EngineRig("session settings deferred");
            await rig.ConnectAll();
            rig.Settings.CentreBeforeRun = false;
            var p = rig.Engine.Profile.ActiveProfile;
            var target = Sky.At(45, 140);
            var session = rig.Engine.Session;
            var run = session.RunAsync(Plan(target.RA, target.Dec, frames: 3, seconds: 0.5, ditherEvery: 0));
            (await EngineRig.Eventually(() => session.State == SessionState.Running, TimeSpan.FromSeconds(5))).Should().BeTrue();

            rig.Settings.Gain = 120;
            rig.Settings.ImagesRoot = Path.Combine(rig.Folder, "later");
            rig.Engine.ApplySettings().Should().BeFalse("the run keeps the settings it started with");
            rig.Engine.SettingsPending.Should().BeTrue();
            p.CameraSettings.Gain.Should().Be(252);

            await run;
            session.Progress.StopReason.Should().Be("All frames taken");
            session.Progress.LastFile.Should().StartWith(rig.ImagesRoot, "the frames of the run stay together");
            rig.Engine.SettingsPending.Should().BeFalse();
            p.CameraSettings.Gain.Should().Be(120);
            p.ImageFileSettings.FilePath.Should().Be(Path.Combine(rig.Folder, "later"));
        }

        [Test]
        public async Task Settings_Reapplied_KeepTheFocusScreensSpeed_UnlessTheDefaultChanged() {
            await using var rig = new EngineRig("session settings focuser");
            await rig.Engine.Mount.ConnectAsync();
            var focuser = rig.Engine.Focuser;
            focuser.SetSpeed(3);
            await focuser.MoveAsync(400);

            rig.Engine.ApplySettings().Should().BeTrue();
            focuser.Speed.Should().Be(3, "the Focus screen's speed is not the Settings default");
            focuser.Position.Should().Be(32900);

            rig.Settings.FocuserSpeed = 4;
            rig.Engine.ApplySettings().Should().BeTrue();
            focuser.Speed.Should().Be(4);
            focuser.Position.Should().Be(32500, "positions only compare at one speed");
        }
    }
}
