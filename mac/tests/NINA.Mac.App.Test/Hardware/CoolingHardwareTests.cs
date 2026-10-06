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
using NINA.Mac.App.Engine;
using NINA.Mac.App.Services;
using NINA.Mac.App.ViewModels;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZWOptical.ASISDK;
using Ctl = ZWOptical.ASISDK.ASICameraDll.ASI_CONTROL_TYPE;

namespace NINA.Mac.App.Test.Hardware {

    /// <summary>
    /// Cooling on the real camera through the whole app: AppServices with Settings › Devices = Real (the app's own composition,
    /// in a temporary home folder), the Cool screen's Start cooling, the app's 1 Hz tick, then the Teardown screen's run
    /// (the warm-up ramp, then disconnect). Needs the 12 V supply; without it the test records "no 12 V" and is inconclusive.
    /// The set point is never below 0 °C. Whatever happens, the cooler ends off: Teardown and the app's shutdown switch it off,
    /// and a raw SDK safety net switches it off again and reads ASI_COOLER_ON back.
    /// </summary>
    [TestFixture]
    [Category("Hardware")]
    [NonParallelizable]
    public class CoolingHardwareTests {

        private static void Log(string line) {
            var text = $"{DateTime.Now:HH:mm:ss.fff} {line}";
            TestContext.Out.WriteLine(text);
            TestContext.Progress.WriteLine(text);
        }

        [Test]
        [Explicit("Needs the ZWO ASI585MC Pro on USB and 12 V; run by hand (up to about 9 minutes)")]
        public async Task Cooling_ThroughTheApp_GuardStaysQuiet_TeardownWarmsAndSwitchesTheCoolerOff() {
            if (FindCameraId() == null) {
                Assert.Inconclusive("No ZWO ASI585MC on USB");
            }
            // Off the UI thread, without a synchronization context: the services raise Changed inline
            await Task.Run(RunAsync);
        }

        private static async Task RunAsync() {
            var home = Path.Combine(Path.GetTempPath(), "nightglass hw home " + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(home);
            var settings = new AppSettings { DeviceSource = DeviceSource.Real };
            AppServices services = null;
            int? readBack = null;
            using var tickerStop = new CancellationTokenSource();
            Task ticker = null;
            var samples = new ConcurrentQueue<(double Seconds, double? Temperature, double? Power, bool CoolerOn, string Warning)>();
            var clock = Stopwatch.StartNew();
            try {
                services = AppServices.Create(new AppServicesOptions {
                    Settings = new MemorySettingsStore(settings),
                    KeepAwake = new RecordingKeepAwake(),
                    PowerSource = new FakePowerSource(),
                    HomeDirectory = home,
                    SerialPortLister = () => Array.Empty<NINA.Mac.Platform.SerialPortInfo>(),
                });
                services.EngineError.Should().BeNull();
                services.Camera.Should().BeOfType<EngineCameraService>();
                Log($"engine data {EngineRuntime.DataDirectory}; warm-up rate {services.Settings.Current.WarmupRateCelsiusPerMinute} C/min, end {CameraWarmup.EndTemperature} C");

                // The app's 1 Hz timer
                ticker = Task.Run(async () => {
                    while (!tickerStop.IsCancellationRequested) {
                        try {
                            services.Tick();
                            var c = services.Camera;
                            samples.Enqueue((clock.Elapsed.TotalSeconds, c.SensorTemperature, c.CoolerPowerPercent, c.CoolerOn, c.HealthWarning));
                        } catch (Exception ex) {
                            Log($"tick failed: {ex.Message}");
                        }
                        try {
                            await Task.Delay(1000, tickerStop.Token);
                        } catch (OperationCanceledException) {
                            break;
                        }
                    }
                });

                var connect = new ConnectViewModel(services);
                var sw = Stopwatch.StartNew();
                await connect.ConnectCameraCommand.ExecuteAsync(null);
                Log($"Connect screen: camera {services.Camera.State} in {sw.Elapsed.TotalSeconds:0.00} s; error '{connect.ErrorMessage}'");
                services.Camera.State.Should().Be(DeviceConnectionState.Connected);
                var id = FindCameraId()!.Value;
                await Task.Delay(4000);
                var ambient = services.Camera.SensorTemperature ?? throw new InvalidOperationException("no sensor reading 4 s after connect");
                var target = Math.Max(0, Math.Round(ambient - 8));
                Log($"ambient (sensor, cooler off) {ambient} C -> target {target} C; SDK {Describe(id)}");

                var cool = new CoolViewModel(services) { TargetTemperature = target };
                var start = clock.Elapsed.TotalSeconds;
                cool.StartCoolingCommand.Execute(null);
                cool.ErrorMessage.Should().BeNull();
                Log($"Cool screen: Start cooling to {target} C; SDK {Describe(id)}");

                // Watch: 12 V check in the first 60 s, then up to 6 min in all; a line every 10 s
                var powered = false;
                var reached = (double?)null;
                var lastLog = -10.0;
                while (clock.Elapsed.TotalSeconds - start < 360) {
                    var t = clock.Elapsed.TotalSeconds - start;
                    var temp = services.Camera.SensorTemperature;
                    var power = services.Camera.CoolerPowerPercent;
                    if (t - lastLog >= 10) {
                        lastLog = t;
                        cool.Refresh();
                        Log($"t={t,5:0} s  sensor {temp,5:0.0} C  power {power,3:0} %  cooler {services.Camera.CoolerOn}  warning {services.Camera.HealthWarning ?? "-"}  status '{cool.StatusText}'  raw power {SafeRead(id, Ctl.ASI_COOLER_POWER_PERC)}");
                    }
                    if (!powered && power > 0 && temp < ambient - 0.5) {
                        powered = true;
                        Log($"12 V present: power {power} %, sensor {temp} C after {t:0} s");
                    }
                    if (!powered && t > 60) {
                        Log($"no 12 V: after 60 s power {power} %, sensor {temp} C (ambient {ambient} C)");
                        break;
                    }
                    if (reached == null && temp is { } tt && Math.Abs(tt - target) <= 0.3) {
                        reached = t;
                        Log($"reached {target} C after {t:0} s");
                    }
                    if (reached is { } r && t - r > 60) {
                        // A minute holding the set point is enough to watch the guard
                        break;
                    }
                    await Task.Delay(500);
                }
                var warnings = samples.Where(s => s.Warning != null).ToList();
                Log($"stuck-sensor guard during the ramp: {warnings.Count} warning tick(s) of {samples.Count}{(warnings.Count > 0 ? ": " + warnings[0].Warning : "")}");

                // Teardown screen: stop session (none), warm-up ramp, soft park (no mount), disconnect camera and mount, keep-awake
                var teardown = new TeardownViewModel(services);
                sw.Restart();
                var beforeWarm = (services.Camera.SensorTemperature, services.Camera.TargetTemperature, services.Camera.CoolerPowerPercent);
                await teardown.RunTeardownCommand.ExecuteAsync(null);
                Log($"Teardown in {sw.Elapsed.TotalSeconds:0} s from sensor {beforeWarm.SensorTemperature} C, set point {beforeWarm.TargetTemperature} C, power {beforeWarm.CoolerPowerPercent} %; error '{teardown.ErrorMessage}'");
                foreach (var step in teardown.Steps) {
                    Log($"  {step.Status,-8} {step.Title}: {step.Detail}");
                }
                services.Camera.State.Should().Be(DeviceConnectionState.Disconnected);
                // The camera is closed now: what did the app leave? (a closed ASI585MC keeps its cooler flag, see the engine tests)
                readBack = EnsureCoolerOff("after Teardown");

                teardown.ErrorMessage.Should().BeNull();
                teardown.WarmCamera.Status.Should().Be(TeardownStepStatus.Done);
                teardown.DisconnectCamera.Status.Should().Be(TeardownStepStatus.Done);
                readBack.Should().Be(0, "Teardown ends with the cooler off");
                if (!powered) {
                    Assert.Inconclusive("no 12 V: the cooler power never rose; cooling not tested (Teardown still ended with the cooler off)");
                }
                warnings.Should().BeEmpty("the stuck-sensor guard must not trigger during a normal ramp");
                reached.Should().NotBeNull($"the sensor reaches {target} C within 6 minutes on 12 V");
            } finally {
                tickerStop.Cancel();
                if (ticker != null) {
                    await ticker;
                }
                if (services != null) {
                    try {
                        await services.ShutdownDevicesAsync();
                    } catch (Exception ex) {
                        Log($"shutdown failed: {ex.Message}");
                    }
                    services.Dispose();
                }
                var final = EnsureCoolerOff("final");
                Log($"final ASI_COOLER_ON read back: {final?.ToString() ?? "n/a"}");
                NINA.Core.Utility.Logger.CloseAndFlush();
                if (Environment.GetEnvironmentVariable("NIGHTGLASS_KEEP_TEST_DATA") != "1") {
                    try {
                        Directory.Delete(home, true);
                    } catch (IOException) {
                    } catch (UnauthorizedAccessException) {
                    }
                } else {
                    Log($"test data kept in {home}");
                }
            }
        }

        private static int? FindCameraId() {
            var count = ASICameraDll.GetNumOfConnectedCameras();
            for (var i = 0; i < count; i++) {
                var info = ASICameraDll.GetCameraProperties(i);
                if (info.Name.Contains("ASI585MC", StringComparison.Ordinal)) {
                    return info.CameraID;
                }
            }
            return null;
        }

        private static string SafeRead(int id, Ctl type) {
            try {
                return ASICameraDll.GetControlValue(id, type, out _).ToString();
            } catch (Exception ex) {
                return $"({ex.Message.Split('\n')[0].Trim()})";
            }
        }

        private static string Describe(int id) => string.Join(", ", new[] { Ctl.ASI_COOLER_ON, Ctl.ASI_TARGET_TEMP, Ctl.ASI_COOLER_POWER_PERC, Ctl.ASI_TEMPERATURE }.Select(t => $"{t}={SafeRead(id, t)}"));

        /// <summary>The cooler off and ASI_COOLER_ON read back: through the open session, or by opening (and closing) the camera.</summary>
        private static int? EnsureCoolerOff(string context) {
            var id = FindCameraId();
            if (id == null) {
                Log($"[{context}] no camera on USB");
                return null;
            }
            try {
                var before = ASICameraDll.GetControlValue(id.Value, Ctl.ASI_COOLER_ON, out _);
                if (before != 0) {
                    ASICameraDll.SetControlValue(id.Value, Ctl.ASI_COOLER_ON, 0, false);
                }
                var after = ASICameraDll.GetControlValue(id.Value, Ctl.ASI_COOLER_ON, out _);
                Log($"[{context}] camera open: ASI_COOLER_ON was {before}, read back {after}");
                return after;
            } catch (ASICameraException) {
            }
            var opened = false;
            try {
                ASICameraDll.OpenCamera(id.Value);
                opened = true;
                int before;
                try {
                    before = ASICameraDll.GetControlValue(id.Value, Ctl.ASI_COOLER_ON, out _);
                } catch (ASICameraException) {
                    ASICameraDll.InitCamera(id.Value);
                    before = ASICameraDll.GetControlValue(id.Value, Ctl.ASI_COOLER_ON, out _);
                }
                if (before != 0) {
                    ASICameraDll.SetControlValue(id.Value, Ctl.ASI_COOLER_ON, 0, false);
                }
                var after = ASICameraDll.GetControlValue(id.Value, Ctl.ASI_COOLER_ON, out _);
                Log($"[{context}] reopened: ASI_COOLER_ON was {before}, read back {after}; sensor {SafeRead(id.Value, Ctl.ASI_TEMPERATURE)} (x0.1 C)");
                return after;
            } catch (Exception ex) {
                Log($"[{context}] cooler safety net failed: {ex.Message}");
                return null;
            } finally {
                if (opened) {
                    try {
                        ASICameraDll.CloseCamera(id.Value);
                    } catch (Exception ex) {
                        Log($"[{context}] close failed: {ex.Message}");
                    }
                }
            }
        }
    }
}
