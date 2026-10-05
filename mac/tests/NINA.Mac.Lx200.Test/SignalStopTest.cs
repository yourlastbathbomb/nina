#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NINA.Mac.Lx200.Test {

    /// <summary>
    /// The probe as a real process (built-in simulator): Ctrl+C (SIGINT), a hang-up or SIGTERM in the middle of a
    /// host-timed focuser move must still put :FQ# and :Q# on the wire before the process ends, or the #1209 would
    /// run to its end stop; in the middle of the date test, the real date and time must still be written back.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class SignalStopTest {
        private const int SIGHUP = 1;
        private const int SIGINT = 2;
        private const int SIGTERM = 15;

        // trace.log TX lines: "TX" padded to 7 columns, a space, then the hex bytes
        private const string FocusInTx = "TX      3A 46 2B 23";    // :F+#
        private const string FocusHaltTx = "TX      3A 46 51 23";  // :FQ#
        private const string HaltAllTx = "TX      3A 51 23";       // :Q#
        private const string SetDateTx = "TX      3A 53 43";       // :SC...
        private const string SetTimeTx = "TX      3A 53 4C";       // :SL...

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        [DllImport("libc", SetLastError = true)]
        private static extern int sigaction(int sig, IntPtr act, byte[] oldAct);

        [TestCase(SIGINT, "Ctrl+C")]
        [TestCase(SIGTERM, "SIGTERM")]
        [TestCase(SIGHUP, "SIGHUP")]
        public void Signal_DuringAHostTimedFocusMove_SendsStopsBeforeTheProcessEnds(int signal, string name) {
            var dir = SimHarness.TempDir("signal-" + name);
            var trace = Path.Combine(dir, "trace.log");
            var output = new StringBuilder();
            using var p = StartProbe(signal, output, "checklist", "--sim", "--out", dir, "--skip", "1,2,3,4,5,6,8", "--focus-ms", "8000", "--trailing-ms", "20");
            try {
                // wait until the focuser has been started (speed 1, 8 s host-timed move inward)
                WaitForTrace(p, trace, FocusInTx, output);

                kill(p.Id, signal).Should().Be(0, $"kill({name}) errno {Marshal.GetLastPInvokeError()}");

                p.WaitForExit(20000).Should().BeTrue($"the probe ends after a {name}:\n{Text(output)}");
                var log = ReadShared(trace);
                var afterStart = log[log.IndexOf(FocusInTx, StringComparison.Ordinal)..];
                afterStart.Should().Contain($"STOP ALL ({name})");
                afterStart.Should().Contain(FocusHaltTx).And.Contain(HaltAllTx);
                afterStart.IndexOf(FocusHaltTx, StringComparison.Ordinal).Should().BeGreaterThan(afterStart.IndexOf($"STOP ALL ({name})", StringComparison.Ordinal),
                    "the focuser had not been halted before the signal (the move was 8 s long)");
                // handled, not fatal: the run is marked aborted and results.md is written
                p.ExitCode.Should().Be(130, Text(output));
                File.ReadAllText(Path.Combine(dir, "results.md")).Should().Contain("| 7 | Focuser (#1209) | Aborted |");
            } finally {
                if (!p.HasExited) {
                    p.Kill();
                }
            }
        }

        /// <summary>
        /// Closing the Terminal window or 'kill' while the mount holds the date test's simulated date: the probe must
        /// still write the real date and time back before it exits, as it does after Ctrl+C.
        /// </summary>
        [TestCase(SIGTERM, "SIGTERM")]
        [TestCase(SIGHUP, "SIGHUP")]
        public void Signal_DuringTheDateTest_PutsTheRealDateAndTimeBack(int signal, string name) {
            var dir = SimHarness.TempDir("signal-date-" + name);
            var trace = Path.Combine(dir, "trace.log");
            var output = new StringBuilder();
            // planetary=3: after each :SC the simulated mount is busy (NAK) for 3 s, as the real one is for longer
            using var p = StartProbe(signal, output, "checklist", "--sim", "--date-test", "--out", dir, "--skip", "1,2,3,5,6,7,8", "--sim-quirk", "planetary=3", "--trailing-ms", "20");
            try {
                WaitForTrace(p, trace, SetDateTx, output);

                kill(p.Id, signal).Should().Be(0, $"kill({name}) errno {Marshal.GetLastPInvokeError()}");

                p.WaitForExit(90000).Should().BeTrue($"the probe ends after a {name}:\n{Text(output)}");
                p.ExitCode.Should().Be(130, Text(output));
                var log = ReadShared(trace);
                var stop = log.IndexOf($"STOP ALL ({name})", StringComparison.Ordinal);
                stop.Should().BeGreaterThan(0, log);
                var afterSignal = log[stop..];
                afterSignal.Should().Contain(SetTimeTx, "the restore writes the time").And.Contain(SetDateTx, "and the date");
                var results = File.ReadAllText(Path.Combine(dir, "results.md"));
                results.Should().Contain("| 4 | Date convention (:SC) | Aborted |");
                results.Should().Contain("Restored :SL from the Mac clock").And.Contain("restore ok");
            } finally {
                if (!p.HasExited) {
                    p.Kill();
                }
            }
        }

        /// <summary>Starts lx200probe.dll with the test host's dotnet, or ignores the test where that cannot work.</summary>
        private static Process StartProbe(int signal, StringBuilder output, params string[] args) {
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) {
                Assert.Ignore("POSIX signals only");
            }
            if (signal == SIGINT && SignalIgnored(SIGINT)) {
                // a job started with '&' by a non-interactive shell ignores SIGINT, and its children inherit that
                Assert.Ignore("SIGINT is ignored in this test run (started in the background?), so the probe cannot see Ctrl+C");
            }
            var host = Environment.ProcessPath;
            if (host == null || Path.GetFileNameWithoutExtension(host) != "dotnet") {
                Assert.Ignore($"the test host is '{host}', not dotnet, so lx200probe.dll cannot be started the same way");
            }
            var psi = new ProcessStartInfo(host) {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "lx200probe.dll"));
            foreach (var a in args) {
                psi.ArgumentList.Add(a);
            }
            var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => Append(output, e.Data);
            p.ErrorDataReceived += (_, e) => Append(output, e.Data);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        private static void WaitForTrace(Process p, string trace, string text, StringBuilder output) {
            var sw = Stopwatch.StartNew();
            while (!ReadShared(trace).Contains(text)) {
                if (p.HasExited || sw.Elapsed > TimeSpan.FromSeconds(60)) {
                    Assert.Fail($"the probe never sent '{text}' (exited: {p.HasExited}):\n{Text(output)}");
                }
                Thread.Sleep(50);
            }
        }

        /// <summary>True when this process ignores the signal (SIG_IGN = 1 in the first field of struct sigaction).</summary>
        private static bool SignalIgnored(int signal) {
            var old = new byte[256];   // larger than struct sigaction on macOS (16 bytes) and Linux (152 bytes)
            return sigaction(signal, IntPtr.Zero, old) == 0 && BitConverter.ToInt64(old, 0) == 1;
        }

        private static void Append(StringBuilder sb, string line) {
            if (line != null) {
                lock (sb) {
                    sb.AppendLine(line);
                }
            }
        }

        private static string Text(StringBuilder sb) {
            lock (sb) {
                return sb.ToString();
            }
        }

        /// <summary>Reads a file the probe still has open for writing.</summary>
        private static string ReadShared(string path) {
            if (!File.Exists(path)) {
                return "";
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}
