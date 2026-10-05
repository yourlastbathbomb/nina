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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace NINA.Mac.Lx200.Test {

    /// <summary>Answers prompts through a function; tests use it to say yes, refuse, or throw at a given prompt.</summary>
    internal sealed class FuncConsole : IOperatorConsole {
        private readonly Func<string, string> answer;
        private readonly List<string> output = new();
        private string last = "";

        public FuncConsole(Func<string, string> answer) {
            this.answer = answer;
        }

        public string Text {
            get {
                lock (output) {
                    return string.Join("\n", output);
                }
            }
        }

        public void WriteLine(string text) => Add(text);

        public void Write(string text) => Add(text);

        public string ReadLine() => answer(last);

        private void Add(string text) {
            lock (output) {
                output.Add(text);
                last = text;
            }
        }
    }

    [TestFixture]
    public class ProbeChecklistTest {

        // short timings so a full run takes well under a minute
        private static readonly string[] Fast = { "--pulse-ms", "800", "--focus-ms", "300", "--track-wait", "1", "--trailing-ms", "20", "--sim-quirk", "slew=1", "--sim-quirk", "planetary=0.5" };

        [Test]
        public void ChecklistSim_FullRun_WritesSensibleResults() {
            var dir = SimHarness.TempDir("checklist");
            var console = new ScriptedConsole();
            var args = new[] { "checklist", "--sim", "--date-test", "--out", dir }.Concat(Fast).ToArray();

            var exit = NINA.Mac.Lx200Probe.Program.Run(args, console);

            exit.Should().Be(0, console.Text);
            var results = File.ReadAllText(Path.Combine(dir, "results.md"));
            TestContext.Out.WriteLine(results.Length > 6000 ? results[..6000] : results);
            results.Should().StartWith("# M2 bench results");
            results.Should().Contain("**Simulator run.**");
            for (var step = 1; step <= 8; step++) {
                results.Should().MatchRegex($@"\| {step} \| [^|]+ \| Done \|", $"step {step} should be Done");
            }
            results.Should().NotContain("| Failed |");
            results.Should().Contain("Product :GVP# = LX2001");
            results.Should().Contain("Firmware 4.2g = stock Meade");
            results.Should().Contain(":GW# = 'AT2' (3 bytes: 3 chars and NO '#')");
            results.Should().Contain("Degree glyph in :GD#/:GA#/:GZ# replies: 0xDF");
            results.Should().Contain("Left in High precision");
            results.Should().Contain("High Precision pointing was OFF and is OFF again");
            results.Should().Contain("Azimuth is counted from North through East");
            results.Should().Contain(":Sg245*49# (0-360 westward, Meade.net/INDIGO) -> '1'");
            results.Should().MatchRegex(@"After writing: .*\(matches: time, date and longitude are consistent\)");
            results.Should().Contain("VERDICT: :SC takes the UTC date");
            results.Should().Contain("bar byte(s) while slewing: 7F");
            results.Should().Contain(":CM# returned the fixed Autostar II string");
            results.Should().Contain("DITHER STRATEGY: (a) native :Mg");
            results.Should().Contain("concurrent: the second axis was already moving halfway through the first pulse");
            results.Should().Contain(":fT# answers '+21.500'");
            results.Should().Contain(":FP (mount-timed pulse, P07 l.194) works");
            results.Should().Contain("Alignment: :GW# third char '2' -> '2'");
            results.Should().Contain("### Raw replies");
            results.Should().Contain("| `:GVP#` | 4C 58 32 30 30 31 23 | `LX2001#` |");
            var trace = File.ReadAllText(Path.Combine(dir, "trace.log"));
            trace.Should().Contain("TX      3A 47 56 50 23  |:GVP#|");
            trace.Should().NotContain("3A 68 50 23", "the probe never sends :hP#");
            // the date test destroys the alignment, so it runs after the steps that need it (goto, sync, tracking)
            var firstDateWrite = trace.IndexOf("|:SC", StringComparison.Ordinal);
            firstDateWrite.Should().BeGreaterThan(trace.LastIndexOf("|:CM#|", StringComparison.Ordinal)).And.BeGreaterThan(trace.LastIndexOf("|:AA#|", StringComparison.Ordinal));
            console.Text.IndexOf("=== Step 4:", StringComparison.Ordinal).Should().BeGreaterThan(console.Text.IndexOf("=== Step 8:", StringComparison.Ordinal));
        }

        [Test]
        public void ChecklistSim_Quirks_ChangeTheConclusions() {
            var dir = SimHarness.TempDir("quirks");
            var args = new[] {
                "checklist", "--sim", "--date-test", "--out", dir, "--skip", "5,8",
                "--sim-quirk", "date=local", "--sim-quirk", "pulse=ignored", "--sim-quirk", "degree=star", "--sim-quirk", "gw=hash",
                "--sim-quirk", "temp=none", "--sim-quirk", "fp=false", "--sim-quirk", "lon=west360", "--sim-quirk", "long=true", "--sim-quirk", "hp=true"
            }.Concat(Fast).ToArray();

            var exit = NINA.Mac.Lx200Probe.Program.Run(args, new ScriptedConsole());

            exit.Should().Be(0);
            var results = File.ReadAllText(Path.Combine(dir, "results.md"));
            results.Should().Contain("| 5 | Goto and sync | Skipped | skipped (--skip) |");
            results.Should().Contain("Degree glyph in :GD#/:GA#/:GZ# replies: '*' (0x2A)");
            results.Should().Contain("3 chars THEN '#'");
            results.Should().Contain("High Precision pointing WAS ON");
            results.Should().Contain("Format at start: High");
            results.Should().Contain("(0-360 westward)");
            results.Should().Contain("VERDICT: :SC takes the LOCAL date");
            results.Should().Contain(":Mg moved the mount in 0/4 directions");
            results.Should().Contain("DITHER STRATEGY: (b)");
            results.Should().Contain(":fT# is not answered");
            results.Should().Contain(":FP (mount-timed pulse, P07 l.194) does nothing");
        }

        [Test]
        public void ChecklistSim_FirmwareClockAndAlignmentQuirks() {
            var dir = SimHarness.TempDir("quirks2");
            var args = new[] {
                "checklist", "--sim", "--out", dir, "--skip", "4,5,6,7",
                "--sim-quirk", "firmware=4.2G", "--sim-quirk", "seconds-sep=colon", "--sim-quirk", "clock-error=-86400", "--sim-quirk", "align-lost=true"
            }.Concat(Fast).ToArray();

            NINA.Mac.Lx200Probe.Program.Run(args, new ScriptedConsole()).Should().Be(0);

            var results = File.ReadAllText(Path.Combine(dir, "results.md"));
            results.Should().Contain("Firmware 4.2G = StarPatch");
            results.Should().Contain("Minutes/seconds separator in long angle replies: ':' (0x3A)");
            results.Should().MatchRegex(@"Before writing: :GS# .*the DATE is one day early");
            results.Should().Contain("A whole-day or whole-hour error here points at the date convention");
            // the mount's date is a day off, but both longitude forms still give the right site
            results.Should().MatchRegex(@"- :Sg245\*49# .*: WORKS\.");
            results.Should().MatchRegex(@"- :Sg-114\*11# .*: WORKS\.");
            results.Should().Contain("Alignment: :GW# third char '2' -> '0'");
            results.Should().Contain("LOST or unclear");
        }

        [Test]
        public void ChecklistSim_PulseRerunAlone_SwitchesToLongFormat_AndSeesAQueue() {
            // the README's "pulse axes" rerun skips step 2; after a power cycle the mount is back in short format
            var dir = SimHarness.TempDir("pulse-only");
            var args = new[] { "checklist", "--sim", "--out", dir, "--skip", "1,2,3,4,5,7,8", "--sim-quirk", "two-axis=queued" }.Concat(Fast).ToArray();

            NINA.Mac.Lx200Probe.Program.Run(args, new ScriptedConsole()).Should().Be(0);

            var results = File.ReadAllText(Path.Combine(dir, "results.md"));
            results.Should().Contain("Replies were in short format, so :U# switched them to long format first");
            results.Should().Contain(":Mg moved the mount in 4/4 directions");
            results.Should().Contain("queued: the second axis only moved after the first pulse ended");
            results.Should().Contain("Send the second axis only after the first pulse ends");
        }

        [TestCase("checklist")]
        [TestCase("raw")]
        public void Yes_IsRefusedWithoutSimulator(string command) {
            var console = new ScriptedConsole();
            var outDir = System.IO.Path.Combine(SimHarness.TempDir("yes-" + command), "out");
            NINA.Mac.Lx200Probe.Program.Run(new[] { command, "--port", "/dev/null-nothing", "--yes", "--out", outDir }, console).Should().Be(2);
            console.Text.Should().Contain("--yes is only allowed with --sim");
            Directory.Exists(outDir).Should().BeFalse("nothing is opened or created");
        }

        [Test]
        public void MissingPort_IsAUsageError() {
            var console = new ScriptedConsole();
            NINA.Mac.Lx200Probe.Program.Run(new[] { "checklist", "--out", SimHarness.TempDir("noport") }, console).Should().Be(2);
            console.Text.Should().Contain("--port");
        }

        // A typo at the bench must stop the run with a message, before a folder is made or the port is opened
        [TestCase("--pulse-ms abc", "--pulse-ms needs a whole number, not 'abc'")]
        [TestCase("--skip 5,x", "--skip takes numbers 1..8")]
        [TestCase("--skip 9", "--skip takes numbers 1..8")]
        [TestCase("--guide-rate 20", "--guide-rate must be 0")]
        [TestCase("--lon 245.8", "--lon must be -180..180")]
        [TestCase("--sim-quirk pulse=bogus", "--sim-quirk: pulse=bogus?")]
        [TestCase("--sim-quirk nonsense", "--sim-quirk: Quirk 'nonsense' is not key=value")]
        [TestCase("--sim-quirk degree=dee-eff", "--sim-quirk: degree=dee-eff? (one of df, 0xdf, star, *)")]
        [TestCase("--sim-quirk long=maybe", "--sim-quirk: long=maybe? (one of true, false)")]
        [TestCase("--sim-quirk date=gmt", "--sim-quirk: date=gmt? (one of utc, local)")]
        [TestCase("--sim-quirk slew=-1", "--sim-quirk: slew=-1? (seconds, 0 or more)")]
        [TestCase("--sim-quirk nak-every=often", "--sim-quirk: nak-every=often? (a whole number")]
        [TestCase("--sim-quirk colour=red", "Unknown simulator quirk 'colour'")]
        [TestCase("--skp 4", "'checklist' does not take --skp")]
        [TestCase("--sim --port /dev/cu.x", "either --sim or --port")]
        [TestCase("--date-test --utc-offset 0", "--date-test needs a non-zero --utc-offset")]
        [TestCase("--utc-offset 5.5", "--utc-offset must be whole hours")]
        public void BadChecklistOptions_AreUsageErrors_BeforeAnythingIsOpened(string extra, string message) {
            var parent = SimHarness.TempDir("badopt");
            var outDir = System.IO.Path.Combine(parent, "out");
            var args = new[] { "checklist", "--sim", "--out", outDir }.Concat(extra.Split(' ')).Distinct().ToArray();
            var console = new ScriptedConsole();

            NINA.Mac.Lx200Probe.Program.Run(args, console).Should().Be(2, console.Text);

            console.Text.Should().Contain(message);
            Directory.Exists(outDir).Should().BeFalse("nothing is created for a bad command line");
        }

        [Test]
        public void UnwritableOutputFolder_SaysSo_NotAPortError() {
            var parent = SimHarness.TempDir("outfile");
            var blocker = System.IO.Path.Combine(parent, "a-file");
            File.WriteAllText(blocker, "");
            var console = new ScriptedConsole();

            NINA.Mac.Lx200Probe.Program.Run(new[] { "checklist", "--sim", "--out", System.IO.Path.Combine(blocker, "out") }, console).Should().Be(2);

            console.Text.Should().Contain("Cannot create the output folder").And.NotContain("Cannot open the port").And.NotContain("Serial I/O error");
        }

        [Test]
        public void NonexistentPort_SaysSo_InsteadOfAccessDenied() {
            var console = new ScriptedConsole();
            var outDir = System.IO.Path.Combine(SimHarness.TempDir("noport2"), "out");
            NINA.Mac.Lx200Probe.Program.Run(new[] { "checklist", "--port", "/dev/cu.usbserial-NOPE", "--out", outDir }, console).Should().Be(2);
            console.Text.Should().Contain("/dev/cu.usbserial-NOPE does not exist").And.Contain("lx200probe ports");
            Directory.Exists(outDir).Should().BeFalse();
        }

        [Test]
        public void Raw_And_Ports_RefuseOptionsTheyDoNotTake() {
            var console = new ScriptedConsole();
            NINA.Mac.Lx200Probe.Program.Run(new[] { "raw", "--sim", "--date-test" }, console).Should().Be(2);
            console.Text.Should().Contain("'raw' does not take --date-test");
            NINA.Mac.Lx200Probe.Program.Run(new[] { "ports", "--port", "x" }, console).Should().Be(2);
            console.Text.Should().Contain("'ports' does not take --port");
            NINA.Mac.Lx200Probe.Program.Run(new[] { "checklist", "--sim", "--out" }, console).Should().Be(2);
            console.Text.Should().Contain("--out needs a folder");
        }

        [Test]
        public void Declining_SkipsMotionAndWrites_ButReadOnlyStepsRun() {
            using var h = new SimHarness();
            var dir = SimHarness.TempDir("decline");
            var console = new FuncConsole(_ => "n");
            var options = new ChecklistOptions { OutputDirectory = dir, PulseMs = 300, FocusMs = 100, TrackWaitSeconds = 0.2 };
            var checklist = new Checklist(h.Link, new Prompter(console), options, CancellationToken.None);

            checklist.Run().Should().Be(0);

            var steps = checklist.Document.Steps;
            steps[0].Outcome.Should().Be(StepOutcome.Done);
            steps[1].Outcome.Should().Be(StepOutcome.Done, "the :P# part is optional");
            steps[2].Outcome.Should().Be(StepOutcome.Done, "site read-back runs, writes are declined");
            steps[3].Outcome.Should().Be(StepOutcome.Skipped, "--date-test not given");
            steps[4].Outcome.Should().Be(StepOutcome.Declined);
            steps[5].Outcome.Should().Be(StepOutcome.Declined);
            steps[6].Outcome.Should().Be(StepOutcome.Declined);
            steps[7].Outcome.Should().Be(StepOutcome.Declined);
            var sent = h.Sim.ReceivedCommands;
            sent.Should().NotContain(c => c.StartsWith(":MS", StringComparison.Ordinal) || c.StartsWith(":Mg", StringComparison.Ordinal) || c == ":F+#" || c == ":AL#");
            sent.Should().NotContain(c => c.StartsWith(":St", StringComparison.Ordinal) || c.StartsWith(":Sg", StringComparison.Ordinal) || c.StartsWith(":SC", StringComparison.Ordinal) || c == ":P#");
            h.Link.MotionCommanded.Should().BeFalse();
        }

        [Test]
        public void DateTest_NeedsTheExactWord() {
            using var h = new SimHarness();
            var dir = SimHarness.TempDir("dateword");
            var console = new FuncConsole(prompt => prompt.Contains("Type DESTROY") ? "yes" : "n");
            var options = new ChecklistOptions { OutputDirectory = dir, DateTest = true };
            options.Skip.UnionWith(new[] { 1, 2, 3, 5, 6, 7, 8 });
            var checklist = new Checklist(h.Link, new Prompter(console), options, CancellationToken.None);

            checklist.Run();

            checklist.Document.Steps[3].Outcome.Should().Be(StepOutcome.Declined);
            h.Sim.ReceivedCommands.Should().NotContain(c => c.StartsWith(":SC", StringComparison.Ordinal) || c.StartsWith(":SL", StringComparison.Ordinal));
        }

        [Test]
        public void CtrlC_DuringPulseGuiding_SendsStops() {
            using var h = new SimHarness(new SimOptions { SlewSeconds = 0.5, PlanetaryUpdateSeconds = 0.2 });
            using var cts = new CancellationTokenSource();
            h.Sim.CommandReceived += c => {
                if (c.StartsWith(":Mgn", StringComparison.Ordinal)) {
                    cts.Cancel();   // what the Ctrl+C handler does
                }
            };
            var dir = SimHarness.TempDir("abort");
            var options = new ChecklistOptions { OutputDirectory = dir, PulseMs = 2000 };
            options.Skip.UnionWith(new[] { 1, 2, 3, 4, 5, 7, 8 });
            var checklist = new Checklist(h.Link, new Prompter(new ScriptedConsole(), autoYes: true), options, cts.Token, h.Sim);

            var exit = checklist.Run();

            exit.Should().Be(130);
            checklist.Document.Steps[5].Outcome.Should().Be(StepOutcome.Aborted);
            var after = h.Sim.ReceivedCommands.SkipWhile(c => !c.StartsWith(":Mgn", StringComparison.Ordinal)).ToList();
            after.Should().Contain(":Q#").And.Contain(":FQ#");
            h.Sim.AnyAxisMotion.Should().BeFalse("the pulse was cut short by :Q#");
            File.ReadAllText(Path.Combine(dir, "results.md")).Should().Contain("Stop commands (:Q#, :Qn/s/e/w#, :FQ# twice) were sent");
        }

        [Test]
        public void CtrlC_DuringDateTest_StillRestoresTheMountClock() {
            using var h = new SimHarness();
            using var cts = new CancellationTokenSource();
            var scCount = 0;
            h.Sim.CommandReceived += c => {
                if (c.StartsWith(":SC", StringComparison.Ordinal) && Interlocked.Increment(ref scCount) == 2) {
                    cts.Cancel();   // Ctrl+C while the mount holds the simulated 01:00 date
                }
            };
            var dir = SimHarness.TempDir("date-abort");
            var options = new ChecklistOptions { OutputDirectory = dir, DateTest = true };
            options.Skip.UnionWith(new[] { 1, 2, 3, 5, 6, 7, 8 });
            var checklist = new Checklist(h.Link, new Prompter(new ScriptedConsole(), autoYes: true), options, cts.Token, h.Sim);

            checklist.Run().Should().Be(130);

            checklist.Document.Steps[3].Outcome.Should().Be(StepOutcome.Aborted);
            checklist.Document.Steps[3].Conclusions.Should().Contain(c => c.StartsWith("Restored :SL from the Mac clock", StringComparison.Ordinal));
            (h.Sim.MountUtc - DateTime.UtcNow).Duration().Should().BeLessThan(TimeSpan.FromSeconds(5), "the real date and time were written back");
        }

        [Test]
        public void Exception_WhileFocuserRuns_SendsStops() {
            using var h = new SimHarness();
            var thrown = false;
            h.Link.Sending += cmd => {
                if (!thrown && cmd.Text == ":FQ#") {
                    thrown = true;
                    throw new IOException("simulated: cable pulled before the halt");
                }
            };
            var dir = SimHarness.TempDir("exception");
            var options = new ChecklistOptions { OutputDirectory = dir, FocusMs = 300 };
            options.Skip.UnionWith(new[] { 1, 2, 3, 4, 5, 6, 8 });
            var checklist = new Checklist(h.Link, new Prompter(new ScriptedConsole(), autoYes: true), options, CancellationToken.None, h.Sim);

            checklist.Run().Should().Be(1);

            var focuser = checklist.Document.Steps[6];
            focuser.Outcome.Should().Be(StepOutcome.Failed);
            focuser.Notes.Should().Contain(n => n.Contains("Stop commands sent"));
            h.Sim.FocuserMoving.Should().BeFalse("the stop-on-exception path sent :FQ#");
            var after = h.Sim.ReceivedCommands.SkipWhile(c => c != ":F+#").ToList();
            after.Should().Contain(":FQ#").And.Contain(":Q#");
        }

        [Test]
        public void NoAnswerToAck_AbortsTheRun() {
            var (client, server) = DuplexPipe.Create();
            using var trace = new Lx200Trace();
            using var link = Lx200Connection.OverStream(client, "dead", trace);
            var dir = SimHarness.TempDir("dead");
            var options = new ChecklistOptions { OutputDirectory = dir };
            var checklist = new Checklist(link, new Prompter(new ScriptedConsole(), autoYes: true), options, CancellationToken.None);

            checklist.Run().Should().Be(1);

            checklist.Document.Steps[0].Summary.Should().Contain("no answer to ACK");
            checklist.Document.Steps.Skip(1).Should().OnlyContain(s => s.Outcome == StepOutcome.NotRun);
            server.Dispose();
        }

        private static Checklist.Delta D(double alt, double az) => new(alt, az, az, 0, 0, 0);

        // single :Mgn moved altitude, single :Mge moved azimuth (the alt-az reading of n/s/e/w), 20" each
        [TestCase(10, 10, 20, 20, "concurrent")]
        [TestCase(10, 0, 20, 20, "queued")]
        [TestCase(10, 0.5, 20, 0.5, "second pulse DROPPED")]
        [TestCase(0, 10, 0.3, 20, "first pulse cancelled by the second")]
        [TestCase(1, 1, 1, 1, "inconclusive")]
        public void ClassifyTwoAxis_AltAzMoves(double midAlt, double midAz, double endAlt, double endAz, string expected) {
            Checklist.ClassifyTwoAxis(D(20, 0), D(0, 20), D(midAlt, midAz), D(endAlt, endAz), 20, 6, double.NaN, 2000)
                .Should().StartWith(expected);
        }

        [Test]
        public void ClassifyTwoAxis_SlantedMoves_DoNotLeakIntoEachOther() {
            // RA/Dec pulses seen on the alt/az axes (from a pulse=eq simulator run): n and e are 70° apart, not 90°
            var n = D(14, -20);
            var e = D(-14, -20.7);
            var halfN = D(7, -10);
            // a projection would put 4.4" of this half n-move on e, above the 4" "already moving" mark
            halfN.Along(e).Should().BeGreaterThan(4);
            Checklist.Split(halfN, n, e).E.Should().BeApproximately(0, 1e-9);
            Checklist.ClassifyTwoAxis(n, e, halfN, D(0, -40.7), 20, 6, double.NaN, 2000).Should().StartWith("queued");
        }

        [Test]
        public void ClassifyTwoAxis_LateHalfwayReadout_SaysItCannotTell() {
            Checklist.ClassifyTwoAxis(D(20, 0), D(0, 20), D(20, 20), D(20, 20), 20, 6, 1.95, 2000)
                .Should().StartWith("both axes moved, but concurrent and queued cannot be told apart").And.Contain("longer --pulse-ms");
        }

        [Test]
        public void ClassifyTwoAxis_NoSinglePulseMovement_IsNotClassifiable() {
            Checklist.ClassifyTwoAxis(D(0.5, 0), D(0, 20), D(10, 10), D(20, 20), 20, 6, double.NaN, 2000)
                .Should().StartWith("not classifiable");
        }

        [TestCase(0.4, "matches")]
        [TestCase(-236.0, "one day early")]
        [TestCase(235.5, "one day late")]
        [TestCase(-28800.0, "about 8 h")]
        [TestCase(120.0, "clock or the longitude")]
        public void ExplainLstError(double seconds, string expected) {
            Checklist.ExplainLstError(seconds).Should().Contain(expected);
        }
    }

    [TestFixture]
    public class PortsTest {

        [Test]
        public void ListCalloutPorts_OnlyCu_UsbAdaptersFirst() {
            var dev = SimHarness.TempDir("dev");
            foreach (var name in new[] { "cu.Bluetooth-Incoming-Port", "tty.usbserial-A10K1XYZ", "cu.usbserial-A10K1XYZ", "cu.debug-console", "cu.usbmodem1101", "ttys004" }) {
                File.WriteAllText(Path.Combine(dev, name), "");
            }

            var ports = NINA.Mac.Lx200.Lx200Serial.ListCalloutPorts(dev).Select(Path.GetFileName).ToList();

            ports.Should().Equal("cu.usbmodem1101", "cu.usbserial-A10K1XYZ", "cu.Bluetooth-Incoming-Port", "cu.debug-console");
            NINA.Mac.Lx200.Lx200Serial.IsUsbSerial("/dev/cu.usbserial-A10K1XYZ").Should().BeTrue();
            NINA.Mac.Lx200.Lx200Serial.IsUsbSerial("/dev/cu.Bluetooth-Incoming-Port").Should().BeFalse();
            NINA.Mac.Lx200.Lx200Serial.ListCalloutPorts(Path.Combine(dev, "missing")).Should().BeEmpty();
        }

        [Test]
        public void Ports_ListsTheCalloutDevices_AndExplainsTheTtyTwins() {
            var console = new ScriptedConsole();

            NINA.Mac.Lx200Probe.Program.Run(new[] { "ports" }, console).Should().Be(0, console.Text);

            console.Text.Should().Contain("Callout serial devices (/dev/cu.*):");
            console.Text.Should().MatchRegex(@"SerialPort\.GetPortNames\(\) returns \d+ names \(\d+ /dev/tty\.\* and \d+ /dev/cu\.\*\)");
        }
    }

    [TestFixture]
    public class RawConsoleTest {

        [Test]
        public void Raw_RefusesPark_ConfirmsMotion_StopsOnExit() {
            using var h = new SimHarness();
            var answers = new Queue<string>(new[] { ":hP#", "GR", ":Mn#", "n", ":Mn#", "y", ".shape drain :GW#", ".bogus", ".quit" });
            var console = new FuncConsole(_ => answers.Count > 0 ? answers.Dequeue() : null);

            new RawConsole(h.Link, new Prompter(console), CancellationToken.None).Run().Should().Be(0);

            var text = console.Text;
            text.Should().Contain("REFUSED :hP#");
            text.Should().Contain("Not sent.");
            text.Should().Contain(":GR# -> Ok");
            text.Should().Contain(":GW# -> Ok");
            text.Should().Contain("Unknown meta command");
            text.Should().Contain("sending stop commands");
            var sent = h.Sim.ReceivedCommands;
            sent.Should().NotContain(":hP#");
            sent.Count(c => c == ":Mn#").Should().Be(1, "the first :Mn# was declined");
            sent.SkipWhile(c => c != ":Mn#").Should().Contain(":Q#");
            h.Sim.AnyAxisMotion.Should().BeFalse();
        }

        [Test]
        public void Raw_EndOfInput_Leaves() {
            using var h = new SimHarness();
            new RawConsole(h.Link, new Prompter(new ScriptedConsole()), CancellationToken.None).Run().Should().Be(0);
        }
    }
}
