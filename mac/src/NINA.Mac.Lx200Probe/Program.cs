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
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;

namespace NINA.Mac.Lx200Probe {

    public static class Program {

        private const string Usage = """
            lx200probe: M2 mount probe for the NINA macOS port (Meade LX200GPS / Autostar II, 9600 8N1)

              lx200probe ports
                  List /dev/cu.* serial devices; the FTDI cable shows as /dev/cu.usbserial-*.
              lx200probe raw (--port /dev/cu.usbserial-XXXX | --sim) [--out DIR]
                  Interactive console: type ':GR#' or 'GR', see the reply bytes (hex + text) and timing.
                  Blocklisted commands (:hP# park etc.) are refused; motion and config writes ask first.
              lx200probe checklist (--port /dev/cu.usbserial-XXXX | --sim) [options]
                  MAC_PORT_PLAN.md section 5, steps 1-8. Writes results.md + trace.log to
                  ~/Astro/NINA/m2-bench/<timestamp> (or --out DIR). Asks before anything moves or is written.

            checklist options:
              --date-test          also run step 4 (:SC UTC vs local date). BENCH ONLY: destroys the alignment.
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

        public static int Main(string[] args) => Run(args, new SystemConsole(), handleCtrlC: true);

        /// <summary>Entry point with an injectable console (tests drive the probe through this).</summary>
        public static int Run(string[] args, IOperatorConsole console, bool handleCtrlC = false, CancellationToken cancel = default) {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") {
                console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }
            ProbeOptions opts;
            try {
                opts = ProbeOptions.Parse(args.Skip(1));
            } catch (ArgumentException ex) {
                console.WriteLine(ex.Message);
                return 2;
            }
            try {
                return args[0] switch {
                    "ports" => Ports(console),
                    "raw" => Raw(opts, console, handleCtrlC, cancel),
                    "checklist" => RunChecklist(opts, console, handleCtrlC, cancel, string.Join(' ', args)),
                    _ => Fail(console, $"Unknown command '{args[0]}'\n\n{Usage}")
                };
            } catch (UsageException ex) {
                return Fail(console, ex.Message);
            } catch (UnauthorizedAccessException ex) {
                return Fail(console, $"Cannot open the port: {ex.Message}. Another program (KStars/INDI, a terminal) may hold it; macOS ports are opened exclusively.");
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
            var dir = OutputDirectory(opts, "raw-");
            Directory.CreateDirectory(dir);
            var tracePath = Path.Combine(dir, "trace.log");
            var linkOptions = new Lx200ConnectionOptions { TrailingWindow = TimeSpan.FromMilliseconds(200) };
            using var session = Session.Open(opts, tracePath, linkOptions);
            console.WriteLine($"Trace: {tracePath}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            using var ctrlC = handleCtrlC ? CtrlC.Install(cts, session.Link, console, "press Enter to leave the console") : null;
            var prompter = new Prompter(console, autoYes: opts.Flag("yes") && session.Sim != null);
            return new RawConsole(session.Link, prompter, cts.Token).Run();
        }

        private static int RunChecklist(ProbeOptions opts, IOperatorConsole console, bool handleCtrlC, CancellationToken cancel, string commandLine) {
            var simulator = opts.Flag("sim");
            if (opts.Flag("yes") && !simulator) {
                throw new UsageException("--yes is only allowed with --sim: on a real mount every motion and write must be confirmed.");
            }
            var dir = OutputDirectory(opts, simulator ? "sim-" : "");
            Directory.CreateDirectory(dir);
            var tracePath = Path.Combine(dir, "trace.log");
            // At the bench: keep listening after every reply so a wrong reply shape shows up as TRAILING bytes
            var linkOptions = new Lx200ConnectionOptions { TrailingWindow = TimeSpan.FromMilliseconds(opts.Int("trailing-ms", 150)) };
            using var session = Session.Open(opts, tracePath, linkOptions);
            var options = new ChecklistOptions {
                OutputDirectory = dir,
                DateTest = opts.Flag("date-test"),
                PulseMs = opts.Int("pulse-ms", 2000),
                GuideRateArcsecPerSec = opts.Double("guide-rate", 10.0),
                FocusMs = opts.Int("focus-ms", 1000),
                TrackWaitSeconds = opts.Double("track-wait", 5),
                GotoOffsetAzDeg = opts.Double("goto-offset", 15),
                SiteLatitude = opts.Double("lat", 22.25),
                SiteLongitudeEast = opts.Double("lon", 114.18),
                UtcOffsetHours = opts.Double("utc-offset", 8),
                PortDescription = session.Description,
                CommandLine = "lx200probe " + commandLine
            };
            if (options.PulseMs is < 1 or > 9999) {
                throw new UsageException("--pulse-ms must be 1..9999 (P07 :MgnDDDD#)");
            }
            if (options.FocusMs is < 1 or > 65000) {
                throw new UsageException("--focus-ms must be 1..65000 (P07 :FPsDDDD#)");
            }
            foreach (var part in opts.String("skip", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                options.Skip.Add(int.Parse(part, CultureInfo.InvariantCulture));
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            using var ctrlC = handleCtrlC ? CtrlC.Install(cts, session.Link, console, "finishing the current exchange and writing results") : null;
            var prompter = new Prompter(console, autoYes: simulator && !opts.Flag("interactive"));
            console.WriteLine($"lx200probe checklist on {session.Description}");
            console.WriteLine($"Output: {dir}");
            if (!simulator) {
                console.WriteLine("Before you start: quick handbox alignment done, scope clear to move, focuser plugged in. Never send :hP# (the probe refuses it).");
            }
            var checklist = new Checklist(session.Link, prompter, options, cts.Token, session.Sim);
            return checklist.Run();
        }

        private static string OutputDirectory(ProbeOptions opts, string prefix) {
            if (opts.Has("out")) {
                return ProbeOptions.ExpandHome(opts.String("out", "."));
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

            public static Session Open(ProbeOptions opts, string tracePath, Lx200ConnectionOptions linkOptions) {
                var trace = Lx200Trace.ToFile(tracePath);
                try {
                    if (opts.Flag("sim")) {
                        var simOptions = new SimOptions().Apply(opts.All("sim-quirk"));
                        var (client, server) = DuplexPipe.Create();
                        var sim = new AutostarSimulator(server, simOptions);
                        trace.Note($"simulator: {simOptions.Describe()}");
                        var link = Lx200Connection.OverStream(client, "simulator", trace, linkOptions);
                        return new Session(link, sim, trace, $"built-in simulator ({simOptions.Describe()})");
                    }
                    var port = opts.String("port", null) ?? throw new UsageException("Give --port /dev/cu.usbserial-XXXX (see 'lx200probe ports') or --sim.");
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

        /// <summary>First Ctrl+C: cancel and send the stop commands at once. Second: stop again and exit.</summary>
        private sealed class CtrlC : IDisposable {
            private readonly ConsoleCancelEventHandler handler;

            private CtrlC(ConsoleCancelEventHandler handler) {
                this.handler = handler;
                Console.CancelKeyPress += handler;
            }

            public static CtrlC Install(CancellationTokenSource cts, Lx200Connection link, IOperatorConsole console, string what) {
                return new CtrlC((_, e) => {
                    if (!cts.IsCancellationRequested) {
                        e.Cancel = true;
                        console.WriteLine($"\nCtrl+C: sending stop commands (:Q#, :FQ#), {what}. Ctrl+C again to quit at once.");
                        cts.Cancel();
                        ThreadPool.QueueUserWorkItem(_ => link.StopAll("Ctrl+C"));
                    } else {
                        link.StopAll("second Ctrl+C");
                        e.Cancel = false;
                    }
                });
            }

            public void Dispose() => Console.CancelKeyPress -= handler;
        }

        private sealed class UsageException : Exception {

            public UsageException(string message) : base(message) {
            }
        }
    }
}
