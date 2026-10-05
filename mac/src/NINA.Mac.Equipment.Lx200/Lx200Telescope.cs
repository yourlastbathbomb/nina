#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using NINA.Mac.Lx200;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200 {

    /// <summary>
    /// NINA's <see cref="ITelescope"/> for a Meade LX200GPS (Autostar II) on an alt-az fork, no wedge, over the mount's RS-232
    /// port. Design: research/rig_investigate_mount.md Table 2 as corrected by rig_verify_mount.md, plan section 6.
    /// <list type="bullet">
    /// <item>Every property comes from a cache that a background poll refreshes through the link's Poll lane (":GR#"/":GD#"
    /// every cycle, ":GA#"/":GZ#" every second cycle, ":GW#" every fifth, ":GS#" every thirtieth), so NINA's 2-second
    /// property polling never waits on the serial line.</item>
    /// <item>Gotos and syncs take NINA <see cref="Coordinates"/> in any epoch and send JNow from NINA's own transform
    /// (<see cref="Coordinates.Transform(Epoch)"/>, SOFA), in the mount's high-precision format (":U#" at connect).</item>
    /// <item>Alt-az fork: side of pier unknown, no meridian flip (<see cref="TimeToMeridianFlip"/> is NaN, so NINA's flip
    /// trigger stands down), no home. Park is a soft park: halt, optionally an alt-az goto to a stored position, then tracking
    /// off. The Autostar's own park command is blocklisted: after it the Autostar answers nothing until power-cycled.</item>
    /// <item>The mount keeps its site to 1'; within <see cref="Lx200Settings.SiteToleranceArcmin"/> the profile's site is
    /// reported, so NINA's 0.001° site check does not prompt on every connect (RIM MNT-14).</item>
    /// <item>Time and date are written only to a mount that ":GW#" says is not aligned (plan risk 3); an aligned mount's clock
    /// is checked against the Mac (and ":GS#" against the computed sidereal time) and a difference is reported, never fixed.</item>
    /// <item>Pulse guiding (NINA's mount dither) has three strategies (<see cref="Lx200PulseStrategy"/>).</item>
    /// </list>
    /// </summary>
    public sealed class Lx200Telescope : BaseINPC, ITelescope, IDisposable {

        public const string DeviceId = "Nightglass.Lx200GPS.Telescope";

        /// <summary>
        /// Rates for ":RC#", ":RM#" and ":RS#" in deg/s. P07 names them only "centering (2nd slowest)", "find (2nd fastest)" and
        /// "max"; mapped here to the handbox speeds 4 (16x), 7 (1.5°/s) and 9 (8°/s) of the LX200GPS manual's table (p. 17), as
        /// the simulator assumes. To be measured at the bench.
        /// </summary>
        internal const double CentreRateDegPerSec = 0.067;

        internal const double FindRateDegPerSec = 1.5;

        internal const double MaxRateDegPerSec = 8.0;

        /// <summary>Sidereal day / solar day: how fast the mount's sidereal clock runs against the Mac's.</summary>
        private const double SiderealPerSolar = 1.00273790935;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly IProfileService profileService;
        private readonly Lx200LinkPool pool;
        private readonly Lx200Clock clock;
        private readonly object stateLock = new();
        private readonly HashSet<TelescopeAxes> movingAxes = new();
        private Lx200Link link;
        private CancellationTokenSource pollCts;
        private Task pollTask;
        private Lx200PulseGuider pulseGuider;
        private bool connected;

        // ---- cached mount state (stateLock) ----
        private CoordinatePrecision precision = CoordinatePrecision.High;
        private string product = string.Empty;
        private string firmware = string.Empty;
        private bool gwSupported;
        private char mode = 'A';
        private bool tracking;
        private char? alignment;
        private double ra = double.NaN;
        private double dec = double.NaN;
        private double alt = double.NaN;
        private double az = double.NaN;
        private double lstHours = double.NaN;
        private DateTime lstReadUtc;
        private double mountLatitude = double.NaN;
        private double mountLongitude = double.NaN;
        private double? requestedLatitude;
        private double? requestedLongitude;
        private double? siteElevation;
        private TimeSpan clockOffset;
        private bool atPark;
        private bool parkStoppedTracking;
        private bool gotoActive;
        private int abortGeneration;
        private (double Ra, double Dec)? reportedTarget;
        private Coordinates targetCoordinates;
        private TrackingMode trackingMode = TrackingMode.Sidereal;
        private double primaryMovingRate = double.NaN;
        private double secondaryMovingRate = double.NaN;
        private Lx200PulseStrategy effectiveStrategy = Lx200PulseStrategy.HostTimedMove;

        public Lx200Telescope(IProfileService profileService, Lx200LinkPool pool = null, Lx200Clock clock = null) {
            this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            this.pool = pool ?? Lx200LinkPool.Shared;
            this.clock = clock ?? Lx200Clock.System;
            Settings = new Lx200Settings(profileService);
        }

        public Lx200Settings Settings { get; }

        /// <summary>The serial link while connected (diagnostics: trace, state, queue lengths).</summary>
        public Lx200Link Link => link;

        public Lx200LinkState LinkState => link?.State ?? Lx200LinkState.Closed;

        /// <summary>":GVP#" and ":GVN#" as read at connect, e.g. "LX2001 4.2g".</summary>
        public string Firmware {
            get {
                lock (stateLock) {
                    return $"{product} {firmware}".Trim();
                }
            }
        }

        /// <summary>From ":GW#"'s third character: true when aligned, false when not ('0'), null when ":GW#" is not answered.</summary>
        public bool? IsAligned {
            get {
                lock (stateLock) {
                    return gwSupported && alignment.HasValue ? alignment.Value != '0' : null;
                }
            }
        }

        /// <summary>The pulse-guide strategy in use (<see cref="Lx200PulseStrategy.Auto"/> resolved at connect).</summary>
        public Lx200PulseStrategy EffectivePulseStrategy {
            get {
                lock (stateLock) {
                    return effectiveStrategy;
                }
            }
        }

        // =============================================================================================
        // IDevice
        // =============================================================================================

        public bool HasSetupDialog => false;

        public string Id => DeviceId;

        public string Name => "Meade LX200GPS";

        public string DisplayName => "Meade LX200GPS (alt-az, serial)";

        public string Category => "Meade";

        public bool Connected {
            get => connected;
            private set {
                if (connected != value) {
                    connected = value;
                    RaisePropertyChanged();
                }
            }
        }

        public string Description => "Native LX200GPS / Autostar II driver for an alt-az fork (no wedge) on the mount's serial port";

        public string DriverInfo => "Nightglass LX200 driver (NINA.Mac.Equipment.Lx200)";

        public string DriverVersion => typeof(Lx200Telescope).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                       ?? typeof(Lx200Telescope).Assembly.GetName().Version?.ToString() ?? "1.0";

        public void SetupDialog() {
        }

        public async Task<bool> Connect(CancellationToken token) {
            if (Connected) {
                return true;
            }
            Lx200Link acquired = null;
            try {
                var port = Lx200Ports.Resolve(Settings.PortPath);
                acquired = await Task.Run(() => pool.Acquire(port, token), token).ConfigureAwait(false);
                link = acquired;
                link.StateChanged += OnLinkStateChanged;
                await InitializeSession(token).ConfigureAwait(false);
                pulseGuider = new Lx200PulseGuider(this, link, Settings) { Strategy = EffectivePulseStrategy };
                StartPolling();
                Connected = true;
                Logger.Info($"LX200 mount connected on {port}: {Firmware}, pulse strategy {EffectivePulseStrategy}, aligned {IsAligned?.ToString() ?? "unknown"}, trace {link.TraceFile ?? "(memory)"}");
                return true;
            } catch (OperationCanceledException) {
                Teardown();
                throw;
            } catch (Exception ex) {
                Logger.Error("LX200 mount: connect failed", ex);
                Notification.ShowError($"LX200: cannot connect the mount: {ex.Message}");
                Teardown();
                return false;
            }
        }

        public void Disconnect() {
            if (!Connected && link == null) {
                return;
            }
            Connected = false;
            Teardown();
            Logger.Info("LX200 mount disconnected");
        }

        public void Dispose() {
            Disconnect();
        }

        /// <summary>Stops polling and releases the link; safe to call twice at once (user disconnect and a failed link).</summary>
        private void Teardown() {
            var cts = Interlocked.Exchange(ref pollCts, null);
            var poll = Interlocked.Exchange(ref pollTask, null);
            try {
                cts?.Cancel();
                poll?.Wait(TimeSpan.FromSeconds(3));
            } catch (Exception) {
                // the poll loop ends with a cancellation
            }
            cts?.Dispose();
            Interlocked.Exchange(ref pulseGuider, null)?.Dispose();
            var l = Interlocked.Exchange(ref link, null);
            if (l != null) {
                l.StateChanged -= OnLinkStateChanged;
                bool moving;
                lock (stateLock) {
                    moving = movingAxes.Count > 0;
                    movingAxes.Clear();
                    gotoActive = false;
                }
                if (moving && l.IsConnected) {
                    TrySend(l, ":Qn#", Lx200Lane.Stop);
                    TrySend(l, ":Qs#", Lx200Lane.Stop);
                    TrySend(l, ":Qe#", Lx200Lane.Stop);
                    TrySend(l, ":Qw#", Lx200Lane.Stop);
                }
                pool.Release(l);
            }
            lock (stateLock) {
                reportedTarget = null;
                targetCoordinates = null;
            }
        }

        /// <summary>A halt through the Stop lane; while the link is down the link owes it and sends it first when the mount is back.</summary>
        private static void SendHalt(Lx200Link l, string command) {
            try {
                l.Send(command, Lx200Lane.Stop);
            } catch (Lx200DisconnectedException) {
                Logger.Warning($"LX200: {command} could not be sent now (link down); it goes out as soon as the mount answers again");
            }
        }

        private static void TrySend(Lx200Link l, string command, Lx200Lane lane) {
            try {
                l.Send(command, lane);
            } catch (Exception ex) {
                Logger.Warning($"LX200: {command} failed: {ex.Message}");
            }
        }

        public IList<string> SupportedActions => new List<string> {
            "Lx200.LinkState", "Lx200.TraceFile", "Lx200.Firmware", "Lx200.PulseStrategy", "Lx200.SetMountClock"
        };

        public string Action(string actionName, string actionParameters) {
            return actionName switch {
                "Lx200.LinkState" => LinkState.ToString(),
                "Lx200.TraceFile" => link?.TraceFile ?? string.Empty,
                "Lx200.Firmware" => Firmware,
                "Lx200.PulseStrategy" => EffectivePulseStrategy.ToString(),
                "Lx200.SetMountClock" => SetMountClock().GetAwaiter().GetResult() ? "written" : "refused",
                _ => throw new NotSupportedException($"Action '{actionName}' is not supported by the LX200 driver")
            };
        }

        /// <summary>
        /// Sends a raw command through the link's Command lane and returns the whole reply. The command may be given with or
        /// without ':' and '#'. Blocklisted commands throw <see cref="Lx200BlockedCommandException"/> and nothing is written.
        /// </summary>
        public string SendCommandString(string command, bool raw = true) {
            var reply = RequireLink().Send(command);
            if (!reply.IsOk) {
                throw new Lx200ReplyException($"{command}: {reply.Status} ({reply.Describe()})", reply);
            }
            return reply.Joined;
        }

        public bool SendCommandBool(string command, bool raw = true) => SendCommandString(command, raw) == "1";

        public void SendCommandBlind(string command, bool raw = true) {
            var reply = RequireLink().Send(command, Lx200Lane.Command, ReplyShape.None);
            if (!reply.IsOk) {
                throw new Lx200ReplyException($"{command}: {reply.Status} ({reply.Describe()})", reply);
            }
        }

        private Lx200Link RequireLink() => link != null && Connected ? link : throw new InvalidOperationException("The LX200 mount is not connected");

        // =============================================================================================
        // Connect sequence
        // =============================================================================================

        private async Task InitializeSession(CancellationToken token) {
            lock (stateLock) {
                gwChecked = false;
                gwSupported = false;
                alignment = null;
                atPark = false;
                parkStoppedTracking = false;
                requestedLatitude = requestedLongitude = null;
                lstHours = double.NaN;
                clockOffset = TimeSpan.Zero;
            }
            // Identity (RVM T1: expect LX2001; firmware decides :GW# support, the StarPatch pulse path and the rollover warning)
            var gvp = await Query(":GVP#", token).ConfigureAwait(false);
            var gvn = await Query(":GVN#", token).ConfigureAwait(false);
            lock (stateLock) {
                product = gvp.Trim();
                firmware = gvn.Trim();
            }
            if (product != "LX2001") {
                Logger.Warning($"LX200: product '{product}' is not the LX200GPS's LX2001; the driver was written for the LX200GPS");
            }
            if (Lx200Firmware.LacksRolloverFix(firmware)) {
                Logger.Warning("LX200: stock firmware 4.2g has no GPS week-rollover fix; the date Automatic Align takes from GPS can be wrong (checked below)");
            }

            await RefreshStatus(token).ConfigureAwait(false);
            await EnsureHighPrecisionFormat(token).ConfigureAwait(false);
            if (Settings.EnsureLowPrecisionPointing) {
                await EnsureLowPrecisionPointing(token).ConfigureAwait(false);
            }

            // site and clock are checks: an odd reply is logged, it does not stop the connect
            try {
                var gt = await Query(":Gt#", token).ConfigureAwait(false);
                var gg = await Query(":Gg#", token).ConfigureAwait(false);
                lock (stateLock) {
                    mountLatitude = Lx200Format.ParseDegrees(gt);
                    mountLongitude = Lx200Format.ParseLongitudeEast(gg);
                }
                var profileSite = profileService.ActiveProfile.AstrometrySettings;
                Logger.Info(string.Create(Inv, $"LX200 site: mount {mountLatitude:0.0000} {mountLongitude:0.0000} (1' resolution), profile {profileSite.Latitude:0.0000} {profileSite.Longitude:0.0000}"));
            } catch (Exception ex) when (ex is FormatException or Lx200ReplyException) {
                Logger.Warning($"LX200: could not read the mount's site: {ex.Message}");
            }
            try {
                await CheckClock(profileService.ActiveProfile.TelescopeSettings.TimeSync, token).ConfigureAwait(false);
            } catch (Exception ex) when (ex is FormatException or Lx200ReplyException) {
                Logger.Warning($"LX200: could not check the mount's clock: {ex.Message}");
                Notification.ShowWarning($"LX200: could not check the mount's clock ({ex.Message}); check time, date and site on the handbox.");
            }

            if (Settings.SetGuideRateOnConnect) {
                var rg = await link.SendAsync(Lx200Format.GuideRateCommand(Settings.GuideRateArcsecPerSec), Lx200Lane.Command, token).ConfigureAwait(false);
                if (!rg.IsOk) {
                    Logger.Warning($"LX200: guide rate not set ({rg.Describe()})");
                }
            }

            var strategy = Settings.PulseStrategy;
            if (strategy == Lx200PulseStrategy.Auto) {
                strategy = Lx200Firmware.IsStarPatch(firmware) ? Lx200PulseStrategy.NativePulse : Lx200PulseStrategy.HostTimedMove;
            }
            lock (stateLock) {
                effectiveStrategy = strategy;
            }
            await RefreshPosition(includeAltAz: true, token).ConfigureAwait(false);
        }

        /// <summary>":GW#" (mount, tracking, alignment); when it is not answered, ACK gives the mode and tracking (RVM T2).</summary>
        private async Task RefreshStatus(CancellationToken token, Lx200Lane lane = Lx200Lane.Command) {
            bool useGw;
            lock (stateLock) {
                useGw = !gwChecked || gwSupported;
            }
            if (useGw) {
                var gw = await link.SendAsync(":GW#", lane, token, coalesceKey: lane == Lx200Lane.Poll ? ":GW#" : null).ConfigureAwait(false);
                var parsed = gw.IsOk ? Lx200Firmware.ParseGw(gw.Value) : null;
                lock (stateLock) {
                    if (!gwChecked) {
                        gwChecked = true;
                        gwSupported = parsed != null;
                        if (!gwSupported) {
                            Logger.Warning($"LX200: ':GW#' is not answered ({gw.Status}); tracking comes from ACK and the alignment state is unknown, so the mount's clock is never written");
                        }
                    }
                    if (parsed is { } p) {
                        mode = p.Mode;
                        tracking = p.Tracking;
                        alignment = p.Alignment;
                        return;
                    }
                }
                if (gwSupported) {
                    return;   // a lost poll; keep the last state
                }
            }
            var ack = await link.SendAsync("ack", lane, token, coalesceKey: lane == Lx200Lane.Poll ? "ACK" : null).ConfigureAwait(false);
            if (ack.IsOk && ack.Value.Length == 1) {
                lock (stateLock) {
                    mode = ack.Value[0];
                    tracking = mode != 'L';
                }
            }
        }

        private bool gwChecked;

        private async Task EnsureHighPrecisionFormat(CancellationToken token) {
            var gr = await Query(":GR#", token).ConfigureAwait(false);
            var p = Lx200Format.DetectPrecision(gr);
            if (p == CoordinatePrecision.Low) {
                await link.SendAsync(":U#", Lx200Lane.Command, token).ConfigureAwait(false);
                gr = await Query(":GR#", token).ConfigureAwait(false);
                p = Lx200Format.DetectPrecision(gr);
                if (p == CoordinatePrecision.Low) {
                    Logger.Warning("LX200: ':U#' did not switch to high precision; positions are 0.1 min of RA and 1' of Dec");
                }
            }
            lock (stateLock) {
                precision = p;
            }
        }

        private async Task EnsureLowPrecisionPointing(CancellationToken token) {
            // ":P#" toggles High Precision POINTING and answers the new state; it must end LOW (RIM MNT-09)
            var first = await link.SendAsync(":P#", Lx200Lane.Command, token).ConfigureAwait(false);
            if (!first.IsOk) {
                Logger.Warning($"LX200: ':P#' answered {first.Describe()}; set High Precision OFF on the handbox");
                return;
            }
            if (first.Value.StartsWith("LOW", StringComparison.Ordinal)) {
                Logger.Warning("LX200: High Precision pointing was ON (every goto would wait for ENTER); switched it OFF");
                return;
            }
            var second = await link.SendAsync(":P#", Lx200Lane.Command, token).ConfigureAwait(false);
            if (!second.IsOk || !second.Value.StartsWith("LOW", StringComparison.Ordinal)) {
                Logger.Error($"LX200: High Precision pointing may be ON ({second.Describe()}); gotos would wait for ENTER");
                Notification.ShowError("LX200: could not switch High Precision pointing OFF; set it OFF on the handbox (Setup > Telescope > High Precision).");
            }
        }

        // =============================================================================================
        // Time
        // =============================================================================================

        /// <summary>
        /// Compares the mount's clock (":GL#" + ":GG#", date by ":GC#") with the Mac's, and ":GS#" with the sidereal time computed
        /// for the mount's longitude, which also catches a wrong date (3m56s per day) or longitude. A difference over 10 s (NINA's
        /// own threshold) is corrected only if the profile asks for time sync and ":GW#" says the mount is not aligned.
        /// </summary>
        private async Task CheckClock(bool timeSync, CancellationToken token) {
            var gg = await Query(":GG#", token).ConfigureAwait(false);
            var gl = await Query(":GL#", token).ConfigureAwait(false);
            var gc = await Query(":GC#", token).ConfigureAwait(false);
            var gs = await Query(":GS#", token).ConfigureAwait(false);
            var macUtc = clock.UtcNow();
            var hoursToUtc = Lx200Format.ParseHoursToUtc(gg);
            var mountUtc = Lx200Format.ParseDate(gc) + Lx200Format.ParseTime(gl) + TimeSpan.FromHours(hoursToUtc);
            // ":GC#" may hold the local or the UTC date (RVM MNT-M7); take the day that puts the clock nearest the Mac's
            var best = new[] { -1, 0, 1 }.Select(d => mountUtc.AddDays(d)).OrderBy(t => Math.Abs((t - macUtc).TotalSeconds)).First();
            var offset = best - macUtc;
            var mountLst = Lx200Format.ParseTime(gs).TotalHours;
            double lon;
            lock (stateLock) {
                clockOffset = offset;
                lstHours = mountLst;
                lstReadUtc = clock.UtcNow();
                lon = double.IsNaN(mountLongitude) ? profileService.ActiveProfile.AstrometrySettings.Longitude : mountLongitude;
            }
            var lstError = Lx200Astro.HourDifference(mountLst, Lx200Astro.LstHours(macUtc, lon)) * 3600.0;
            Logger.Info(string.Create(Inv, $"LX200 clock: mount {best:yyyy-MM-dd HH:mm:ss} UTC (offset {-hoursToUtc:+0.#;-0.#} h), Mac {macUtc:yyyy-MM-dd HH:mm:ss} UTC, difference {offset.TotalSeconds:+0.0;-0.0} s; :GS# {gs} is {lstError:+0.0;-0.0} s from the computed sidereal time"));
            if (Math.Abs(offset.TotalSeconds) <= 10 && Math.Abs(lstError) <= 10) {
                return;
            }
            var aligned = IsAligned;
            if (timeSync && aligned == false) {
                if (await SetMountClock(macUtc, token).ConfigureAwait(false)) {
                    return;
                }
            }
            var why = !timeSync
                ? "time sync is off in the profile"
                : aligned == true
                    ? "the mount is aligned, and writing the time after alignment shifts every goto"
                    : aligned == null ? "':GW#' does not say whether the mount is aligned" : "see the log";
            var message = string.Create(Inv, $"The mount's clock is {offset.TotalSeconds:+0;-0} s from the Mac's and its sidereal time {lstError:+0;-0} s from the computed one (time, date or longitude). Not corrected: {why}. Set time, date and site on the handbox before aligning.");
            Logger.Warning("LX200: " + message);
            Notification.ShowWarning("LX200: " + message);
        }

        /// <summary>
        /// Writes the Mac's time (":SG" offset, ":SL" local time, ":SC" date) to a mount that is NOT aligned. Refused (false) when
        /// ":GW#" says aligned or cannot tell (plan risk 3: time written after alignment shifts every goto), and when the ":SC"
        /// date convention is not settled while the UTC and local dates differ (00:00-08:00 in Hong Kong, RVM MNT-M7).
        /// </summary>
        public async Task<bool> SetMountClock(DateTime? utc = null, CancellationToken token = default) {
            if (link == null) {
                return false;
            }
            var aligned = IsAligned;
            if (aligned != false) {
                var why = aligned == true ? "the mount is aligned" : "':GW#' does not say whether the mount is aligned";
                Logger.Warning($"LX200: mount clock not written: {why} (time and date are written only before alignment)");
                return false;
            }
            var now = utc ?? clock.UtcNow();
            var offset = clock.UtcOffset(now);
            var local = now + offset;
            DateTime? date = Settings.DateConvention switch {
                Lx200DateConvention.Utc => now.Date,
                Lx200DateConvention.Local => local.Date,
                _ => now.Date == local.Date ? now.Date : null
            };
            if (date == null) {
                Logger.Warning(string.Create(Inv, $"LX200: mount clock not written: the UTC date {now:yyyy-MM-dd} and the local date {local:yyyy-MM-dd} differ, and the ':SC' date convention is not settled (bench step 4)"));
                return false;
            }
            var hours = offset.TotalHours;
            var whole = Math.Abs(hours - Math.Round(hours)) < 1e-9;
            var sg = await link.SendAsync($":SG{Lx200Format.FormatHoursToUtc(hours, withDecimal: !whole)}#", Lx200Lane.Command, token).ConfigureAwait(false);
            var sl = await link.SendAsync($":SL{Lx200Format.FormatTime((clock.UtcNow() + offset).TimeOfDay)}#", Lx200Lane.Command, token).ConfigureAwait(false);
            var sc = await link.SendAsync($":SC{Lx200Format.FormatDate(date.Value)}#", Lx200Lane.Command, token).ConfigureAwait(false);
            if (sg.Value != "1" || sl.Value != "1" || sc.Value != "1") {
                Logger.Error($"LX200: writing the mount clock failed: {sg.Describe()} / {sl.Describe()} / {sc.Describe()}");
                return false;
            }
            var gs = await Query(":GS#", token).ConfigureAwait(false);
            double lon;
            lock (stateLock) {
                clockOffset = TimeSpan.Zero;
                lstHours = Lx200Format.ParseTime(gs).TotalHours;
                lstReadUtc = clock.UtcNow();
                lon = double.IsNaN(mountLongitude) ? profileService.ActiveProfile.AstrometrySettings.Longitude : mountLongitude;
            }
            var lstError = Lx200Astro.HourDifference(lstHours, Lx200Astro.LstHours(clock.UtcNow(), lon)) * 3600.0;
            Logger.Info(string.Create(Inv, $"LX200: mount clock written ({Settings.DateConvention} date {date:yyyy-MM-dd}); :GS# now {lstError:+0.0;-0.0} s from the computed sidereal time"));
            return true;
        }

        // =============================================================================================
        // Polling
        // =============================================================================================

        private void StartPolling() {
            pollCts = new CancellationTokenSource();
            var token = pollCts.Token;
            pollTask = Task.Run(() => PollLoop(token));
        }

        private async Task PollLoop(CancellationToken token) {
            var cycle = 0;
            while (!token.IsCancellationRequested) {
                var l = link;
                if (l != null && l.IsConnected) {
                    try {
                        await PollOnce(cycle++, token).ConfigureAwait(false);
                    } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                        break;
                    } catch (Exception ex) {
                        Logger.Trace($"LX200 poll: {ex.Message}");
                    }
                }
                try {
                    await Task.Delay(Settings.PollIntervalMs, token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    break;
                }
            }
        }

        private async Task PollOnce(int cycle, CancellationToken token) {
            var position = RefreshPosition(includeAltAz: cycle % 2 == 0, token, Lx200Lane.Poll);
            Task status = cycle % 5 == 4 ? RefreshStatus(token, Lx200Lane.Poll) : Task.CompletedTask;
            Task lst = cycle % 30 == 15 ? RefreshSiderealTime(token) : Task.CompletedTask;
            await Task.WhenAll(position, status, lst).ConfigureAwait(false);
        }

        private async Task RefreshSiderealTime(CancellationToken token) {
            var gs = await link.SendAsync(":GS#", Lx200Lane.Poll, token, coalesceKey: ":GS#").ConfigureAwait(false);
            if (gs.IsOk && TryParse(gs.Value, Lx200Format.ParseTime, out var t)) {
                lock (stateLock) {
                    lstHours = t.TotalHours;
                    lstReadUtc = clock.UtcNow();
                }
            }
        }

        /// <summary>Reads ":GR#"/":GD#" (and ":GA#"/":GZ#") into the cache; drops the reported target once the mount has moved off it.</summary>
        private async Task RefreshPosition(bool includeAltAz, CancellationToken token, Lx200Lane lane = Lx200Lane.Command) {
            var l = link ?? throw new Lx200DisconnectedException("not connected");
            string Key(string c) => lane == Lx200Lane.Poll ? c : null;
            var gr = l.SendAsync(":GR#", lane, token, coalesceKey: Key(":GR#"));
            var gd = l.SendAsync(":GD#", lane, token, coalesceKey: Key(":GD#"));
            Task<Lx200Reply> ga = null, gz = null;
            if (includeAltAz) {
                ga = l.SendAsync(":GA#", lane, token, coalesceKey: Key(":GA#"));
                gz = l.SendAsync(":GZ#", lane, token, coalesceKey: Key(":GZ#"));
            }
            // await them together, so a failed one does not leave the others unobserved
            await Task.WhenAll(new[] { gr, gd, ga, gz }.Where(t => t != null)).ConfigureAwait(false);
            var r = gr.Result;
            var d = gd.Result;
            var a = ga?.Result;
            var z = gz?.Result;
            lock (stateLock) {
                if (r.IsOk && d.IsOk && TryParse(r.Value, Lx200Format.ParseRaHours, out var raValue) && TryParse(d.Value, Lx200Format.ParseDegrees, out var decValue)) {
                    ra = raValue;
                    dec = decValue;
                    if (reportedTarget is { } target && !WithinResolution(raValue, decValue, target.Ra, target.Dec)) {
                        reportedTarget = null;
                    }
                }
                if (a != null && a.IsOk && TryParse(a.Value, Lx200Format.ParseDegrees, out var altValue)) {
                    alt = altValue;
                }
                if (z != null && z.IsOk && TryParse(z.Value, Lx200Format.ParseDegrees, out var azValue)) {
                    az = azValue;
                }
            }
        }

        /// <summary>True when the mount's reply could be the rounding of the target: 0.5 s of RA and 0.5" of Dec in high precision.</summary>
        private bool WithinResolution(double raReply, double decReply, double raTarget, double decTarget) {
            var high = precision == CoordinatePrecision.High;
            var raTol = high ? 0.5 + 1e-6 : 3.0 + 1e-6;      // seconds of time (low: HH:MM.T = 6 s steps)
            var decTol = high ? 0.5 + 1e-6 : 30.0 + 1e-6;    // arcseconds (low: 1' steps)
            return Math.Abs(Lx200Astro.HourDifference(raReply, raTarget)) * 3600.0 <= raTol
                && Math.Abs(decReply - decTarget) * 3600.0 <= decTol;
        }

        private static bool TryParse<T>(string text, Func<string, T> parse, out T value) {
            try {
                value = parse(text);
                return true;
            } catch (FormatException) {
                value = default;
                return false;
            }
        }

        private async Task<string> Query(string command, CancellationToken token) {
            var reply = await link.SendAsync(command, Lx200Lane.Command, token).ConfigureAwait(false);
            if (!reply.IsOk) {
                throw new Lx200ReplyException($"{command}: {reply.Status} ({reply.Describe()})", reply);
            }
            return reply.Value;
        }

        // =============================================================================================
        // Position, time, site
        // =============================================================================================

        public Coordinates Coordinates => new Coordinates(Angle.ByHours(RightAscension), Angle.ByDegree(Declination), Epoch.JNOW);

        public double RightAscension {
            get {
                lock (stateLock) {
                    return reportedTarget?.Ra ?? ra;
                }
            }
        }

        public string RightAscensionString => double.IsNaN(RightAscension) ? string.Empty : AstroUtil.HoursToHMS(RightAscension);

        public double Declination {
            get {
                lock (stateLock) {
                    return reportedTarget?.Dec ?? dec;
                }
            }
        }

        public string DeclinationString => double.IsNaN(Declination) ? string.Empty : AstroUtil.DegreesToDMS(Declination);

        /// <summary>The mount's own sidereal time (":GS#", run on between reads), so hour angles agree with the mount's RA.</summary>
        public double SiderealTime {
            get {
                lock (stateLock) {
                    if (!double.IsNaN(lstHours)) {
                        var hours = (clock.UtcNow() - lstReadUtc).TotalHours * SiderealPerSolar;
                        return Lx200Astro.Wrap(lstHours + hours, 24.0);
                    }
                }
                return Lx200Astro.LstHours(clock.UtcNow(), profileService.ActiveProfile.AstrometrySettings.Longitude);
            }
        }

        public string SiderealTimeString => AstroUtil.HoursToHMS(SiderealTime);

        public double Altitude {
            get {
                lock (stateLock) {
                    return alt;
                }
            }
        }

        public string AltitudeString => double.IsNaN(Altitude) ? string.Empty : AstroUtil.DegreesToDMS(Altitude);

        public double Azimuth {
            get {
                lock (stateLock) {
                    return az;
                }
            }
        }

        public string AzimuthString => double.IsNaN(Azimuth) ? string.Empty : AstroUtil.DegreesToDMS(Azimuth);

        public double HoursToMeridian {
            get {
                if (double.IsNaN(RightAscension) || !TrackingEnabled) {
                    return 24;
                }
                return NINA.Astrometry.MeridianFlip.TimeToMeridian(Coordinates, Angle.ByHours(SiderealTime)).TotalHours;
            }
        }

        public string HoursToMeridianString => AstroUtil.HoursToHMS(HoursToMeridian);

        /// <summary>NaN: an alt-az fork never flips, and NINA's MeridianFlipTrigger stands down on NaN (MeridianFlipTrigger.cs:183-191).</summary>
        public double TimeToMeridianFlip => double.NaN;

        public string TimeToMeridianFlipString => string.Empty;

        public PierSide SideOfPier => PierSide.pierUnknown;

        public PierSide DestinationSideOfPier(Coordinates coordinates) => PierSide.pierUnknown;

        public PierSide? TargetSideOfPier => null;

        public bool CanSetPierSide => false;

        public Task<bool> MeridianFlip(Coordinates targetCoordinates, CancellationToken token) {
            Logger.Info("LX200: alt-az fork mount, no meridian flip; nothing to do");
            return Task.FromResult(true);
        }

        public double SiteLatitude {
            get {
                lock (stateLock) {
                    return SiteValue(mountLatitude, profileService.ActiveProfile.AstrometrySettings.Latitude, requestedLatitude);
                }
            }
            set => WriteSite(latitude: true, value);
        }

        public double SiteLongitude {
            get {
                lock (stateLock) {
                    return SiteValue(mountLongitude, profileService.ActiveProfile.AstrometrySettings.Longitude, requestedLongitude);
                }
            }
            set => WriteSite(latitude: false, value);
        }

        /// <summary>The mount stores no elevation: the profile's, or the last value NINA set (RIM T2).</summary>
        public double SiteElevation {
            get {
                lock (stateLock) {
                    return siteElevation ?? profileService.ActiveProfile.AstrometrySettings.Elevation;
                }
            }
            set {
                lock (stateLock) {
                    siteElevation = value;
                }
            }
        }

        /// <summary>
        /// The value last written (NINA's TOTELESCOPE site sync writes the profile's), or else the profile's, when the mount's
        /// 1'-resolution value is within the tolerance of it; otherwise the mount's own value.
        /// </summary>
        private double SiteValue(double mountValue, double profileValue, double? requested) {
            if (double.IsNaN(mountValue)) {
                return profileValue;
            }
            var tolerance = Settings.SiteToleranceArcmin / 60.0 + 1e-9;
            if (requested.HasValue && Math.Abs(Lx200Astro.DegreeDifference(mountValue, requested.Value)) <= tolerance) {
                return requested.Value;
            }
            if (Math.Abs(Lx200Astro.DegreeDifference(mountValue, profileValue)) <= tolerance) {
                return profileValue;
            }
            return mountValue;
        }

        private void WriteSite(bool latitude, double value) {
            var l = link;
            if (l == null || !Connected || double.IsNaN(value)) {
                return;
            }
            if (IsAligned != false) {
                Logger.Warning("LX200: changing the site of an aligned mount shifts its gotos; re-align afterwards");
            }
            var command = latitude ? $":St{Lx200Format.FormatLatitude(value)}#" : $":Sg{Lx200Format.FormatLongitudeWest360(value)}#";
            try {
                var reply = l.Send(command);
                if (reply.Value != "1") {
                    Logger.Error($"LX200: site not accepted: {reply.Describe()}");
                    return;
                }
                var back = l.Send(latitude ? ":Gt#" : ":Gg#");
                lock (stateLock) {
                    if (latitude) {
                        requestedLatitude = value;
                        if (back.IsOk && TryParse(back.Value, Lx200Format.ParseDegrees, out var v)) {
                            mountLatitude = v;
                        }
                    } else {
                        requestedLongitude = value;
                        if (back.IsOk && TryParse(back.Value, Lx200Format.ParseLongitudeEast, out var v)) {
                            mountLongitude = v;
                        }
                    }
                }
            } catch (Exception ex) {
                Logger.Error($"LX200: writing the site failed: {ex.Message}");
            }
        }

        /// <summary>The Mac's UTC plus the mount's clock offset measured at connect (never a serial read per NINA poll, RVM T2).</summary>
        public DateTime UTCDate {
            get {
                lock (stateLock) {
                    return clock.UtcNow() + clockOffset;
                }
            }
        }

        public bool AtHome => false;

        public bool CanFindHome => false;

        public Task FindHome(CancellationToken token) => Task.CompletedTask;

        public Epoch EquatorialSystem => Epoch.JNOW;

        public bool HasUnknownEpoch => false;

        public AlignmentMode AlignmentMode {
            get {
                lock (stateLock) {
                    return mode switch {
                        'P' => NINA.Core.Enum.AlignmentMode.Polar,
                        'G' => NINA.Core.Enum.AlignmentMode.GermanPolar,
                        _ => NINA.Core.Enum.AlignmentMode.AltAz
                    };
                }
            }
        }

        public Coordinates TargetCoordinates {
            get {
                lock (stateLock) {
                    return targetCoordinates;
                }
            }
        }

        public bool Slewing {
            get {
                lock (stateLock) {
                    return gotoActive || movingAxes.Count > 0;
                }
            }
        }

        // =============================================================================================
        // Tracking
        // =============================================================================================

        public bool CanSetTrackingEnabled => Connected;

        /// <summary>
        /// ":GW#"'s second character (T), or ACK != 'L'. Setting true sends ":AA#" only when the mount is not tracking; false sends
        /// ":AL#" (land mode). ":AP#" (polar) is blocklisted.
        /// </summary>
        public bool TrackingEnabled {
            get {
                lock (stateLock) {
                    return tracking;
                }
            }
            set {
                var l = link;
                if (l == null || !Connected) {
                    return;
                }
                try {
                    if (value) {
                        if (!TrackingEnabled) {
                            l.Send(":AA#");
                        }
                    } else {
                        l.Send(":AL#");
                    }
                    lock (stateLock) {
                        tracking = value;
                        mode = value ? 'A' : 'L';
                    }
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(TrackingRate));
                    RaisePropertyChanged(nameof(TrackingMode));
                } catch (Exception ex) {
                    Logger.Error($"LX200: tracking {(value ? "on" : "off")} failed: {ex.Message}");
                    Notification.ShowError($"LX200: tracking {(value ? "on" : "off")} failed: {ex.Message}");
                }
            }
        }

        public IList<TrackingMode> TrackingModes { get; } = new List<TrackingMode> { TrackingMode.Sidereal, TrackingMode.Lunar, TrackingMode.Stopped }.AsReadOnly();

        public TrackingRate TrackingRate {
            get {
                if (!Connected || !TrackingEnabled) {
                    return new TrackingRate { TrackingMode = TrackingMode.Stopped };
                }
                lock (stateLock) {
                    return new TrackingRate { TrackingMode = trackingMode };
                }
            }
        }

        public TrackingMode TrackingMode {
            get => TrackingRate.TrackingMode;
            set {
                if (value == TrackingMode.Custom) {
                    throw new ArgumentException("TrackingMode cannot be set to Custom. Use SetCustomTrackingRate");
                }
                var l = link;
                if (l == null || !Connected) {
                    return;
                }
                switch (value) {
                    case TrackingMode.Stopped:
                        TrackingEnabled = false;
                        return;
                    case TrackingMode.Sidereal:
                    case TrackingMode.Lunar:
                        try {
                            l.Send(value == TrackingMode.Sidereal ? ":TQ#" : ":TL#");
                            lock (stateLock) {
                                trackingMode = value;
                            }
                            TrackingEnabled = true;
                        } catch (Exception ex) {
                            Logger.Error($"LX200: tracking rate {value} failed: {ex.Message}");
                        }
                        return;
                    default:
                        Logger.Warning($"LX200: tracking mode {value} is not supported (sidereal and lunar only)");
                        return;
                }
            }
        }

        public bool CanSetDeclinationRate => false;

        public bool CanSetRightAscensionRate => false;

        public void SetCustomTrackingRate(double rightAscensionRate, double declinationRate) {
            throw new NotSupportedException("Custom tracking rate not supported");
        }

        // =============================================================================================
        // Gotos, sync, abort
        // =============================================================================================

        public bool CanSlew => true;

        public bool CanSlewAltAz => true;

        public async Task<bool> SlewToCoordinates(Coordinates coordinates, CancellationToken token) {
            if (!Connected || link == null) {
                return false;
            }
            if (AtPark) {
                Notification.ShowWarning("LX200: the mount is soft-parked; unpark it first");
                return false;
            }
            var jnow = coordinates.Transform(Epoch.JNOW);
            lock (stateLock) {
                targetCoordinates = jnow;
            }
            try {
                return await GotoAsync(jnow.RA, jnow.Dec, token, "goto").ConfigureAwait(false);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Fail($"goto failed: {ex.Message}");
                return false;
            } finally {
                lock (stateLock) {
                    targetCoordinates = null;
                }
            }
        }

        public async Task<bool> SlewToAltAz(TopocentricCoordinates coordinates, CancellationToken token) {
            if (!Connected || link == null) {
                return false;
            }
            if (AtPark) {
                Notification.ShowWarning("LX200: the mount is soft-parked; unpark it first");
                return false;
            }
            try {
                return await GotoAltAzAsync(coordinates.Altitude.Degree, coordinates.Azimuth.Degree, token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Fail($"alt-az goto failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>":Sr" + ":Sd" (each must answer '1'), ":MS#" ('0' = slewing), then <see cref="WaitForSlew"/>. JNow hours/degrees.</summary>
        internal async Task<bool> GotoAsync(double raHours, double decDegrees, CancellationToken token, string purpose) {
            var l = link ?? throw new Lx200DisconnectedException("not connected");
            CoordinatePrecision p;
            lock (stateLock) {
                reportedTarget = null;
                p = precision;
            }
            var sr = await l.SendAsync($":Sr{Lx200Format.FormatRa(raHours, p)}#", Lx200Lane.Command, token).ConfigureAwait(false);
            var sd = await l.SendAsync($":Sd{Lx200Format.FormatDec(decDegrees, p)}#", Lx200Lane.Command, token).ConfigureAwait(false);
            if (sr.Value != "1" || sd.Value != "1") {
                Fail($"{purpose}: target not accepted (:Sr -> {sr.Describe()}, :Sd -> {sd.Describe()})");
                return false;
            }
            int generation;
            lock (stateLock) {
                gotoActive = true;
                generation = abortGeneration;
            }
            RaisePropertyChanged(nameof(Slewing));
            try {
                var ms = await l.SendAsync(":MS#", Lx200Lane.Command, token).ConfigureAwait(false);
                if (!ms.IsOk) {
                    Fail($"{purpose}: ':MS#' {ms.Describe()}");
                    return false;
                }
                if (ms.Value != "0") {
                    var why = ms.Value switch { "1" => "below the horizon limit", "2" => "above the high limit", "3" => "the scope could hit the mount", _ => "refused" };
                    Fail($"{purpose}: the mount refused the goto ({why}: \"{Lx200Format.Printable(ms.Joined)}\")");
                    return false;
                }
                var arrived = await WaitForSlew(generation, purpose, token, async () => {
                    await RefreshPosition(includeAltAz: false, token).ConfigureAwait(false);
                    lock (stateLock) {
                        return Lx200Astro.SeparationDeg(ra, dec, raHours, decDegrees);
                    }
                }).ConfigureAwait(false);
                if (arrived && Settings.ReportTargetWithinResolution) {
                    lock (stateLock) {
                        if (WithinResolution(ra, dec, raHours, decDegrees)) {
                            reportedTarget = (raHours, decDegrees);
                        }
                    }
                }
                return arrived;
            } finally {
                lock (stateLock) {
                    gotoActive = false;
                }
                RaisePropertyChanged(nameof(Slewing));
            }
        }

        /// <summary>":Sa" + ":Sz", ":MA#" ('0' = no fault), then <see cref="WaitForSlew"/> against ":GA#"/":GZ#".</summary>
        internal async Task<bool> GotoAltAzAsync(double altitude, double azimuth, CancellationToken token) {
            var l = link ?? throw new Lx200DisconnectedException("not connected");
            CoordinatePrecision p;
            lock (stateLock) {
                reportedTarget = null;
                p = precision;
            }
            var sa = await l.SendAsync($":Sa{Lx200Format.FormatAltitude(altitude, p)}#", Lx200Lane.Command, token).ConfigureAwait(false);
            var sz = await l.SendAsync($":Sz{Lx200Format.FormatAzimuth(azimuth, p)}#", Lx200Lane.Command, token).ConfigureAwait(false);
            if (sa.Value != "1" || sz.Value != "1") {
                Fail($"alt-az goto: target not accepted (:Sa -> {sa.Describe()}, :Sz -> {sz.Describe()})");
                return false;
            }
            int generation;
            lock (stateLock) {
                gotoActive = true;
                generation = abortGeneration;
            }
            RaisePropertyChanged(nameof(Slewing));
            try {
                var ma = await l.SendAsync(":MA#", Lx200Lane.Command, token).ConfigureAwait(false);
                if (!ma.IsOk || ma.Value != "0") {
                    Fail($"alt-az goto: ':MA#' {ma.Describe()}");
                    return false;
                }
                return await WaitForSlew(generation, "alt-az goto", token, async () => {
                    await RefreshPosition(includeAltAz: true, token).ConfigureAwait(false);
                    lock (stateLock) {
                        var dAz = Lx200Astro.DegreeDifference(az, azimuth) * Math.Cos(altitude * Math.PI / 180.0);
                        return Math.Sqrt((dAz * dAz) + ((alt - altitude) * (alt - altitude)));
                    }
                }).ConfigureAwait(false);
            } finally {
                lock (stateLock) {
                    gotoActive = false;
                }
                RaisePropertyChanged(nameof(Slewing));
            }
        }

        /// <summary>
        /// Polls ":D#" every 0.5 s. A goto ends when ":D#" shows no bar (any non-space byte before '#' is a bar, RVM MNT-04),
        /// but not in the first <see cref="Lx200Settings.MinimumSlewSeconds"/>, and the mount's position is within
        /// <see cref="Lx200Settings.ArrivalToleranceArcmin"/> of the target or has stopped changing. Cancellation and the
        /// timeout send ":Q#"; <see cref="StopSlew"/> ends the wait with false.
        /// </summary>
        private async Task<bool> WaitForSlew(int generation, string purpose, CancellationToken token, Func<Task<double>> distanceDeg) {
            var l = link;
            var started = DateTime.UtcNow;
            var minimum = TimeSpan.FromSeconds(Math.Max(0, Settings.MinimumSlewSeconds));
            var timeout = TimeSpan.FromSeconds(Math.Max(1, Settings.SlewTimeoutSeconds));
            var tolerance = Settings.ArrivalToleranceArcmin / 60.0;
            var lastDistance = double.NaN;
            try {
                while (true) {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    if (Aborted(generation)) {
                        Logger.Info($"LX200: {purpose} aborted");
                        return false;
                    }
                    var elapsed = DateTime.UtcNow - started;
                    if (elapsed > timeout) {
                        TrySend(l, ":Q#", Lx200Lane.Stop);
                        Fail($"{purpose}: still slewing after {timeout.TotalSeconds:0} s; stopped with ':Q#' (High Precision pointing waiting for ENTER?)");
                        return false;
                    }
                    var bar = await l.SendAsync(":D#", Lx200Lane.Command, token).ConfigureAwait(false);
                    if (!bar.IsOk || bar.Value.Trim().Length > 0 || elapsed < minimum) {
                        continue;
                    }
                    var distance = await distanceDeg().ConfigureAwait(false);
                    if (distance <= tolerance) {
                        return true;
                    }
                    if (!double.IsNaN(lastDistance) && Math.Abs(distance - lastDistance) < 1.0 / 3600.0) {
                        Fail(string.Create(Inv, $"{purpose}: the mount stopped {distance * 60:0.0}' from the target (limit {Settings.ArrivalToleranceArcmin:0.#}')"));
                        return false;
                    }
                    lastDistance = distance;
                }
            } catch (OperationCanceledException) {
                TrySend(l, ":Q#", Lx200Lane.Stop);
                Logger.Info($"LX200: {purpose} cancelled; sent ':Q#'");
                throw;
            }
        }

        private bool Aborted(int generation) {
            lock (stateLock) {
                return abortGeneration != generation;
            }
        }

        private static void Fail(string message) {
            Logger.Error("LX200: " + message);
            Notification.ShowError("LX200: " + message);
        }

        /// <summary>":Q#" through the Stop lane, ahead of everything queued; ends a running goto wait with false.</summary>
        public void StopSlew() {
            var l = link;
            if (l == null) {
                return;
            }
            lock (stateLock) {
                abortGeneration++;
                movingAxes.Clear();
            }
            l.SendAsync(":Q#", Lx200Lane.Stop).ContinueWith(t => Logger.Warning($"LX200: ':Q#' failed: {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            RaisePropertyChanged(nameof(Slewing));
        }

        /// <summary>
        /// ":Sr" + ":Sd" (each '1') then ":CM#" (its fixed " M31 EX GAL ..." reply is read and dropped). Refused while slewing and
        /// when the target is more than <see cref="Lx200Settings.MaxSyncOffsetDegrees"/> from the mount's position (a bad solve).
        /// No tracking precondition: that is AscomTelescope's rule, not the mount's (RVM MNT-11).
        /// </summary>
        public bool Sync(Coordinates coordinates) {
            var l = link;
            if (l == null || !Connected) {
                return false;
            }
            if (Slewing) {
                Fail("sync refused while the mount is slewing");
                return false;
            }
            try {
                var jnow = coordinates.Transform(Epoch.JNOW);
                RefreshPosition(includeAltAz: false, CancellationToken.None).GetAwaiter().GetResult();
                double separation;
                CoordinatePrecision p;
                lock (stateLock) {
                    separation = Lx200Astro.SeparationDeg(ra, dec, jnow.RA, jnow.Dec);
                    p = precision;
                    reportedTarget = null;
                }
                var limit = Settings.MaxSyncOffsetDegrees;
                if (limit > 0 && separation > limit) {
                    Fail(string.Create(Inv, $"sync refused: the target is {separation:0.00}° from where the mount points (limit {limit:0.#}°); a plate solve that far off is more likely wrong than the mount"));
                    return false;
                }
                var sr = l.Send($":Sr{Lx200Format.FormatRa(jnow.RA, p)}#");
                var sd = l.Send($":Sd{Lx200Format.FormatDec(jnow.Dec, p)}#");
                if (sr.Value != "1" || sd.Value != "1") {
                    Fail($"sync: target not accepted (:Sr -> {sr.Describe()}, :Sd -> {sd.Describe()})");
                    return false;
                }
                var cm = l.Send(":CM#");
                if (!cm.IsOk) {
                    Fail($"sync: ':CM#' {cm.Describe()}");
                    return false;
                }
                RefreshPosition(includeAltAz: false, CancellationToken.None).GetAwaiter().GetResult();
                lock (stateLock) {
                    if (Settings.ReportTargetWithinResolution && WithinResolution(ra, dec, jnow.RA, jnow.Dec)) {
                        reportedTarget = (jnow.RA, jnow.Dec);
                    }
                }
                Logger.Info(string.Create(Inv, $"LX200: synced to {jnow} ({separation * 3600:0}\" from the previous position)"));
                return true;
            } catch (Exception ex) {
                Fail($"sync failed: {ex.Message}");
                return false;
            }
        }

        // =============================================================================================
        // Soft park
        // =============================================================================================

        public bool AtPark {
            get {
                lock (stateLock) {
                    return atPark;
                }
            }
        }

        public bool CanPark => true;

        public bool CanUnpark => true;

        public bool CanSetPark => true;

        /// <summary>
        /// Soft park (RVM MNT-M1): ":Q#", then an alt-az goto to <see cref="Lx200Settings.SoftParkAltitude"/>/<see cref="Lx200Settings.SoftParkAzimuth"/>
        /// if set, then tracking off (":AL#") if <see cref="Lx200Settings.SoftParkStopsTracking"/>. The Autostar's park command is
        /// never sent (blocklisted): it leaves the Autostar silent until power-cycled, and a carried-in fork has no park position.
        /// With CanPark true, NINA does not fall back to its slew to Dec +89 (TelescopeVM.cs:131-159), which on this site points
        /// the fork at the blocked northern sky.
        /// </summary>
        public async Task Park(CancellationToken token) {
            var l = link;
            if (l == null || !Connected || AtPark) {
                return;
            }
            await l.SendAsync(":Q#", Lx200Lane.Stop, token).ConfigureAwait(false);
            lock (stateLock) {
                abortGeneration++;
                movingAxes.Clear();
            }
            var parkAlt = Settings.SoftParkAltitude;
            var parkAz = Settings.SoftParkAzimuth;
            if (!double.IsNaN(parkAlt) && !double.IsNaN(parkAz)) {
                if (!await GotoAltAzAsync(parkAlt, parkAz, token).ConfigureAwait(false)) {
                    Fail("soft park: the goto to the park position failed; not parked");
                    return;
                }
            }
            if (Settings.SoftParkStopsTracking && TrackingEnabled) {
                TrackingEnabled = false;
                lock (stateLock) {
                    parkStoppedTracking = true;
                }
            }
            lock (stateLock) {
                atPark = true;
            }
            RaisePropertyChanged(nameof(AtPark));
            Logger.Info("LX200: soft-parked (halted" + (double.IsNaN(parkAlt) ? "" : string.Create(Inv, $", at alt {parkAlt:0.0} az {parkAz:0.0}")) + (Settings.SoftParkStopsTracking ? ", tracking off" : "") + ")");
        }

        public Task Unpark(CancellationToken token) {
            bool resume;
            lock (stateLock) {
                if (!atPark) {
                    return Task.CompletedTask;
                }
                atPark = false;
                resume = parkStoppedTracking;
                parkStoppedTracking = false;
            }
            if (resume) {
                TrackingEnabled = true;
            }
            RaisePropertyChanged(nameof(AtPark));
            Logger.Info("LX200: unparked" + (resume ? " (tracking on)" : ""));
            return Task.CompletedTask;
        }

        /// <summary>Stores the current altitude and azimuth as the soft-park position.</summary>
        public void Setpark() {
            if (!Connected) {
                return;
            }
            var (a, z) = (Altitude, Azimuth);
            if (double.IsNaN(a) || double.IsNaN(z)) {
                return;
            }
            Settings.SoftParkAltitude = a;
            Settings.SoftParkAzimuth = z;
            Logger.Info(string.Create(Inv, $"LX200: soft-park position set to alt {a:0.00} az {z:0.00}"));
        }

        // =============================================================================================
        // Manual moves
        // =============================================================================================

        public bool CanMovePrimaryAxis => Connected;

        public bool CanMoveSecondaryAxis => Connected;

        /// <summary>Guide (":RG#", the ":Rg" rate), centre (":RC#"), find (":RM#") and max (":RS#"), in deg/s.</summary>
        public IList<(double, double)> GetAxisRates(TelescopeAxes axis) {
            if (axis == TelescopeAxes.Tertiary) {
                return new List<(double, double)>();
            }
            return Rates().Select(r => (r.DegPerSec, r.DegPerSec)).ToList();
        }

        private IEnumerable<(double DegPerSec, string Command)> Rates() {
            yield return (Settings.GuideRateArcsecPerSec / 3600.0, ":RG#");
            yield return (CentreRateDegPerSec, ":RC#");
            yield return (FindRateDegPerSec, ":RM#");
            yield return (MaxRateDegPerSec, ":RS#");
        }

        private (double DegPerSec, string Command) NearestRate(double degPerSec) =>
            Rates().OrderBy(r => Math.Abs(Math.Log(Math.Max(1e-9, degPerSec) / r.DegPerSec))).First();

        public double PrimaryMovingRate {
            get => double.IsNaN(primaryMovingRate) ? CentreRateDegPerSec : primaryMovingRate;
            set {
                primaryMovingRate = NearestRate(Math.Abs(value)).DegPerSec;
                RaisePropertyChanged();
            }
        }

        public double SecondaryMovingRate {
            get => double.IsNaN(secondaryMovingRate) ? CentreRateDegPerSec : secondaryMovingRate;
            set {
                secondaryMovingRate = NearestRate(Math.Abs(value)).DegPerSec;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Primary = azimuth (":Me#" for a positive rate, ":Mw#" negative), secondary = altitude (":Mn#" up, ":Ms#" down), at the
        /// nearest of the four rates; rate 0 sends the axis' two halts through the Stop lane. The link owes the halt until it is
        /// accepted, and sends it first after a reconnect.
        /// </summary>
        public void MoveAxis(TelescopeAxes axis, double rate) {
            var l = link;
            if (l == null || !Connected) {
                Notification.ShowWarning("LX200: the mount is not connected");
                return;
            }
            if (axis == TelescopeAxes.Tertiary) {
                return;
            }
            var (plus, minus) = axis == TelescopeAxes.Primary ? ('e', 'w') : ('n', 's');
            try {
                if (rate == 0) {
                    lock (stateLock) {
                        movingAxes.Remove(axis);
                    }
                    SendHalt(l, $":Q{plus}#");
                    SendHalt(l, $":Q{minus}#");
                } else {
                    if (AtPark) {
                        Notification.ShowWarning("LX200: the mount is soft-parked; unpark it first");
                        return;
                    }
                    bool wasMoving;
                    lock (stateLock) {
                        reportedTarget = null;
                        wasMoving = movingAxes.Contains(axis);
                    }
                    if (wasMoving) {
                        l.Send($":Q{(rate > 0 ? minus : plus)}#", Lx200Lane.Stop);
                    }
                    l.Send(NearestRate(Math.Abs(rate)).Command, Lx200Lane.Timed);
                    l.Send($":M{(rate > 0 ? plus : minus)}#", Lx200Lane.Timed);
                    lock (stateLock) {
                        movingAxes.Add(axis);
                    }
                }
                RaisePropertyChanged(nameof(Slewing));
            } catch (Exception ex) {
                Logger.Error($"LX200: move {axis} at {rate} deg/s failed: {ex.Message}");
                Notification.ShowError($"LX200: move failed: {ex.Message}");
            }
        }

        // =============================================================================================
        // Pulse guiding
        // =============================================================================================

        public bool CanPulseGuide => true;

        public bool IsPulseGuiding => pulseGuider?.IsPulseGuiding ?? false;

        /// <summary>The ":Rg" rate for both axes; NINA's DirectGuider turns dither pixels into pulse lengths with it.</summary>
        public double GuideRateRightAscensionArcsecPerSec => Settings.GuideRateArcsecPerSec;

        public double GuideRateDeclinationArcsecPerSec => Settings.GuideRateArcsecPerSec;

        /// <summary>Returns at once; the pulse runs on the link (<see cref="Lx200PulseGuider"/>). <see cref="IsPulseGuiding"/> stays true until it ends.</summary>
        public void PulseGuide(GuideDirections direction, int duration) {
            var guider = pulseGuider;
            if (guider == null || !Connected) {
                Notification.ShowWarning("LX200: the mount is not connected");
                return;
            }
            if (AtPark) {
                Notification.ShowWarning("LX200: the mount is soft-parked; no pulse sent");
                return;
            }
            if (Slewing && guider.Strategy != Lx200PulseStrategy.GotoOffset) {
                Logger.Warning($"LX200: pulse {direction} {duration} ms ignored while the mount is slewing");
                return;
            }
            lock (stateLock) {
                reportedTarget = null;
            }
            guider.Pulse(direction, duration);
        }

        /// <summary>For the goto-offset strategy: fresh ":GR#"/":GD#".</summary>
        internal async Task<(double Ra, double Dec)> ReadPositionAsync(CancellationToken token) {
            await RefreshPosition(includeAltAz: false, token).ConfigureAwait(false);
            lock (stateLock) {
                return (ra, dec);
            }
        }

        internal CoordinatePrecision Precision {
            get {
                lock (stateLock) {
                    return precision;
                }
            }
        }

        // =============================================================================================
        // Link events
        // =============================================================================================

        private void OnLinkStateChanged(Lx200LinkState state) {
            RaisePropertyChanged(nameof(LinkState));
            switch (state) {
                case Lx200LinkState.Connected when Connected:
                    // the mount may have been switched off and on: it then starts in the low-precision format again
                    _ = Task.Run(async () => {
                        try {
                            await EnsureHighPrecisionFormat(CancellationToken.None).ConfigureAwait(false);
                            await RefreshStatus(CancellationToken.None).ConfigureAwait(false);
                            Logger.Info("LX200 mount: link restored, format and status re-read");
                        } catch (Exception ex) {
                            Logger.Warning($"LX200 mount: re-reading the status after the reconnect failed: {ex.Message}");
                        }
                    });
                    break;
                case Lx200LinkState.Failed when Connected:
                    Logger.Error("LX200 mount: the link gave up; disconnecting the mount");
                    _ = Task.Run(Disconnect);
                    break;
            }
        }
    }
}
