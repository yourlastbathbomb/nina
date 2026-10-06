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
using System.IO;
using System.Threading;

namespace NINA.Mac.Equipment.Lx200.Test {

    /// <summary>
    /// The Autostar II simulator behind a USB-serial "cable" that can be unplugged and plugged back in. The simulator keeps
    /// running (it is the mount, which stays powered); each <see cref="Open"/> is a new port session over a fresh in-memory
    /// pipe, like reopening /dev/cu.usbserial-* after the adapter comes back. Unplugging closes the session's pipe (the driver's
    /// reader sees the stream end, as on a vanished device node), and while unplugged <see cref="Open"/> throws as opening a
    /// missing /dev node does. Optionally the cable runs at a real link's pace: each byte takes 10 bits at
    /// <see cref="BaudRate"/> and every reply waits <see cref="Turnaround"/> first (Autostar plus FTDI latency, RIM Table 5).
    /// </summary>
    internal sealed class SimCable : IDisposable {
        private readonly object sync = new();
        private readonly Stream simSide;
        private readonly Thread fromSim;
        private Stream session;
        private bool plugged = true;
        private bool muted;
        private bool disposed;

        public SimCable(SimOptions options, int baudRate = 0, TimeSpan turnaround = default) {
            var (ours, sim) = DuplexPipe.Create();
            simSide = ours;
            Sim = new AutostarSimulator(sim, options);
            BaudRate = baudRate;
            Turnaround = turnaround;
            fromSim = new Thread(PumpFromSim) { IsBackground = true, Name = "sim-cable rx" };
            fromSim.Start();
        }

        public AutostarSimulator Sim { get; }

        /// <summary>0 = instant.</summary>
        public int BaudRate { get; }

        public TimeSpan Turnaround { get; }

        public int Opens { get; private set; }

        public int FailedOpens { get; private set; }

        public bool Plugged {
            get {
                lock (sync) {
                    return plugged;
                }
            }
        }

        /// <summary>Opens a port session (the link's transport opener).</summary>
        public ILx200Transport Open(Lx200Trace trace) {
            lock (sync) {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!plugged) {
                    FailedOpens++;
                    throw new IOException("No such file or directory: /dev/cu.usbserial-SIM (adapter unplugged)");
                }
                CloseSessionLocked();
                var (client, ours) = DuplexPipe.Create();
                session = ours;
                var toSim = new Thread(() => PumpToSim(ours)) { IsBackground = true, Name = "sim-cable tx" };
                toSim.Start();
                Opens++;
                return new StreamTransport(client, "sim-cable", trace);
            }
        }

        /// <summary>Pulls the USB adapter: the open session ends, and opening fails until <see cref="Replug"/>.</summary>
        public void Unplug() {
            lock (sync) {
                plugged = false;
                CloseSessionLocked();
            }
        }

        public void Replug() {
            lock (sync) {
                plugged = true;
            }
        }

        /// <summary>Bytes that reach the Mac as if the mount had sent them (a stray byte, line noise), right now.</summary>
        public void Inject(params byte[] bytes) {
            Stream target;
            lock (sync) {
                target = session;
            }
            try {
                target?.Write(bytes, 0, bytes.Length);
            } catch (IOException) {
            } catch (ObjectDisposedException) {
            }
        }

        /// <summary>The cable stays, but nothing comes back (mount switched off): the session stays open and silent.</summary>
        public void Mute(bool value) {
            lock (sync) {
                muted = value;
            }
        }

        private void CloseSessionLocked() {
            try {
                session?.Dispose();
            } catch (Exception) {
                // already closed
            }
            session = null;
        }

        private void PumpToSim(Stream from) {
            var buffer = new byte[64];
            try {
                while (true) {
                    var n = from.Read(buffer, 0, buffer.Length);
                    if (n <= 0) {
                        return;
                    }
                    Wire(n);
                    lock (sync) {
                        if (!ReferenceEquals(session, from)) {
                            return;   // unplugged while the bytes were on the wire
                        }
                    }
                    simSide.Write(buffer, 0, n);
                }
            } catch (Exception) {
                // session closed
            }
        }

        private void PumpFromSim() {
            var buffer = new byte[256];
            try {
                while (true) {
                    var n = simSide.Read(buffer, 0, buffer.Length);
                    if (n <= 0) {
                        return;
                    }
                    if (Turnaround > TimeSpan.Zero) {
                        Thread.Sleep(Turnaround);
                    }
                    Wire(n);
                    Stream target;
                    lock (sync) {
                        target = plugged && !muted ? session : null;
                    }
                    try {
                        target?.Write(buffer, 0, n);
                    } catch (IOException) {
                        // the session closed meanwhile: the bytes are lost, as on a pulled cable
                    } catch (ObjectDisposedException) {
                    }
                }
            } catch (Exception) {
                // cable disposed
            }
        }

        private void Wire(int bytes) {
            if (BaudRate > 0) {
                Thread.Sleep(TimeSpan.FromMilliseconds(bytes * 10.0 * 1000.0 / BaudRate));
            }
        }

        public void Dispose() {
            lock (sync) {
                if (disposed) {
                    return;
                }
                disposed = true;
                CloseSessionLocked();
            }
            Sim.Dispose();
            simSide.Dispose();
        }
    }
}
