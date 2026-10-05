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
using System.Text;

namespace NINA.Mac.Lx200 {

    public enum TraceKind {

        /// <summary>Bytes written to the mount.</summary>
        Tx,

        /// <summary>Bytes received, logged when they arrive.</summary>
        Rx,

        /// <summary>Stale bytes thrown away before a command (evidence of desync).</summary>
        Discard,

        /// <summary>One line per transaction: command, shape, status, timing.</summary>
        Reply,

        Note,

        Error
    }

    public sealed class TraceEntry {

        public TraceEntry(DateTime utc, double elapsedMs, TraceKind kind, byte[] bytes, string text) {
            Utc = utc;
            ElapsedMs = elapsedMs;
            Kind = kind;
            Bytes = bytes ?? Array.Empty<byte>();
            Text = text;
        }

        public DateTime Utc { get; }

        /// <summary>Monotonic milliseconds since the trace started (Stopwatch based).</summary>
        public double ElapsedMs { get; }

        public TraceKind Kind { get; }

        public byte[] Bytes { get; }

        public string Text { get; }
    }

    /// <summary>
    /// Timestamped byte-level serial trace: direction, hex and printable ASCII. Thread-safe; RX lines are written
    /// by the transport's reader thread at arrival, so the log shows real latencies.
    /// </summary>
    public sealed class Lx200Trace : IDisposable {
        private readonly object sync = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly DateTime startUtc;
        private readonly List<TraceEntry> entries = new();
        private readonly int maxEntries;
        private TextWriter writer;

        /// <param name="startUtc">
        /// What <see cref="UtcNow"/> reads at the start (default: the system clock). Tests set it to run the probe at a
        /// chosen time of day, e.g. just after local midnight for the date test.
        /// </param>
        public Lx200Trace(TextWriter writer = null, int maxEntries = 200_000, DateTime? startUtc = null) {
            this.writer = writer;
            this.maxEntries = maxEntries;
            this.startUtc = startUtc ?? DateTime.UtcNow;
        }

        public static Lx200Trace ToFile(string path) {
            var w = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            var t = new Lx200Trace(w);
            t.Note($"trace started {DateTime.UtcNow:O} (UTC); columns: UTC time, ms since start, kind, hex, printable ASCII (non-ASCII as '.')");
            return t;
        }

        /// <summary>Optional live echo (e.g. the raw console prints every byte line).</summary>
        public Action<string> Echo { get; set; }

        public DateTime UtcNow => startUtc + clock.Elapsed;

        public double ElapsedMs => clock.Elapsed.TotalMilliseconds;

        public void Log(TraceKind kind, ReadOnlySpan<byte> bytes, string text = null) {
            string line;
            lock (sync) {
                var e = new TraceEntry(UtcNow, ElapsedMs, kind, bytes.ToArray(), text);
                if (entries.Count < maxEntries) {
                    entries.Add(e);
                }
                line = Format(e);
                writer?.WriteLine(line);
            }
            Echo?.Invoke(line);
        }

        public void Note(string text) => Log(TraceKind.Note, ReadOnlySpan<byte>.Empty, text);

        public IReadOnlyList<TraceEntry> Snapshot() {
            lock (sync) {
                return entries.ToArray();
            }
        }

        public static string Format(TraceEntry e) {
            var sb = new StringBuilder();
            sb.Append(e.Utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            sb.Append(string.Create(CultureInfo.InvariantCulture, $" {e.ElapsedMs,12:0.000}ms "));
            sb.Append(e.Kind switch {
                TraceKind.Tx => "TX     ",
                TraceKind.Rx => "RX     ",
                TraceKind.Discard => "DISCARD",
                TraceKind.Reply => "REPLY  ",
                TraceKind.Error => "ERROR  ",
                _ => "NOTE   "
            });
            if (e.Bytes.Length > 0) {
                sb.Append(' ').Append(Hex(e.Bytes)).Append("  |").Append(Ascii(e.Bytes)).Append('|');
            }
            if (!string.IsNullOrEmpty(e.Text)) {
                sb.Append(' ').Append(e.Text);
            }
            return sb.ToString();
        }

        public static string Hex(ReadOnlySpan<byte> bytes) {
            if (bytes.IsEmpty) {
                return "";
            }
            var sb = new StringBuilder(bytes.Length * 3);
            foreach (var b in bytes) {
                if (sb.Length > 0) {
                    sb.Append(' ');
                }
                sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Printable ASCII with every other byte (control codes, 0xDF, 0x7F) shown as '.'.</summary>
        public static string Ascii(ReadOnlySpan<byte> bytes) {
            var chars = new char[bytes.Length];
            for (var i = 0; i < bytes.Length; i++) {
                chars[i] = bytes[i] >= 0x20 && bytes[i] < 0x7F ? (char)bytes[i] : '.';
            }
            return new string(chars);
        }

        public void Dispose() {
            lock (sync) {
                writer?.Flush();
                writer?.Dispose();
                writer = null;
            }
        }
    }
}
