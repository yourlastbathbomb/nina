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
using NINA.Mac.Lx200;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200 {

    /// <summary>
    /// Priority of a queued transaction; a lower value goes first. One serial link carries the mount's gotos, pulses and polling
    /// and the focuser's moves (research/rig_investigate_mount.md MNT-27, Table 4).
    /// </summary>
    public enum Lx200Lane {

        /// <summary>Halts (":Q#", ":Qn#", ":FQ#"): sent as soon as the transaction on the wire ends.</summary>
        Stop = 0,

        /// <summary>
        /// Pulse and focus starts and stops, optionally scheduled for a given time. While one is due within
        /// <see cref="Lx200LinkOptions.TimedGuard"/>, no command or poll starts, so the stop goes out on time.
        /// </summary>
        Timed = 1,

        /// <summary>Gotos, syncs, setters and pass-through commands, first in, first out.</summary>
        Command = 2,

        /// <summary>Status reads for the property caches. A read with the same key that is still waiting is reused.</summary>
        Poll = 3
    }

    public enum Lx200LinkState {

        /// <summary>Not opened yet, or disposed.</summary>
        Closed,

        Connected,

        /// <summary>The port failed or the mount stopped answering; the link reopens the port on its own.</summary>
        Reconnecting,

        /// <summary>Reconnecting gave up after <see cref="Lx200LinkOptions.ReconnectGiveUp"/>. Terminal.</summary>
        Failed
    }

    public sealed class Lx200LinkOptions {

        /// <summary>
        /// Framing options of the protocol library. The driver listens 40 ms for a NAK after a command without a reply (P07:
        /// NAK within 10 ms of the '#', plus up to 16 ms FTDI latency, RIM MNT-24) instead of the bench probe's 60 ms, because
        /// a host-timed pulse or focus stop cannot go out while that window is open.
        /// </summary>
        public Lx200ConnectionOptions Connection { get; set; } = new Lx200ConnectionOptions { NakWindow = TimeSpan.FromMilliseconds(40) };

        /// <summary>No command or poll starts when a timed transaction is due within this time (a reply at 9600 baud with FTDI latency takes 35-60 ms, RIM Table 5).</summary>
        public TimeSpan TimedGuard { get; set; } = TimeSpan.FromMilliseconds(120);

        /// <summary>A command whose reply may take longer than this (":SC" waits for the planetary update) waits while any timed transaction is pending.</summary>
        public TimeSpan LongTransaction { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>ACK attempts when the port is first opened.</summary>
        public int HandshakeAttempts { get; set; } = 3;

        public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>How long a command queued while the link is down waits for it to come back before it fails.</summary>
        public TimeSpan WaitForReconnect { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>After this long without the mount the link stops trying and enters <see cref="Lx200LinkState.Failed"/>; null = never.</summary>
        public TimeSpan? ReconnectGiveUp { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Consecutive transactions with no usable reply, after a failed ACK resync each, that count as a lost link (mount switched off, dead cable).</summary>
        public int FailuresBeforeLost { get; set; } = 3;

        /// <summary>A refused halt (NAK) is sent again for this long before the link gives up on it and raises an error.</summary>
        public TimeSpan HaltRetryFor { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>Folder for the byte trace file (one per link); null keeps the trace in memory only.</summary>
        public string TraceDirectory { get; set; }

        /// <summary>Trace lines kept in memory (the file keeps all of them).</summary>
        public int TraceMemoryEntries { get; set; } = 20_000;

        /// <summary>Show NINA notifications when the link is lost, restored or given up.</summary>
        public bool ShowNotifications { get; set; } = true;
    }

    /// <summary>
    /// One serial link to the LX200GPS, shared by <see cref="Lx200Telescope"/> and <see cref="Lx200Focuser"/>. A single worker
    /// thread owns the port and runs one transaction at a time through <see cref="Lx200Connection"/> (per-command reply shapes
    /// from <see cref="Lx200Catalog"/>, NAK retry, ACK resync, byte trace), taking work from four lanes in priority order
    /// (<see cref="Lx200Lane"/>). When the port fails (USB adapter unplugged) or the mount stops answering, the link reopens
    /// the port every <see cref="Lx200LinkOptions.ReconnectInterval"/>; the first thing it sends once the mount answers again
    /// are the halts still owed for host-timed motions that were running when the link went down.
    /// Blocklisted commands (":hP#" and the rest of <see cref="Lx200Catalog.BlockedCommands"/>) are refused before they are
    /// queued, and again by the connection before any byte is written.
    /// </summary>
    public sealed class Lx200Link : IDisposable {
        private readonly Func<Lx200Trace, ILx200Transport> opener;
        private readonly object gate = new();
        private readonly LinkedList<Request> stops = new();
        private readonly List<Request> timed = new();
        private readonly LinkedList<Request> commands = new();
        private readonly LinkedList<Request> polls = new();
        private readonly HashSet<string> owedStops = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> owedStopSent = new(StringComparer.Ordinal);
        private readonly bool ownsTrace;
        private Thread worker;
        private Lx200Connection connection;
        private Lx200LinkState state = Lx200LinkState.Closed;
        private DateTime downSinceUtc;
        private DateTime nextAttemptUtc;
        private int consecutiveFailures;
        private int failedAttempts;
        private int reconnectCount;
        private bool disposed;

        /// <param name="name">Port path (or a test name); used in messages and the trace file name.</param>
        /// <param name="opener">Opens the transport; called again for every reconnect attempt. Throws when the port is missing or busy.</param>
        public Lx200Link(string name, Func<Lx200Trace, ILx200Transport> opener, Lx200LinkOptions options = null, Lx200Trace trace = null) {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            this.opener = opener ?? throw new ArgumentNullException(nameof(opener));
            Options = options ?? new Lx200LinkOptions();
            if (trace == null) {
                Trace = CreateTrace(Name, Options, out var file);
                TraceFile = file;
                ownsTrace = true;
            } else {
                Trace = trace;
            }
        }

        public string Name { get; }

        public Lx200LinkOptions Options { get; }

        public Lx200Trace Trace { get; }

        /// <summary>Path of the byte trace, or null when it is kept in memory only.</summary>
        public string TraceFile { get; }

        public Lx200LinkState State {
            get {
                lock (gate) {
                    return state;
                }
            }
        }

        public bool IsConnected => State == Lx200LinkState.Connected;

        /// <summary>How often the link came back after being lost.</summary>
        public int ReconnectCount {
            get {
                lock (gate) {
                    return reconnectCount;
                }
            }
        }

        /// <summary>The link's clock (monotonic, as written in the trace and in <see cref="Lx200Reply.SentUtc"/>).</summary>
        public DateTime UtcNow => Trace.UtcNow;

        /// <summary>Raised on a thread-pool thread after every state change.</summary>
        public event Action<Lx200LinkState> StateChanged;

        /// <summary>Halts the link still owes for host-timed motions (":Qn#", ":FQ#"); sent first after a reconnect and on dispose.</summary>
        public IReadOnlyCollection<string> OwedStops {
            get {
                lock (gate) {
                    return owedStops.ToArray();
                }
            }
        }

        /// <summary>
        /// When an owed halt (<paramref name="command"/>, e.g. ":FQ#") last went out after a reconnect, on the link clock; null if
        /// never. A host-timed motion that was running when the link went down really stopped then.
        /// </summary>
        public DateTime? OwedStopSentUtc(string command) {
            lock (gate) {
                return owedStopSent.TryGetValue(command, out var t) ? t : null;
            }
        }

        /// <summary>Waits until the link is <see cref="Lx200LinkState.Connected"/>; false after <paramref name="timeout"/> or when it failed or closed.</summary>
        public async Task<bool> WaitUntilConnectedAsync(TimeSpan timeout, CancellationToken token = default) {
            var until = DateTime.UtcNow + timeout;
            while (true) {
                var s = State;
                if (s == Lx200LinkState.Connected) {
                    return true;
                }
                if (s is Lx200LinkState.Failed or Lx200LinkState.Closed || DateTime.UtcNow >= until) {
                    return false;
                }
                await Task.Delay(20, token).ConfigureAwait(false);
            }
        }

        /// <summary>Pool bookkeeping (<see cref="Lx200LinkPool"/>).</summary>
        internal int RefCount { get; set; }

        public int QueueLength(Lx200Lane lane) {
            lock (gate) {
                return lane switch {
                    Lx200Lane.Stop => stops.Count,
                    Lx200Lane.Timed => timed.Count,
                    Lx200Lane.Command => commands.Count,
                    _ => polls.Count
                };
            }
        }

        /// <summary>Opens the port and waits for the mount to answer ACK. Throws <see cref="Lx200Exception"/> when it does not.</summary>
        public void Open(CancellationToken token = default) {
            lock (gate) {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (state != Lx200LinkState.Closed) {
                    return;
                }
            }
            Exception last = null;
            for (var attempt = 1; attempt <= Math.Max(1, Options.HandshakeAttempts); attempt++) {
                token.ThrowIfCancellationRequested();
                try {
                    var conn = ConnectOnce();
                    if (conn != null) {
                        lock (gate) {
                            connection = conn;
                            state = Lx200LinkState.Connected;
                            worker = new Thread(Run) { IsBackground = true, Name = $"lx200-link {Name}", Priority = ThreadPriority.AboveNormal };
                            worker.Start();
                        }
                        Trace.Note($"link open on {Name}");
                        RaiseStateChanged(Lx200LinkState.Connected);
                        return;
                    }
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    last = ex;
                    Trace.Note($"open attempt {attempt} on {Name} failed: {ex.Message}");
                }
                if (attempt < Options.HandshakeAttempts) {
                    token.WaitHandle.WaitOne(Options.ReconnectInterval);
                }
            }
            token.ThrowIfCancellationRequested();
            throw new Lx200Exception(last != null
                ? $"Cannot open the LX200 link on {Name}: {last.Message}"
                : $"No LX200 mount answered ACK on {Name}", last);
        }

        /// <summary>
        /// Queues one command. Blocklisted and malformed commands throw here, before anything is queued. The task fails with
        /// <see cref="Lx200DisconnectedException"/> when the link is down for longer than <see cref="Lx200LinkOptions.WaitForReconnect"/>
        /// (a poll fails at once), and is cancelled when <paramref name="token"/> fires before the command was written.
        /// </summary>
        /// <param name="coalesceKey">Poll lane: a waiting poll with the same key is returned instead of queueing another.</param>
        /// <param name="dueUtc">Timed lane: not sent before this time (link clock, <see cref="UtcNow"/>).</param>
        public Task<Lx200Reply> SendAsync(string command, Lx200Lane lane = Lx200Lane.Command, CancellationToken token = default,
                                          ReplyShape shape = null, TimeSpan? timeout = null, string coalesceKey = null, DateTime? dueUtc = null) {
            var cmd = Lx200Command.Parse(command);
            if (cmd.Spec?.IsBlocked == true) {
                Trace.Log(TraceKind.Error, ReadOnlySpan<byte>.Empty, $"REFUSED {cmd.Text} (blocklist, nothing queued or written): {cmd.Spec.BlockedReason}");
                throw new Lx200BlockedCommandException(cmd.Text, cmd.Spec);
            }
            var now = UtcNow;
            var request = new Request(cmd, lane, shape, timeout, coalesceKey, token, dueUtc ?? now, now);
            lock (gate) {
                if (disposed || state is Lx200LinkState.Closed or Lx200LinkState.Failed) {
                    return Task.FromException<Lx200Reply>(new Lx200DisconnectedException($"{cmd.Text}: the LX200 link on {Name} is {(disposed ? "closed" : state.ToString().ToLowerInvariant())}"));
                }
                if (state == Lx200LinkState.Reconnecting && lane == Lx200Lane.Poll) {
                    return Task.FromException<Lx200Reply>(new Lx200DisconnectedException($"{cmd.Text}: the LX200 link on {Name} is down, reconnecting"));
                }
                if (state == Lx200LinkState.Reconnecting && lane == Lx200Lane.Stop) {
                    owedStops.Add(cmd.Text);
                    Trace.Note($"{cmd.Text} owed: the link is down; it goes out first when the mount answers again");
                    return Task.FromException<Lx200Reply>(new Lx200DisconnectedException($"{cmd.Text}: the LX200 link on {Name} is down; the halt is sent as soon as it is back"));
                }
                if (lane == Lx200Lane.Poll && coalesceKey != null) {
                    foreach (var waiting in polls) {
                        if (waiting.CoalesceKey == coalesceKey && !waiting.Token.IsCancellationRequested) {
                            return waiting.Task;
                        }
                    }
                }
                switch (lane) {
                    case Lx200Lane.Stop: stops.AddLast(request); break;
                    case Lx200Lane.Timed: timed.Add(request); break;
                    case Lx200Lane.Command: commands.AddLast(request); break;
                    default: polls.AddLast(request); break;
                }
                Monitor.PulseAll(gate);
            }
            if (token.CanBeCanceled) {
                request.Registration = token.Register(() => CancelQueued(request));
            }
            return request.Task;
        }

        /// <summary>Synchronous <see cref="SendAsync"/>.</summary>
        public Lx200Reply Send(string command, Lx200Lane lane = Lx200Lane.Command, ReplyShape shape = null, TimeSpan? timeout = null) =>
            SendAsync(command, lane, CancellationToken.None, shape, timeout).GetAwaiter().GetResult();

        /// <summary>
        /// Emergency stop from the caller's thread: ":Q#", the four direction halts and ":FQ#" twice, written directly if the
        /// worker holds the port for more than 3 s (<see cref="Lx200Connection.StopAll"/>). False when a halt could not be sent
        /// or was refused.
        /// </summary>
        public bool EmergencyStop(string reason) {
            Lx200Connection conn;
            lock (gate) {
                conn = state == Lx200LinkState.Connected ? connection : null;
            }
            if (conn == null) {
                lock (gate) {
                    owedStops.Add(":Q#");
                    owedStops.Add(":FQ#");
                }
                return false;
            }
            var ok = conn.StopAll(reason);
            if (ok) {
                lock (gate) {
                    owedStops.Clear();
                }
            }
            return ok;
        }

        public void Dispose() {
            Thread w;
            lock (gate) {
                if (disposed) {
                    return;
                }
                disposed = true;
                w = worker;
                Monitor.PulseAll(gate);
            }
            if (w != null && !w.Join(TimeSpan.FromSeconds(10))) {
                Trace.Note("link worker did not stop within 10 s");
            }
            Lx200Connection conn;
            string[] unsent;
            lock (gate) {
                conn = connection;
                connection = null;
                state = Lx200LinkState.Closed;
                unsent = owedStops.ToArray();
            }
            if (unsent.Length > 0) {
                Trace.Note($"closing with halt(s) still owed and not sent (link down): {string.Join(" ", unsent)}");
                Logger.Warning($"LX200 link on {Name} closed while the halts {string.Join(" ", unsent)} could not be sent: a host-timed motion may still be running");
            }
            try {
                conn?.Dispose();
            } catch (Exception ex) {
                Trace.Note($"closing the port: {ex.Message}");
            }
            Trace.Note($"link on {Name} closed");
            RaiseStateChanged(Lx200LinkState.Closed);
            if (ownsTrace) {
                Trace.Dispose();
            }
        }

        // =============================================================================================
        // Worker
        // =============================================================================================

        private void Run() {
            while (true) {
                Request next = null;
                var reconnectNow = false;
                var portClosed = false;
                lock (gate) {
                    while (next == null && !reconnectNow && !portClosed) {
                        if (disposed) {
                            break;
                        }
                        var now = UtcNow;
                        if (state == Lx200LinkState.Connected) {
                            if (connection != null && !connection.IsOpen) {
                                portClosed = true;   // the adapter went away while the link was idle
                                break;
                            }
                            next = Pick(now, out var wait);
                            if (next == null) {
                                Monitor.Wait(gate, wait < IdleCheck ? wait : IdleCheck);
                            }
                        } else if (state == Lx200LinkState.Reconnecting) {
                            ExpireWhileDown(now);
                            if (now >= nextAttemptUtc) {
                                reconnectNow = true;
                            } else {
                                Monitor.Wait(gate, Clamp(nextAttemptUtc - now, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(100)));
                            }
                        } else {
                            FailAllPending("the LX200 link failed");
                            Monitor.Wait(gate, 250);
                        }
                    }
                    if (disposed && next == null && !reconnectNow && !portClosed) {
                        FailAllPending("the LX200 link was closed");
                    }
                }
                if (portClosed) {
                    MarkLost("the port closed");
                } else if (next != null) {
                    Execute(next);
                } else if (reconnectNow) {
                    TryReconnect();
                } else {
                    // disposed: the halts still owed go out before the port closes
                    Lx200Connection conn;
                    lock (gate) {
                        conn = state == Lx200LinkState.Connected ? connection : null;
                    }
                    if (conn != null) {
                        SendOwedStops(conn, "the link is closing");
                    }
                    return;
                }
            }
        }

        /// <summary>How often an idle worker checks that the port is still there.</summary>
        private static readonly TimeSpan IdleCheck = TimeSpan.FromMilliseconds(100);

        /// <summary>Next request in lane order, or null with how long to wait. Called under the lock.</summary>
        private Request Pick(DateTime now, out TimeSpan wait) {
            wait = TimeSpan.FromMilliseconds(500);
            if (stops.First != null) {
                var stop = stops.First.Value;
                stops.RemoveFirst();
                return stop;
            }
            Request dueTimed = null;
            DateTime? nextDue = null;
            foreach (var t in timed) {
                if (t.Due <= now) {
                    if (dueTimed == null || t.Due < dueTimed.Due) {
                        dueTimed = t;
                    }
                } else if (nextDue == null || t.Due < nextDue.Value) {
                    nextDue = t.Due;
                }
            }
            if (dueTimed != null) {
                timed.Remove(dueTimed);
                return dueTimed;
            }
            if (nextDue.HasValue && nextDue.Value - now <= Options.TimedGuard) {
                wait = Clamp(nextDue.Value - now, TimeSpan.FromMilliseconds(1), Options.TimedGuard);
                return null;
            }
            for (var node = commands.First; node != null; node = node.Next) {
                if (timed.Count > 0 && IsLong(node.Value)) {
                    continue;
                }
                commands.Remove(node);
                return node.Value;
            }
            if (polls.First != null) {
                var poll = polls.First.Value;
                polls.RemoveFirst();
                return poll;
            }
            if (nextDue.HasValue) {
                wait = Clamp(nextDue.Value - now - Options.TimedGuard, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(500));
            }
            return null;
        }

        private bool IsLong(Request r) => (r.Timeout ?? r.Command.Spec?.Timeout ?? Lx200CommandSpec.DefaultTimeout) > Options.LongTransaction;

        private void Execute(Request request) {
            if (request.Token.IsCancellationRequested) {
                request.Cancel();
                return;
            }
            Lx200Connection conn;
            lock (gate) {
                conn = connection;
            }
            if (conn == null) {
                request.Fail(new Lx200DisconnectedException($"{request.Command.Text}: the LX200 link is down"));
                return;
            }
            Lx200Reply reply;
            try {
                reply = conn.Send(request.Command, request.Shape, request.Timeout);
            } catch (Exception ex) {
                request.Fail(ex);
                return;
            }
            string lostReason = null;
            if (reply.Status == ReplyStatus.Disconnected || (!conn.IsOpen && !reply.IsOk)) {
                lostReason = reply.Error ?? "the port closed";
            } else {
                TrackOwedStops(request.Command, reply);
                if (reply.IsOk || reply.Status == ReplyStatus.Nak || reply.Resynced) {
                    consecutiveFailures = 0;
                } else if (++consecutiveFailures >= Options.FailuresBeforeLost) {
                    lostReason = $"{consecutiveFailures} transactions in a row without a usable reply, and ACK resync failed";
                }
            }
            if (lostReason != null) {
                MarkLost(lostReason);
                if (request.Lane == Lx200Lane.Stop) {
                    lock (gate) {
                        owedStops.Add(request.Command.Text);
                    }
                }
                request.Fail(new Lx200DisconnectedException($"{request.Command.Text}: the LX200 link on {Name} was lost ({lostReason})"));
                return;
            }
            if (!conn.IsOpen) {
                MarkLost("the port closed");   // the reply made it; the port went right after
            }
            if (request.Lane == Lx200Lane.Stop && reply.Status == ReplyStatus.Nak) {
                var now = UtcNow;
                request.FirstRefusalUtc ??= now;
                if (now - request.FirstRefusalUtc.Value < Options.HaltRetryFor) {
                    lock (gate) {
                        request.Due = now + TimeSpan.FromMilliseconds(100);
                        timed.Add(request);   // the Timed lane runs before any command or poll
                        Monitor.PulseAll(gate);
                    }
                    return;
                }
                var message = $"The mount refused the halt {request.Command.Text} (NAK, busy) for {(now - request.FirstRefusalUtc.Value).TotalSeconds:0.0} s: the mount or the focuser may still be moving. Switch the mount off.";
                Trace.Note("HALT REFUSED: " + message);
                Logger.Error(message);
                if (Options.ShowNotifications) {
                    Notification.ShowError("LX200: " + message);
                }
            }
            request.Complete(reply);
        }

        /// <summary>Remembers host-timed motions that still need a halt; forgets them when the halt is accepted.</summary>
        private void TrackOwedStops(Lx200Command command, Lx200Reply reply) {
            if (!reply.IsOk) {
                return;
            }
            var code = command.Spec?.Code;
            lock (gate) {
                switch (code) {
                    case "Mn":
                    case "Ms":
                    case "Me":
                    case "Mw":
                        owedStops.Add($":Q{code[1]}#");
                        break;
                    case "F+":
                    case "F-":
                        owedStops.Add(":FQ#");
                        break;
                    case "Qn":
                    case "Qs":
                    case "Qe":
                    case "Qw":
                    case "FQ":
                        owedStops.Remove(command.Text);
                        break;
                    case "Q":
                        owedStops.Remove(":Q#");
                        owedStops.Remove(":Qn#");
                        owedStops.Remove(":Qs#");
                        owedStops.Remove(":Qe#");
                        owedStops.Remove(":Qw#");
                        break;
                }
            }
        }

        private void MarkLost(string reason) {
            Lx200Connection old;
            lock (gate) {
                if (state != Lx200LinkState.Connected) {
                    return;
                }
                state = Lx200LinkState.Reconnecting;
                downSinceUtc = UtcNow;
                nextAttemptUtc = downSinceUtc + Options.ReconnectInterval;
                failedAttempts = 0;
                old = connection;
                connection = null;
                Monitor.PulseAll(gate);
            }
            Trace.Note($"LINK LOST on {Name}: {reason}. Reconnecting every {Options.ReconnectInterval.TotalSeconds:0.#} s");
            try {
                old?.Dispose();
            } catch (Exception ex) {
                Trace.Note($"closing the lost port: {ex.Message}");
            }
            Logger.Warning($"LX200 link on {Name} lost ({reason}); reconnecting");
            if (Options.ShowNotifications) {
                Notification.ShowWarning($"LX200: connection to the mount on {Name} lost ({reason}). Reconnecting on its own; plug the cable back in.");
            }
            RaiseStateChanged(Lx200LinkState.Reconnecting);
        }

        private void TryReconnect() {
            Lx200Connection conn = null;
            string error = null;
            try {
                conn = ConnectOnce();
                if (conn == null) {
                    error = "no reply to ACK";
                }
            } catch (Exception ex) {
                error = ex.Message;
            }
            if (conn == null) {
                bool giveUp;
                int attempts;
                TimeSpan down;
                lock (gate) {
                    attempts = ++failedAttempts;
                    var now = UtcNow;
                    down = now - downSinceUtc;
                    nextAttemptUtc = now + Options.ReconnectInterval;
                    giveUp = Options.ReconnectGiveUp.HasValue && down >= Options.ReconnectGiveUp.Value;
                    if (giveUp) {
                        state = Lx200LinkState.Failed;
                        FailAllPending("the LX200 link did not come back");
                    }
                }
                if (attempts <= 3 || attempts % 30 == 0) {
                    Trace.Note($"reconnect attempt {attempts} on {Name} failed: {error}");
                }
                if (giveUp) {
                    var message = $"The mount on {Name} did not come back within {down.TotalMinutes:0.#} min; the LX200 devices are disconnected.";
                    Trace.Note("LINK FAILED: " + message);
                    Logger.Error(message);
                    if (Options.ShowNotifications) {
                        Notification.ShowError("LX200: " + message);
                    }
                    RaiseStateChanged(Lx200LinkState.Failed);
                }
                return;
            }
            SendOwedStops(conn, "the link was lost while they were running");
            TimeSpan downtime;
            lock (gate) {
                if (disposed) {
                    conn.Dispose();
                    return;
                }
                connection = conn;
                state = Lx200LinkState.Connected;
                consecutiveFailures = 0;
                reconnectCount++;
                downtime = UtcNow - downSinceUtc;
                // halts that became owed while the ones above were being sent go out next, ahead of everything else
                foreach (var stop in owedStops) {
                    stops.AddLast(new Request(Lx200Command.Parse(stop), Lx200Lane.Stop, null, null, null, CancellationToken.None, UtcNow, UtcNow));
                }
                Monitor.PulseAll(gate);
            }
            Trace.Note(string.Create(CultureInfo.InvariantCulture, $"LINK RESTORED on {Name} after {downtime.TotalSeconds:0.0} s ({failedAttempts} failed attempt(s))"));
            Logger.Info($"LX200 link on {Name} restored after {downtime.TotalSeconds:0.0} s");
            if (Options.ShowNotifications) {
                Notification.ShowInformation($"LX200: connection to the mount on {Name} restored.");
            }
            RaiseStateChanged(Lx200LinkState.Connected);
        }

        /// <summary>Opens the transport and checks that the mount answers ACK. Null when it does not.</summary>
        private Lx200Connection ConnectOnce() {
            var transport = opener(Trace);
            var conn = new Lx200Connection(transport, Trace, Options.Connection);
            try {
                for (var i = 0; i < 2; i++) {
                    var ack = conn.Ack();
                    if (ack.IsOk) {
                        Trace.Note($"handshake on {Name}: ACK -> '{ack.Value}'");
                        return conn;
                    }
                }
            } catch (Lx200Exception ex) {
                Trace.Note($"handshake on {Name} failed: {ex.Message}");
            }
            conn.Dispose();
            return null;
        }

        private void SendOwedStops(Lx200Connection conn, string why) {
            string[] owed;
            lock (gate) {
                owed = owedStops.OrderBy(s => s == ":FQ#" ? 1 : 0).ThenBy(s => s, StringComparer.Ordinal).ToArray();
            }
            if (owed.Length == 0) {
                return;
            }
            Trace.Note($"sending {owed.Length} owed halt(s) ({why}): {string.Join(" ", owed)}");
            foreach (var stop in owed) {
                try {
                    var cmd = Lx200Command.Parse(stop);
                    var reply = conn.Send(cmd);
                    TrackOwedStops(cmd, reply);
                    lock (gate) {
                        owedStopSent[stop] = reply.SentUtc;
                    }
                    if (stop == ":FQ#") {
                        conn.Send(cmd);   // a single :FQ# is sometimes missed (MN, RVM T1)
                    }
                    if (!reply.IsOk) {
                        Trace.Note($"owed halt {stop} not accepted: {reply.Status}");
                    }
                } catch (Exception ex) {
                    Trace.Note($"owed halt {stop} failed: {ex.Message}");
                }
            }
        }

        /// <summary>While the link is down: polls fail at once, halts become owed, other work fails after WaitForReconnect. Under the lock.</summary>
        private void ExpireWhileDown(DateTime now) {
            foreach (var p in polls) {
                p.Fail(new Lx200DisconnectedException($"{p.Command.Text}: the LX200 link on {Name} is down, reconnecting"));
            }
            polls.Clear();
            foreach (var s in stops) {
                owedStops.Add(s.Command.Text);
                s.Fail(new Lx200DisconnectedException($"{s.Command.Text}: the LX200 link on {Name} is down; the halt is sent as soon as it is back"));
            }
            stops.Clear();
            ExpireList(commands, now);
            for (var i = timed.Count - 1; i >= 0; i--) {
                var t = timed[i];
                if (Expired(t, now)) {
                    timed.RemoveAt(i);
                    t.Fail(DownFor(t));
                }
            }
        }

        private void ExpireList(LinkedList<Request> list, DateTime now) {
            for (var node = list.First; node != null;) {
                var nextNode = node.Next;
                if (Expired(node.Value, now)) {
                    list.Remove(node);
                    node.Value.Fail(DownFor(node.Value));
                }
                node = nextNode;
            }
        }

        private bool Expired(Request r, DateTime now) => now - (r.EnqueuedUtc > downSinceUtc ? r.EnqueuedUtc : downSinceUtc) > Options.WaitForReconnect;

        private Lx200DisconnectedException DownFor(Request r) =>
            new($"{r.Command.Text}: the LX200 link on {Name} has been down for more than {Options.WaitForReconnect.TotalSeconds:0.#} s");

        private void FailAllPending(string why) {
            foreach (var list in new[] { stops, commands, polls }) {
                foreach (var r in list) {
                    r.Fail(new Lx200DisconnectedException($"{r.Command.Text}: {why}"));
                }
                list.Clear();
            }
            foreach (var r in timed) {
                r.Fail(new Lx200DisconnectedException($"{r.Command.Text}: {why}"));
            }
            timed.Clear();
        }

        private void CancelQueued(Request request) {
            bool removed;
            lock (gate) {
                removed = stops.Remove(request) || timed.Remove(request) || commands.Remove(request) || polls.Remove(request);
                if (removed) {
                    Monitor.PulseAll(gate);
                }
            }
            if (removed) {
                request.Cancel();
            }
        }

        private void RaiseStateChanged(Lx200LinkState newState) {
            var handler = StateChanged;
            if (handler == null) {
                return;
            }
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    handler(newState);
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            });
        }

        private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;

        private static Lx200Trace CreateTrace(string name, Lx200LinkOptions options, out string file) {
            file = null;
            if (string.IsNullOrEmpty(options.TraceDirectory)) {
                return new Lx200Trace(maxEntries: options.TraceMemoryEntries);
            }
            try {
                Directory.CreateDirectory(options.TraceDirectory);
                var safe = new string(Path.GetFileName(name).Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '_').ToArray());
                file = Path.Combine(options.TraceDirectory, $"lx200-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{safe}.log");
                var writer = new StreamWriter(file, append: false, new UTF8Encoding(false)) { AutoFlush = true };
                var trace = new Lx200Trace(writer, options.TraceMemoryEntries);
                trace.Note($"LX200 driver trace for {name}, started {DateTime.UtcNow:O} (UTC); columns: UTC time, ms since start, kind, hex, printable ASCII (non-ASCII as '.')");
                return trace;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Logger.Warning($"Cannot write the LX200 trace to {options.TraceDirectory}: {ex.Message}; keeping it in memory");
                file = null;
                return new Lx200Trace(maxEntries: options.TraceMemoryEntries);
            }
        }

        private sealed class Request {

            public Request(Lx200Command command, Lx200Lane lane, ReplyShape shape, TimeSpan? timeout, string coalesceKey, CancellationToken token, DateTime due, DateTime enqueuedUtc) {
                Command = command;
                Lane = lane;
                Shape = shape;
                Timeout = timeout;
                CoalesceKey = coalesceKey;
                Token = token;
                Due = due;
                EnqueuedUtc = enqueuedUtc;
            }

            public Lx200Command Command { get; }

            public Lx200Lane Lane { get; }

            public ReplyShape Shape { get; }

            public TimeSpan? Timeout { get; }

            public string CoalesceKey { get; }

            public CancellationToken Token { get; }

            public DateTime Due { get; set; }

            public DateTime EnqueuedUtc { get; }

            public DateTime? FirstRefusalUtc { get; set; }

            public CancellationTokenRegistration Registration { get; set; }

            private readonly TaskCompletionSource<Lx200Reply> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<Lx200Reply> Task => tcs.Task;

            // Unregister, never Dispose: Dispose waits for a running callback, which may be waiting for the link's lock
            public void Complete(Lx200Reply reply) {
                Registration.Unregister();
                tcs.TrySetResult(reply);
            }

            public void Fail(Exception ex) {
                Registration.Unregister();
                tcs.TrySetException(ex);
            }

            public void Cancel() {
                Registration.Unregister();
                tcs.TrySetCanceled(Token);
            }
        }
    }
}
