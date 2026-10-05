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
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace NINA.Mac.Lx200Probe {

    public static class Program {

        private const string Usage = """
            lx200probe: M2 mount probe for the NINA macOS port (Meade LX200GPS / Autostar II, 9600 8N1)

              lx200probe ports
                  List /dev/cu.* serial devices; the FTDI cable shows as /dev/cu.usbserial-*.
              lx200probe raw (--port /dev/cu.usbserial-XXXX | --sim [--yes]) [--out DIR]
                  Interactive console: type ':GR#' or 'GR', see the reply bytes (hex + text) and timing.
                  Blocklisted commands (:hP# park etc.) are refused; motion and config writes ask first
                  (with --sim, --yes answers those questions itself). '.help' lists the console's own commands.
              lx200probe checklist (--port /dev/cu.usbserial-XXXX | --sim) [options]
                  MAC_PORT_PLAN.md section 5, steps 1-8. Writes results.md + trace.log to
                  ~/Astro/NINA/m2-bench/<timestamp> (or --out DIR). Asks before anything moves or is written.

            checklist options:
              --date-test          also run step 4 (:SC UTC vs local date), last, after steps 5-8. BENCH ONLY: destroys the alignment.
              --skip 5,6           skip steps
              --pulse-ms 2000      pulse/move length for step 6 (1..9999)
              --guide-rate 10      :Rg value in "/s sent before step 6 (0 = leave the mount's rate)
              --focus-ms 1000      focuser move length for step 7
              --track-wait 5       seconds in Land mode for step 8
              --goto-offset 15     azimuth offset of the step 5 goto target, degrees
              --lat 22.25 --lon 114.18 --utc-offset 8    site (default Deep Water Bay, Hong Kong)
              --trailing-ms 150    listen this long after each reply for unexpected bytes (wrong reply shape)

            simulator:
              --sim                use the built-in Autostar II simulator instead of a port (answers prompts itself)
              --interactive        with --sim: ask the prompts anyway
              --sim-quirk k=v      simulator switch, repeatable (lx200sim quirks lists them), e.g. --sim-quirk pulse=ignored
            """;

        /// <summary>Options each command accepts; anything else is a usage error (a typo must not be ignored at the bench).</summary>
        private static readonly string[] RawOptions = { "port", "sim", "sim-quirk", "out", "yes" };

        private static readonly string[] ChecklistOptionNames = {
            "port", "sim", "interactive", "sim-quirk", "out", "yes", "date-test", "skip", "pulse-ms", "guide-rate", "focus-ms",
            "track-wait", "goto-offset", "lat", "lon", "utc-offset", "trailing-ms"
        };

        public static int Main(string[] args) => Run(args, new SystemConsole(), handleCtrlC: true);

        /// <summary>Entry point with an injectable console (tests drive the probe through this).</summary>
        public static int Run(string[] args, IOperatorConsole console, bool handleCtrlC = false, CancellationToken cancel = default) {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") {
                console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }
            try {
                var opts = ProbeOptions.Parse(args.Skip(1));
                switch (args[0]) {
                    case "ports":
                        opts.RequireOnly("ports", Array.Empty<string>());
                        return Ports(console);
                    case "raw":
                        opts.RequireOnly("raw", RawOptions);
                        return Raw(opts, console, handleCtrlC, cancel);
                    case "checklist":
                        opts.RequireOnly("checklist", ChecklistOptionNames);
                        return RunChecklist(opts, console, handleCtrlC, cancel, string.Join(' ', args));
                    default:
                        return Fail(console, $"Unknown command '{args[0]}'\n\n{Usage}");
                }
            } catch (UsageException ex) {
                return Fail(console, ex.Message);
            } catch (UnauthorizedAccessException ex) {
                return Fail(console, $"Cannot open the port: {ex.Message.TrimEnd('.')}. Another program (KStars/INDI, a terminal) may hold it; macOS ports are opened exclusively.");
            } catch (IOException ex) {
                return Fail(console, $"Serial I/O error: {ex.Message}");
            }
        }

        private static int Fail(IOperatorConsole console, string message) {
            console.WriteLine(message);
            return 2;
        }

        private static int Ports(IOperatorConsole console) {
            var cu = Lx200Serial.ListCalloutPorts();
            console.WriteLine("Callout serial devices (/dev/cu.*):");
            foreach (var p in cu) {
                console.WriteLine($"  {p}{(Lx200Serial.IsUsbSerial(p) ? "   <- USB serial adapter: use this path" : "")}");
            }
            if (!cu.Any(Lx200Serial.IsUsbSerial)) {
                console.WriteLine("No /dev/cu.usbserial-* or /dev/cu.usbmodem* device: plug in the FTDI cable (macOS has Apple's FTDI driver built in).");
            }
            string[] names;
            try {
                names = SerialPort.GetPortNames();
            } catch (Exception ex) {
                names = Array.Empty<string>();
                console.WriteLine($"SerialPort.GetPortNames() failed: {ex.Message}");
            }
            var tty = names.Count(n => n.StartsWith("/dev/tty.", StringComparison.Ordinal));
            var cuCount = names.Count(n => n.StartsWith("/dev/cu.", StringComparison.Ordinal));
            console.WriteLine($".NET SerialPort.GetPortNames() returns {names.Length} names ({tty} /dev/tty.* and {cuCount} /dev/cu.*): each device appears twice; use the /dev/cu.* path.");
            return 0;
        }

        private static int Raw(ProbeOptions opts, IOperatorConsole console, bool handleCtrlC, CancellationToken cancel) {
            Require(!opts.Flag("yes") || opts.Flag("sim"), "--yes is only allowed with --sim: on a real mount every motion and write must be confirmed.");
            var target = Target.From(opts);
            var dir = OutputDirectory(opts, "raw-");
            CreateOutputDirectory(dir);
            var tracePath = Path.Combine(dir, "trace.log");
            var linkOptions = new Lx200ConnectionOptions { TrailingWindow = TimeSpan.FromMilliseconds(200) };
            using var session = Session.Open(target, tracePath, linkOptions);
            console.WriteLine($"Trace: {tracePath}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            using var ctrlC = handleCtrlC ? CtrlC.Install(cts, session.Link, console, "press Enter to leave the console") : null;
            var prompter = new Prompter(console, autoYes: opts.Flag("yes"));
            return new RawConsole(session.Link, prompter, cts.Token).Run();
        }

        private static int RunChecklist(ProbeOptions opts, IOperatorConsole console, bool handleCtrlC, CancellationToken cancel, string commandLine) {
            var simulator = opts.Flag("sim");
            if (opts.Flag("yes") && !simulator) {
                throw new UsageException("--yes is only allowed with --sim: on a real mount every motion and write must be confirmed.");
            }
            // Everything the operator typed is checked before a folder is made or the port is opened
            var target = Target.From(opts);
            var options = new ChecklistOptions {
                DateTest = opts.Flag("date-test"),
                PulseMs = opts.Int("pulse-ms", 2000),
                GuideRateArcsecPerSec = opts.Double("guide-rate", 10.0),
                FocusMs = opts.Int("focus-ms", 1000),
                TrackWaitSeconds = opts.Double("track-wait", 5),
                GotoOffsetAzDeg = opts.Double("goto-offset", 15),
                SiteLatitude = opts.Double("lat", 22.25),
                SiteLongitudeEast = opts.Double("lon", 114.18),
                UtcOffsetHours = opts.Double("utc-offset", 8),
                CommandLine = "lx200probe " + commandLine
            };
            options.Skip.UnionWith(opts.IntList("skip", 1, 8));
            var trailingMs = opts.Int("trailing-ms", 150);
            Require(options.PulseMs is >= 1 and <= 9999, "--pulse-ms must be 1..9999 (P07 :MgnDDDD#)");
            Require(options.FocusMs is >= 1 and <= 65000, "--focus-ms must be 1..65000 (P07 :FPsDDDD#)");
            Require(options.GuideRateArcsecPerSec >= 0 && options.GuideRateArcsecPerSec <= 15.0417, "--guide-rate must be 0 (leave the mount's rate) or up to 15.0417 \"/s (P07 :RgSS.S#)");
            Require(options.TrackWaitSeconds is >= 0 and <= 600, "--track-wait must be 0..600 seconds");
            Require(Math.Abs(options.GotoOffsetAzDeg) <= 90, "--goto-offset must be -90..90 degrees");
            Require(Math.Abs(options.SiteLatitude) <= 90, "--lat must be -90..90 (north positive)");
            Require(Math.Abs(options.SiteLongitudeEast) <= 180, "--lon must be -180..180 (EAST positive; Hong Kong is 114.18)");
            Require(Math.Abs(options.UtcOffsetHours) <= 14, "--utc-offset must be -14..14 hours (the usual UTC+N number; Hong Kong is 8)");
            Require(options.UtcOffsetHours == Math.Round(options.UtcOffsetHours), "--utc-offset must be whole hours: the probe writes :SG in the integer form first (P07's sHH.H form for half-hour zones is not handled)");
            Require(!options.DateTest || options.UtcOffsetHours != 0, "--date-test needs a non-zero --utc-offset: at UTC+0 the UTC and local dates are always the same, so the test cannot tell them apart");
            Require(trailingMs is >= 0 and <= 5000, "--trailing-ms must be 0..5000");

            var dir = OutputDirectory(opts, simulator ? "sim-" : "");
            CreateOutputDirectory(dir);
            var tracePath = Path.Combine(dir, "trace.log");
            // At the bench: keep listening after every reply so a wrong reply shape shows up as TRAILING bytes
            var linkOptions = new Lx200ConnectionOptions { TrailingWindow = TimeSpan.FromMilliseconds(trailingMs) };
            using var session = Session.Open(target, tracePath, linkOptions);
            options.OutputDirectory = dir;
            options.PortDescription = session.Description;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var prompter = new Prompter(console, autoYes: simulator && !opts.Flag("interactive"));
            var checklist = new Checklist(session.Link, prompter, options, cts.Token, session.Sim);
            using var ctrlC = handleCtrlC
                ? CtrlC.Install(cts, session.Link, console, "finishing the current exchange and writing results (press Enter if a question is waiting)", () => checklist.RestorePending)
                : null;
            console.WriteLine($"lx200probe checklist on {session.Description}");
            console.WriteLine($"Output: {dir}");
            if (!simulator) {
                console.WriteLine("Before you start: quick handbox alignment done, scope clear to move, focuser plugged in. Never send :hP# (the probe refuses it).");
            }
            return checklist.Run();
        }

        /// <summary>Creates the results folder; a failure is the operator's to fix, not a serial error.</summary>
        private static void CreateOutputDirectory(string dir) {
            try {
                Directory.CreateDirectory(dir);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                throw new UsageException($"Cannot create the output folder {dir}: {ex.Message}");
            }
        }

        private static void Require(bool condition, string message) {
            if (!condition) {
                throw new UsageException(message);
            }
        }

        /// <summary>What to talk to, checked before anything is opened: the in-process simulator or an existing port path.</summary>
        private sealed class Target {

            private Target(SimOptions sim, string port) {
                Sim = sim;
                Port = port;
            }

            /// <summary>Non-null for --sim.</summary>
            public SimOptions Sim { get; }

            public string Port { get; }

            public static Target From(ProbeOptions opts) {
                if (opts.Flag("sim")) {
                    if (opts.Has("port")) {
                        throw new UsageException("Give either --sim or --port, not both.");
                    }
                    try {
                        return new Target(new SimOptions().Apply(opts.All("sim-quirk")), null);
                    } catch (Exception ex) when (ex is ArgumentException or FormatException) {
                        throw new UsageException($"--sim-quirk: {ex.Message} ('lx200sim quirks' lists the switches)");
                    }
                }
                if (opts.Has("sim-quirk")) {
                    throw new UsageException("--sim-quirk only applies with --sim.");
                }
                var port = opts.String("port", null);
                if (port == null || port == "true") {
                    throw new UsageException("Give --port /dev/cu.usbserial-XXXX (see 'lx200probe ports') or --sim.");
                }
                if (!File.Exists(port)) {
                    throw new UsageException($"{port} does not exist. Is the cable plugged in? 'lx200probe ports' lists the /dev/cu.* devices.");
                }
                return new Target(null, port);
            }
        }

        private static string OutputDirectory(ProbeOptions opts, string prefix) {
            if (opts.Has("out")) {
                var outDir = opts.String("out", ".");
                Require(outDir != "true", "--out needs a folder, e.g. --out ~/Astro/NINA/m2-bench/test1");
                return ProbeOptions.ExpandHome(outDir);
            }
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            return ProbeOptions.ExpandHome($"~/Astro/NINA/m2-bench/{prefix}{stamp}");
        }

        /// <summary>A link to a real port or to the in-process simulator, plus the trace file.</summary>
        private sealed class Session : IDisposable {

            private Session(Lx200Connection link, AutostarSimulator sim, Lx200Trace trace, string description) {
                Link = link;
                Sim = sim;
                Trace = trace;
                Description = description;
            }

            public Lx200Connection Link { get; }

            public AutostarSimulator Sim { get; }

            public Lx200Trace Trace { get; }

            public string Description { get; }

            public static Session Open(Target target, string tracePath, Lx200ConnectionOptions linkOptions) {
                var trace = Lx200Trace.ToFile(tracePath);
                try {
                    if (target.Sim != null) {
                        var simOptions = target.Sim;
                        var (client, server) = DuplexPipe.Create();
                        var sim = new AutostarSimulator(server, simOptions);
                        trace.Note($"simulator: {simOptions.Describe()}");
                        var link = Lx200Connection.OverStream(client, "simulator", trace, linkOptions);
                        return new Session(link, sim, trace, $"built-in simulator ({simOptions.Describe()})");
                    }
                    var port = target.Port;
                    if (port.StartsWith("/dev/tty.", StringComparison.Ordinal)) {
                        trace.Note($"warning: {port} is the dial-in side; /dev/cu.{port["/dev/tty.".Length..]} is the right device for an outgoing connection");
                    }
                    var serial = Lx200Connection.OpenSerial(port, trace, linkOptions);
                    return new Session(serial, null, trace, $"{port} (9600 8N1, no handshake)");
                } catch {
                    trace.Dispose();
                    throw;
                }
            }

            public void Dispose() {
                Link.Dispose();
                Sim?.Dispose();
                Trace.Dispose();
            }
        }

        /// <summary>
        /// First Ctrl+C: cancel and send the stop commands at once. Second: stop again and exit. A hang-up (the Terminal
        /// window closed), SIGTERM or SIGQUIT sends the stop commands at once too, so a host-timed move (:Mn#, :F+#)
        /// cannot run on, and then lets the run unwind like Ctrl+C: the date test puts the mount's real date and time
        /// back and results.md is written before the process exits. A watchdog ends the process if that takes too long
        /// (e.g. the run waits at a prompt nobody will answer); a second such signal ends it at once.
        /// </summary>
        private sealed class CtrlC : IDisposable {

            /// <summary>Longest wait for the run to unwind after a signal; longer while the date test's restore is pending.</summary>
            private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

            /// <summary>Longer than a :SC planetary update (30 s) plus the restore's own :SL/:SC/:GS# retries.</summary>
            private static readonly TimeSpan RestoreGrace = TimeSpan.FromSeconds(90);

            private readonly CancellationTokenSource cts;
            private readonly Lx200Connection link;
            private readonly IOperatorConsole console;
            private readonly string what;
            private readonly Func<bool> restorePending;
            private readonly ManualResetEventSlim finished = new();
            private readonly ConsoleCancelEventHandler handler;
            private readonly PosixSignalRegistration[] endSignals;
            private int ending;

            private CtrlC(CancellationTokenSource cts, Lx200Connection link, IOperatorConsole console, string what, Func<bool> restorePending) {
                this.cts = cts;
                this.link = link;
                this.console = console;
                this.what = what;
                this.restorePending = restorePending ?? (() => false);
                handler = OnCtrlC;
                Console.CancelKeyPress += handler;
                endSignals = new[] { PosixSignal.SIGHUP, PosixSignal.SIGTERM, PosixSignal.SIGQUIT }
                    .Select(s => PosixSignalRegistration.Create(s, OnEnd))
                    .ToArray();
            }

            public static CtrlC Install(CancellationTokenSource cts, Lx200Connection link, IOperatorConsole console, string what, Func<bool> restorePending = null) =>
                new(cts, link, console, what, restorePending);

            private string RestoreWarning => restorePending()
                ? " The mount's real date and time are being written back: do NOT stop the probe again until it exits."
                : "";

            private void OnCtrlC(object sender, ConsoleCancelEventArgs e) {
                if (!cts.IsCancellationRequested) {
                    e.Cancel = true;
                    console.WriteLine($"\nCtrl+C: sending stop commands (:Q#, :FQ#), {what}. Ctrl+C again to quit at once.{RestoreWarning}");
                    Cancel();
                    ThreadPool.QueueUserWorkItem(_ => link.StopAll("Ctrl+C"));
                } else {
                    link.StopAll("second Ctrl+C");
                    e.Cancel = false;
                }
            }

            private void OnEnd(PosixSignalContext context) {
                if (Interlocked.Exchange(ref ending, 1) == 1) {
                    // a second hang-up/SIGTERM/SIGQUIT: stop again, then the default action ends the process
                    link.StopAll("second " + context.Signal);
                    return;
                }
                context.Cancel = true;   // the process does not end here: the run unwinds first (restore, results.md)
                if (context.Signal != PosixSignal.SIGHUP) {
                    console.WriteLine($"\n{context.Signal}: sending stop commands (:Q#, :FQ#), then finishing and writing results before exiting.{RestoreWarning}");
                }
                link.StopAll(context.Signal.ToString());
                Cancel();
                var signal = context.Signal;
                new Thread(() => Watchdog(signal)) { IsBackground = true, Name = "signal watchdog" }.Start();
            }

            private void Watchdog(PosixSignal signal) {
                var sw = Stopwatch.StartNew();
                while (!finished.Wait(TimeSpan.FromMilliseconds(250))) {
                    if (sw.Elapsed > RestoreGrace || (sw.Elapsed > Grace && !restorePending())) {
                        link.StopAll($"{signal}: run still not finished after {sw.Elapsed.TotalSeconds:0} s");
                        Environment.Exit(signal switch {
                            PosixSignal.SIGHUP => 129,
                            PosixSignal.SIGQUIT => 131,
                            _ => 143
                        });
                    }
                }
            }

            private void Cancel() {
                try {
                    cts.Cancel();
                } catch (ObjectDisposedException) {
                    // the run already finished
                }
            }

            public void Dispose() {
                finished.Set();
                Console.CancelKeyPress -= handler;
                foreach (var s in endSignals) {
                    s.Dispose();
                }
            }
        }
    }
}
