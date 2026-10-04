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
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;

namespace NINA.Mac.Lx200 {

    /// <summary>Byte pipe to the mount. Reads never block longer than the timeout given.</summary>
    public interface ILx200Transport : IDisposable {

        string Name { get; }

        bool IsOpen { get; }

        void Write(ReadOnlySpan<byte> data);

        /// <summary>Next byte, or -1 when nothing arrives within <paramref name="timeout"/>.</summary>
        /// <exception cref="Lx200DisconnectedException">The stream ended or failed.</exception>
        int ReadByte(TimeSpan timeout);

        /// <summary>Returns and removes every byte already received.</summary>
        byte[] DrainAvailable();

        int BytesAvailable { get; }
    }

    /// <summary>
    /// Transport over any <see cref="Stream"/> (serial BaseStream, pseudo-terminal, in-memory pipe). A dedicated
    /// reader thread moves bytes into a queue and traces them at arrival; it never uses DataReceived, which drops
    /// bytes on Unix (RIM MNT-25, dotnet/runtime#106631).
    /// </summary>
    public sealed class StreamTransport : ILx200Transport {
        private readonly Stream stream;
        private readonly Lx200Trace trace;
        private readonly Action onDispose;
        private readonly Queue<byte> queue = new();
        private readonly object sync = new();
        private readonly Thread reader;
        private volatile bool disposed;
        private volatile bool ended;
        private Exception readError;

        public StreamTransport(Stream stream, string name, Lx200Trace trace, Action onDispose = null) {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.trace = trace;
            this.onDispose = onDispose;
            Name = name;
            reader = new Thread(ReadLoop) { IsBackground = true, Name = $"lx200-rx {name}", Priority = ThreadPriority.AboveNormal };
            reader.Start();
        }

        public string Name { get; }

        public bool IsOpen => !disposed && !ended;

        public int BytesAvailable {
            get {
                lock (sync) {
                    return queue.Count;
                }
            }
        }

        public void Write(ReadOnlySpan<byte> data) {
            if (!IsOpen) {
                throw new Lx200DisconnectedException($"{Name} is closed", readError);
            }
            trace?.Log(TraceKind.Tx, data);
            try {
                stream.Write(data);
                stream.Flush();
            } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException) {
                throw new Lx200DisconnectedException($"Write to {Name} failed: {ex.Message}", ex);
            }
        }

        public int ReadByte(TimeSpan timeout) {
            var sw = Stopwatch.StartNew();
            lock (sync) {
                while (queue.Count == 0) {
                    if (ended || disposed) {
                        throw new Lx200DisconnectedException($"{Name} closed{(readError != null ? ": " + readError.Message : "")}", readError);
                    }
                    var left = timeout - sw.Elapsed;
                    if (left <= TimeSpan.Zero) {
                        return -1;
                    }
                    Monitor.Wait(sync, left);
                }
                return queue.Dequeue();
            }
        }

        public byte[] DrainAvailable() {
            lock (sync) {
                var bytes = queue.ToArray();
                queue.Clear();
                return bytes;
            }
        }

        private void ReadLoop() {
            var buffer = new byte[256];
            try {
                while (!disposed) {
                    int n;
                    try {
                        n = stream.Read(buffer, 0, buffer.Length);
                    } catch (TimeoutException) {
                        continue;   // SerialPort BaseStream with a ReadTimeout: just poll again
                    }
                    if (n <= 0) {
                        break;
                    }
                    trace?.Log(TraceKind.Rx, buffer.AsSpan(0, n));
                    lock (sync) {
                        for (var i = 0; i < n; i++) {
                            queue.Enqueue(buffer[i]);
                        }
                        Monitor.PulseAll(sync);
                    }
                }
            } catch (Exception ex) {
                if (!disposed) {
                    readError = ex;
                    trace?.Log(TraceKind.Error, ReadOnlySpan<byte>.Empty, $"reader stopped: {ex.GetType().Name}: {ex.Message}");
                }
            } finally {
                lock (sync) {
                    ended = true;
                    Monitor.PulseAll(sync);
                }
            }
        }

        public void Dispose() {
            if (disposed) {
                return;
            }
            disposed = true;
            try {
                onDispose?.Invoke();
            } catch (Exception) {
                // closing a vanished USB device may throw; nothing left to do
            }
            try {
                stream.Dispose();
            } catch (Exception) {
                // as above
            }
            reader.Join(TimeSpan.FromSeconds(2));
            lock (sync) {
                Monitor.PulseAll(sync);
            }
        }
    }

    /// <summary>Serial port helpers for macOS: 9600 8N1, no handshake, DTR/RTS off (RIM MNT-22).</summary>
    public static class Lx200Serial {

        public const int BaudRate = 9600;

        /// <summary>Opens a port path such as /dev/cu.usbserial-XXXX with the LX200 settings.</summary>
        public static StreamTransport Open(string path, Lx200Trace trace) {
            var port = new SerialPort(path, BaudRate, Parity.None, 8, StopBits.One) {
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false,
                Encoding = Encoding.Latin1,   // never used for I/O here, but keeps 0xDF intact if someone does
                ReadTimeout = 100,            // the reader thread polls so Dispose never hangs in a blocking read
                WriteTimeout = 2000,
                ReadBufferSize = 4096
            };
            try {
                port.Open();
                // Report the requested DTR/RTS rather than reading them back: the getters issue TIOCMGET, which a
                // pseudo-terminal (the simulator's /dev/ttys*) rejects with ENOTTY.
                trace?.Note($"opened {path}: {port.BaudRate} {port.DataBits}{port.Parity.ToString()[0]}{(int)port.StopBits} handshake={port.Handshake} DTR=False RTS=False");
                return new StreamTransport(port.BaseStream, path, trace, () => port.Dispose());
            } catch {
                port.Dispose();
                throw;
            }
        }

        /// <summary>
        /// /dev/cu.* devices (the callout side; macOS also lists a /dev/tty.* twin for each, which the .NET
        /// GetPortNames returns as well). USB serial adapters (usbserial, usbmodem) first.
        /// </summary>
        public static IReadOnlyList<string> ListCalloutPorts(string devDirectory = "/dev") {
            if (!Directory.Exists(devDirectory)) {
                return Array.Empty<string>();
            }
            return Directory.GetFiles(devDirectory, "cu.*")
                .OrderBy(p => IsUsbSerial(p) ? 0 : 1)
                .ThenBy(p => p, StringComparer.Ordinal)
                .ToArray();
        }

        public static bool IsUsbSerial(string path) {
            var name = Path.GetFileName(path);
            return name.Contains("usbserial", StringComparison.OrdinalIgnoreCase) || name.Contains("usbmodem", StringComparison.OrdinalIgnoreCase);
        }
    }
}
