#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

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

    /// <summary>One focuser move as it went out: positions, requested motor time (backlash included) and measured run time.</summary>
    public sealed record Lx200FocusMoveRecord(int From, int To, int RequestedMs, double RanMs, Lx200FocusMethod Method, bool Completed);

    /// <summary>
    /// NINA's <see cref="IFocuser"/> for the Meade #1209 microfocuser on the LX200GPS's focuser port, driven through the mount's
    /// serial link. The #1209 has no position readout, so the position is virtual: milliseconds of motor time at one locked
    /// speed, starting at MaxStep/2 on connect, the way NINA fakes an absolute position for relative ASCOM focusers
    /// (AscomFocuser.cs:42-55 internalPosition, 169-174 MaxStep/2). A move to p runs |p - Position| ms toward p and then sets
    /// Position = p exactly, so NINA's FocuserVM loop ("while Position != target") ends; a halted move adds what actually ran.
    /// The speed (":F1#".. ":F4#") is sent before every move because the handbox can change it (RVM MNT-M6). A lower position is
    /// inward (":F+#", toward the objective), as NINA's In buttons expect (RVM MNT-17); <see cref="Lx200Settings.FocuserReverse"/>
    /// swaps it.
    /// </summary>
    public sealed class Lx200Focuser : BaseINPC, IFocuser, IDisposable {

        public const string DeviceId = "Nightglass.Lx200GPS.Focuser";

        private const int MaxPulseMs = 65000;   // ":FPsDDDD#" range (P07 l.194-199)

        private readonly IProfileService profileService;
        private readonly Lx200LinkPool pool;
        private readonly SemaphoreSlim moveLock = new(1, 1);
        private readonly object sync = new();
        private readonly List<Lx200FocusMoveRecord> history = new();
        private Lx200Link link;
        private bool connected;
        private bool isMoving;
        private int position;
        private int lastDirection;
        private double temperature = double.NaN;
        private CancellationTokenSource haltCts;
        private CancellationTokenSource pollCts;

        public Lx200Focuser(IProfileService profileService, Lx200LinkPool pool = null) {
            this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            this.pool = pool ?? Lx200LinkPool.Shared;
            Settings = new Lx200Settings(profileService);
        }

        public Lx200Settings Settings { get; }

        public Lx200Link Link => link;

        public Lx200LinkState LinkState => link?.State ?? Lx200LinkState.Closed;

        /// <summary>Every move since connect, for diagnostics and the bench.</summary>
        public IReadOnlyList<Lx200FocusMoveRecord> History {
            get {
                lock (sync) {
                    return history.ToArray();
                }
            }
        }

        // =============================================================================================
        // IDevice
        // =============================================================================================

        public bool HasSetupDialog => false;

        public string Id => DeviceId;

        public string Name => "Meade #1209 focuser";

        public string DisplayName => "Meade #1209 focuser (LX200GPS port)";

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

        public string Description => "Meade #1209 microfocuser on the LX200GPS focuser port; virtual position in milliseconds of motor time";

        public string DriverInfo => "Nightglass LX200 driver (NINA.Mac.Equipment.Lx200)";

        public string DriverVersion => typeof(Lx200Focuser).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                       ?? typeof(Lx200Focuser).Assembly.GetName().Version?.ToString() ?? "1.0";

        public void SetupDialog() {
        }

        public async Task<bool> Connect(CancellationToken token) {
            if (Connected) {
                return true;
            }
            try {
                var port = Lx200Ports.Resolve(Settings.PortPath);
                link = await Task.Run(() => pool.Acquire(port, token), token).ConfigureAwait(false);
                link.StateChanged += OnLinkStateChanged;
                var speed = await link.SendAsync($":F{Settings.FocuserSpeed}#", Lx200Lane.Command, token).ConfigureAwait(false);
                if (!speed.IsOk) {
                    throw new Lx200ReplyException($"focus speed: {speed.Describe()}", speed);
                }
                // ":fT#" (OTA temperature, LX200GPS) may not be answered by every model; then Temperature stays NaN
                var ft = await link.SendAsync(":fT#", Lx200Lane.Command, token).ConfigureAwait(false);
                var supported = ft.IsOk && double.TryParse(ft.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && double.IsFinite(t);
                lock (sync) {
                    temperature = supported ? double.Parse(ft.Value, NumberStyles.Float, CultureInfo.InvariantCulture) : double.NaN;
                    position = Settings.FocuserMaxStep / 2;
                    lastDirection = 0;
                    history.Clear();
                }
                if (supported) {
                    StartTemperaturePolling();
                }
                Connected = true;
                RaisePropertyChanged(nameof(Position));
                Logger.Info($"LX200 focuser connected on {port}: virtual position {Position} of {MaxStep} ms at speed {Settings.FocuserSpeed}, {Settings.FocusMethod}, temperature {(supported ? Temperature.ToString("0.0", CultureInfo.InvariantCulture) + " C" : "not reported")}");
                return true;
            } catch (OperationCanceledException) {
                Teardown();
                throw;
            } catch (Exception ex) {
                Logger.Error("LX200 focuser: connect failed", ex);
                Notification.ShowError($"LX200: cannot connect the focuser: {ex.Message}");
                Teardown();
                return false;
            }
        }

        public void Disconnect() {
            if (!Connected && link == null) {
                return;
            }
            Halt();
            Connected = false;
            Teardown();
            Logger.Info("LX200 focuser disconnected");
        }

        public void Dispose() {
            Disconnect();
        }

        /// <summary>Releases the link; safe to call twice at once (user disconnect and a failed link).</summary>
        private void Teardown() {
            var cts = Interlocked.Exchange(ref pollCts, null);
            try {
                cts?.Cancel();
            } catch (ObjectDisposedException) {
            }
            var l = Interlocked.Exchange(ref link, null);
            if (l != null) {
                l.StateChanged -= OnLinkStateChanged;
                pool.Release(l);
            }
        }

        public IList<string> SupportedActions => new List<string> { "Lx200.LinkState", "Lx200.RecenterPosition" };

        public string Action(string actionName, string actionParameters) {
            switch (actionName) {
                case "Lx200.LinkState":
                    return LinkState.ToString();
                case "Lx200.RecenterPosition":
                    RecenterPosition();
                    return Position.ToString(CultureInfo.InvariantCulture);
                default:
                    throw new NotSupportedException($"Action '{actionName}' is not supported by the LX200 focuser");
            }
        }

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

        private Lx200Link RequireLink() => link != null && Connected ? link : throw new InvalidOperationException("The LX200 focuser is not connected");

        // =============================================================================================
        // IFocuser
        // =============================================================================================

        public bool IsMoving {
            get {
                lock (sync) {
                    return isMoving;
                }
            }
            private set {
                lock (sync) {
                    isMoving = value;
                }
                RaisePropertyChanged();
            }
        }

        public int MaxIncrement => MaxStep;

        public int MaxStep => Settings.FocuserMaxStep;

        public int Position {
            get {
                lock (sync) {
                    return position;
                }
            }
            private set {
                lock (sync) {
                    position = value;
                }
                RaisePropertyChanged();
            }
        }

        /// <summary>No physical step: a step is a millisecond of motor time.</summary>
        public double StepSize => double.NaN;

        public bool TempCompAvailable => false;

        public bool TempComp {
            get => false;
            set {
            }
        }

        /// <summary>":fT#" OTA temperature in °C, or NaN when the mount does not answer it.</summary>
        public double Temperature {
            get {
                lock (sync) {
                    return temperature;
                }
            }
        }

        /// <summary>Puts the virtual position back to the middle (after a coarse focus with the mirror unlocked, RVM MNT-M9).</summary>
        public void RecenterPosition() {
            Position = MaxStep / 2;
            lock (sync) {
                lastDirection = 0;
            }
        }

        /// <summary>
        /// Moves to <paramref name="targetPosition"/> (clamped to 0..MaxStep): the speed command, then either ":F+#"/":F-#" and a
        /// ":FQ#" scheduled on the link for start + ms (Timed lane: no poll or command starts while it is due, so it goes out on
        /// time), or one mount-timed ":FP" pulse per 65 s. A change of direction adds <see cref="Lx200Settings.FocuserBacklashMs"/>
        /// of motor time that is not counted in the position. Cancellation or <see cref="Halt"/> stops at once; the position then
        /// grows by the time that actually ran.
        /// </summary>
        public async Task Move(int targetPosition, CancellationToken ct, int waitInMs = 1000) {
            var l = link;
            if (l == null || !Connected) {
                return;
            }
            var target = Math.Clamp(targetPosition, 0, MaxStep);
            await moveLock.WaitAsync(ct).ConfigureAwait(false);
            using var halt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (sync) {
                haltCts = halt;
            }
            try {
                var from = Position;
                var delta = target - from;
                if (delta == 0) {
                    return;
                }
                var direction = delta > 0 ? 1 : -1;
                var outwardMotor = (direction > 0) != Settings.FocuserReverse;
                int extra;
                lock (sync) {
                    extra = lastDirection != 0 && lastDirection != direction ? Settings.FocuserBacklashMs : 0;
                }
                var ms = Math.Abs(delta) + extra;
                var method = Settings.FocusMethod;
                IsMoving = true;
                var speed = await l.SendAsync($":F{Settings.FocuserSpeed}#", Lx200Lane.Timed, ct).ConfigureAwait(false);
                if (!speed.IsOk) {
                    throw new Lx200ReplyException($"focus speed: {speed.Describe()}", speed);
                }
                var (ran, completed) = method == Lx200FocusMethod.MountPulse
                    ? await PulseMove(l, outwardMotor, ms, halt.Token).ConfigureAwait(false)
                    : await TimedMove(l, outwardMotor, ms, halt.Token).ConfigureAwait(false);
                int reached;
                if (completed) {
                    reached = target;
                } else {
                    var moved = (int)Math.Round(Math.Clamp(ran - extra, 0, Math.Abs(delta)));
                    reached = from + (direction * moved);
                }
                lock (sync) {
                    lastDirection = direction;
                    history.Add(new Lx200FocusMoveRecord(from, reached, ms, ran, method, completed));
                }
                Position = reached;
                if (completed && method == Lx200FocusMethod.HostTimed && Math.Abs(ran - ms) > 30) {
                    Logger.Debug(string.Create(CultureInfo.InvariantCulture, $"LX200 focuser: {ms} ms move ran {ran:0} ms"));
                }
                if (!completed) {
                    Logger.Info(string.Create(CultureInfo.InvariantCulture, $"LX200 focuser: move to {target} stopped after {ran:0} ms at {reached}"));
                }
            } finally {
                lock (sync) {
                    haltCts = null;
                }
                IsMoving = false;
                moveLock.Release();
            }
        }

        /// <summary>
        /// Host-timed: ":F+#" (inward) or ":F-#", then ":FQ#" at start + ms, and a second ":FQ#" right after. If the link goes down
        /// while the motor may run (also when it goes as the start is written), the link owes the halt and sends it first when the
        /// mount answers again; a start answered with stray bytes is halted by the link at once. The move then counts as having
        /// run until that halt (<see cref="Lx200Link.OwedStopSentUtc"/>), and a warning says the position is an estimate.
        /// </summary>
        private async Task<(double RanMs, bool Completed)> TimedMove(Lx200Link l, bool outward, int ms, CancellationToken halt) {
            var asked = l.UtcNow;
            Lx200Reply start;
            try {
                start = await l.SendAsync(outward ? ":F-#" : ":F+#", Lx200Lane.Timed).ConfigureAwait(false);
            } catch (Lx200DisconnectedException) when (MayRun(l, asked)) {
                return await AfterLinkLoss(l, asked, "the link went down as the focuser started").ConfigureAwait(false);
            }
            if (start.Status == ReplyStatus.Nak) {
                throw new Lx200ReplyException($"focuser start refused: {start.Describe()}", start);   // busy: nothing moved
            }
            if (!start.IsOk) {
                return await AfterLinkLoss(l, start.SentUtc, $"the focuser start got an unexpected answer ({start.Status})").ConfigureAwait(false);
            }
            using var dropScheduled = new CancellationTokenSource();
            var scheduled = l.SendAsync(":FQ#", Lx200Lane.Timed, dropScheduled.Token, dueUtc: start.SentUtc + TimeSpan.FromMilliseconds(ms));
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnState(Lx200LinkState s) {
                if (s != Lx200LinkState.Connected) {
                    lost.TrySetResult();
                }
            }
            Lx200Reply stop;
            var completed = true;
            l.StateChanged += OnState;
            try {
                if (l.State != Lx200LinkState.Connected) {
                    lost.TrySetResult();
                }
                using (halt.Register(() => stopped.TrySetResult())) {
                    var first = await Task.WhenAny(scheduled, stopped.Task, lost.Task).ConfigureAwait(false);
                    if (first == lost.Task || scheduled.IsFaulted) {
                        dropScheduled.Cancel();
                        return await AfterLinkLoss(l, start.SentUtc, "the link was lost during a focuser move").ConfigureAwait(false);
                    }
                    if (first == stopped.Task) {
                        completed = false;
                        dropScheduled.Cancel();
                        stop = await l.SendAsync(":FQ#", Lx200Lane.Stop).ConfigureAwait(false);
                        if (scheduled.IsCompletedSuccessfully && scheduled.Result.SentUtc <= stop.SentUtc) {
                            stop = scheduled.Result;   // the scheduled halt went out first: the move was complete
                            completed = true;
                        }
                    } else {
                        stop = await scheduled.ConfigureAwait(false);
                    }
                }
            } finally {
                l.StateChanged -= OnState;
            }
            if (!stop.IsOk) {
                stop = await l.SendAsync(":FQ#", Lx200Lane.Stop).ConfigureAwait(false);
            }
            if (Settings.FocuserDoubleHalt) {
                await l.SendAsync(":FQ#", Lx200Lane.Timed).ConfigureAwait(false);
            }
            return ((stop.SentUtc - start.SentUtc).TotalMilliseconds, completed);
        }

        /// <summary>True when a start that failed may still have reached the motor: the link owes (or has just sent) its halt.</summary>
        private static bool MayRun(Lx200Link l, DateTime asked) =>
            l.OwedStops.Contains(":FQ#") || (l.OwedStopSentUtc(":FQ#") is { } sent && sent >= asked);

        /// <summary>
        /// The motor may have run without its scheduled halt: waits for the link to send the halt it owes (first thing after a
        /// reconnect, or at once after a garbled start) and counts the move until then.
        /// </summary>
        private async Task<(double RanMs, bool Completed)> AfterLinkLoss(Lx200Link l, DateTime startUtc, string what) {
            Logger.Warning($"LX200 focuser: {what}; the focuser may be moving, and its halt is sent as soon as the mount answers");
            var back = await l.WaitUntilConnectedAsync(l.Options.WaitForReconnect + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var waitForHalt = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (back && !(l.OwedStopSentUtc(":FQ#") >= startUtc) && DateTime.UtcNow < waitForHalt) {
                await Task.Delay(10).ConfigureAwait(false);
            }
            var sent = l.OwedStopSentUtc(":FQ#");
            if (back && sent.HasValue && sent.Value >= startUtc) {
                var ran = (sent.Value - startUtc).TotalMilliseconds;
                var message = string.Create(CultureInfo.InvariantCulture, $"{what}; the focuser was stopped after about {ran:0} ms. The position is an estimate: check focus.");
                Logger.Warning("LX200 focuser: " + message);
                Notification.ShowWarning("LX200: " + message);
                return (ran, false);
            }
            throw new Lx200DisconnectedException($"{what}, and the link has not come back; the focuser may still be running and its position is unknown (check focus, then recentre)");
        }

        /// <summary>Mount-timed: ":FP+DDDD#" (inward) or ":FP-DDDD#", one per 65 s, waiting out each; a halt sends ":FQ#".</summary>
        private async Task<(double RanMs, bool Completed)> PulseMove(Lx200Link l, bool outward, int ms, CancellationToken halt) {
            var remaining = ms;
            double ran = 0;
            while (remaining > 0) {
                var chunk = Math.Min(remaining, MaxPulseMs);
                var sent = await l.SendAsync(Lx200Format.FocusPulseCommand(outward ? -chunk : chunk), Lx200Lane.Timed).ConfigureAwait(false);
                if (!sent.IsOk) {
                    throw new Lx200ReplyException($"focus pulse: {sent.Describe()}", sent);
                }
                var end = sent.SentUtc + TimeSpan.FromMilliseconds(chunk);
                try {
                    var wait = end - l.UtcNow;
                    if (wait > TimeSpan.Zero) {
                        await Task.Delay(wait, halt).ConfigureAwait(false);
                    }
                } catch (OperationCanceledException) {
                    var stop = await l.SendAsync(":FQ#", Lx200Lane.Stop).ConfigureAwait(false);
                    if (Settings.FocuserDoubleHalt) {
                        await l.SendAsync(":FQ#", Lx200Lane.Timed).ConfigureAwait(false);
                    }
                    return (ran + Math.Min(chunk, (stop.SentUtc - sent.SentUtc).TotalMilliseconds), false);
                }
                ran += chunk;
                remaining -= chunk;
            }
            return (ran, true);
        }

        /// <summary>Stops a running move at once (":FQ#" through the Stop lane); with no move running, sends ":FQ#" anyway.</summary>
        public void Halt() {
            CancellationTokenSource h;
            lock (sync) {
                h = haltCts;
            }
            if (h != null) {
                try {
                    h.Cancel();
                } catch (ObjectDisposedException) {
                }
                return;
            }
            var l = link;
            if (l != null && Connected) {
                l.SendAsync(":FQ#", Lx200Lane.Stop).ContinueWith(t => Logger.Warning($"LX200 focuser: ':FQ#' failed: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }

        // =============================================================================================

        private void StartTemperaturePolling() {
            pollCts = new CancellationTokenSource();
            var token = pollCts.Token;
            _ = Task.Run(async () => {
                while (!token.IsCancellationRequested) {
                    try {
                        await Task.Delay(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
                        var l = link;
                        if (l == null || !l.IsConnected) {
                            continue;
                        }
                        var ft = await l.SendAsync(":fT#", Lx200Lane.Poll, token, coalesceKey: ":fT#").ConfigureAwait(false);
                        if (ft.IsOk && double.TryParse(ft.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && double.IsFinite(t)) {
                            lock (sync) {
                                temperature = t;
                            }
                        }
                    } catch (OperationCanceledException) {
                        break;
                    } catch (Exception ex) {
                        Logger.Trace($"LX200 focuser temperature: {ex.Message}");
                    }
                }
            });
        }

        private void OnLinkStateChanged(Lx200LinkState state) {
            RaisePropertyChanged(nameof(LinkState));
            if (state == Lx200LinkState.Failed && Connected) {
                Logger.Error("LX200 focuser: the link gave up; disconnecting the focuser");
                _ = Task.Run(Disconnect);
            }
        }
    }
}
