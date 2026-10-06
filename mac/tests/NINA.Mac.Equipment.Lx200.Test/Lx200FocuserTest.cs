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
using NINA.Mac.Lx200;
using NINA.Mac.Lx200.Sim;
using NUnit.Framework;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200.Test {

    /// <summary>
    /// <see cref="Lx200Focuser"/>: the virtual millisecond position (AscomFocuser.cs:42-55, 169-174), the speed before every move,
    /// host-timed and mount-timed moves, halts, backlash, direction, and sharing the mount's link. The simulator's #1209 runs at
    /// 10 µm/s at speed 2 (an assumed figure), so the drawtube's travel shows how long the motor really ran.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Lx200FocuserTest {

        private const double UmPerMsAtSpeed2 = 10.0 / 1000.0;

        /// <summary>
        /// Motor time as the driver ran it: milliseconds between a start command and the next ":FQ#" leaving the Mac, on the
        /// link's monotonic clock (TX trace). This is what the driver controls; the simulator's receive times add the emulated
        /// cable's thread hops on top.
        /// </summary>
        private static double LinkRunMs(Rig rig, string startCommand, DateTime sinceUtc) {
            var tx = rig.Transmitted().Where(t => t.Utc >= sinceUtc).ToList();
            var start = tx.First(t => t.Text == startCommand);
            var stop = tx.First(t => t.Text == ":FQ#" && t.Utc > start.Utc);
            return (stop.Utc - start.Utc).TotalMilliseconds;
        }

        /// <summary>The same interval as the simulated mount received it (its log), for coarse checks only.</summary>
        private static double SimRunMs(Rig rig, string startCommand, int fromLogIndex) {
            var log = rig.SimLog.Skip(fromLogIndex).ToList();
            var start = log.First(e => e.Command == startCommand);
            var stop = log.First(e => e.Command == ":FQ#" && e.Utc >= start.Utc);
            return (stop.Utc - start.Utc).TotalMilliseconds;
        }

        /// <summary>How far the simulator's receive times may stray from the link's send times: pump threads of the emulated cable, under load.</summary>
        private const double CableJitterMs = 50;

        [Test]
        public async Task Connect_StartsAtHalfOfMaxStep_LocksTheSpeed_AndReadsTheTemperature() {
            using var rig = new Rig();
            rig.Sim.FocusSpeed.Should().Be(4, "the handbox left the fastest speed");
            var focuser = await rig.ConnectFocuser();

            focuser.Connected.Should().BeTrue();
            focuser.MaxStep.Should().Be(65000);
            focuser.MaxIncrement.Should().Be(65000);
            focuser.Position.Should().Be(32500, "MaxStep/2, as NINA does for relative ASCOM focusers");
            rig.Sim.FocusSpeed.Should().Be(2, "':F2#' at connect");
            focuser.Temperature.Should().Be(21.5, "':fT#' answered +21.500");
            double.IsNaN(focuser.StepSize).Should().BeTrue();
            focuser.TempCompAvailable.Should().BeFalse();
            focuser.IsMoving.Should().BeFalse();
        }

        [Test]
        public async Task Move_Outward_RunsTheDifferenceInMilliseconds_AndLandsExactlyOnTheTarget() {
            using var rig = new Rig();
            var focuser = await rig.ConnectFocuser();
            var um0 = rig.Sim.FocuserPositionUm;
            var log = rig.SimLog.Count;
            var asked = rig.Link.UtcNow;

            var move = focuser.Move(focuser.Position + 800, CancellationToken.None);
            (await Rig.Eventually(() => focuser.IsMoving, TimeSpan.FromSeconds(1))).Should().BeTrue();
            await move;

            focuser.Position.Should().Be(33300, "Position is set to the target exactly, so FocuserVM's 'while Position != target' ends");
            focuser.IsMoving.Should().BeFalse();
            var commands = rig.SimLog.Skip(log).Select(e => e.Command).ToList();
            commands.Should().ContainInOrder(":F2#", ":F-#", ":FQ#", ":FQ#");
            var ran = LinkRunMs(rig, ":F-#", asked);
            ran.Should().BeInRange(795, 830, "800 ms of motor time");
            var simRan = SimRunMs(rig, ":F-#", log);
            simRan.Should().BeApproximately(ran, CableJitterMs, "the mount saw the same interval, give or take the emulated cable");
            (rig.Sim.FocuserPositionUm - um0).Should().BeApproximately(simRan * UmPerMsAtSpeed2, 0.05, "the drawtube went outward for as long as the simulated motor ran");
            focuser.History.Last().RanMs.Should().BeApproximately(800, 30);
        }

        [Test]
        public async Task Move_Inward_UsesFPlus_AndTheSpeedIsSentBeforeEveryMove() {
            using var rig = new Rig();
            var focuser = await rig.ConnectFocuser();
            var um0 = rig.Sim.FocuserPositionUm;
            var log = rig.SimLog.Count;

            await focuser.Move(focuser.Position - 300, CancellationToken.None);
            rig.Link.Send(":F4#");   // someone turns the speed up on the handbox between moves
            await focuser.Move(focuser.Position - 300, CancellationToken.None);

            focuser.Position.Should().Be(31900);
            var commands = rig.SimLog.Skip(log).Select(e => e.Command).ToList();
            commands.Should().ContainInOrder(":F2#", ":F+#", ":FQ#", ":F4#", ":F2#", ":F+#", ":FQ#");
            rig.Sim.FocusSpeed.Should().Be(2);
            (rig.Sim.FocuserPositionUm - um0).Should().BeApproximately(-600 * UmPerMsAtSpeed2, 0.6, "inward, always at speed 2");
        }

        [Test]
        public async Task MountPulse_SendsOneFpCommand_PerMove() {
            using var rig = new Rig(configure: s => s.FocusMethod = Lx200FocusMethod.MountPulse);
            var focuser = await rig.ConnectFocuser();
            var um0 = rig.Sim.FocuserPositionUm;
            var log = rig.SimLog.Count;

            await focuser.Move(focuser.Position + 500, CancellationToken.None);
            await focuser.Move(focuser.Position - 250, CancellationToken.None);

            focuser.Position.Should().Be(32750);
            var commands = rig.SimLog.Skip(log).Select(e => e.Command).ToList();
            commands.Should().ContainInOrder(":F2#", ":FP-0500#", ":F2#", ":FP+0250#");
            commands.Should().NotContain(c => c == ":F+#" || c == ":F-#", "the mount times ':FP' itself");
            // the simulator's own 150 ms backlash after the reversal eats part of the inward pulse
            (rig.Sim.FocuserPositionUm - um0).Should().BeApproximately((500 - 100) * UmPerMsAtSpeed2, 0.05);
        }

        [Test]
        public async Task Halt_StopsAtOnce_AndThePositionCountsWhatRan() {
            using var rig = new Rig();
            var focuser = await rig.ConnectFocuser();
            var log = rig.SimLog.Count;
            var asked = rig.Link.UtcNow;

            var move = focuser.Move(focuser.Position + 5000, CancellationToken.None);
            await Task.Delay(600);
            focuser.Halt();
            await move.WaitAsync(TimeSpan.FromSeconds(2));

            var ran = LinkRunMs(rig, ":F-#", asked);
            ran.Should().BeInRange(450, 650, "halted about 600 ms after the move was asked for (the speed command and the start went first)");
            SimRunMs(rig, ":F-#", log).Should().BeApproximately(ran, CableJitterMs);
            focuser.Position.Should().BeCloseTo(32500 + (int)Math.Round(ran), 5);
            focuser.History.Last().Completed.Should().BeFalse();
            rig.Sim.FocuserMoving.Should().BeFalse();
            rig.Link.OwedStops.Should().BeEmpty();
        }

        [Test]
        public async Task Cancellation_StopsAtOnce_AndThePositionCountsWhatRan() {
            using var rig = new Rig();
            var focuser = await rig.ConnectFocuser();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

            await focuser.Move(focuser.Position - 4000, cts.Token);

            focuser.Position.Should().BeInRange(32500 - 600, 32500 - 300);
            rig.Sim.FocuserMoving.Should().BeFalse();
        }

        [Test]
        public async Task Backlash_AddsMotorTimeOnAReversal_ThatTheVirtualPositionDoesNotCount() {
            using var rig = new Rig(configure: s => s.FocuserBacklashMs = 150);
            var focuser = await rig.ConnectFocuser();
            var log = rig.SimLog.Count;
            var first = rig.Link.UtcNow;

            await focuser.Move(32800, CancellationToken.None);
            var reversal = rig.SimLog.Count;
            var second = rig.Link.UtcNow;
            await focuser.Move(32500, CancellationToken.None);

            focuser.Position.Should().Be(32500);
            LinkRunMs(rig, ":F-#", first).Should().BeInRange(295, 330, "the first move has no reversal");
            LinkRunMs(rig, ":F+#", second).Should().BeInRange(445, 480, "300 ms plus 150 ms of backlash");
            SimRunMs(rig, ":F-#", log).Should().BeApproximately(LinkRunMs(rig, ":F-#", first), CableJitterMs);
            SimRunMs(rig, ":F+#", reversal).Should().BeApproximately(LinkRunMs(rig, ":F+#", second), CableJitterMs);
            focuser.History.Select(h => h.RequestedMs).Should().Equal(300, 450);
        }

        [Test]
        public async Task Reverse_SwapsTheMotorDirection() {
            using var rig = new Rig(configure: s => s.FocuserReverse = true);
            var focuser = await rig.ConnectFocuser();
            var log = rig.SimLog.Count;
            await focuser.Move(focuser.Position + 200, CancellationToken.None);
            rig.SimLog.Skip(log).Select(e => e.Command).Should().Contain(":F+#").And.NotContain(":F-#");
        }

        [Test]
        public async Task Move_ClampsToTheVirtualTravel() {
            using var rig = new Rig(configure: s => s.FocuserMaxStep = 2000);
            var focuser = await rig.ConnectFocuser();
            focuser.Position.Should().Be(1000);
            await focuser.Move(-500, CancellationToken.None);
            focuser.Position.Should().Be(0);
            focuser.Action("Lx200.RecenterPosition", "").Should().Be("1000");
        }

        [Test]
        public async Task MountAndFocuser_ShareOneLink_AndTheFocuserMovesWhileTheMountIsPolled() {
            using var rig = new Rig(configure: s => s.PollIntervalMs = 100, baudRate: 9600, turnaround: TimeSpan.FromMilliseconds(15));
            await rig.ConnectTelescope();
            var focuser = await rig.ConnectFocuser();
            rig.Cable.Opens.Should().Be(1, "one port, one link");
            rig.Link.RefCount.Should().Be(2);
            var log = rig.SimLog.Count;

            var asked = rig.Link.UtcNow;
            await focuser.Move(focuser.Position + 400, CancellationToken.None);
            // on the link's own clock: when the start and the halt left the Mac
            var tx = rig.Transmitted().Where(t => t.Utc >= asked).ToList();
            var start = tx.First(t => t.Text == ":F-#");
            var halt = tx.First(t => t.Text == ":FQ#" && t.Utc > start.Utc);
            var sent = (halt.Utc - start.Utc).TotalMilliseconds;
            // at the mount (the simulator's receive times; the emulated cable adds its pump threads' jitter)
            var ran = SimRunMs(rig, ":F-#", log);
            TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"400 ms move under 100 ms polling at 9600 baud: halt sent {sent:0.0} ms after the start, mount saw {ran:0.0} ms"));
            sent.Should().BeInRange(399, 415, "the halt is a timed transaction: polls cannot delay it");
            ran.Should().BeApproximately(sent, CableJitterMs, "the mount saw the same interval, give or take the emulated cable");
            rig.SimLog.Skip(log).Should().Contain(e => e.Command == ":GR#", "the mount kept being polled meanwhile");

            rig.Telescope.Disconnect();
            rig.Link.Should().NotBeNull("the focuser still holds the link");
            await focuser.Move(focuser.Position - 100, CancellationToken.None);
            focuser.Disconnect();
            rig.Link.Should().BeNull("closed with its last device");
        }

        [Test]
        public async Task CableUnpluggedDuringAMove_TheHaltGoesOutOnReplug_AndThePositionIsEstimated() {
            using var rig = new Rig();
            var focuser = await rig.ConnectFocuser();
            using var notes = Rig.CaptureNotifications();
            var log = rig.SimLog.Count;
            var asked = rig.Link.UtcNow;

            var move = focuser.Move(focuser.Position + 5000, CancellationToken.None);
            await Task.Delay(400);
            rig.Cable.Unplug();
            await Task.Delay(800);
            rig.Sim.FocuserMoving.Should().BeTrue("nothing can stop the motor while the cable is out");
            rig.Cable.Replug();
            await move.WaitAsync(TimeSpan.FromSeconds(6));

            rig.Sim.FocuserMoving.Should().BeFalse("the owed ':FQ#' went out first after the reconnect");
            var ran = LinkRunMs(rig, ":F-#", asked);   // from the start to the owed ':FQ#' sent after the reconnect
            ran.Should().BeInRange(1100, 2500);
            SimRunMs(rig, ":F-#", log).Should().BeApproximately(ran, CableJitterMs);
            focuser.Position.Should().BeCloseTo(32500 + (int)Math.Round(ran), 60, "the position counts the time until the owed halt");
            focuser.History.Last().Completed.Should().BeFalse();
            notes.Posted.Should().Contain(n => n.Message.Contains("position is an estimate"));
            await focuser.Move(focuser.Position - 200, CancellationToken.None);
            focuser.History.Last().Completed.Should().BeTrue("the focuser works again after the reconnect");
        }
    }
}
