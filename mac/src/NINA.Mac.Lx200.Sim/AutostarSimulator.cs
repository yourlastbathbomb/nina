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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Lx200.Sim {

    public sealed class SimLogEntry {

        public SimLogEntry(DateTime utc, string command, bool naked) {
            Utc = utc;
            Command = command;
            Naked = naked;
        }

        public DateTime Utc { get; }

        /// <summary>":GR#" style text, or "ACK".</summary>
        public string Command { get; }

        /// <summary>The simulator answered NAK and did not process it.</summary>
        public bool Naked { get; }

        public override string ToString() => Command;
    }

    /// <summary>
    /// Autostar II (LX200GPS) protocol simulator over a <see cref="Stream"/>. Reply bytes follow P07
    /// (research/reference/proto2007.txt) and are formatted independently of NINA.Mac.Lx200's formatters so the
    /// simulator stays a separate oracle; only sidereal time and alt-az conversions are shared.
    /// <para>
    /// Model: the mount knows its axis altitude/azimuth. Its RA/Dec is derived from them with its own clock and
    /// site, so changing the date or longitude changes the RA it reports (as on the real mount), and tracking
    /// moves the axes to hold RA/Dec. Moves and pulses act on the axes at the guide/move rate in axis degrees.
    /// </para>
    /// </summary>
    public sealed class AutostarSimulator : IDisposable {

        public const string SyncReply = " M31 EX GAL MAG 3.5 SZ178.0'#";

        private static readonly Encoding Latin1 = Encoding.Latin1;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Regex HmsRegex = new(@"^(\d{1,2}):(\d{1,2})(?::(\d{1,2})|\.(\d))$", RegexOptions.CultureInvariant);
        private static readonly Regex DmsRegex = new(@"^([+-])?(\d{1,3})[*ß:](\d{1,2})(?:[:'](\d{1,2}))?\*?$", RegexOptions.CultureInvariant);
        private static readonly Regex DateRegex = new(@"^(\d{2})/(\d{2})/(\d{2})$", RegexOptions.CultureInvariant);

        private readonly Stream stream;
        private readonly SimOptions o;
        private readonly object state = new();
        private readonly object writeLock = new();
        private readonly Thread thread;
        private readonly List<SimLogEntry> log = new();
        private readonly List<Motion> motions = new();
        private volatile bool disposed;

        // ---- mount state (guarded by 'state') ----
        private double axisAlt;
        private double axisAz;
        private DateTime lastAdvance;
        private TimeSpan clockOffset;
        private double hoursToUtc;
        private int latitudeArcmin;
        private int longitudeWestArcmin;
        private bool dst;
        private double lowLimit;
        private double highLimit;
        private bool longFormat;
        private bool hpPointing;
        private bool tracking = true;
        private bool wasLand;
        private char mode = 'A';
        private char alignChar;
        private double targetRa;
        private double targetDec;
        private double targetAlt;
        private double targetAz;
        private Slew slew;
        private double moveRateDegPerSec;
        private double guideRateArcsecPerSec;
        private int focusSpeed;
        private double focusUm;
        private FocusMove focusMove;
        private int lastFocusDir;
        private double focusBacklashLeftMs;
        private bool haltMissArmed = true;
        private DateTime busyUntil;
        private bool silent;
        private bool garbled;
        private int commandCount;
        private int replyCount;

        public AutostarSimulator(Stream stream, SimOptions options = null) {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            o = options ?? new SimOptions();
            var now = o.UtcNow();
            lastAdvance = now;
            clockOffset = TimeSpan.FromSeconds(o.ClockErrorSeconds);
            hoursToUtc = -o.UtcOffsetHours;
            latitudeArcmin = (int)Math.Round(o.SiteLatitude * 60);
            longitudeWestArcmin = (int)Math.Round(Lx200Astro.Wrap(360 - o.SiteLongitudeEast, 360) * 60) % 21600;
            lowLimit = o.LowLimitDeg;
            highLimit = o.HighLimitDeg;
            longFormat = o.StartInLongFormat;
            hpPointing = o.HighPrecisionPointing;
            alignChar = o.AlignmentStatus;
            axisAlt = o.StartAltitude;
            axisAz = o.StartAzimuth;
            guideRateArcsecPerSec = o.GuideRateArcsecPerSec;
            moveRateDegPerSec = guideRateArcsecPerSec / 3600.0;
            focusSpeed = Math.Clamp(o.FocusSpeedAtStart, 1, 4);
            focusUm = o.FocusTravelUm / 2;
            (targetRa, targetDec) = RaDec(now);
            thread = new Thread(Loop) { IsBackground = true, Name = "autostar-sim" };
            thread.Start();
        }

        public SimOptions Options => o;

        /// <summary>Raised for every complete command received (":GR#" or "ACK"), before it is processed.</summary>
        public event Action<string> CommandReceived;

        public IReadOnlyList<SimLogEntry> Log {
            get {
                lock (log) {
                    return log.ToArray();
                }
            }
        }

        public IReadOnlyList<string> ReceivedCommands => Log.Select(e => e.Command).ToArray();

        // ---- ground truth for tests and the --sim results ----

        public double FocuserPositionUm => Locked(now => focusUm);

        public bool FocuserMoving => Locked(now => focusMove != null);

        public int FocusSpeed => Locked(now => focusSpeed);

        public (double Alt, double Az) Axes => Locked(now => (axisAlt, axisAz));

        public (double RaHours, double DecDeg) BelievedRaDec => Locked(RaDec);

        public bool Tracking => Locked(now => tracking);

        public char Mode => Locked(now => mode);

        public bool HighPrecisionPointing => Locked(now => hpPointing);

        public bool LongFormat => Locked(now => longFormat);

        public bool Slewing => Locked(now => slew != null);

        public bool AnyAxisMotion => Locked(now => slew != null || motions.Count > 0);

        public bool IsSilent => Locked(now => silent);

        public DateTime MountUtc => Locked(MountUtcAt);

        public double LongitudeEast => Locked(now => EastLongitude);

        public double GuideRateArcsecPerSec => Locked(now => guideRateArcsecPerSec);

        /// <summary>Clears the "silent after :hP#" and "garbled after :SB" states, like switching the mount off and on.</summary>
        public void PowerCycle() {
            lock (state) {
                silent = false;
                garbled = false;
            }
        }

        /// <summary>
        /// The mount switched off and on: as <see cref="PowerCycle"/>, and it also forgets the session. Every motion stops; the
        /// coordinate format, High Precision pointing, the guide rate and the focus speed are back at their power-on values; the
        /// alignment is lost (":GW#" ends in '0'). Site and clock survive, as the Autostar keeps them. The cable is left as it is.
        /// </summary>
        public void SwitchOffAndOn() {
            lock (state) {
                Advance(o.UtcNow());
                silent = false;
                garbled = false;
                slew = null;
                motions.Clear();
                focusMove = null;
                longFormat = o.StartInLongFormat;
                hpPointing = o.HighPrecisionPointing;
                alignChar = '0';
                guideRateArcsecPerSec = o.GuideRateArcsecPerSec;
                moveRateDegPerSec = guideRateArcsecPerSec / 3600.0;
                focusSpeed = Math.Clamp(o.FocusSpeedAtStart, 1, 4);
                busyUntil = default;
            }
        }

        public void Dispose() {
            if (disposed) {
                return;
            }
            disposed = true;
            try {
                stream.Dispose();
            } catch (Exception) {
                // the other end may already be gone
            }
            thread.Join(TimeSpan.FromSeconds(2));
        }

        // =============================================================================================

        private void Loop() {
            var buffer = new byte[256];
            var cmd = new StringBuilder();
            var inCommand = false;
            try {
                while (!disposed) {
                    var n = stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) {
                        return;
                    }
                    for (var i = 0; i < n; i++) {
                        var b = buffer[i];
                        if (b == (byte)':' && !inCommand) {
                            inCommand = true;   // ':' inside a command is an argument character (":SL01:00:00#")
                            cmd.Clear();
                        } else if (inCommand && b == (byte)'#') {
                            inCommand = false;
                            Dispatch(cmd.ToString());
                        } else if (inCommand) {
                            cmd.Append((char)b);
                            if (cmd.Length > 64) {
                                inCommand = false;
                            }
                        } else if (b == 0x06) {
                            DispatchAck();
                        }
                        // any other byte outside a command is ignored, as the Autostar does
                    }
                }
            } catch (Exception) when (disposed) {
                // shutting down
            } catch (IOException) {
                // client went away
            } catch (ObjectDisposedException) {
                // client went away
            }
        }

        private void DispatchAck() {
            Record("ACK", false);
            string reply;
            lock (state) {
                if (silent) {
                    return;
                }
                reply = mode.ToString();
            }
            Send(reply, countAsReply: false);
        }

        private void Dispatch(string body) {
            var text = ":" + body + "#";
            var now = o.UtcNow();
            bool nak;
            lock (state) {
                if (silent) {
                    Record(text, false);
                    return;
                }
                commandCount++;
                nak = (o.NakEveryNth > 0 && commandCount % o.NakEveryNth == 0) || (o.NakWhileBusy && now < busyUntil)
                    || (o.HaltsNaked && body is "Q" or "Qn" or "Qs" or "Qe" or "Qw" or "FQ")
                    || o.NakWhen?.Invoke(text) == true;
            }
            Record(text, nak);
            if (nak) {
                Write(new byte[] { 0x15 });
                return;
            }
            string reply;
            lock (state) {
                Advance(now);
                reply = Handle(body, now);
            }
            if (reply != null) {
                Send(reply, countAsReply: true);
            }
        }

        private void Record(string command, bool naked) {
            lock (log) {
                log.Add(new SimLogEntry(o.UtcNow(), command, naked));
            }
            CommandReceived?.Invoke(command);
        }

        private void Send(string reply, bool countAsReply) {
            bool garbage = false, trailing = false, scramble;
            lock (state) {
                scramble = garbled;
                if (countAsReply) {
                    replyCount++;
                    garbage = o.GarbageEveryNth > 0 && replyCount % o.GarbageEveryNth == 0;
                    trailing = o.TrailingGarbageEveryNth > 0 && replyCount % o.TrailingGarbageEveryNth == 0;
                }
            }
            if (scramble) {
                Write(new byte[] { 0xF8, 0x80, 0xF8 });
                return;
            }
            var bytes = new List<byte>();
            if (garbage) {
                bytes.AddRange(new byte[] { 0xFF, (byte)'x', (byte)'#' });
            }
            bytes.AddRange(Latin1.GetBytes(reply));
            if (trailing) {
                bytes.AddRange(new byte[] { 0xFF, 0xFE });
            }
            Write(bytes.ToArray());
        }

        private void Write(byte[] bytes) {
            lock (writeLock) {
                try {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                } catch (Exception) when (disposed) {
                    // closing
                } catch (IOException) {
                    // client went away
                } catch (ObjectDisposedException) {
                    // client went away
                }
            }
        }

        // =============================================================================================
        // Command handling (inside the state lock). Returns the reply text (Latin-1) or null for none.
        // =============================================================================================

        private string Handle(string b, DateTime now) {
            switch (b) {
                case "GVP": return o.Product + "#";
                case "GVN": return o.Firmware + "#";
                case "GVD": return o.FirmwareDate + "#";
                case "GVT": return o.FirmwareTime + "#";
                case "GW": return o.GwSupported ? $"{mode}{(tracking ? 'T' : 'N')}{alignChar}{(o.GwHasHash ? "#" : "")}" : null;
                case "GR": return FormatRa(RaDec(now).RaHours) + "#";
                case "GD": return FormatSigned(RaDec(now).DecDeg) + "#";
                case "GA": return FormatSigned(axisAlt) + "#";
                case "GZ": return FormatAzimuth(axisAz) + "#";
                case "Gr": return FormatRa(targetRa) + "#";
                case "Gd": return FormatSigned(targetDec) + "#";
                case "D": return slew != null && (now - slew.Start).TotalSeconds >= o.DistanceBarDelaySeconds ? $"{(char)o.DistanceBar}#" : "#";
                case "GS": return FormatHms(Lst(now)) + "#";
                case "GL": return FormatHms(LocalOf(MountUtcAt(now)).TimeOfDay.TotalHours) + "#";
                case "GC": return MountDateForGc(now).ToString("MM'/'dd'/'yy", Inv) + "#";
                case "GG": return FormatHoursToUtc() + "#";
                case "Gt": return FormatSiteAngle(latitudeArcmin, 2, signed: true) + "#";
                case "Gg": return FormatLongitude() + "#";
                case "GH": return dst ? "1" : "0";
                case "Gh": return string.Create(Inv, $"+{(int)highLimit:00}{Deg}");
                case "Go": return string.Create(Inv, $"{(int)lowLimit:00}{Deg}#");
                case "GT": return "60.1#";
                case "fT":
                    return o.OtaTemperatureC.HasValue
                        ? string.Create(Inv, $"{(o.OtaTemperatureC.Value < 0 ? "-" : "+")}{Math.Abs(o.OtaTemperatureC.Value):00.000}#")
                        : null;
                case "F+": StartFocus(-1, null, now); return null;
                case "F-": StartFocus(+1, null, now); return null;
                case "FQ":
                    if (o.FirstFocusHaltIgnored && focusMove != null && haltMissArmed) {
                        haltMissArmed = false;   // this halt is "missed"; the next one works
                        return null;
                    }
                    haltMissArmed = true;
                    focusMove = null;
                    return null;
                case "F1": case "F2": case "F3": case "F4": focusSpeed = b[1] - '0'; return null;
                case "FF": focusSpeed = 4; return null;
                case "FS": focusSpeed = 1; return null;
                case "U": longFormat = !longFormat; return null;
                case "P":
                    hpPointing = !hpPointing;
                    return hpPointing ? "HIGH PRECISION" : "LOW PRECISION";
                case "RG": moveRateDegPerSec = guideRateArcsecPerSec / 3600.0; return null;
                case "RC": moveRateDegPerSec = 0.067; return null;   // manual speed 4 (16x), assumed mapping
                case "RM": moveRateDegPerSec = 1.5; return null;     // speed 7, assumed
                case "RS": moveRateDegPerSec = 8.0; return null;     // speed 9 (max)
                case "TQ": case "TL": return null;
                case "CM":
                    SetAxesTo(targetRa, targetDec, now);
                    return SyncReply;
                case "MS": return StartGoto(now);
                case "MA":
                    slew = new Slew(now, o.SlewSeconds, axisAlt, axisAz, null, null, targetAlt, targetAz, hpPointing);
                    return "0";
                case "Mn": case "Ms": case "Me": case "Mw":
                    StartMove(b, b[1], null, moveRateDegPerSec, now, manual: true);
                    return null;
                case "AA":
                    mode = 'A';
                    tracking = true;
                    if (o.AlignmentLostOnLandMode && wasLand) {
                        alignChar = '0';
                    }
                    return null;
                case "AL":
                    mode = 'L';
                    tracking = false;
                    wasLand = true;
                    return null;
                case "Q":
                    slew = null;
                    motions.Clear();
                    return null;
                case "Qn": case "Qs": case "Qe": case "Qw":
                    motions.RemoveAll(m => m.Tag == "M" + b[1]);
                    return null;
                case "hP":
                    silent = true;   // what the research warns about: silent until power-cycled
                    return null;
            }

            if (b.StartsWith("Mg", StringComparison.Ordinal) && b.Length > 3) {
                if (int.TryParse(b.AsSpan(3), NumberStyles.None, Inv, out var ms) && ms > 0) {
                    StartPulse(b[2], ms, now);
                }
                return null;
            }
            if (b.StartsWith("FP", StringComparison.Ordinal)) {
                if (o.FocusPulseSupported && int.TryParse(b.AsSpan(2), NumberStyles.AllowLeadingSign, Inv, out var ms) && ms != 0 && Math.Abs(ms) <= 65000) {
                    StartFocus(ms > 0 ? -1 : +1, now.AddMilliseconds(Math.Abs(ms)), now);
                }
                return null;
            }
            if (b.StartsWith("Rg", StringComparison.Ordinal)) {
                if (double.TryParse(b.AsSpan(2), NumberStyles.Float, Inv, out var r) && r > 0 && r <= 15.0417) {
                    guideRateArcsecPerSec = r;
                }
                return null;
            }
            if ((b.StartsWith("RA", StringComparison.Ordinal) || b.StartsWith("RE", StringComparison.Ordinal)) && b.Length > 2) {
                if (double.TryParse(b.AsSpan(2), NumberStyles.Float, Inv, out var r) && r > 0) {
                    moveRateDegPerSec = r;
                }
                return null;
            }
            if (b.StartsWith("Sr", StringComparison.Ordinal)) {
                return Accept(TryHms(b[2..], out var v), v, x => targetRa = x);
            }
            if (b.StartsWith("Sd", StringComparison.Ordinal)) {
                return Accept(TryDms(b[2..], out var v) && Math.Abs(v) <= 90, v, x => targetDec = x);
            }
            if (b.StartsWith("Sa", StringComparison.Ordinal)) {
                return Accept(TryDms(b[2..], out var v) && Math.Abs(v) <= 90, v, x => targetAlt = x);
            }
            if (b.StartsWith("Sz", StringComparison.Ordinal)) {
                return Accept(TryDms(b[2..], out var v) && v >= 0 && v < 360, v, x => targetAz = x);
            }
            if (b.StartsWith("St", StringComparison.Ordinal)) {
                if (!TryDms(b[2..], out var lat) || Math.Abs(lat) > 90) {
                    return "0";
                }
                latitudeArcmin = (int)Math.Round(lat * 60);
                return "1";
            }
            if (b.StartsWith("Sg", StringComparison.Ordinal)) {
                if (!TryDms(b[2..], out var lon) || Math.Abs(lon) >= 360) {
                    return "0";
                }
                // DDD*MM westward 0-360; a signed value is the same meridian (-114*11 = 245*49)
                longitudeWestArcmin = (int)Math.Round(Lx200Astro.Wrap(lon, 360) * 60) % 21600;
                return "1";
            }
            if (b.StartsWith("SG", StringComparison.Ordinal)) {
                if (!double.TryParse(b.AsSpan(2), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Inv, out var h) || Math.Abs(h) > 14) {
                    return "0";
                }
                hoursToUtc = h;   // the mount clock (UTC) stays; local time follows
                return "1";
            }
            if (b.StartsWith("SH", StringComparison.Ordinal)) {
                dst = b.EndsWith('1');
                return "1";
            }
            if (b.StartsWith("SL", StringComparison.Ordinal)) {
                if (!TryHms(b[2..], out var hours) || !b.Contains(':') || b.Length != 10) {
                    return "0";
                }
                SetLocalTime(TimeSpan.FromHours(hours), now);
                return "1";
            }
            if (b.StartsWith("SC", StringComparison.Ordinal)) {
                var m = DateRegex.Match(b[2..]);
                if (!m.Success || !DateTime.TryParseExact(b[2..], "MM'/'dd'/'yy", Inv, DateTimeStyles.None, out var date)) {
                    return "0";
                }
                SetDate(date, now);
                busyUntil = now.AddSeconds(o.PlanetaryUpdateSeconds);
                var delay = TimeSpan.FromSeconds(o.PlanetaryUpdateSeconds);
                _ = Task.Run(async () => {
                    await Task.Delay(delay).ConfigureAwait(false);
                    if (!disposed) {
                        Write(Latin1.GetBytes(new string(' ', 32) + "#"));
                    }
                });
                return "1Updating Planetary Data#";
            }
            if (b.StartsWith("Sh", StringComparison.Ordinal)) {
                if (!int.TryParse(b.AsSpan(2), NumberStyles.None, Inv, out var lo)) {
                    return "0";
                }
                lowLimit = lo;
                return "1";
            }
            if (b.StartsWith("So", StringComparison.Ordinal)) {
                if (!int.TryParse(b.AsSpan(2).TrimEnd('*'), NumberStyles.None, Inv, out var hi)) {
                    return "0";
                }
                highLimit = hi;
                return "1";
            }
            if (b.StartsWith("hI", StringComparison.Ordinal)) {
                return "0";   // only accepted at the startup prompt, which the simulator never shows
            }
            if (b.StartsWith("SB", StringComparison.Ordinal)) {
                garbled = true;   // the host keeps talking 9600, the mount does not
                return "1";
            }
            return null;   // unknown or unmodelled: the Autostar ignores it
        }

        // =============================================================================================
        // Model
        // =============================================================================================

        private T Locked<T>(Func<DateTime, T> f) {
            lock (state) {
                var now = o.UtcNow();
                Advance(now);
                return f(now);
            }
        }

        private double EastLongitude => Lx200Format.NormalizeLongitude(-(longitudeWestArcmin / 60.0));

        private double Latitude => latitudeArcmin / 60.0;

        private DateTime MountUtcAt(DateTime real) => real + clockOffset;

        private DateTime LocalOf(DateTime mountUtc) => mountUtc - TimeSpan.FromHours(hoursToUtc);

        private double Lst(DateTime real) => Lx200Astro.LstHours(MountUtcAt(real), EastLongitude);

        private (double RaHours, double DecDeg) RaDec(DateTime now) => Lx200Astro.ToRaDec(axisAlt, axisAz, Lst(now), Latitude);

        private void SetAxesTo(double ra, double dec, DateTime now) {
            (axisAlt, axisAz) = Lx200Astro.ToAltAz(ra, dec, Lst(now), Latitude);
        }

        private DateTime MountDateForGc(DateTime now) {
            var utc = MountUtcAt(now);
            return o.ScDate == DateConvention.Utc && o.GcFollowsScConvention ? utc.Date : LocalOf(utc).Date;
        }

        private void SetLocalTime(TimeSpan timeOfDay, DateTime now) {
            var utc = MountUtcAt(now);
            DateTime newUtc;
            if (o.ScDate == DateConvention.Local) {
                newUtc = LocalOf(utc).Date + timeOfDay + TimeSpan.FromHours(hoursToUtc);
            } else {
                // UTC-date firmware: keep the stored UTC date, set the UTC time of day from local time + offset
                var utcTod = TimeSpan.FromHours(Lx200Astro.Wrap((timeOfDay + TimeSpan.FromHours(hoursToUtc)).TotalHours, 24));
                newUtc = utc.Date + utcTod;
            }
            clockOffset = newUtc - now;
        }

        private void SetDate(DateTime date, DateTime now) {
            var utc = MountUtcAt(now);
            DateTime newUtc = o.ScDate == DateConvention.Local
                ? date.Date + LocalOf(utc).TimeOfDay + TimeSpan.FromHours(hoursToUtc)
                : date.Date + utc.TimeOfDay;
            clockOffset = newUtc - now;
        }

        private string StartGoto(DateTime now) {
            var (alt, _) = Lx200Astro.ToAltAz(targetRa, targetDec, Lst(now), Latitude);
            if (alt < lowLimit) {
                return "1Object Below Horizon#";
            }
            if (alt > highLimit) {
                return "2Object Above Higher#";
            }
            slew = new Slew(now, o.SlewSeconds, axisAlt, axisAz, targetRa, targetDec, null, null, hpPointing);
            return "0";
        }

        private void StartMove(string tag, char direction, DateTime? end, double degPerSec, DateTime now, bool manual) {
            var (axis, sign) = MapDirection(direction, manual ? PulseGuideBehaviour.AltAzAxes : o.PulseGuide);
            motions.Add(new Motion(tag, axis, sign * degPerSec, now, end));
        }

        private void StartPulse(char direction, int ms, DateTime now) {
            if (o.PulseGuide == PulseGuideBehaviour.Ignored || "nsew".IndexOf(direction) < 0) {
                return;
            }
            var (axis, _) = MapDirection(direction, o.PulseGuide);
            var others = motions.Where(m => m.Tag.StartsWith("Mg", StringComparison.Ordinal) && m.Axis != axis && (m.End ?? DateTime.MaxValue) > now).ToList();
            var start = now;
            if (others.Count > 0) {
                switch (o.TwoAxisPulse) {
                    case TwoAxisPulseBehaviour.SecondDropped:
                        return;
                    case TwoAxisPulseBehaviour.Queued:
                        start = others.Max(m => m.End ?? now);
                        break;
                }
            }
            motions.RemoveAll(m => m.Tag.StartsWith("Mg", StringComparison.Ordinal) && m.Axis == axis);
            var rate = guideRateArcsecPerSec / 3600.0;
            var (_, sign) = MapDirection(direction, o.PulseGuide);
            motions.Add(new Motion("Mg" + direction, axis, sign * rate, start, start.AddMilliseconds(ms)));
        }

        private static (Axis Axis, int Sign) MapDirection(char direction, PulseGuideBehaviour mapping) {
            var eq = mapping == PulseGuideBehaviour.EquatorialAxes;
            return direction switch {
                'n' => (eq ? Axis.Dec : Axis.Alt, +1),
                's' => (eq ? Axis.Dec : Axis.Alt, -1),
                'e' => (eq ? Axis.Ra : Axis.Az, +1),
                'w' => (eq ? Axis.Ra : Axis.Az, -1),
                _ => throw new ArgumentException($"direction {direction}")
            };
        }

        private void StartFocus(int direction, DateTime? end, DateTime now) {
            if (direction != lastFocusDir && lastFocusDir != 0) {
                focusBacklashLeftMs = o.FocusBacklashMs;
            }
            lastFocusDir = direction;
            focusMove = new FocusMove(direction, now, end);
        }

        /// <summary>Integrates tracking, slews, moves and the focuser from the last update to <paramref name="now"/>.</summary>
        private void Advance(DateTime now) {
            var t0 = lastAdvance;
            if (now <= t0) {
                return;
            }
            if (tracking && slew == null) {
                var (ra, dec) = Lx200Astro.ToRaDec(axisAlt, axisAz, Lst(t0), Latitude);
                (axisAlt, axisAz) = Lx200Astro.ToAltAz(ra, dec, Lst(now), Latitude);
            }
            if (slew != null) {
                var fraction = Math.Clamp((now - slew.Start).TotalSeconds / Math.Max(0.001, slew.Seconds), 0, 1);
                double toAlt, toAz;
                if (slew.ToRa.HasValue) {
                    (toAlt, toAz) = Lx200Astro.ToAltAz(slew.ToRa.Value, slew.ToDec.Value, Lst(now), Latitude);
                } else {
                    (toAlt, toAz) = (slew.ToAlt.Value, slew.ToAz.Value);
                }
                axisAlt = slew.FromAlt + ((toAlt - slew.FromAlt) * fraction);
                axisAz = Lx200Astro.Wrap(slew.FromAz + (Lx200Astro.DegreeDifference(toAz, slew.FromAz) * fraction), 360);
                if (fraction >= 1 && !slew.Hang) {
                    slew = null;
                }
            }
            double dAlt = 0, dAz = 0, dRa = 0, dDec = 0;
            foreach (var m in motions) {
                var from = m.Start > t0 ? m.Start : t0;
                var to = m.End.HasValue && m.End.Value < now ? m.End.Value : now;
                if (to <= from) {
                    continue;
                }
                var d = m.DegPerSec * (to - from).TotalSeconds;
                switch (m.Axis) {
                    case Axis.Alt: dAlt += d; break;
                    case Axis.Az: dAz += d; break;
                    case Axis.Ra: dRa += d; break;
                    case Axis.Dec: dDec += d; break;
                }
            }
            motions.RemoveAll(m => m.End.HasValue && m.End.Value <= now);
            axisAlt = Math.Clamp(axisAlt + dAlt, -10, 90);
            axisAz = Lx200Astro.Wrap(axisAz + dAz, 360);
            if (dRa != 0 || dDec != 0) {
                var (ra, dec) = RaDec(now);
                SetAxesTo(Lx200Astro.Wrap(ra + (dRa / 15.0), 24), Math.Clamp(dec + dDec, -90, 90), now);
            }
            if (focusMove != null) {
                var from = focusMove.Start > t0 ? focusMove.Start : t0;
                var to = focusMove.End.HasValue && focusMove.End.Value < now ? focusMove.End.Value : now;
                if (to > from) {
                    var ms = (to - from).TotalMilliseconds;
                    var eaten = Math.Min(ms, focusBacklashLeftMs);
                    focusBacklashLeftMs -= eaten;
                    ms -= eaten;
                    focusUm = Math.Clamp(focusUm + (focusMove.Direction * o.FocusSpeedsUmPerSec[focusSpeed - 1] * ms / 1000.0), 0, o.FocusTravelUm);
                }
                if (focusMove.End.HasValue && focusMove.End.Value <= now) {
                    focusMove = null;
                }
            }
            lastAdvance = now;
        }

        // =============================================================================================
        // Reply formatting (independent of NINA.Mac.Lx200.Lx200Format on purpose)
        // =============================================================================================

        private char Deg => (char)o.DegreeByte;

        private string FormatRa(double hours) {
            if (longFormat) {
                var s = Mod((long)Math.Round(hours * 3600), 86400);
                return string.Create(Inv, $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}");
            }
            var t = Mod((long)Math.Round(hours * 600), 14400);
            return string.Create(Inv, $"{t / 600:00}:{t / 10 % 60:00}.{t % 10}");
        }

        private static string FormatHms(double hours) {
            var s = Mod((long)Math.Floor(hours * 3600 + 1e-6), 86400);
            return string.Create(Inv, $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}");
        }

        private string FormatSigned(double degrees) {
            var neg = degrees < 0;
            var abs = Math.Abs(degrees);
            if (longFormat) {
                var s = (long)Math.Round(abs * 3600);
                return string.Create(Inv, $"{(neg && s > 0 ? '-' : '+')}{s / 3600:00}{Deg}{s / 60 % 60:00}{o.SecondsSeparator}{s % 60:00}");
            }
            var m = (long)Math.Round(abs * 60);
            return string.Create(Inv, $"{(neg && m > 0 ? '-' : '+')}{m / 60:00}{Deg}{m % 60:00}");
        }

        private string FormatAzimuth(double degrees) {
            if (longFormat) {
                var s = Mod((long)Math.Round(degrees * 3600), 1296000);
                return string.Create(Inv, $"{s / 3600:000}{Deg}{s / 60 % 60:00}{o.SecondsSeparator}{s % 60:00}");
            }
            var m = Mod((long)Math.Round(degrees * 60), 21600);
            return string.Create(Inv, $"{m / 60:000}{Deg}{m % 60:00}");
        }

        private string FormatSiteAngle(int arcmin, int digits, bool signed) {
            var abs = Math.Abs(arcmin);
            var d = (abs / 60).ToString(new string('0', digits), Inv);
            return string.Create(Inv, $"{(signed ? (arcmin < 0 ? "-" : "+") : "")}{d}{Deg}{abs % 60:00}");
        }

        private string FormatLongitude() {
            if (o.LongitudeFormat == LongitudeReadback.West360) {
                return FormatSiteAngle(longitudeWestArcmin, 3, signed: false);
            }
            // P07: East negative. West-positive arcmin in (-180, 180]
            var west = longitudeWestArcmin > 10800 ? longitudeWestArcmin - 21600 : longitudeWestArcmin;
            return FormatSiteAngle(west, 3, signed: true);
        }

        private string FormatHoursToUtc() {
            var whole = Math.Abs(hoursToUtc - Math.Round(hoursToUtc)) < 1e-9;
            var sign = hoursToUtc < 0 ? "-" : "+";
            return whole
                ? string.Create(Inv, $"{sign}{Math.Abs(Math.Round(hoursToUtc)):00}")
                : string.Create(Inv, $"{sign}{Math.Abs(hoursToUtc):00.0}");
        }

        private static bool TryHms(string s, out double hours) {
            hours = 0;
            var m = HmsRegex.Match(s);
            if (!m.Success) {
                return false;
            }
            int h = int.Parse(m.Groups[1].Value, Inv), min = int.Parse(m.Groups[2].Value, Inv);
            var sec = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, Inv) : int.Parse(m.Groups[4].Value, Inv) * 6;
            if (h > 23 || min > 59 || sec > 59) {
                return false;
            }
            hours = h + (min / 60.0) + (sec / 3600.0);
            return true;
        }

        private static bool TryDms(string s, out double degrees) {
            degrees = 0;
            var m = DmsRegex.Match(s);
            if (!m.Success) {
                return false;
            }
            int d = int.Parse(m.Groups[2].Value, Inv), min = int.Parse(m.Groups[3].Value, Inv);
            var sec = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, Inv) : 0;
            if (min > 59 || sec > 59) {
                return false;
            }
            degrees = d + (min / 60.0) + (sec / 3600.0);
            if (m.Groups[1].Value == "-") {
                degrees = -degrees;
            }
            return true;
        }

        private static long Mod(long v, long m) => ((v % m) + m) % m;

        private static string Accept(bool valid, double value, Action<double> set) {
            if (!valid) {
                return "0";
            }
            set(value);
            return "1";
        }

        private enum Axis {
            Alt,
            Az,
            Ra,
            Dec
        }

        private sealed record Motion(string Tag, Axis Axis, double DegPerSec, DateTime Start, DateTime? End);

        private sealed record Slew(DateTime Start, double Seconds, double FromAlt, double FromAz, double? ToRa, double? ToDec, double? ToAlt, double? ToAz, bool Hang);

        private sealed record FocusMove(int Direction, DateTime Start, DateTime? End);
    }
}
