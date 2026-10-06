#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using NINA.Mac.App.Engine.Test.Fakes;
using NINA.Mac.App.Services;
using NINA.Mac.Equipment.Lx200;
using NINA.Mac.Equipment.Lx200.Test;
using NINA.Mac.Lx200.Sim;
using NINA.Mac.Platform;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZWOptical.ASISDK;

namespace NINA.Mac.App.Engine.Test.Hardware {

    /// <summary>
    /// The Real device services composed exactly as the app composes them with Settings › Devices = Real
    /// (<see cref="EngineDevices.Create"/> with the app's defaults), against the REAL camera: the camera list is NINA.Equipment.Mac's
    /// own CameraChooser behind the gate (the ZWO ASI585MC Pro on USB), the stuck-sensor guard and the 3 s first-reading delay
    /// are the app's defaults. Only what is not under test is replaced: the mount is the Autostar II simulator on an in-memory
    /// cable (the real LX200 is not connected), the plate solver is the fake that reports where the simulated mount points.
    /// The engine's data folder is the test assembly's temp folder (TestHost), images go to a temp folder.
    /// A background loop calls the services' Tick once a second, as the app's timer does (AppServices.Tick).
    /// Dispose shuts the engine down as the app does at quit and then, whatever happened, makes sure the cooler is off with a raw
    /// SDK call and reads ASI_COOLER_ON back (<see cref="CoolerReadBack"/>).
    /// </summary>
    internal sealed class HardwareRig : IAsyncDisposable {
        public const string Port = "/dev/cu.usbserial-SIMRIG";

        private readonly CancellationTokenSource tickerStop = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private volatile bool synced;
        private Task ticker;

        public HardwareRig(string name, Action<AppSettings> configure = null) {
            Folder = TestHost.NewFolder(name);
            ImagesRoot = Path.Combine(Folder, "images");
            // The app's defaults (gain 252, offset 8, bin 2, cooling target 0 °C, focus mono-bin on), Real devices
            Settings = new AppSettings {
                ImagesRoot = ImagesRoot,
                DeviceSource = DeviceSource.Real,
            };
            Settings.Site.LatitudeDegrees = Sky.Latitude;
            Settings.Site.LongitudeDegrees = Sky.Longitude;
            Settings.Site.ElevationMeters = Sky.Elevation;
            // No real ASTAP: the fake solver stands in, and drift recentring (which would use the profile's ASTAP) stays off
            Settings.Solver.AstapExecutable = Path.Combine(Folder, "no-astap", "astap_cli");
            Settings.Solver.RecenterArcmin = 0;
            configure?.Invoke(Settings);

            SimOptions = new SimOptions { PlanetaryUpdateSeconds = 0.2, SlewSeconds = 0.8 };
            Cable = new SimCable(SimOptions);
            Cable.Sim.CommandReceived += command => {
                if (command.StartsWith(":CM", StringComparison.Ordinal)) {
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

            Engine = EngineDevices.Create(new EngineDevicesOptions {
                Settings = () => Settings,
                ImagesRoot = () => Settings.ImagesRoot ?? ImagesRoot,
                // CameraChooser left null: the app's default, NINA.Equipment.Mac's CameraChooser (real ZWO cameras) behind the gate
                LinkPool = Pool,
                Lx200Clock = new Lx200Clock(null, _ => TimeSpan.FromHours(8)),
                PlateSolverFactory = new FakeSolverFactory(Solver),
                SerialPortLister = () => Ports,
                // StuckSensor and FirstReadingDelay left at the app's defaults (90 s / 15 points, 3 s)
            });

            var lx = Engine.Lx200.Telescope.Settings;
            lx.PortPath = Port;
            lx.PollIntervalMs = 200;
            lx.MinimumSlewSeconds = 0.6;
            var p = Engine.Profile.ActiveProfile;
            p.TelescopeSettings.SettleTime = 0;
            p.TelescopeSettings.TimeSync = false;
            p.GuiderSettings.SettleTime = 0;
            p.PlateSolveSettings.ExposureTime = 1;
            p.PlateSolveSettings.NumberOfAttempts = 1;
            p.PlateSolveSettings.ReattemptDelay = 0;
        }

        public string Folder { get; }

        public string ImagesRoot { get; }

        public AppSettings Settings { get; }

        public SimOptions SimOptions { get; }

        public SimCable Cable { get; }

        public Lx200LinkPool Pool { get; }

        public FakeSolver Solver { get; }

        public List<SerialPortInfo> Ports { get; }

        public EngineDevices Engine { get; }

        public EngineCameraService Camera => Engine.Camera;

        public IReadOnlyList<string> Received => Cable.Sim.ReceivedCommands;

        /// <summary>Every HealthWarning the 1 Hz tick saw (time since the ticker started, text).</summary>
        public ConcurrentQueue<(double Seconds, string Warning)> HealthWarnings { get; } = new();

        /// <summary>Every tick's readout: seconds since the rig was created, sensor temperature, cooler power, cooler on, warning.</summary>
        public ConcurrentQueue<(double Seconds, double? Temperature, double? Power, bool CoolerOn, string Warning)> Ticks { get; } = new();

        /// <summary>ASI_COOLER_ON read back by the raw SDK after the shutdown (null until disposed or when the camera was absent).</summary>
        public int? CoolerReadBack { get; private set; }

        public double Seconds => clock.Elapsed.TotalSeconds;

        public Coordinates Truth() {
            var (ra, dec) = Cable.Sim.BelievedRaDec;
            var believed = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), Epoch.JNOW).Transform(Epoch.J2000);
            if (synced) {
                return believed;
            }
            // 12' RA, -7' Dec pointing error until NINA syncs, as EngineRig
            var raError = 12.0 / 60 / Math.Cos(AstroUtil.ToRadians(believed.Dec));
            return new Coordinates(Angle.ByDegree(believed.RADegrees - raError), Angle.ByDegree(believed.Dec + (7.0 / 60)), Epoch.J2000);
        }

        /// <summary>The app's 1 Hz timer: camera and mount Tick (readouts, stuck-sensor guard, loss detection).</summary>
        public void StartTicker() {
            ticker ??= Task.Run(async () => {
                while (!tickerStop.IsCancellationRequested) {
                    try {
                        Engine.Camera.Tick();
                        Engine.Mount.Tick();
                        var warning = Engine.Camera.HealthWarning;
                        if (warning != null) {
                            HealthWarnings.Enqueue((Seconds, warning));
                        }
                        Ticks.Enqueue((Seconds, Engine.Camera.SensorTemperature, Engine.Camera.CoolerPowerPercent, Engine.Camera.CoolerOn, warning));
                    } catch (Exception ex) {
                        TestContext.Progress.WriteLine($"tick failed: {ex.Message}");
                    }
                    try {
                        await Task.Delay(1000, tickerStop.Token);
                    } catch (OperationCanceledException) {
                        break;
                    }
                }
            });
        }

        public static void Log(string line) {
            var text = $"{DateTime.Now:HH:mm:ss.fff} {line}";
            TestContext.Out.WriteLine(text);
            TestContext.Progress.WriteLine(text);
        }

        public async ValueTask DisposeAsync() {
            tickerStop.Cancel();
            if (ticker != null) {
                try {
                    await ticker;
                } catch (Exception) {
                }
            }
            try {
                await Engine.ShutdownAsync();
            } catch (Exception ex) {
                Log($"engine shutdown failed: {ex.Message}");
            } finally {
                Engine.Dispose();
                var parked = Received.Where(c => c.StartsWith(":hP", StringComparison.Ordinal)).ToList();
                Cable.Dispose();
                CoolerReadBack = Asi.EnsureCoolerOff("rig dispose");
                if (parked.Count > 0) {
                    Assert.Fail("the Autostar's park command reached the (simulated) mount");
                }
            }
        }
    }

    /// <summary>Raw ZWO SDK access for checks and the cooler safety net (the same process-wide SDK NINA's ASICamera uses).</summary>
    internal static class Asi {

        /// <summary>The ASI585MC's camera id, or null when no such camera is on USB.</summary>
        public static int? FindCameraId() {
            var count = ASICameraDll.GetNumOfConnectedCameras();
            for (var i = 0; i < count; i++) {
                var info = ASICameraDll.GetCameraProperties(i);
                if (info.Name.Contains("ASI585MC", StringComparison.Ordinal)) {
                    return info.CameraID;
                }
            }
            return null;
        }

        public static int RequireCamera() {
            var id = FindCameraId();
            if (id == null) {
                Assert.Inconclusive("No ZWO ASI585MC on USB (ASIGetNumOfConnectedCameras found none)");
            }
            return id.Value;
        }

        /// <summary>A control's value while NINA (or anyone in this process) has the camera open.</summary>
        public static int Read(int id, ASICameraDll.ASI_CONTROL_TYPE type) => ASICameraDll.GetControlValue(id, type, out _);

        public static string Describe(int id, params ASICameraDll.ASI_CONTROL_TYPE[] types) =>
            string.Join(", ", types.Select(t => $"{t}={SafeRead(id, t)}"));

        public static string SafeRead(int id, ASICameraDll.ASI_CONTROL_TYPE type) {
            try {
                return Read(id, type).ToString(CultureInfo.InvariantCulture);
            } catch (Exception ex) {
                return $"({ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()})";
            }
        }

        /// <summary>
        /// Safety net: the cooler off and ASI_COOLER_ON read back. Uses the open session when there is one; otherwise opens the
        /// camera (no init first: init could itself reset the cooler and hide what the app left), switches the cooler off when
        /// it is on, reads it back and closes the camera again. Returns the read-back, or null when there is no camera.
        /// </summary>
        public static int? EnsureCoolerOff(string context) {
            int? id;
            try {
                id = FindCameraId();
            } catch (Exception ex) {
                HardwareRig.Log($"[{context}] camera list failed: {ex.Message}");
                return null;
            }
            if (id == null) {
                HardwareRig.Log($"[{context}] no ASI585MC on USB: nothing to switch off");
                return null;
            }
            try {
                var before = Read(id.Value, ASICameraDll.ASI_CONTROL_TYPE.ASI_COOLER_ON);
                if (before != 0) {
                    ASICameraDll.SetControlValue(id.Value, ASICameraDll.ASI_CONTROL_TYPE.ASI_COOLER_ON, 0, false);
                }
                var after = Read(id.Value, ASICameraDll.ASI_CONTROL_TYPE.ASI_COOLER_ON);
                HardwareRig.Log($"[{context}] camera open: ASI_COOLER_ON was {before}, read back {after}");
                return after;
            } catch (ASICameraException) {
                // Closed: open it ourselves
            }
            var opened = false;
            try {
                ASICameraDll.OpenCamera(id.Value);
                opened = true;
                int before;
                try {
                    before = Read(id.Value, ASICameraDll.ASI_CONTROL_TYPE.ASI_COOLER_ON);
                } catch (ASICameraException) {
                    ASICameraDll.InitCamera(id.Value);
                    before = Read(id.Value, ASICameraDll.ASI_CONTROL_TYPE.ASI_COOLER_ON);
                }
                if (before != 0) {
                    ASICameraDll.SetControlValue(id.Value, ASICameraDll.ASI_CONTROL_TYPE.ASI_COOLER_ON, 0, false);
                }
                var after = Read(id.Value, ASICameraDll.ASI_CONTROL_TYPE.ASI_COOLER_ON);
                HardwareRig.Log($"[{context}] reopened: ASI_COOLER_ON was {before}, read back {after}");
                return after;
            } catch (Exception ex) {
                HardwareRig.Log($"[{context}] cooler safety net failed: {ex.Message}");
                return null;
            } finally {
                if (opened) {
                    try {
                        ASICameraDll.CloseCamera(id.Value);
                    } catch (Exception ex) {
                        HardwareRig.Log($"[{context}] close failed: {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>Pixel statistics of a raw frame read back from its FITS file.</summary>
    internal static class FrameStats {

        /// <summary>
        /// Means of the four 2x2 parity sub-planes (row parity, column parity) of a single-plane frame, and the "checkerboard index":
        /// (max - min) / mean of those four means. A lit colour (Bayer) frame shows R, G, G, B differences; a ZWO mono-bin frame,
        /// whose every pixel sums a whole Bayer cell, has none.
        /// </summary>
        public static (double[] Means, double Index, double Mean, double Median) Parity(float[] plane, int width, int height) {
            var sums = new double[4];
            var counts = new long[4];
            // Skip a border: amp glow and edge rows are not what is measured here
            var bx = width / 10;
            var by = height / 10;
            for (var y = by; y < height - by; y++) {
                var row = y * width;
                for (var x = bx; x < width - bx; x++) {
                    var k = ((y & 1) << 1) | (x & 1);
                    sums[k] += plane[row + x];
                    counts[k]++;
                }
            }
            var means = sums.Select((s, i) => s / counts[i]).ToArray();
            var mean = means.Average();
            var sample = plane.Where((_, i) => i % 7 == 0).OrderBy(v => v).ToArray();
            return (means, mean > 0 ? (means.Max() - means.Min()) / mean : 0, mean, sample[sample.Length / 2]);
        }
    }
}
