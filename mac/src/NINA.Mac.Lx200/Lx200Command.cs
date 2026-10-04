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

namespace NINA.Mac.Lx200 {

    public class Lx200Exception : Exception {

        public Lx200Exception(string message, Exception inner = null) : base(message, inner) {
        }
    }

    /// <summary>The safety layer refused a command; nothing was written.</summary>
    public sealed class Lx200BlockedCommandException : Lx200Exception {

        public Lx200BlockedCommandException(string command, Lx200CommandSpec spec)
            : base($"Refused {command}: {spec.Purpose}. {spec.BlockedReason} ({spec.Source})") {
            Command = command;
            Spec = spec;
        }

        public string Command { get; }

        public Lx200CommandSpec Spec { get; }
    }

    /// <summary>Malformed outgoing bytes (not exactly one ':'..'#' command or a lone ACK).</summary>
    public sealed class Lx200MalformedCommandException : Lx200Exception {

        public Lx200MalformedCommandException(string message) : base(message) {
        }
    }

    public sealed class Lx200DisconnectedException : Lx200Exception {

        public Lx200DisconnectedException(string message, Exception inner = null) : base(message, inner) {
        }
    }

    /// <summary>A reply was missing, incomplete, malformed or could not be parsed.</summary>
    public sealed class Lx200ReplyException : Lx200Exception {

        public Lx200ReplyException(string message, Lx200Reply reply, Exception inner = null) : base(message, inner) {
            Reply = reply;
        }

        public Lx200Reply Reply { get; }
    }

    /// <summary>
    /// One outgoing command, validated: exactly one ':'..'#' command of printable ASCII, or the lone ACK byte.
    /// Packing several commands into one write would let a blocked command ride along (":Q#:hP#"), so an inner '#'
    /// is refused and an inner ':' segment is checked against the blocklist.
    /// </summary>
    public sealed class Lx200Command {

        private Lx200Command(byte[] bytes, string body, Lx200CommandSpec spec) {
            Bytes = bytes;
            Body = body;
            Spec = spec;
        }

        public byte[] Bytes { get; }

        /// <summary>Text between ':' and '#' (empty for ACK).</summary>
        public string Body { get; }

        /// <summary>Catalog entry, or null for a command the catalog does not know.</summary>
        public Lx200CommandSpec Spec { get; }

        public bool IsAck => Bytes.Length == 1 && Bytes[0] == ReplyShape.Ack;

        public bool IsKnown => Spec != null;

        /// <summary>Unknown commands are treated as the worst case: they may move hardware.</summary>
        public CommandEffect Effect => Spec?.Effect ?? CommandEffect.MovesHardware;

        public bool NeedsConfirmation => Spec?.NeedsConfirmation ?? true;

        public string Text => IsAck ? "ACK(0x06)" : Lx200Format.Latin1.GetString(Bytes);

        public override string ToString() => Text;

        /// <summary>
        /// Accepts ":GR#", "GR" (':' and '#' added), "ack" or "\x06". Throws on anything that is not a single command.
        /// </summary>
        public static Lx200Command Parse(string text) {
            if (text == null) {
                throw new Lx200MalformedCommandException("No command");
            }
            var t = text.Trim();
            if (t.Length == 0) {
                throw new Lx200MalformedCommandException("Empty command");
            }
            if (t == "\u0006" || string.Equals(t, "ack", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "0x06", StringComparison.OrdinalIgnoreCase)) {
                return FromBytes(new[] { ReplyShape.Ack });
            }
            if (!t.StartsWith(':')) {
                t = ":" + t;
            }
            if (!t.EndsWith('#')) {
                t += "#";
            }
            foreach (var c in t) {
                if (c > 0xFF) {
                    throw new Lx200MalformedCommandException($"Non-Latin-1 character in '{text}'");
                }
            }
            return FromBytes(Lx200Format.Latin1.GetBytes(t));
        }

        public static Lx200Command FromBytes(byte[] bytes) {
            if (bytes == null || bytes.Length == 0) {
                throw new Lx200MalformedCommandException("Empty command");
            }
            if (bytes.Length == 1 && bytes[0] == ReplyShape.Ack) {
                return new Lx200Command(bytes, "", Lx200Catalog.Ack);
            }
            if (bytes.Length < 3 || bytes[0] != (byte)':' || bytes[^1] != ReplyShape.Hash) {
                throw new Lx200MalformedCommandException($"Not a single ':...#' command: {Lx200Trace.Hex(bytes)}");
            }
            if (bytes.Length > 64) {
                throw new Lx200MalformedCommandException($"Command too long ({bytes.Length} bytes)");
            }
            for (var i = 1; i < bytes.Length - 1; i++) {
                var b = bytes[i];
                if (b == ReplyShape.Hash) {
                    throw new Lx200MalformedCommandException($"More than one command in one write: '{Lx200Format.Latin1.GetString(bytes)}'");
                }
                if (b < 0x20 || b > 0x7E) {
                    throw new Lx200MalformedCommandException($"Non-printable byte 0x{b:X2} in command '{Lx200Format.Printable(Lx200Format.Latin1.GetString(bytes))}'");
                }
            }
            var body = Lx200Format.Latin1.GetString(bytes, 1, bytes.Length - 2);
            var spec = Lx200Catalog.Match(body);
            // ':' is legal inside arguments (":SL01:00:00#"), but if a firmware restarted its parser at an inner ':'
            // then ":Q:hP#" would park the mount. Treat any inner segment that matches a blocked code as blocked.
            for (var i = body.IndexOf(':'); i >= 0 && spec?.IsBlocked != true; i = body.IndexOf(':', i + 1)) {
                var inner = Lx200Catalog.Match(body[(i + 1)..]);
                if (inner?.IsBlocked == true) {
                    spec = inner;
                }
            }
            return new Lx200Command(bytes, body, spec);
        }
    }
}
