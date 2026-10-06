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
    /// The driver against the simulator configured as this rig's mount as the M2 read-out measured it on 2026-10-06
    /// (mac/docs/m2-mount-readout-2026-10-06.md): firmware 4.0g, ":GW#" not answered, the long format already on, longitude read
    /// back west-positive 0-360, degree byte 0xDF. ":GW#" is asked once per connection and its unanswered state is cached: polls
    /// use ACK and never wait out the 2 s timeout again; with the alignment unknown, site, date and time are never written; and
    /// the native ":Mg" pulse is the default dither strategy.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Lx200Firmware40gTest {

        private static SimOptions Firmware40g() => new SimOptions { PlanetaryUpdateSeconds = 0.2, SlewSeconds = 0.8 }
            .Apply(new[] { "firmware=4.0g", "gw=none", "long=true", "lon=west360", "degree=df" });

        private static int Count(Rig rig, string command) => rig.Received.Count(c => c == command);

        [Test]
        public async Task Connect_AsksGwOncePerConnection_ThenPollsWithAck_WithoutRepeatingTheTimeout() {
            using var rig = new Rig(Firmware40g(), configure: s => s.PollIntervalMs = 100);
            using var notes = Rig.CaptureNotifications();

            var mount = await rig.ConnectTelescope();
            mount.Firmware.Should().Be("LX2001 4.0g");
            mount.IsAligned.Should().BeNull("':GW#' is not answered, so the alignment is unknown");
            mount.EffectivePulseStrategy.Should().Be(Lx200PulseStrategy.NativePulse, "the default: ':Mg' was measured to work in alt-az on 4.0g");
            mount.TrackingEnabled.Should().BeTrue("ACK 'A' means alt-az tracking");
            Count(rig, ":GW#").Should().Be(1, "asked once, at connect");
            var notesAtConnect = notes.Posted.Count;
            var acksAtConnect = Count(rig, "ACK");

            // 2.5 s of 100 ms polls: with ':GW#' every fifth cycle a driver that did not cache would ask it 5 times more,
            // each a 2 s timeout that would also hold up the position polls
            await Task.Delay(2500);
            Count(rig, ":GW#").Should().Be(1, "the unanswered ':GW#' is cached for the connection");
            Count(rig, "ACK").Should().BeGreaterThan(acksAtConnect + 2, "the status polls use ACK instead");
            Count(rig, ":GR#").Should().BeGreaterThan(10, "position polls keep running every 100 ms, never held up by a timeout");
            rig.Telescope.LinkState.Should().Be(Lx200LinkState.Connected);
            rig.Link.ReconnectCount.Should().Be(0, "an unanswered ':GW#' is not a lost link");
            notes.Posted.Count.Should().Be(notesAtConnect, "polling is quiet: no notification per poll");
            notes.Posted.Should().NotContain(n => n.Kind == NotificationKind.Error);
            mount.IsAligned.Should().BeNull();

            // A new connection asks again (the firmware could have been flashed in between), once
            mount.Disconnect();
            await rig.ConnectTelescope();
            Count(rig, ":GW#").Should().Be(2, "once per connection");
            await Task.Delay(1200);
            Count(rig, ":GW#").Should().Be(2);
        }

        [Test]
        public async Task SiteDateAndTime_AreNeverWritten_WhileTheAlignmentIsUnknown_EvenWithTimeSyncOn() {
            // The real mount's clock read 00:02 on 01/01/01 (never set, indoors); its site was already right
            var options = Firmware40g();
            options.ClockErrorSeconds = -3600;
            using var rig = new Rig(options);
            rig.Profile.TelescopeSettings.TimeSync = true;
            using var notes = Rig.CaptureNotifications();

            var mount = await rig.ConnectTelescope();
            mount.SiteLatitude = 22.3;
            mount.SiteLongitude = 114.5;
            (await mount.SetMountClock()).Should().BeFalse("the clock is written only to a mount known to be unaligned");

            rig.Received.Should().NotContain(c => c.StartsWith(":SC", StringComparison.Ordinal) || c.StartsWith(":SL", StringComparison.Ordinal)
                || c.StartsWith(":SG", StringComparison.Ordinal) || c.StartsWith(":St", StringComparison.Ordinal) || c.StartsWith(":Sg", StringComparison.Ordinal),
                "with ':GW#' unanswered the driver cannot tell whether the mount is aligned, and a write after alignment shifts every goto");
            mount.SiteLatitude.Should().Be(22.25, "the mount's own site is reported");
            notes.Posted.Should().Contain(n => n.Message.Contains("does not say whether the mount is aligned"), "the refusal says why");
        }

        [Test]
        public async Task PulseGuide_UsesTheNativeMgPulse_ByDefault() {
            var options = Firmware40g();
            options.PulseGuide = PulseGuideBehaviour.EquatorialAxes;
            using var rig = new Rig(options);
            var mount = await rig.ConnectTelescope();
            var (_, dec0) = rig.Sim.BelievedRaDec;

            mount.PulseGuide(GuideDirections.guideNorth, 300);
            mount.IsPulseGuiding.Should().BeTrue();
            (await Rig.Eventually(() => !mount.IsPulseGuiding, TimeSpan.FromSeconds(5))).Should().BeTrue();

            rig.Received.Should().Contain(":Mgn0300#");
            rig.Received.Should().NotContain(c => c == ":Mn#" || c == ":Ms#" || c == ":Me#" || c == ":Mw#" || c.StartsWith(":MS", StringComparison.Ordinal),
                "neither host-timed moves nor goto offsets");
            var (_, dec1) = rig.Sim.BelievedRaDec;
            ((dec1 - dec0) * 3600).Should().BeApproximately(3.0, 0.1, "300 ms at the 10\"/s guide rate set at connect");
        }
    }
}
