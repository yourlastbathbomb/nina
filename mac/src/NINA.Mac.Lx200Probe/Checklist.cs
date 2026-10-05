#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.Lx200;
using NINA.Mac.Lx200.Sim;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace NINA.Mac.Lx200Probe {

    public sealed class ChecklistOptions {

        public string OutputDirectory { get; set; }

        /// <summary>Step 4 (:SC date convention) is opt-in: bench only, it destroys the alignment.</summary>
        public bool DateTest { get; set; }

        public double SiteLatitude { get; set; } = 22.25;

        public double SiteLongitudeEast { get; set; } = 114.18;

        /// <summary>The usual "UTC+8"; the mount's :SG value is the negative of this.</summary>
        public double UtcOffsetHours { get; set; } = 8;

        public int PulseMs { get; set; } = 2000;

        /// <summary>Sent with :Rg before the pulse tests so the expected movement is known; 0 = leave the mount's rate.</summary>
        public double GuideRateArcsecPerSec { get; set; } = 10.0;

        public int FocusMs { get; set; } = 1000;

        public double TrackWaitSeconds { get; set; } = 5;

        public double GotoOffsetAzDeg { get; set; } = 15;

        public double GotoTimeoutSeconds { get; set; } = 180;

        /// <summary>
        /// A null :D# reply this soon after :MS# answered '0' still counts as slewing: the bar can lag the start of the
        /// goto (RVM MNT-04 rec; Meade.net Telescope.cs:3290-3310 counts a minimum time as slewing).
        /// </summary>
        public double MinimumSlewSeconds { get; set; } = 1.5;

        /// <summary>
        /// The step 5 sync (:CM#) is only sent when the mount's own position is this close to the goto target; farther
        /// off, the goto did not really end there and a sync would put a wrong point into the pointing model.
        /// </summary>
        public double SyncGuardArcsec { get; set; } = 600;

        public double SettleSeconds { get; set; } = 0.7;

        public double DistancePollSeconds { get; set; } = 0.5;

        public ISet<int> Skip { get; } = new HashSet<int>();

        public string PortDescription { get; set; } = "";

        public string CommandLine { get; set; } = "";
    }

    /// <summary>
    /// MAC_PORT_PLAN.md section 5, steps 1-8, against a real mount or the simulator. Read-only probes run freely;
    /// anything that moves hardware or writes config/date/time asks first and can be skipped. Whatever happens
    /// (exception, abort, Ctrl+C), if motion was commanded the stop commands are sent.
    /// </summary>
    public sealed partial class Checklist {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Step 4 (the opt-in date test) runs last: it destroys the alignment that steps 5-8 rely on (goto and sync,
        /// tracking), so a full run with --date-test still gets meaningful results from them.
        /// </summary>
        private static readonly int[] ExecutionOrder = { 1, 2, 3, 5, 6, 7, 8, 4 };

        /// <summary>How long a halt the mount refuses (NAK) is resent before the run stops with a warning.</summary>
        private static readonly TimeSpan HaltRetryTime = TimeSpan.FromSeconds(3);

        private readonly Lx200Connection link;
        private readonly Prompter ask;
        private readonly ChecklistOptions opt;
        private readonly CancellationToken token;
        private readonly AutostarSimulator sim;
        private StepRecord current;
        private volatile bool restorePending;

        // facts learned by earlier steps
        private CoordinatePrecision? precision;
        private double mountLatitude = double.NaN;
        private double mountLongitudeEast = double.NaN;

        public Checklist(Lx200Connection link, Prompter prompter, ChecklistOptions options, CancellationToken token, AutostarSimulator simulator = null) {
            this.link = link ?? throw new ArgumentNullException(nameof(link));
            ask = prompter ?? throw new ArgumentNullException(nameof(prompter));
            opt = options ?? throw new ArgumentNullException(nameof(options));
            this.token = token;
            sim = simulator;
            Document = new ResultsDocument { Simulator = sim != null, SimulatorDescription = sim?.Options.Describe() };
        }

        public ResultsDocument Document { get; }

        public string ResultsPath { get; private set; }

        /// <summary>
        /// True while the mount clock holds the date test's simulated date: from the first :SL until the real date and
        /// time are written back. Stopping the process then would leave the mount on the wrong date.
        /// </summary>
        public bool RestorePending => restorePending;

        /// <summary>Runs every step; returns 0 when all ran, 1 when any failed or a stop was refused, 130 when aborted.</summary>
        public int Run() {
            Directory.CreateDirectory(opt.OutputDirectory);
            ResultsPath = Path.Combine(opt.OutputDirectory, "results.md");
            var started = DateTime.UtcNow;
            Document.Header.Add(("Run", $"{started.AddHours(opt.UtcOffsetHours):yyyy-MM-dd HH:mm:ss} site time (UTC{opt.UtcOffsetHours:+0;-0}) = {started:yyyy-MM-dd HH:mm:ss} UTC"));
            Document.Header.Add(("Port", opt.PortDescription));
            Document.Header.Add(("Command", opt.CommandLine));
            Document.Header.Add(("Site assumed", string.Create(Inv, $"{opt.SiteLatitude:0.00} N, {opt.SiteLongitudeEast:0.00} E, UTC{opt.UtcOffsetHours:+0;-0} (mount :SG{Lx200Format.FormatHoursToUtc(opt.UtcOffsetHours, false)})")));
            Document.Header.Add(("Pulse / guide rate / focus", string.Create(Inv, $"{opt.PulseMs} ms pulses at {(opt.GuideRateArcsecPerSec > 0 ? opt.GuideRateArcsecPerSec.ToString("0.0", Inv) + "\"/s" : "mount's rate")}, focus moves {opt.FocusMs} ms")));
            Document.Header.Add(("Trace", "trace.log (same folder)"));

            var steps = new (int Number, string Title, string Question, Action<StepRecord> Body)[] {
                (1, "Firmware", "Which firmware is this (:GVP#, :GVN#), and what are :GW#'s raw bytes: does it end with '#'?", StepFirmware),
                (2, "Coordinates and precision", "Degree byte 0xDF or '*'? Does :U# switch to long format? Is High Precision pointing (:P#) LOW?", StepCoordinates),
                (3, "Site and sidereal time", "Do :St/:Sg/:SG/:SH write the site, which longitude form works (245*49 or -114*11), and does :GS# match the LST computed for 114.18 E?", StepSite),
                (4, "Date convention (:SC)", "Does :SC take the UTC date or the local date? (Decides whether :SC may ever be written between 00:00 and 08:00 HKT.)", StepDate),
                (5, "Goto and sync", "Does :MS# + :D# polling work, what is the :D# bar byte, and does :CM# return the fixed string?", StepGotoSync),
                (6, "Pulse guiding in alt-az", "Does :Mg move the mount in alt-az, on which axes, and can two axes be pulsed back to back? If not, does :RG# + :Mx#/:Qx# work? (Decides dithering.)", StepPulseGuide),
                (7, "Focuser (#1209)", "Does :FP (mount-timed pulse) work, how do speeds :F1#-:F4# compare, how much backlash, does :fT# answer?", StepFocuser),
                (8, "Tracking :AL#/:AA#", "Does switching tracking off (:AL#) and on (:AA#) keep the alignment?", StepTracking)
            };
            var records = steps.Select(s => new StepRecord(s.Number, s.Title, s.Question)).ToList();
            Document.Steps.AddRange(records);
            Save();

            link.Transacted += OnTransacted;
            var exit = 0;
            var stopRefused = false;
            var order = ExecutionOrder.Select(n => Array.FindIndex(steps, s => s.Number == n)).ToArray();
            try {
                for (var k = 0; k < order.Length; k++) {
                    var i = order[k];
                    var rec = records[i];
                    var later = order.Skip(k + 1).Select(j => records[j]);
                    if (opt.Skip.Contains(rec.Number)) {
                        rec.Outcome = StepOutcome.Skipped;
                        rec.Summary = "skipped (--skip)";
                        Save();
                        continue;
                    }
                    token.ThrowIfCancellationRequested();
                    current = rec;
                    ask.Say("");
                    ask.Say($"=== Step {rec.Number}: {rec.Title} ===");
                    try {
                        steps[i].Body(rec);
                        if (rec.Outcome == StepOutcome.NotRun) {
                            rec.Outcome = StepOutcome.Done;
                        }
                    } catch (OperationCanceledException) {
                        rec.Outcome = StepOutcome.Aborted;
                        rec.Summary = WithPrevious("aborted (Ctrl+C or a signal)", rec.Summary);
                        throw;
                    } catch (ChecklistAbortException ex) {
                        rec.Outcome = StepOutcome.Failed;
                        rec.Summary = WithPrevious(ex.Message, rec.Summary);
                        rec.Conclusions.Add(ex.Message);
                        exit = 1;
                        stopRefused |= !StopIfMoving(rec, "checklist stopped");
                        foreach (var r in later.Where(r => r.Outcome == StepOutcome.NotRun)) {
                            r.Summary = "not run: " + ex.Message;
                        }
                        break;
                    } catch (Exception ex) {
                        rec.Outcome = StepOutcome.Failed;
                        rec.Summary = WithPrevious($"failed: {ex.Message}", rec.Summary);
                        rec.Notes.Add($"Exception {ex.GetType().Name}: {ex.Message}");
                        exit = 1;
                        stopRefused |= !StopIfMoving(rec, $"step {rec.Number} failed");
                        if (ex is Lx200DisconnectedException) {
                            foreach (var r in later) {
                                r.Summary = "not run: link lost";
                            }
                            break;
                        }
                    } finally {
                        current = null;
                        Save();
                    }
                }
            } catch (OperationCanceledException) {
                exit = 130;
                foreach (var r in records.Where(r => r.Outcome == StepOutcome.NotRun)) {
                    r.Summary = "not run (aborted)";
                }
            } finally {
                link.Transacted -= OnTransacted;
                if (link.MotionCommanded) {
                    if (link.StopAll("checklist finished or aborted after motion")) {
                        Document.Changes.Add("Stop commands (:Q#, :Qn/s/e/w#, :FQ# twice) were sent at the end because motion had been commanded.");
                    } else {
                        stopRefused = true;
                        Document.Changes.Add("STOP COMMANDS NOT ACCEPTED at the end (:Q#, :Qn/s/e/w#, :FQ# twice; NAK or no link, see trace.log): the mount or the focuser may still have been moving. Switch the mount off before anything else.");
                    }
                }
                Save();
            }
            if (exit == 0 && (stopRefused || records.Any(r => r.Outcome == StepOutcome.Failed))) {
                exit = 1;
            }
            if (stopRefused) {
                ask.Alarm(Prompter.StopNotAccepted);
            }
            ask.Say("");
            ask.Say($"Results: {ResultsPath}");
            return exit;
        }

        /// <summary>Keeps what a step had already recorded (e.g. the date test's restore result) after the reason it ended.</summary>
        private static string WithPrevious(string reason, string previous) =>
            string.IsNullOrEmpty(previous) ? reason : $"{reason}: {previous}";

        /// <summary>Sends the stop commands if motion was commanded; false when the mount did not take them all.</summary>
        private bool StopIfMoving(StepRecord rec, string reason) {
            if (!link.MotionCommanded) {
                return true;
            }
            if (link.StopAll(reason)) {
                rec.Notes.Add("Stop commands sent (:Q#, :Qn/s/e/w#, :FQ# twice) because motion had been commanded.");
                return true;
            }
            rec.Notes.Add(Prompter.StopNotAccepted);
            ask.Alarm(Prompter.StopNotAccepted);
            return false;
        }

        private void OnTransacted(Lx200Reply reply) {
            var rec = current;
            if (rec != null && rec.Recording) {
                rec.Exchanges.Add(reply);
            }
        }

        private void Save() {
            if (ResultsPath != null) {
                Document.Save(ResultsPath);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------------------------------------

        private Lx200Reply Tx(string command, ReplyShape shape = null) {
            token.ThrowIfCancellationRequested();
            return link.Send(command, shape);
        }

        /// <summary>Sends and checks the reply is complete; a resync happens inside the link on failure.</summary>
        private Lx200Reply TxOk(string command, ReplyShape shape = null) {
            var r = Tx(command, shape);
            if (!r.IsOk) {
                throw new Lx200ReplyException($"{command}: {r.Status}{(r.Error != null ? " (" + r.Error + ")" : "")}", r);
            }
            return r;
        }

        /// <summary>
        /// Sends a halt (:Q#, :Qn#, :FQ#) and makes sure the mount took it. Any command can be refused with NAK while
        /// the mount is busy (P07 l.10-13), so a refused halt is sent again for up to <see cref="HaltRetryTime"/>; if
        /// it is still refused the run stops, because the motor may still be running. Sent even after Ctrl+C.
        /// </summary>
        private Lx200Reply Halt(string command) {
            var sw = Stopwatch.StartNew();
            while (true) {
                var r = link.Send(command);
                if (r.Status is ReplyStatus.NoReplyExpected or ReplyStatus.FramingError) {
                    return r;
                }
                if (r.Status == ReplyStatus.Disconnected || sw.Elapsed > HaltRetryTime) {
                    throw new ChecklistAbortException(string.Create(Inv, $"halt {command} not accepted ({r.Status}, still refused after {sw.Elapsed.TotalSeconds:0.0} s): the mount or the focuser may still be moving. Switch the mount off."));
                }
                Thread.Sleep(100);
            }
        }

        private T Read<T>(string command, Func<string, T> parse) {
            token.ThrowIfCancellationRequested();
            return link.Query(command, parse).Value;
        }

        private void Wait(double seconds) {
            if (seconds <= 0) {
                return;
            }
            if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds))) {
                token.ThrowIfCancellationRequested();
            }
        }

        /// <summary>Asks before a hardware/config action; a "no" marks the whole step as declined.</summary>
        private bool ConfirmStep(StepRecord s, string question) {
            var ok = ask.Confirm(question);
            s.Observations.Add($"{(ok ? "Confirmed" : "Declined")}: {FirstLine(question)}");
            if (!ok) {
                s.Outcome = StepOutcome.Declined;
                s.Summary = "declined by operator";
            }
            return ok;
        }

        /// <summary>Asks before an optional part of a step; a "no" only skips that part.</summary>
        private bool ConfirmPart(StepRecord s, string question) {
            var ok = ask.Confirm(question);
            s.Observations.Add($"{(ok ? "Confirmed" : "Skipped")}: {FirstLine(question)}");
            return ok;
        }

        /// <summary>Operator observation, or the simulator's ground truth in a simulator run.</summary>
        private string Observe(StepRecord s, string question, Func<string> simTruth = null) {
            if (sim != null && simTruth != null) {
                var truth = simTruth();
                ask.Say($"{question} -> (simulator) {truth}");
                s.Observations.Add($"{question} -> simulator ground truth: {truth}");
                return truth;
            }
            var answer = ask.Ask(question);
            s.Observations.Add($"{question} -> {(answer.Length == 0 ? "(no answer)" : answer)}");
            return answer;
        }

        /// <summary>
        /// Makes sure replies are in long format (1 s RA, 1" Dec) before a step measures small moves: read :GR#, and if
        /// it is short, send :U# and read again (RIM MNT-09). Needed when step 2 was skipped, e.g. a pulse-only rerun
        /// after the mount was power-cycled. :U# only changes the serial format, so no confirmation is needed.
        /// </summary>
        private CoordinatePrecision EnsureHighPrecision(StepRecord s) {
            if (precision == CoordinatePrecision.High) {
                return CoordinatePrecision.High;
            }
            var p = Lx200Format.DetectPrecision(TxOk(":GR#").Value);
            if (p == CoordinatePrecision.Low) {
                Tx(":U#");
                p = Lx200Format.DetectPrecision(TxOk(":GR#").Value);
                s.Notes.Add(p == CoordinatePrecision.High
                    ? "Replies were in short format, so :U# switched them to long format first (step 2 was not run in this session)."
                    : ":U# did not switch to long format: positions resolve 1' only, so small moves cannot be measured.");
                if (p == CoordinatePrecision.High) {
                    Document.Changes.Add("Coordinate format (:U#): switched to High precision (serial format only).");
                }
            }
            precision = p;
            return p;
        }

        private Pos ReadPos() {
            var t0 = link.Trace.UtcNow;
            var ra = Read(":GR#", Lx200Format.ParseRaHours);
            var dec = Read(":GD#", Lx200Format.ParseDegrees);
            var alt = Read(":GA#", Lx200Format.ParseDegrees);
            var az = Read(":GZ#", Lx200Format.ParseDegrees);
            var t1 = link.Trace.UtcNow;
            return new Pos(ra, dec, alt, az, t0 + TimeSpan.FromTicks((t1 - t0).Ticks / 2));
        }

        private static string FirstLine(string text) {
            var i = text.IndexOf('\n');
            return i < 0 ? text : text[..i];
        }

        private static string Hms(double hours) {
            var s = (long)Math.Round(Lx200Astro.Wrap(hours, 24) * 3600) % 86400;
            return string.Create(Inv, $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}");
        }

        private static string Dms(double degrees) {
            var s = (long)Math.Round(Math.Abs(degrees) * 3600);
            return string.Create(Inv, $"{(degrees < 0 && s > 0 ? "-" : "+")}{s / 3600:00}°{s / 60 % 60:00}'{s % 60:00}\"");
        }

        private static string Sec(double seconds) => seconds.ToString("+0.0;-0.0;0.0", Inv);

        /// <summary>What an LST error (mount :GS# minus computed) most likely means.</summary>
        internal static string ExplainLstError(double seconds) {
            var abs = Math.Abs(seconds);
            if (abs <= 15) {
                return "matches: time, date and longitude are consistent";
            }
            if (Math.Abs(abs - 236.6) <= 30) {   // one solar day = 236.6 s of sidereal time (0.9856°/day)
                return $"off by about one day of sidereal drift (3m56s): the DATE is {(seconds > 0 ? "one day late" : "one day early")}";
            }
            var hours = abs / 3600.0;
            if (hours >= 0.9 && Math.Abs(hours - Math.Round(hours)) * 3600 <= 90) {
                return $"off by about {Math.Round(hours):0} h: the UTC offset (:SG sign or value) or the time is wrong";
            }
            return $"off by {abs:0} s: the clock or the longitude is wrong";
        }

        internal readonly record struct Pos(double Ra, double Dec, double Alt, double Az, DateTime Utc);

        /// <summary>Stops the run: nothing after this step can work (no link, no answer to ACK).</summary>
        private sealed class ChecklistAbortException : Exception {

            public ChecklistAbortException(string message) : base(message) {
            }
        }
    }
}
