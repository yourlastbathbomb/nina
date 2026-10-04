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
using System.IO;
using System.Threading;

namespace NINA.Mac.Lx200.Sim {

    /// <summary>An in-memory, full-duplex byte pipe: what one end writes, the other end reads.</summary>
    public static class DuplexPipe {

        public static (Stream A, Stream B) Create() {
            var aToB = new ByteQueue();
            var bToA = new ByteQueue();
            return (new PipeEnd(bToA, aToB), new PipeEnd(aToB, bToA));
        }

        private sealed class ByteQueue {
            private readonly Queue<byte> queue = new();
            private bool closed;

            public void Write(ReadOnlySpan<byte> data) {
                lock (queue) {
                    if (closed) {
                        throw new IOException("Pipe closed");
                    }
                    foreach (var b in data) {
                        queue.Enqueue(b);
                    }
                    Monitor.PulseAll(queue);
                }
            }

            public int Read(Span<byte> buffer) {
                lock (queue) {
                    while (queue.Count == 0) {
                        if (closed) {
                            return 0;
                        }
                        Monitor.Wait(queue);
                    }
                    var n = 0;
                    while (n < buffer.Length && queue.Count > 0) {
                        buffer[n++] = queue.Dequeue();
                    }
                    return n;
                }
            }

            public void Close() {
                lock (queue) {
                    closed = true;
                    Monitor.PulseAll(queue);
                }
            }
        }

        private sealed class PipeEnd : Stream {
            private readonly ByteQueue incoming;
            private readonly ByteQueue outgoing;
            private bool disposed;

            public PipeEnd(ByteQueue incoming, ByteQueue outgoing) {
                this.incoming = incoming;
                this.outgoing = outgoing;
            }

            public override bool CanRead => !disposed;

            public override bool CanSeek => false;

            public override bool CanWrite => !disposed;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => incoming.Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer) => incoming.Read(buffer);

            public override void Write(byte[] buffer, int offset, int count) => outgoing.Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer) => outgoing.Write(buffer);

            public override void Flush() {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing) {
                if (!disposed) {
                    disposed = true;
                    incoming.Close();
                    outgoing.Close();
                }
                base.Dispose(disposing);
            }
        }
    }
}
