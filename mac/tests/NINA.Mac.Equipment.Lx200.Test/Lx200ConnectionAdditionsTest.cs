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
using System.Linq;

namespace NINA.Mac.Equipment.Lx200.Test {

    /// <summary>
    /// The two small additions the driver made to the protocol library (NINA.Mac.Lx200), on a bare connection: a separate NAK
    /// window for halts (<see cref="Lx200ConnectionOptions.HaltNakWindow"/>), and a transaction that hands a NAK back at once
    /// (<see cref="Lx200Connection.Send(Lx200Command, ReplyShape, TimeSpan?, bool)"/>). Both default to the old behaviour, which
    /// the probe keeps.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Lx200ConnectionAdditionsTest {

        [Test]
        public void HaltNakWindow_AppliesToHaltsOnly_AndUnsetKeepsTheNakWindow() {
            // every NAK reaches the Mac 55 ms after the command (Autostar + FTDI latency + a loaded Mac)
            var sim = new SimOptions { HaltsNaked = true, NakWhen = c => c == ":Mn#" };
            using var cable = new SimCable(sim, turnaround: TimeSpan.FromMilliseconds(55));
            var trace = new Lx200Trace();
            var options = new Lx200ConnectionOptions { NakWindow = TimeSpan.FromMilliseconds(40), HaltNakWindow = TimeSpan.FromMilliseconds(150), NakRetries = 0 };
            using (var conn = new Lx200Connection(cable.Open(trace), trace, options)) {
                conn.Send(":Qn#").Status.Should().Be(ReplyStatus.Nak, "a halt gets the 150 ms window");
                conn.Send(":Mn#").Status.Should().Be(ReplyStatus.NoReplyExpected, "a motion start keeps the 40 ms window, so its halt is not held up");
            }
            options.HaltNakWindow = null;
            using var cable2 = new SimCable(new SimOptions { HaltsNaked = true }, turnaround: TimeSpan.FromMilliseconds(55));
            using (var conn = new Lx200Connection(cable2.Open(trace), trace, options)) {
                conn.Send(":Qs#").Status.Should().Be(ReplyStatus.NoReplyExpected, "unset, halts use the NakWindow as before");
            }
        }

        [Test]
        public void NakReturnsAtOnce_HandsTheNakBack_WithoutWaitingRetryingOrResyncing() {
            var sim = new SimOptions { NakWhen = c => c == ":GVP#" };
            using var cable = new SimCable(sim);
            var trace = new Lx200Trace();
            using var conn = new Lx200Connection(cable.Open(trace), trace, new Lx200ConnectionOptions());
            var command = Lx200Command.Parse(":GVP#");

            var before = cable.Sim.ReceivedCommands.Count;
            var once = conn.Send(command, null, null, nakReturnsAtOnce: true);
            once.Status.Should().Be(ReplyStatus.Nak);
            once.NakCount.Should().Be(1);
            once.Resynced.Should().BeFalse("a NAK is a well-formed answer: no resync");
            cable.Sim.ReceivedCommands.Skip(before).Should().Equal(":GVP#");

            before = cable.Sim.ReceivedCommands.Count;
            var retried = conn.Send(command);
            retried.Status.Should().Be(ReplyStatus.Nak);
            retried.NakCount.Should().Be(4, "by default the connection retries 3 times, as the probe expects");
            cable.Sim.ReceivedCommands.Skip(before).Count(c => c == ":GVP#").Should().Be(4);
            retried.Resynced.Should().BeTrue("and resyncs after the last NAK, as before");
        }
    }
}
