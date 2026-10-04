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
using NINA.Mac.Lx200Probe;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace NINA.Mac.Lx200.Test {

    /// <summary>
    /// The simulator on a pseudo-terminal, the probe on the other side through System.IO.Ports: the same code path
    /// as /dev/cu.usbserial-* at the bench, minus the FTDI chip.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class PtyEndToEndTest {

        private static PseudoTerminal OpenPtyOrIgnore() {
            if (!PseudoTerminal.IsSupported) {
                Assert.Ignore("openpty needs macOS or Linux");
            }
            try {
                return PseudoTerminal.Open();
            } catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException) {
                Assert.Ignore($"openpty unavailable here: {ex.Message}");
                return null;
            }
        }

        [Test]
        public void SystemIoPorts_TalksToTheSimulatorOverAPty() {
            using var pty = OpenPtyOrIgnore();
            using var sim = new AutostarSimulator(pty.Master, new SimOptions { PlanetaryUpdateSeconds = 0.3 });
            pty.SlavePath.Should().StartWith("/dev/tty");
            using var trace = new Lx200Trace();
            using (var link = Lx200Connection.OpenSerial(pty.SlavePath, trace, new Lx200ConnectionOptions { TrailingWindow = TimeSpan.FromMilliseconds(100) })) {
                link.Ack().Value.Should().Be("A");
                link.Query(":GVP#").Should().Be("LX2001");
                var gd = link.Send(":GD#");
                gd.Status.Should().Be(ReplyStatus.Ok, gd.Describe());
                gd.Raw.Should().Contain(Lx200Format.DegreeByte, "System.IO.Ports must pass the 0xDF byte through untouched");
                link.Send(":U#").Status.Should().Be(ReplyStatus.NoReplyExpected);
                Lx200Format.DetectPrecision(link.Query(":GR#")).Should().Be(CoordinatePrecision.High);
                var sc = link.Send(":SC" + Lx200Format.FormatDate(DateTime.UtcNow) + "#");
                sc.Parts.Should().HaveCount(3, sc.Describe());
                sc.Trailing.Should().BeEmpty();
                FluentActions.Invoking(() => link.Send(":hP#")).Should().Throw<Lx200BlockedCommandException>();
                link.Send(":Mn#");
                link.StopAll("pty test");
                sim.AnyAxisMotion.Should().BeFalse();
            }
            Thread.Sleep(200);
            // a second session on the same pty (the probe is run again at the bench)
            using (var again = Lx200Connection.OpenSerial(pty.SlavePath, trace)) {
                again.Query(":GVN#").Should().Be("4.2g");
            }
            sim.ReceivedCommands.Should().NotContain(":hP#");
            sim.ReceivedCommands.Should().Contain(":Q#").And.Contain(":FQ#");
        }

        [Test]
        public void Probe_ChecklistSteps1to3_OverAPty() {
            using var pty = OpenPtyOrIgnore();
            using var sim = new AutostarSimulator(pty.Master, new SimOptions());
            var dir = SimHarness.TempDir("pty-checklist");
            // the real probe entry point, exactly as at the bench, but with the simulator's tty as the port;
            // steps that move hardware are skipped because a real port never auto-confirms
            var console = new FuncConsole(prompt => prompt.Contains("Proceed?") ? "y" : "");
            var exit = NINA.Mac.Lx200Probe.Program.Run(new[] { "checklist", "--port", pty.SlavePath, "--out", dir, "--skip", "4,5,6,7,8", "--trailing-ms", "50" }, console);

            exit.Should().Be(0, console.Text);
            var results = File.ReadAllText(Path.Combine(dir, "results.md"));
            results.Should().Contain($"| Port | {pty.SlavePath} (9600 8N1, no handshake) |");
            results.Should().MatchRegex(@"\| 1 \| Firmware \| Done \| LX2001 4\.2g");
            results.Should().MatchRegex(@"\| 2 \| Coordinates and precision \| Done \| degree 0xDF");
            results.Should().MatchRegex(@"\| 3 \| Site and sidereal time \| Done \|");
            results.Should().NotContain("VERDICT", "step 4 was skipped");
            File.ReadAllText(Path.Combine(dir, "trace.log")).Should().Contain($"opened {pty.SlavePath}: 9600 8N1 handshake=None DTR=False RTS=False");
            sim.ReceivedCommands.Should().Contain(":St+22*15#").And.Contain(":Sg245*49#");
        }
    }
}
