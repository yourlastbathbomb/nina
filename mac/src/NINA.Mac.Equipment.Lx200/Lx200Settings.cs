#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Profile;
using NINA.Profile.Interfaces;
using System;

namespace NINA.Mac.Equipment.Lx200 {

    /// <summary>
    /// How <see cref="Lx200Telescope.PulseGuide"/> moves the mount. Plan risk 1 (RIM MNT-05) asked whether the firmware accepts
    /// ":Mg" in alt-az mode; the M2 read-out on this rig's mount (firmware 4.0g, 2026-10-06, mac/docs/m2-mount-readout-2026-10-06.md)
    /// showed that it does: ":Mgn/s/e/w3000#" ran the motors on all four directions, unaligned. <see cref="NativePulse"/> is
    /// therefore the default (<see cref="Lx200Settings.PulseStrategy"/>); the others stay selectable. The size and direction of a
    /// pulse per axis after an alignment is still to be measured at the bench (checklist step 6).
    /// </summary>
    public enum Lx200PulseStrategy {

        /// <summary>
        /// <see cref="NativePulse"/> on StarPatch firmware (":GVN#" ends in an upper-case letter, e.g. "4.2G", which Meade.net trusts
        /// with alt-az pulses, RVM MNT-M5), otherwise <see cref="HostTimedMove"/>, which moves on every firmware. The conservative
        /// choice from before the M2 read-out; no longer the default.
        /// </summary>
        Auto,

        /// <summary>":Mg{n,s,e,w}DDDD#": the mount times the pulse (P07 l.544-551). The default: accepted in alt-az by this rig's 4.0g.</summary>
        NativePulse,

        /// <summary>":RG#" (guide rate), ":Mn#"/":Ms#"/":Me#"/":Mw#", then the matching ":Qx#" timed by the host (Meade.net's "old method").</summary>
        HostTimedMove,

        /// <summary>
        /// The pulse becomes a small goto offset (":Sr"/":Sd"/":MS#") of duration x guide rate. Coarse: RA steps are 1 s of time,
        /// about 14" at Dec -20 (RVM MNT-M4), so only for large dithers.
        /// </summary>
        GotoOffset
    }

    /// <summary>How the #1209 is timed.</summary>
    public enum Lx200FocusMethod {

        /// <summary>":F+#"/":F-#", then ":FQ#" (twice) when the host's clock says so. Every open driver does this.</summary>
        HostTimed,

        /// <summary>":FPsDDDD#", timed by the mount (P07 l.194-199, LX200GPS). No open driver uses it; the bench step 7 decides.</summary>
        MountPulse
    }

    /// <summary>Which calendar date ":SC" takes on this firmware (RVM MNT-07/M7: disputed; bench step 4 decides).</summary>
    public enum Lx200DateConvention {

        /// <summary>Not settled: the date is written only when the UTC date and the local date are the same day.</summary>
        Unknown,

        Utc,

        Local
    }

    /// <summary>
    /// Settings of the LX200 driver, stored in the active NINA profile (its plugin store, <see cref="PluginOptionsAccessor"/>,
    /// under <see cref="SettingsId"/>), so each optical-train profile keeps its own. The defaults are reasoned guesses until the
    /// M2 bench results are in; each says where its value comes from.
    /// </summary>
    public sealed class Lx200Settings {

        /// <summary>Key of the driver's settings in the profile's plugin store.</summary>
        public static readonly Guid SettingsId = new("5b0c2f4e-7a61-4d0b-9e3a-2c8f1d6a4b70");

        private readonly IPluginOptionsAccessor store;

        public Lx200Settings(IProfileService profileService) : this(new PluginOptionsAccessor(profileService, SettingsId)) {
        }

        public Lx200Settings(IPluginOptionsAccessor store) {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // ---- Link -------------------------------------------------------------------------------------------

        /// <summary>Serial port, e.g. /dev/cu.usbserial-A10KXYZ. Empty: the only /dev/cu.usbserial-* or /dev/cu.usbmodem-* present.</summary>
        public string PortPath {
            get => store.GetValueString(nameof(PortPath), string.Empty);
            set => store.SetValueString(nameof(PortPath), value ?? string.Empty);
        }

        /// <summary>How often the mount's position is read for NINA's property cache (NINA itself polls every 2 s).</summary>
        public int PollIntervalMs {
            get => Math.Clamp(store.GetValueInt32(nameof(PollIntervalMs), 1000), 100, 10000);
            set => store.SetValueInt32(nameof(PollIntervalMs), value);
        }

        // ---- Mount ------------------------------------------------------------------------------------------

        /// <summary>
        /// Pulse-guide strategy; default <see cref="Lx200PulseStrategy.NativePulse"/> (":Mg", measured to work in alt-az on this rig's
        /// firmware 4.0g). A value stored in the profile wins, so a profile that chose another strategy keeps it.
        /// </summary>
        public Lx200PulseStrategy PulseStrategy {
            get => store.GetValueEnum(nameof(PulseStrategy), Lx200PulseStrategy.NativePulse);
            set => store.SetValueEnum(nameof(PulseStrategy), value);
        }

        /// <summary>
        /// Guide rate in arcsec/s, sent with ":Rg" at connect and reported to NINA as both guide rates (there is no getter, RIM
        /// MNT-06). 10.0 is close to Meade.net's default of 10.08 (67% sidereal); the maximum is sidereal, 15.0417.
        /// </summary>
        public double GuideRateArcsecPerSec {
            get => Math.Clamp(store.GetValueDouble(nameof(GuideRateArcsecPerSec), 10.0), 0.1, 15.0417);
            set => store.SetValueDouble(nameof(GuideRateArcsecPerSec), value);
        }

        public bool SetGuideRateOnConnect {
            get => store.GetValueBoolean(nameof(SetGuideRateOnConnect), true);
            set => store.SetValueBoolean(nameof(SetGuideRateOnConnect), value);
        }

        /// <summary>
        /// Run a second-axis pulse after the first one ends instead of at the same time, until the bench shows the Autostar takes
        /// two axes at once (RIM MNT-05 rec; NINA's DirectGuider sends both back to back).
        /// </summary>
        public bool SerializePulseAxes {
            get => store.GetValueBoolean(nameof(SerializePulseAxes), true);
            set => store.SetValueBoolean(nameof(SerializePulseAxes), value);
        }

        /// <summary>Syncs farther than this from where the mount says it points are refused (a bad plate solve; RIM MNT-11). 0 = no limit.</summary>
        public double MaxSyncOffsetDegrees {
            get => store.GetValueDouble(nameof(MaxSyncOffsetDegrees), 3.0);
            set => store.SetValueDouble(nameof(MaxSyncOffsetDegrees), value);
        }

        /// <summary>An empty ":D#" this soon after ":MS#" does not end a goto: the bar can lag the start (Meade.net, RVM MNT-04).</summary>
        public double MinimumSlewSeconds {
            get => store.GetValueDouble(nameof(MinimumSlewSeconds), 1.5);
            set => store.SetValueDouble(nameof(MinimumSlewSeconds), value);
        }

        /// <summary>A goto still running after this long is stopped with ":Q#" (High Precision pointing waiting for ENTER looks like this).</summary>
        public double SlewTimeoutSeconds {
            get => store.GetValueDouble(nameof(SlewTimeoutSeconds), 240);
            set => store.SetValueDouble(nameof(SlewTimeoutSeconds), value);
        }

        /// <summary>A goto that ends farther than this from its target is reported as failed (the probe's sync guard is 10').</summary>
        public double ArrivalToleranceArcmin {
            get => store.GetValueDouble(nameof(ArrivalToleranceArcmin), 10);
            set => store.SetValueDouble(nameof(ArrivalToleranceArcmin), value);
        }

        /// <summary>Toggle ":P#" at connect until it reads LOW: High Precision pointing makes every goto wait for ENTER (RIM MNT-09).</summary>
        public bool EnsureLowPrecisionPointing {
            get => store.GetValueBoolean(nameof(EnsureLowPrecisionPointing), true);
            set => store.SetValueBoolean(nameof(EnsureLowPrecisionPointing), value);
        }

        /// <summary>
        /// After a sync or a goto, report the target itself while the mount's reply is within its 1 s / 1" resolution of it, so
        /// NINA's 1" post-sync settle loop does not wait out its 5 s on rounding alone (plan section 6).
        /// </summary>
        public bool ReportTargetWithinResolution {
            get => store.GetValueBoolean(nameof(ReportTargetWithinResolution), true);
            set => store.SetValueBoolean(nameof(ReportTargetWithinResolution), value);
        }

        public Lx200DateConvention DateConvention {
            get => store.GetValueEnum(nameof(DateConvention), Lx200DateConvention.Unknown);
            set => store.SetValueEnum(nameof(DateConvention), value);
        }

        /// <summary>
        /// The driver's own slew guard (defence in depth below the app's checks): <see cref="Lx200Telescope.SlewToCoordinates"/>
        /// and <see cref="Lx200Telescope.SlewToAltAz"/> refuse a target whose altitude now, at the profile's site (NINA's own
        /// transform), is below the profile's custom horizon (0° without one) or above <see cref="MaxAltitudeDegrees"/>. Soft
        /// park and dither offsets are not checked. On by default.
        /// </summary>
        public bool SlewGuardEnabled {
            get => store.GetValueBoolean(nameof(SlewGuardEnabled), true);
            set => store.SetValueBoolean(nameof(SlewGuardEnabled), value);
        }

        /// <summary>
        /// Highest altitude a goto may go to: the zenith keyhole of the alt-az fork (MAC_PORT_PLAN section 6, decision 5: 75°).
        /// Used by the slew guard and, when <see cref="WriteMountHighLimitOnConnect"/> is on, written to the mount with ":So".
        /// </summary>
        public double MaxAltitudeDegrees {
            get => Math.Clamp(store.GetValueDouble(nameof(MaxAltitudeDegrees), 75.0), 1.0, 90.0);
            set => store.SetValueDouble(nameof(MaxAltitudeDegrees), value);
        }

        /// <summary>
        /// Write <see cref="MaxAltitudeDegrees"/> (whole degrees, rounded down) to the Autostar as its high limit (":SoDD*#", P07
        /// l.804-808) at every connect and reconnect, then read it back with ":Gh#". The Autostar keeps the value in its own
        /// memory (a configuration write). Off by default until the bench shows that firmware 4.0g accepts and honours ":So".
        /// </summary>
        public bool WriteMountHighLimitOnConnect {
            get => store.GetValueBoolean(nameof(WriteMountHighLimitOnConnect), false);
            set => store.SetValueBoolean(nameof(WriteMountHighLimitOnConnect), value);
        }

        /// <summary>The mount keeps its site to 1' (":Gt#" sDD*MM, ":Gg#" sDDD*MM); within this, the profile's value is reported (RIM MNT-14).</summary>
        public double SiteToleranceArcmin {
            get => store.GetValueDouble(nameof(SiteToleranceArcmin), 1.0);
            set => store.SetValueDouble(nameof(SiteToleranceArcmin), value);
        }

        /// <summary>Soft park ends with ":AL#" (tracking off). The bench step 8 shows whether ":AL#"/":AA#" keeps the alignment.</summary>
        public bool SoftParkStopsTracking {
            get => store.GetValueBoolean(nameof(SoftParkStopsTracking), true);
            set => store.SetValueBoolean(nameof(SoftParkStopsTracking), value);
        }

        /// <summary>Soft-park position (alt/az goto). NaN: park where the mount stands. Set with <see cref="Lx200Telescope.Setpark"/>.</summary>
        public double SoftParkAltitude {
            get => store.GetValueDouble(nameof(SoftParkAltitude), double.NaN);
            set => store.SetValueDouble(nameof(SoftParkAltitude), value);
        }

        public double SoftParkAzimuth {
            get => store.GetValueDouble(nameof(SoftParkAzimuth), double.NaN);
            set => store.SetValueDouble(nameof(SoftParkAzimuth), value);
        }

        /// <summary>
        /// The driver soft-parked the mount and nothing has unparked it since; kept in the profile, so a reconnect (or the next
        /// session) still reports AtPark and NINA's Unpark resumes tracking. Written by the driver.
        /// </summary>
        public bool SoftParked {
            get => store.GetValueBoolean(nameof(SoftParked), false);
            set => store.SetValueBoolean(nameof(SoftParked), value);
        }

        // ---- Focuser ----------------------------------------------------------------------------------------

        /// <summary>
        /// Speed ":F1#" (slowest) to ":F4#", sent before every move, so a virtual step is always one millisecond at this speed
        /// (the handbox can change the speed; Meade.net never sets it, RVM MNT-M6). The M2 probe left speed 2.
        /// </summary>
        public int FocuserSpeed {
            get => Math.Clamp(store.GetValueInt32(nameof(FocuserSpeed), 2), 1, 4);
            set => store.SetValueInt32(nameof(FocuserSpeed), value);
        }

        /// <summary>Virtual travel in ms; the position starts at the middle. 65000 is the ":FP" range (RIM Table 3).</summary>
        public int FocuserMaxStep {
            get => Math.Clamp(store.GetValueInt32(nameof(FocuserMaxStep), 65000), 2, 1_000_000);
            set => store.SetValueInt32(nameof(FocuserMaxStep), value);
        }

        public Lx200FocusMethod FocusMethod {
            get => store.GetValueEnum(nameof(FocusMethod), Lx200FocusMethod.HostTimed);
            set => store.SetValueEnum(nameof(FocusMethod), value);
        }

        /// <summary>False: a lower position is inward (":F+#", toward the objective), as NINA's "In" buttons expect (RVM MNT-17).</summary>
        public bool FocuserReverse {
            get => store.GetValueBoolean(nameof(FocuserReverse), false);
            set => store.SetValueBoolean(nameof(FocuserReverse), value);
        }

        /// <summary>Extra motor time after a change of direction, not counted in the position. 0: leave backlash to NINA's own compensation.</summary>
        public int FocuserBacklashMs {
            get => Math.Max(0, store.GetValueInt32(nameof(FocuserBacklashMs), 0));
            set => store.SetValueInt32(nameof(FocuserBacklashMs), value);
        }

        /// <summary>Send ":FQ#" twice (Meade.net notes a single halt is sometimes missed).</summary>
        public bool FocuserDoubleHalt {
            get => store.GetValueBoolean(nameof(FocuserDoubleHalt), true);
            set => store.SetValueBoolean(nameof(FocuserDoubleHalt), value);
        }
    }
}
