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

namespace NINA.Mac.Lx200 {

    public enum ReplyStatus {

        /// <summary>The reply matched its shape.</summary>
        Ok,

        /// <summary>The command has no reply and no NAK came back.</summary>
        NoReplyExpected,

        /// <summary>Not a single byte arrived before the timeout.</summary>
        Silent,

        /// <summary>Some bytes arrived, then the reply stopped before it was complete.</summary>
        Partial,

        /// <summary>The bytes do not fit the expected shape (e.g. 'X' where '0'/'1' was expected).</summary>
        FramingError,

        /// <summary>The mount kept answering NAK (0x15, busy) after every retry.</summary>
        Nak,

        Disconnected
    }

    /// <summary>Everything about one transaction, kept for the results file.</summary>
    public sealed class Lx200Reply {

        internal Lx200Reply(Lx200Command command, ReplyShape shape) {
            Command = command;
            Shape = shape;
        }

        public Lx200Command Command { get; }

        public ReplyShape Shape { get; }

        public ReplyStatus Status { get; internal set; }

        /// <summary>Reply bytes exactly as received (terminators included, NAKs excluded).</summary>
        public byte[] Raw { get; internal set; } = Array.Empty<byte>();

        /// <summary>Reply split into its parts without '#': [text], [digit, message], ['1', s1, s2].</summary>
        public IReadOnlyList<string> Parts { get; internal set; } = Array.Empty<string>();

        /// <summary>For optional-'#' shapes: whether the '#' came.</summary>
        public bool TerminatorSeen { get; internal set; }

        /// <summary>Bytes discarded from the input just before this command was written.</summary>
        public byte[] Stale { get; internal set; } = Array.Empty<byte>();

        /// <summary>Unexpected bytes that arrived after the reply was complete (only collected when a trailing window is set).</summary>
        public byte[] Trailing { get; internal set; } = Array.Empty<byte>();

        public int NakCount { get; internal set; }

        public DateTime SentUtc { get; internal set; }

        /// <summary>Time from the end of the write to the first reply byte (NaN when none came).</summary>
        public double FirstByteMs { get; internal set; } = double.NaN;

        /// <summary>Time from the end of the write to the last byte of the reply.</summary>
        public double TotalMs { get; internal set; }

        public bool Resynced { get; internal set; }

        public string Error { get; internal set; }

        public bool IsOk => Status is ReplyStatus.Ok or ReplyStatus.NoReplyExpected;

        /// <summary>First part, e.g. "05:35:17" for :GR#, "1" for :St.</summary>
        public string Value => Parts.Count > 0 ? Parts[0] : "";

        /// <summary>All parts joined, e.g. "1Object Below Horizon" for :MS#.</summary>
        public string Joined => string.Concat(Parts);

        /// <summary>Raw bytes as Latin-1 text (0xDF stays U+00DF).</summary>
        public string RawText => Lx200Format.Latin1.GetString(Raw);

        public string RawHex => Lx200Trace.Hex(Raw);

        public string Describe() {
            var parts = new List<string> {
                $"{Command.Text} -> {Status}",
                Raw.Length > 0 ? $"[{RawHex}] \"{Lx200Format.Printable(RawText)}\"" : "(no bytes)",
                string.Create(CultureInfo.InvariantCulture, $"first {(double.IsNaN(FirstByteMs) ? "-" : FirstByteMs.ToString("0.0", CultureInfo.InvariantCulture))} ms, total {TotalMs:0.0} ms")
            };
            if (NakCount > 0) {
                parts.Add($"{NakCount} NAK");
            }
            if (Stale.Length > 0) {
                parts.Add($"discarded stale [{Lx200Trace.Hex(Stale)}]");
            }
            if (Trailing.Length > 0) {
                parts.Add($"TRAILING [{Lx200Trace.Hex(Trailing)}]");
            }
            if (Resynced) {
                parts.Add("resynced");
            }
            if (Error != null) {
                parts.Add(Error);
            }
            return string.Join("; ", parts.Where(p => p.Length > 0));
        }

        public override string ToString() => Describe();
    }
}
