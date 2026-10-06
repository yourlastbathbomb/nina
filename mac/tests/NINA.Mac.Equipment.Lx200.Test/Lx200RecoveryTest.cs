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
using NINA.Mac.Lx200;
using NINA.Mac.Lx200.Sim;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200.Test {

    /// <summary>
    /// The devices through a USB-serial unplug and replug, and the abort latency on a busy 9600-baud link.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Lx200RecoveryTest {

        [Test]
        public async Task CableUnpluggedAndReplugged_TheMountStaysConnected_AndRecoversOnItsOwn() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            var focuser = await rig.ConnectFocuser();
            using var notes = Rig.CaptureNotifications();
            var link = rig.Link;

            rig.Cable.Unplug();
            (await Rig.Eventually(() => mount.LinkState == Lx200LinkState.Reconnecting, TimeSpan.FromSeconds(2))).Should().BeTrue("the next poll finds the port gone");
            mount.Connected.Should().BeTrue("a short outage does not disconnect the devices: NINA's sequence keeps running");
            focuser.Connected.Should().BeTrue();
            var raDuringOutage = mount.RightAscension;
            double.IsNaN(raDuringOutage).Should().BeFalse("the last known position is still reported");
            (await mount.SlewToCoordinates(new Coordinates(Angle.ByHours(mount.RightAscension), Angle.ByDegree(mount.Declination + 1), Epoch.JNOW), CancellationToken.None))
                .Should().BeFalse("a goto while the cable is out fails after WaitForReconnect instead of hanging");
            await Task.Delay(500);
            rig.Cable.FailedOpens.Should().BeGreaterThan(0);

            rig.Cable.Replug();
            (await Rig.Eventually(() => mount.LinkState == Lx200LinkState.Connected, TimeSpan.FromSeconds(5))).Should().BeTrue("the link reopens the port by itself");
            rig.Link.Should().BeSameAs(link, "the same link object recovers; the devices never had to reconnect");
            link.ReconnectCount.Should().Be(1);
            notes.Posted.Should().Contain(n => n.Kind == NotificationKind.Warning && n.Message.Contains("lost"));
            notes.Posted.Should().Contain(n => n.Message.Contains("restored"));

            // everything works again: polling, a goto, a focuser move
            var received = rig.Received.Count;
            (await Rig.Eventually(() => rig.Received.Skip(received).Contains(":GR#"), TimeSpan.FromSeconds(2))).Should().BeTrue("polling resumed");
            var (ra, dec) = rig.Sim.BelievedRaDec;
            var target = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec + 1.5), Epoch.JNOW);
            (await mount.SlewToCoordinates(target, CancellationToken.None)).Should().BeTrue();
            var (ra2, dec2) = rig.Sim.BelievedRaDec;
            Lx200Astro.SeparationDeg(ra2, dec2, target.RA, target.Dec).Should().BeLessThan(10.0 / 3600);
            await focuser.Move(focuser.Position + 200, CancellationToken.None);
            focuser.History.Last().Completed.Should().BeTrue();
        }

        [Test]
        public async Task ManualMoveRunningWhenTheCableIsPulled_IsHaltedFirstThingOnReplug() {
            using var rig = new Rig();
            var mount = await rig.ConnectTelescope();
            mount.MoveAxis(TelescopeAxes.Secondary, 0.06);
            rig.Sim.AnyAxisMotion.Should().BeTrue();

            rig.Cable.Unplug();
            (await Rig.Eventually(() => mount.LinkState == Lx200LinkState.Reconnecting, TimeSpan.FromSeconds(2))).Should().BeTrue();
            mount.MoveAxis(TelescopeAxes.Secondary, 0);   // the user lets go of the button while the cable is out
            await Task.Delay(500);
            rig.Sim.AnyAxisMotion.Should().BeTrue("the mount keeps moving until a halt reaches it");
            var received = rig.Received.Count;

            rig.Cable.Replug();
            (await Rig.Eventually(() => !rig.Sim.AnyAxisMotion, TimeSpan.FromSeconds(5))).Should().BeTrue();
            rig.Received.Skip(received).Take(4).Should().Contain(":Qn#", "the owed halt goes out right after the handshake");
            (await Rig.Eventually(() => rig.Link.OwedStops.Count == 0, TimeSpan.FromSeconds(1))).Should().BeTrue("both halts were accepted");
        }

        [Test]
        public async Task MountSwitchedOffAndOnWhileTheCableWasOut_TheConnectChecksRunAgain_AndALostAlignmentStopsGotos() {
            using var rig = new Rig(new SimOptions { HighPrecisionPointing = true, SlewSeconds = 0.8, PlanetaryUpdateSeconds = 0.2 },
                                    configure: s => s.GuideRateArcsecPerSec = 12.0);
            var mount = await rig.ConnectTelescope();
            rig.Sim.LongFormat.Should().BeTrue();
            rig.Sim.HighPrecisionPointing.Should().BeFalse();
            rig.Sim.GuideRateArcsecPerSec.Should().Be(12.0);
            mount.IsAligned.Should().BeTrue();
            using var notes = Rig.CaptureNotifications();

            rig.Cable.Unplug();
            (await Rig.Eventually(() => mount.LinkState == Lx200LinkState.Reconnecting, TimeSpan.FromSeconds(3))).Should().BeTrue();
            rig.Sim.SwitchOffAndOn();
            rig.Sim.LongFormat.Should().BeFalse("a mount switched on starts in the short format");
            rig.Cable.Replug();
            (await Rig.Eventually(() => mount.LinkState == Lx200LinkState.Connected, TimeSpan.FromSeconds(5))).Should().BeTrue();

            (await Rig.Eventually(() => rig.Sim.LongFormat && !rig.Sim.HighPrecisionPointing && rig.Sim.GuideRateArcsecPerSec == 12.0, TimeSpan.FromSeconds(5)))
                .Should().BeTrue("the format, High Precision pointing and the guide rate are set again after the reconnect");
            (await Rig.Eventually(() => mount.IsAligned == false, TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await Rig.Eventually(() => notes.Posted.Any(n => n.Kind == NotificationKind.Error && n.Message.Contains("no longer aligned")), TimeSpan.FromSeconds(2)))
                .Should().BeTrue("a lost alignment is reported");
            mount.Connected.Should().BeTrue();
            var received = rig.Received.Count;
            var (ra, dec) = rig.Sim.BelievedRaDec;
            (await mount.SlewToCoordinates(new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec + 1), Epoch.JNOW), CancellationToken.None))
                .Should().BeFalse("every goto of a mount that lost its alignment would be wrong");
            rig.Received.Skip(received).Should().NotContain(c => c == ":MS#" || c == ":MA#");
        }

        [Test]
        public async Task CableNeverComesBack_TheDevicesDisconnect_AfterTheGiveUpTime() {
            using var rig = new Rig(linkOptions: new Lx200LinkOptions {
                ReconnectInterval = TimeSpan.FromMilliseconds(100),
                WaitForReconnect = TimeSpan.FromSeconds(1),
                ReconnectGiveUp = TimeSpan.FromSeconds(1.5)
            });
            var mount = await rig.ConnectTelescope();
            using var notes = Rig.CaptureNotifications();
            rig.Cable.Unplug();
            (await Rig.Eventually(() => !mount.Connected, TimeSpan.FromSeconds(6))).Should().BeTrue();
            (await Rig.Eventually(() => notes.Posted.Any(n => n.Kind == NotificationKind.Error && n.Message.Contains("did not come back")), TimeSpan.FromSeconds(2))).Should().BeTrue();
            rig.Link.Should().BeNull("the failed link was released");

            rig.Cable.Replug();
            (await mount.Connect(CancellationToken.None)).Should().BeTrue("connecting again opens a new link");
            rig.Link.State.Should().Be(Lx200LinkState.Connected);
        }

        [Test]
        public async Task StopSlew_OnABusy9600BaudLink_GoesOutWithinOneTransaction() {
            using var rig = new Rig(new SimOptions { SlewSeconds = 10, PlanetaryUpdateSeconds = 0.2 },
                                    configure: s => s.PollIntervalMs = 100, baudRate: 9600, turnaround: TimeSpan.FromMilliseconds(15));
            var mount = await rig.ConnectTelescope();
            var (ra, dec) = rig.Sim.BelievedRaDec;
            var slew = mount.SlewToCoordinates(new Coordinates(Angle.ByHours(ra + 0.5), Angle.ByDegree(dec + 8), Epoch.JNOW), CancellationToken.None);
            (await Rig.Eventually(() => rig.Sim.Slewing, TimeSpan.FromSeconds(3))).Should().BeTrue();
            await Task.Delay(700);

            var asked = rig.Link.UtcNow;
            mount.StopSlew();
            (await slew.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeFalse();
            var halt = rig.Transmitted().First(t => t.Utc >= asked && t.Text == ":Q#");
            var latency = (halt.Utc - asked).TotalMilliseconds;
            TestContext.Out.WriteLine($"':Q#' went out {latency:0} ms after StopSlew with the mount polled every 100 ms at 9600 baud");
            latency.Should().BeLessThan(100);
            rig.Sim.Slewing.Should().BeFalse();
        }
    }
}
