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
using NINA.Mac.Lx200;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace NINA.Mac.Equipment.Lx200 {

    /// <summary>
    /// One <see cref="Lx200Link"/> per serial port, shared by the mount and the focuser and closed when the last of them lets
    /// go (reference counted). macOS opens a serial port exclusively (TIOCEXCL, RIM MNT-22), so two links on one port could
    /// not work anyway.
    /// </summary>
    public sealed class Lx200LinkPool {
        private readonly Func<string, Func<Lx200Trace, ILx200Transport>> openerFor;
        private readonly Func<Lx200LinkOptions> optionsFactory;
        private readonly Dictionary<string, Lx200Link> links = new(StringComparer.Ordinal);
        private readonly object sync = new();

        /// <param name="openerFor">Transport opener for a port path; default: <see cref="Lx200Serial.Open"/> (9600 8N1, no handshake).</param>
        /// <param name="optionsFactory">Options for a new link; default: trace files in NINA's log folder (Logs/lx200), and the device node watched while idle.</param>
        public Lx200LinkPool(Func<string, Func<Lx200Trace, ILx200Transport>> openerFor = null, Func<Lx200LinkOptions> optionsFactory = null) {
            this.openerFor = openerFor ?? (port => trace => Lx200Serial.Open(port, trace));
            this.optionsFactory = optionsFactory ?? (() => new Lx200LinkOptions {
                TraceDirectory = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs", "lx200"),
                WatchPortNode = true
            });
        }

        /// <summary>The pool the devices use unless they are given another one.</summary>
        public static Lx200LinkPool Shared { get; } = new Lx200LinkPool();

        /// <summary>
        /// The open link for <paramref name="port"/>, opening it (and waiting for ACK) if nobody holds it. A link that gave up
        /// reconnecting is replaced by a new one. Every call must be paired with <see cref="Release"/>.
        /// </summary>
        public Lx200Link Acquire(string port, CancellationToken token = default) {
            ArgumentException.ThrowIfNullOrEmpty(port);
            lock (sync) {
                if (links.TryGetValue(port, out var existing) && existing.State != Lx200LinkState.Failed && existing.State != Lx200LinkState.Closed) {
                    existing.RefCount++;
                    return existing;
                }
                if (existing != null) {
                    links.Remove(port);   // the devices still holding it release it later; its refcount disposes it
                }
                var link = new Lx200Link(port, openerFor(port), optionsFactory());
                try {
                    link.Open(token);
                } catch {
                    link.Dispose();
                    throw;
                }
                link.RefCount = 1;
                links[port] = link;
                return link;
            }
        }

        public void Release(Lx200Link link) {
            if (link == null) {
                return;
            }
            bool dispose;
            lock (sync) {
                link.RefCount = Math.Max(0, link.RefCount - 1);
                dispose = link.RefCount == 0;
                if (dispose && links.TryGetValue(link.Name, out var current) && ReferenceEquals(current, link)) {
                    links.Remove(link.Name);
                }
            }
            if (dispose) {
                link.Dispose();
            }
        }

        /// <summary>The link currently open on <paramref name="port"/>, or null.</summary>
        public Lx200Link Find(string port) {
            lock (sync) {
                return links.TryGetValue(port, out var link) ? link : null;
            }
        }
    }
}
