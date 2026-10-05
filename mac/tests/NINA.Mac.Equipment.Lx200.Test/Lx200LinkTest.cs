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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200.Test {

    /// <summary>
    /// The shared serial link on its own: priority lanes, coalesced polls, timed sends, NAK retry, resync, the byte trace,
    /// the blocklist, and reconnecting after the USB adapter is pulled. The latency cases run the cable at 9600 baud with a
    /// 15 ms turnaround, so a transaction takes about as long as on the real mount (RIM Table 5).
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Lx200LinkTest {

        private static Lx200Link OpenLink(SimCable cable, Lx200LinkOptions options = null) {
            var link = new Lx200Link(Rig.Port, cable.Open, options ?? new Lx200LinkOptions {
                ReconnectInterval = TimeSpan.FromMilliseconds(200),
                WaitForReconnect = TimeSpan.FromSeconds(3),
                ReconnectGiveUp = null
            });
            link.Open();
            return link;
        }

        private static List<(DateTime Utc, string Text)> Tx(Lx200Link link) =>
            link.Trace.Snapshot().Where(e => e.Kind == TraceKind.Tx).Select(e => (e.Utc, System.Text.Encoding.Latin1.GetString(e.Bytes))).ToList();

        [Test]
        public async Task StopAndTimed_JumpAheadOfQueuedPolls() {
            using var cable = new SimCable(new SimOptions(), baudRate: 9600, turnaround: TimeSpan.FromMilliseconds(15));
            using var link = OpenLink(cable);
            var polls = Enumerable.Range(0, 30).Select(_ => link.SendAsync(":GVP#", Lx200Lane.Poll)).ToList();
            await Task.Delay(60);   // let the first poll get onto the wire
            var queuedBefore = link.QueueLength(Lx200Lane.Poll);
            var stopAsked = link.UtcNow;
            var stop = link.SendAsync(":Qn#", Lx200Lane.Stop);
            var timed = link.SendAsync(":RG#", Lx200Lane.Timed);
            var command = link.SendAsync(":GVN#", Lx200Lane.Command);
            await Task.WhenAll(polls.Cast<Task>().Concat(new Task[] { stop, timed, command }));

            queuedBefore.Should().BeGreaterThan(20, "the 30 polls were still waiting");
            var tx = Tx(link);
            var stopAt = tx.FindIndex(t => t.Text == ":Qn#");
            var timedAt = tx.FindIndex(t => t.Text == ":RG#");
            var commandAt = tx.FindIndex(t => t.Text == ":GVN#");
            var lastPollAt = tx.FindLastIndex(t => t.Text == ":GVP#");
            stopAt.Should().BeLessThan(timedAt, "Stop lane before Timed lane");
            timedAt.Should().BeLessThan(commandAt, "Timed lane before Command lane");
            commandAt.Should().BeLessThan(lastPollAt, "Command lane before the polls");
            tx.Take(stopAt).Count(t => t.Text == ":GVP#").Should().BeLessThanOrEqualTo(2, "only the poll already on the wire went before the halt");
            var latency = (tx[stopAt].Utc - stopAsked).TotalMilliseconds;
            TestContext.Out.WriteLine($"halt latency with {queuedBefore} polls queued: {latency:0} ms");
            latency.Should().BeLessThan(150, "the halt waits at most for the one transaction on the wire");
            (await stop).Status.Should().Be(ReplyStatus.NoReplyExpected);
            (await command).Value.Should().Be("4.2g");
        }

        [Test]
        public async Task Polls_WithTheSameKey_AreCoalesced() {
            using var cable = new SimCable(new SimOptions(), baudRate: 9600, turnaround: TimeSpan.FromMilliseconds(15));
            using var link = OpenLink(cable);
            var blocker = link.SendAsync(":GVN#", Lx200Lane.Command);
            var a = link.SendAsync(":GR#", Lx200Lane.Poll, coalesceKey: ":GR#");
            var b = link.SendAsync(":GR#", Lx200Lane.Poll, coalesceKey: ":GR#");
            var c = link.SendAsync(":GR#", Lx200Lane.Poll, coalesceKey: ":GR#");
            await Task.WhenAll(blocker, a, b, c);

            b.Should().BeSameAs(a);
            c.Should().BeSameAs(a);
            cable.Sim.ReceivedCommands.Count(x => x == ":GR#").Should().Be(1);
        }

        [Test]
        public async Task TimedRequest_GoesOutAtItsDueTime_AndPollsDoNotDelayIt() {
            using var cable = new SimCable(new SimOptions(), baudRate: 9600, turnaround: TimeSpan.FromMilliseconds(15));
            using var link = OpenLink(cable);
            using var flood = new CancellationTokenSource();
            var pollLoop = Task.Run(async () => {
                while (!flood.IsCancellationRequested) {
                    try {
                        await link.SendAsync(":GD#", Lx200Lane.Poll);
                    } catch (Exception) {
                    }
                }
            });
            var errors = new List<double>();
            for (var i = 0; i < 8; i++) {
                var due = link.UtcNow + TimeSpan.FromMilliseconds(170 + (37 * i));
                var reply = await link.SendAsync(":RG#", Lx200Lane.Timed, dueUtc: due);
                errors.Add((reply.SentUtc - due).TotalMilliseconds);
            }
            flood.Cancel();
            await pollLoop;

            TestContext.Out.WriteLine("timed send minus due (ms): " + string.Join(", ", errors.Select(e => e.ToString("0.0"))));
            errors.Should().OnlyContain(e => e >= -0.5 && e < 15, "no poll may start inside the guard window before a timed send");
        }

        [Test]
        public async Task Nak_IsRetried_AndTheReplyStillArrives() {
            using var cable = new SimCable(new SimOptions { NakEveryNth = 3 });
            using var link = OpenLink(cable);
            for (var i = 0; i < 9; i++) {
                var reply = await link.SendAsync(":GVP#");
                reply.Status.Should().Be(ReplyStatus.Ok);
                reply.Value.Should().Be("LX2001");
            }
            cable.Sim.Log.Count(e => e.Naked).Should().BeGreaterThanOrEqualTo(3, "every third command was answered NAK");
            link.Trace.Snapshot().Should().Contain(e => e.Kind == TraceKind.Note && e.Text.Contains("NAK"));
            link.State.Should().Be(Lx200LinkState.Connected);
        }

        [Test]
        public async Task Garbage_IsDroppedBeforeTheNextCommand_AndTheLinkStaysUp() {
            // Every 4th reply has junk in front ("\xFFx#"). For a '#'-terminated reply the protocol library cannot tell junk from
            // an answer, so that one reply is wrong (the driver's parsers reject it and keep the last value); the real reply is
            // then stale input, dropped before the next command, and every following reply is right again.
            using var cable = new SimCable(new SimOptions { GarbageEveryNth = 4 });
            using var link = OpenLink(cable);
            var replies = new List<Lx200Reply>();
            for (var i = 0; i < 16; i++) {
                replies.Add(await link.SendAsync(":GVP#"));
            }
            replies.Count(r => r.IsOk && r.Value == "LX2001").Should().BeGreaterThanOrEqualTo(12);
            for (var i = 1; i < replies.Count; i++) {
                if (replies[i - 1].Value != "LX2001") {
                    replies[i].Stale.Should().NotBeEmpty("the real reply behind the junk is dropped before the next command");
                    replies[i].Value.Should().Be("LX2001", "the reply after a dropped one is in step again");
                }
            }
            link.Trace.Snapshot().Should().Contain(e => e.Kind == TraceKind.Discard);
            link.State.Should().Be(Lx200LinkState.Connected);
            link.ReconnectCount.Should().Be(0);
        }

        [Test]
        public void BlockedCommands_AreRefusedBeforeAnythingIsQueued() {
            using var cable = new SimCable(new SimOptions());
            using var link = OpenLink(cable);
            foreach (var blocked in new[] { ":hP#", "hP", ":Q:hP#", ": hP#", ":AP#", ":SB6#" }) {
                Action send = () => link.SendAsync(blocked);
                send.Should().Throw<Lx200BlockedCommandException>(blocked);
            }
            link.QueueLength(Lx200Lane.Command).Should().Be(0);
            cable.Sim.ReceivedCommands.Should().NotContain(c => c.Contains("hP") || c == ":AP#" || c.StartsWith(":SB", StringComparison.Ordinal));
        }

        [Test]
        public async Task Trace_HoldsEveryByteBothWays() {
            using var cable = new SimCable(new SimOptions());
            using var link = OpenLink(cable);
            await link.SendAsync(":GD#");
            var entries = link.Trace.Snapshot();
            entries.Should().Contain(e => e.Kind == TraceKind.Tx && System.Text.Encoding.Latin1.GetString(e.Bytes) == ":GD#");
            var rx = entries.Where(e => e.Kind == TraceKind.Rx).SelectMany(e => e.Bytes).ToArray();
            rx.Should().Contain(Lx200Format.DegreeByte, "the 0xDF degree byte arrives untouched");
            entries.Should().Contain(e => e.Kind == TraceKind.Reply && e.Text.StartsWith(":GD# shape=", StringComparison.Ordinal));
        }

        [Test]
        public async Task CancelledRequest_IsNeverSent() {
            using var cable = new SimCable(new SimOptions(), baudRate: 9600, turnaround: TimeSpan.FromMilliseconds(15));
            using var link = OpenLink(cable);
            using var cts = new CancellationTokenSource();
            var due = link.SendAsync(":RG#", Lx200Lane.Timed, cts.Token, dueUtc: link.UtcNow + TimeSpan.FromSeconds(2));
            cts.Cancel();
            Func<Task> wait = () => due;
            await wait.Should().ThrowAsync<OperationCanceledException>();
            await Task.Delay(100);
            cable.Sim.ReceivedCommands.Should().NotContain(":RG#");
            link.QueueLength(Lx200Lane.Timed).Should().Be(0);
        }

        [Test]
        public async Task Unplugged_ReconnectsOnItsOwn_AndSendsTheHaltsStillOwed() {
            using var cable = new SimCable(new SimOptions());
            using var link = OpenLink(cable);
            var states = new List<Lx200LinkState>();
            link.StateChanged += s => {
                lock (states) {
                    states.Add(s);
                }
            };
            (await link.SendAsync(":RG#")).IsOk.Should().BeTrue();
            (await link.SendAsync(":Mn#", Lx200Lane.Timed)).IsOk.Should().BeTrue();
            link.OwedStops.Should().Equal(":Qn#");
            cable.Sim.AnyAxisMotion.Should().BeTrue("the host-timed move runs until its halt");

            cable.Unplug();
            var poll = link.SendAsync(":GR#", Lx200Lane.Poll);
            Func<Task> failing = () => poll;
            await failing.Should().ThrowAsync<Lx200DisconnectedException>();
            (await Rig.Eventually(() => link.State == Lx200LinkState.Reconnecting, TimeSpan.FromSeconds(3))).Should().BeTrue();
            var halt = link.SendAsync(":Qs#", Lx200Lane.Stop);   // asked while down: owed, sent first when the link is back
            Func<Task> owed = () => halt;
            await owed.Should().ThrowAsync<Lx200DisconnectedException>();
            await Task.Delay(800);
            cable.FailedOpens.Should().BeGreaterThan(0, "the link keeps trying while the adapter is out");
            cable.Sim.AnyAxisMotion.Should().BeTrue("the halt could not reach the mount yet");

            var received = cable.Sim.ReceivedCommands.Count;
            cable.Replug();
            (await Rig.Eventually(() => link.State == Lx200LinkState.Connected, TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await Rig.Eventually(() => link.OwedStops.Count == 0, TimeSpan.FromSeconds(2))).Should().BeTrue();
            var after = cable.Sim.ReceivedCommands.Skip(received).ToList();
            after.Should().StartWith("ACK").And.Contain(new[] { ":Qn#", ":Qs#" });
            cable.Sim.AnyAxisMotion.Should().BeFalse("the owed halt stopped the move");
            link.ReconnectCount.Should().Be(1);
            (await link.SendAsync(":GVP#")).Value.Should().Be("LX2001");
            lock (states) {
                states.Should().ContainInOrder(Lx200LinkState.Reconnecting, Lx200LinkState.Connected);
            }
            link.Trace.Snapshot().Should().Contain(e => e.Text != null && e.Text.StartsWith("LINK RESTORED", StringComparison.Ordinal));
        }

        [Test]
        public async Task SilentMount_CountsAsLost_AndTheLinkComesBackWhenItAnswers() {
            using var cable = new SimCable(new SimOptions());
            var options = new Lx200LinkOptions {
                ReconnectInterval = TimeSpan.FromMilliseconds(200),
                ReconnectGiveUp = null,
                Connection = new Lx200ConnectionOptions { NakWindow = TimeSpan.FromMilliseconds(40) }
            };
            using var link = OpenLink(cable, options);
            cable.Mute(true);
            for (var i = 0; i < 3; i++) {
                try {
                    await link.SendAsync(":GVP#", timeout: TimeSpan.FromMilliseconds(200));
                } catch (Lx200DisconnectedException) {
                }
            }
            link.State.Should().Be(Lx200LinkState.Reconnecting, "three silent replies with failed resyncs mean the mount is gone");
            cable.Mute(false);
            (await Rig.Eventually(() => link.State == Lx200LinkState.Connected, TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await link.SendAsync(":GVP#")).Value.Should().Be("LX2001");
        }

        [Test]
        public async Task GivesUp_AfterTheConfiguredTime_AndFailsWhatIsQueued() {
            using var cable = new SimCable(new SimOptions());
            using var link = OpenLink(cable, new Lx200LinkOptions {
                ReconnectInterval = TimeSpan.FromMilliseconds(100),
                WaitForReconnect = TimeSpan.FromSeconds(10),
                ReconnectGiveUp = TimeSpan.FromSeconds(1)
            });
            using var notes = Rig.CaptureNotifications();
            cable.Unplug();
            Func<Task> lost = () => link.SendAsync(":GVP#");
            await lost.Should().ThrowAsync<Lx200DisconnectedException>();
            var queued = link.SendAsync(":GVN#");
            (await Rig.Eventually(() => link.State == Lx200LinkState.Failed, TimeSpan.FromSeconds(5))).Should().BeTrue();
            Func<Task> failed = () => queued;
            await failed.Should().ThrowAsync<Lx200DisconnectedException>();
            (await Rig.Eventually(() => notes.Posted.Any(n => n.Message.Contains("did not come back")), TimeSpan.FromSeconds(2))).Should().BeTrue();
            cable.Replug();
            await Task.Delay(400);
            link.State.Should().Be(Lx200LinkState.Failed, "Failed is terminal; the pool opens a new link on the next connect");
        }

        [Test]
        public async Task RefusedHalt_IsResent_ThenReportedLoudly() {
            using var cable = new SimCable(new SimOptions { HaltsNaked = true });
            using var link = OpenLink(cable, new Lx200LinkOptions { HaltRetryFor = TimeSpan.FromMilliseconds(800) });
            using var notes = Rig.CaptureNotifications();
            var reply = await link.SendAsync(":Q#", Lx200Lane.Stop);

            reply.Status.Should().Be(ReplyStatus.Nak);
            cable.Sim.Log.Count(e => e.Command == ":Q#").Should().BeGreaterThan(4, "the halt is sent again while it is refused");
            notes.Posted.Should().Contain(n => n.Message.Contains("refused the halt"));
        }
    }
}
