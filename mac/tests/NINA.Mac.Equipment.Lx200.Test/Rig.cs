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
using Moq;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Mac.Lx200;
using NINA.Mac.Lx200.Sim;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200.Test {

    /// <summary>
    /// The rig on the bench without hardware: a real NINA <see cref="NINA.Profile.Profile"/> for Deep Water Bay (22.25 N,
    /// 114.18 E, ASI585MC 2.9 µm on 2500 mm), the simulator behind a <see cref="SimCable"/>, a link pool that opens the cable,
    /// and the provider's telescope and focuser. Every test that builds one checks on dispose that the simulator never received
    /// the Autostar's park command.
    /// </summary>
    internal sealed class Rig : IDisposable {
        public const string Port = "/dev/cu.usbserial-SIM";

        public Rig(SimOptions sim = null, Action<Lx200Settings> configure = null, int baudRate = 0, TimeSpan turnaround = default,
                   Lx200LinkOptions linkOptions = null, DateTime? clockUtc = null) {
            SimOptions = sim ?? new SimOptions();
            if (sim == null) {
                SimOptions.PlanetaryUpdateSeconds = 0.2;
                SimOptions.SlewSeconds = 0.8;
            }
            if (clockUtc.HasValue) {
                var start = clockUtc.Value;
                var elapsed = Stopwatch.StartNew();
                SimOptions.UtcNow = () => start + elapsed.Elapsed;
            }
            Clock = new Lx200Clock(SimOptions.UtcNow, _ => TimeSpan.FromHours(8));
            Cable = new SimCable(SimOptions, baudRate, turnaround);

            Profile = new NINA.Profile.Profile("Rig");
            Profile.AstrometrySettings.Latitude = 22.25;
            Profile.AstrometrySettings.Longitude = 114.18;
            Profile.AstrometrySettings.Elevation = 10;
            Profile.CameraSettings.PixelSize = 2.9;
            Profile.TelescopeSettings.FocalLength = 2500;
            Profile.TelescopeSettings.TimeSync = false;
            Profile.GuiderSettings.SettleTime = 0;
            ProfileService = new Mock<IProfileService>();
            ProfileService.SetupGet(p => p.ActiveProfile).Returns(Profile);

            Settings = new Lx200Settings(ProfileService.Object) {
                PortPath = Port,
                PollIntervalMs = 200,
                MinimumSlewSeconds = 0.6
            };
            configure?.Invoke(Settings);
            LinkOptions = linkOptions ?? new Lx200LinkOptions {
                ReconnectInterval = TimeSpan.FromMilliseconds(200),
                WaitForReconnect = TimeSpan.FromSeconds(3),
                ReconnectGiveUp = null
            };
            Pool = new Lx200LinkPool(port => Cable.Open, () => LinkOptions);
            Provider = new Lx200EquipmentProvider(ProfileService.Object, Pool, Clock);
        }

        public SimOptions SimOptions { get; }

        public Lx200Clock Clock { get; }

        public SimCable Cable { get; }

        public AutostarSimulator Sim => Cable.Sim;

        public NINA.Profile.Profile Profile { get; }

        public Mock<IProfileService> ProfileService { get; }

        public Lx200Settings Settings { get; }

        public Lx200LinkOptions LinkOptions { get; }

        public Lx200LinkPool Pool { get; }

        public Lx200EquipmentProvider Provider { get; }

        public Lx200Telescope Telescope => Provider.Telescope;

        public Lx200Focuser Focuser => Provider.Focuser;

        public Lx200Link Link => Pool.Find(Port);

        public async Task<Lx200Telescope> ConnectTelescope() {
            (await Telescope.Connect(CancellationToken.None)).Should().BeTrue("the simulated mount answers");
            return Telescope;
        }

        public async Task<Lx200Focuser> ConnectFocuser() {
            (await Focuser.Connect(CancellationToken.None)).Should().BeTrue("the simulated mount answers");
            return Focuser;
        }

        /// <summary>Commands the simulator received, as ":GR#" text, in order.</summary>
        public IReadOnlyList<string> Received => Sim.ReceivedCommands;

        public IReadOnlyList<SimLogEntry> SimLog => Sim.Log;

        /// <summary>TX entries of the link's trace as text, with their link-clock times.</summary>
        public IReadOnlyList<(DateTime Utc, string Text)> Transmitted(Lx200Trace trace = null) {
            trace ??= Link?.Trace ?? throw new InvalidOperationException("no link");
            return trace.Snapshot().Where(e => e.Kind == TraceKind.Tx).Select(e => (e.Utc, Encoding.Latin1.GetString(e.Bytes))).ToList();
        }

        public void Dispose() {
            try {
                Telescope.Disconnect();
                Focuser.Disconnect();
            } finally {
                var parked = Sim.ReceivedCommands.Where(c => c.StartsWith(":hP", StringComparison.Ordinal)).ToList();
                Cable.Dispose();
                parked.Should().BeEmpty("the Autostar's park command must never reach the mount");
            }
        }

        /// <summary>Polls <paramref name="condition"/> every 20 ms until it holds or the time is up.</summary>
        public static async Task<bool> Eventually(Func<bool> condition, TimeSpan timeout) {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout) {
                if (condition()) {
                    return true;
                }
                await Task.Delay(20);
            }
            return condition();
        }

        /// <summary>Collects NINA notifications while the scope is open.</summary>
        public static NotificationCapture CaptureNotifications() => new NotificationCapture();

        internal sealed class NotificationCapture : IDisposable {
            private readonly List<NotificationPostedEventArgs> posted = new();

            public NotificationCapture() {
                Notification.Posted += OnPosted;
            }

            public IReadOnlyList<NotificationPostedEventArgs> Posted {
                get {
                    lock (posted) {
                        return posted.ToArray();
                    }
                }
            }

            private void OnPosted(object sender, NotificationPostedEventArgs e) {
                lock (posted) {
                    posted.Add(e);
                }
            }

            public void Dispose() {
                Notification.Posted -= OnPosted;
            }
        }
    }

    /// <summary>
    /// Process-wide host setup: NINA's data folder (logs, profiles) in a temp folder before anything touches Logger, as the
    /// other mac test projects do (NINA.Mac.Equipment.Test's TestHost).
    /// </summary>
    internal static class TestHost {

        public static string DataRoot { get; private set; } = string.Empty;

        [ModuleInitializer]
        internal static void Initialize() {
            DataRoot = Path.Combine(Path.GetTempPath(), "nina-mac-lx200-driver-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataRoot);
            CoreUtil.APPLICATIONTEMPPATH = DataRoot;
        }
    }

    [SetUpFixture]
    public class Lx200DriverTestTeardown {

        [OneTimeTearDown]
        public void Stop() {
            Logger.CloseAndFlush();
            if (Environment.GetEnvironmentVariable("NINA_MAC_KEEP_TEST_DATA") == "1") {
                TestContext.Progress.WriteLine($"Test data kept in {TestHost.DataRoot}");
                return;
            }
            try {
                Directory.Delete(TestHost.DataRoot, true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }
    }
}
