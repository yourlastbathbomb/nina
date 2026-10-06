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
using NINA.Core.Model;
using NINA.Core.Utility.Notification;
using NINA.Mac.Lx200;
using NINA.Mac.Lx200.Sim;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200.Test {

    /// <summary>
    /// The driver's slew guard (defence in depth below the app's checks) and the optional ':So' high limit written at connect:
    /// gotos whose target is, now and at the profile's site, below the profile's horizon or above the maximum altitude never
    /// reach the mount; ':So' is written only when switched on, and then read back.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Lx200SlewGuardTest {

        private static SimOptions Firmware40g() => new SimOptions { PlanetaryUpdateSeconds = 0.2, SlewSeconds = 0.8 }
            .Apply(new[] { "firmware=4.0g", "gw=none", "long=true", "lon=west360", "degree=df" });

        /// <summary>JNow RA/Dec of the given altitude and azimuth now, on the simulator's sky (its clock and site).</summary>
        private static Coordinates AtAltAz(Rig rig, double altitude, double azimuth) {
            var lst = Lx200Astro.LstHours(rig.Sim.MountUtc, rig.Sim.LongitudeEast);
            var (ra, dec) = Lx200Astro.ToRaDec(altitude, azimuth, lst, rig.SimOptions.SiteLatitude);
            return new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), Epoch.JNOW);
        }

        private static bool SentAGoto(Rig rig, int from) =>
            rig.Received.Skip(from).Any(c => c.StartsWith(":Sr", StringComparison.Ordinal) || c.StartsWith(":Sd", StringComparison.Ordinal)
                || c == ":MS#" || c.StartsWith(":Sa", StringComparison.Ordinal) || c.StartsWith(":Sz", StringComparison.Ordinal) || c == ":MA#");

        [Test]
        public async Task GotoAboveTheMaximumAltitude_IsRefusedBeforeAnythingIsSent_WithAClearMessage() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            rig.Settings.SlewGuardEnabled.Should().BeTrue("on by default");
            rig.Settings.MaxAltitudeDegrees.Should().Be(75, "decision 5: a fixed 75°");
            using var notes = Rig.CaptureNotifications();
            var sent = rig.Received.Count;

            (await mount.SlewToCoordinates(AtAltAz(rig, 82, 180).Transform(Epoch.J2000), CancellationToken.None)).Should().BeFalse();

            SentAGoto(rig, sent).Should().BeFalse("the guard refuses before ':Sr'/':Sd'/':MS#'");
            rig.Sim.Slewing.Should().BeFalse();
            var error = notes.Posted.Should().ContainSingle(n => n.Kind == NotificationKind.Error).Subject.Message;
            TestContext.Out.WriteLine(error);
            error.Should().Contain("slew guard").And.Contain("above the 75° maximum altitude").And.Contain("altitude 8").And.Contain("22.250°N 114.180°E");
        }

        [Test]
        public async Task GotoBelowTheHorizon_WithoutACustomHorizon_IsRefusedAtZeroDegrees() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            using var notes = Rig.CaptureNotifications();
            var sent = rig.Received.Count;

            (await mount.SlewToCoordinates(AtAltAz(rig, -5, 200), CancellationToken.None)).Should().BeFalse();

            SentAGoto(rig, sent).Should().BeFalse();
            notes.Posted.Should().Contain(n => n.Kind == NotificationKind.Error && n.Message.Contains("below the horizon (0°: no custom horizon is loaded)"));
        }

        [Test]
        public async Task CustomHorizon_RefusesATargetBehindIt_AndLetsAClearOneGo() {
            using var rig = new Rig();
            // 40° from azimuth 300 through north to 60 (no northern sky), 10° over the south
            rig.Profile.AstrometrySettings.Horizon = CustomHorizon.FromReader_Standard(new StringReader("0 40\n60 40\n90 10\n270 10\n300 40\n360 40\n"));
            var mount = await rig.ConnectTelescope();
            using var notes = Rig.CaptureNotifications();
            var sent = rig.Received.Count;

            (await mount.SlewToCoordinates(AtAltAz(rig, 30, 20), CancellationToken.None)).Should().BeFalse("30° in the north is behind the 40° horizon");
            SentAGoto(rig, sent).Should().BeFalse();
            notes.Posted.Should().Contain(n => n.Message.Contains("below the custom horizon (40.0° at that azimuth)"));

            var south = AtAltAz(rig, 30, 180);
            (await mount.SlewToCoordinates(south, CancellationToken.None)).Should().BeTrue("30° over the south clears the 10° horizon");
            var (ra, dec) = rig.Sim.BelievedRaDec;
            Lx200Astro.SeparationDeg(ra, dec, south.RA, south.Dec).Should().BeLessThan(10.0 / 3600);
        }

        [Test]
        public async Task SlewToAltAz_IsGuardedToo() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            var sent = rig.Received.Count;

            (await mount.SlewToAltAz(new TopocentricCoordinates(Angle.ByDegree(180), Angle.ByDegree(80), Angle.ByDegree(22.25), Angle.ByDegree(114.18), 10), CancellationToken.None))
                .Should().BeFalse();
            (await mount.SlewToAltAz(new TopocentricCoordinates(Angle.ByDegree(180), Angle.ByDegree(-2), Angle.ByDegree(22.25), Angle.ByDegree(114.18), 10), CancellationToken.None))
                .Should().BeFalse();
            SentAGoto(rig, sent).Should().BeFalse();

            (await mount.SlewToAltAz(new TopocentricCoordinates(Angle.ByDegree(180), Angle.ByDegree(70), Angle.ByDegree(22.25), Angle.ByDegree(114.18), 10), CancellationToken.None))
                .Should().BeTrue("70° is below the 75° limit");
            mount.Altitude.Should().BeApproximately(70, 1.0 / 60);
        }

        [Test]
        public async Task MaximumAltitude_FollowsTheSetting_AndTheGuardCanBeSwitchedOff() {
            using var rig = new Rig(configure: s => s.MaxAltitudeDegrees = 80);
            var mount = await rig.ConnectTelescope();
            (await mount.SlewToCoordinates(AtAltAz(rig, 78, 180), CancellationToken.None)).Should().BeTrue("78° is below the 80° setting");

            rig.Settings.MaxAltitudeDegrees = 60;
            (await mount.SlewToCoordinates(AtAltAz(rig, 70, 180), CancellationToken.None)).Should().BeFalse("the setting is read at every goto");

            rig.Settings.SlewGuardEnabled = false;
            (await mount.SlewToCoordinates(AtAltAz(rig, 70, 180), CancellationToken.None)).Should().BeTrue("the guard is off; the mount's own high limit is 90°");
        }

        [Test]
        public async Task ProfileSiteUnset_TheMountsSiteIsUsed_AndSaidSo() {
            using var rig = new Rig();
            rig.Profile.AstrometrySettings.Latitude = 0;
            rig.Profile.AstrometrySettings.Longitude = 0;
            var mount = await rig.ConnectTelescope();
            using var notes = Rig.CaptureNotifications();

            (await mount.SlewToCoordinates(AtAltAz(rig, 82, 180), CancellationToken.None)).Should().BeFalse();
            notes.Posted.Should().Contain(n => n.Message.Contains("the mount's site because the profile's is not set") && n.Message.Contains("22.250°N"));
        }

        [Test]
        public void ParseLimitDegrees_ReadsTheGhAndGoShapes() {
            Lx200Telescope.ParseLimitDegrees("+75*").Should().Be(75);
            Lx200Telescope.ParseLimitDegrees("+75ß").Should().Be(75);
            Lx200Telescope.ParseLimitDegrees("75*#").Should().Be(75);
            Lx200Telescope.ParseLimitDegrees("-05*").Should().Be(-5);
            Lx200Telescope.ParseLimitDegrees("*").Should().BeNull();
            Lx200Telescope.ParseLimitDegrees(null).Should().BeNull();
        }

        [Test]
        public async Task HighLimit_IsNotWrittenByDefault_OnFirmware40g() {
            using var rig = new Rig(Firmware40g());
            rig.Settings.WriteMountHighLimitOnConnect.Should().BeFalse("off until the bench confirms ':So' on 4.0g");
            await rig.ConnectTelescope();
            rig.Received.Should().NotContain(c => c.StartsWith(":So", StringComparison.Ordinal) || c.StartsWith(":SO", StringComparison.Ordinal));
        }

        [Test]
        public async Task HighLimit_SwitchedOn_IsWrittenAtConnect_ReadBack_AndTheMountThenRefusesHigherGotos_OnFirmware40g() {
            using var rig = new Rig(Firmware40g(), configure: s => {
                s.WriteMountHighLimitOnConnect = true;
                s.MaxAltitudeDegrees = 70.6;
            });
            using var notes = Rig.CaptureNotifications();
            var mount = await rig.ConnectTelescope();

            rig.Received.Should().ContainInOrder(":So70*#", ":Gh#");
            rig.Received.Should().NotContain(c => c.StartsWith(":SO", StringComparison.Ordinal), "':SO' (upper-case O) sets site 3's name");
            notes.Posted.Should().NotContain(n => n.Message.Contains("high limit"), "the read-back matched");
            Lx200Telescope.ParseLimitDegrees(mount.SendCommandString(":Gh#")).Should().Be(70, "the mount now holds the whole-degree limit, rounded down");

            // with the driver's guard off the mount's own limit refuses the goto
            rig.Settings.SlewGuardEnabled = false;
            (await mount.SlewToCoordinates(AtAltAz(rig, 72, 180), CancellationToken.None)).Should().BeFalse();
            rig.Received.Should().Contain(":MS#");
            notes.Posted.Should().Contain(n => n.Kind == NotificationKind.Error && n.Message.Contains("above the high limit"));
        }

        [Test]
        public async Task HighLimit_RefusedByTheMount_IsReported_AndTheConnectGoesOn() {
            var options = Firmware40g();
            options.NakWhen = command => command.StartsWith(":So", StringComparison.Ordinal);
            using var rig = new Rig(options, configure: s => {
                s.WriteMountHighLimitOnConnect = true;
                s.MaxAltitudeDegrees = 75;
            });
            using var notes = Rig.CaptureNotifications();
            var mount = await rig.ConnectTelescope();
            mount.Connected.Should().BeTrue();
            notes.Posted.Should().Contain(n => n.Kind == NotificationKind.Warning && n.Message.Contains("did not accept the high limit"));
        }
    }
}
