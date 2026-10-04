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

    /// <summary>What a command does to the mount, used for confirmation prompts and stop-on-abort.</summary>
    public enum CommandEffect {

        /// <summary>Only reads state.</summary>
        ReadOnly,

        /// <summary>Changes stored or session state (site, time, date, limits, sync, rates, precision).</summary>
        WritesConfig,

        /// <summary>Starts motion of the mount or the focuser.</summary>
        MovesHardware,

        /// <summary>Stops motion (":Q#", ":Qn#", ":FQ#"). Never needs confirmation.</summary>
        Halt
    }

    /// <summary>One catalog entry: a command code, its reply shape, its effect and where the facts come from.</summary>
    public sealed class Lx200CommandSpec {

        public Lx200CommandSpec(string code, string purpose, ReplyShape reply, CommandEffect effect, string source,
                                bool hasArgs = false, string example = null, bool isVolatile = false,
                                TimeSpan? timeout = null, string blockedReason = null) {
            Code = code;
            Purpose = purpose;
            Reply = reply;
            Effect = effect;
            Source = source;
            HasArgs = hasArgs;
            Example = example ?? (code == AckCode ? "\u0006" : $":{code}#");
            IsVolatile = isVolatile;
            Timeout = timeout ?? DefaultTimeout;
            BlockedReason = blockedReason;
        }

        /// <summary>Code used for the bare ACK byte 0x06, which is not ':'/'#' framed.</summary>
        public const string AckCode = "ACK";

        /// <summary>
        /// Default time for the whole reply. 9600 baud is ~1 ms per byte; the FTDI driver may add 16 ms and the
        /// Autostar some tens of ms (MNT-24, MNT-28), so 2 s is generous for every short reply.
        /// </summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

        /// <summary>Text between ':' and the arguments, case-sensitive (":Ms#" moves south, ":MS#" slews).</summary>
        public string Code { get; }

        public string Purpose { get; }

        public ReplyShape Reply { get; }

        public CommandEffect Effect { get; }

        /// <summary>Spec/research citation. P07 = research/reference/proto2007.txt line numbers.</summary>
        public string Source { get; }

        public bool HasArgs { get; }

        /// <summary>A well-formed example, used by the framing tests and the raw console help.</summary>
        public string Example { get; }

        /// <summary>Session-only state that is harmless to change (target coordinates, precision, rates, focus speed).</summary>
        public bool IsVolatile { get; }

        public TimeSpan Timeout { get; }

        /// <summary>Non-null: the command is refused by <see cref="Lx200Safety"/> before any byte is written.</summary>
        public string BlockedReason { get; }

        public bool IsBlocked => BlockedReason != null;

        /// <summary>Motion always needs the operator's OK; config writes too unless they are volatile session state.</summary>
        public bool NeedsConfirmation => Effect == CommandEffect.MovesHardware || (Effect == CommandEffect.WritesConfig && !IsVolatile);

        public override string ToString() => Code == AckCode ? "ACK (0x06)" : $":{Code}{(HasArgs ? "…" : "")}#";
    }
}
