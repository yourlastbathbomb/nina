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
using System.Globalization;
using System.Linq;

namespace NINA.Mac.Lx200.Sim {

    /// <summary>What ":Mg" pulses do in alt-az mode (RVM MNT-05: unverified on stock firmware).</summary>
    public enum PulseGuideBehaviour {

        /// <summary>n/s move altitude, e/w move azimuth.</summary>
        AltAzAxes,

        /// <summary>n/s move Dec, e/w move RA.</summary>
        EquatorialAxes,

        /// <summary>Accepted silently, nothing moves (what a refusal looks like: :Mg has no reply).</summary>
        Ignored
    }

    /// <summary>A second-axis :Mg sent while the first pulse still runs (RIM open question).</summary>
    public enum TwoAxisPulseBehaviour {
        Concurrent,
        Queued,
        SecondDropped
    }

    /// <summary>Which calendar date ":SC" means (RVM MNT-07/M7: disputed).</summary>
    public enum DateConvention {
        Utc,
        Local
    }

    /// <summary>How ":Gg#" prints longitude.</summary>
    public enum LongitudeReadback {

        /// <summary>P07 l.303: East negative, "-114*11".</summary>
        SignedEastNegative,

        /// <summary>0-360 westward, "245*49".</summary>
        West360
    }

    /// <summary>
    /// Simulator configuration. Defaults model a stock LX200GPS 4.2g at Deep Water Bay (22°15'N 114°11'E, UTC+8)
    /// as the research expects it; every open question from research/rig_*_mount.md is a switch here.
    /// Rates and travel figures marked "assumed" are invented for the simulator, not measured.
    /// </summary>
    public sealed class SimOptions {

        public string Product { get; set; } = "LX2001";

        /// <summary>"4.2g" stock, "4.2k" Meade rollover fix, "4.2G" StarPatch (RVM T1).</summary>
        public string Firmware { get; set; } = "4.2g";

        public string FirmwareDate { get; set; } = "Apr 14 2008";

        public string FirmwareTime { get; set; } = "10:12:31";

        /// <summary>Degree glyph in replies: 0xDF (handbox glyph) or '*'.</summary>
        public byte DegreeByte { get; set; } = 0xDF;

        /// <summary>Minutes/seconds separator in high-precision angle replies: '\'' (P07 l.250) or ':'.</summary>
        public char SecondsSeparator { get; set; } = '\'';

        /// <summary>Format at power-on; ":U#" toggles it.</summary>
        public bool StartInLongFormat { get; set; }

        /// <summary>High Precision pointing state at power-on; ":P#" toggles it. When on, every goto waits for ENTER.</summary>
        public bool HighPrecisionPointing { get; set; }

        /// <summary>Whether ":GW#" answers at all, and whether its 3 chars are followed by '#'.</summary>
        public bool GwSupported { get; set; } = true;

        public bool GwHasHash { get; set; }

        /// <summary>Third ":GW#" char: '0' not aligned, '1'-'3' stars, 'H'/'P'.</summary>
        public char AlignmentStatus { get; set; } = '2';

        public bool AlignmentLostOnLandMode { get; set; }

        public PulseGuideBehaviour PulseGuide { get; set; } = PulseGuideBehaviour.AltAzAxes;

        public TwoAxisPulseBehaviour TwoAxisPulse { get; set; } = TwoAxisPulseBehaviour.Concurrent;

        /// <summary>Guide rate in arcsec/s until ":Rg" changes it (assumed default; MN's default is 10.08).</summary>
        public double GuideRateArcsecPerSec { get; set; } = 10.0;

        /// <summary>Seconds a goto takes in the simulator.</summary>
        public double SlewSeconds { get; set; } = 3.0;

        /// <summary>Byte shown by ":D#" while slewing (0x7F, '|' or 0xFF have been seen).</summary>
        public byte DistanceBar { get; set; } = 0x7F;

        /// <summary>
        /// Seconds after ":MS#" before ":D#" shows the bar (0 = at once): a bar that lags the start of the goto, the
        /// case Meade.net's minimum slewing time covers (RVM MNT-04). Longer than the slew = the bar never shows.
        /// </summary>
        public double DistanceBarDelaySeconds { get; set; }

        /// <summary>Answer NAK (busy) to every halt (":Q#", ":Qn/s/e/w#", ":FQ#") and keep moving.</summary>
        public bool HaltsNaked { get; set; }

        /// <summary>OTA temperature for ":fT#"; null means the command is not answered.</summary>
        public double? OtaTemperatureC { get; set; } = 21.5;

        public bool FocusPulseSupported { get; set; } = true;

        /// <summary>Assumed #1209 travel in µm/s for speeds 1..4.</summary>
        public double[] FocusSpeedsUmPerSec { get; set; } = { 2, 10, 50, 250 };

        /// <summary>Assumed drawtube travel (about half an inch, LX200GPS manual "Focusing the Eyepiece").</summary>
        public double FocusTravelUm { get; set; } = 12700;

        public int FocusSpeedAtStart { get; set; } = 4;

        /// <summary>Assumed backlash: this many ms of motor time are lost after a reversal.</summary>
        public double FocusBacklashMs { get; set; } = 150;

        /// <summary>MN: a single ":FQ#" is sometimes missed. When set, every other first halt is ignored.</summary>
        public bool FirstFocusHaltIgnored { get; set; }

        public DateConvention ScDate { get; set; } = DateConvention.Utc;

        /// <summary>When false, ":GC#" always returns the local date (P07 l.264) whatever ":SC" meant.</summary>
        public bool GcFollowsScConvention { get; set; } = true;

        public LongitudeReadback LongitudeFormat { get; set; } = LongitudeReadback.SignedEastNegative;

        public double SiteLatitude { get; set; } = 22.25;

        public double SiteLongitudeEast { get; set; } = 114.18;

        /// <summary>Usual "UTC+8"; the mount stores the opposite sign (":GG#" = -08).</summary>
        public double UtcOffsetHours { get; set; } = 8;

        /// <summary>Mount clock minus true UTC at start.</summary>
        public double ClockErrorSeconds { get; set; }

        /// <summary>Seconds the planetary update after ":SC" keeps the mount busy (NAKs) before the second string.</summary>
        public double PlanetaryUpdateSeconds { get; set; } = 1.5;

        public bool NakWhileBusy { get; set; } = true;

        /// <summary>Answer every Nth command with NAK once (0 = never). Tests the retry path.</summary>
        public int NakEveryNth { get; set; }

        /// <summary>Answer NAK (busy), instead of the reply, to every command this returns true for, e.g. <c>c => c == ":GR#"</c>; null = none.</summary>
        public Func<string, bool> NakWhen { get; set; }

        /// <summary>Put junk bytes in front of every Nth reply (0 = never). Tests resync.</summary>
        public int GarbageEveryNth { get; set; }

        /// <summary>Send junk after every Nth reply (0 = never). Tests the stale-byte drain.</summary>
        public int TrailingGarbageEveryNth { get; set; }

        public double StartAltitude { get; set; } = 45;

        /// <summary>South-east, off the meridian, so alt-az and equatorial directions differ (parallactic angle ~-40°).</summary>
        public double StartAzimuth { get; set; } = 135;

        public double LowLimitDeg { get; set; }

        public double HighLimitDeg { get; set; } = 90;

        /// <summary>Clock source (tests can inject one).</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public static IReadOnlyList<string> QuirkHelp { get; } = new[] {
            "firmware=4.2g|4.2k|4.2G       :GVN# string",
            "degree=df|star                degree glyph in replies (0xDF or '*')",
            "seconds-sep=apostrophe|colon  minutes/seconds separator",
            "long=true|false               start in long (high precision) format",
            "hp=true|false                 High Precision pointing on at start (gotos hang)",
            "gw=none|nohash|hash           :GW# unsupported / 'AT2' / 'AT2#'",
            "pulse=altaz|eq|ignored        what :Mg does",
            "two-axis=concurrent|queued|dropped",
            "date=utc|local                :SC date convention",
            "gc=follow|local               :GC# follows :SC convention or is always local",
            "lon=signed|west360            :Gg# format",
            "temp=none|<C>                 :fT# absent or value",
            "fp=true|false                 :FP focus pulse supported",
            "halt-miss=true|false          ignore every other first :FQ#",
            "align-lost=true|false         :AL#/:AA# drops the alignment",
            "nak-every=N  garbage-every=N  trailing-every=N",
            "slew=S                        goto duration in seconds",
            "bar-delay=S                   :D# shows no bar for the first S seconds of a goto",
            "halt-nak=true|false           answer NAK to every halt (:Q#, :Qn/s/e/w#, :FQ#) and keep moving",
            "planetary=S                   busy (NAK) time after :SC",
            "clock-error=S                 mount clock minus UTC"
        };

        /// <summary>Applies "key=value" quirk strings (see <see cref="QuirkHelp"/>).</summary>
        public SimOptions Apply(IEnumerable<string> quirks) {
            foreach (var q in quirks ?? Enumerable.Empty<string>()) {
                Apply(q);
            }
            return this;
        }

        /// <summary>Applies one "key=value" quirk. A bad key or value throws <see cref="ArgumentException"/> naming it.</summary>
        public SimOptions Apply(string quirk) {
            var i = quirk?.IndexOf('=') ?? -1;
            if (i <= 0) {
                throw new ArgumentException($"Quirk '{quirk}' is not key=value");
            }
            var key = quirk[..i].Trim().ToLowerInvariant();
            var v = quirk[(i + 1)..].Trim();
            var lower = v.ToLowerInvariant();
            T Pick<T>(params (string Name, T Value)[] choices) {
                foreach (var (name, value) in choices) {
                    if (lower == name) {
                        return value;
                    }
                }
                throw new ArgumentException($"{key}={v}? (one of {string.Join(", ", choices.Select(c => c.Name))})");
            }
            bool Bool() => Pick(("true", true), ("false", false));
            double Number() => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
                ? d
                : throw new ArgumentException($"{key}={v}? (a number)");
            double Seconds() => Number() is var s && s >= 0 ? s : throw new ArgumentException($"{key}={v}? (seconds, 0 or more)");
            int Count() => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new ArgumentException($"{key}={v}? (a whole number, 0 = never)");
            switch (key) {
                case "firmware": Firmware = v.Length > 0 ? v : throw new ArgumentException("firmware= needs a value such as 4.2g"); break;
                case "degree": DegreeByte = Pick(("df", (byte)0xDF), ("0xdf", (byte)0xDF), ("star", (byte)'*'), ("*", (byte)'*')); break;
                case "seconds-sep": SecondsSeparator = Pick(("apostrophe", '\''), ("'", '\''), ("colon", ':'), (":", ':')); break;
                case "long": StartInLongFormat = Bool(); break;
                case "hp": HighPrecisionPointing = Bool(); break;
                case "gw":
                    (GwSupported, GwHasHash) = Pick(("none", (false, false)), ("nohash", (true, false)), ("hash", (true, true)));
                    break;
                case "pulse":
                    PulseGuide = v.ToLowerInvariant() switch {
                        "altaz" => PulseGuideBehaviour.AltAzAxes,
                        "eq" or "equatorial" => PulseGuideBehaviour.EquatorialAxes,
                        "ignored" or "none" => PulseGuideBehaviour.Ignored,
                        _ => throw new ArgumentException($"pulse={v}?")
                    };
                    break;
                case "two-axis":
                    TwoAxisPulse = v.ToLowerInvariant() switch {
                        "concurrent" => TwoAxisPulseBehaviour.Concurrent,
                        "queued" => TwoAxisPulseBehaviour.Queued,
                        "dropped" => TwoAxisPulseBehaviour.SecondDropped,
                        _ => throw new ArgumentException($"two-axis={v}?")
                    };
                    break;
                case "date": ScDate = Pick(("utc", DateConvention.Utc), ("local", DateConvention.Local)); break;
                case "gc": GcFollowsScConvention = Pick(("follow", true), ("local", false)); break;
                case "lon": LongitudeFormat = Pick(("signed", LongitudeReadback.SignedEastNegative), ("west360", LongitudeReadback.West360)); break;
                case "temp": OtaTemperatureC = lower == "none" ? null : Number(); break;
                case "fp": FocusPulseSupported = Bool(); break;
                case "halt-miss": FirstFocusHaltIgnored = Bool(); break;
                case "align-lost": AlignmentLostOnLandMode = Bool(); break;
                case "nak-every": NakEveryNth = Count(); break;
                case "garbage-every": GarbageEveryNth = Count(); break;
                case "trailing-every": TrailingGarbageEveryNth = Count(); break;
                case "slew": SlewSeconds = Seconds(); break;
                case "bar-delay": DistanceBarDelaySeconds = Seconds(); break;
                case "halt-nak": HaltsNaked = Bool(); break;
                case "planetary": PlanetaryUpdateSeconds = Seconds(); break;
                case "clock-error": ClockErrorSeconds = Number(); break;
                default: throw new ArgumentException($"Unknown simulator quirk '{key}'");
            }
            return this;
        }

        public string Describe() => string.Create(CultureInfo.InvariantCulture,
            $"{Product} {Firmware}, degree {(DegreeByte == 0xDF ? "0xDF" : "'*'")}, {(StartInLongFormat ? "long" : "short")} format, HP pointing {(HighPrecisionPointing ? "ON" : "off")}, " +
            $":GW# {(GwSupported ? (GwHasHash ? "AT2#" : "AT2") : "none")}, pulse {PulseGuide}/{TwoAxisPulse}, :SC {ScDate}, :Gg# {LongitudeFormat}, " +
            $":fT# {(OtaTemperatureC.HasValue ? OtaTemperatureC.Value.ToString("0.0", CultureInfo.InvariantCulture) + " C" : "none")}, :FP {(FocusPulseSupported ? "yes" : "no")}, " +
            $"slew {SlewSeconds:0.#} s, NAK every {NakEveryNth}, garbage every {GarbageEveryNth}");
    }
}
