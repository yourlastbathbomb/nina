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
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace NINA.Mac.Lx200Probe {

    public sealed partial class Checklist {

        private const string FixedSyncReply = " M31 EX GAL MAG 3.5 SZ178.0'";

        // =============================================================================================
        // 1. Firmware
        // =============================================================================================

        private void StepFirmware(StepRecord s) {
            var ack = link.Ack();
            if (!ack.IsOk) {
                ack = link.Ack();
            }
            if (!ack.IsOk) {
                throw new ChecklistAbortException($"no answer to ACK (0x06): {ack.Status}. Check the cable, the port, that the mount is on, and that it was not sent :hP# (power-cycle).");
            }
            s.Conclusions.Add(string.Create(Inv, $"Link OK: ACK (0x06) answers '{ack.Value}' = {ModeName(ack.Value[0])}, first byte after {ack.FirstByteMs:0.0} ms."));

            var gvp = Tx(":GVP#");
            var gvn = Tx(":GVN#");
            var gvd = Tx(":GVD#");
            var gvt = Tx(":GVT#");
            var product = gvp.IsOk ? gvp.Value : null;
            var firmware = gvn.IsOk ? gvn.Value : null;
            s.Conclusions.Add(product == "LX2001"
                ? "Product :GVP# = LX2001: the LX200GPS family, as expected."
                : $"Product :GVP# = '{Lx200Format.Printable(product ?? gvp.Status.ToString())}': NOT the expected LX2001; check the driver's model table.");
            s.Conclusions.Add(firmware switch {
                "4.2g" => "Firmware 4.2g = stock Meade, WITHOUT the GPS week-rollover fix: the GPS date set during Automatic Align can be wrong, so the driver must verify :GC#/:GL#/:GS# at every connect (RVM MNT-08). Meade.net refuses :Mg in alt-az on this firmware; step 6 decides for this mount.",
                "4.2k" => "Firmware 4.2k = Meade's update with the GPS rollover fix.",
                "4.2G" => "Firmware 4.2G = StarPatch (GPS rollover fix; Meade.net allows alt-az pulse guiding on it).",
                null => $":GVN# did not answer ({gvn.Status}).",
                _ => $"Firmware '{Lx200Format.Printable(firmware)}' is not one of 4.2g / 4.2k / 4.2G: record it for the driver's firmware table."
            });
            if (gvd.IsOk || gvt.IsOk) {
                s.Conclusions.Add($"Firmware date/time: '{Lx200Format.Printable(gvd.Value)}' '{Lx200Format.Printable(gvt.Value)}'.");
            }

            // :GW# is undocumented: read everything that arrives to learn its true framing
            var gw = Tx(":GW#", ReplyShape.Drain);
            string gwSummary;
            if (gw.Status == ReplyStatus.Silent) {
                gwSummary = "no reply";
                s.Conclusions.Add(":GW# is not answered: this firmware has no :GW#. The driver must use ACK for the mount mode and cannot read tracking/alignment state.");
            } else {
                var text = gw.RawText;
                var hasHash = text.EndsWith('#');
                var body = hasHash ? text[..^1] : text;
                gwSummary = $"'{Lx200Format.Printable(text)}'";
                if (body.Length == 3) {
                    s.Conclusions.Add($":GW# = '{Lx200Format.Printable(body)}' ({gw.Raw.Length} bytes: 3 chars {(hasHash ? "THEN '#'" : "and NO '#'")}) -> mode '{body[0]}' ({ModeName(body[0])}), tracking '{body[1]}' ({(body[1] == 'T' ? "tracking" : body[1] == 'N' ? "not tracking" : "?")}), alignment '{body[2]}'. "
                        + $"The catalog shape (3 chars + optional '#') handles this; {(hasHash ? "INDIGO's read-to-'#' is right" : "Meade.net's 3-char read is right")}.");
                } else {
                    s.Conclusions.Add($":GW# = '{Lx200Format.Printable(text)}' ({gw.Raw.Length} bytes): UNEXPECTED length; the catalog assumes 3 chars + optional '#'. Fix the GW reply shape before M4.");
                }
            }
            s.Summary = $"{Lx200Format.Printable(product ?? "?")} {Lx200Format.Printable(firmware ?? "?")}; :GW# {gwSummary}";
        }

        // =============================================================================================
        // 2. Coordinates and precision
        // =============================================================================================

        private void StepCoordinates(StepRecord s) {
            var first = ReadCoordinateReplies();
            var p0 = Lx200Format.DetectPrecision(first.Gr.Value);
            var glyphs = new[] { first.Gd, first.Ga, first.Gz }.Select(r => Lx200Format.DescribeDegreeGlyph(r.Raw)).Distinct().ToList();
            s.Conclusions.Add($"Degree glyph in :GD#/:GA#/:GZ# replies: {string.Join(" / ", glyphs)}{(glyphs.Count > 1 ? " (MIXED)" : "")}. The parser accepts 0xDF, '*' and ':'; replies must be read as bytes/Latin-1 (.NET's default ASCII would turn 0xDF into '?').");
            s.Conclusions.Add($"Format at start: {p0} ({(p0 == CoordinatePrecision.Low ? "HH:MM.T sDD*MM" : "HH:MM:SS sDD*MM:SS")}): :GR# '{Lx200Format.Printable(first.Gr.Value)}', :GD# '{Lx200Format.Printable(first.Gd.Value)}', :GA# '{Lx200Format.Printable(first.Ga.Value)}', :GZ# '{Lx200Format.Printable(first.Gz.Value)}'.");

            Tx(":U#");
            var second = ReadCoordinateReplies();
            var p1 = Lx200Format.DetectPrecision(second.Gr.Value);
            s.Conclusions.Add($"After :U#: {p1}: :GR# '{Lx200Format.Printable(second.Gr.Value)}', :GD# '{Lx200Format.Printable(second.Gd.Value)}', :GA# '{Lx200Format.Printable(second.Ga.Value)}', :GZ# '{Lx200Format.Printable(second.Gz.Value)}'.");
            if (p1 == p0) {
                s.Conclusions.Add(":U# did NOT change the format: detect precision from :GR# length instead of trusting the toggle.");
            }
            var final = p1;
            if (p1 == CoordinatePrecision.Low) {
                Tx(":U#");
                final = Lx200Format.DetectPrecision(TxOk(":GR#").Value);
            }
            precision = final;
            s.Conclusions.Add($"Left in {final} precision{(final == CoordinatePrecision.High ? " (what the driver wants: 1 s RA, 1\" Dec)" : ": could not reach long format")}.");

            var longReply = p0 == CoordinatePrecision.High ? first.Gd : p1 == CoordinatePrecision.High ? second.Gd : null;
            var separator = "?";
            if (longReply != null) {
                var m = Regex.Match(longReply.Value, @"^[+-]?\d+.\d\d(.)\d\d$");
                if (m.Success) {
                    separator = m.Groups[1].Value == "'" ? "apostrophe (0x27)" : $"'{Lx200Format.Printable(m.Groups[1].Value)}' (0x{(int)m.Groups[1].Value[0]:X2})";
                }
                s.Conclusions.Add($"Minutes/seconds separator in long angle replies: {separator}.");
            }

            // Azimuth convention: compare :GA#/:GZ# with alt/az computed from :GR#/:GD#, :GS# and :Gt#
            var lst = Read(":GS#", Lx200Format.ParseRaHours);
            var lat = Read(":Gt#", Lx200Format.ParseDegrees);
            var pos = ReadPos();
            var (calcAlt, calcAz) = Lx200Astro.ToAltAz(pos.Ra, pos.Dec, lst, lat);
            var dAlt = pos.Alt - calcAlt;
            var dAz = Lx200Astro.DegreeDifference(pos.Az, calcAz);
            var dAzSouth = Lx200Astro.DegreeDifference(pos.Az, calcAz + 180);
            string azConvention;
            if (Math.Abs(dAlt) < 0.5 && Math.Abs(dAz) < 0.5) {
                azConvention = "North=0, East=90";
                s.Conclusions.Add(string.Create(Inv, $"Azimuth is counted from North through East: :GA#/:GZ# match the alt/az computed from :GR#/:GD#, :GS# and :Gt# within {Math.Abs(dAlt) * 60:0.0}'/{Math.Abs(dAz) * 60:0.0}'."));
            } else if (Math.Abs(dAlt) < 0.5 && Math.Abs(dAzSouth) < 0.5) {
                azConvention = "South=0";
                s.Conclusions.Add("Azimuth is counted from SOUTH (classic LX200 convention): the driver must add 180° to :GZ#.");
            } else {
                azConvention = "inconsistent";
                s.Conclusions.Add(string.Create(Inv, $":GA#/:GZ# do not match the alt/az computed from RA/Dec + :GS# + :Gt# (dAlt {dAlt:+0.00;-0.00}°, dAz {dAz:+0.00;-0.00}°): site, time or alignment is inconsistent; re-check after step 3."));
            }

            var hp = "not checked";
            if (ConfirmPart(s, ":P# toggles High Precision POINTING (a stored handbox setting; when HIGH every goto stops at a bright star and waits for ENTER).\n  The probe toggles it, toggles back if needed, and leaves it LOW.")) {
                var r1 = Tx(":P#");
                if (r1.Value == "HIGH PRECISION") {
                    var r2 = Tx(":P#");
                    hp = r2.Value == "LOW PRECISION" ? "was LOW, now LOW" : $"was LOW, second toggle answered '{Lx200Format.Printable(r2.RawText)}'";
                    s.Conclusions.Add($":P# answered 'HIGH PRECISION' then '{Lx200Format.Printable(r2.Value)}': High Precision pointing was OFF and is OFF again (good). Replies are {r1.Raw.Length}/{r2.Raw.Length} bytes{(r1.TerminatorSeen ? " followed by '#'" : " with no '#'")}.");
                } else if (r1.Value == "LOW PRECISION") {
                    hp = "was HIGH, now LOW";
                    s.Conclusions.Add($":P# answered 'LOW PRECISION': High Precision pointing WAS ON (every goto would have waited for ENTER) and is now OFF. {r1.Raw.Length} bytes{(r1.TerminatorSeen ? " followed by '#'" : ", no '#'")}.");
                } else {
                    hp = $"unexpected reply ({r1.Status})";
                    s.Conclusions.Add($":P# reply '{Lx200Format.Printable(r1.RawText)}' ({r1.Status}) is neither documented string: check the shape in the trace; set High Precision OFF on the handbox.");
                }
                Document.Changes.Add($"High Precision pointing (:P#): {hp}.");
            }
            Document.Changes.Add($"Coordinate format (:U#): left in {final} precision (serial format only).");
            s.Summary = $"degree {string.Join("/", glyphs)}, sec. separator {separator}; start {p0} -> {final}; HP pointing {hp}; azimuth {azConvention}";
        }

        private (Lx200Reply Gr, Lx200Reply Gd, Lx200Reply Ga, Lx200Reply Gz) ReadCoordinateReplies() =>
            (TxOk(":GR#"), TxOk(":GD#"), TxOk(":GA#"), TxOk(":GZ#"));

        // =============================================================================================
        // 3. Site and LST
        // =============================================================================================

        private void StepSite(StepRecord s) {
            var gt = TxOk(":Gt#");
            var gg = TxOk(":Gg#");
            var gG = TxOk(":GG#");
            var gh = Tx(":GH#");
            var gl = TxOk(":GL#");
            var gc = TxOk(":GC#");
            var gs = TxOk(":GS#");
            var lat = Lx200Format.ParseDegrees(gt.Value);
            var lon = Lx200Format.ParseLongitudeEast(gg.Value);
            var toUtc = Lx200Format.ParseHoursToUtc(gG.Value);
            s.Conclusions.Add(string.Create(Inv, $"Stored site: :Gt# '{Lx200Format.Printable(gt.Value)}' = {lat:0.000}°, :Gg# '{Lx200Format.Printable(gg.Value)}' = {lon:0.000}° E ({LongitudeForm(gg.Value)}), :GG# '{gG.Value}' (hours added to local time to give UTC; Hong Kong should be -08), :GH# '{Lx200Format.Printable(gh.RawText)}'."));

            var mountUtc = Lx200Format.ParseDate(gc.Value) + Lx200Format.ParseTime(gl.Value) + TimeSpan.FromHours(toUtc);
            var clockError = (mountUtc - gc.SentUtc).TotalSeconds;
            s.Conclusions.Add(string.Create(Inv, $"Mount clock: :GC# {gc.Value} :GL# {gl.Value} read as the LOCAL date (P07 l.264) gives {mountUtc:yyyy-MM-dd HH:mm:ss} UTC, {Sec(clockError)} s vs this Mac.{(Math.Abs(clockError) > 3000 ? " A whole-day or whole-hour error here points at the date convention (step 4) or the UTC offset." : "")}"));
            var lstErr = LstError(gs, lon);
            s.Conclusions.Add(string.Create(Inv, $"Before writing: :GS# {gs.Value} vs LST computed from this Mac's UTC and the mount's longitude: {Sec(lstErr)} s ({ExplainLstError(lstErr)})."));
            mountLatitude = lat;
            mountLongitudeEast = lon;

            var westForm = Lx200Format.FormatLongitudeWest360(opt.SiteLongitudeEast);
            var signedForm = Lx200Format.FormatLongitudeSigned(opt.SiteLongitudeEast);
            var sg = Lx200Format.FormatHoursToUtc(opt.UtcOffsetHours, false);
            if (!ConfirmStep(s, $"Write the site: :St{Lx200Format.FormatLatitude(opt.SiteLatitude)}# :Sg{westForm}# (then also :Sg{signedForm}# to compare) :SG{sg}# :SH0#.\n  Same values as the handbox site = no change; different values shift every goto after alignment.")) {
                s.Summary = string.Create(Inv, $"read-only: lat {Lx200Format.Printable(gt.Value)}, lon {Lx200Format.Printable(gg.Value)}, :GG# {gG.Value}; LST {Sec(lstErr)} s ({ExplainLstError(lstErr)}); writes declined");
                s.Outcome = StepOutcome.Done;
                return;
            }

            // Each form is judged by how :GS# moved from its value before the write, so the mount clock's own error
            // (a GPS-rollover date, a time set by hand without a fix) cancels out; that error is reported above.
            var st = TxOk($":St{Lx200Format.FormatLatitude(opt.SiteLatitude)}#");
            var sgA = TxOk($":Sg{westForm}#");
            var ggA = TxOk(":Gg#");
            var errA = SiteLstError(TxOk(":GS#"), opt.SiteLongitudeEast, lstErr);
            var sgB = TxOk($":Sg{signedForm}#");
            var ggB = TxOk(":Gg#");
            var errB = SiteLstError(TxOk(":GS#"), opt.SiteLongitudeEast, lstErr);
            var okA = sgA.Value == "1" && Math.Abs(errA) <= 15 && Math.Abs(Lx200Format.ParseLongitudeEast(ggA.Value) - opt.SiteLongitudeEast) < 0.02;
            var okB = sgB.Value == "1" && Math.Abs(errB) <= 15 && Math.Abs(Lx200Format.ParseLongitudeEast(ggB.Value) - opt.SiteLongitudeEast) < 0.02;
            s.Conclusions.Add(string.Create(Inv, $":St{Lx200Format.FormatLatitude(opt.SiteLatitude)}# -> '{st.Value}'."));
            var judged = string.Create(Inv, $"vs the LST for {opt.SiteLongitudeEast:0.00} E with the mount clock's {Sec(lstErr)} s taken out");
            s.Conclusions.Add(string.Create(Inv, $":Sg{westForm}# (0-360 westward, Meade.net/INDIGO) -> '{sgA.Value}', reads back '{Lx200Format.Printable(ggA.Value)}', :GS# {Sec(errA)} s {judged}: {(okA ? "WORKS" : "does not give the right site")}."));
            s.Conclusions.Add(string.Create(Inv, $":Sg{signedForm}# (signed, East negative, INDI-style) -> '{sgB.Value}', reads back '{Lx200Format.Printable(ggB.Value)}', :GS# {Sec(errB)} s {judged}: {(okB ? "WORKS" : "does not give the right site")}."));
            var finalForm = okA || !okB ? westForm : signedForm;
            if (finalForm != signedForm) {
                TxOk($":Sg{finalForm}#");
            }
            s.Conclusions.Add($"Driver should send :Sg{finalForm}#{(okA && okB ? " (both forms work)" : "")}.");

            var sgReply = TxOk($":SG{sg}#");
            var sgUsed = sg;
            if (sgReply.Value != "1") {
                sgUsed = Lx200Format.FormatHoursToUtc(opt.UtcOffsetHours, true);
                sgReply = TxOk($":SG{sgUsed}#");
            }
            s.Conclusions.Add($":SG{sgUsed}# -> '{sgReply.Value}'{(sgUsed != sg ? $" (the integer form :SG{sg}# was rejected)" : " (integer form accepted)")}.");
            var sh = TxOk(":SH0#");
            s.Conclusions.Add($":SH0# (no daylight saving) -> '{sh.Value}'.");

            var gtF = TxOk(":Gt#");
            var ggF = TxOk(":Gg#");
            var gGF = TxOk(":GG#");
            var gsF = TxOk(":GS#");
            mountLatitude = Lx200Format.ParseDegrees(gtF.Value);
            mountLongitudeEast = Lx200Format.ParseLongitudeEast(ggF.Value);
            var errF = LstError(gsF, opt.SiteLongitudeEast);
            var siteF = SiteLstError(gsF, opt.SiteLongitudeEast, lstErr);
            s.Conclusions.Add(string.Create(Inv, $"After writing: :Gt# '{Lx200Format.Printable(gtF.Value)}', :Gg# '{Lx200Format.Printable(ggF.Value)}' ({LongitudeForm(ggF.Value)}), :GG# '{gGF.Value}'; :GS# {gsF.Value} vs LST computed for {opt.SiteLongitudeEast:0.00} E: {Sec(errF)} s ({ExplainLstError(errF)}); {Sec(siteF)} s with the mount clock's error taken out."));
            s.Conclusions.Add(string.Create(Inv, $"The mount stores the site to 1' ({mountLatitude:0.0000}°, {mountLongitudeEast:0.0000}°): NINA's 0.001° site tolerance would prompt on every connect (RVM MNT-14); the driver should accept 1'."));
            Document.Changes.Add($"Site: :St{Lx200Format.FormatLatitude(opt.SiteLatitude)}# :Sg{finalForm}# :SG{sgUsed}# :SH0#.");
            s.Summary = string.Create(Inv, $":Gg# form {LongitudeForm(ggF.Value)}; :Sg{westForm}# {(okA ? "ok" : "NO")}, :Sg{signedForm}# {(okB ? "ok" : "NO")}; :SG{sgUsed}#; LST after {Sec(errF)} s ({ExplainLstError(errF)})");
        }

        private static string LongitudeForm(string gg) {
            var t = gg.Trim();
            if (t.StartsWith('-') || t.StartsWith('+')) {
                return "signed, East negative (P07)";
            }
            return int.TryParse(new string(t.TakeWhile(char.IsDigit).ToArray()), NumberStyles.None, Inv, out var d) && d >= 180
                ? "0-360 westward"
                : "unsigned (ambiguous)";
        }

        /// <summary>Mount :GS# minus the LST computed at the moment the query was sent, in seconds of time.</summary>
        private static double LstError(Lx200Reply gs, double eastLongitude) {
            var mount = Lx200Format.ParseRaHours(gs.Value);
            var at = gs.SentUtc + TimeSpan.FromMilliseconds(double.IsNaN(gs.FirstByteMs) ? 0 : gs.FirstByteMs / 2);
            return Lx200Astro.HourDifference(mount, Lx200Astro.LstHours(at, eastLongitude)) * 3600.0;
        }

        /// <summary>
        /// <see cref="LstError"/> for <paramref name="eastLongitude"/> minus the mount clock's own LST error
        /// (<paramref name="clockLstError"/>: the :GS# error before the site was written, at the mount's stored
        /// longitude). Writing the site moves :GS# by the longitude change only, so this is near 0 when the mount took
        /// the longitude, however wrong its date or time.
        /// </summary>
        private static double SiteLstError(Lx200Reply gs, double eastLongitude, double clockLstError) =>
            Lx200Astro.HourDifference(LstError(gs, eastLongitude) / 3600.0, clockLstError / 3600.0) * 3600.0;

        // =============================================================================================
        // 4. :SC date convention (opt-in)
        // =============================================================================================

        private void StepDate(StepRecord s) {
            if (!opt.DateTest) {
                s.Outcome = StepOutcome.Skipped;
                s.Summary = "not run: opt-in with --date-test (bench only, destroys the alignment)";
                s.Conclusions.Add("Not run. Until it is, the driver must never write :SC between 00:00 and 08:00 HKT (when the UTC and local dates differ).");
                return;
            }
            if (opt.UtcOffsetHours == 0) {
                s.Outcome = StepOutcome.Skipped;
                s.Summary = "not run: at UTC+0 the UTC and local dates are always the same, so the test cannot tell them apart";
                return;
            }
            // A local time at which the UTC date is another day: just after local midnight east of Greenwich (01:00 HKT
            // = 17:00 UTC the day before), just before it west of Greenwich (23:00 at UTC-5 = 04:00 UTC the day after)
            var offset = TimeSpan.FromHours(opt.UtcOffsetHours);
            var fromMidnight = TimeSpan.FromHours(Math.Min(1.0, Math.Abs(opt.UtcOffsetHours) / 2));
            var simTimeOfDay = opt.UtcOffsetHours > 0 ? fromMidnight : TimeSpan.FromDays(1) - fromMidnight;
            var warning = "BENCH ONLY - THIS DESTROYS THE ALIGNMENT (which is why this step runs after steps 5-8).\n"
                + string.Create(Inv, $"  The probe sets the mount clock to a simulated {simTimeOfDay:hh\\:mm} local time (UTC{opt.UtcOffsetHours:+0.##;-0.##}), when the UTC date is another day,\n")
                + "  writes :SC once with the UTC date and once with the local date, compares :GS# with the computed LST each time,\n"
                + "  then restores the real date and time from this Mac's clock. If the probe is stopped meanwhile (Ctrl+C, closing\n"
                + "  the window, kill) it still restores them before it exits: do not stop it a second time.\n"
                + "  Re-align on the handbox afterwards before trusting any goto.";
            if (!ask.ConfirmStrong(warning, "DESTROY")) {
                s.Outcome = StepOutcome.Declined;
                s.Summary = "declined by operator";
                s.Observations.Add("Operator did not type DESTROY: date test skipped.");
                return;
            }
            s.Observations.Add("Operator typed DESTROY to run the date test.");
            var lon = double.IsNaN(mountLongitudeEast) ? Read(":Gg#", Lx200Format.ParseLongitudeEast) : mountLongitudeEast;
            TxOk($":SG{Lx200Format.FormatHoursToUtc(opt.UtcOffsetHours, false)}#");

            var nowUtc = link.Trace.UtcNow;
            var simLocal = (nowUtc + offset).Date + simTimeOfDay;
            var simUtc = simLocal - offset;
            s.Notes.Add(string.Create(Inv, $"Simulated time: {simLocal:yyyy-MM-dd HH:mm} local = {simUtc:yyyy-MM-dd HH:mm} UTC; UTC date {Lx200Format.FormatDate(simUtc)}, local date {Lx200Format.FormatDate(simLocal)}."));
            Document.Changes.Add("Date/time: rewritten for the :SC test, then restored from the Mac clock. Re-align before relying on gotos.");

            // If the process dies before the restore, this is what results.md says (the finally below replaces it)
            s.Summary = "IN PROGRESS: the mount clock is on a test date. If this is still here, the probe was killed before it put the real date back: set the date and time on the handbox";
            Save();
            restorePending = true;
            var errors = new Dictionary<string, double>();
            var verdict = "inconclusive";
            try {
                foreach (var (label, date) in new[] { ("UTC date", simUtc.Date), ("local date", simLocal.Date) }) {
                    var sl = TxPatient($":SL{Lx200Format.FormatTime(simLocal.TimeOfDay)}#");
                    var slAt = sl.SentUtc + TimeSpan.FromMilliseconds(double.IsNaN(sl.FirstByteMs) ? 0 : sl.FirstByteMs);
                    var sc = TxPatient($":SC{Lx200Format.FormatDate(date)}#");
                    var gc = TxPatient(":GC#");
                    var gl = TxPatient(":GL#");
                    var gs = TxPatient(":GS#");
                    var readAt = gs.SentUtc + TimeSpan.FromMilliseconds(double.IsNaN(gs.FirstByteMs) ? 0 : gs.FirstByteMs / 2);
                    var expected = Lx200Astro.LstHours(simUtc + (readAt - slAt), lon);
                    var err = Lx200Astro.HourDifference(Lx200Format.ParseRaHours(gs.Value), expected) * 3600;
                    errors[label] = err;
                    s.Conclusions.Add(string.Create(Inv,
                        $"Trial with the {label} ({Lx200Format.FormatDate(date)}): :SL -> '{sl.Value}', :SC -> {sc.Parts.Count} part(s) '{Lx200Format.Printable(string.Join("|", sc.Parts))}' ({sc.Status}), :GC# {gc.Value}, :GL# {gl.Value}, :GS# {gs.Value} vs expected {Hms(expected)}: {Sec(err)} s ({ExplainLstError(err)})."));
                }
                var utcOk = Math.Abs(errors["UTC date"]) <= 30;
                var localOk = Math.Abs(errors["local date"]) <= 30;
                if (utcOk && !localOk) {
                    verdict = "UTC";
                    s.Conclusions.Add("VERDICT: :SC takes the UTC date (as P07's LX200GPS note and INDI's lx200gps say). The driver sends the UTC date; Meade.net/INDIGO's local date would be a day off after local midnight.");
                } else if (localOk && !utcOk) {
                    verdict = "local";
                    s.Conclusions.Add("VERDICT: :SC takes the LOCAL date (as Meade.net and INDIGO send). P07's 'UTC' note is wrong for this firmware.");
                } else {
                    s.Conclusions.Add("VERDICT: inconclusive (both or neither trial matched). Check the trace; keep the rule 'never write :SC between 00:00 and 08:00 HKT'.");
                }
            } finally {
                // Always put the real date and time back, even after an error, Ctrl+C or a signal (writing correct values is safe)
                RestoreDateTime(s, verdict, lon, offset, out var restoreError);
                restorePending = false;
                s.Summary = string.Create(Inv, $"{verdict.ToUpperInvariant()}: UTC-date trial {(errors.TryGetValue("UTC date", out var u) ? Sec(u) : "-")} s, local-date trial {(errors.TryGetValue("local date", out var l) ? Sec(l) : "-")} s; restore {(restoreError == null ? "ok" : "FAILED: " + restoreError)}");
                Save();
            }
        }

        /// <summary>Writes the Mac's date/time back to the mount after the :SC test and checks :GS#. Ignores cancellation.</summary>
        private void RestoreDateTime(StepRecord s, string verdict, double lon, TimeSpan offset, out string error) {
            error = null;
            try {
                var real = link.Trace.UtcNow;
                var realLocal = real + offset;
                DateTime dateToWrite;
                if (verdict == "UTC") {
                    dateToWrite = real.Date;
                } else if (verdict == "local" || realLocal.Date == real.Date) {
                    dateToWrite = realLocal.Date;
                } else {
                    dateToWrite = real.Date;
                    s.Conclusions.Add("WARNING: restored with the UTC date while the UTC and local dates differ and the convention is unknown: check the date on the handbox.");
                }
                Lx200Reply Patient(string command) {
                    for (var i = 0; ; i++) {
                        var r = link.Send(command);
                        if (r.IsOk || r.Status != ReplyStatus.Nak || i >= 30) {
                            return r;
                        }
                        Thread.Sleep(500);
                    }
                }
                var sl = Patient($":SL{Lx200Format.FormatTime((link.Trace.UtcNow + offset).TimeOfDay)}#");
                var sc = Patient($":SC{Lx200Format.FormatDate(dateToWrite)}#");
                var gs = Patient(":GS#");
                if (!sl.IsOk || !sc.IsOk || !gs.IsOk) {
                    error = $":SL {sl.Status}, :SC {sc.Status}, :GS# {gs.Status}";
                    s.Conclusions.Add($"RESTORE FAILED ({error}): set the date and time on the handbox before using the mount.");
                    return;
                }
                var errR = LstError(gs, lon);
                s.Conclusions.Add(string.Create(Inv, $"Restored :SL from the Mac clock and :SC{Lx200Format.FormatDate(dateToWrite)}#: :GS# {gs.Value}, {Sec(errR)} s vs computed ({ExplainLstError(errR)})."));
                if (Math.Abs(errR) > 15) {
                    error = $"LST {Sec(errR)} s after restore";
                    s.Conclusions.Add($"RESTORE CHECK FAILED ({error}): set the date and time on the handbox before using the mount.");
                }
            } catch (Exception ex) {
                error = ex.Message;
                s.Conclusions.Add($"RESTORE FAILED ({ex.Message}): set the date and time on the handbox before using the mount.");
            }
        }

        /// <summary>Like <see cref="TxOk"/> but keeps retrying for up to 15 s while the mount answers NAK (busy after :SC).</summary>
        private Lx200Reply TxPatient(string command) {
            var sw = Stopwatch.StartNew();
            while (true) {
                var r = Tx(command);
                if (r.IsOk) {
                    return r;
                }
                if (r.Status != ReplyStatus.Nak || sw.Elapsed > TimeSpan.FromSeconds(15)) {
                    throw new Lx200ReplyException($"{command}: {r.Status}", r);
                }
                Wait(0.5);
            }
        }

        // =============================================================================================
        // 5. Goto and sync
        // =============================================================================================

        private void StepGotoSync(StepRecord s) {
            var idle = TxOk(":D#");
            s.Conclusions.Add($":D# while idle: [{(idle.Raw.Length > 0 ? idle.RawHex : "")}] -> {(idle.Value.Trim().Length == 0 ? "null string, as P07 says" : "NOT empty: a slew may still be running")}.");
            var p = EnsureHighPrecision(s);
            var start = ReadPos();
            var lst = Read(":GS#", Lx200Format.ParseRaHours);
            var lat = double.IsNaN(mountLatitude) ? Read(":Gt#", Lx200Format.ParseDegrees) : mountLatitude;
            var tAlt = Math.Clamp(start.Alt, 20, 70);
            var tAz = Lx200Astro.Wrap(start.Az + opt.GotoOffsetAzDeg, 360);
            var (tRa, tDec) = Lx200Astro.ToRaDec(tAlt, tAz, lst, lat);
            var distance = Lx200Astro.SeparationDeg(start.Ra, start.Dec, tRa, tDec);
            if (!ConfirmStep(s, string.Create(Inv, $"Goto test: the mount will SLEW about {distance:0}° to RA {Hms(tRa)} Dec {Dms(tDec)} (alt {tAlt:0.0}°, az {tAz:0.0}°),\n  sync there with :CM#, then (if you agree) slew back. Stand clear of the scope and cables; Ctrl+C stops it (:Q#)."))) {
                return;
            }
            var arrived = Goto(s, tRa, tDec, p, "Goto");
            if (arrived != true) {
                s.Summary = arrived == null ? "goto never finished (stopped with :Q#)" : "goto refused";
                return;
            }
            var after = ReadPos();
            var err = Lx200Astro.SeparationDeg(after.Ra, after.Dec, tRa, tDec) * 3600;
            s.Conclusions.Add(string.Create(Inv, $"Goto ended {err:0}\" from the target by the mount's own readback (resolution ~1\" Dec, 15\"·cos δ RA in long format)."));
            if (err > opt.SyncGuardArcsec) {
                // The mount is not where the goto should have left it: still slewing with no :D# bar, or it went
                // elsewhere. A sync here would write a wrong point into the pointing model (RVM MNT-11 guards syncs).
                var still = Tx(":D#");
                Halt(":Q#");
                s.Conclusions.Add(string.Create(Inv, $"The mount's own position is {err / 3600:0.00}° from the goto target (limit {opt.SyncGuardArcsec / 60:0}'), and :D# now answers [{still.RawHex}]: the goto had not really ended (a :D# bar that never showed?) or went elsewhere. Sent :Q#. :CM# NOT sent: a sync here would put a wrong point into the pointing model. Check the trace before relying on :D#."));
                s.Outcome = StepOutcome.Failed;
                s.Summary = string.Create(Inv, $"goto ended {err:0}\" from the target (limit {opt.SyncGuardArcsec:0}\"): stopped with :Q#, :CM# NOT sent");
                return;
            }

            TxOk($":Sr{Lx200Format.FormatRa(tRa, p)}#");
            TxOk($":Sd{Lx200Format.FormatDec(tDec, p)}#");
            var cm = Tx(":CM#");
            var cmFixed = cm.IsOk && cm.Value == FixedSyncReply;
            s.Conclusions.Add(cmFixed
                ? $":CM# returned the fixed Autostar II string \"{FixedSyncReply}#\" ({cm.Raw.Length} bytes, P07 l.107): read it up to '#' and discard it."
                : $":CM# returned '{Lx200Format.Printable(cm.RawText)}' ({cm.Status}): NOT the documented fixed string; check the trace before relying on sync.");
            var synced = ReadPos();
            var errSync = Lx200Astro.SeparationDeg(synced.Ra, synced.Dec, tRa, tDec) * 3600;
            s.Conclusions.Add(string.Create(Inv, $"After :CM# the mount reports {errSync:0}\" from the sync target."));
            Document.Changes.Add("Sync: one :CM# at the goto target (the pointing model changed near it).");

            var back = "not done";
            if (ConfirmPart(s, string.Create(Inv, $"Slew back to the start (RA {Hms(start.Ra)} Dec {Dms(start.Dec)})?"))) {
                var ok = Goto(s, start.Ra, start.Dec, p, "Return");
                back = ok == true ? "returned" : "return failed";
            }
            s.Summary = string.Create(Inv, $"goto {distance:0}° ok, {err:0}\" off; :CM# {(cmFixed ? "fixed string" : "UNEXPECTED")}; {back}");
        }

        /// <summary>
        /// :Sr/:Sd/:MS# then :D# polling. True = arrived, false = refused, null = timed out and stopped. A null :D# in
        /// the first <see cref="ChecklistOptions.MinimumSlewSeconds"/> after :MS# is not trusted (RVM MNT-04).
        /// </summary>
        private bool? Goto(StepRecord s, double ra, double dec, CoordinatePrecision p, string label) {
            var sr = TxOk($":Sr{Lx200Format.FormatRa(ra, p)}#");
            var sd = TxOk($":Sd{Lx200Format.FormatDec(dec, p)}#");
            if (sr.Value != "1" || sd.Value != "1") {
                s.Conclusions.Add($"{label}: target rejected (:Sr -> '{sr.Value}', :Sd -> '{sd.Value}'); the format may not match the precision mode ({p}).");
                return false;
            }
            var ms = TxOk(":MS#");
            if (ms.Value != "0") {
                s.Conclusions.Add($"{label}: :MS# refused with '{Lx200Format.Printable(ms.Joined)}' (1 = below horizon, 2 = above the high limit).");
                return false;
            }
            var sw = Stopwatch.StartNew();   // from the '0': the slew is under way
            var polls = 0;
            var earlyNulls = 0;
            var bars = new SortedSet<string>(StringComparer.Ordinal);
            Lx200Reply firstBar = null, last = null;
            s.Recording = false;
            try {
                while (true) {
                    Wait(opt.DistancePollSeconds);
                    var d = TxOk(":D#");
                    polls++;
                    last = d;
                    if (d.Value.Trim().Length == 0) {
                        if (sw.Elapsed.TotalSeconds >= opt.MinimumSlewSeconds) {
                            break;
                        }
                        earlyNulls++;
                    } else {
                        firstBar ??= d;
                        bars.Add(Lx200Trace.Hex(d.Raw.AsSpan(0, d.Raw.Length - 1)));
                    }
                    if (sw.Elapsed.TotalSeconds > opt.GotoTimeoutSeconds) {
                        s.Recording = true;
                        Halt(":Q#");
                        s.Conclusions.Add(string.Create(Inv, $"{label}: still slewing after {opt.GotoTimeoutSeconds:0} s, sent :Q#. If the handbox shows 'ENTER to Sync', High Precision pointing is ON (step 2)."));
                        return null;
                    }
                }
            } finally {
                s.Recording = true;
            }
            if (firstBar != null) {
                s.Exchanges.Add(firstBar);
            }
            s.Exchanges.Add(last);
            var early = earlyNulls == 0 ? "" : string.Create(Inv, $"; {earlyNulls} null :D# repl{(earlyNulls == 1 ? "y" : "ies")} in the first {opt.MinimumSlewSeconds:0.0} s after :MS# not trusted (the bar can lag the start of a goto)");
            s.Conclusions.Add(string.Create(Inv, $"{label}: :MS# -> '0'; :D# polled {polls} times over {sw.Elapsed.TotalSeconds:0.0} s; bar byte(s) while slewing: {(bars.Count > 0 ? string.Join(", ", bars) : "none seen")}{early}; done when :D# returned [{last.RawHex}]."));
            return true;
        }

        // =============================================================================================
        // 6. Pulse guiding
        // =============================================================================================

        private void StepPulseGuide(StepRecord s) {
            var rate = opt.GuideRateArcsecPerSec;
            var assumedRate = rate > 0 ? rate : 7.52;   // NINA's fallback when the rate is unknown is 0.5x sidereal (RVM MNT-06)
            var ms = opt.PulseMs;
            var expected = assumedRate * ms / 1000.0;
            if (!ConfirmStep(s, string.Create(Inv, $"Pulse-guide test: the mount will MOVE in small steps: {ms} ms pulses (~{expected:0}\" each at {assumedRate:0.0}\"/s) n/s/e/w, two axes back to back,\n  then the same with :RG# + :Mx#/:Qx#.{(rate > 0 ? $" First :Rg{rate:00.0}# sets the guide rate." : "")} Stand clear; Ctrl+C stops it."))) {
                return;
            }
            var gw = Tx(":GW#");
            if (gw.IsOk) {
                s.Notes.Add($":GW# before: '{Lx200Format.Printable(gw.Value)}' (second char T = tracking).");
            }
            if (rate > 0) {
                Tx(Lx200Format.GuideRateCommand(rate));
                Document.Changes.Add(string.Create(Inv, $"Guide rate: :Rg{rate:00.0}#."));
            }
            var p = EnsureHighPrecision(s);
            var threshold = p == CoordinatePrecision.High ? Math.Max(4, 0.3 * expected) : Math.Max(90, 0.3 * expected);
            if (p == CoordinatePrecision.Low) {
                s.Notes.Add("Low precision format: positions resolve 1' only, so small pulses cannot be measured.");
            }
            var lst = Read(":GS#", Lx200Format.ParseRaHours);
            var lat = double.IsNaN(mountLatitude) ? Read(":Gt#", Lx200Format.ParseDegrees) : mountLatitude;

            var b0 = ReadPos();
            Wait((ms / 1000.0) + opt.SettleSeconds);
            var b1 = ReadPos();
            var drift = Rates.Between(b0, b1);
            var q = Lx200Astro.ParallacticAngleDeg(b1.Ra, b1.Dec, lst, lat);
            s.Notes.Add(string.Create(Inv, $"Baseline without pulses over {(b1.Utc - b0.Utc).TotalSeconds:0.0} s: alt {drift.Alt:+0.00;-0.00}\"/s, az {drift.Az:+0.00;-0.00}\"/s (axis), Dec {drift.Dec:+0.00;-0.00}\"/s, RA {drift.Ra:+0.00;-0.00}\"/s (axis). Deltas below have this drift removed. Parallactic angle {q:0}°{(Math.Abs(q) < 15 || Math.Abs(Math.Abs(q) - 180) < 15 ? " (alt-az and equatorial directions nearly coincide here, so axis attribution is ambiguous: point the scope 30° or more east or west of south and rerun with --skip 1,2,3,4,5,7,8)" : "")}."));

            var mg = new Dictionary<char, Delta>();
            foreach (var d in "nsew") {
                var a = ReadPos();
                Tx(Lx200Format.PulseGuideCommand(d, ms));
                Wait((ms / 1000.0) + opt.SettleSeconds);
                var b = ReadPos();
                mg[d] = Delta.Measure(a, b, drift);
                s.Conclusions.Add($":Mg{d}{ms:0000}#: {mg[d].Describe(expected, threshold)}");
            }

            // Two axes back to back, as NINA's DirectGuider sends them (DirectGuider.cs:245-248, RIM MNT-05). A readout
            // centred halfway through the first pulse tells "concurrent" (the second axis is already moving) from
            // "queued" (it waits for the first pulse to end); a readout after both pulses tells "dropped"/"cancelled".
            var readClock = Stopwatch.StartNew();
            var a2 = ReadPos();
            var readSeconds = readClock.Elapsed.TotalSeconds;
            var firstPulse = Tx(Lx200Format.PulseGuideCommand('n', ms));
            Tx(Lx200Format.PulseGuideCommand('e', ms));
            double SinceFirstPulse() => (link.Trace.UtcNow - firstPulse.SentUtc).TotalSeconds;
            Wait((0.5 * ms / 1000.0) - (readSeconds / 2) - SinceFirstPulse());
            var mid = ReadPos();
            var midDone = SinceFirstPulse();
            Wait((2.0 * ms / 1000.0) + opt.SettleSeconds - SinceFirstPulse());
            var end = ReadPos();
            // a queued second pulse starts when the first ends: a halfway readout that ends later than that proves nothing
            var midTooLate = midDone > 0.9 * ms / 1000.0 ? midDone : double.NaN;
            var twoAxis = ClassifyTwoAxis(mg['n'], mg['e'], Delta.Measure(a2, mid, drift), Delta.Measure(a2, end, drift), expected, threshold, midTooLate, ms);
            s.Conclusions.Add($"Two axes back to back (:Mgn then :Mge at once): {twoAxis}");

            var mgMoved = "nsew".Count(d => mg[d].Moved(threshold));
            var mapping = Mapping(mg, threshold);
            s.Conclusions.Add($":Mg moved the mount in {mgMoved}/4 directions; axes: {mapping}.");

            var rgMoved = -1;
            if (ConfirmPart(s, string.Create(Inv, $"Also test host-timed moves (:RG# then :Mn#/:Ms#/:Me#/:Mw#, {ms} ms, then :Qn#/:Qs#/:Qe#/:Qw#)?"))) {
                Tx(":RG#");
                var rg = new Dictionary<char, Delta>();
                foreach (var d in "nsew") {
                    var a = ReadPos();
                    var start = Tx($":M{d}#");
                    var elapsed = (link.Trace.UtcNow - start.SentUtc).TotalSeconds;
                    Wait((ms / 1000.0) - elapsed);
                    var stop = Halt($":Q{d}#");
                    Wait(opt.SettleSeconds);
                    var b = ReadPos();
                    rg[d] = Delta.Measure(a, b, drift);
                    s.Conclusions.Add(string.Create(Inv, $":RG# :M{d}# ... :Q{d}# (host-timed {(stop.SentUtc - start.SentUtc).TotalMilliseconds:0} ms): {rg[d].Describe(expected, threshold)}"));
                }
                rgMoved = "nsew".Count(d => rg[d].Moved(threshold));
                s.Conclusions.Add($"Host-timed :RG# moves moved the mount in {rgMoved}/4 directions; axes: {Mapping(rg, threshold)}.");
            }

            string strategy;
            if (mgMoved == 4) {
                strategy = "(a) native :Mg";
                s.Conclusions.Add($"DITHER STRATEGY: (a) native :Mg pulse guiding works in alt-az on this mount, so NINA's DirectGuider can dither through PulseGuide. {(twoAxis.StartsWith("concurrent", StringComparison.Ordinal) ? "" : "Send the second axis only after the first pulse ends (queue it in the driver).")}");
            } else if (rgMoved == 4) {
                strategy = "(b) :RG#+:Mx#/:Qx#";
                s.Conclusions.Add("DITHER STRATEGY: (b) :Mg does not move every axis, but host-timed :RG# + :Mx#/:Qx# does: use it for PulseGuide.");
            } else {
                strategy = "(c) goto offset";
                s.Conclusions.Add("DITHER STRATEGY: (c) neither :Mg nor :RG# moves moved every axis: dither by goto offset (coarse: 1 s of RA = ~37 px at f/6.3; RVM MNT-M4).");
            }
            s.Summary = $":Mg {mgMoved}/4 ({mapping}); two-axis {twoAxis.Split(':')[0]}; :RG# {(rgMoved < 0 ? "not tested" : rgMoved + "/4")} -> {strategy}";
        }

        private static string Mapping(Dictionary<char, Delta> d, double threshold) {
            string Axis(char c) => d[c].Moved(threshold) ? d[c].DominantAxis : "none";
            return $"n {Axis('n')}, s {Axis('s')}, e {Axis('e')}, w {Axis('w')}";
        }

        /// <summary>
        /// Classifies :Mgn + :Mge sent back to back from a readout halfway through the first pulse and one after both.
        /// <paramref name="midTooLateSeconds"/> is NaN unless the halfway readout ended too late to see a queue.
        /// </summary>
        internal static string ClassifyTwoAxis(Delta nSingle, Delta eSingle, Delta atMid, Delta atEnd, double expected, double threshold, double midTooLateSeconds, int ms) {
            if (!nSingle.Moved(threshold) || !eSingle.Moved(threshold)) {
                return "not classifiable: a single :Mgn or :Mge did not move the mount.";
            }
            var full = Math.Max(threshold, 0.6 * expected);       // "this axis did its pulse"
            var moving = Math.Max(0.5 * threshold, 0.2 * expected);  // "this axis had started" (about half is expected)
            var (nMid, eMid, separable) = Split(atMid, nSingle, eSingle);
            var (nEnd, eEnd, _) = Split(atEnd, nSingle, eSingle);
            var report = string.Create(Inv, $"halfway through the first pulse n {nMid:0.0}\" e {eMid:0.0}\"; after both n {nEnd:0.0}\" e {eEnd:0.0}\" (expected ~{expected:0}\" each)")
                + (separable ? "" : "; the n and e single-pulse moves point almost the same way, so the split is approximate");
            if (nEnd >= full && eEnd >= full) {
                if (!double.IsNaN(midTooLateSeconds)) {
                    return string.Create(Inv, $"both axes moved, but concurrent and queued cannot be told apart: the halfway readout ended {midTooLateSeconds * 1000:0} ms after the first {ms} ms pulse was sent (rerun with a longer --pulse-ms); ") + report;
                }
                return eMid >= moving
                    ? "concurrent: the second axis was already moving halfway through the first pulse; " + report
                    : "queued: the second axis only moved after the first pulse ended; " + report;
            }
            if (nEnd >= full) {
                return "second pulse DROPPED: only the first axis moved; " + report;
            }
            if (eEnd >= full) {
                return "first pulse cancelled by the second: only the second axis moved; " + report;
            }
            return "inconclusive; " + report;
        }

        /// <summary>
        /// Writes a movement as N times the single :Mgn move plus E times the single :Mge move (2x2 solve on the alt/az
        /// axis arcseconds) and returns both in arcsec along those moves. Unlike a projection, a slant between the two
        /// directions (e.g. RA/Dec moves seen on alt/az axes) does not leak one axis into the other.
        /// </summary>
        internal static (double N, double E, bool Separable) Split(Delta d, Delta n, Delta e) {
            var nLen = Math.Sqrt((n.Alt * n.Alt) + (n.AzAxis * n.AzAxis));
            var eLen = Math.Sqrt((e.Alt * e.Alt) + (e.AzAxis * e.AzAxis));
            var det = (n.Alt * e.AzAxis) - (e.Alt * n.AzAxis);
            if (nLen < 1e-9 || eLen < 1e-9 || Math.Abs(det) < 0.2 * nLen * eLen) {   // under ~12° apart
                return (d.Along(n), d.Along(e), false);
            }
            var a = ((d.Alt * e.AzAxis) - (e.Alt * d.AzAxis)) / det;
            var b = ((n.Alt * d.AzAxis) - (d.Alt * n.AzAxis)) / det;
            return (a * nLen, b * eLen, true);
        }

        internal readonly record struct Rates(double Alt, double Az, double Dec, double Ra) {

            public static Rates Between(Pos a, Pos b) {
                var dt = Math.Max(0.001, (b.Utc - a.Utc).TotalSeconds);
                return new Rates(
                    (b.Alt - a.Alt) * 3600 / dt,
                    Lx200Astro.DegreeDifference(b.Az, a.Az) * 3600 / dt,
                    (b.Dec - a.Dec) * 3600 / dt,
                    Lx200Astro.HourDifference(b.Ra, a.Ra) * 15 * 3600 / dt);
            }
        }

        /// <summary>Movement between two readouts with the baseline drift removed, arcsec on each axis and on the sky.</summary>
        internal readonly record struct Delta(double Alt, double AzAxis, double AzSky, double Dec, double RaAxis, double RaSky) {

            public static Delta Measure(Pos a, Pos b, Rates drift) {
                var dt = (b.Utc - a.Utc).TotalSeconds;
                var alt = ((b.Alt - a.Alt) * 3600) - (drift.Alt * dt);
                var az = (Lx200Astro.DegreeDifference(b.Az, a.Az) * 3600) - (drift.Az * dt);
                var dec = ((b.Dec - a.Dec) * 3600) - (drift.Dec * dt);
                var ra = (Lx200Astro.HourDifference(b.Ra, a.Ra) * 15 * 3600) - (drift.Ra * dt);
                return new Delta(alt, az, az * Math.Cos(b.Alt * Math.PI / 180), dec, ra, ra * Math.Cos(b.Dec * Math.PI / 180));
            }

            /// <summary>Moved when an alt/az axis or Dec changed beyond the threshold (RA resolves only 15" in long format).</summary>
            public bool Moved(double threshold) => Math.Max(Math.Abs(Alt), Math.Abs(AzAxis)) >= threshold || Math.Abs(Dec) >= threshold;

            public string DominantAxis {
                get {
                    var altaz = Math.Abs(Alt) > 2 * Math.Abs(AzSky) ? "altitude" : Math.Abs(AzSky) > 2 * Math.Abs(Alt) ? "azimuth" : "alt+az";
                    var eq = Math.Abs(Dec) > 2 * Math.Abs(RaSky) ? "Dec" : Math.Abs(RaSky) > 2 * Math.Abs(Dec) ? "RA" : "RA+Dec";
                    return $"{altaz} ({eq})";
                }
            }

            /// <summary>Component of this movement along another movement's direction, in axis arcseconds (alt, az).</summary>
            public double Along(Delta reference) {
                var len = Math.Sqrt((reference.Alt * reference.Alt) + (reference.AzAxis * reference.AzAxis));
                return len < 1e-9 ? 0 : ((Alt * reference.Alt) + (AzAxis * reference.AzAxis)) / len;
            }

            public string Describe(double expected, double threshold) {
                var axis = Math.Sqrt((Alt * Alt) + (AzAxis * AzAxis));
                var sky = Math.Sqrt((Alt * Alt) + (AzSky * AzSky));
                return string.Create(CultureInfo.InvariantCulture,
                    $"alt {Alt:+0.0;-0.0}\", az {AzAxis:+0.0;-0.0}\" axis ({AzSky:+0.0;-0.0}\" sky), Dec {Dec:+0.0;-0.0}\", RA {RaAxis:+0.0;-0.0}\" axis -> {(Moved(threshold) ? $"MOVED {axis:0.0}\" on the axes ({(expected > 0 ? axis / expected * 100 : 0):0}% of the expected {expected:0}\"), {sky:0.0}\" on the sky, {DominantAxis}" : $"no movement above {threshold:0}\"")}");
            }
        }

        // =============================================================================================
        // 7. Focuser
        // =============================================================================================

        private void StepFocuser(StepRecord s) {
            var ft = Tx(":fT#");
            string temp;
            if (ft.IsOk && double.TryParse(ft.Value.Trim(), NumberStyles.Float, Inv, out var celsius)) {
                temp = string.Create(Inv, $"{celsius:0.0} C");
                s.Conclusions.Add(string.Create(Inv, $":fT# answers '{Lx200Format.Printable(ft.Value)}' = {celsius:0.0} °C: usable as FITS FOCTEMP."));
            } else if (ft.Status == ReplyStatus.Silent) {
                temp = "none";
                s.Conclusions.Add(":fT# is not answered: no OTA temperature on this mount; FOCTEMP stays empty.");
            } else {
                temp = $"odd ({ft.Status})";
                s.Conclusions.Add($":fT# reply '{Lx200Format.Printable(ft.RawText)}' ({ft.Status}) is not a number: check the trace.");
            }

            var ms = opt.FocusMs;
            if (!ConfirmStep(s, string.Create(Inv, $"Focuser test: the #1209 will MOVE in and out ({ms} ms per move at speeds 1-4, then :FP pulses, then a backlash check).\n  Start with the drawtube near mid-travel. It has no position readout: watch the drawtube (or a star on the camera) and answer the questions. Ctrl+C stops it (:FQ#)."))) {
                s.Summary = $":fT# {temp}; moves declined";
                return;
            }

            var hostTimed = new List<string>();
            for (var speed = 1; speed <= 4; speed++) {
                Tx($":F{speed}#");
                var before = SimFocus();
                var runIn = HostTimedFocus(inward: true, ms);
                Wait(0.3);
                Observe(s, string.Create(Inv, $"Speed {speed}: IN for {runIn:0} ms (host-timed :F+# ... :FQ#). Did it move, and how far (e.g. 'y 0.1 mm', 'n')?"), () => SimFocusTruth(before));
                var mid = SimFocus();
                var runOut = HostTimedFocus(inward: false, ms);
                Wait(0.3);
                Observe(s, string.Create(Inv, $"Speed {speed}: OUT for {runOut:0} ms. Did it move back about the same?"), () => SimFocusTruth(mid));
                hostTimed.Add(string.Create(Inv, $"speed {speed}: in {runIn:0} ms / out {runOut:0} ms"));
            }
            s.Conclusions.Add($"Host-timed moves (time from :F+#/:F-# to the first :FQ#, measured from the trace): {string.Join("; ", hostTimed)}. Two :FQ# are sent per stop.");

            Tx(":F2#");
            var fpBefore = SimFocus();
            Tx(Lx200Format.FocusPulseCommand(+ms));
            Wait((ms / 1000.0) + 0.5);
            var fpIn = Observe(s, string.Create(Inv, $":FP+{ms:0000}# (mount-timed {ms} ms IN at speed 2). Did it move IN about as far as the speed-2 host-timed move?"), () => SimFocusTruth(fpBefore));
            var fpMid = SimFocus();
            Tx(Lx200Format.FocusPulseCommand(-ms));
            Wait((ms / 1000.0) + 0.5);
            var fpOut = Observe(s, string.Create(Inv, $":FP-{ms:0000}# ({ms} ms OUT). Did it move back?"), () => SimFocusTruth(fpMid));
            string fpVerdict;
            if (sim != null) {
                fpVerdict = Math.Abs(fpMid - fpBefore) > 0.5 ? "works" : "does nothing";
            } else {
                fpVerdict = fpIn.StartsWith("y", StringComparison.OrdinalIgnoreCase) ? "works (operator)" : fpIn.Length == 0 ? "unknown (no answer)" : "does not work (operator)";
            }
            s.Conclusions.Add($":FP (mount-timed pulse, P07 l.194) {fpVerdict}: IN answer '{fpIn}', OUT answer '{fpOut}'. {(fpVerdict.StartsWith("works", StringComparison.Ordinal) ? "A virtual-ms move can be one command, free of macOS timing jitter." : "Use host-timed :F+#/:F-# ... :FQ# moves.")}");

            // backlash: run OUT, then reverse IN in steps of about 200 ms until motion is seen. The bracket adds up the
            // measured run times (from :F+# to :FQ# on the wire), not the requested ones.
            Tx(":F2#");
            HostTimedFocus(inward: false, 2 * ms);
            Wait(0.3);
            string backlash = null;
            var total = 0.0;
            for (var k = 1; k <= 5; k++) {
                var before = SimFocus();
                var run = HostTimedFocus(inward: true, 200);
                var previous = total;
                total += run;
                Wait(0.3);
                var answer = Observe(s, string.Create(Inv, $"Backlash {k}: reversed IN for {run:0} ms (total {total:0} ms since reversing). Visible IN motion now? (y/n)"), () => SimFocusTruth(before));
                var moved = sim != null ? Math.Abs(SimFocus() - before) > 0.5 : answer.StartsWith("y", StringComparison.OrdinalIgnoreCase);
                if (moved) {
                    backlash = string.Create(Inv, $"between {previous:0} and {total:0} ms at speed 2");
                    break;
                }
            }
            backlash ??= string.Create(Inv, $"not seen within {total:0} ms");
            s.Conclusions.Add($"Backlash after a reversal: {backlash}. Use NINA's backlash compensation on the virtual-ms position rather than adding it in the driver.");
            Document.Changes.Add("Focus speed: left at 2 (:F2#); drawtube near where it started (in/out moves were paired).");
            s.Summary = $":fT# {temp}; :FP {fpVerdict}; backlash {backlash}";
        }

        /// <summary>
        /// Starts the focuser, waits until <paramref name="ms"/> after the start byte left, sends :FQ# (which the mount
        /// must accept) and a second :FQ# right after it in case the first is missed. Returns the measured run time.
        /// </summary>
        private double HostTimedFocus(bool inward, int ms) {
            var start = Tx(inward ? ":F+#" : ":F-#");
            var elapsed = (link.Trace.UtcNow - start.SentUtc).TotalSeconds;
            Wait((ms / 1000.0) - elapsed);
            var stop = Halt(":FQ#");
            link.Send(":FQ#");
            return (stop.SentUtc - start.SentUtc).TotalMilliseconds;
        }

        private double SimFocus() => sim?.FocuserPositionUm ?? double.NaN;

        private string SimFocusTruth(double before) {
            var now = sim.FocuserPositionUm;
            return string.Create(Inv, $"moved {now - before:+0.0;-0.0;0.0} um (negative = inward), drawtube at {now:0} um, speed {sim.FocusSpeed}");
        }

        // =============================================================================================
        // 8. Tracking :AL#/:AA#
        // =============================================================================================

        private void StepTracking(StepRecord s) {
            var wait = opt.TrackWaitSeconds;
            if (!ConfirmStep(s, string.Create(Inv, $"Tracking test: :AL# switches to Land mode (tracking OFF) for {wait:0} s, then :AA# returns to alt-az tracking.\n  The drives stop and restart; no slew."))) {
                return;
            }
            EnsureHighPrecision(s);   // the tracking verdict needs 1 s RA and 1" Dec
            var a = Snapshot();
            Tx(":AL#");
            Wait(wait);
            var b = Snapshot();
            Tx(":AA#");
            Wait(wait);
            var c = Snapshot();
            Wait(wait);
            var d = ReadPos();
            Document.Changes.Add("Tracking: :AL# then :AA# (left in alt-az tracking).");

            s.Conclusions.Add($"Mode: ACK '{a.Ack}' -> '{b.Ack}' after :AL# -> '{c.Ack}' after :AA#; :GW# '{a.Gw}' -> '{b.Gw}' -> '{c.Gw}'.");
            var landSeconds = (b.Pos.Utc - a.Pos.Utc).TotalSeconds;
            var raLand = Lx200Astro.HourDifference(b.Pos.Ra, a.Pos.Ra) * 3600;
            var altazLand = Math.Max(Math.Abs(b.Pos.Alt - a.Pos.Alt), Math.Abs(Lx200Astro.DegreeDifference(b.Pos.Az, a.Pos.Az))) * 3600;
            s.Conclusions.Add(string.Create(Inv, $"In Land mode for {landSeconds:0.0} s: RA advanced {raLand:+0;-0} s of time (sidereal would be {landSeconds * 1.0027:0}), alt/az changed {altazLand:0}\": {(Math.Abs(raLand - (landSeconds * 1.0027)) < Math.Max(3, landSeconds * 0.3) && altazLand < 30 ? "tracking really stopped" : "does not look like stopped tracking")}."));
            var raTrack = Lx200Astro.HourDifference(d.Ra, c.Pos.Ra) * 3600;
            var decTrack = (d.Dec - c.Pos.Dec) * 3600;
            var tracking = Math.Abs(raTrack) <= 2 && Math.Abs(decTrack) <= 10;
            s.Conclusions.Add(string.Create(Inv, $"After :AA#, over {(d.Utc - c.Pos.Utc).TotalSeconds:0.0} s: RA {raTrack:+0;-0} s, Dec {decTrack:+0;-0}\": {(tracking ? "tracking resumed" : "NOT tracking")}."));
            var jump = Lx200Astro.SeparationDeg(b.Pos.Ra, b.Pos.Dec, c.Pos.Ra, c.Pos.Dec);   // tracking resumes at :AA#, so RA/Dec should continue from b
            string alignment;
            if (a.Align is not char alignBefore || c.Align is not char alignAfter) {
                // without a 3-character :GW# reply the alignment state cannot be read, and the RA/Dec jump alone does not show it was kept
                alignment = "unreadable (no :GW#)";
                s.Conclusions.Add(string.Create(Inv, $"Alignment state unreadable: :GW# gave '{a.Gw}' -> '{c.Gw}', not a 3-character mode/tracking/alignment reply. RA/Dec jump at :AA# {jump:0.000}° (limit 0.1°), which alone cannot show the alignment was kept: check the handbox (alignment status) before using :AL# for 'tracking off'."));
            } else {
                var kept = alignBefore == alignAfter && alignBefore != '0' && jump < 0.1;
                alignment = kept ? "kept" : "LOST/unclear";
                s.Conclusions.Add(string.Create(Inv, $"Alignment: :GW# third char '{alignBefore}' -> '{alignAfter}', RA/Dec jump at :AA# {jump:0.000}° (limit 0.1°): {(kept ? "KEPT. The driver may use :AL#/:AA# for tracking off/on." : "LOST or unclear: do not use :AL# for 'tracking off'; check the handbox.")}"));
            }
            s.Summary = $"{a.Ack}->{b.Ack}->{c.Ack}; :GW# {a.Gw}->{b.Gw}->{c.Gw}; alignment {alignment}; tracking {(tracking ? "resumed" : "NOT resumed")}";
        }

        /// <summary>ACK, :GW# and the position. Align is :GW#'s third character, or null when :GW# gave no 3-character reply.</summary>
        private (string Ack, string Gw, char? Align, Pos Pos) Snapshot() {
            var ack = link.Ack();
            var gw = Tx(":GW#");
            char? align = gw.IsOk && gw.Value.Length == 3 ? gw.Value[2] : null;
            return (ack.IsOk ? ack.Value : "?", gw.IsOk ? Lx200Format.Printable(gw.Value) : $"({gw.Status})", align, ReadPos());
        }

        private static string ModeName(char c) => c switch {
            'A' => "alt-az",
            'L' => "land",
            'P' => "polar",
            'D' => "updater",
            'G' => "German equatorial",
            _ => "unknown"
        };
    }
}
