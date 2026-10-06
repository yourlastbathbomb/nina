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
using NINA.Mac.Platform;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine.Test {

    /// <summary>
    /// The Real mount and focuser: NINA's TelescopeVM over the LX200 driver (NINA.Mac.Equipment.Lx200) and the Autostar II
    /// simulator on an in-memory cable. Port picker, connect, gotos in J2000 with the slew guard, sync, dithers through NINA's
    /// DirectGuider, tracking, soft park, the connection-lost state while the cable is out, and timed focuser moves.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class MountServiceTests {

        [Test]
        public async Task PortPicker_ListsTheSerialPorts_AndRemembersTheChoiceInTheProfile() {
            await using var rig = new EngineRig("mount ports");
            var mount = rig.Engine.Mount;
            mount.SelectPort(null);
            mount.PortName.Should().BeNull();

            mount.RefreshPorts();
            mount.AvailablePorts.Select(p => p.Path).Should().Equal("/dev/cu.Bluetooth-Incoming-Port", EngineRig.Port);
            mount.PortName.Should().Be(EngineRig.Port, "an empty choice takes the first USB serial adapter");

            rig.Ports.Clear();
            mount.RefreshPorts();
            mount.PortName.Should().Be(EngineRig.Port, "a saved port is kept while its adapter is unplugged");

            mount.SelectPort("/dev/cu.usbserial-OTHER");
            rig.Engine.Lx200.Telescope.Settings.PortPath.Should().Be("/dev/cu.usbserial-OTHER");
            mount.SelectPort(EngineRig.Port);
            await mount.ConnectAsync();
            mount.Invoking(m => m.SelectPort("/dev/cu.usbserial-OTHER")).Should().Throw<InvalidOperationException>();
        }

        [Test]
        public async Task Connect_BringsTheFocuserAndTheMountDither_AndReportsThePointing() {
            await using var rig = new EngineRig("mount connect");
            var mount = rig.Engine.Mount;
            await mount.ConnectAsync();

            mount.State.Should().Be(DeviceConnectionState.Connected);
            mount.IsSimulated.Should().BeFalse();
            mount.FocuserConnected.Should().BeTrue("the #1209 shares the mount's link");
            rig.Engine.Focuser.State.Should().Be(DeviceConnectionState.Connected);
            rig.Engine.Host.Guider.GuiderInfo.Connected.Should().BeTrue("NINA's DirectGuider dithers through the mount");
            (await EngineRig.Eventually(() => mount.Altitude.HasValue && mount.RightAscensionHours.HasValue, TimeSpan.FromSeconds(5))).Should().BeTrue();
            var (ra, dec) = rig.Sim.BelievedRaDec;
            var j2000 = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), Epoch.JNOW).Transform(Epoch.J2000);
            mount.RightAscensionHours.Should().BeApproximately(j2000.RA, 0.01, "the service reports J2000, the mount JNow");
            mount.DeclinationDegrees.Should().BeApproximately(j2000.Dec, 0.1);
            mount.Altitude.Should().BeInRange(0, 90);
        }

        [Test]
        public async Task Goto_InJ2000_ReachesTheTarget_AndTheSlewGuardRefusesTheHorizonAndTheKeyhole() {
            await using var rig = new EngineRig("mount goto");
            var mount = rig.Engine.Mount;
            await mount.ConnectAsync();
            var target = Sky.At(45, 150);

            await mount.SlewToAsync(target.RA, target.Dec);

            rig.Received.Should().Contain(c => c.StartsWith(":MS", StringComparison.Ordinal));
            var (ra, dec) = rig.Sim.BelievedRaDec;
            var jnow = target.Transform(Epoch.JNOW);
            ra.Should().BeApproximately(jnow.RA, 0.01, "the driver sends JNow from NINA's transform");
            dec.Should().BeApproximately(jnow.Dec, 0.05);

            var gotos = rig.Received.Count(c => c.StartsWith(":MS", StringComparison.Ordinal));
            var low = Sky.At(-10, 150);
            await FluentActions.Awaiting(() => mount.SlewToAsync(low.RA, low.Dec)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*below the horizon*");
            var high = Sky.At(80, 150);
            await FluentActions.Awaiting(() => mount.SlewToAsync(high.RA, high.Dec)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*keyhole*");
            rig.Received.Count(c => c.StartsWith(":MS", StringComparison.Ordinal)).Should().Be(gotos, "refused gotos never reach the mount");
        }

        [Test]
        public async Task Sync_Dither_Tracking_AndSoftPark_ReachTheMount() {
            await using var rig = new EngineRig("mount commands");
            var mount = rig.Engine.Mount;
            await mount.ConnectAsync();
            var target = Sky.At(40, 120);
            await mount.SlewToAsync(target.RA, target.Dec);

            await mount.SyncAsync(target.RA, target.Dec);
            rig.Received.Should().Contain(c => c.StartsWith(":CM", StringComparison.Ordinal));

            var before = rig.Received.Count;
            await mount.DitherAsync(5);
            rig.Received.Skip(before).Should().Contain(c => c.StartsWith(":Mg", StringComparison.Ordinal) || c.StartsWith(":Mn", StringComparison.Ordinal)
                || c.StartsWith(":Ms", StringComparison.Ordinal) || c.StartsWith(":Me", StringComparison.Ordinal) || c.StartsWith(":Mw", StringComparison.Ordinal),
                "DirectGuider's dither pulses reach the mount");

            mount.SetTracking(false);
            (await EngineRig.Eventually(() => !rig.Sim.Tracking, TimeSpan.FromSeconds(3))).Should().BeTrue();
            mount.SetTracking(true);
            (await EngineRig.Eventually(() => rig.Sim.Tracking, TimeSpan.FromSeconds(3))).Should().BeTrue();

            await mount.SoftParkAsync();
            (await EngineRig.Eventually(() => !rig.Sim.Tracking, TimeSpan.FromSeconds(3))).Should().BeTrue("the soft park stops tracking");
            rig.Received.Should().NotContain(c => c.StartsWith(":hP", StringComparison.Ordinal));

            await mount.DisconnectAsync();
            mount.State.Should().Be(DeviceConnectionState.Disconnected);
            rig.Engine.Focuser.State.Should().Be(DeviceConnectionState.Disconnected);
        }

        [Test]
        public async Task CableOut_ShowsLost_AndTheLinkComesBackByItself() {
            await using var rig = new EngineRig("mount cable");
            var mount = rig.Engine.Mount;
            var focuser = rig.Engine.Focuser;
            await mount.ConnectAsync();

            rig.Cable.Unplug();
            (await EngineRig.Eventually(() => mount.State == DeviceConnectionState.Lost, TimeSpan.FromSeconds(5))).Should().BeTrue("the driver's link goes to Reconnecting");
            mount.LastError.Should().Contain("reconnecting");
            focuser.State.Should().Be(DeviceConnectionState.Lost, "the focuser is driven through the mount");
            var target = Sky.At(45, 150);
            await FluentActions.Awaiting(() => mount.SlewToAsync(target.RA, target.Dec)).Should().ThrowAsync<DeviceLostException>();
            await FluentActions.Awaiting(() => focuser.MoveAsync(100)).Should().ThrowAsync<DeviceLostException>();

            rig.Cable.Replug();
            (await EngineRig.Eventually(() => mount.State == DeviceConnectionState.Connected, TimeSpan.FromSeconds(5))).Should().BeTrue("the driver reopens the port by itself");
            await mount.SlewToAsync(target.RA, target.Dec);
            rig.Received.Should().Contain(c => c.StartsWith(":MS", StringComparison.Ordinal));
        }

        [Test]
        public async Task Reconnect_WhileTheLinkIsDown_WaitsForTheLinkThenStartsOver() {
            await using var rig = new EngineRig("mount reconnect");
            var mount = rig.Engine.Mount;
            await mount.ConnectAsync();
            rig.Cable.Unplug();
            (await EngineRig.Eventually(() => mount.State == DeviceConnectionState.Lost, TimeSpan.FromSeconds(5))).Should().BeTrue();

            // The banner's Reconnect: the cable is back while it waits
            var reconnect = mount.ConnectAsync();
            await Task.Delay(300);
            rig.Cable.Replug();
            await reconnect;
            mount.State.Should().Be(DeviceConnectionState.Connected);
        }

        [Test]
        public async Task Focuser_TimedMovesAtTheChosenSpeed_AndSpeedChangesRecentre() {
            await using var rig = new EngineRig("focuser");
            await rig.Engine.Mount.ConnectAsync();
            var focuser = rig.Engine.Focuser;
            focuser.MaxStep.Should().Be(65000);
            focuser.Position.Should().Be(32500, "the virtual position starts in the middle");

            focuser.SetSpeed(3);
            focuser.Speed.Should().Be(3);
            var before = rig.Received.Count;
            await focuser.MoveAsync(400);
            focuser.Position.Should().Be(32900);
            var moves = rig.Received.Skip(before).ToList();
            moves.Should().Contain(":F3#", "the speed goes out before every move");
            moves.Should().Contain(c => c == ":F-#" || c == ":F+#");
            moves.Should().Contain(":FQ#");

            await focuser.MoveAsync(-250);
            focuser.Position.Should().Be(32650);
            focuser.SetSpeed(2);
            focuser.Position.Should().Be(32500, "positions only compare at one speed");
            await FluentActions.Awaiting(() => focuser.MoveAsync(40000)).Should().ThrowAsync<ArgumentOutOfRangeException>();
            focuser.Invoking(f => f.SetSpeed(5)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Test]
        public async Task Focuser_NeedsTheMount() {
            await using var rig = new EngineRig("focuser no mount");
            await FluentActions.Awaiting(() => rig.Engine.Focuser.MoveAsync(100)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*connect the mount*");
        }

        [Test]
        public async Task Connect_WithoutAnAnsweringMount_FailsWithAReason() {
            await using var rig = new EngineRig("mount silent");
            rig.Cable.Unplug();
            await FluentActions.Awaiting(() => rig.Engine.Mount.ConnectAsync()).Should().ThrowAsync<InvalidOperationException>();
            rig.Engine.Mount.State.Should().Be(DeviceConnectionState.Disconnected);
            rig.Engine.Mount.LastError.Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public async Task Disconnect_DuringAGoto_HaltsTheMountFirst() {
            await using var rig = new EngineRig("mount disconnect mid-goto");
            var mount = rig.Engine.Mount;
            await mount.ConnectAsync();
            rig.SimOptions.SlewSeconds = 8;
            var target = Sky.At(45, 150);

            var slew = mount.SlewToAsync(target.RA, target.Dec);
            (await EngineRig.Eventually(() => rig.Sim.Slewing, TimeSpan.FromSeconds(5))).Should().BeTrue("the goto is under way");
            var before = rig.Received.Count;
            await mount.DisconnectAsync();

            rig.Received.Skip(before).Should().Contain(":Q#", "a disconnect must not leave the mount slewing on its own");
            rig.Sim.Slewing.Should().BeFalse();
            mount.State.Should().Be(DeviceConnectionState.Disconnected);
            await FluentActions.Awaiting(() => slew).Should().ThrowAsync<Exception>("the goto was halted");
        }

        [Test]
        public async Task Shutdown_DuringSlewAndCentre_HaltsTheMount() {
            await using var rig = new EngineRig("mount shutdown mid-goto");
            await rig.ConnectAll();
            rig.SimOptions.SlewSeconds = 8;
            var target = Sky.At(45, 150);

            // As the Target screen starts it, then the app quits
            var centring = rig.Engine.Centring.SlewAndCentreAsync(target.RA, target.Dec);
            (await EngineRig.Eventually(() => rig.Sim.Slewing, TimeSpan.FromSeconds(5))).Should().BeTrue("the goto is under way");
            var before = rig.Received.Count;
            await rig.Engine.ShutdownAsync();

            rig.Received.Skip(before).Should().Contain(":Q#", "quitting must halt a running goto before the link closes");
            rig.Sim.Slewing.Should().BeFalse();
            await FluentActions.Awaiting(() => centring).Should().ThrowAsync<Exception>("the goto was halted");
        }

        [Test]
        public async Task CancellingSlewAndCentre_DuringTheGoto_HaltsTheMount() {
            await using var rig = new EngineRig("mount cancel mid-goto");
            await rig.ConnectAll();
            rig.SimOptions.SlewSeconds = 8;
            var target = Sky.At(45, 150);
            using var cts = new System.Threading.CancellationTokenSource();

            // The Target screen's Stop: cancel the token, then Abort
            var centring = rig.Engine.Centring.SlewAndCentreAsync(target.RA, target.Dec, null, cts.Token);
            (await EngineRig.Eventually(() => rig.Sim.Slewing, TimeSpan.FromSeconds(5))).Should().BeTrue("the goto is under way");
            cts.Cancel();
            rig.Engine.Mount.Abort();

            await FluentActions.Awaiting(() => centring).Should().ThrowAsync<OperationCanceledException>();
            rig.Received.Should().Contain(":Q#");
            (await EngineRig.Eventually(() => !rig.Sim.Slewing, TimeSpan.FromSeconds(3))).Should().BeTrue();
            rig.Engine.Mount.State.Should().Be(DeviceConnectionState.Connected, "a stopped goto leaves the mount connected");
        }
    }
}
