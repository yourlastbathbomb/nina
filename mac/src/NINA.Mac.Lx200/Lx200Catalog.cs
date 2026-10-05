#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Mac.Lx200 {

    /// <summary>
    /// The LX200GPS / Autostar II commands this rig needs, each with its reply shape.
    /// <para>
    /// Sources (cited per entry):
    /// P07 = Meade Telescope Serial Command Protocol rev 2007.10, text copy at research/reference/proto2007.txt
    /// ("P07 l.362" = line 362 of that file). RVM = research/rig_verify_mount.md (corrected Table 1, which wins
    /// over RIM = research/rig_investigate_mount.md). MN = Meade.net (MIT, bitbucket cjdskunkworks/meadeautostar497),
    /// IGO = INDIGO indigo_mount_lx200.c, INDI = indilib lx200driver.cpp, as quoted by RIM/RVM.
    /// </para>
    /// <para>
    /// Where the spec and the drivers disagree the shape is tolerant (an optional trailing '#') so a wrong guess
    /// cannot leave a byte behind; the bench checklist records the real bytes.
    /// </para>
    /// </summary>
    public static class Lx200Catalog {

        private static readonly TimeSpan SlowReply = TimeSpan.FromSeconds(3);

        /// <summary>":SC" answers '1' at once, then the second string after the planetary update (several seconds).</summary>
        private static readonly TimeSpan PlanetaryUpdate = TimeSpan.FromSeconds(30);

        public static IReadOnlyList<Lx200CommandSpec> All { get; } = Build();

        public static Lx200CommandSpec Ack => All[0];

        public static IEnumerable<Lx200CommandSpec> BlockedCommands => All.Where(c => c.IsBlocked);

        /// <summary>Finds the entry for a command code, e.g. "GR", "Sr", "Mgn" or <see cref="Lx200CommandSpec.AckCode"/>.</summary>
        public static Lx200CommandSpec ByCode(string code) => All.FirstOrDefault(c => c.Code == code)
            ?? throw new ArgumentException($"'{code}' is not in the LX200 catalog", nameof(code));

        /// <summary>
        /// Matches the text between ':' and '#'. Blocked codes match as a prefix whatever follows them; other
        /// codes match exactly (no arguments) or as a prefix followed by arguments. Longest code wins.
        /// Returns null for commands that are not in the catalog.
        /// </summary>
        public static Lx200CommandSpec Match(string body) {
            if (body == null) {
                return null;
            }
            // A blocked prefix wins whatever follows it (":hP#", ":hPx#", ":SB6#")
            var blocked = All.Where(s => s.IsBlocked && body.StartsWith(s.Code, StringComparison.Ordinal))
                             .OrderByDescending(s => s.Code.Length).FirstOrDefault();
            if (blocked != null) {
                return blocked;
            }
            return All.Where(s => !s.IsBlocked && s.Code != Lx200CommandSpec.AckCode
                                  && (s.HasArgs ? body.Length > s.Code.Length && body.StartsWith(s.Code, StringComparison.Ordinal) : body == s.Code))
                      .OrderByDescending(s => s.Code.Length).FirstOrDefault();
        }

        private static List<Lx200CommandSpec> Build() {
            var ro = CommandEffect.ReadOnly;
            var cfg = CommandEffect.WritesConfig;
            var move = CommandEffect.MovesHardware;
            var halt = CommandEffect.Halt;
            var list = new List<Lx200CommandSpec> {
                // ---- Link / identity -------------------------------------------------------------------------
                new(Lx200CommandSpec.AckCode, "Alignment query / link test (0x06, not ':'-framed)", ReplyShape.MountMode, ro,
                    "P07 l.51-57: A alt-az, D updater, L land, P polar; 1 char, no '#'. RVM T1 row ACK. Used for resync (RIM MNT-02 rec)."),
                new("GVP", "Product name", ReplyShape.Terminated, ro, "P07 l.399 '<string>#'; RVM T1: expect LX2001#"),
                new("GVN", "Firmware number", ReplyShape.Terminated, ro, "P07 l.396 'dd.d#'; RVM T1: 4.2g stock, 4.2k Meade rollover fix, 4.2G StarPatch"),
                new("GVD", "Firmware date", ReplyShape.Terminated, ro, "P07 l.393 'mmm dd yyyy#'"),
                new("GVT", "Firmware time", ReplyShape.Terminated, ro, "P07 l.402 'HH:MM:SS#'"),
                new("GW", "Mount/tracking/alignment status (undocumented)", ReplyShape.FixedThenOptionalHash(3), ro,
                    "Not in P07. RVM T1: 3 chars e.g. 'AT2'; MN Telescope.cs:432-445 reads 3 chars (no '#'), IGO reads to '#': optional '#' tolerated"),

                // ---- Position ------------------------------------------------------------------------------
                new("GR", "Telescope RA", ReplyShape.Terminated, ro, "P07 l.362 'HH:MM.T#' or 'HH:MM:SS#' (precision)"),
                new("GD", "Telescope Dec", ReplyShape.Terminated, ro, "P07 l.270 'sDD*MM#' or 'sDD*MM'SS#'; degree may be 0xDF (RVM T1, IGO:845)"),
                new("GA", "Telescope altitude", ReplyShape.Terminated, ro, "P07 l.249 'sDD*MM#' or 'sDD*MM'SS#'"),
                new("GZ", "Telescope azimuth", ReplyShape.Terminated, ro, "P07 l.416 'DDD*MM#' or 'DDD*MM'SS#' (P07 misprints 'DDD*MM#T', RVM T1)"),
                new("Gr", "Target RA", ReplyShape.Terminated, ro, "P07 l.366 'HH:MM.T#' or 'HH:MM:SS'"),
                new("Gd", "Target Dec", ReplyShape.Terminated, ro, "P07 l.274 'sDD*MM#' or 'sDD*MM'SS#'"),
                new("D", "Distance bars (slew status)", ReplyShape.Terminated, ro,
                    "P07 l.110-113: one bar until the slew completes, then a null string ('#'). Bar may be 0x7F, '|' or 0xFF (RVM T1, INDI:252-306)"),

                // ---- Time / site ---------------------------------------------------------------------------
                new("GS", "Local sidereal time", ReplyShape.Terminated, ro, "P07 l.376 'HH:MM:SS#'; best check of time+date+longitude (RVM T1)"),
                new("GL", "Local time, 24 h", ReplyShape.Terminated, ro, "P07 l.322 'HH:MM:SS#'"),
                new("GC", "Date", ReplyShape.Terminated, ro, "P07 l.262 'MM/DD/YY#', 'local calendar date' (contradicts the :SC UTC note, RVM MNT-M7)"),
                new("GG", "Hours added to local time to give UTC", ReplyShape.Terminated, ro, "P07 l.298 'sHH#' or 'sHH.H#'"),
                new("Gt", "Site latitude", ReplyShape.Terminated, ro, "P07 l.389 'sDD*MM#'"),
                new("Gg", "Site longitude", ReplyShape.Terminated, ro, "P07 l.303 'sDDD*MM#', East negative; RVM T1: accept 245*49 and -114*11"),
                new("GH", "Daylight saving (Autostar II)", ReplyShape.FixedThenOptionalHash(1), ro,
                    "P07 l.313-316 '1'/'0' (no '#' shown); optional '#' tolerated"),
                new("Gh", "High elevation limit", ReplyShape.DegreeLimit, ro, "P07 l.318 'sDD*' (no '#'); RVM T1 new row: tolerant read"),
                new("Go", "Low elevation limit", ReplyShape.DegreeLimit, ro, "P07 l.346 'DD*#'"),
                new("GT", "Tracking rate", ReplyShape.Terminated, ro, "P07 l.384 'TT.T#'"),

                // ---- Focuser / OTA ---------------------------------------------------------------------------
                new("fT", "OTA temperature (LX200GPS/RCX400)", ReplyShape.Terminated, ro,
                    "P07 l.147-150 'sdd.ddd#'; whether the LX200R answers is open (RIM open questions)"),
                new("F+", "Focuser start inward (toward objective)", ReplyShape.None, move, "P07 l.157"),
                new("F-", "Focuser start outward", ReplyShape.None, move, "P07 l.160"),
                new("FQ", "Focuser halt", ReplyShape.None, halt, "P07 l.205; MN notes one :FQ# is sometimes missed (RVM T1), we send it twice"),
                new("FP", "Focus pulse, signed ms (+ inward), mount-timed", ReplyShape.None, move,
                    "P07 l.194-199 ':FPsDDDD#' LX200GPS/RCX400, range +/-65000; no open driver uses it (RIM MNT-16)", hasArgs: true, example: ":FP+1000#"),
                new("F1", "Focus speed 1 (slowest)", ReplyShape.None, cfg, "P07 l.214", isVolatile: true),
                new("F2", "Focus speed 2", ReplyShape.None, cfg, "P07 l.214", isVolatile: true),
                new("F3", "Focus speed 3", ReplyShape.None, cfg, "P07 l.214", isVolatile: true),
                new("F4", "Focus speed 4 (fastest)", ReplyShape.None, cfg, "P07 l.214", isVolatile: true),
                new("FF", "Focus speed fastest", ReplyShape.None, cfg, "P07 l.208", isVolatile: true),
                new("FS", "Focus speed slowest", ReplyShape.None, cfg, "P07 l.211", isVolatile: true),

                // ---- Protocol / precision ------------------------------------------------------------------
                new("U", "Toggle low/high coordinate precision (serial format only)", ReplyShape.None, cfg, "P07 l.897-901; detect the result from :GR# length (RIM MNT-09)", isVolatile: true),
                new("P", "Toggle High Precision POINTING (must end LOW)", ReplyShape.OneOf("HIGH PRECISION", "LOW PRECISION"), cfg,
                    "P07 l.578-583; MN Telescope.cs:880-905 reads 13/14 chars, no '#' (RVM T1). Never poll it: it toggles."),

                // ---- Rates ---------------------------------------------------------------------------------
                new("RG", "Move rate: guide", ReplyShape.None, cfg, "P07 l.647", isVolatile: true),
                new("RC", "Move rate: centre", ReplyShape.None, cfg, "P07 l.644", isVolatile: true),
                new("RM", "Move rate: find", ReplyShape.None, cfg, "P07 l.650", isVolatile: true),
                new("RS", "Move rate: max", ReplyShape.None, cfg, "P07 l.653", isVolatile: true),
                new("RA", "Azimuth/RA axis rate, deg/s (LX200GPS)", ReplyShape.None, cfg, "P07 l.656 ':RADD.D#'", hasArgs: true, example: ":RA01.5#", isVolatile: true),
                new("RE", "Altitude/Dec axis rate, deg/s (LX200GPS)", ReplyShape.None, cfg, "P07 l.660 ':REDD.D#'", hasArgs: true, example: ":RE01.5#", isVolatile: true),
                new("Rg", "Guide rate, arcsec/s (max 15.0417)", ReplyShape.None, cfg,
                    "P07 l.664-668 ':RgSS.S#'; no getter; format with InvariantCulture (RVM T1)", hasArgs: true, example: ":Rg10.0#"),
                new("TQ", "Sidereal tracking rate", ReplyShape.None, cfg, "P07 l.889"),
                new("TL", "Lunar tracking rate", ReplyShape.None, cfg, "P07 l.883"),

                // ---- Targets and setters (all answer one char '1'/'0') ----------------------------------------
                new("Sr", "Set target RA", ReplyShape.Bool, cfg, "P07 l.820-825 ':SrHH:MM.T#' or ':SrHH:MM:SS#' by precision", hasArgs: true, example: ":Sr05:35:17#", isVolatile: true),
                new("Sd", "Set target Dec", ReplyShape.Bool, cfg, "P07 l.710-714 ':SdsDD*MM#' or 'sDD*MM:SS'", hasArgs: true, example: ":Sd-05*23:28#", isVolatile: true),
                new("Sa", "Set target altitude", ReplyShape.Bool, cfg, "P07 l.672-676", hasArgs: true, example: ":Sa+45*00:00#", isVolatile: true),
                new("Sz", "Set target azimuth", ReplyShape.Bool, cfg, "P07 l.864-868", hasArgs: true, example: ":Sz180*00:00#", isVolatile: true),
                new("St", "Set site latitude", ReplyShape.Bool, cfg, "P07 l.839-843 ':StsDD*MM#'", hasArgs: true, example: ":St+22*15#"),
                new("Sg", "Set site longitude", ReplyShape.Bool, cfg,
                    "P07 l.738-742 ':SgDDD*MM#'; 360-E westward form is MN/IGO practice, INDI sends signed (RVM MNT-15)", hasArgs: true, example: ":Sg245*49#"),
                new("SG", "Set hours added to local time to give UTC", ReplyShape.Bool, cfg, "P07 l.743-753 ':SGsHH.H#'; IGO sends :SG-08#, INDI :SG-8.0# (RVM T1)", hasArgs: true, example: ":SG-08#"),
                new("SH", "Set daylight saving (Autostar II)", ReplyShape.Bool, cfg, "P07 l.756-760", hasArgs: true, example: ":SH0#"),
                new("SL", "Set local time", ReplyShape.Bool, cfg, "P07 l.774-778 ':SLHH:MM:SS#'", hasArgs: true, example: ":SL21:30:00#"),
                new("SC", "Set date (UTC or local: disputed)", ReplyShape.DateResult, cfg,
                    "P07 l.703-708: '0', or '1' + 'Updating Planetary Data#' + spaces '#'; 'UTC data' note vs :GC# local (RVM MNT-M7)",
                    hasArgs: true, example: ":SC10/04/26#", timeout: PlanetaryUpdate),
                new("Sh", "Set low elevation limit", ReplyShape.Bool, cfg, "P07 l.762-766 ':ShDD#'", hasArgs: true, example: ":Sh00#"),
                new("So", "Set high elevation limit", ReplyShape.Bool, cfg, "P07 l.804-808 ':SoDD*#'", hasArgs: true, example: ":So75*#"),
                new("hI", "Answer the startup DST/date/time prompt", ReplyShape.AnyChar, cfg,
                    "P07 l.430-441 ':hIYYMMDDHHMMSS#' '1' if accepted; only valid at the startup prompt", hasArgs: true, example: ":hI261004213000#", timeout: SlowReply),
                new("W", "Select stored site 1-4", ReplyShape.None, cfg, "P07 l.914-917 ':W<n>#'", hasArgs: true, example: ":W1#"),
                new("CM", "Sync to the :Sr/:Sd target", ReplyShape.Terminated, cfg,
                    "P07 l.104-107: Autostar II returns the static \" M31 EX GAL MAG 3.5 SZ178.0'#\" (read and discard)", timeout: SlowReply),

                // ---- Motion --------------------------------------------------------------------------------
                new("MS", "Goto the :Sr/:Sd target", ReplyShape.SlewResult, move,
                    "P07 l.572-576 '0', '1<msg>#' below horizon, '2<msg>#' above limit; MN also handles '3' (RVM T1)", timeout: SlowReply),
                new("MA", "Goto the :Sa/:Sz target", ReplyShape.Bool, move, "P07 l.538-542 '0' no fault / '1' fault", timeout: SlowReply),
                new("Mgn", "Pulse guide north, ms", ReplyShape.None, move,
                    "P07 l.544-551 ':MgnDDDD#' no reply; alt-az acceptance unverified (RVM MNT-05)", hasArgs: true, example: ":Mgn2000#"),
                new("Mgs", "Pulse guide south, ms", ReplyShape.None, move, "P07 l.545", hasArgs: true, example: ":Mgs2000#"),
                new("Mge", "Pulse guide east, ms", ReplyShape.None, move, "P07 l.546", hasArgs: true, example: ":Mge2000#"),
                new("Mgw", "Pulse guide west, ms", ReplyShape.None, move, "P07 l.547", hasArgs: true, example: ":Mgw2000#"),
                new("Mn", "Move north at the current rate", ReplyShape.None, move, "P07 l.556"),
                new("Ms", "Move south at the current rate", ReplyShape.None, move, "P07 l.565"),
                new("Me", "Move east at the current rate", ReplyShape.None, move, "P07 l.553"),
                new("Mw", "Move west at the current rate", ReplyShape.None, move, "P07 l.568"),
                new("AA", "Alt-az mode, tracking on", ReplyShape.None, move, "P07 l.72; tracking on in MN and IGO (RVM T1)"),
                new("AL", "Land mode, tracking off", ReplyShape.None, cfg, "P07 l.66; tracking off in MN and IGO (RVM T1)"),
                new("Q", "Halt all slewing", ReplyShape.None, halt, "P07 l.615"),
                new("Qn", "Halt northward move", ReplyShape.None, halt, "P07 l.627"),
                new("Qs", "Halt southward move", ReplyShape.None, halt, "P07 l.630"),
                new("Qe", "Halt eastward move", ReplyShape.None, halt, "P07 l.618"),
                new("Qw", "Halt westward move", ReplyShape.None, halt, "P07 l.633"),

                // ---- Blocklist: refused before any byte is written -------------------------------------------
                Block("hP", "Park", "P07 l.446",
                    "After :hP# an Autostar stops answering until it is power-cycled (IGO:1907-1916, plan section 5). A carried-in fork has no park."),
                Block("hN", "Sleep telescope", "P07 l.443-444",
                    "Powers off motors, encoders and display; the link looks dead until a key or :hW#. Nothing in M2 needs it."),
                Block("hF", "Seek home and re-align from stored encoders", "P07 l.426-428",
                    "Re-aligns from encoder values stored for a permanent setup; on a scope carried in nightly it replaces the handbox alignment."),
                Block("hC", "Calibrate home position", "P07 l.421-424",
                    "Slews to seek home and rewrites the stored home calibration; meaningless for a nightly setup and it moves the OTA unattended."),
                Block("I", "Re-initialise telescope", "P07 l.467-468",
                    "Restarts the Autostar at power-on initialisation: alignment lost, link drops into the startup prompts."),
                Block("Aa", "Automatic alignment", "P07 l.61-64",
                    "Multi-minute unattended slews (home, level, north, stars) that overwrite the handbox alignment and hold the link; do it on the handbox."),
                Block("AP", "Polar alignment mode", "P07 l.69-70",
                    "This fork has no wedge: polar mode makes every goto wrong and drops the alt-az alignment (RVM T1: never :AP#)."),
                Block("SB", "Change baud rate", "P07 l.690-701",
                    "The mount switches speed after replying; the probe and any later reconnect at 9600 then see garbage until a power cycle."),
                Block("SS", "Set local sidereal time", "P07 l.833-837",
                    "Overwrites LST directly; after alignment it shifts every goto (plan risk 3). Verify with :GS#, never write it."),
                Block("gT", "GPS time update", "P07 l.228-231",
                    "Takes minutes with the handbox blocked, and on stock 4.2g (no rollover fix) the GPS date it writes is wrong (RVM MNT-08)."),
                Block("gps", "NMEA GPS data stream on", "P07 l.225-226",
                    "P07 says it 'turns on' an NMEA stream but documents one '#'-terminated sentence: if more sentences follow on the command link, they land between replies and desynchronise every later reply (plan risk 6). Nothing in M2 needs it."),
                Block("f-", "Accessory panel power off (LX200GPS/R)", "P07 l.128-132",
                    "Cuts power to the accessory panel; which ports of this rig depend on it is unknown and nothing in M2 needs it."),
                Block("$Q", "Smart Drive PEC / SmartMount toggles and training", "P07 l.585-612",
                    "Toggles or retrains stored PEC/SmartMount corrections; a wrong toggle silently degrades tracking."),
                Block("$B", "Set active anti-backlash", "P07 l.75-82",
                    "Overwrites the anti-backlash values Train Drive measured; gotos and guiding would degrade."),
            };
            return list;
        }

        private static Lx200CommandSpec Block(string code, string purpose, string source, string reason) =>
            new(code, purpose, ReplyShape.None, CommandEffect.WritesConfig, source, hasArgs: false, blockedReason: reason);
    }
}
