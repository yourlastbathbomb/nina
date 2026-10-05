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
using System.Threading;

namespace NINA.Mac.Lx200.Sim {

    public static class Program {

        private const string Usage = """
            lx200sim: Autostar II (LX200GPS) simulator for the NINA macOS port

              lx200sim pty [--quirk key=value]... [--log]
                  Serves the simulator on a pseudo-terminal and prints its /dev/ttysNNN path. Point
                  lx200probe at it:  lx200probe checklist --port /dev/ttysNNN
                  Runs until Ctrl+C. --log prints every command received.
              lx200sim quirks
                  Lists the quirk switches.
            """;

        public static int Main(string[] args) {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }
            if (args[0] == "quirks") {
                foreach (var line in SimOptions.QuirkHelp) {
                    Console.WriteLine("  " + line);
                }
                return 0;
            }
            if (args[0] != "pty") {
                Console.Error.WriteLine($"Unknown command '{args[0]}'\n\n{Usage}");
                return 2;
            }

            var quirks = new List<string>();
            var echo = false;
            for (var i = 1; i < args.Length; i++) {
                if (args[i] == "--quirk" && i + 1 < args.Length) {
                    quirks.Add(args[++i]);
                } else if (args[i] == "--log") {
                    echo = true;
                } else {
                    Console.Error.WriteLine($"Unexpected argument '{args[i]}'");
                    return 2;
                }
            }

            SimOptions options;
            try {
                options = new SimOptions().Apply(quirks);
            } catch (ArgumentException ex) {
                Console.Error.WriteLine($"{ex.Message} ('lx200sim quirks' lists the switches)");
                return 2;
            }

            using var pty = PseudoTerminal.Open();
            using var sim = new AutostarSimulator(pty.Master, options);
            if (echo) {
                sim.CommandReceived += c => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} <- {c}");
            }
            using var done = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => {
                e.Cancel = true;
                done.Set();
            };
            Console.WriteLine(pty.SlavePath);
            Console.WriteLine($"lx200sim: {options.Describe()}");
            Console.WriteLine($"lx200sim: serving on {pty.SlavePath}; try  lx200probe checklist --port {pty.SlavePath}  (Ctrl+C to stop)");
            done.Wait();
            return 0;
        }
    }
}
