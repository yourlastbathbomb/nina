#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.Lx200;
using NINA.Mac.Lx200.Sim;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace NINA.Mac.Lx200.Test {

    /// <summary>An in-process simulator wired to a connection through an in-memory pipe.</summary>
    internal sealed class SimHarness : IDisposable {

        /// <param name="clockUtc">
        /// When set, the simulator's clock and the probe's clock (the trace) both start at this UTC time and run on in
        /// real time, so a test can pin the time of day.
        /// </param>
        public SimHarness(SimOptions simOptions = null, Lx200ConnectionOptions linkOptions = null, DateTime? clockUtc = null) {
            var (client, server) = DuplexPipe.Create();
            simOptions ??= new SimOptions { PlanetaryUpdateSeconds = 0.2, SlewSeconds = 0.5 };
            if (clockUtc.HasValue) {
                var start = clockUtc.Value;
                var elapsed = Stopwatch.StartNew();
                simOptions.UtcNow = () => start + elapsed.Elapsed;
            }
            Sim = new AutostarSimulator(server, simOptions);
            Trace = new Lx200Trace(startUtc: clockUtc);
            Link = Lx200Connection.OverStream(client, "test-sim", Trace, linkOptions ?? new Lx200ConnectionOptions { MinimumGap = TimeSpan.FromMilliseconds(5) });
        }

        public AutostarSimulator Sim { get; }

        public Lx200Trace Trace { get; }

        public Lx200Connection Link { get; }

        /// <summary>True when any TX in the trace carries exactly these bytes.</summary>
        public bool Transmitted(string command) {
            var bytes = Lx200Format.Latin1.GetBytes(command);
            return Trace.Snapshot().Any(e => e.Kind == TraceKind.Tx && e.Bytes.AsSpan().SequenceEqual(bytes));
        }

        public static string TempDir(string name) {
            var dir = Path.Combine(Path.GetTempPath(), "lx200test", $"{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public void Dispose() {
            Link.Dispose();
            Sim.Dispose();
            Trace.Dispose();
        }
    }
}
