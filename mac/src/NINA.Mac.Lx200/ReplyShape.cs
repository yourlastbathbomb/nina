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
using System.Linq;

namespace NINA.Mac.Lx200 {

    /// <summary>
    /// How many bytes a command's reply has and how it ends. The LX200 protocol has no common framing
    /// (research/rig_investigate_mount.md MNT-02 lists six shapes), so every command needs its own entry;
    /// a wrong guess desynchronises every later reply (plan risk 6).
    /// </summary>
    public enum ReplyKind {

        /// <summary>Nothing comes back (":Q#", ":Mn#", ":F+#", ":U#"). A NAK (0x15) may still arrive within 10 ms.</summary>
        None,

        /// <summary>Exactly one byte and no '#': ACK answers A/L/P/D, setters answer 1/0.</summary>
        Char,

        /// <summary>Bytes up to and including '#' (":GR#", ":D#", ":CM#", ":GVN#").</summary>
        Terminated,

        /// <summary>Exactly <see cref="ReplyShape.Length"/> bytes, no '#'.</summary>
        Fixed,

        /// <summary><see cref="ReplyShape.Length"/> bytes, then a '#' that may or may not follow (":GW#", ":GH#").</summary>
        FixedThenOptionalHash,

        /// <summary>One of a fixed set of literal strings, no '#' (":P#" answers "HIGH PRECISION" or "LOW PRECISION").</summary>
        OneOf,

        /// <summary>":MS#": '0' alone on success, otherwise a digit then a '#'-terminated message.</summary>
        CharThenTerminatedIfNotZero,

        /// <summary>":SC": '0' alone for an invalid date, otherwise '1' then two '#'-terminated strings.</summary>
        CharThenTwoTerminatedIfOne,

        /// <summary>":Gh#"/":Go#": digits up to the degree sign ('*' or 0xDF), then an optional '#'.</summary>
        UntilDegreeThenOptionalHash,

        /// <summary>Unknown shape (raw console): everything that arrives until the line has been quiet for a while.</summary>
        Drain
    }

    public sealed class ReplyShape {
        public const byte Hash = (byte)'#';
        public const byte Ack = 0x06;
        public const byte Nak = 0x15;

        private ReplyShape(ReplyKind kind, int length = 0, IReadOnlyList<string> alternatives = null, string validFirstChars = null) {
            Kind = kind;
            Length = length;
            Alternatives = alternatives ?? Array.Empty<string>();
            ValidFirstChars = validFirstChars;
        }

        public ReplyKind Kind { get; }

        /// <summary>Byte count for <see cref="ReplyKind.Fixed"/> and <see cref="ReplyKind.FixedThenOptionalHash"/>.</summary>
        public int Length { get; }

        /// <summary>Literal replies for <see cref="ReplyKind.OneOf"/>.</summary>
        public IReadOnlyList<string> Alternatives { get; }

        /// <summary>When set, the first reply byte must be one of these characters or the reply is a framing error.</summary>
        public string ValidFirstChars { get; }

        public bool ExpectsReply => Kind != ReplyKind.None;

        public static ReplyShape None { get; } = new(ReplyKind.None);

        /// <summary>One unterminated character, any value.</summary>
        public static ReplyShape AnyChar { get; } = new(ReplyKind.Char);

        /// <summary>One unterminated '1' (valid) or '0' (invalid), the answer of every :S setter.</summary>
        public static ReplyShape Bool { get; } = new(ReplyKind.Char, validFirstChars: "01");

        /// <summary>ACK (0x06) answer: A alt-az, L land, P polar, D updater (P07 l.51-57); G (German equatorial, other Meade models) is tolerated.</summary>
        public static ReplyShape MountMode { get; } = new(ReplyKind.Char, validFirstChars: "ALPDG");

        public static ReplyShape Terminated { get; } = new(ReplyKind.Terminated);

        public static ReplyShape SlewResult { get; } = new(ReplyKind.CharThenTerminatedIfNotZero, validFirstChars: "0123");

        public static ReplyShape DateResult { get; } = new(ReplyKind.CharThenTwoTerminatedIfOne, validFirstChars: "01");

        public static ReplyShape DegreeLimit { get; } = new(ReplyKind.UntilDegreeThenOptionalHash);

        public static ReplyShape Drain { get; } = new(ReplyKind.Drain);

        public static ReplyShape Fixed(int length) => new(ReplyKind.Fixed, length);

        public static ReplyShape FixedThenOptionalHash(int length) => new(ReplyKind.FixedThenOptionalHash, length);

        public static ReplyShape OneOf(params string[] alternatives) => new(ReplyKind.OneOf, alternatives: alternatives.ToArray());

        /// <summary>Parses the names used by the raw console's ".shape" command.</summary>
        public static bool TryParse(string name, out ReplyShape shape) {
            shape = name?.ToLowerInvariant() switch {
                "none" => None,
                "char" => AnyChar,
                "bool" => Bool,
                "hash" or "terminated" => Terminated,
                "ms" => SlewResult,
                "sc" => DateResult,
                "degree" => DegreeLimit,
                "drain" => Drain,
                _ => null
            };
            if (shape == null && name != null && name.StartsWith("fixed", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name.AsSpan(5), out var n) && n > 0 && n < 256) {
                shape = Fixed(n);
            }
            return shape != null;
        }

        public override string ToString() => Kind switch {
            ReplyKind.None => "no reply",
            ReplyKind.Char when ValidFirstChars != null => $"one char of [{ValidFirstChars}], no '#'",
            ReplyKind.Char => "one char, no '#'",
            ReplyKind.Terminated => "'#'-terminated string",
            ReplyKind.Fixed => $"{Length} chars, no '#'",
            ReplyKind.FixedThenOptionalHash => $"{Length} chars, then optional '#'",
            ReplyKind.OneOf => $"one of {string.Join(" / ", Alternatives.Select(a => $"\"{a}\""))}, no '#'",
            ReplyKind.CharThenTerminatedIfNotZero => "'0', or '1'/'2'/'3' + '#'-terminated message",
            ReplyKind.CharThenTwoTerminatedIfOne => "'0', or '1' + two '#'-terminated strings",
            ReplyKind.UntilDegreeThenOptionalHash => "digits up to '*'/0xDF, then optional '#'",
            ReplyKind.Drain => "unknown: read until the line is quiet",
            _ => Kind.ToString()
        };
    }
}
