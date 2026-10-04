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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Lx200.Test {

    [TestFixture]
    public class Lx200ConnectionTest {

        [Test]
        public void Nak_IsRetried_AndTheCommandRunsOnce() {
            using var h = new SimHarness(new SimOptions { NakEveryNth = 2 });
            for (var i = 0; i < 10; i++) {
                var r = h.Link.Send(":GVP#");
                r.Status.Should().Be(ReplyStatus.Ok, r.Describe());
                r.Value.Should().Be("LX2001");
            }
            h.Sim.Log.Count(e => e.Naked).Should().BeGreaterThan(0);
            h.Trace.Snapshot().Should().Contain(e => e.Kind == TraceKind.Note && e.Text.Contains("NAK"));
        }

        [Test]
        public void Nak_OnANoReplyCommand_IsDetectedInTheNakWindow() {
            // the 2nd command (:U#) is NAKed: the link must notice within the NAK window and resend it once
            using var h = new SimHarness(new SimOptions { NakEveryNth = 2 });
            var before = h.Sim.LongFormat;
            h.Link.Send(":GVN#").Status.Should().Be(ReplyStatus.Ok);
            var u = h.Link.Send(":U#");
            u.Status.Should().Be(ReplyStatus.NoReplyExpected);
            u.NakCount.Should().Be(1);
            h.Sim.LongFormat.Should().Be(!before, "the retried :U# toggled exactly once");
        }

        [Test]
        public void PermanentNak_EndsAsNak() {
            using var h = new SimHarness(new SimOptions { NakEveryNth = 1 }, new Lx200ConnectionOptions { NakRetries = 2, NakRetryDelay = TimeSpan.FromMilliseconds(10) });
            var r = h.Link.Send(":GVP#");
            r.Status.Should().Be(ReplyStatus.Nak);
            r.NakCount.Should().Be(3);
        }

        [Test]
        public void GarbageBeforeReplies_TypedQueriesResyncAndRecover() {
            using var h = new SimHarness(new SimOptions { GarbageEveryNth = 3, StartInLongFormat = true });
            var expected = h.Sim.BelievedRaDec.RaHours;
            var resynced = 0;
            for (var i = 0; i < 12; i++) {
                var (ra, reply) = h.Link.Query(":GR#", Lx200Format.ParseRaHours);
                Lx200Astro.HourDifference(ra, expected).Should().BeApproximately(0, 5.0 / 3600);
                resynced += reply.Resynced ? 1 : 0;
            }
            var all = h.Trace.Snapshot();
            all.Should().Contain(e => e.Kind == TraceKind.Error && e.Text.Contains("unparseable"));
            all.Should().Contain(e => e.Kind == TraceKind.Note && e.Text.StartsWith("resync ok"));
        }

        [Test]
        public void GarbageBeforeACharReply_IsAFramingError_ThenResync() {
            using var h = new SimHarness(new SimOptions { GarbageEveryNth = 1 });
            var r = h.Link.Send(":St+22*15#");
            r.Status.Should().Be(ReplyStatus.FramingError);
            r.Error.Should().Contain("0xFF");
            r.Resynced.Should().BeTrue();
        }

        [Test]
        public void TrailingGarbage_IsDroppedBeforeTheNextCommand() {
            using var h = new SimHarness(new SimOptions { TrailingGarbageEveryNth = 1 });
            h.Link.Send(":GVP#").Value.Should().Be("LX2001");
            Thread.Sleep(50);
            var next = h.Link.Send(":GVN#");
            next.Value.Should().Be("4.2g");
            next.Stale.Should().Equal(0xFF, 0xFE);
            h.Trace.Snapshot().Should().Contain(e => e.Kind == TraceKind.Discard);
        }

        [Test]
        public void TrailingWindow_ReportsUnexpectedBytes() {
            using var h = new SimHarness(new SimOptions { TrailingGarbageEveryNth = 1 }, new Lx200ConnectionOptions { TrailingWindow = TimeSpan.FromMilliseconds(100), ResyncOnFailure = false });
            var r = h.Link.Send(":GVP#");
            r.Status.Should().Be(ReplyStatus.Ok);
            r.Trailing.Should().Equal(0xFF, 0xFE);
            r.Error.Should().Contain("unexpected");
        }

        [Test]
        public void MissingReply_IsSilent_ThenResyncs() {
            using var h = new SimHarness(new SimOptions { OtaTemperatureC = null });
            var r = h.Link.Send(":fT#", timeout: TimeSpan.FromMilliseconds(300));
            r.Status.Should().Be(ReplyStatus.Silent);
            r.Resynced.Should().BeTrue();
            h.Link.Query(":GVN#").Should().Be("4.2g");
        }

        [Test]
        public void Query_Throws_WhenNoValidReply() {
            using var h = new SimHarness(new SimOptions { OtaTemperatureC = null });
            FluentActions.Invoking(() => h.Link.Query(":fT#", TimeSpan.FromMilliseconds(200))).Should().Throw<Lx200ReplyException>();
        }

        [Test]
        public void Blocklist_RefusesPark_AndWritesNothing() {
            using var h = new SimHarness();
            var ex = FluentActions.Invoking(() => h.Link.Send(":hP#")).Should().Throw<Lx200BlockedCommandException>().Which;
            ex.Message.Should().Contain("power-cycled");
            h.Transmitted(":hP#").Should().BeFalse();
            Thread.Sleep(100);
            h.Sim.ReceivedCommands.Should().NotContain(":hP#");
            h.Sim.IsSilent.Should().BeFalse();
            h.Link.Ack().Value.Should().Be("A");
            h.Trace.Snapshot().Should().Contain(e => e.Kind == TraceKind.Error && e.Text.StartsWith("REFUSED :hP#"));
        }

        [Test]
        public void Blocklist_RefusesEveryBlockedCode_AndHiddenVariants() {
            using var h = new SimHarness();
            foreach (var spec in Lx200Catalog.BlockedCommands) {
                FluentActions.Invoking(() => h.Link.Send(":" + spec.Code + "#")).Should().Throw<Lx200BlockedCommandException>(spec.Code);
            }
            FluentActions.Invoking(() => h.Link.Send(":hP1#")).Should().Throw<Lx200BlockedCommandException>();
            FluentActions.Invoking(() => h.Link.Send(":Q:hP#")).Should().Throw<Lx200BlockedCommandException>();
            FluentActions.Invoking(() => h.Link.Send(":SB6#")).Should().Throw<Lx200BlockedCommandException>();
            FluentActions.Invoking(() => h.Link.Send(":Q#:hP#")).Should().Throw<Lx200MalformedCommandException>();
            h.Trace.Snapshot().Where(e => e.Kind == TraceKind.Tx).Should().BeEmpty("nothing was written");
        }

        [Test]
        public void Simulator_ModelsTheParkHazard() {
            // why :hP# is blocked: written behind the link's back, the simulated Autostar goes silent until power-cycled
            var (client, server) = DuplexPipe.Create();
            using var sim = new AutostarSimulator(server);
            client.Write(":hP#"u8);
            client.Write(new byte[] { 0x06 });
            var buffer = new byte[8];
            var read = Task.Run(() => client.Read(buffer, 0, buffer.Length));
            read.Wait(TimeSpan.FromMilliseconds(400)).Should().BeFalse("the parked Autostar does not even answer ACK");
            sim.IsSilent.Should().BeTrue();
            sim.PowerCycle();
            sim.IsSilent.Should().BeFalse();
            client.Dispose();
            read.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        }

        [Test]
        public void StopAll_SendsQAndFocuserHaltTwice_AndStopsMotion() {
            using var h = new SimHarness();
            h.Link.Send(":RS#");
            h.Link.Send(":Mn#");
            h.Link.Send(":F+#");
            h.Link.MotionCommanded.Should().BeTrue();
            h.Sim.AnyAxisMotion.Should().BeTrue();
            h.Sim.FocuserMoving.Should().BeTrue();

            h.Link.StopAll("test");

            h.Link.MotionCommanded.Should().BeFalse();
            h.Sim.AnyAxisMotion.Should().BeFalse();
            h.Sim.FocuserMoving.Should().BeFalse();
            var tail = h.Sim.ReceivedCommands.SkipWhile(c => c != ":F+#").Skip(1).ToList();
            tail.Should().ContainInOrder(":Q#", ":Qn#", ":Qs#", ":Qe#", ":Qw#", ":FQ#", ":FQ#");
        }

        [Test]
        public void StopAll_SecondHaltCatchesAMissedFirstHalt() {
            using var h = new SimHarness(new SimOptions { FirstFocusHaltIgnored = true });
            h.Link.Send(":F-#");
            h.Link.StopAll("test");
            h.Sim.FocuserMoving.Should().BeFalse("the second :FQ# stops it when the first is missed");
        }

        [Test]
        public void ReadOnlyCommands_DoNotSetMotionCommanded() {
            using var h = new SimHarness();
            h.Link.Send(":GR#");
            h.Link.Send(":Sr05:35:17#");
            h.Link.Send(":Q#");
            h.Link.MotionCommanded.Should().BeFalse();
            h.Link.Send(":XY#", ReplyShape.None);
            h.Link.MotionCommanded.Should().BeTrue("an unknown command is treated as possible motion");
        }

        [Test]
        public void Disconnect_IsReported() {
            using var h = new SimHarness();
            h.Link.Send(":GVP#").Status.Should().Be(ReplyStatus.Ok);
            h.Sim.Dispose();   // closes the simulator end of the pipe
            Thread.Sleep(100);
            var r = h.Link.Send(":GVP#");
            r.Status.Should().Be(ReplyStatus.Disconnected);
            h.Link.IsOpen.Should().BeFalse();
        }

        [Test]
        public void Trace_HasTimestampedHexAndAscii() {
            using var h = new SimHarness();
            h.Link.Send(":GD#");
            var lines = h.Trace.Snapshot().Select(Lx200Trace.Format).ToList();
            lines.Should().Contain(l => l.Contains("TX") && l.Contains("3A 47 44 23") && l.Contains("|:GD#|"));
            lines.Should().Contain(l => l.Contains("RX") && l.Contains(" DF ") && l.Contains("."));
            lines.Should().Contain(l => l.Contains("REPLY") && l.Contains("status=Ok"));
            lines.Should().OnlyContain(l => l.Length > 24 && l[4] == '-' && l[10] == 'T' && l[23] == 'Z');
        }

        [Test]
        public void TraceFile_IsWrittenWithoutBom() {
            var dir = SimHarness.TempDir("trace");
            var path = Path.Combine(dir, "trace.log");
            using (var t = Lx200Trace.ToFile(path)) {
                t.Log(TraceKind.Tx, ":GR#"u8);
            }
            var bytes = File.ReadAllBytes(path);
            bytes[0].Should().Be((byte)'2', "no UTF-8 BOM");
            File.ReadAllText(path).Should().Contain("3A 47 52 23");
        }
    }
}
