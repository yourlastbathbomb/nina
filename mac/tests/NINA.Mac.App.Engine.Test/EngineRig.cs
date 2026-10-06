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
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Mac.App.Engine.Test.Fakes;
using NINA.Mac.App.Services;
using NINA.Mac.Equipment.Lx200;
using NINA.Mac.Equipment.Lx200.Test;
using NINA.Mac.Lx200.Sim;
using NINA.Mac.Platform;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine.Test {

    /// <summary>
    /// The Real device services composed the way the app composes them (<see cref="EngineDevices.Create"/>), with hardware
    /// replaced: a <see cref="FakeCamera"/> in the camera list, the Autostar II simulator behind an in-memory cable for the
    /// LX200 driver, a fake plate solver that reports where the simulated mount really points, and a dawn an hour away.
    /// Dispose shuts the engine down as the app does at quit and checks that the cooler ended off and that the simulator never
    /// received the Autostar's park command.
    /// </summary>
    internal sealed class EngineRig : IAsyncDisposable {
        public const string Port = "/dev/cu.usbserial-SIMRIG";

        private volatile bool synced;

        public EngineRig(string name, Action<AppSettings> configure = null, IClock clock = null, StuckSensorGuardOptions stuckSensor = null,
                TimeSpan? firstReadingDelay = null, double pointingErrorRaArcmin = 12, double pointingErrorDecArcmin = -7, DateTime? dawn = null,
                Action<EngineDevicesOptions> configureOptions = null) {
            Folder = TestHost.NewFolder(name);
            ImagesRoot = Path.Combine(Folder, "images");
            Settings = new AppSettings {
                ImagesRoot = ImagesRoot,
                DeviceSource = DeviceSource.Real,
            };
            Settings.Site.LatitudeDegrees = Sky.Latitude;
            Settings.Site.LongitudeDegrees = Sky.Longitude;
            Settings.Site.ElevationMeters = Sky.Elevation;
            // No real ASTAP in the tests: the fake solver factory stands in (and drift checks, which use the profile's ASTAP, stay off)
            Settings.Solver.AstapExecutable = Path.Combine(Folder, "no-astap", "astap_cli");
            Settings.Solver.RecenterArcmin = 0;
            configure?.Invoke(Settings);

            ErrorRaArcmin = pointingErrorRaArcmin;
            ErrorDecArcmin = pointingErrorDecArcmin;
            SimOptions = new SimOptions { PlanetaryUpdateSeconds = 0.2, SlewSeconds = 0.8 };
            Cable = new SimCable(SimOptions);
            Cable.Sim.CommandReceived += command => {
                if (command.StartsWith(":CM", StringComparison.Ordinal)) {
                    // NINA syncs to the solved position: from here on the mount's belief is the sky
                    synced = true;
                }
            };
            Pool = new Lx200LinkPool(_ => Cable.Open, () => new Lx200LinkOptions {
                ReconnectInterval = TimeSpan.FromMilliseconds(200),
                WaitForReconnect = TimeSpan.FromSeconds(3),
                ReconnectGiveUp = null,
            });
            Solver = new FakeSolver(Truth);
            Ports = new List<SerialPortInfo> { new("/dev/cu.Bluetooth-Incoming-Port", false), new(Port, true) };

            var options = new EngineDevicesOptions {
                Settings = () => Settings,
                // As the app: the images root follows the settings (Settings › Storage)
                ImagesRoot = () => Settings.ImagesRoot ?? ImagesRoot,
                Clock = clock,
                CameraChooser = (profile, exposureDataFactory) => {
                    Camera = new FakeCamera(exposureDataFactory, profileService: profile);
                    Chooser = new FixedDeviceChooser(new DummyDevice("No Camera"), Camera);
                    return Chooser;
                },
                LinkPool = Pool,
                Lx200Clock = new Lx200Clock(null, _ => TimeSpan.FromHours(8)),
                PlateSolverFactory = new FakeSolverFactory(Solver),
                NighttimeCalculator = new FixedNighttimeCalculator(dawn ?? DateTime.Now.AddHours(1)),
                SerialPortLister = () => Ports,
                StuckSensor = stuckSensor,
                FirstReadingDelay = firstReadingDelay ?? TimeSpan.Zero,
            };
            configureOptions?.Invoke(options);
            Engine = EngineDevices.Create(options);

            // Faster than the rig's defaults, as the driver and sequencer tests run them
            var lx = Engine.Lx200.Telescope.Settings;
            lx.PortPath = Port;
            lx.PollIntervalMs = 200;
            lx.MinimumSlewSeconds = 0.6;
            var p = Engine.Profile.ActiveProfile;
            p.TelescopeSettings.SettleTime = 0;
            p.TelescopeSettings.TimeSync = false;
            p.GuiderSettings.SettleTime = 0;
            p.GuiderSettings.DitherPixels = 5;
            p.PlateSolveSettings.ExposureTime = 1;
            p.PlateSolveSettings.NumberOfAttempts = 1;
            p.PlateSolveSettings.ReattemptDelay = 0;
            p.ApplicationSettings.DevicePollingInterval = 0.5;
        }

        public string Folder { get; }

        public string ImagesRoot { get; }

        public AppSettings Settings { get; }

        public double ErrorRaArcmin { get; }

        public double ErrorDecArcmin { get; }

        public SimOptions SimOptions { get; }

        public SimCable Cable { get; }

        public AutostarSimulator Sim => Cable.Sim;

        public Lx200LinkPool Pool { get; }

        public FakeSolver Solver { get; }

        public List<SerialPortInfo> Ports { get; }

        public EngineDevices Engine { get; }

        /// <summary>Created when the engine builds its camera list.</summary>
        public FakeCamera Camera { get; private set; }

        public FixedDeviceChooser Chooser { get; private set; }

        public IReadOnlyList<string> Received => Sim.ReceivedCommands;

        /// <summary>Where the simulated mount really points (J2000): its belief, off by the pointing error until NINA syncs.</summary>
        public Coordinates Truth() {
            var (ra, dec) = Sim.BelievedRaDec;
            var believed = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), Epoch.JNOW).Transform(Epoch.J2000);
            if (synced) {
                return believed;
            }
            var raError = ErrorRaArcmin / 60 / Math.Cos(AstroUtil.ToRadians(believed.Dec));
            return new Coordinates(Angle.ByDegree(believed.RADegrees - raError), Angle.ByDegree(believed.Dec - (ErrorDecArcmin / 60)), Epoch.J2000);
        }

        public async Task ConnectAll() {
            await Engine.Camera.ConnectAsync();
            await Engine.Mount.ConnectAsync();
            Engine.Camera.State.Should().Be(DeviceConnectionState.Connected);
            Engine.Mount.State.Should().Be(DeviceConnectionState.Connected);
        }

        public async ValueTask DisposeAsync() {
            try {
                await Engine.ShutdownAsync();
            } finally {
                Engine.Dispose();
                var parked = Received.Where(c => c.StartsWith(":hP", StringComparison.Ordinal)).ToList();
                Cable.Dispose();
                parked.Should().BeEmpty("the Autostar's park command must never reach the mount");
                if (Camera != null) {
                    Camera.CoolerOn.Should().BeFalse("the cooler must end off on every exit path");
                }
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
    }

    /// <summary>
    /// Process-wide host set-up, as the app does it: the engine's data folder (NINA's Logs/, Profiles/, PlateSolver/) in a fresh
    /// temp folder, and the WPF-compat Application, before any test touches NINA.
    /// </summary>
    internal static class TestHost {

        public static string DataRoot { get; private set; } = string.Empty;

        [ModuleInitializer]
        internal static void Initialize() {
            // A space in the path on purpose, like ~/Library/Application Support
            DataRoot = Path.Combine(Path.GetTempPath(), "nightglass engine test " + Guid.NewGuid().ToString("N"));
            EngineRuntime.Initialize(Path.Combine(DataRoot, "Engine"));
        }

        public static string NewFolder(string name) {
            var folder = Path.Combine(DataRoot, "Work", name + " " + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}

/// <summary>Runs once after every fixture: flushes NINA's log and removes the data folder (NIGHTGLASS_KEEP_TEST_DATA=1 keeps it).</summary>
[NUnit.Framework.SetUpFixture]
public class EngineTestTeardown {

    [NUnit.Framework.OneTimeTearDown]
    public void Stop() {
        NINA.Core.Utility.Logger.CloseAndFlush();
        if (Environment.GetEnvironmentVariable("NIGHTGLASS_KEEP_TEST_DATA") == "1") {
            NUnit.Framework.TestContext.Progress.WriteLine($"Test data kept in {NINA.Mac.App.Engine.Test.TestHost.DataRoot}");
            return;
        }
        try {
            Directory.Delete(NINA.Mac.App.Engine.Test.TestHost.DataRoot, true);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}
