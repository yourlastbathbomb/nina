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
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Mac.App.Services;
using NINA.Mac.Siril;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZWOptical.ASISDK;
using Ctl = ZWOptical.ASISDK.ASICameraDll.ASI_CONTROL_TYPE;

namespace NINA.Mac.App.Engine.Test.Hardware {

    /// <summary>
    /// M8 on the real camera: the app's EngineCameraService (Settings › Devices = Real) driving the ZWO ASI585MC Pro through
    /// NINA's CameraVM, ImagingVM and ASICamera. Connect and disconnect, exposures (colour lights, mono-bin focus frames), frame
    /// timing on USB 3 and the dew-heater control. Explicit: run one at a time by hand with the camera on USB 3 (and, for
    /// cooler tests, 12 V). The cooler is switched off on every exit path and ASI_COOLER_ON is read back with the raw SDK.
    /// </summary>
    [TestFixture]
    [Category("Hardware")]
    [NonParallelizable]
    public class CameraServiceHardwareTests {

        private static void Log(string line) => HardwareRig.Log(line);

        /// <summary>
        /// Raw SDK, no app code: does the cooler flag survive ASICloseCamera and a later ASIOpenCamera (and ASIInitCamera)?
        /// This is what gives the app's "disconnect switches the cooler off" check its meaning: if a closed camera kept cooling,
        /// NINA's ASICamera.Disconnect (close only) would leave it on. Set point 20 °C (never below 0).
        /// </summary>
        [Test]
        [Explicit("Needs the ZWO ASI585MC Pro on USB; run by hand")]
        public void RawSdk_CoolerFlag_AcrossCloseAndReopen() {
            var id = Asi.RequireCamera();
            var opened = false;
            int? finalReadBack = null;
            try {
                ASICameraDll.OpenCamera(id);
                opened = true;
                ASICameraDll.InitCamera(id);
                Log($"open+init: {Asi.Describe(id, Ctl.ASI_COOLER_ON, Ctl.ASI_TARGET_TEMP, Ctl.ASI_COOLER_POWER_PERC, Ctl.ASI_TEMPERATURE)}");
                ASICameraDll.SetControlValue(id, Ctl.ASI_TARGET_TEMP, 20, false);
                ASICameraDll.SetControlValue(id, Ctl.ASI_COOLER_ON, 1, false);
                Thread.Sleep(3000);
                Log($"cooler on, 3 s later: {Asi.Describe(id, Ctl.ASI_COOLER_ON, Ctl.ASI_TARGET_TEMP, Ctl.ASI_COOLER_POWER_PERC, Ctl.ASI_TEMPERATURE)}");
                Asi.Read(id, Ctl.ASI_COOLER_ON).Should().Be(1);
                ASICameraDll.CloseCamera(id);
                opened = false;
                Log("closed with the cooler on");
                Thread.Sleep(2000);

                ASICameraDll.OpenCamera(id);
                opened = true;
                var afterOpen = Asi.SafeRead(id, Ctl.ASI_COOLER_ON);
                Log($"reopened (no init): ASI_COOLER_ON={afterOpen}, {Asi.Describe(id, Ctl.ASI_TARGET_TEMP, Ctl.ASI_COOLER_POWER_PERC)}");
                ASICameraDll.InitCamera(id);
                var afterInit = Asi.SafeRead(id, Ctl.ASI_COOLER_ON);
                Log($"after init: ASI_COOLER_ON={afterInit}, {Asi.Describe(id, Ctl.ASI_TARGET_TEMP, Ctl.ASI_COOLER_POWER_PERC)}");
                TestContext.Out.WriteLine($"RESULT cooler flag after close+open: {afterOpen}; after init: {afterInit}");
            } finally {
                try {
                    if (!opened) {
                        ASICameraDll.OpenCamera(id);
                        opened = true;
                    }
                    ASICameraDll.SetControlValue(id, Ctl.ASI_COOLER_ON, 0, false);
                    finalReadBack = Asi.Read(id, Ctl.ASI_COOLER_ON);
                    Log($"cooler off, read back ASI_COOLER_ON={finalReadBack}");
                } finally {
                    if (opened) {
                        ASICameraDll.CloseCamera(id);
                    }
                }
            }
            finalReadBack.Should().Be(0);
        }

        [Test]
        [Explicit("Needs the ZWO ASI585MC Pro on USB; run by hand")]
        public async Task Connect_Properties_FirstReadings_Health_AndDisconnectSwitchesTheCoolerOff() {
            var id = Asi.RequireCamera();
            await using var rig = new HardwareRig("hw connect");
            var camera = rig.Camera;
            rig.StartTicker();

            var sw = Stopwatch.StartNew();
            await camera.ConnectAsync();
            var connectSeconds = sw.Elapsed.TotalSeconds;
            var firstReadingAfterConnect = camera.SensorTemperature;
            var rawTemperatureAtConnect = Asi.SafeRead(id, Ctl.ASI_TEMPERATURE);
            var ninaTemperatureAtConnect = rig.Engine.Host.Camera.CameraInfo.Temperature;
            Log($"connected in {connectSeconds:0.00} s; service temperature {firstReadingAfterConnect?.ToString() ?? "null"}, raw ASI_TEMPERATURE {rawTemperatureAtConnect} (x0.1 C), CameraVM {ninaTemperatureAtConnect}");

            camera.State.Should().Be(DeviceConnectionState.Connected);
            camera.LastError.Should().BeNull();
            var info = camera.Info;
            Log($"Info: {info}");
            info.Model.Should().Be("ZWO ASI585MC Pro");
            info.Width.Should().Be(3840);
            info.Height.Should().Be(2160);
            info.PixelSizeMicrons.Should().Be(2.9);
            info.BayerPattern.Should().Be("RGGB");
            info.Bins.Should().Equal(1, 2, 3, 4);
            info.HasCooler.Should().BeTrue();
            info.MaxCoolingDelta.Should().Be(35);
            firstReadingAfterConnect.Should().BeNull("readings in the first 3 s after a connect are ignored (M1 finding 2)");

            // The rig profile's camera defaults reached the SDK through NINA's PersistSettingsCameraDecorator
            var gain = Asi.Read(id, Ctl.ASI_GAIN);
            var offset = Asi.Read(id, Ctl.ASI_OFFSET);
            ASICameraDll.GetROIFormat(id, out var roiBin, out var imgType);
            Log($"SDK after connect: gain {gain}, offset {offset}, ROI bin {roiBin}, {imgType}; {Asi.Describe(id, Ctl.ASI_BANDWIDTHOVERLOAD, Ctl.ASI_MONO_BIN, Ctl.ASI_COOLER_ON, Ctl.ASI_TARGET_TEMP, Ctl.ASI_FLIP, Ctl.ASI_HIGH_SPEED_MODE, Ctl.ASI_HARDWARE_BIN)}");
            gain.Should().Be(rig.Settings.Gain);
            offset.Should().Be(rig.Settings.Offset);
            roiBin.Should().Be(rig.Settings.Optics.Bin);
            Asi.Read(id, Ctl.ASI_MONO_BIN).Should().Be(0, "lights must keep their Bayer pattern");
            Asi.Read(id, Ctl.ASI_COOLER_ON).Should().Be(0, "connecting does not switch the cooler on");

            // When does the service report its first reading?
            double? firstReadingAt = null;
            double? firstReading = null;
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(10)) {
                if (camera.SensorTemperature is { } t) {
                    firstReadingAt = connectSeconds + wait.Elapsed.TotalSeconds;
                    firstReading = t;
                    break;
                }
                await Task.Delay(100);
            }
            Log($"first service temperature {firstReading} C at {firstReadingAt:0.0} s after Connect() started (raw now {Asi.SafeRead(id, Ctl.ASI_TEMPERATURE)})");
            firstReading.Should().NotBeNull("readings come back after the 3 s ignore window");
            firstReading.Should().BeInRange(-5, 45).And.NotBe(0, "a real sensor reading, not the SDK's pre-poll 0.0");

            // Normal operation, cooler off: no health warning for 20 s of ticks
            await Task.Delay(TimeSpan.FromSeconds(20));
            camera.HealthWarning.Should().BeNull();
            rig.HealthWarnings.Should().BeEmpty();
            camera.CoolerOn.Should().BeFalse();
            camera.CoolerPowerPercent.Should().Be(0);
            var ambient = camera.SensorTemperature!.Value;
            Log($"idle 20 s: sensor {ambient} C, power {camera.CoolerPowerPercent}, ticks {rig.Ticks.Count}, temperatures seen {string.Join(" ", rig.Ticks.Select(t => t.Temperature?.ToString("0.0") ?? "-").Distinct())}");

            // Cooler on through the service (set point never below 0), then Disconnect must switch it off
            var setpoint = Math.Max(0, Math.Round(ambient - 5));
            camera.SetCooler(true, setpoint);
            await Task.Delay(TimeSpan.FromSeconds(8));
            var coolerFlag = Asi.Read(id, Ctl.ASI_COOLER_ON);
            Log($"SetCooler(true, {setpoint}): service CoolerOn {camera.CoolerOn}, target {camera.TargetTemperature}, power {camera.CoolerPowerPercent} %; SDK {Asi.Describe(id, Ctl.ASI_COOLER_ON, Ctl.ASI_TARGET_TEMP, Ctl.ASI_COOLER_POWER_PERC, Ctl.ASI_TEMPERATURE)}");
            coolerFlag.Should().Be(1);
            camera.CoolerOn.Should().BeTrue("the tick after the 2.5 s command settle reads the cooler back on");
            camera.TargetTemperature.Should().Be(setpoint);

            sw.Restart();
            await camera.DisconnectAsync();
            Log($"disconnected in {sw.Elapsed.TotalSeconds:0.00} s; state {camera.State}");
            camera.State.Should().Be(DeviceConnectionState.Disconnected);
            camera.CoolerOn.Should().BeFalse();
            // The camera is closed now: reopen it raw and read the flag the app left (RawSdk_CoolerFlag_AcrossCloseAndReopen shows it survives a close)
            var leftByApp = Asi.EnsureCoolerOff("after DisconnectAsync");
            leftByApp.Should().Be(0, "Disconnect switches the cooler off before it closes the camera");
            rig.HealthWarnings.Should().BeEmpty();
        }

        [Test]
        [Explicit("Needs the ZWO ASI585MC Pro on USB; run by hand")]
        public async Task Exposures_ColourLight_MonoBinFocusFrame_ThenColourAgain() {
            var id = Asi.RequireCamera();
            await using var rig = new HardwareRig("hw exposures");
            var camera = rig.Camera;
            rig.StartTicker();
            await camera.ConnectAsync();
            await Task.Delay(3500);
            var s = rig.Settings;

            // 1. A bin-2 colour light
            var light1 = await camera.ExposeAsync(new ExposureRequest(FrameType.Light, 1, s.Gain, s.Offset, 2) { TargetName = "HW colour" });
            var monoAfterLight1 = Asi.Read(id, Ctl.ASI_MONO_BIN);
            Log($"light 1: {light1.FilePath}; HFR {light1.Hfr:0.00}, stars {light1.Stars}, mean {light1.MeanAduFraction:0.0000}, temp {light1.SensorTemperature}; ASI_MONO_BIN {monoAfterLight1}");
            CheckColourLight(light1.FilePath, 1, s.Gain, s.Offset, "HW colour");
            monoAfterLight1.Should().Be(0);

            // 2. A focus frame as the Focus screen takes it (snapshot, bin 2, mono-bin), polling ASI_MONO_BIN while it runs
            var (focus, monoSeen) = await WhilePollingMonoBin(id, () => camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.5, s.Gain, s.Offset, 2) { MonoBin = s.FocusMonoBin }));
            var monoAfterFocus = Asi.Read(id, Ctl.ASI_MONO_BIN);
            Log($"focus frame (snapshot, mono-bin {s.FocusMonoBin}): HFR {focus.Hfr:0.00}, stars {focus.Stars}, mean {focus.MeanAduFraction:0.0000}, Bahtinov {focus.BahtinovOffsetPixels}; ASI_MONO_BIN during {string.Join("/", monoSeen.Distinct())}, after {monoAfterFocus}; profile flag {rig.Engine.Profile.ActiveProfile.CameraSettings.ZwoAsiMonoBinMode}");
            focus.FilePath.Should().BeNull("focus snapshots are never saved");
            monoSeen.Should().Contain(1, "the SDK reports mono-bin on during the focus frame");
            monoAfterFocus.Should().Be(0, "mono-bin goes off as soon as the frame is in");
            rig.Engine.Profile.ActiveProfile.CameraSettings.ZwoAsiMonoBinMode.Should().NotBe(true);

            // 3. The same mono-bin path, kept as a light so its FITS can be inspected (the service applies MonoBin to any frame type)
            var (mono, monoSeen2) = await WhilePollingMonoBin(id, () => camera.ExposeAsync(new ExposureRequest(FrameType.Light, 1, s.Gain, s.Offset, 2) { MonoBin = true, TargetName = "HW monobin" }));
            var monoAfterMono = Asi.Read(id, Ctl.ASI_MONO_BIN);
            Log($"mono-bin light: {mono.FilePath}; ASI_MONO_BIN during {string.Join("/", monoSeen2.Distinct())}, after {monoAfterMono}");
            var monoHeader = FitsFile.ReadHeader(mono.FilePath);
            monoHeader.Contains("BAYERPAT").Should().BeFalse("a mono-bin frame has no Bayer pattern");
            monoHeader.GetInt("NAXIS1").Should().Be(1920);
            monoHeader.GetInt("NAXIS2").Should().Be(1080);
            monoHeader.GetInt("XBINNING").Should().Be(2);
            monoAfterMono.Should().Be(0);

            // 4. A colour light again: BAYERPAT back, mono-bin really off in the SDK
            var light2 = await camera.ExposeAsync(new ExposureRequest(FrameType.Light, 1, s.Gain, s.Offset, 2) { TargetName = "HW colour" });
            var monoAfterLight2 = Asi.Read(id, Ctl.ASI_MONO_BIN);
            Log($"light 2: {light2.FilePath}; ASI_MONO_BIN {monoAfterLight2}");
            CheckColourLight(light2.FilePath, 1, s.Gain, s.Offset, "HW colour");
            monoAfterLight2.Should().Be(0);

            // Does mono-bin change the data? Bayer parity of the colour frames against the mono-bin frame
            var c1 = Parity(light1.FilePath);
            var m = Parity(mono.FilePath);
            var c2 = Parity(light2.FilePath);
            Log($"parity means R/G1/G2/B colour 1 [{string.Join(", ", c1.Means.Select(v => v.ToString("0.0")))}] index {c1.Index:0.0000} mean {c1.Mean:0.0} median {c1.Median}");
            Log($"parity means           mono-bin [{string.Join(", ", m.Means.Select(v => v.ToString("0.0")))}] index {m.Index:0.0000} mean {m.Mean:0.0} median {m.Median}");
            Log($"parity means           colour 2 [{string.Join(", ", c2.Means.Select(v => v.ToString("0.0")))}] index {c2.Index:0.0000} mean {c2.Mean:0.0} median {c2.Median}");
            TestContext.Out.WriteLine($"RESULT checkerboard index colour {c1.Index:0.0000} / {c2.Index:0.0000}, mono-bin {m.Index:0.0000}; mean colour {c1.Mean:0.0}, mono-bin {m.Mean:0.0}");
            if (c1.Index > 0.05) {
                m.Index.Should().BeLessThan(c1.Index / 3, "a lit colour frame shows its Bayer checkerboard, a mono-bin frame must not");
            } else {
                Log("colour frame shows no checkerboard (dark or grey scene): the data check of mono-bin is inconclusive; the SDK read-back and the missing BAYERPAT stand");
            }
            rig.HealthWarnings.Should().BeEmpty();
        }

        [Test]
        [Explicit("Needs the ZWO ASI585MC Pro on USB 3; run by hand (about 3 minutes)")]
        public async Task Timing_OnUsb3_ThroughTheService() {
            var id = Asi.RequireCamera();
            await using var rig = new HardwareRig("hw timing");
            var camera = rig.Camera;
            rig.StartTicker();
            var sw = Stopwatch.StartNew();
            await camera.ConnectAsync();
            Log($"connect {sw.Elapsed.TotalSeconds:0.00} s; {Asi.Describe(id, Ctl.ASI_BANDWIDTHOVERLOAD, Ctl.ASI_HIGH_SPEED_MODE)}");
            await Task.Delay(3500);
            var s = rig.Settings;
            var results = new List<string>();

            async Task<double[]> Series(string label, int count, Func<Task<FrameResult>> take, double exposure) {
                var overheads = new List<double>();
                for (var i = 0; i < count; i++) {
                    var t = Stopwatch.StartNew();
                    await take();
                    overheads.Add(t.Elapsed.TotalSeconds - exposure);
                }
                var line = $"{label}: {count} x {exposure:0.###} s: overhead mean {overheads.Average():0.000} s, median {Median(overheads):0.000}, min {overheads.Min():0.000}, max {overheads.Max():0.000}; frames/s {count / (overheads.Sum() + (count * exposure)):0.00}";
                Log(line);
                results.Add(line);
                return overheads.ToArray();
            }

            // Warm-up frame (first frame after connect allocates buffers)
            await camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.01, s.Gain, s.Offset, 2));

            var light10ms = await Series("lights kept (NINA save + analysis)", 20,
                () => camera.ExposeAsync(new ExposureRequest(FrameType.Light, 0.01, s.Gain, s.Offset, 2) { TargetName = "HW timing" }), 0.01);
            var light2s = await Series("lights kept (NINA save + analysis)", 20,
                () => camera.ExposeAsync(new ExposureRequest(FrameType.Light, 2, s.Gain, s.Offset, 2) { TargetName = "HW timing" }), 2);
            var focusFast = await Series("focus loop (snapshot, mono-bin, HFR)", 15,
                () => camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.1, s.Gain, s.Offset, 2) { MonoBin = true }), 0.1);
            var focusDefault = await Series("focus loop at the Focus screen's default 2 s", 5,
                () => camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 2, s.Gain, s.Offset, 2) { MonoBin = true }), 2);

            // Breakdown: NINA's ImagingVM.CaptureImage alone (no ToImageData, analysis or save)
            var capture = new List<double>();
            for (var i = 0; i < 10; i++) {
                var seq = new CaptureSequence(0.01, CaptureSequence.ImageTypes.SNAPSHOT, null, new BinningMode(2, 2), 1) { Gain = s.Gain, Offset = s.Offset };
                var t = Stopwatch.StartNew();
                var data = await rig.Engine.Host.ImagingMediator.CaptureImage(seq, CancellationToken.None, new Progress<ApplicationStatus>(), string.Empty);
                capture.Add(t.Elapsed.TotalSeconds - 0.01);
                data.Should().NotBeNull();
            }
            var captureLine = $"ImagingVM.CaptureImage only: 10 x 0.01 s: overhead mean {capture.Average():0.000} s, median {Median(capture):0.000}, min {capture.Min():0.000}, max {capture.Max():0.000}";
            Log(captureLine);
            results.Add(captureLine);

            // USB bandwidth: NINA's ASICamera sets 40 (its minimum) at connect; what does 100 change on USB 3?
            var device = (ICamera)rig.Engine.Host.Camera.GetDevice();
            var usbBefore = device.USBLimit;
            device.USBLimit = 100;
            Log($"USB limit {usbBefore} -> {device.USBLimit} (SDK {Asi.SafeRead(id, Ctl.ASI_BANDWIDTHOVERLOAD)})");
            await camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.01, s.Gain, s.Offset, 2));
            var usb100 = await Series("lights kept at USB limit 100", 10,
                () => camera.ExposeAsync(new ExposureRequest(FrameType.Light, 0.01, s.Gain, s.Offset, 2) { TargetName = "HW timing usb100" }), 0.01);
            var focus100 = await Series("focus loop at USB limit 100", 10,
                () => camera.ExposeAsync(new ExposureRequest(FrameType.Snapshot, 0.1, s.Gain, s.Offset, 2) { MonoBin = true }), 0.1);
            device.USBLimit = usbBefore;

            TestContext.Out.WriteLine("RESULT\n" + string.Join("\n", results));
            TestContext.Out.WriteLine("M1 on USB 2 (zwoprobe): 1.14 s mean overhead per 30 s frame, ~1 s per short snap");
            Directory.GetFiles(rig.Engine.CurrentFolders().LightsDirectory("HW timing"), "*.fits").Should().HaveCount(40);
            rig.HealthWarnings.Should().BeEmpty();
            light2s.Should().OnlyContain(o => o > 0);
        }

        [Test]
        [Explicit("Needs the ZWO ASI585MC Pro on USB; run by hand")]
        public async Task DewHeater_ControlCaps_AndTheAppsPath() {
            var id = Asi.RequireCamera();
            await using var rig = new HardwareRig("hw dew heater");
            await rig.Camera.ConnectAsync();
            var since = DateTime.Now;

            // Every control the SDK lists for this camera, with its caps
            var count = ASICameraDll.GetNumOfControls(id);
            var caps = Enumerable.Range(0, count).Select(i => ASICameraDll.GetControlCaps(id, i)).ToList();
            foreach (var c in caps) {
                Log($"  {c.ControlType,-28} {c.Name,-24} min {c.MinValue,11} max {c.MaxValue,11} def {c.DefaultValue,6} {(c.IsWritable == ASICameraDll.ASI_BOOL.ASI_TRUE ? "rw" : "ro")} {(c.IsAutoSupported == ASICameraDll.ASI_BOOL.ASI_TRUE ? "auto" : "")}  {c.Description}");
            }
            var dew = caps.Where(c => c.ControlType == Ctl.ASI_ANTI_DEW_HEATER).ToList();
            TestContext.Out.WriteLine($"RESULT {count} controls; ASI_ANTI_DEW_HEATER {(dew.Count == 0 ? "NOT listed" : $"listed, writable {dew[0].IsWritable}")}; also absent: {string.Join(", ", new[] { Ctl.ASI_FAN_ON, Ctl.ASI_GAMMA, Ctl.ASI_OVERCLOCK, Ctl.ASI_PATTERN_ADJUST }.Where(t => caps.All(c => c.ControlType != t)))}");
            var rawDew = Asi.SafeRead(id, Ctl.ASI_ANTI_DEW_HEATER);
            Log($"raw ASIGetControlValue(ASI_ANTI_DEW_HEATER): {rawDew}");

            // What NINA's driver and CameraVM make of it
            var device = (ICamera)rig.Engine.Host.Camera.GetDevice();
            var info = rig.Engine.Host.Camera.CameraInfo;
            Log($"ICamera.HasDewHeater {device.HasDewHeater}, DewHeaterOn {device.DewHeaterOn}; CameraInfo.HasDewHeater {info.HasDewHeater}; profile CameraSettings.DewHeaterOn {rig.Engine.Profile.ActiveProfile.CameraSettings.DewHeaterOn?.ToString() ?? "null"}");
            device.HasDewHeater.Should().Be(dew.Any(c => c.IsWritable == ASICameraDll.ASI_BOOL.ASI_TRUE));

            // The app's path: the generated night never switches the dew heater (EngineSessionService: DewHeater = false)
            var night = rig.Engine.Session.ToNightPlan(new SessionPlan("HW dew", 1, 10, 2, 1, rig.Settings.Gain, rig.Settings.Offset, 2, 0, 75, 20, true));
            night.DewHeater.Should().BeFalse();
            var tree = rig.Engine.Host.Generator.Generate(night);
            var items = new List<object>();
            NINA.Mac.Sequencing.Runner.HeadlessSequenceRunner.Walk(tree, e => items.Add(e));
            items.OfType<NINA.Sequencer.SequenceItem.Camera.DewHeater>().Should().BeEmpty();

            // NINA.Mac.Sequencing's own default (PlanModel DewHeater = true) on this camera: what would NINA's validation say?
            var withDew = rig.Engine.Host.Generator.Generate(night with { DewHeater = true });
            var dewItems = new List<NINA.Sequencer.SequenceItem.Camera.DewHeater>();
            NINA.Mac.Sequencing.Runner.HeadlessSequenceRunner.Walk(withDew, e => {
                if (e is NINA.Sequencer.SequenceItem.Camera.DewHeater d) {
                    dewItems.Add(d);
                }
            });
            foreach (var d in dewItems) {
                d.Validate();
                Log($"DewHeater(OnOff={d.OnOff}) item issues: {string.Join(" | ", d.Issues)}");
            }

            // Toggling through the driver on a camera without the control: no SDK write, no exception, never on
            if (!device.HasDewHeater) {
                device.DewHeaterOn = false;
                device.DewHeaterOn.Should().BeFalse();
            }
            await rig.Camera.DisconnectAsync();
            var log = EngineLogSince(since);
            Log("engine log lines about controls: " + string.Join(" | ", log.Where(l => l.Contains("ASI Control", StringComparison.Ordinal) || l.Contains("DEW", StringComparison.Ordinal))));
        }

        // -------------------------------------------------------------------------------------------------------------

        internal static void CheckColourLight(string path, double exposure, int gain, int offset, string target) {
            path.Should().NotBeNull();
            File.Exists(path).Should().BeTrue();
            var h = FitsFile.ReadHeader(path);
            Log($"FITS {Path.GetFileName(path)}: {string.Join(" ", new[] { "IMAGETYP", "OBJECT", "BAYERPAT", "ROWORDER", "XPIXSZ", "YPIXSZ", "XBINNING", "GAIN", "OFFSET", "EXPTIME", "CCD-TEMP", "INSTRUME", "NAXIS1", "NAXIS2", "BITPIX", "SWCREATE" }.Select(k => $"{k}={h.GetString(k)}"))}");
            h.GetString("IMAGETYP").Should().Be("LIGHT");
            h.GetString("OBJECT").Should().Be(target);
            h.GetString("BAYERPAT").Should().Be("RGGB");
            h.GetString("ROWORDER").Should().Be("TOP-DOWN");
            h.GetDouble("XPIXSZ").Should().Be(5.8);
            h.GetDouble("YPIXSZ").Should().Be(5.8);
            h.GetInt("XBINNING").Should().Be(2);
            h.GetInt("YBINNING").Should().Be(2);
            h.GetInt("GAIN").Should().Be(gain);
            h.GetInt("OFFSET").Should().Be(offset);
            h.GetDouble("EXPTIME").Should().Be(exposure);
            h.GetString("INSTRUME").Should().Be("ZWO ASI585MC Pro");
            h.GetInt("NAXIS1").Should().Be(1920);
            h.GetInt("NAXIS2").Should().Be(1080);
        }

        private static (double[] Means, double Index, double Mean, double Median) Parity(string path) {
            var image = FitsFile.ReadImage(path);
            return FrameStats.Parity(image.Planes[0], image.Width, image.Height);
        }

        private static async Task<(T Result, List<int> Seen)> WhilePollingMonoBin<T>(int id, Func<Task<T>> action) {
            var seen = new List<int>();
            using var stop = new CancellationTokenSource();
            var poll = Task.Run(async () => {
                while (!stop.IsCancellationRequested) {
                    try {
                        var v = Asi.Read(id, Ctl.ASI_MONO_BIN);
                        lock (seen) {
                            seen.Add(v);
                        }
                    } catch (ASICameraException) {
                    }
                    await Task.Delay(50);
                }
            });
            try {
                return (await action(), seen);
            } finally {
                stop.Cancel();
                await poll;
            }
        }

        private static double Median(IEnumerable<double> values) {
            var v = values.OrderBy(x => x).ToArray();
            return v.Length % 2 == 1 ? v[v.Length / 2] : (v[(v.Length / 2) - 1] + v[v.Length / 2]) / 2;
        }

        /// <summary>Lines of NINA's log (in the engine's data folder) written since <paramref name="since"/>.</summary>
        internal static List<string> EngineLogSince(DateTime since) {
            var logs = Path.Combine(EngineRuntime.DataDirectory, "Logs");
            if (!Directory.Exists(logs)) {
                return new List<string>();
            }
            var lines = new List<string>();
            foreach (var file in Directory.GetFiles(logs, "*.log").Where(f => File.GetLastWriteTime(f) >= since.AddMinutes(-1))) {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                string line;
                while ((line = reader.ReadLine()) != null) {
                    if (line.Length >= 19 && DateTime.TryParse(line[..19], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at < since.AddSeconds(-1)) {
                        continue;
                    }
                    lines.Add(line);
                }
            }
            return lines;
        }
    }
}
