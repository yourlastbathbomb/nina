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
using NINA.Core.Enum;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using NINA.Mac.Lx200;
using NINA.Mac.Lx200.Sim;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200.Test {

    /// <summary>
    /// <see cref="Lx200Telescope"/> against the simulator: connect sequence, gotos with NINA's J2000 to JNow transform, sync,
    /// abort, soft park, the alt-az specifics (no meridian flip, pier side unknown), the 1' site tolerance, the
    /// never-after-alignment clock rule, tracking and manual moves.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Lx200TelescopeTest {

        private static Coordinates JNow(double raHours, double decDeg) => new Coordinates(Angle.ByHours(raHours), Angle.ByDegree(decDeg), Epoch.JNOW);

        /// <summary>A target near where the simulated mount points (it starts at alt 45, az 135), as J2000 for NINA's slew API.</summary>
        private static (Coordinates J2000, Coordinates JNow) NearbyTarget(AutostarSimulator sim, double dRaHours, double dDecDeg) {
            var (ra, dec) = sim.BelievedRaDec;
            var jnow = JNow(Lx200Astro.Wrap(ra + dRaHours, 24), dec + dDecDeg);
            return (jnow.Transform(Epoch.J2000), jnow);
        }

        [Test]
        public async Task Connect_ReadsTheMount_SwitchesToHighPrecision_AndLeavesHighPrecisionPointingOff() {
            using var rig = new Rig(new SimOptions { HighPrecisionPointing = true, SlewSeconds = 0.8, PlanetaryUpdateSeconds = 0.2 },
                                    configure: s => s.PulseStrategy = Lx200PulseStrategy.Auto);
            rig.Sim.LongFormat.Should().BeFalse("the Autostar starts in the short format");
            var mount = await rig.ConnectTelescope();

            mount.Connected.Should().BeTrue();
            mount.Firmware.Should().Be("LX2001 4.2g");
            mount.IsAligned.Should().BeTrue("the simulator reports ':GW#' AT2");
            mount.EffectivePulseStrategy.Should().Be(Lx200PulseStrategy.HostTimedMove, "Auto on stock 4.2g firmware");
            rig.Sim.LongFormat.Should().BeTrue("':U#' switched the format to HH:MM:SS / sDD*MM'SS");
            rig.Sim.HighPrecisionPointing.Should().BeFalse("':P#' was toggled until it read LOW");
            rig.Sim.GuideRateArcsecPerSec.Should().Be(10.0, "':Rg10.0#' at connect");
            var (simRa, simDec) = rig.Sim.BelievedRaDec;
            mount.RightAscension.Should().BeApproximately(simRa, 1.0 / 3600);
            mount.Declination.Should().BeApproximately(simDec, 1.0 / 3600);
            mount.Altitude.Should().BeApproximately(rig.Sim.Axes.Alt, 2.0 / 3600);
            mount.Azimuth.Should().BeApproximately(rig.Sim.Axes.Az, 2.0 / 3600);
            mount.EquatorialSystem.Should().Be(Epoch.JNOW);
            mount.AlignmentMode.Should().Be(AlignmentMode.AltAz);
            mount.TrackingEnabled.Should().BeTrue();
            mount.CanPulseGuide.Should().BeTrue();
            mount.GuideRateRightAscensionArcsecPerSec.Should().Be(10.0);
            Math.Abs(Lx200Astro.HourDifference(mount.SiderealTime, Lx200Astro.LstHours(DateTime.UtcNow, 114.18333)) * 3600).Should().BeLessThan(2);
        }

        [Test]
        public async Task Auto_UsesTheNativePulse_OnStarPatchFirmware() {
            using var rig = new Rig(new SimOptions { Firmware = "4.2G" }, configure: s => s.PulseStrategy = Lx200PulseStrategy.Auto);
            var mount = await rig.ConnectTelescope();
            mount.EffectivePulseStrategy.Should().Be(Lx200PulseStrategy.NativePulse);
        }

        [Test]
        public async Task PropertyReads_NeverTouchTheSerialLine() {
            using var rig = new Rig(configure: s => s.PollIntervalMs = 10000);
            var mount = await rig.ConnectTelescope();
            await Task.Delay(300);   // the first poll cycle runs right after connect
            var before = rig.Received.Count;
            for (var i = 0; i < 50; i++) {
                _ = (mount.Coordinates, mount.RightAscensionString, mount.DeclinationString, mount.Altitude, mount.Azimuth, mount.SiderealTime,
                     mount.HoursToMeridian, mount.TimeToMeridianFlip, mount.SideOfPier, mount.TrackingEnabled, mount.TrackingRate, mount.Slewing,
                     mount.IsPulseGuiding, mount.SiteLatitude, mount.SiteLongitude, mount.SiteElevation, mount.UTCDate, mount.AtPark,
                     mount.GuideRateDeclinationArcsecPerSec, mount.AlignmentMode, mount.TargetCoordinates, mount.CanSetTrackingEnabled);
            }
            rig.Received.Count.Should().Be(before, "NINA polls about 27 properties every 2 s; they come from the cache");
        }

        [Test]
        public async Task Goto_J2000Target_IsSentAsJNow_FromNinasTransform_AndArrives() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            var (j2000, jnow) = NearbyTarget(rig.Sim, 0.25, 3.0);
            Lx200Astro.SeparationDeg(j2000.RA, j2000.Dec, jnow.RA, jnow.Dec).Should().BeGreaterThan(0.2, "26 years of precession separate J2000 from JNow");

            var sw = Stopwatch.StartNew();
            (await mount.SlewToCoordinates(j2000, CancellationToken.None)).Should().BeTrue();
            sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(0.6), "an empty ':D#' in the first MinimumSlewSeconds is not trusted");

            rig.Received.Should().Contain($":Sr{Lx200Format.FormatRa(jnow.RA, CoordinatePrecision.High)}#");
            rig.Received.Should().Contain($":Sd{Lx200Format.FormatDec(jnow.Dec, CoordinatePrecision.High)}#");
            var (ra, dec) = rig.Sim.BelievedRaDec;
            Lx200Astro.SeparationDeg(ra, dec, jnow.RA, jnow.Dec).Should().BeLessThan(10.0 / 3600, "the simulated mount went to the JNow target");
            // the driver transformed J2000 to JNow itself a moment later than the test did: equal to ~1e-12 h
            mount.RightAscension.Should().BeApproximately(jnow.RA, 1e-9, "after a goto the target is reported while the mount's reply rounds to it");
            mount.Declination.Should().BeApproximately(jnow.Dec, 1e-9);
            mount.Slewing.Should().BeFalse();
            mount.TargetCoordinates.Should().BeNull();
        }

        [Test]
        public async Task Goto_BelowTheHorizon_IsRefusedByTheMount_AndReturnsFalse() {
            // the mount's own refusal: the driver's slew guard (Lx200SlewGuardTest) would stop this goto before ':MS#'
            using var rig = new Rig(configure: s => s.SlewGuardEnabled = false);
            var mount = await rig.ConnectTelescope();
            using var notes = Rig.CaptureNotifications();
            var (raNow, _) = rig.Sim.BelievedRaDec;
            var below = JNow(Lx200Astro.Wrap(raNow + 12, 24), -80);   // opposite hour angle, far south: below the horizon

            (await mount.SlewToCoordinates(below, CancellationToken.None)).Should().BeFalse();
            rig.Received.Should().Contain(":MS#");
            rig.Sim.Slewing.Should().BeFalse();
            notes.Posted.Should().Contain(n => n.Kind == NotificationKind.Error && n.Message.Contains("below the horizon"));
        }

        [Test]
        public async Task SlewToAltAz_UsesSaSzMa_AndArrives() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            var target = new TopocentricCoordinates(Angle.ByDegree(160), Angle.ByDegree(40), Angle.ByDegree(22.25), Angle.ByDegree(114.18), 10);

            (await mount.SlewToAltAz(target, CancellationToken.None)).Should().BeTrue();
            rig.Received.Should().Contain(new[] { ":Sa+40*00'00#", ":Sz160*00:00#", ":MA#" });
            mount.Altitude.Should().BeApproximately(40, 1.0 / 60);
            mount.Azimuth.Should().BeApproximately(160, 1.0 / 60);
        }

        [Test]
        public async Task Sync_MovesTheMountsPosition_AndReportsTheSyncTargetExactly() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            var (j2000, jnow) = NearbyTarget(rig.Sim, 0.02, -0.3);

            mount.Sync(j2000).Should().BeTrue();

            rig.Received.Should().Contain(":CM#");
            var (ra, dec) = rig.Sim.BelievedRaDec;
            Lx200Astro.SeparationDeg(ra, dec, jnow.RA, jnow.Dec).Should().BeLessThan(8.0 / 3600, "':Sr' carries whole seconds of RA (up to 7.5\" of rounding)");
            // NINA's TelescopeVM waits until its position is within 1" of the sync target, or 5 s: the mount's reply is
            // rounded to 1 s of RA, so without this it would wait out the 5 s on every centring pass (plan section 6)
            (mount.Coordinates - jnow).Distance.ArcSeconds.Should().BeLessThan(0.01);
            await Task.Delay(700);   // a few poll cycles: the mount still rounds to the target, so it stays reported
            (mount.Coordinates - jnow).Distance.ArcSeconds.Should().BeLessThan(0.01);
        }

        [Test]
        public async Task Sync_FarFromTheMountsPosition_IsRefused_AndNothingIsSynced() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            var (j2000, _) = NearbyTarget(rig.Sim, 0.0, 5.0);

            mount.Sync(j2000).Should().BeFalse("5° is beyond the 3° sync guard: more likely a bad solve");
            rig.Received.Should().NotContain(":CM#");
        }

        [Test]
        public async Task StopSlew_AbortsTheGoto_FastAndTheSlewReturnsFalse() {
            using var rig = new Rig(new SimOptions { SlewSeconds = 8, PlanetaryUpdateSeconds = 0.2 });
            var mount = await rig.ConnectTelescope();
            var (j2000, _) = NearbyTarget(rig.Sim, 1.0, 10.0);
            var slew = mount.SlewToCoordinates(j2000, CancellationToken.None);
            (await Rig.Eventually(() => rig.Sim.Slewing, TimeSpan.FromSeconds(2))).Should().BeTrue();
            await Task.Delay(500);

            var asked = rig.Link.UtcNow;
            mount.StopSlew();
            (await slew.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeFalse();

            var halt = rig.Transmitted().Last(t => t.Text == ":Q#");
            (halt.Utc - asked).TotalMilliseconds.Should().BeLessThan(100, "':Q#' jumps the queue");
            rig.Sim.Slewing.Should().BeFalse();
            mount.Slewing.Should().BeFalse();
        }

        [Test]
        public async Task CancelledGoto_SendsQ_AndThrows() {
            using var rig = new Rig(new SimOptions { SlewSeconds = 8, PlanetaryUpdateSeconds = 0.2 });
            var mount = await rig.ConnectTelescope();
            var (j2000, _) = NearbyTarget(rig.Sim, 1.0, 10.0);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));

            Func<Task> slew = () => mount.SlewToCoordinates(j2000, cts.Token);
            await slew.Should().ThrowAsync<OperationCanceledException>();
            (await Rig.Eventually(() => !rig.Sim.Slewing, TimeSpan.FromSeconds(1))).Should().BeTrue();
            rig.Received.Should().Contain(":Q#");
        }

        [Test]
        public async Task SoftPark_HaltsAndStopsTracking_WithoutTheAutostarParkCommand() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            mount.CanPark.Should().BeTrue("with CanPark false NINA would slew to Dec +89 and send ':AL#' (TelescopeVM.cs:131-159)");

            await mount.Park(CancellationToken.None);
            mount.AtPark.Should().BeTrue();
            rig.Sim.Tracking.Should().BeFalse("soft park ends with ':AL#'");
            rig.Received.Should().ContainInOrder(":Q#", ":AL#");
            (await mount.SlewToCoordinates(NearbyTarget(rig.Sim, 0.1, 1).J2000, CancellationToken.None)).Should().BeFalse("no goto while parked");

            await mount.Unpark(CancellationToken.None);
            mount.AtPark.Should().BeFalse();
            rig.Sim.Tracking.Should().BeTrue("unpark resumes tracking with ':AA#'");
            rig.Received.Should().NotContain(c => c.StartsWith(":hP", StringComparison.Ordinal));
        }

        [Test]
        public async Task Goto_WithTrackingOff_SwitchesTrackingOnFirst_AsNinasAscomTelescopeDoes() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            mount.TrackingEnabled = false;   // e.g. a sequence's "Set Tracking: Stopped"
            rig.Sim.Tracking.Should().BeFalse();
            var received = rig.Received.Count;

            (await mount.SlewToCoordinates(NearbyTarget(rig.Sim, 0.1, 1.0).J2000, CancellationToken.None)).Should().BeTrue();

            rig.Received.Skip(received).Should().ContainInOrder(":AA#", ":MS#");
            rig.Sim.Tracking.Should().BeTrue("the target must not drift out of the frame");
            mount.TrackingEnabled.Should().BeTrue();
        }

        [Test]
        public async Task SoftPark_SurvivesAReconnect_AndUnparkThenResumesTracking() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            await mount.Park(CancellationToken.None);
            rig.Sim.Tracking.Should().BeFalse();
            mount.Disconnect();

            await rig.ConnectTelescope();
            mount.AtPark.Should().BeTrue("the soft park is kept in the profile");
            (await mount.SlewToCoordinates(NearbyTarget(rig.Sim, 0.1, 1).J2000, CancellationToken.None)).Should().BeFalse("no goto while parked");
            await mount.Unpark(CancellationToken.None);
            mount.AtPark.Should().BeFalse();
            rig.Sim.Tracking.Should().BeTrue("unpark resumes the tracking the park stopped");
            rig.Settings.SoftParked.Should().BeFalse();

            mount.Disconnect();
            await rig.ConnectTelescope();
            mount.AtPark.Should().BeFalse();
        }

        [Test]
        public async Task SoftPark_UnparkedOnTheHandbox_IsNotRestored() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            await mount.Park(CancellationToken.None);
            mount.Disconnect();
            using (var handbox = rig.Cable.Open(new Lx200Trace())) {   // tracking switched on at the mount meanwhile
                handbox.Write(System.Text.Encoding.ASCII.GetBytes(":AA#"));
                (await Rig.Eventually(() => rig.Sim.Tracking, TimeSpan.FromSeconds(2))).Should().BeTrue();
            }

            await rig.ConnectTelescope();
            mount.AtPark.Should().BeFalse("a mount that tracks again was unparked elsewhere");
            rig.Settings.SoftParked.Should().BeFalse();
        }

        [Test]
        public async Task SoftPark_ToAStoredPosition_SlewsThereFirst() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            rig.Settings.SoftParkAltitude = 30;
            rig.Settings.SoftParkAzimuth = 180;

            await mount.Park(CancellationToken.None);

            rig.Received.Should().ContainInOrder(":Q#", ":Sa+30*00'00#", ":Sz180*00:00#", ":MA#", ":AL#");
            rig.Sim.Axes.Alt.Should().BeApproximately(30, 0.01);
            rig.Sim.Axes.Az.Should().BeApproximately(180, 0.01);
            mount.AtPark.Should().BeTrue();
        }

        [Test]
        public async Task TheAutostarParkCommand_CanNeverBeSent() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            foreach (var attempt in new Action[] {
                () => mount.SendCommandBlind(":hP#"),
                () => mount.SendCommandBlind("hP"),
                () => mount.SendCommandString(":hP#"),
                () => mount.SendCommandBool(":hP#"),
                () => mount.SendCommandBlind(":Q:hP#"),
                () => mount.SendCommandBlind(": hP#"),
                () => rig.Link.Send(":hP#", Lx200Lane.Stop)
            }) {
                attempt.Should().Throw<Lx200BlockedCommandException>();
            }
            await mount.Park(CancellationToken.None);
            await mount.Unpark(CancellationToken.None);
            mount.Setpark();
            mount.Disconnect();
            rig.Received.Should().NotContain(c => c.Contains("hP"));
            rig.Link.Should().BeNull("the link closed with the last device");
        }

        [Test]
        public async Task AltAzFork_HasNoMeridianFlip_AndNoPierSide() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            var before = rig.Received.Count;

            double.IsNaN(mount.TimeToMeridianFlip).Should().BeTrue("NINA's MeridianFlipTrigger stands down on NaN");
            mount.SideOfPier.Should().Be(PierSide.pierUnknown);
            mount.DestinationSideOfPier(mount.Coordinates).Should().Be(PierSide.pierUnknown);
            mount.TargetSideOfPier.Should().BeNull();
            mount.CanSetPierSide.Should().BeFalse();
            mount.CanFindHome.Should().BeFalse();
            mount.AtHome.Should().BeFalse();
            (await mount.MeridianFlip(mount.Coordinates, CancellationToken.None)).Should().BeTrue();
            await Task.Delay(100);
            rig.Received.Skip(before).Should().OnlyContain(c => c == ":GR#" || c == ":GD#" || c == ":GA#" || c == ":GZ#" || c == ":GW#" || c == ":GS#",
                "a meridian flip sends nothing but the background polls");
        }

        [Test]
        public async Task Site_WithinTheMountsArcminute_ReportsTheProfile_AndWritesBeforeAlignmentAreTolerant() {
            // the site may only be written before alignment (plan, "Time and site authority"): a mount that is not aligned yet
            using var rig = new Rig(new SimOptions { AlignmentStatus = '0', SlewSeconds = 0.8, PlanetaryUpdateSeconds = 0.2 });
            var mount = await rig.ConnectTelescope();
            mount.IsAligned.Should().BeFalse();
            rig.Sim.LongitudeEast.Should().BeApproximately(114 + (11.0 / 60), 1e-9, "the mount keeps 114°11'");

            mount.SiteLongitude.Should().Be(114.18, "0.0033° off is the mount's 1' rounding, not a different site (RIM MNT-14)");
            mount.SiteLatitude.Should().Be(22.25);
            mount.SiteElevation.Should().Be(10, "the mount stores no elevation");

            mount.SiteLatitude = 22.3;
            rig.Received.Should().Contain(":St+22*18#");
            mount.SiteLatitude.Should().Be(22.3, "the readback 22°18' is within 1' of what was written");
            mount.SiteLongitude = 114.17;
            rig.Received.Should().Contain(":Sg245*50#");
            mount.SiteLongitude.Should().Be(114.17);
        }

        [Test]
        public async Task Site_OnAnAlignedMount_IsNeverWritten_AndTheMountsValueStaysReported() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            mount.IsAligned.Should().BeTrue();
            using var notes = Rig.CaptureNotifications();

            mount.SiteLatitude = 22.3;      // what NINA's TOTELESCOPE site sync does at connect (TelescopeVM.cs:445-451)
            mount.SiteLongitude = 114.5;

            rig.Received.Should().NotContain(c => c.StartsWith(":St", StringComparison.Ordinal) || c.StartsWith(":Sg", StringComparison.Ordinal),
                "after alignment the port only verifies the site, never writes it");
            mount.SiteLatitude.Should().Be(22.25, "the mount's own site is still reported, so NINA's check after its sync says it could not be set");
            mount.SiteLongitude.Should().Be(114.18);
            notes.Posted.Should().Contain(n => n.Kind == NotificationKind.Warning && n.Message.Contains("not written") && n.Message.Contains("aligned"));
        }

        [Test]
        public async Task Site_IsNeverWritten_WhenGwCannotTellWhetherTheMountIsAligned() {
            using var rig = new Rig(new SimOptions { GwSupported = false, SlewSeconds = 0.8, PlanetaryUpdateSeconds = 0.2 });
            var mount = await rig.ConnectTelescope();
            mount.IsAligned.Should().BeNull();
            mount.SiteLatitude = 22.3;
            rig.Received.Should().NotContain(c => c.StartsWith(":St", StringComparison.Ordinal));
        }

        [Test]
        public async Task Site_FarFromTheProfile_ReportsTheMountsValue() {
            using var rig = new Rig(new SimOptions { SiteLatitude = 23.0 });
            var mount = await rig.ConnectTelescope();
            mount.SiteLatitude.Should().Be(23.0, "a 45' difference is a different site; NINA then offers to sync it");
        }

        [Test]
        public async Task MountClockOff_OnAnAlignedMount_IsReported_ButNeverWritten() {
            using var rig = new Rig(new SimOptions { ClockErrorSeconds = 120, PlanetaryUpdateSeconds = 0.2 });
            rig.Profile.TelescopeSettings.TimeSync = true;
            using var notes = Rig.CaptureNotifications();
            var mount = await rig.ConnectTelescope();

            rig.Received.Should().NotContain(c => c.StartsWith(":SC", StringComparison.Ordinal) || c.StartsWith(":SL", StringComparison.Ordinal) || c.StartsWith(":SG", StringComparison.Ordinal));
            notes.Posted.Should().Contain(n => n.Kind == NotificationKind.Warning && n.Message.Contains("mount is aligned"));
            (mount.UTCDate - DateTime.UtcNow).TotalSeconds.Should().BeApproximately(120, 2, "UTCDate reports the mount's clock");
            (await mount.SetMountClock()).Should().BeFalse("time is never written after alignment");
            rig.Received.Should().NotContain(c => c.StartsWith(":SC", StringComparison.Ordinal));
        }

        [Test]
        public async Task MountClockOff_BeforeAlignment_IsWritten_WhenTheUtcAndLocalDatesAgree() {
            // 04:00 UTC = 12:00 in Hong Kong: one calendar date, so ':SC' is unambiguous
            using var rig = new Rig(new SimOptions { ClockErrorSeconds = 300, AlignmentStatus = '0', PlanetaryUpdateSeconds = 0.2, SlewSeconds = 0.8 },
                                    clockUtc: new DateTime(2026, 10, 5, 4, 0, 0, DateTimeKind.Utc));
            rig.Profile.TelescopeSettings.TimeSync = true;
            var mount = await rig.ConnectTelescope();

            mount.IsAligned.Should().BeFalse();
            var received = rig.Received.ToList();
            var sg = received.IndexOf(":SG-08#");
            var sl = received.FindIndex(c => c.StartsWith(":SL12:00:", StringComparison.Ordinal));
            var sc = received.IndexOf(":SC10/05/26#");
            sg.Should().BeGreaterThan(0, "the UTC offset of Hong Kong, ':SG-08#'");
            sl.Should().BeGreaterThan(sg, "then the local time, 12:00");
            sc.Should().BeGreaterThan(sl, "then the date, the same day in UTC and local time");
            Math.Abs((rig.Sim.MountUtc - rig.SimOptions.UtcNow()).TotalSeconds).Should().BeLessThan(2, "the mount clock now matches");
            (mount.UTCDate - rig.SimOptions.UtcNow()).TotalSeconds.Should().BeApproximately(0, 2);
        }

        [Test]
        public async Task MountClockOff_BeforeAlignment_IsNotWritten_JustAfterLocalMidnight_UntilTheDateConventionIsKnown() {
            // 16:30 UTC = 00:30 HKT the next day: the ':SC' convention decides the date, and it is not settled yet
            var start = new DateTime(2026, 10, 5, 16, 30, 0, DateTimeKind.Utc);
            using (var rig = new Rig(new SimOptions { ClockErrorSeconds = 300, AlignmentStatus = '0', PlanetaryUpdateSeconds = 0.2 }, clockUtc: start)) {
                rig.Profile.TelescopeSettings.TimeSync = true;
                await rig.ConnectTelescope();
                rig.Received.Should().NotContain(c => c.StartsWith(":SC", StringComparison.Ordinal) || c.StartsWith(":SL", StringComparison.Ordinal));
            }
            using (var rig = new Rig(new SimOptions { ClockErrorSeconds = 300, AlignmentStatus = '0', PlanetaryUpdateSeconds = 0.2 }, clockUtc: start,
                                     configure: s => s.DateConvention = Lx200DateConvention.Utc)) {
                rig.Profile.TelescopeSettings.TimeSync = true;
                await rig.ConnectTelescope();
                rig.Received.Should().Contain(":SC10/05/26#", "the UTC date, as the bench settled");
                rig.Received.Should().Contain(c => c.StartsWith(":SL00:30:", StringComparison.Ordinal));
                Math.Abs((rig.Sim.MountUtc - rig.SimOptions.UtcNow()).TotalSeconds).Should().BeLessThan(2);
            }
        }

        [Test]
        public async Task MountWithoutGw_IsTreatedAsAligned_ForTheClockRule() {
            using var rig = new Rig(new SimOptions { GwSupported = false, ClockErrorSeconds = 60, PlanetaryUpdateSeconds = 0.2 });
            rig.Profile.TelescopeSettings.TimeSync = true;
            var mount = await rig.ConnectTelescope();
            mount.IsAligned.Should().BeNull();
            mount.TrackingEnabled.Should().BeTrue("ACK 'A' means alt-az tracking");
            rig.Received.Should().NotContain(c => c.StartsWith(":SC", StringComparison.Ordinal) || c.StartsWith(":SL", StringComparison.Ordinal));
        }

        [Test]
        public async Task Tracking_OffSendsAL_OnSendsAAOnlyWhenNeeded_AndModesMapToTQTL() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            mount.TrackingModes.Should().Equal(TrackingMode.Sidereal, TrackingMode.Lunar, TrackingMode.Stopped);

            mount.TrackingEnabled = true;
            rig.Received.Should().NotContain(":AA#", "already tracking");
            mount.TrackingEnabled = false;
            rig.Sim.Tracking.Should().BeFalse();
            mount.TrackingRate.TrackingMode.Should().Be(TrackingMode.Stopped);
            mount.TrackingEnabled = true;
            rig.Sim.Tracking.Should().BeTrue();
            rig.Received.Count(c => c == ":AA#").Should().Be(1);
            mount.TrackingMode = TrackingMode.Lunar;
            rig.Received.Should().Contain(":TL#");
            mount.TrackingRate.TrackingMode.Should().Be(TrackingMode.Lunar);
            Action custom = () => mount.TrackingMode = TrackingMode.Custom;
            custom.Should().Throw<ArgumentException>();
            rig.Received.Should().NotContain(":AP#");
        }

        [Test]
        public async Task MoveAxis_PicksTheNearestRate_MovesTheAxis_AndStops() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            mount.GetAxisRates(TelescopeAxes.Primary).Select(r => r.Item1).Should().Equal(10.0 / 3600, 0.067, 1.5, 8.0);

            var az0 = rig.Sim.Axes.Az;
            mount.MoveAxis(TelescopeAxes.Primary, 1.4);
            mount.Slewing.Should().BeTrue();
            await Task.Delay(400);
            mount.MoveAxis(TelescopeAxes.Primary, 0);

            rig.Received.Should().ContainInOrder(":RM#", ":Me#", ":Qe#", ":Qw#");
            Lx200Astro.DegreeDifference(rig.Sim.Axes.Az, az0).Should().BeInRange(0.3, 1.0, "about 0.4 s at 1.5°/s, eastward");
            rig.Sim.AnyAxisMotion.Should().BeFalse();
            mount.Slewing.Should().BeFalse();
            rig.Link.OwedStops.Should().BeEmpty();
        }

        [Test]
        public async Task Disconnect_ClosesTheLink() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            var link = rig.Link;
            mount.Disconnect();
            mount.Connected.Should().BeFalse();
            link.State.Should().Be(Lx200LinkState.Closed);
            rig.Link.Should().BeNull();
            (await mount.Connect(CancellationToken.None)).Should().BeTrue("a new link opens on the next connect");
            rig.Cable.Opens.Should().Be(2);
        }

        [Test]
        public async Task Connect_WithoutAMount_FailsWithAMessage() {
            using var rig = new Rig();
            rig.Cable.Mute(true);
            rig.LinkOptions.HandshakeAttempts = 1;
            using var notes = Rig.CaptureNotifications();
            (await rig.Telescope.Connect(CancellationToken.None)).Should().BeFalse();
            rig.Telescope.Connected.Should().BeFalse();
            notes.Posted.Should().Contain(n => n.Kind == NotificationKind.Error && n.Message.Contains("No LX200 mount answered"));
        }
    }
}
