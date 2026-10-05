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
using System.Linq;
using System.Threading;

namespace NINA.Mac.Lx200Probe {

    /// <summary>
    /// Interactive byte-level console: type a command, see the reply bytes (hex + printable) and timing.
    /// Blocklisted commands are refused, motion and persistent config writes need a "y".
    /// </summary>
    public sealed class RawConsole {

        private static readonly Dictionary<string, string> Warnings = new(StringComparer.Ordinal) {
            ["SC"] = "Writes the DATE. Whether it is the UTC or the local date is disputed (checklist step 4); a wrong date shifts every goto by ~1° and corrupts the alignment. Never between 00:00 and 08:00 HKT until settled.",
            ["SL"] = "Writes the clock. After alignment a clock change shifts every goto (plan risk 3).",
            ["SG"] = "Writes the UTC offset (Hong Kong = -08). After alignment this shifts every goto.",
            ["St"] = "Writes the site latitude. After alignment this shifts every goto.",
            ["Sg"] = "Writes the site longitude. After alignment this shifts every goto.",
            ["SH"] = "Writes the daylight-saving flag (Hong Kong has none).",
            ["CM"] = "Sync: the pointing model is rewritten near the current target.",
            ["P"] = "Toggles High Precision POINTING (a handbox setting). It must end LOW: HIGH makes every goto wait for ENTER.",
            ["AL"] = "Land mode: tracking stops.",
            ["AA"] = "Alt-az mode: tracking starts.",
            ["MS"] = "SLEWS the mount to the :Sr/:Sd target. Stand clear; .stop sends :Q#.",
            ["MA"] = "SLEWS the mount to the :Sa/:Sz target. Stand clear; .stop sends :Q#.",
            ["Mn"] = "Moves the mount until :Qn# or .stop.",
            ["Ms"] = "Moves the mount until :Qs# or .stop.",
            ["Me"] = "Moves the mount until :Qe# or .stop.",
            ["Mw"] = "Moves the mount until :Qw# or .stop.",
            ["F+"] = "Runs the focuser until :FQ# or .stop.",
            ["F-"] = "Runs the focuser until :FQ# or .stop.",
            ["FP"] = "Runs the focuser for the given milliseconds, timed by the mount (+ = inward). .stop sends :FQ#.",
            ["Mg"] = "Pulse-guides the mount for the given milliseconds at the guide rate; it stops by itself. .stop sends :Q#.",
            ["hI"] = "Answers the startup daylight-saving/date/time prompt with this date and time.",
            ["Rg"] = "Changes the guide rate (handbox guide-rate setting)."
        };

        private readonly Lx200Connection link;
        private readonly Prompter ask;
        private readonly CancellationToken token;

        public RawConsole(Lx200Connection link, Prompter prompter, CancellationToken token) {
            this.link = link;
            ask = prompter;
            this.token = token;
        }

        /// <summary>Returns 0, or 1 when the stop commands at the end were not all accepted.</summary>
        public int Run() {
            Help();
            var exit = 0;
            try {
                while (!token.IsCancellationRequested) {
                    ask.Console.Write("lx200> ");
                    var line = ask.Console.ReadLine();
                    if (line == null) {
                        break;
                    }
                    line = line.Trim();
                    if (line.Length == 0) {
                        continue;
                    }
                    if (line.StartsWith('.')) {
                        if (!Meta(line)) {
                            break;
                        }
                        continue;
                    }
                    Execute(line, null);
                }
            } finally {
                if (link.MotionCommanded) {
                    ask.Say("Motion was commanded in this session: sending stop commands.");
                    if (!link.StopAll("raw console exit")) {
                        ask.Alarm(Prompter.StopNotAccepted);
                        exit = 1;
                    }
                }
            }
            return exit;
        }

        private void Help() {
            ask.Say($"Raw LX200 console on {link.PortName}. Type a command (':GR#', 'GR', 'ack'); '.help' lists the meta commands.");
            ask.Say("Replies are shown as hex + printable text with timing. Blocklisted commands are refused; motion and config writes ask first.");
        }

        /// <summary>Returns false to quit.</summary>
        private bool Meta(string line) {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant()) {
                case ".quit":
                case ".exit":
                case ".q":
                    return false;
                case ".help":
                case ".h":
                    ask.Say(".stop                 :Q#, :Qn/s/e/w#, :FQ# twice");
                    ask.Say(".resync               drain, ACK, expect A/L/P/D");
                    ask.Say(".shape NAME CMD       send CMD reading the reply as NAME: none, char, bool, hash, ms, sc, degree, drain, fixedN");
                    ask.Say(".catalog [text]       list catalog commands (reply shape, effect, source)");
                    ask.Say(".blocked              list refused commands and why");
                    ask.Say(".trailing MS          keep listening MS ms after each reply for unexpected bytes (now " + link.Options.TrailingWindow.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + ")");
                    ask.Say(".quit                 leave (sends stops if anything moved)");
                    return true;
                case ".stop":
                    if (link.StopAll("operator .stop")) {
                        ask.Say("Stop commands sent.");
                    } else {
                        ask.Alarm(Prompter.StopNotAccepted);
                    }
                    return true;
                case ".resync":
                    ask.Say(link.Resync() ? "Resync OK." : "Resync FAILED: no sane answer to ACK.");
                    return true;
                case ".shape":
                    if (parts.Length < 3 || !ReplyShape.TryParse(parts[1], out var shape)) {
                        ask.Say("usage: .shape NAME CMD   (NAME: none, char, bool, hash, ms, sc, degree, drain, fixedN)");
                        return true;
                    }
                    Execute(parts[2], shape);
                    return true;
                case ".catalog":
                    var filter = parts.Length > 1 ? parts[1] : null;
                    foreach (var c in Lx200Catalog.All.Where(c => !c.IsBlocked && (filter == null || c.Code.Contains(filter, StringComparison.Ordinal) || c.Purpose.Contains(filter, StringComparison.OrdinalIgnoreCase)))) {
                        ask.Say($"  {c.Example,-20} {c.Effect,-13} {c.Reply,-45} {c.Purpose}  [{c.Source}]");
                    }
                    return true;
                case ".blocked":
                    foreach (var c in Lx200Catalog.BlockedCommands) {
                        ask.Say($"  :{c.Code}...#  {c.Purpose}: {c.BlockedReason} [{c.Source}]");
                    }
                    return true;
                case ".trailing":
                    if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)) {
                        link.Options.TrailingWindow = TimeSpan.FromMilliseconds(ms);
                    }
                    ask.Say($"Trailing window {link.Options.TrailingWindow.TotalMilliseconds:0} ms.");
                    return true;
                default:
                    ask.Say($"Unknown meta command '{parts[0]}' (.help).");
                    return true;
            }
        }

        private void Execute(string text, ReplyShape shape) {
            Lx200Command cmd;
            try {
                cmd = Lx200Command.Parse(text);
            } catch (Lx200MalformedCommandException ex) {
                ask.Say($"Not sent: {ex.Message}");
                return;
            }
            if (cmd.Spec?.IsBlocked == true) {
                ask.Say($"REFUSED {cmd.Text}: {cmd.Spec.Purpose}. {cmd.Spec.BlockedReason} [{cmd.Spec.Source}]");
                return;
            }
            if (cmd.NeedsConfirmation) {
                var what = cmd.Spec == null
                    ? "Not in the catalog: effect unknown, treated as possible motion; the reply is read until the line is quiet."
                    : $"{cmd.Spec.Purpose} ({cmd.Effect}).";
                var key = cmd.Spec?.Code ?? "";
                if (key.StartsWith("Mg", StringComparison.Ordinal)) {
                    key = "Mg";
                }
                var warning = Warnings.TryGetValue(key, out var w) ? "\n  " + w : "";
                if (!ask.Confirm($"{cmd.Text}: {what}{warning}")) {
                    ask.Say("Not sent.");
                    return;
                }
            }
            try {
                var reply = link.Send(cmd, shape);
                ask.Say("  " + reply.Describe());
                if (reply.Parts.Count > 1) {
                    ask.Say("  parts: " + string.Join(" | ", reply.Parts.Select(p => $"\"{Lx200Format.Printable(p)}\"")));
                }
                ask.Say($"  shape: {reply.Shape}{(cmd.Spec != null ? $"; {cmd.Spec.Effect}; {cmd.Spec.Source}" : "; not in the catalog")}");
            } catch (Lx200BlockedCommandException ex) {
                ask.Say(ex.Message);
            }
        }
    }
}
