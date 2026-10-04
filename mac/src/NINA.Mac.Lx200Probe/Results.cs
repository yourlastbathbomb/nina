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
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NINA.Mac.Lx200Probe {

    public enum StepOutcome {
        NotRun,
        Done,
        Skipped,
        Declined,
        Failed,
        Aborted
    }

    /// <summary>One checklist step: its raw exchanges, operator observations and the conclusions they support.</summary>
    public sealed class StepRecord {

        public StepRecord(int number, string title, string question) {
            Number = number;
            Title = title;
            Question = question;
        }

        public int Number { get; }

        public string Title { get; }

        /// <summary>The bench unknown this step settles (plan section 5).</summary>
        public string Question { get; }

        public StepOutcome Outcome { get; set; } = StepOutcome.NotRun;

        /// <summary>One line for the summary table.</summary>
        public string Summary { get; set; } = "";

        public List<Lx200Reply> Exchanges { get; } = new();

        public List<string> Notes { get; } = new();

        public List<string> Observations { get; } = new();

        public List<string> Conclusions { get; } = new();

        /// <summary>When false, transactions are not added to <see cref="Exchanges"/> (polling loops).</summary>
        public bool Recording { get; set; } = true;
    }

    /// <summary>Builds results.md: header, summary table, then every step with raw replies and conclusions.</summary>
    public sealed class ResultsDocument {

        public List<(string Key, string Value)> Header { get; } = new();

        public List<StepRecord> Steps { get; } = new();

        public List<string> Changes { get; } = new();

        public bool Simulator { get; set; }

        public string SimulatorDescription { get; set; }

        public string Render() {
            var sb = new StringBuilder();
            sb.AppendLine("# M2 bench results: LX200GPS mount probe");
            sb.AppendLine();
            if (Simulator) {
                sb.AppendLine("> **Simulator run.** Every reply below came from the built-in Autostar II simulator, so the conclusions describe");
                sb.AppendLine($"> its configured quirks ({Escape(SimulatorDescription)}), not your mount.");
                sb.AppendLine();
            }
            sb.AppendLine("| | |");
            sb.AppendLine("|---|---|");
            foreach (var (k, v) in Header) {
                sb.AppendLine($"| {Escape(k)} | {Escape(v)} |");
            }
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| # | Step | Outcome | Result |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var s in Steps) {
                sb.AppendLine($"| {s.Number} | {Escape(s.Title)} | {s.Outcome} | {Escape(s.Summary)} |");
            }
            sb.AppendLine();
            if (Changes.Count > 0) {
                sb.AppendLine("## What the probe changed on the mount");
                sb.AppendLine();
                foreach (var c in Changes) {
                    sb.AppendLine($"- {c}");
                }
                sb.AppendLine();
            }
            foreach (var s in Steps) {
                RenderStep(sb, s);
            }
            sb.AppendLine("---");
            sb.AppendLine("Byte-level detail of every exchange (with timestamps) is in `trace.log` next to this file.");
            return sb.ToString();
        }

        public void Save(string path) {
            File.WriteAllText(path, Render(), new UTF8Encoding(false));
        }

        private static void RenderStep(StringBuilder sb, StepRecord s) {
            sb.AppendLine($"## {s.Number}. {s.Title}");
            sb.AppendLine();
            sb.AppendLine($"*Question:* {s.Question}");
            sb.AppendLine();
            sb.AppendLine($"*Outcome:* **{s.Outcome}**");
            sb.AppendLine();
            if (s.Conclusions.Count > 0) {
                sb.AppendLine("### Conclusions");
                sb.AppendLine();
                foreach (var c in s.Conclusions) {
                    sb.AppendLine($"- {c}");
                }
                sb.AppendLine();
            }
            if (s.Observations.Count > 0) {
                sb.AppendLine("### Operator observations");
                sb.AppendLine();
                foreach (var o in s.Observations) {
                    sb.AppendLine($"- {o}");
                }
                sb.AppendLine();
            }
            if (s.Notes.Count > 0) {
                sb.AppendLine("### Notes");
                sb.AppendLine();
                foreach (var n in s.Notes) {
                    sb.AppendLine($"- {n}");
                }
                sb.AppendLine();
            }
            if (s.Exchanges.Count > 0) {
                sb.AppendLine("### Raw replies");
                sb.AppendLine();
                sb.AppendLine("| Time (UTC) | Sent | Reply hex | Reply text | Shape | Status | First byte ms | Total ms | Notes |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
                foreach (var r in s.Exchanges) {
                    var notes = new List<string>();
                    if (r.NakCount > 0) {
                        notes.Add($"{r.NakCount} NAK");
                    }
                    if (r.Stale.Length > 0) {
                        notes.Add($"stale dropped: {Lx200Trace.Hex(r.Stale)}");
                    }
                    if (r.Trailing.Length > 0) {
                        notes.Add($"TRAILING: {Lx200Trace.Hex(r.Trailing)}");
                    }
                    if (r.Shape.Kind is ReplyKind.FixedThenOptionalHash or ReplyKind.OneOf or ReplyKind.UntilDegreeThenOptionalHash) {
                        notes.Add(r.TerminatorSeen ? "'#' present" : "no '#'");
                    }
                    if (r.Resynced) {
                        notes.Add("resynced");
                    }
                    if (r.Error != null) {
                        notes.Add(r.Error);
                    }
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"| {r.SentUtc:HH:mm:ss.fff} | `{Code(r.Command.Text)}` | {(r.Raw.Length > 0 ? r.RawHex : "-")} | {(r.Raw.Length > 0 ? "`" + Code(Lx200Format.Printable(r.RawText)) + "`" : "-")} | {Escape(r.Shape.ToString())} | {r.Status} | {(double.IsNaN(r.FirstByteMs) ? "-" : r.FirstByteMs.ToString("0.0", CultureInfo.InvariantCulture))} | {r.TotalMs:0.0} | {Escape(string.Join("; ", notes))} |"));
                }
                sb.AppendLine();
            }
        }

        public static string Escape(string text) => (text ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

        private static string Code(string text) => (text ?? "").Replace("`", "'").Replace("|", "\\|");
    }
}
