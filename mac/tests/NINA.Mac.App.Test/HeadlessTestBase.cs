#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Avalonia;
using Avalonia.Headless;
using NINA.Mac.App;
using NINA.Mac.App.Services;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Test {

    /// <summary>Avalonia entry point for the headless test session: the real App, Skia rendering, no windowing system.</summary>
    public static class TestAppBuilder {

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    /// <summary>
    /// One Avalonia headless session for the whole assembly. Avalonia.Headless.NUnit would provide [AvaloniaTest],
    /// but its 12.x releases require NUnit 4.5.1 and the mac test stack pins 4.4.0, so tests dispatch onto the
    /// session's UI thread through <see cref="HeadlessTestBase.OnUi"/>.
    /// </summary>
    [SetUpFixture]
    public sealed class HeadlessSession {

        public static HeadlessUnitTestSession Session { get; private set; }

        [OneTimeSetUp]
        public void Start() {
            Session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        }

        [OneTimeTearDown]
        public void Stop() {
            Session?.Dispose();
            Session = null;
        }
    }

    public abstract class HeadlessTestBase {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        protected static void OnUi(Action action) {
            using var cts = new CancellationTokenSource(Timeout);
            HeadlessSession.Session.Dispatch(action, cts.Token).GetAwaiter().GetResult();
        }

        protected static T OnUi<T>(Func<T> func) {
            using var cts = new CancellationTokenSource(Timeout);
            return HeadlessSession.Session.Dispatch(func, cts.Token).GetAwaiter().GetResult();
        }

        protected static void OnUiAsync(Func<Task> func) {
            using var cts = new CancellationTokenSource(Timeout);
            HeadlessSession.Session.Dispatch(async () => {
                await func();
                return true;
            }, cts.Token).GetAwaiter().GetResult();
        }

        /// <summary>Services on a manual clock with in-memory settings and a recording keep-awake: nothing touches the real Mac.</summary>
        protected static AppServices CreateServices(DateTimeOffset? start = null, AppSettings settings = null, RecordingKeepAwake keepAwake = null) =>
            AppServices.Create(new AppServicesOptions {
                Clock = new ManualClock(start ?? TestTimes.EveningOct10),
                Settings = new MemorySettingsStore(settings),
                KeepAwake = keepAwake ?? new RecordingKeepAwake(),
                PowerSource = new FakePowerSource(),
                HomeDirectory = "/Users/test",
                SerialPortLister = () => new[] { new NINA.Mac.Platform.SerialPortInfo("/dev/cu.usbserial-A10KX5Z3", true), new NINA.Mac.Platform.SerialPortInfo("/dev/cu.Bluetooth-Incoming-Port", false) },
                FastSimulation = true,
            });

        protected static string ScreenshotDirectory {
            get {
                var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "screens");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }

    public static class TestTimes {

        /// <summary>21:00 HKT on a clear October night (NGC 253 rising in the south-east).</summary>
        public static readonly DateTimeOffset EveningOct10 = new(2026, 10, 10, 21, 0, 0, TimeSpan.FromHours(8));
    }

    public static class Shell {

        /// <summary>Runs a command and returns stdout (throws on a non-zero exit).</summary>
        public static string Run(string file, string arguments, string workingDirectory = null, int timeoutMs = 60000) {
            var psi = new ProcessStartInfo(file, arguments) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            };
            using var p = Process.Start(psi);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) {
                p.Kill(true);
                throw new TimeoutException($"{file} {arguments} timed out");
            }
            if (p.ExitCode != 0) {
                throw new InvalidOperationException($"{file} {arguments} exited {p.ExitCode}: {stderr.Result}{stdout.Result}");
            }
            return stdout.Result;
        }
    }
}
