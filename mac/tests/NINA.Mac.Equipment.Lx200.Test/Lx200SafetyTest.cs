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
using NINA.Mac.Lx200;
using NINA.Mac.Lx200.Sim;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200.Test {

    /// <summary>
    /// "Nothing may leave the telescope or the focuser moving": the review cases where a motion could outlive the driver's
    /// intent. A halt queued just before the link closes, a motion start whose outcome is unknown (the cable went as it was
    /// written, or a stray byte came back in its NAK window), a serial node that hangs up without closing (writes time out
    /// through System.IO.Ports on macOS), and a halt the mount refuses with a NAK that arrives late.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Lx200SafetyTest {

        private static Lx200Link OpenLink(SimCable cable, Lx200LinkOptions options = null) {
            var link = new Lx200Link(Rig.Port, cable.Open, options ?? new Lx200LinkOptions {
                ReconnectInterval = TimeSpan.FromMilliseconds(200),
                WaitForReconnect = TimeSpan.FromSeconds(3),
                ReconnectGiveUp = null
            });
            link.Open();
            return link;
        }

        /// <summary>Runs <paramref name="action"/> once, on a pool thread, when the simulator receives <paramref name="command"/>.</summary>
        private static void OnceWhenReceived(AutostarSimulator sim, string command, Action action, bool inline = false) {
            var fired = 0;
            void Handler(string c) {
                if (c != command || Interlocked.Exchange(ref fired, 1) != 0) {
                    return;
                }
                sim.CommandReceived -= Handler;
                if (inline) {
                    action();
                } else {
                    _ = Task.Run(action);
                }
            }
            sim.CommandReceived += Handler;
        }

        // ---------------------------------------------------------------------------------------------
        // A halt queued just before the link closes
        // ---------------------------------------------------------------------------------------------

        [Test]
        public async Task HaltQueuedWhileATransactionIsOnTheWire_GoesOutBeforeThePortCloses() {
            using var cable = new SimCable(new SimOptions(), turnaround: TimeSpan.FromMilliseconds(250));
            var link = OpenLink(cable);
            var inFlight = link.SendAsync(":GVP#");
            (await Rig.Eventually(() => cable.Sim.ReceivedCommands.Contains(":GVP#"), TimeSpan.FromSeconds(2))).Should().BeTrue();

            var halt = link.SendAsync(":Q#", Lx200Lane.Stop);
            link.Dispose();   // the worker is still waiting 250 ms for the ':GVP#' reply

            cable.Sim.ReceivedCommands.Should().Contain(":Q#", "a halt accepted by the link goes out before the port closes");
            (await halt).IsOk.Should().BeTrue();
            (await inFlight).Value.Should().Be("LX2001");
        }

        [Test]
        public async Task StopSlew_ThenDisconnectAtOnce_TheGotoIsStillAborted() {
            using var rig = new Rig(new SimOptions { SlewSeconds = 8, PlanetaryUpdateSeconds = 0.2 }, turnaround: TimeSpan.FromMilliseconds(300));
            var mount = await rig.ConnectTelescope();
            var (ra, dec) = rig.Sim.BelievedRaDec;
            var slew = mount.SlewToCoordinates(new Coordinates(Angle.ByHours(Lx200Astro.Wrap(ra + 0.5, 24)), Angle.ByDegree(dec + 8), Epoch.JNOW), CancellationToken.None);
            (await Rig.Eventually(() => rig.Sim.Slewing, TimeSpan.FromSeconds(10))).Should().BeTrue();

            // the user presses Stop and then Disconnect while a ':D#' is on the wire (the goto wait polls it)
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            OnceWhenReceived(rig.Sim, ":D#", () => {
                mount.StopSlew();
                mount.Disconnect();
                done.TrySetResult();
            });
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));

            rig.Sim.ReceivedCommands.Should().Contain(":Q#");
            rig.Sim.Slewing.Should().BeFalse("the abort reached the mount before the port closed");
            (await slew.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        }

        // ---------------------------------------------------------------------------------------------
        // Motion starts whose outcome is unknown
        // ---------------------------------------------------------------------------------------------

        [Test]
        public async Task FocuserStart_CableLostAsItWentOut_TheHaltIsOwed_AndGoesOutFirstOnReplug() {
            using var rig = new Rig();
            var focuser = await rig.ConnectFocuser();
            using var notes = Rig.CaptureNotifications();
            OnceWhenReceived(rig.Sim, ":F-#", rig.Cable.Unplug, inline: true);   // the start reached the motor, then the cable went

            var move = focuser.Move(focuser.Position + 4000, CancellationToken.None);
            (await Rig.Eventually(() => rig.Link.State == Lx200LinkState.Reconnecting, TimeSpan.FromSeconds(3))).Should().BeTrue();
            rig.Sim.FocuserMoving.Should().BeTrue("the motor runs: nothing can stop it while the cable is out");
            rig.Link.OwedStops.Should().Contain(":FQ#", "a start that may have reached the motor owes its halt");
            await Task.Delay(400);
            var received = rig.Received.Count;

            rig.Cable.Replug();
            (await Rig.Eventually(() => !rig.Sim.FocuserMoving, TimeSpan.FromSeconds(5))).Should().BeTrue("the owed ':FQ#' stopped the motor");
            rig.Received.Skip(received).Take(3).Should().Contain(":FQ#", "the owed halt is the first command after the handshake");
            await move.WaitAsync(TimeSpan.FromSeconds(10));
            var last = focuser.History.Last();
            last.Completed.Should().BeFalse();
            focuser.Position.Should().BeGreaterThan(32500, "the position counts the time until the owed halt (an estimate)");
            notes.Posted.Should().Contain(n => n.Message.Contains("position is an estimate"));
        }

        [Test]
        public async Task PulseStart_CableLostAsItWentOut_TheHaltIsOwed_AndGoesOutFirstOnReplug() {
            using var rig = new Rig(configure: s => s.PulseStrategy = Lx200PulseStrategy.HostTimedMove);
            var mount = await rig.ConnectTelescope();
            OnceWhenReceived(rig.Sim, ":Mn#", rig.Cable.Unplug, inline: true);

            mount.PulseGuide(GuideDirections.guideNorth, 300);
            (await Rig.Eventually(() => rig.Link.State == Lx200LinkState.Reconnecting, TimeSpan.FromSeconds(3))).Should().BeTrue();
            rig.Sim.AnyAxisMotion.Should().BeTrue();
            rig.Link.OwedStops.Should().Contain(":Qn#");
            await Task.Delay(600);
            rig.Sim.AnyAxisMotion.Should().BeTrue("a 300 ms pulse whose halt cannot be sent runs on");

            rig.Cable.Replug();
            (await Rig.Eventually(() => !rig.Sim.AnyAxisMotion, TimeSpan.FromSeconds(5))).Should().BeTrue("the owed ':Qn#' went out after the handshake");
            (await Rig.Eventually(() => rig.Link.OwedStops.Count == 0, TimeSpan.FromSeconds(2))).Should().BeTrue();
        }

        [Test]
        public async Task FocuserStart_AnsweredByAStrayByte_IsHaltedAtOnce_AndThePositionIsEstimated() {
            using var rig = new Rig();
            var focuser = await rig.ConnectFocuser();
            using var notes = Rig.CaptureNotifications();
            OnceWhenReceived(rig.Sim, ":F-#", () => rig.Cable.Inject((byte)'#'), inline: true);   // line noise in the NAK window
            var asked = rig.Link.UtcNow;

            var move = focuser.Move(focuser.Position + 5000, CancellationToken.None);
            await move.WaitAsync(TimeSpan.FromSeconds(4));

            rig.Sim.FocuserMoving.Should().BeFalse("a start answered by a stray byte may be running: it is halted at once, not after 5 s");
            var tx = rig.Transmitted().Where(t => t.Utc >= asked).ToList();
            var start = tx.First(t => t.Text == ":F-#");
            var halt = tx.First(t => t.Text == ":FQ#" && t.Utc > start.Utc);
            (halt.Utc - start.Utc).TotalMilliseconds.Should().BeLessThan(1000, "the halt follows the resync of the garbled start");
            var last = focuser.History.Last();
            last.Completed.Should().BeFalse();
            focuser.Position.Should().BeInRange(32500, 32500 + 1000, "the position counts the time until that halt");
            rig.Link.OwedStops.Should().BeEmpty();
            notes.Posted.Should().Contain(n => n.Message.Contains("position is an estimate"));
        }

        [Test]
        public async Task PulseStart_AnsweredByAStrayByte_IsHaltedAtOnce() {
            using var rig = new Rig(configure: s => s.PulseStrategy = Lx200PulseStrategy.HostTimedMove);
            var mount = await rig.ConnectTelescope();
            OnceWhenReceived(rig.Sim, ":Mn#", () => rig.Cable.Inject((byte)'#'), inline: true);
            var received = rig.Received.Count;

            mount.PulseGuide(GuideDirections.guideNorth, 3000);
            (await Rig.Eventually(() => rig.Received.Skip(received).Contains(":Mn#"), TimeSpan.FromSeconds(2))).Should().BeTrue();
            (await Rig.Eventually(() => !rig.Sim.AnyAxisMotion, TimeSpan.FromSeconds(1.5))).Should().BeTrue("halted at once, not after 3 s (or never)");
            rig.Received.Skip(received).Should().Contain(":Qn#");
            (await Rig.Eventually(() => !mount.IsPulseGuiding, TimeSpan.FromSeconds(2))).Should().BeTrue();
            (await Rig.Eventually(() => rig.Link.OwedStops.Count == 0, TimeSpan.FromSeconds(2))).Should().BeTrue("the halt was accepted (after its NAK window)");
        }

        [Test]
        public async Task MotionStart_RefusedWithNak_OwesNoHalt() {
            using var cable = new SimCable(new SimOptions { NakEveryNth = 1 });   // every command refused: nothing moves
            using var link = OpenLink(cable);
            var reply = await link.SendAsync(":Mn#", Lx200Lane.Timed);
            reply.Status.Should().Be(ReplyStatus.Nak);
            cable.Sim.AnyAxisMotion.Should().BeFalse();
            link.OwedStops.Should().BeEmpty("a refused start moved nothing");
        }

        // ---------------------------------------------------------------------------------------------
        // A serial node that hangs up without closing
        // ---------------------------------------------------------------------------------------------

        [Test]
        public async Task SerialNodeHungUp_WritesTimeOut_AndTheLinkCountsAsLost_WithTheHaltOwed() {
            if (!PseudoTerminal.IsSupported) {
                Assert.Ignore("openpty needs macOS or Linux");
            }
            var pty = PseudoTerminal.Open();
            var sim = new AutostarSimulator(pty.Master, new SimOptions());
            var path = pty.SlavePath;
            var opens = 0;
            // the first open is the real System.IO.Ports path; afterwards the node counts as gone (a reopen could reach another
            // process's pseudo-terminal under the same name)
            Func<Lx200Trace, ILx200Transport> opener = trace => Interlocked.Increment(ref opens) == 1
                ? Lx200Serial.Open(path, trace)
                : throw new IOException($"{path}: no such device (hung up)");
            using var link = new Lx200Link(path, opener, new Lx200LinkOptions {
                ReconnectInterval = TimeSpan.FromMilliseconds(300),
                WaitForReconnect = TimeSpan.FromSeconds(3),
                ReconnectGiveUp = null
            });
            try {
                link.Open();
                (await link.SendAsync(":GVP#")).Value.Should().Be("LX2001");
                (await link.SendAsync(":Mn#", Lx200Lane.Timed)).IsOk.Should().BeTrue();
                link.OwedStops.Should().Equal(":Qn#");

                sim.Dispose();   // closes the master side: the client's node is hung up but stays open
                var halt = link.SendAsync(":Qn#", Lx200Lane.Stop);
                Func<Task> sending = () => halt.WaitAsync(TimeSpan.FromSeconds(15));
                await sending.Should().ThrowAsync<Lx200DisconnectedException>("the halt cannot be written to a hung-up node");

                link.State.Should().Be(Lx200LinkState.Reconnecting, "a transport that fails a write is a lost link, not a failed command");
                link.OwedStops.Should().Contain(":Qn#", "the halt goes out first when the mount answers again");
                link.Trace.Snapshot().Should().Contain(e => e.Text != null && e.Text.StartsWith("LINK LOST", StringComparison.Ordinal));
                Func<Task> poll = () => link.SendAsync(":GR#", Lx200Lane.Poll);
                await poll.Should().ThrowAsync<Lx200DisconnectedException>("polls fail at once while the link is down");
            } finally {
                sim.Dispose();
                pty.Dispose();
            }
        }

        [Test]
        public async Task PortNodeVanishes_WhileTheLinkIsIdle_TheLinkCountsAsLost_AndTheOwedHaltGoesOutWhenItIsBack() {
            using var cable = new SimCable(new SimOptions());
            var node = Path.Combine(TestHost.DataRoot, $"cu.usbserial-NODE{Guid.NewGuid():N}");
            File.WriteAllText(node, string.Empty);
            using var link = new Lx200Link(node, cable.Open, new Lx200LinkOptions {
                WatchPortNode = true,
                ReconnectInterval = TimeSpan.FromMilliseconds(200),
                ReconnectGiveUp = null
            });
            link.Open();
            (await link.SendAsync(":Mn#", Lx200Lane.Timed)).IsOk.Should().BeTrue();

            File.Delete(node);   // the adapter is pulled; the handle that is still open may report nothing at all
            (await Rig.Eventually(() => link.State == Lx200LinkState.Reconnecting, TimeSpan.FromSeconds(1))).Should().BeTrue("an idle link checks its node every 100 ms");
            link.Trace.Snapshot().Should().Contain(e => e.Text != null && e.Text.Contains("no longer exists"));
            link.OwedStops.Should().Contain(":Qn#");
            cable.Sim.AnyAxisMotion.Should().BeTrue();

            File.WriteAllText(node, string.Empty);   // plugged back in
            (await Rig.Eventually(() => link.State == Lx200LinkState.Connected && link.OwedStops.Count == 0, TimeSpan.FromSeconds(3))).Should().BeTrue();
            cable.Sim.AnyAxisMotion.Should().BeFalse("the owed halt went out first");
        }

        // ---------------------------------------------------------------------------------------------
        // NAK retries of other transactions
        // ---------------------------------------------------------------------------------------------

        [Test]
        public async Task PollsTheMountRefuses_DoNotDelayATimedHalt() {
            // 9600 baud, 15 ms turnaround, the mount polled every 100 ms, and every position read answered NAK (busy). A NAKed
            // read is retried 3 times 100 ms apart; those retries must not hold the link while a focuser halt is due.
            using var rig = new Rig(configure: s => s.PollIntervalMs = 100, baudRate: 9600, turnaround: TimeSpan.FromMilliseconds(15));
            await rig.ConnectTelescope();
            var focuser = await rig.ConnectFocuser();
            rig.SimOptions.NakWhen = c => c is ":GR#" or ":GD#" or ":GA#" or ":GZ#";   // busy for every position read
            try {
                await Task.Delay(300);
                var sent = new List<double>();
                for (var i = 0; i < 5; i++) {
                    var asked = rig.Link.UtcNow;
                    await focuser.Move(focuser.Position + 400, CancellationToken.None);
                    var tx = rig.Transmitted().Where(t => t.Utc >= asked).ToList();
                    var start = tx.First(t => t.Text == ":F-#");
                    var halt = tx.First(t => t.Text == ":FQ#" && t.Utc > start.Utc);
                    sent.Add((halt.Utc - start.Utc).TotalMilliseconds);
                }
                TestContext.Out.WriteLine("400 ms moves with every position read NAKed, halt sent after (ms, link clock): " +
                                          string.Join(", ", sent.Select(ms => ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))));
                rig.Sim.Log.Count(e => e.Command == ":GR#").Should().BeGreaterThan(10, "the mount kept being polled");
                rig.Link.Trace.Snapshot().Should().Contain(e => e.Kind == TraceKind.Note && e.Text.Contains("NAK"));
                sent.Should().OnlyContain(ms => ms >= 399 && ms <= 415, "a refused read waits for its retry behind the timed halt");
            } finally {
                rig.SimOptions.NakWhen = null;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // A halt refused with a late NAK
        // ---------------------------------------------------------------------------------------------

        [Test]
        public async Task HaltRefusedWithALateNak_IsSeenAsRefused_StaysOwed_AndIsReportedLoudly() {
            // The NAK leaves the mount 55 ms after the halt: Autostar busy 10 ms (P07) + FTDI latency 16 ms + a loaded Mac. The
            // driver's 40 ms window for motion commands would miss it and count the halt as accepted.
            using var cable = new SimCable(new SimOptions { HaltsNaked = true }, turnaround: TimeSpan.FromMilliseconds(55));
            using var link = OpenLink(cable, new Lx200LinkOptions {
                HaltRetryFor = TimeSpan.FromMilliseconds(500),
                ReconnectInterval = TimeSpan.FromMilliseconds(200),
                ReconnectGiveUp = null
            });
            using var notes = Rig.CaptureNotifications();
            (await link.SendAsync(":RG#", Lx200Lane.Timed)).IsOk.Should().BeTrue();
            (await link.SendAsync(":Mn#", Lx200Lane.Timed)).IsOk.Should().BeTrue();
            cable.Sim.AnyAxisMotion.Should().BeTrue();

            var reply = await link.SendAsync(":Qn#", Lx200Lane.Stop);

            reply.Status.Should().Be(ReplyStatus.Nak, "the late NAK is still the halt's answer");
            cable.Sim.AnyAxisMotion.Should().BeTrue("the mount refused every halt");
            link.OwedStops.Should().Contain(":Qn#", "a refused halt is still owed");
            notes.Posted.Should().Contain(n => n.Message.Contains("refused the halt"));
            link.Trace.Snapshot().Should().NotContain(e => e.Kind == TraceKind.Discard && e.Bytes.Contains((byte)0x15),
                "no NAK is left over to be dropped as stale input");
        }
    }
}
