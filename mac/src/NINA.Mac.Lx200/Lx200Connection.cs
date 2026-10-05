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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace NINA.Mac.Lx200 {

    public sealed class Lx200ConnectionOptions {

        /// <summary>P07 l.10-13: a busy Autostar II answers NAK within 10 ms; wait and retry.</summary>
        public int NakRetries { get; set; } = 3;

        public TimeSpan NakRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// How long to listen for a NAK after a command that has no reply: 10 ms (P07) + up to 16 ms FTDI
        /// latency (RIM MNT-24) + margin.
        /// </summary>
        public TimeSpan NakWindow { get; set; } = TimeSpan.FromMilliseconds(60);

        /// <summary>Wait for an optional '#' after fixed-length replies (":GW#", ":GH#", ":P#").</summary>
        public TimeSpan OptionalTerminatorGrace { get; set; } = TimeSpan.FromMilliseconds(120);

        /// <summary>
        /// When positive, keep listening this long after each complete reply and report anything that arrives as
        /// <see cref="Lx200Reply.Trailing"/>: proof at the bench that a reply shape is wrong. Not applied after
        /// motion starts and halts that have no reply, so a host-timed move stops on time.
        /// </summary>
        public TimeSpan TrailingWindow { get; set; } = TimeSpan.Zero;

        /// <summary>Minimum quiet time between transactions (INDIGO sleeps 50 ms, INDI 10 ms; RIM MNT-03).</summary>
        public TimeSpan MinimumGap { get; set; } = TimeSpan.FromMilliseconds(20);

        /// <summary>For <see cref="ReplyKind.Drain"/>: the reply is over after this much silence.</summary>
        public TimeSpan DrainQuiet { get; set; } = TimeSpan.FromMilliseconds(300);

        /// <summary>After a failed reply: drain, send ACK, expect one mode character (RIM MNT-02 rec).</summary>
        public bool ResyncOnFailure { get; set; } = true;

        public int MaxReplyLength { get; set; } = 160;
    }

    /// <summary>
    /// Synchronous LX200 link. Each transaction: refuse blocklisted commands, wait the minimum gap, discard stale
    /// input, write, read exactly the catalog's reply shape within the command's timeout, retry on NAK, resync
    /// with ACK after a failure. Every byte is traced. Thread-safe (one transaction at a time).
    /// </summary>
    public sealed class Lx200Connection : IDisposable {

        private static readonly string[] StopCommands = { ":Q#", ":Qn#", ":Qs#", ":Qe#", ":Qw#", ":FQ#", ":FQ#" };

        private readonly object sync = new();
        private readonly ILx200Transport transport;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private TimeSpan lastEnd = TimeSpan.MinValue;
        private int motionCommanded;

        public Lx200Connection(ILx200Transport transport, Lx200Trace trace, Lx200ConnectionOptions options = null) {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Trace = trace ?? new Lx200Trace();
            Options = options ?? new Lx200ConnectionOptions();
        }

        public static Lx200Connection OpenSerial(string portPath, Lx200Trace trace, Lx200ConnectionOptions options = null) {
            trace ??= new Lx200Trace();
            return new Lx200Connection(Lx200Serial.Open(portPath, trace), trace, options);
        }

        public static Lx200Connection OverStream(Stream stream, string name, Lx200Trace trace, Lx200ConnectionOptions options = null) {
            trace ??= new Lx200Trace();
            return new Lx200Connection(new StreamTransport(stream, name, trace), trace, options);
        }

        public Lx200Trace Trace { get; }

        public Lx200ConnectionOptions Options { get; }

        public string PortName => transport.Name;

        public bool IsOpen => transport.IsOpen;

        /// <summary>True from the first motion command (or unknown command) until <see cref="StopAll"/> succeeds.</summary>
        public bool MotionCommanded => Volatile.Read(ref motionCommanded) != 0;

        /// <summary>Raised after every transaction (inside the link lock; keep handlers short).</summary>
        public event Action<Lx200Reply> Transacted;

        /// <summary>Raised just before bytes are written, after the safety checks.</summary>
        public event Action<Lx200Command> Sending;

        public Lx200Reply Send(string command, ReplyShape shape = null, TimeSpan? timeout = null) =>
            Send(Lx200Command.Parse(command), shape, timeout);

        public Lx200Reply Send(Lx200Command command, ReplyShape shape = null, TimeSpan? timeout = null) {
            ArgumentNullException.ThrowIfNull(command);
            if (command.Spec?.IsBlocked == true) {
                Trace.Log(TraceKind.Error, ReadOnlySpan<byte>.Empty, $"REFUSED {command.Text} (blocklist, nothing written): {command.Spec.BlockedReason}");
                throw new Lx200BlockedCommandException(command.Text, command.Spec);
            }
            shape ??= command.Spec?.Reply ?? ReplyShape.Drain;
            var limit = timeout ?? command.Spec?.Timeout ?? Lx200CommandSpec.DefaultTimeout;

            lock (sync) {
                var reply = new Lx200Reply(command, shape);
                try {
                    Transact(command, shape, limit, reply);
                } catch (Lx200DisconnectedException ex) {
                    reply.Status = ReplyStatus.Disconnected;
                    reply.Error = ex.Message;
                    Trace.Log(TraceKind.Error, ReadOnlySpan<byte>.Empty, $"{command.Text}: disconnected: {ex.Message}");
                }
                if (!reply.IsOk && reply.Status != ReplyStatus.Disconnected && Options.ResyncOnFailure && transport.IsOpen) {
                    try {
                        reply.Resynced = ResyncCore();
                    } catch (Lx200DisconnectedException ex) {
                        reply.Error = (reply.Error == null ? "" : reply.Error + "; ") + "resync: " + ex.Message;
                    }
                }
                lastEnd = clock.Elapsed;
                Transacted?.Invoke(reply);
                return reply;
            }
        }

        /// <summary>Sends ACK (0x06): one character A/L/P/D.</summary>
        public Lx200Reply Ack() => Send(Lx200Command.FromBytes(new[] { ReplyShape.Ack }));

        /// <summary>Sends a command and returns its first part; throws when the reply is not complete.</summary>
        public string Query(string command, TimeSpan? timeout = null) {
            var reply = Send(command, timeout: timeout);
            if (!reply.IsOk) {
                throw new Lx200ReplyException($"{command}: {reply.Status} ({reply.Describe()})", reply);
            }
            return reply.Value;
        }

        /// <summary>Sends a query and parses it; a parse failure (garbage, desync) triggers a resync and one more try.</summary>
        public (T Value, Lx200Reply Reply) Query<T>(string command, Func<string, T> parse, int attempts = 2) {
            Exception last = null;
            Lx200Reply reply = null;
            for (var i = 0; i < Math.Max(1, attempts); i++) {
                reply = Send(command);
                if (reply.IsOk) {
                    try {
                        return (parse(reply.Value), reply);
                    } catch (FormatException ex) {
                        last = ex;
                        reply.Error = ex.Message;
                        Trace.Log(TraceKind.Error, ReadOnlySpan<byte>.Empty, $"{command}: unparseable reply \"{Lx200Format.Printable(reply.Value)}\": {ex.Message}");
                        lock (sync) {
                            reply.Resynced = ResyncCore();
                        }
                    }
                } else {
                    last = new Lx200ReplyException($"{command}: {reply.Status}", reply);
                }
            }
            throw new Lx200ReplyException($"{command}: no valid reply after {attempts} attempt(s): {last?.Message}", reply, last);
        }

        /// <summary>Drain, send ACK, expect one mode character, drain again. True when the link answers sanely.</summary>
        public bool Resync() {
            lock (sync) {
                return ResyncCore();
            }
        }

        /// <summary>
        /// Best-effort stop of every motion: ":Q#", the four direction stops and ":FQ#" twice (MN notes a single
        /// :FQ# is sometimes missed). Never throws. If another thread holds the link for more than 3 s the stop
        /// bytes are written directly.
        /// </summary>
        /// <returns>
        /// False when a stop could not be written or the mount refused one (NAK after every retry): the mount or the
        /// focuser may still be moving, and <see cref="MotionCommanded"/> stays set.
        /// </returns>
        public bool StopAll(string reason) {
            var taken = false;
            try {
                Monitor.TryEnter(sync, TimeSpan.FromSeconds(3), ref taken);
                Trace.Note($"STOP ALL ({reason}){(taken ? "" : " - link busy, writing stop bytes directly")}");
                var allSent = true;
                var trailing = Options.TrailingWindow;
                Options.TrailingWindow = TimeSpan.Zero;   // stops go out back to back; no listening for stray bytes
                foreach (var c in StopCommands) {
                    try {
                        if (taken) {
                            var r = Send(c);
                            allSent &= r.Status is ReplyStatus.NoReplyExpected or ReplyStatus.FramingError;
                        } else {
                            transport.Write(Lx200Format.Latin1.GetBytes(c));
                            Thread.Sleep(30);
                        }
                    } catch (Exception ex) {
                        allSent = false;
                        Trace.Log(TraceKind.Error, ReadOnlySpan<byte>.Empty, $"stop {c} failed: {ex.Message}");
                    }
                }
                Options.TrailingWindow = trailing;
                if (allSent) {
                    Volatile.Write(ref motionCommanded, 0);
                } else {
                    Trace.Note("STOP ALL: not every stop was accepted; motion may continue");
                }
                return allSent;
            } catch (Exception ex) {
                Trace.Log(TraceKind.Error, ReadOnlySpan<byte>.Empty, $"STOP ALL failed: {ex.Message}");
                return false;
            } finally {
                if (taken) {
                    Monitor.Exit(sync);
                }
            }
        }

        private void Transact(Lx200Command command, ReplyShape shape, TimeSpan limit, Lx200Reply reply) {
            if (lastEnd != TimeSpan.MinValue) {
                var gapLeft = Options.MinimumGap - (clock.Elapsed - lastEnd);
                if (gapLeft > TimeSpan.Zero) {
                    Thread.Sleep(gapLeft);
                }
            }
            var stale = new List<byte>();
            var raw = new List<byte>();
            var parts = new List<string>();
            var trailing = new List<byte>();
            for (var attempt = 0; ; attempt++) {
                var old = transport.DrainAvailable();
                if (old.Length > 0) {
                    Trace.Log(TraceKind.Discard, old, $"{old.Length} stale byte(s) dropped before {command.Text}");
                    stale.AddRange(old);
                }
                Sending?.Invoke(command);
                if (command.Effect == CommandEffect.MovesHardware) {
                    Volatile.Write(ref motionCommanded, 1);
                }
                reply.SentUtc = Trace.UtcNow;
                transport.Write(command.Bytes);
                var sw = Stopwatch.StartNew();
                raw.Clear();
                parts.Clear();
                trailing.Clear();
                var reader = new ReplyReader(transport, sw, limit, Options);
                var status = reader.Read(shape, raw, parts, trailing);
                reply.FirstByteMs = reader.FirstByteMs;
                reply.TerminatorSeen = reader.TerminatorSeen;
                reply.TotalMs = sw.Elapsed.TotalMilliseconds;
                if (status == ReplyStatus.Nak) {
                    reply.NakCount++;
                    if (attempt < Options.NakRetries) {
                        Trace.Note($"{command.Text}: NAK (0x15, mount busy) - retry {attempt + 1}/{Options.NakRetries} in {Options.NakRetryDelay.TotalMilliseconds:0} ms");
                        Thread.Sleep(Options.NakRetryDelay);
                        continue;
                    }
                }
                // Motion starts and halts are timed by the host (:F+# ... :FQ#, :Mn# ... :Qn#): listening after them
                // would push the halt late. A byte sent back to them still shows up in the NAK window (framing
                // error) or as stale input before the next command.
                var timed = status == ReplyStatus.NoReplyExpected && command.Effect is CommandEffect.MovesHardware or CommandEffect.Halt;
                if (status is ReplyStatus.Ok or ReplyStatus.NoReplyExpected && Options.TrailingWindow > TimeSpan.Zero && !timed) {
                    trailing.AddRange(reader.Quiet(Options.TrailingWindow));
                }
                reply.Status = status;
                reply.Error = reader.Error;
                break;
            }
            reply.Raw = raw.ToArray();
            reply.Parts = parts.ToArray();
            reply.Stale = stale.ToArray();
            reply.Trailing = trailing.ToArray();
            if (reply.Trailing.Length > 0) {
                reply.Error ??= $"{reply.Trailing.Length} unexpected byte(s) after the reply";
            }
            Trace.Log(TraceKind.Reply, ReadOnlySpan<byte>.Empty,
                string.Create(CultureInfo.InvariantCulture,
                    $"{command.Text} shape=[{shape}] status={reply.Status} reply=\"{Lx200Format.Printable(reply.RawText)}\" first={(double.IsNaN(reply.FirstByteMs) ? "-" : reply.FirstByteMs.ToString("0.0", CultureInfo.InvariantCulture))}ms total={reply.TotalMs:0.0}ms{(reply.NakCount > 0 ? $" naks={reply.NakCount}" : "")}{(reply.Trailing.Length > 0 ? $" TRAILING=[{Lx200Trace.Hex(reply.Trailing)}]" : "")}{(reply.Error != null ? " error=" + reply.Error : "")}"));
        }

        private bool ResyncCore() {
            Trace.Note("resync: drain until quiet, send ACK (0x06), expect one of A/L/P/D");
            var junk = new List<byte>();
            junk.AddRange(transport.DrainAvailable());
            for (int b; (b = transport.ReadByte(TimeSpan.FromMilliseconds(100))) >= 0 && junk.Count < 4096;) {
                junk.Add((byte)b);
            }
            if (junk.Count > 0) {
                Trace.Log(TraceKind.Discard, junk.ToArray(), $"resync drained {junk.Count} byte(s)");
            }
            transport.Write(new[] { ReplyShape.Ack });
            var mode = transport.ReadByte(TimeSpan.FromSeconds(1));
            var ok = mode >= 0 && ReplyShape.MountMode.ValidFirstChars.IndexOf((char)mode) >= 0;
            Thread.Sleep(50);
            var after = transport.DrainAvailable();
            if (after.Length > 0) {
                Trace.Log(TraceKind.Discard, after, "after resync ACK");
            }
            Trace.Note(ok ? $"resync ok (ACK -> '{(char)mode}')" : $"resync FAILED (ACK -> {(mode < 0 ? "nothing" : $"0x{mode:X2}")})");
            lastEnd = clock.Elapsed;
            return ok;
        }

        public void Dispose() {
            transport.Dispose();
        }

        /// <summary>Reads one reply according to its shape.</summary>
        private sealed class ReplyReader {
            private readonly ILx200Transport transport;
            private readonly Stopwatch sw;
            private readonly TimeSpan limit;
            private readonly Lx200ConnectionOptions options;

            public ReplyReader(ILx200Transport transport, Stopwatch sw, TimeSpan limit, Lx200ConnectionOptions options) {
                this.transport = transport;
                this.sw = sw;
                this.limit = limit;
                this.options = options;
            }

            public double FirstByteMs { get; private set; } = double.NaN;

            public bool TerminatorSeen { get; private set; }

            public string Error { get; private set; }

            public ReplyStatus Read(ReplyShape shape, List<byte> raw, List<string> parts, List<byte> trailing) {
                if (shape.Kind == ReplyKind.None) {
                    var b = Next(options.NakWindow);
                    if (b < 0) {
                        return ReplyStatus.NoReplyExpected;
                    }
                    if (b == ReplyShape.Nak) {
                        return ReplyStatus.Nak;
                    }
                    raw.Add((byte)b);
                    raw.AddRange(Quiet(TimeSpan.FromMilliseconds(100)));
                    Error = "bytes came back for a command that should have no reply";
                    return ReplyStatus.FramingError;
                }

                var first = Next(limit);
                if (first < 0) {
                    return ReplyStatus.Silent;
                }
                if (first == ReplyShape.Nak) {
                    return ReplyStatus.Nak;
                }

                switch (shape.Kind) {
                    case ReplyKind.Char:
                        raw.Add((byte)first);
                        parts.Add(((char)first).ToString());
                        return CheckFirst(shape, first);

                    case ReplyKind.Terminated: {
                        var status = ReadTerminated(first, raw, out var text);
                        if (status == ReplyStatus.Ok) {
                            parts.Add(text);
                        }
                        return status;
                    }

                    case ReplyKind.Fixed:
                    case ReplyKind.FixedThenOptionalHash: {
                        raw.Add((byte)first);
                        while (raw.Count < shape.Length) {
                            var b = Next(limit);
                            if (b < 0) {
                                return ReplyStatus.Partial;
                            }
                            raw.Add((byte)b);
                        }
                        parts.Add(Lx200Format.Latin1.GetString(raw.ToArray()));
                        return shape.Kind == ReplyKind.Fixed ? ReplyStatus.Ok : OptionalHash(raw, trailing);
                    }

                    case ReplyKind.OneOf: {
                        raw.Add((byte)first);
                        while (true) {
                            var text = Lx200Format.Latin1.GetString(raw.ToArray());
                            if (shape.Alternatives.Contains(text)) {
                                parts.Add(text);
                                return OptionalHash(raw, trailing);
                            }
                            if (!shape.Alternatives.Any(a => a.StartsWith(text, StringComparison.Ordinal))) {
                                raw.AddRange(Quiet(TimeSpan.FromMilliseconds(100)));
                                Error = $"\"{Lx200Format.Printable(text)}\" is not a prefix of any expected reply";
                                return ReplyStatus.FramingError;
                            }
                            var b = Next(limit);
                            if (b < 0) {
                                return ReplyStatus.Partial;
                            }
                            raw.Add((byte)b);
                        }
                    }

                    case ReplyKind.CharThenTerminatedIfNotZero: {
                        raw.Add((byte)first);
                        parts.Add(((char)first).ToString());
                        var check = CheckFirst(shape, first);
                        if (check != ReplyStatus.Ok || first == '0') {
                            return check;
                        }
                        var next = Next(limit);
                        if (next < 0) {
                            return ReplyStatus.Partial;
                        }
                        var status = ReadTerminated(next, raw, out var message);
                        if (status == ReplyStatus.Ok) {
                            parts.Add(message);
                        }
                        return status;
                    }

                    case ReplyKind.CharThenTwoTerminatedIfOne: {
                        raw.Add((byte)first);
                        parts.Add(((char)first).ToString());
                        var check = CheckFirst(shape, first);
                        if (check != ReplyStatus.Ok || first == '0') {
                            return check;
                        }
                        for (var i = 0; i < 2; i++) {
                            var next = Next(limit);
                            if (next < 0) {
                                return ReplyStatus.Partial;
                            }
                            var status = ReadTerminated(next, raw, out var s);
                            if (status != ReplyStatus.Ok) {
                                return status;
                            }
                            parts.Add(s);
                        }
                        return ReplyStatus.Ok;
                    }

                    case ReplyKind.UntilDegreeThenOptionalHash: {
                        raw.Add((byte)first);
                        while (raw[^1] != (byte)'*' && raw[^1] != Lx200Format.DegreeByte) {
                            if (raw.Count >= 8) {
                                Error = "no degree sign within 8 bytes";
                                return ReplyStatus.FramingError;
                            }
                            var b = Next(limit);
                            if (b < 0) {
                                return ReplyStatus.Partial;
                            }
                            raw.Add((byte)b);
                        }
                        parts.Add(Lx200Format.Latin1.GetString(raw.ToArray()));
                        return OptionalHash(raw, trailing);
                    }

                    case ReplyKind.Drain: {
                        raw.Add((byte)first);
                        raw.AddRange(Quiet(options.DrainQuiet));
                        parts.Add(Lx200Format.Latin1.GetString(raw.ToArray()));
                        return ReplyStatus.Ok;
                    }

                    default:
                        throw new InvalidOperationException($"Unhandled reply kind {shape.Kind}");
                }
            }

            /// <summary>Collects bytes until nothing arrives for <paramref name="quiet"/>.</summary>
            public List<byte> Quiet(TimeSpan quiet) {
                var got = new List<byte>();
                for (int b; got.Count < 4096 && (b = transport.ReadByte(quiet)) >= 0;) {
                    got.Add((byte)b);
                }
                return got;
            }

            private ReplyStatus ReadTerminated(int first, List<byte> raw, out string text) {
                var start = raw.Count;
                var b = first;
                while (true) {
                    raw.Add((byte)b);
                    if (b == ReplyShape.Hash) {
                        text = Lx200Format.Latin1.GetString(raw.ToArray(), start, raw.Count - start - 1);
                        return ReplyStatus.Ok;
                    }
                    if (raw.Count - start >= options.MaxReplyLength) {
                        text = null;
                        Error = $"no '#' within {options.MaxReplyLength} bytes";
                        return ReplyStatus.FramingError;
                    }
                    b = Next(limit);
                    if (b < 0) {
                        text = null;
                        Error = "reply stopped before '#'";
                        return ReplyStatus.Partial;
                    }
                }
            }

            private ReplyStatus OptionalHash(List<byte> raw, List<byte> trailing) {
                var b = transport.ReadByte(options.OptionalTerminatorGrace);
                if (b < 0) {
                    return ReplyStatus.Ok;
                }
                if (b == ReplyShape.Hash) {
                    raw.Add((byte)b);
                    TerminatorSeen = true;
                    return ReplyStatus.Ok;
                }
                trailing.Add((byte)b);
                trailing.AddRange(Quiet(TimeSpan.FromMilliseconds(100)));
                Error = "unexpected byte after a fixed-length reply";
                return ReplyStatus.FramingError;
            }

            private ReplyStatus CheckFirst(ReplyShape shape, int first) {
                if (shape.ValidFirstChars != null && shape.ValidFirstChars.IndexOf((char)first) < 0) {
                    Error = $"first byte 0x{first:X2} is not one of [{shape.ValidFirstChars}]";
                    return ReplyStatus.FramingError;
                }
                return ReplyStatus.Ok;
            }

            private int Next(TimeSpan untilElapsed) {
                var left = untilElapsed - sw.Elapsed;
                var b = transport.ReadByte(left > TimeSpan.Zero ? left : TimeSpan.Zero);
                if (b >= 0 && double.IsNaN(FirstByteMs)) {
                    FirstByteMs = sw.Elapsed.TotalMilliseconds;
                }
                return b;
            }
        }
    }
}
