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
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NINA.Mac.Lx200.Sim {

    /// <summary>
    /// A pseudo-terminal pair from libc openpty(3). The simulator serves the master side; any serial client
    /// (System.IO.Ports included) opens <see cref="SlavePath"/> (/dev/ttysNNN) like a real port. The slave is put
    /// in raw mode (no echo, no line editing) and one slave descriptor stays open here so the master never sees
    /// a hang-up between client sessions.
    /// </summary>
    public sealed class PseudoTerminal : IDisposable {

        private const int TCSANOW = 0;
        private const short POLLIN = 0x0001;

        private readonly int masterFd;
        private readonly int slaveFd;
        private bool disposed;

        private PseudoTerminal(int masterFd, int slaveFd, string slavePath) {
            this.masterFd = masterFd;
            this.slaveFd = slaveFd;
            SlavePath = slavePath;
            Master = new MasterStream(this);
        }

        public string SlavePath { get; }

        /// <summary>Byte stream of the master side: what the client writes is read here and vice versa.</summary>
        public Stream Master { get; }

        public static bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

        public static PseudoTerminal Open() {
            if (!IsSupported) {
                throw new PlatformNotSupportedException("openpty needs macOS or Linux");
            }
            var name = new byte[128];
            if (openpty(out var master, out var slave, name, IntPtr.Zero, IntPtr.Zero) != 0) {
                throw new IOException($"openpty failed, errno {Marshal.GetLastPInvokeError()}");
            }
            var len = Array.IndexOf(name, (byte)0);
            var path = Encoding.ASCII.GetString(name, 0, len < 0 ? name.Length : len);
            // termios is 72 bytes on macOS arm64; a larger buffer avoids depending on its layout
            var termios = new byte[512];
            if (tcgetattr(slave, termios) == 0) {
                cfmakeraw(termios);
                tcsetattr(slave, TCSANOW, termios);
            }
            return new PseudoTerminal(master, slave, path);
        }

        public void Dispose() {
            if (disposed) {
                return;
            }
            disposed = true;
            close(masterFd);
            close(slaveFd);
        }

        private int ReadMaster(byte[] buffer, int offset, int count) {
            var fds = new PollFd[] { new() { fd = masterFd, events = POLLIN } };
            while (!disposed) {
                fds[0].revents = 0;
                var ready = poll(fds, 1, 100);
                if (disposed) {
                    return 0;
                }
                if (ready < 0) {
                    Thread.Sleep(10);   // EINTR
                    continue;
                }
                if (ready == 0) {
                    continue;
                }
                if ((fds[0].revents & POLLIN) == 0) {
                    Thread.Sleep(20);   // POLLHUP without data: no client right now
                    continue;
                }
                var n = (int)read(masterFd, ref buffer[offset], count);
                if (n > 0) {
                    return n;
                }
                if (n == 0) {
                    Thread.Sleep(20);
                    continue;
                }
                var errno = Marshal.GetLastPInvokeError();
                if (errno is 4 or 35 or 5) {   // EINTR, EAGAIN, EIO (no slave open): wait for a client
                    Thread.Sleep(20);
                    continue;
                }
                throw new IOException($"pty read failed, errno {errno}");
            }
            return 0;
        }

        private void WriteMaster(byte[] buffer, int offset, int count) {
            while (count > 0) {
                if (disposed) {
                    throw new ObjectDisposedException(nameof(PseudoTerminal));
                }
                var n = (int)write(masterFd, ref buffer[offset], count);
                if (n < 0) {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno is 4 or 35) {
                        Thread.Sleep(5);
                        continue;
                    }
                    throw new IOException($"pty write failed, errno {errno}");
                }
                offset += n;
                count -= n;
            }
        }

        private sealed class MasterStream : Stream {
            private readonly PseudoTerminal pty;

            public MasterStream(PseudoTerminal pty) {
                this.pty = pty;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => count == 0 ? 0 : pty.ReadMaster(buffer, offset, count);

            public override void Write(byte[] buffer, int offset, int count) {
                if (count > 0) {
                    pty.WriteMaster(buffer, offset, count);
                }
            }

            public override void Flush() {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing) {
                pty.Dispose();
                base.Dispose(disposing);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd {
            public int fd;
            public short events;
            public short revents;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int openpty(out int amaster, out int aslave, [In, Out] byte[] name, IntPtr termp, IntPtr winp);

        [DllImport("libc", SetLastError = true)]
        private static extern int tcgetattr(int fd, [In, Out] byte[] termios);

        [DllImport("libc", SetLastError = true)]
        private static extern int tcsetattr(int fd, int optionalActions, byte[] termios);

        [DllImport("libc")]
        private static extern void cfmakeraw([In, Out] byte[] termios);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern nint read(int fd, ref byte buffer, nint count);

        [DllImport("libc", SetLastError = true)]
        private static extern nint write(int fd, ref byte buffer, nint count);

        [DllImport("libc", SetLastError = true)]
        private static extern int poll([In, Out] PollFd[] fds, uint nfds, int timeout);
    }
}
