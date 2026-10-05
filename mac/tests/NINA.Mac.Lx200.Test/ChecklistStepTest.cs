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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace NINA.Mac.Lx200.Test {

    /// <summary>
    /// One checklist step at a time against the simulator, for the cases where a step could reach a wrong conclusion
    /// or leave the mount in a wrong state: a late :D# bar, a mount clock that is off, no :GW#, halts the mount
    /// refuses, the date test west of Greenwich or just after local midnight.
    /// </summary>
    [TestFixture]
    public class ChecklistStepTest {

        /// <summary>00:30 HKT on 5 October 2026: the UTC date (4 October) and the local date differ.</summary>
        private static readonly DateTime JustAfterHongKongMidnight = new(2026, 10, 4, 16, 30, 0, DateTimeKind.Utc);

        private static ChecklistOptions Only(string name, int step, Action<ChecklistOptions> set = null) {
            var options = new ChecklistOptions { OutputDirectory = SimHarness.TempDir(name) };
            options.Skip.UnionWith(Enumerable.Range(1, 8).Where(n => n != step));
            set?.Invoke(options);
            return options;
        }

        private static Checklist AutoYes(SimHarness h, ChecklistOptions options, IOperatorConsole console = null, CancellationToken token = default) =>
            new(h.Link, new Prompter(console ?? new ScriptedConsole(), autoYes: true), options, token, h.Sim);

        private static string All(StepRecord s) => string.Join("\n", s.Conclusions.Concat(s.Notes).Prepend(s.Summary));

        // ---- step 5: goto and sync ----------------------------------------------------------------

        [Test]
        public void Goto_ABarThatShowsLate_StillCountsAsSlewing_SoTheSyncWaits() {
            using var h = new SimHarness(new SimOptions { SlewSeconds = 3, DistanceBarDelaySeconds = 0.8, PlanetaryUpdateSeconds = 0.2 });
            bool? slewingAtSync = null;
            h.Sim.CommandReceived += c => {
                if (c == ":CM#") {
                    slewingAtSync = h.Sim.Slewing;
                }
            };
            var checklist = AutoYes(h, Only("late-bar", 5));

            checklist.Run().Should().Be(0);

            var step = checklist.Document.Steps[4];
            step.Outcome.Should().Be(StepOutcome.Done, All(step));
            slewingAtSync.Should().BeFalse("the sync must wait for the end of the goto, not the first null :D#");
            step.Conclusions.Should().Contain(c => c.Contains("bar byte(s) while slewing: 7F"));
            step.Conclusions.Should().Contain(c => c.Contains("not trusted"), "the null :D# before the bar showed is reported");
            step.Summary.Should().MatchRegex(@"goto \d+° ok, \d+"" off; :CM# fixed string; returned");
        }

        [Test]
        public void Goto_NoBarAtAll_TheSyncIsRefused_AndTheSlewStopped() {
            // the bar never shows: after the minimum slewing time the probe believes the goto ended, but the mount's
            // own position is still far from the target, so syncing there would corrupt the pointing model
            using var h = new SimHarness(new SimOptions { SlewSeconds = 4, DistanceBarDelaySeconds = 60, PlanetaryUpdateSeconds = 0.2 });
            var checklist = AutoYes(h, Only("no-bar", 5));

            checklist.Run().Should().Be(1, "a goto that ends far from its target is a failed step");

            var step = checklist.Document.Steps[4];
            step.Outcome.Should().Be(StepOutcome.Failed, All(step));
            step.Summary.Should().Contain(":CM# NOT sent");
            h.Sim.ReceivedCommands.Should().NotContain(":CM#");
            h.Sim.AnyAxisMotion.Should().BeFalse("the probe stopped the slew it could not see");
        }

        // ---- step 3: site ---------------------------------------------------------------------------

        [TestCase(60.0, TestName = "Site_MountClockAMinuteFast_BothLongitudeFormsStillJudgedRight")]
        [TestCase(-86400.0, TestName = "Site_MountDateADayEarly_BothLongitudeFormsStillJudgedRight")]
        [TestCase(-1024 * 7 * 86400.0, TestName = "Site_GpsWeekRolloverDate_BothLongitudeFormsStillJudgedRight")]
        public void Site_MountClockOff_DoesNotFailAWorkingLongitudeForm(double clockErrorSeconds) {
            using var h = new SimHarness(new SimOptions { ClockErrorSeconds = clockErrorSeconds, PlanetaryUpdateSeconds = 0.2 });
            var checklist = AutoYes(h, Only("clock-off", 3));

            checklist.Run().Should().Be(0);

            var step = checklist.Document.Steps[2];
            step.Conclusions.Should().Contain(c => c.StartsWith(":Sg245*49# ", StringComparison.Ordinal) && c.EndsWith("WORKS.", StringComparison.Ordinal), All(step));
            step.Conclusions.Should().Contain(c => c.StartsWith(":Sg-114*11# ", StringComparison.Ordinal) && c.EndsWith("WORKS.", StringComparison.Ordinal), All(step));
            step.Summary.Should().Contain(":Sg245*49# ok, :Sg-114*11# ok");
            step.Conclusions.Should().Contain(c => c.StartsWith("Before writing:", StringComparison.Ordinal) && !c.Contains("(matches"),
                "the mount clock error is still reported, separately");
        }

        // ---- step 8: tracking ------------------------------------------------------------------------

        [TestCase(true)]
        [TestCase(false)]
        public void Tracking_WithoutGw_TheAlignmentIsUnreadable_NotKept(bool alignmentLost) {
            using var h = new SimHarness(new SimOptions { GwSupported = false, AlignmentLostOnLandMode = alignmentLost, PlanetaryUpdateSeconds = 0.2 });
            var checklist = AutoYes(h, Only("no-gw", 8, o => o.TrackWaitSeconds = 1));

            checklist.Run().Should().Be(0);

            var step = checklist.Document.Steps[7];
            step.Summary.Should().Contain("alignment unreadable");
            step.Conclusions.Should().Contain(c => c.Contains("unreadable") && c.Contains("check the handbox"), All(step));
            step.Conclusions.Should().NotContain(c => c.Contains("KEPT"));
        }

        // ---- step 7: focuser -------------------------------------------------------------------------

        [Test]
        public void Focuser_HostTimedMovesStopOnTime_AndTheBacklashBracketUsesTheMeasuredTimes() {
            // a long trailing window (listening for stray bytes after each reply) must not delay a halt
            using var h = new SimHarness(null, new Lx200ConnectionOptions { MinimumGap = TimeSpan.FromMilliseconds(5), TrailingWindow = TimeSpan.FromMilliseconds(400) });
            var checklist = AutoYes(h, Only("focus-timing", 7, o => o.FocusMs = 300));

            checklist.Run().Should().Be(0);

            var tx = h.Trace.Snapshot().Where(e => e.Kind == TraceKind.Tx).Select(e => (Text: Lx200Format.Latin1.GetString(e.Bytes), e.ElapsedMs)).ToList();
            var runs = new List<double>();
            for (var i = 0; i < tx.Count; i++) {
                if (tx[i].Text is ":F+#" or ":F-#") {
                    var halt = tx.FindIndex(i + 1, t => t.Text == ":FQ#");
                    halt.Should().BeGreaterThan(i);
                    runs.Add(tx[halt].ElapsedMs - tx[i].ElapsedMs);
                    tx[halt + 1].Text.Should().Be(":FQ#", "the backup halt follows the first");
                    (tx[halt + 1].ElapsedMs - tx[halt].ElapsedMs).Should().BeLessThan(200, "the backup halt goes out right after the first");
                }
            }
            var step = checklist.Document.Steps[6];
            var backlash = step.Observations.Where(o => o.StartsWith("Backlash ", StringComparison.Ordinal)).ToList();
            backlash.Should().NotBeEmpty();
            runs.Should().HaveCount(8 + 1 + backlash.Count, "4 speeds x in/out, one run out, then the backlash steps");
            var backlashRuns = runs.Skip(9).ToList();
            backlashRuns.Should().OnlyContain(r => r < 200 + 150, "a 200 ms step must not run for the NAK and trailing windows too");
            for (var k = 0; k < backlash.Count; k++) {
                var reported = double.Parse(Regex.Match(backlash[k], @"reversed IN for (\d+) ms").Groups[1].Value, CultureInfo.InvariantCulture);
                reported.Should().BeApproximately(backlashRuns[k], 1.5, backlash[k]);
            }
            var bracket = Regex.Match(step.Summary, @"backlash between (\d+) and (\d+) ms");
            bracket.Success.Should().BeTrue(step.Summary);
            double.Parse(bracket.Groups[2].Value, CultureInfo.InvariantCulture).Should().BeApproximately(backlashRuns.Sum(), 1.5 * backlashRuns.Count);
        }

        // ---- halts the mount refuses -----------------------------------------------------------------

        [TestCase(7, ":FQ#")]
        [TestCase(6, ":Qn#")]
        [TestCase(5, ":Q#")]
        public void HaltRefused_FailsTheRun_AndWarnsThatTheMotorMayStillRun(int stepNumber, string halt) {
            // step 5 only sends :Q# when a goto never ends: High Precision pointing makes it hang
            using var h = new SimHarness(new SimOptions { HaltsNaked = true, HighPrecisionPointing = stepNumber == 5, SlewSeconds = 0.5, PlanetaryUpdateSeconds = 0.2 });
            var console = new ScriptedConsole();
            var options = Only("halt-nak", stepNumber, o => {
                o.FocusMs = 300;
                o.PulseMs = 300;
                o.GotoTimeoutSeconds = 2;
            });
            var checklist = AutoYes(h, options, console);

            checklist.Run().Should().Be(1);

            var step = checklist.Document.Steps[stepNumber - 1];
            step.Outcome.Should().Be(StepOutcome.Failed, All(step));
            step.Summary.Should().Contain($"halt {halt} not accepted");
            var results = File.ReadAllText(Path.Combine(options.OutputDirectory, "results.md"));
            results.Should().NotContain("Stop commands (:Q#, :Qn/s/e/w#, :FQ# twice) were sent");
            results.Should().Contain("STOP COMMANDS NOT ACCEPTED");
            console.Text.Should().Contain("may still be moving: switch the mount off");
            h.Link.MotionCommanded.Should().BeTrue("no stop was accepted");
            if (stepNumber == 7) {
                h.Sim.FocuserMoving.Should().BeTrue("the simulated mount refused every halt, so the warning is warranted");
            }
        }

        [Test]
        public void RawConsole_Stop_SaysSoWhenTheMountRefusesTheHalts() {
            using var h = new SimHarness(new SimOptions { HaltsNaked = true });
            var answers = new Queue<string>(new[] { ":F+#", "y", ".stop", ".quit" });
            var console = new FuncConsole(_ => answers.Count > 0 ? answers.Dequeue() : null);

            new RawConsole(h.Link, new Prompter(console), CancellationToken.None).Run().Should().Be(1);

            console.Text.Should().Contain("may still be moving: switch the mount off").And.NotContain("Stop commands sent.");
        }

        // ---- step 4: the date test -------------------------------------------------------------------

        [TestCase(DateConvention.Utc, "UTC")]
        [TestCase(DateConvention.Local, "LOCAL")]
        public void DateTest_WestOfGreenwich_SeparatesTheDates(DateConvention convention, string verdict) {
            using var h = new SimHarness(new SimOptions { ScDate = convention, PlanetaryUpdateSeconds = 0.2 });
            var checklist = AutoYes(h, Only("date-west", 4, o => {
                o.DateTest = true;
                o.UtcOffsetHours = -5;
                o.SiteLongitudeEast = -74;
                o.SiteLatitude = 40.7;
            }));

            checklist.Run().Should().Be(0);

            var step = checklist.Document.Steps[3];
            step.Summary.Should().StartWith(verdict + ":", All(step)).And.Contain("restore ok");
            var dates = Regex.Match(string.Join("\n", step.Notes), @"UTC date (\S+), local date (\S+)\.");
            dates.Success.Should().BeTrue();
            dates.Groups[1].Value.Should().NotBe(dates.Groups[2].Value, "the simulated time must put the UTC and local dates on different days");
            (h.Sim.MountUtc - h.Trace.UtcNow).Duration().Should().BeLessThan(TimeSpan.FromSeconds(5), "the real date and time were written back");
        }

        [TestCase(DateConvention.Utc, "UTC")]
        [TestCase(DateConvention.Local, "LOCAL")]
        public void DateTest_JustAfterLocalMidnight_RestoresTheRightDate(DateConvention convention, string verdict) {
            using var h = new SimHarness(new SimOptions { ScDate = convention, PlanetaryUpdateSeconds = 0.2 }, null, JustAfterHongKongMidnight);
            var checklist = AutoYes(h, Only("date-midnight", 4, o => o.DateTest = true));

            checklist.Run().Should().Be(0);

            var step = checklist.Document.Steps[3];
            step.Summary.Should().StartWith(verdict + ":", All(step)).And.Contain("restore ok");
            (h.Sim.MountUtc - h.Trace.UtcNow).Duration().Should().BeLessThan(TimeSpan.FromSeconds(5), "the restore wrote the date the verdict implies");
            h.Sim.MountUtc.AddHours(8).Date.Should().Be(new DateTime(2026, 10, 5), "local date in Hong Kong");
        }

        [TestCase(DateConvention.Utc)]
        [TestCase(DateConvention.Local)]
        public void DateTest_InterruptedJustAfterLocalMidnight_WritesTheUtcDate_AndFlagsALocalDateMount(DateConvention convention) {
            using var h = new SimHarness(new SimOptions { ScDate = convention, PlanetaryUpdateSeconds = 0.2 }, null, JustAfterHongKongMidnight);
            using var cts = new CancellationTokenSource();
            var scCount = 0;
            h.Sim.CommandReceived += c => {
                if (c.StartsWith(":SC", StringComparison.Ordinal) && Interlocked.Increment(ref scCount) == 2) {
                    cts.Cancel();   // Ctrl+C during the second trial: no verdict
                }
            };
            var checklist = AutoYes(h, Only("date-midnight-abort", 4, o => o.DateTest = true), token: cts.Token);

            checklist.Run().Should().Be(130);

            var step = checklist.Document.Steps[3];
            step.Outcome.Should().Be(StepOutcome.Aborted);
            step.Conclusions.Should().Contain(c => c.StartsWith("WARNING: restored with the UTC date", StringComparison.Ordinal), All(step));
            var mountMinusTrue = h.Sim.MountUtc - h.Trace.UtcNow;
            if (convention == DateConvention.Utc) {
                mountMinusTrue.Duration().Should().BeLessThan(TimeSpan.FromSeconds(5));
                step.Summary.Should().Contain("restore ok");
                step.Conclusions.Should().NotContain(c => c.Contains("RESTORE"));
            } else {
                // a local-date mount given the UTC date is a day early: the LST check after the restore must say so
                (mountMinusTrue + TimeSpan.FromDays(1)).Duration().Should().BeLessThan(TimeSpan.FromSeconds(5));
                step.Summary.Should().Contain("restore FAILED");
                step.Conclusions.Should().Contain(c => c.StartsWith("RESTORE CHECK FAILED", StringComparison.Ordinal) && c.Contains("set the date and time on the handbox"));
                step.Conclusions.Should().Contain(c => c.StartsWith("Restored :SL", StringComparison.Ordinal) && c.Contains("one day early"));
            }
        }
    }
}
