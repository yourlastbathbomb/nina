#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ZWOptical.ASISDK;
using static ZWOptical.ASISDK.ASICameraDll;

namespace NINA.Mac.ZwoProbe {

    public static class Program {

        private const string Usage = """
            zwoprobe: M1 camera probe for the NINA macOS port (ZWO SDK, osx-arm64)

              zwoprobe selftest                     SDK version, dylib path, struct layout (no camera needed)
              zwoprobe synth [--out FILE]           synthetic RGGB frame (R bright, G mid, B dim) to check Siril colour
              zwoprobe info                         camera properties, all controls, gain presets
              zwoprobe snap  [capture options] [--count N]
              zwoprobe cool  [--temp 0] [--timeout 20] [--hold 2] [--keep]
              zwoprobe loop  [capture options] [--minutes 30] [--save]

            capture options:
              --exp S          exposure seconds (snap 2, loop 30)
              --bin N          binning (default 2)
              --gain N         gain (default: leave camera value)
              --offset N       offset (default: leave camera value)
              --usb N          USB bandwidth limit (default 40, NINA's default)
              --mono-bin       ZWO mono bin (drops Bayer pattern at bin > 1)
              --dark           shutter-less dark flag (use with --type DARK/BIAS)
              --type T         IMAGETYP: LIGHT, DARK, FLAT, BIAS (default LIGHT)
              --temp C         cool to C first and keep cooling while capturing
              --out DIR        output folder (default ~/Astro/NINA/probe)
              --camera I       camera index (default 0)
            """;

        public static int Main(string[] args) {
            NativeLibraries.Register(typeof(ASICameraDll).Assembly);

            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }

            var opts = Options.Parse(args.Skip(1));
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => {
                e.Cancel = true;
                Log("Ctrl+C: stopping after the current step...");
                cts.Cancel();
            };

            try {
                return args[0] switch {
                    "selftest" => SelfTest(),
                    "synth" => Synth(opts),
                    "info" => WithCamera(opts, cam => Info(cam)),
                    "snap" => WithCamera(opts, cam => Snap(cam, opts, cts.Token)),
                    "cool" => WithCamera(opts, cam => Cool(cam, opts, cts.Token)),
                    "loop" => WithCamera(opts, cam => Loop(cam, opts, cts.Token)),
                    _ => Fail($"Unknown command '{args[0]}'\n\n{Usage}")
                };
            } catch (OperationCanceledException) {
                Log("Cancelled.");
                return 130;
            } catch (Exception ex) {
                Log($"ERROR: {ex}");
                return 1;
            }
        }

        private static int Fail(string message) {
            Console.Error.WriteLine(message);
            return 2;
        }

        private static int WithCamera(Options opts, Func<ProbeCamera, int> action) {
            using var cam = ProbeCamera.Open(opts.Int("camera", 0));
            return action(cam);
        }

        private static int SelfTest() {
            Log($"Process: {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.OSDescription}, .NET {Environment.Version}");
            Log($"Dylib:   {NativeLibraries.Locate("ASICamera2.dll") ?? "NOT FOUND"}");
            Log($"SDK:     {GetSDKVersion()}");
            var ok = true;
            // The SDK fills ASICameraDll's private native mirrors (C long = CLong); the public structs keep upstream's int layout
            var info = SdkNativeLayout.CameraInfo;
            var caps = SdkNativeLayout.ControlCaps;
            ok &= Check("sizeof(ASI_CAMERA_INFO)", Marshal.SizeOf(info), 248);
            ok &= Check("offsetof(ASI_CAMERA_INFO, MaxWidth)", Marshal.OffsetOf(info, nameof(ASI_CAMERA_INFO.MaxWidth)).ToInt32(), 80);
            ok &= Check("offsetof(ASI_CAMERA_INFO, IsColorCam)", Marshal.OffsetOf(info, nameof(ASI_CAMERA_INFO.IsColorCam)).ToInt32(), 88);
            ok &= Check("offsetof(ASI_CAMERA_INFO, SupportedBins)", Marshal.OffsetOf(info, nameof(ASI_CAMERA_INFO.SupportedBins)).ToInt32(), 96);
            ok &= Check("offsetof(ASI_CAMERA_INFO, PixelSize)", Marshal.OffsetOf(info, nameof(ASI_CAMERA_INFO.PixelSize)).ToInt32(), 192);
            ok &= Check("sizeof(ASI_CONTROL_CAPS)", Marshal.SizeOf(caps), 264);
            ok &= Check("offsetof(ASI_CONTROL_CAPS, ControlType)", Marshal.OffsetOf(caps, nameof(ASI_CONTROL_CAPS.ControlType)).ToInt32(), 224);
            Log($"Cameras: {GetNumOfConnectedCameras()} connected");
            return ok ? 0 : 1;

            static bool Check(string what, int actual, int expected) {
                var pass = actual == expected;
                Log($"  {(pass ? "ok  " : "FAIL")} {what} = {actual} (expect {expected})");
                return pass;
            }
        }

        private static int Synth(Options opts) {
            // Top-left 2x2 cell is R G / G B, so after an RGGB debayer red must be the brightest channel
            const int width = 64, height = 32;
            var data = new ushort[width * height];
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    data[(y * width) + x] = (y % 2, x % 2) switch {
                        (0, 0) => 40000,
                        (1, 1) => 2000,
                        _ => 10000
                    };
                }
            }
            // Mark the top rows so orientation is visible: first 4 rows get +5000
            for (var i = 0; i < width * 4; i++) { data[i] += 5000; }

            var fits = new ProbeFits();
            fits.Add("IMAGETYP", "LIGHT", "Type of exposure");
            fits.Add("EXPTIME", 1.0, "[s] Exposure duration");
            fits.Add("BAYERPAT", "RGGB", "Sensor Bayer pattern");
            fits.Add("XBAYROFF", 0, "Bayer pattern X axis offset");
            fits.Add("YBAYROFF", 0, "Bayer pattern Y axis offset");
            fits.Add("ROWORDER", "TOP-DOWN", "FITS Image Orientation");
            var path = Path.GetFullPath(opts.String("out", "synthetic_rggb.fits"));
            fits.Write(path, data, width, height);
            Log($"Wrote {path}");
            return 0;
        }

        private static int Info(ProbeCamera cam) {
            var i = cam.Info;
            Log($"SDK {GetSDKVersion()}; camera '{i.Name}' id={i.CameraID} serial/ID='{GetId(cam.Id)}'");
            Log($"  resolution     {i.MaxWidth} x {i.MaxHeight}, pixel {i.PixelSize:0.##} um, bit depth {i.BitDepth}, {i.ElecPerADU:0.###} e/ADU");
            Log($"  colour         {cam.IsColor} ({cam.BayerPatternName})");
            Log($"  bins           {string.Join(", ", cam.SupportedBins)}");
            Log($"  formats        {string.Join(", ", i.SupportedVideoFormat.TakeWhile(f => f != ASI_IMG_TYPE.ASI_IMG_END))}");
            Log($"  cooler         {i.IsCoolerCam == ASI_BOOL.ASI_TRUE}, shutter {i.MechanicalShutter == ASI_BOOL.ASI_TRUE}, ST4 {i.ST4Port == ASI_BOOL.ASI_TRUE}");
            Log($"  USB3 host/cam  {i.IsUSB3Host == ASI_BOOL.ASI_TRUE} / {i.IsUSB3Camera == ASI_BOOL.ASI_TRUE}");
            Log($"  temperature    {cam.TemperatureC:0.0} C");
            Log("  controls:");
            foreach (var (type, caps) in cam.Controls.OrderBy(c => c.Key)) {
                var value = SafeGet(cam, type);
                Log($"    {type,-28} {caps.Name,-22} min {caps.MinValue,9} max {caps.MaxValue,11} def {caps.DefaultValue,9} now {value,11} {(caps.IsWritable == ASI_BOOL.ASI_TRUE ? "rw" : "ro")}{(caps.IsAutoSupported == ASI_BOOL.ASI_TRUE ? " auto" : "")}");
            }
            try {
                GainPresets.GetGainOffset(cam.Id, out var offHdr, out var offUnity, out var gainLrn, out var offLrn);
                Log($"  GetGainOffset  offset(HighestDR)={offHdr} offset(Unity)={offUnity} gain(LowestRN)={gainLrn} offset(LowestRN)={offLrn}");
            } catch (Exception ex) {
                Log($"  GetGainOffset  failed: {ex.Message}");
            }
            try {
                GainPresets.GetLMHGainOffset(cam.Id, out var lg, out var mg, out var hg, out var ho);
                Log($"  GetLMHGainOffset low={lg} medium={mg} high={hg} highOffset={ho}");
            } catch (Exception ex) {
                Log($"  GetLMHGainOffset failed: {ex.Message}");
            }
            return 0;
        }

        private static string SafeGet(ProbeCamera cam, ASI_CONTROL_TYPE type) {
            try {
                return cam.Get(type).ToString(CultureInfo.InvariantCulture);
            } catch (ASICameraException) {
                return "n/a";
            }
        }

        private static int Snap(ProbeCamera cam, Options opts, CancellationToken token) {
            var settings = opts.Capture(defaultExposure: 2);
            CoolIfRequested(cam, opts, token);
            var count = opts.Int("count", 1);
            for (var n = 1; n <= count; n++) {
                var result = cam.Capture(settings, token);
                if (result.Data == null) {
                    Log($"Frame {n}: FAILED status={result.FinalStatus} stalled={result.Stalled}");
                    return 1;
                }
                var path = Save(cam, opts, settings, result, n);
                Log($"Frame {n}: {result.Width}x{result.Height} exp-wait {result.ExposureWait.TotalSeconds:0.00}s download {result.Download.TotalMilliseconds:0}ms {Stats(result.Data)} -> {path}");
            }
            return 0;
        }

        private static int Cool(ProbeCamera cam, Options opts, CancellationToken token) {
            if (cam.Info.IsCoolerCam != ASI_BOOL.ASI_TRUE) {
                return Fail("Camera has no cooler.");
            }
            var target = opts.Int("temp", 0);
            cam.LeaveCoolerOn = opts.Flag("keep");
            var reached = CoolTo(cam, target, TimeSpan.FromMinutes(opts.Double("timeout", 20)), token);
            var hold = TimeSpan.FromMinutes(opts.Double("hold", 2));
            Log($"Holding {hold.TotalMinutes:0.#} min to check regulation...");
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < hold) {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                token.ThrowIfCancellationRequested();
                LogCooler(cam, target);
            }
            Log(cam.LeaveCoolerOn ? "Leaving cooler ON (--keep)." : "Turning cooler off.");
            return reached ? 0 : 1;
        }

        private static int Loop(ProbeCamera cam, Options opts, CancellationToken token) {
            var settings = opts.Capture(defaultExposure: 30);
            CoolIfRequested(cam, opts, token);
            var duration = TimeSpan.FromMinutes(opts.Double("minutes", 30));
            var save = opts.Flag("save");
            var outDir = OutDir(opts);
            Directory.CreateDirectory(outDir);
            var csvPath = Path.Combine(outDir, $"loop_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            using var csv = new StreamWriter(csvPath) { AutoFlush = true };
            csv.WriteLine("frame,start_utc,status,stalled,exp_wait_s,download_ms,overhead_s,mean,median,min,max,temp_c,cooler_pct,dropped");

            Log($"Loop: {settings.ExposureSeconds}s x bin{settings.Bin} for {duration.TotalMinutes} min; log {csvPath}");
            var total = Stopwatch.StartNew();
            int frames = 0, failures = 0, stalls = 0;
            var overheads = new List<double>();
            while (total.Elapsed < duration) {
                token.ThrowIfCancellationRequested();
                frames++;
                var result = cam.Capture(settings, token);
                var overhead = result.ExposureWait.TotalSeconds - settings.ExposureSeconds + result.Download.TotalSeconds;
                var temp = cam.TemperatureC;
                var power = cam.Has(ASI_CONTROL_TYPE.ASI_COOLER_POWER_PERC) ? cam.Get(ASI_CONTROL_TYPE.ASI_COOLER_POWER_PERC) : -1;
                var dropped = GetDroppedFrames(cam.Id);
                if (result.Stalled) { stalls++; }
                if (result.Data == null) {
                    failures++;
                    Log($"#{frames}: FAILED status={result.FinalStatus} stalled={result.Stalled} after {result.ExposureWait.TotalSeconds:0.0}s");
                    csv.WriteLine(FormattableString.Invariant($"{frames},{result.StartUtc:O},{result.FinalStatus},{result.Stalled},{result.ExposureWait.TotalSeconds:0.000},,,,,,,{temp:0.0},{power},{dropped}"));
                    continue;
                }
                overheads.Add(overhead);
                var s = ImageStats.Of(result.Data);
                csv.WriteLine(FormattableString.Invariant($"{frames},{result.StartUtc:O},{result.FinalStatus},False,{result.ExposureWait.TotalSeconds:0.000},{result.Download.TotalMilliseconds:0},{overhead:0.000},{s.Mean:0.0},{s.Median},{s.Min},{s.Max},{temp:0.0},{power},{dropped}"));
                var saved = save ? " -> " + Path.GetFileName(Save(cam, opts, settings, result, frames)) : "";
                Log($"#{frames}: overhead {overhead:0.00}s, download {result.Download.TotalMilliseconds:0}ms, {s}, {temp:0.0}C {power}%{saved}");
            }
            Log($"Loop done: {frames} frames, {failures} failed, {stalls} stalled, overhead mean {(overheads.Count > 0 ? overheads.Average() : double.NaN):0.00}s max {(overheads.Count > 0 ? overheads.Max() : double.NaN):0.00}s");
            return failures == 0 ? 0 : 1;
        }

        private static void CoolIfRequested(ProbeCamera cam, Options opts, CancellationToken token) {
            if (opts.Has("temp")) {
                CoolTo(cam, opts.Int("temp", 0), TimeSpan.FromMinutes(opts.Double("timeout", 20)), token);
            }
        }

        private static bool CoolTo(ProbeCamera cam, int target, TimeSpan timeout, CancellationToken token) {
            cam.SetCooler(true, target);
            Log($"Cooler on, target {target} C (ambient start {cam.TemperatureC:0.0} C)");
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout) {
                LogCooler(cam, target);
                if (Math.Abs(cam.TemperatureC - target) <= 0.5) {
                    Log($"Reached {target} C in {sw.Elapsed:mm\\:ss}");
                    return true;
                }
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                token.ThrowIfCancellationRequested();
            }
            Log($"Did not reach {target} C within {timeout.TotalMinutes} min (now {cam.TemperatureC:0.0} C)");
            return false;
        }

        private static void LogCooler(ProbeCamera cam, int target) {
            var power = cam.Has(ASI_CONTROL_TYPE.ASI_COOLER_POWER_PERC) ? cam.Get(ASI_CONTROL_TYPE.ASI_COOLER_POWER_PERC) : -1;
            Log($"  sensor {cam.TemperatureC,5:0.0} C  target {target} C  cooler {power,3}%");
        }

        private static string OutDir(Options opts) {
            var dir = opts.String("out", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Astro", "NINA", "probe"));
            return dir.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dir[2..])
                : Path.GetFullPath(dir);
        }

        private static string Save(ProbeCamera cam, Options opts, CaptureSettings s, CaptureResult r, int sequence) {
            var type = opts.String("type", "LIGHT").ToUpperInvariant();
            var gain = cam.Get(ASI_CONTROL_TYPE.ASI_GAIN);
            var offset = cam.Get(ASI_CONTROL_TYPE.ASI_OFFSET);
            var temp = cam.TemperatureC;
            var bayered = cam.IsColor && !(s.MonoBin && s.Bin > 1);

            var fits = new ProbeFits();
            fits.Add("IMAGETYP", type, "Type of exposure");
            fits.Add("EXPOSURE", s.ExposureSeconds, "[s] Exposure duration");
            fits.Add("EXPTIME", s.ExposureSeconds, "[s] Exposure duration");
            fits.Add("DATE-LOC", r.StartUtc.ToLocalTime(), "Time of observation (local)");
            fits.Add("DATE-OBS", r.StartUtc, "Time of observation (UTC)");
            fits.Add("XBINNING", s.Bin, "X axis binning factor");
            fits.Add("YBINNING", s.Bin, "Y axis binning factor");
            fits.Add("GAIN", gain, "Sensor gain");
            fits.Add("OFFSET", offset, "Sensor gain offset");
            fits.Add("EGAIN", (double)cam.Info.ElecPerADU, "[e-/ADU] Electrons per A/D unit");
            fits.Add("XPIXSZ", cam.Info.PixelSize * s.Bin, "[um] Pixel X axis size");
            fits.Add("YPIXSZ", cam.Info.PixelSize * s.Bin, "[um] Pixel Y axis size");
            fits.Add("INSTRUME", cam.Info.Name, "Imaging instrument name");
            if (cam.CoolerEnabledByProbe) {
                fits.Add("SET-TEMP", (double)cam.Get(ASI_CONTROL_TYPE.ASI_TARGET_TEMP), "[degC] CCD temperature setpoint");
            }
            fits.Add("CCD-TEMP", temp, "[degC] CCD temperature");
            if (bayered) {
                fits.Add("BAYERPAT", cam.BayerPatternName, "Sensor Bayer pattern");
                fits.Add("XBAYROFF", 0, "Bayer pattern X axis offset");
                fits.Add("YBAYROFF", 0, "Bayer pattern Y axis offset");
            }
            fits.Add("USBLIMIT", s.UsbLimit, "Camera-specific USB setting");
            fits.Add("ROWORDER", "TOP-DOWN", "FITS Image Orientation");
            fits.Add("SWCREATE", "N.I.N.A. macOS port zwoprobe (arm64)", "Software that created this file");

            var name = FormattableString.Invariant($"{r.StartUtc.ToLocalTime():yyyy-MM-dd_HH-mm-ss}_{type}_{s.ExposureSeconds:0.###}s_bin{s.Bin}_G{gain}_O{offset}_{temp:0}C_{sequence:0000}.fits");
            var path = Path.Combine(OutDir(opts), name);
            fits.Write(path, r.Data, r.Width, r.Height);
            return path;
        }

        private static string Stats(ushort[] data) => ImageStats.Of(data).ToString();

        internal static void Log(string message) {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
        }
    }

    internal readonly record struct ImageStats(double Mean, int Median, int Min, int Max) {

        public static ImageStats Of(ushort[] data) {
            var histogram = new int[65536];
            long sum = 0;
            foreach (var v in data) {
                histogram[v]++;
                sum += v;
            }
            int min = 0, max = 65535;
            while (histogram[min] == 0) { min++; }
            while (histogram[max] == 0) { max--; }
            int median = 0;
            long seen = 0, half = data.Length / 2;
            for (; median < 65536; median++) {
                seen += histogram[median];
                if (seen > half) { break; }
            }
            return new ImageStats((double)sum / data.Length, median, min, max);
        }

        public override string ToString() => FormattableString.Invariant($"mean {Mean:0.0} median {Median} min {Min} max {Max}");
    }

    internal sealed class Options {
        private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(IEnumerable<string> args) {
            var o = new Options();
            var list = args.ToList();
            for (var i = 0; i < list.Count; i++) {
                if (!list[i].StartsWith("--", StringComparison.Ordinal)) {
                    throw new ArgumentException($"Unexpected argument '{list[i]}'");
                }
                var key = list[i][2..];
                var hasValue = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal);
                o.values[key] = hasValue ? list[++i] : "true";
            }
            return o;
        }

        public bool Has(string key) => values.ContainsKey(key);

        public bool Flag(string key) => values.TryGetValue(key, out var v) && v == "true";

        public string String(string key, string fallback) => values.TryGetValue(key, out var v) ? v : fallback;

        public int Int(string key, int fallback) => values.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

        public int? IntOrNull(string key) => values.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : null;

        public double Double(string key, double fallback) => values.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

        public CaptureSettings Capture(double defaultExposure) => new(
            ExposureSeconds: Double("exp", defaultExposure),
            Bin: Int("bin", 2),
            Gain: IntOrNull("gain"),
            Offset: IntOrNull("offset"),
            UsbLimit: Int("usb", 40),
            MonoBin: Flag("mono-bin"),
            Dark: Flag("dark"));
    }
}
