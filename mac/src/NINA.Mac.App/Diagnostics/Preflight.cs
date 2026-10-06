#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry.Mac;
using NINA.Mac.App.Astro;
using NINA.Mac.App.Engine;
using NINA.Mac.App.Services;
using NINA.Mac.Native;
using NINA.Mac.Platform;
using NINA.PlateSolving.Mac;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NINA.Mac.App.Diagnostics {

    public enum PreflightStatus {
        Pass,
        Info,
        Warn,
        Fail,
    }

    /// <summary>One preflight line: what was checked, how it came out, and what to do about it.</summary>
    public sealed record PreflightItem(string Name, PreflightStatus Status, string Detail, string Fix = null) {

        /// <summary>True for the checks that concern the app bundle itself (the smoke test fails on these).</summary>
        public bool IsBundleCheck { get; init; }

        public string StatusText => Status switch {
            PreflightStatus.Pass => "PASS",
            PreflightStatus.Info => "INFO",
            PreflightStatus.Warn => "WARN",
            _ => "FAIL",
        };
    }

    public sealed class PreflightReport {

        public PreflightReport(IReadOnlyList<PreflightItem> items, DateTimeOffset ranAt, TimeSpan duration) {
            Items = items;
            RanAt = ranAt;
            Duration = duration;
        }

        public IReadOnlyList<PreflightItem> Items { get; }

        public DateTimeOffset RanAt { get; }

        public TimeSpan Duration { get; }

        public int Failures => Items.Count(i => i.Status == PreflightStatus.Fail);

        public int Warnings => Items.Count(i => i.Status == PreflightStatus.Warn);

        /// <summary>0 when nothing failed (warnings allowed), 1 otherwise.</summary>
        public int ExitCode => Failures == 0 ? 0 : 1;

        public string Summary => Failures > 0
            ? $"{Failures} FAIL, {Warnings} WARN: fix the FAIL lines before going out"
            : Warnings > 0 ? $"no FAIL, {Warnings} WARN: read the WARN lines" : "all PASS";

        /// <summary>Plain text, one line per item with its fix indented below (the CLI output).</summary>
        public string Format() {
            var text = new StringBuilder();
            foreach (var item in Items) {
                text.Append(item.StatusText).Append("  ").Append(item.Name).Append(": ").Append(item.Detail).Append('\n');
                if (!string.IsNullOrWhiteSpace(item.Fix)) {
                    text.Append("      fix: ").Append(item.Fix).Append('\n');
                }
            }
            text.Append(string.Create(CultureInfo.InvariantCulture, $"Preflight: {Summary} ({Items.Count} checks, {Duration.TotalSeconds:0.0} s)\n"));
            return text.ToString();
        }
    }

    /// <summary>Everything a preflight looks at; each piece is a seam so tests run it against fakes.</summary>
    public sealed class PreflightContext {

        /// <summary>Deep Water Bay, Hong Kong (MAC_PORT_PLAN.md).</summary>
        public const double SiteLatitude = 22.25;

        public const double SiteLongitude = 114.18;

        /// <summary>The rig's FTDI adapter as the M2 bench session found it.</summary>
        public const string RigSerialPort = "/dev/cu.usbserial-DU0D8VUG";

        public AppSettings Settings { get; init; } = new();

        /// <summary>The settings file (for the fix text); null when unknown.</summary>
        public string SettingsFile { get; init; }

        /// <summary>Why the settings file could not be read (the app then runs on defaults), or null.</summary>
        public string SettingsWarning { get; init; }

        public UserDataPaths DataPaths { get; init; }

        public IClock Clock { get; init; } = SystemClock.Instance;

        public IPowerSource Power { get; init; }

        public Func<IReadOnlyList<SerialPortInfo>> SerialPorts { get; init; } = () => NINA.Mac.Platform.SerialPorts.List();

        /// <summary>The port the rig profile (or the connected mount) expects; null = <see cref="RigSerialPort"/>.</summary>
        public string ExpectedPort { get; init; }

        public HorizonProfile Horizon { get; init; }

        /// <summary>Why the horizon file could not be read, or null.</summary>
        public string HorizonError { get; init; }

        /// <summary>Base folder holding the engine assemblies, External/ and Database/ (the app's base directory).</summary>
        public string BaseDirectory { get; init; } = AppContext.BaseDirectory;

        /// <summary>astrometry.net index folders; null = AstrometryNetSetup.IndexDirectories.</summary>
        public IReadOnlyList<string> IndexDirectories { get; init; }

        public string SirilCli { get; init; } = SirilDefaults.Cli;

        /// <summary>Siril's GUI configuration folder (config.&lt;version&gt;.ini).</summary>
        public string SirilConfigDirectory { get; init; }

        /// <summary>Free bytes on the volume of a path; null when unknown.</summary>
        public Func<string, long?> FreeSpace { get; init; } = Preflight.DefaultFreeSpace;

        /// <summary>The device source in use and why Real could not start (null = fine).</summary>
        public DeviceSource ActiveDeviceSource { get; init; }

        public string EngineError { get; init; }

        /// <summary>Ask the ZWO SDK how many cameras are on USB (touches USB; only when the operator asks).</summary>
        public bool WithDevices { get; init; }

        /// <summary>The camera is already connected in this app: report it instead of scanning USB.</summary>
        public string ConnectedCamera { get; init; }

        /// <summary>Run NOVAS once to prove the ephemeris is open (only when the engine runtime is set up in this process).</summary>
        public bool LiveEphemerisCheck { get; init; }

        /// <summary>Whether a file carries macOS's download quarantine; default <see cref="FileQuarantine.IsQuarantined"/>.</summary>
        public Func<string, bool> IsQuarantined { get; init; } = FileQuarantine.IsQuarantined;

        /// <summary>Starts the ASTAP solver once (<c>-h</c>): null when it runs, else why not; null = skipped. Default <see cref="Preflight.ProbeAstap"/>.</summary>
        public Func<string, string> ProbeAstap { get; init; } = Preflight.ProbeAstap;

        /// <summary>Frame interval assumed for the disk budget (the first-light plan: 10 s subs).</summary>
        public double SubSeconds { get; init; } = 10;

        internal string Home => DataPaths?.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    internal static class SirilDefaults {
        public const string Cli = NINA.Mac.Siril.SirilRunnerOptions.DefaultSirilCli;
    }

    /// <summary>
    /// The daytime (or dusk) check: finishes in seconds, opens no device unless asked, and prints PASS/WARN/FAIL lines with the
    /// fix for each. Run as <c>Nightglass --preflight [--with-devices]</c>, from the Connect screen's Preflight card, and (without
    /// devices) by the smoke test.
    /// </summary>
    public static class Preflight {

        /// <summary>Engine assemblies the Real devices need (all must load).</summary>
        public static readonly IReadOnlyList<string> EngineAssemblies = new[] {
            "NINA.Core", "NINA.Profile", "NINA.Astrometry", "NINA.Equipment", "NINA.Image", "NINA.Platesolving", "NINA.Sequencer",
            "NINA.WPF.Base", "NINA.Mac.WpfCompat", "NINA.Mac.Sequencing", "NINA.Mac.Siril", "NINA.Mac.Lx200", "NINA.Mac.Equipment.Lx200",
            "NINA.Mac.ImageAnalysis", "NINA.Mac.App.Engine", "NINA.Mac.Native",
        };

        /// <summary>Native libraries by the names upstream imports them (NativeLibraries maps them to the bundled dylibs).</summary>
        public static readonly IReadOnlyList<(string Import, string What)> NativeImports = new[] {
            ("ASICamera2.dll", "ZWO camera SDK (with libusb)"),
            ("SOFA_2023_10_11.dll", "SOFA"),
            ("NOVAS31lib.dll", "NOVAS"),
        };

        /// <summary>ASTAP's documented minimum field height for the D80 database, degrees (plan risk 4).</summary>
        public const double AstapD80MinimumFieldDegrees = 0.15;

        /// <summary>Bytes of one 16-bit FITS frame at this binning, header included.</summary>
        public static long FrameBytes(OpticsSettings optics) {
            var bin = Math.Max(1, optics.Bin);
            return ((long)(optics.SensorWidth / bin) * (optics.SensorHeight / bin) * 2) + (2880 * 4);
        }

        /// <summary>
        /// Siril's intermediates per light in the stacking script NINA.Mac.Siril generates (RGB mode): <c>set32bits</c>, then
        /// <c>calibrate -debayer</c> writes a 32-bit RGB pp_ frame and <c>register</c> a 32-bit RGB r_pp_ frame (<c>convert</c>
        /// only links the raw lights). About 12x the 16-bit CFA light; they stay in each target's process/ folder after the stack.
        /// </summary>
        public static long StackingBytesPerLight(OpticsSettings optics) {
            var bin = Math.Max(1, optics.Bin);
            return 2 * (((long)(optics.SensorWidth / bin) * (optics.SensorHeight / bin) * 3 * 4) + (2880 * 4));
        }

        public static PreflightReport Run(PreflightContext c) {
            ArgumentNullException.ThrowIfNull(c);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var items = new List<PreflightItem>();
            void Add(string name, Func<IEnumerable<PreflightItem>> check, bool bundle = false) {
                try {
                    foreach (var item in check()) {
                        items.Add(item with { IsBundleCheck = bundle });
                    }
                } catch (Exception ex) {
                    items.Add(new PreflightItem(name, PreflightStatus.Fail, $"the check itself failed: {ex.GetType().Name}: {ex.Message}", "Report this; the other checks still ran") { IsBundleCheck = bundle });
                }
            }
            Add("Settings", () => One(SettingsCheck(c)));
            Add("Device source", () => One(DeviceSourceCheck(c)));
            Add("Engine assemblies", () => One(AssemblyCheck()), bundle: true);
            Add("Engine data", () => One(EngineDataCheck(c)), bundle: true);
            Add("Native libraries", () => One(NativeCheck()), bundle: true);
            Add("ZWO SDK", () => One(ZwoCheck(c)), bundle: true);
            Add("NINA database", () => One(DatabaseCheck(c)));
            Add("Serial port", () => One(SerialCheck(c)));
            Add("ASTAP", () => AstapChecks(c));
            Add("solve-field", () => AstrometryChecks(c));
            Add("Siril", () => SirilChecks(c));
            Add("Images folder", () => StorageChecks(c));
            Add("Battery", () => One(BatteryCheck(c)));
            Add("Site", () => SiteChecks(c));
            Add("Horizon", () => One(HorizonCheck(c)));
            Add("Tonight", () => NightChecks(c));
            Add("Optical train", () => One(OpticsCheck(c)));
            return new PreflightReport(items, c.Clock.Now, watch.Elapsed);
        }

        private static IEnumerable<PreflightItem> One(PreflightItem item) => new[] { item };

        private static PreflightItem SettingsCheck(PreflightContext c) {
            var file = c.SettingsFile ?? "settings.json";
            if (c.SettingsWarning != null) {
                return new PreflightItem("Settings", PreflightStatus.Fail,
                    $"{c.SettingsWarning} Device source, site, optics and paths below are the defaults, not yours",
                    $"Fix {file} in a text editor (the app keeps a copy of the unreadable file as {Path.GetFileName(file)}.bad when it starts), or re-enter Settings (Devices, Site, Optics, Storage) and Save, which rewrites it");
            }
            return new PreflightItem("Settings", PreflightStatus.Pass, c.SettingsFile != null && !File.Exists(c.SettingsFile)
                ? $"no {Path.GetFileName(file)} yet: defaults"
                : $"{file} read");
        }

        private static PreflightItem DeviceSourceCheck(PreflightContext c) {
            if (c.SettingsWarning != null && c.Settings.DeviceSource != DeviceSource.Real) {
                return new PreflightItem("Device source", PreflightStatus.Warn, "Simulated, because the settings file could not be read (defaults)",
                    "Fix the settings file first (see the Settings line), then check Settings › Devices › Device source is Real");
            }
            if (c.Settings.DeviceSource == DeviceSource.Real && c.EngineError != null) {
                return new PreflightItem("Device source", PreflightStatus.Fail, $"Real devices were asked for but could not start: {c.EngineError}",
                    "Fix the engine problem below (bundle, data files), then restart the app");
            }
            if (c.Settings.DeviceSource != DeviceSource.Real) {
                return new PreflightItem("Device source", PreflightStatus.Warn, "Simulated: nothing real will be connected tonight",
                    "Settings › Devices › Device source: Real, Save, then restart the app");
            }
            return new PreflightItem("Device source", PreflightStatus.Pass,
                c.ActiveDeviceSource == DeviceSource.Real ? "Real devices (NINA's engine)" : "Real (takes effect at the next start)");
        }

        private static PreflightItem AssemblyCheck() {
            var missing = new List<string>();
            foreach (var name in EngineAssemblies) {
                try {
                    Assembly.Load(new AssemblyName(name));
                } catch (Exception ex) when (ex is FileNotFoundException || ex is FileLoadException || ex is BadImageFormatException) {
                    missing.Add($"{name} ({ex.GetType().Name})");
                }
            }
            return missing.Count == 0
                ? new PreflightItem("Engine assemblies", PreflightStatus.Pass, $"{EngineAssemblies.Count} engine assemblies load")
                : new PreflightItem("Engine assemblies", PreflightStatus.Fail, $"cannot load {string.Join(", ", missing)}",
                    "Rebuild and repackage the app (mac/packaging/package-app.sh); do not copy single files into the bundle");
        }

        private static PreflightItem EngineDataCheck(PreflightContext c) {
            var problems = EngineData.FindMissingFiles(c.BaseDirectory).ToList();
            var live = "";
            if (problems.Count == 0 && c.LiveEphemerisCheck && EngineRuntime.DataDirectory != null) {
                problems.AddRange(EngineData.Check());
                live = ", NOVAS reads the ephemeris";
            }
            return problems.Count == 0
                ? new PreflightItem("Engine data", PreflightStatus.Pass, $"JPLEPH and the catalogue scripts are in place{live}")
                : new PreflightItem("Engine data", PreflightStatus.Fail, string.Join("; ", problems),
                    "Put JPLEPH in mac/native/ephemeris/JPLEPH and repackage (mac/README.md, M3)");
        }

        private static PreflightItem NativeCheck() {
            var loaded = new List<string>();
            var problems = new List<string>();
            foreach (var (import, what) in NativeImports) {
                var path = NativeLibraries.Locate(import);
                if (path == null) {
                    problems.Add($"{what}: {NativeLibraries.DylibName(import)} not found in {string.Join(", ", NativeLibraries.SearchDirectories())}");
                } else if (!NativeLibrary.TryLoad(path, out var handle)) {
                    problems.Add($"{what}: {Path.GetFileName(path)} does not load (dependency or signature)");
                } else {
                    loaded.Add(Path.GetFileName(path));
                    NativeLibrary.Free(handle);
                }
            }
            return problems.Count == 0
                ? new PreflightItem("Native libraries", PreflightStatus.Pass, $"{string.Join(", ", loaded)} load")
                : new PreflightItem("Native libraries", PreflightStatus.Fail, string.Join("; ", problems),
                    "Re-stage the dylibs (mac/scripts/stage-zwo.sh, build-astrometry-natives.sh) and repackage");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr AsiGetSdkVersion();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AsiGetNumOfConnectedCameras();

        private static PreflightItem ZwoCheck(PreflightContext c) {
            var path = NativeLibraries.Locate("ASICamera2.dll");
            if (path == null || !NativeLibrary.TryLoad(path, out var handle)) {
                return new PreflightItem("ZWO SDK", PreflightStatus.Fail, "libASICamera2 does not load", "See Native libraries above");
            }
            try {
                string version = null;
                if (NativeLibrary.TryGetExport(handle, "ASIGetSDKVersion", out var fn)) {
                    version = Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<AsiGetSdkVersion>(fn)());
                }
                var detail = $"SDK {version ?? "(version unknown)"} loads";
                if (c.ConnectedCamera != null) {
                    return new PreflightItem("ZWO SDK", PreflightStatus.Pass, $"{detail}; camera connected in the app: {c.ConnectedCamera}");
                }
                if (!c.WithDevices) {
                    return new PreflightItem("ZWO SDK", PreflightStatus.Pass, $"{detail}; USB not scanned (--with-devices scans for the camera)");
                }
                if (!NativeLibrary.TryGetExport(handle, "ASIGetNumOfConnectedCameras", out var count)) {
                    return new PreflightItem("ZWO SDK", PreflightStatus.Fail, $"{detail}, but ASIGetNumOfConnectedCameras is missing", "Re-stage the ZWO SDK");
                }
                var cameras = Marshal.GetDelegateForFunctionPointer<AsiGetNumOfConnectedCameras>(count)();
                return cameras > 0
                    ? new PreflightItem("ZWO SDK", PreflightStatus.Pass, $"{detail}; {cameras} ZWO camera{(cameras == 1 ? "" : "s")} on USB")
                    : new PreflightItem("ZWO SDK", PreflightStatus.Warn, $"{detail}; no ZWO camera on USB",
                        "Plug the camera's USB 3 cable straight into the Mac (no hub); quit KStars/ASIStudio if they hold it");
            } finally {
                NativeLibrary.Free(handle);
            }
        }

        private static PreflightItem DatabaseCheck(PreflightContext c) {
            var engineData = c.DataPaths?.EngineDataDirectory;
            if (engineData == null) {
                return new PreflightItem("NINA database", PreflightStatus.Info, "no engine data folder configured");
            }
            var db = Path.Combine(engineData, "NINA.sqlite");
            if (File.Exists(db)) {
                try {
                    using var stream = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return stream.Length > 0
                        ? new PreflightItem("NINA database", PreflightStatus.Pass, $"{db} ({stream.Length / 1024 / 1024.0:0.0} MB)")
                        : new PreflightItem("NINA database", PreflightStatus.Warn, $"{db} is empty", $"Quit the app, delete {db}; it is rebuilt from the bundled scripts");
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                    return new PreflightItem("NINA database", PreflightStatus.Fail, $"{db} cannot be read: {ex.Message}", "Check the file's permissions");
                }
            }
            var writable = WritableAncestor(engineData);
            return writable != null
                ? new PreflightItem("NINA database", PreflightStatus.Pass, $"not built yet; NINA builds it in {engineData} from the bundled scripts on first use")
                : new PreflightItem("NINA database", PreflightStatus.Fail, $"{engineData} is not writable", "Check the permissions of ~/Library/Application Support");
        }

        private static readonly Regex profilePort = new(@"/dev/cu\.[A-Za-z0-9._-]+", RegexOptions.CultureInvariant);

        /// <summary>The serial port the rig profile names (Lx200Settings.PortPath in the plugin store), read as text; null when none.</summary>
        public static string PortFromProfile(string engineDataDirectory) {
            if (string.IsNullOrWhiteSpace(engineDataDirectory)) {
                return null;
            }
            var path = Path.Combine(engineDataDirectory, "Profiles", $"{EngineProfileService.RigProfileId}.profile");
            try {
                if (!File.Exists(path)) {
                    return null;
                }
                var match = profilePort.Match(File.ReadAllText(path));
                return match.Success ? match.Value : null;
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                return null;
            }
        }

        private static PreflightItem SerialCheck(PreflightContext c) {
            var expected = string.IsNullOrWhiteSpace(c.ExpectedPort) ? PreflightContext.RigSerialPort : c.ExpectedPort;
            var adapters = (c.SerialPorts?.Invoke() ?? Array.Empty<SerialPortInfo>())
                .Where(p => Path.GetFileName(p.Path).StartsWith("cu.usbserial", StringComparison.Ordinal)).Select(p => p.Path).ToList();
            if (adapters.Contains(expected)) {
                return new PreflightItem("Serial port", PreflightStatus.Pass, $"{expected} present (the mount cable)");
            }
            if (adapters.Count > 0) {
                return new PreflightItem("Serial port", PreflightStatus.Warn, $"expected {expected}, found {string.Join(", ", adapters)}",
                    "If that is the mount's FTDI cable, pick it on Connect › Serial port (another adapter gets another name)");
            }
            return new PreflightItem("Serial port", PreflightStatus.Warn, $"no /dev/cu.usbserial-* adapter (expected {expected})",
                "Plug the FTDI serial cable into the camera's USB 2 hub (or the Mac) and run preflight again; fine in daytime without the mount");
        }

        private static IEnumerable<PreflightItem> AstapChecks(PreflightContext c) {
            var solver = c.Settings.Solver ?? new SolverSettings();
            var exe = UserDataPaths.ExpandHome(solver.AstapExecutable?.Trim() ?? "", c.Home);
            var db = UserDataPaths.ExpandHome(solver.AstapDatabase?.Trim() ?? "", c.Home);
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) {
                var zip = FindAstapDownload(c.Home);
                yield return new PreflightItem("ASTAP", PreflightStatus.Fail, $"astap_cli not found at {exe} (centring would be goto only)",
                    zip != null
                        ? $"Unzip {zip} so that {exe} exists (Finder: double-click the zip, move astap_cli there), then: xattr -d com.apple.quarantine \"{exe}\""
                        : $"Install the ASTAP command-line solver for Apple Silicon at {exe} (Settings › Plate solving names the path)");
            } else if (!IsExecutable(exe)) {
                yield return new PreflightItem("ASTAP", PreflightStatus.Fail, $"{exe} is not executable", $"chmod +x \"{exe}\"");
            } else if (c.IsQuarantined?.Invoke(exe) == true) {
                // Not started: a quarantined tool is killed by macOS (or brings up a Gatekeeper dialog at the scope)
                yield return new PreflightItem("ASTAP", PreflightStatus.Fail,
                    $"{exe} still carries macOS's download quarantine: macOS kills it when a solve starts, so centring would be goto only",
                    $"In Terminal: {FileQuarantine.RemoveCommand(exe)}");
            } else if (c.ProbeAstap?.Invoke(exe) is { } problem) {
                yield return new PreflightItem("ASTAP", PreflightStatus.Fail, $"{exe} does not run: {problem}",
                    $"Run \"{exe}\" -h in Terminal: it must print ASTAP's banner. If macOS refuses it: {FileQuarantine.RemoveCommand(exe)}; otherwise reinstall the Apple Silicon astap_cli");
            } else {
                yield return new PreflightItem("ASTAP", PreflightStatus.Pass, $"{exe} (runs)");
            }
            var tiles = Directory.Exists(db) ? Directory.GetFiles(db, "d80_*").Length : 0;
            yield return tiles > 0
                ? new PreflightItem("ASTAP D80", PreflightStatus.Pass, $"{tiles} D80 files in {db}")
                : new PreflightItem("ASTAP D80", PreflightStatus.Fail, $"no D80 star database in {db}",
                    $"Install ASTAP's D80 database (d80_*.1476 files) into {db}");
        }

        /// <summary>
        /// Starts <c>astap_cli -h</c> (prints its banner, opens no device, ~30 ms) to prove macOS lets the solver run: null when
        /// it ran, otherwise why not. A quarantined or badly signed binary is killed by the OS (exit 137) and prints nothing.
        /// </summary>
        public static string ProbeAstap(string exe) {
            try {
                using var process = new System.Diagnostics.Process {
                    StartInfo = new System.Diagnostics.ProcessStartInfo(exe, "-h") {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(AstapProbeTimeoutMs)) {
                    try {
                        process.Kill(entireProcessTree: true);
                    } catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) {
                        // already gone
                    }
                    return $"no answer to -h within {AstapProbeTimeoutMs / 1000} s";
                }
                var output = (stdout.Wait(1000) ? stdout.Result : "") + (stderr.Wait(1000) ? stderr.Result : "");
                if (process.ExitCode == 0 || output.Contains("ASTAP", StringComparison.Ordinal)) {
                    return null;
                }
                return process.ExitCode == 137
                    ? "macOS killed it at start (exit 137: download quarantine or an invalid code signature)"
                    : $"'-h' exited with {process.ExitCode} without ASTAP's banner";
            } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException) {
                return $"cannot be started ({ex.Message})";
            }
        }

        public const int AstapProbeTimeoutMs = 5000;

        private static string FindAstapDownload(string home) {
            try {
                var dl = Path.Combine(home, "Astro", "astap", "dl");
                return Directory.Exists(dl) ? Directory.GetFiles(dl, "astap_cli*.zip").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() : null;
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                return null;
            }
        }

        /// <summary>The 4200-series scales (index-42SS) whose quad sizes fall in 30-100 % of the field width (astrometry.net: quads 10-100 % of the image).</summary>
        public static IReadOnlyList<int> IndexScalesFor(double fieldWidthArcmin) {
            // Quad diameter ranges of index-4200..4219 in arcminutes (astrometry.net's index table)
            double[] edges = { 2.0, 2.8, 4.0, 5.6, 8.0, 11.0, 16.0, 22.0, 30.0, 42.0, 60.0, 85.0, 120.0, 170.0, 240.0, 340.0, 480.0, 680.0, 1000.0, 1400.0, 2000.0 };
            var low = 0.3 * fieldWidthArcmin;
            var scales = new List<int>();
            for (var s = 0; s < 20; s++) {
                if (edges[s + 1] > low && edges[s] < fieldWidthArcmin) {
                    scales.Add(s);
                }
            }
            return scales;
        }

        private static IEnumerable<PreflightItem> AstrometryChecks(PreflightContext c) {
            var solver = c.Settings.Solver ?? new SolverSettings();
            var bin = UserDataPaths.ExpandHome(solver.AstrometryBinDirectory?.Trim() ?? "", c.Home);
            var solveField = Path.Combine(string.IsNullOrWhiteSpace(bin) ? AstrometryNetSetup.HomebrewBinDirectory : bin, "solve-field");
            yield return File.Exists(solveField)
                ? new PreflightItem("solve-field", PreflightStatus.Pass, $"{solveField} (blind fallback)")
                : new PreflightItem("solve-field", PreflightStatus.Warn, $"not found at {solveField}: no blind fallback when ASTAP fails",
                    "brew install astrometry-net (or set Settings › Plate solving › solve-field folder)");

            var optics = c.Settings.Optics;
            var widthArcmin = optics.SensorWidth * (206.264806 * optics.PixelSizeMicrons / optics.EffectiveFocalLengthMm) / 60.0;
            var scales = IndexScalesFor(widthArcmin);
            var dirs = c.IndexDirectories ?? AstrometryNetSetup.IndexDirectories;
            var saved = AstrometryNetSetup.IndexDirectories;
            IReadOnlyList<string> files;
            IReadOnlyList<string> missingTiles;
            string description;
            try {
                AstrometryNetSetup.IndexDirectories = dirs;
                files = AstrometryNetSetup.FindIndexFiles();
                missingTiles = AstrometryNetSetup.FindMissingIndexTiles();
                description = AstrometryNetSetup.DescribeMissingIndexTiles();
            } finally {
                AstrometryNetSetup.IndexDirectories = saved;
            }
            var names = new HashSet<string>(files.Select(Path.GetFileName), StringComparer.Ordinal);
            var train = optics.UseReducer ? "reducer" : "native f/10";
            var needed = string.Join(", ", scales.Select(s => $"42{s:00}"));
            var absent = scales.Where(s => !names.Any(n => n.StartsWith($"index-42{s:00}", StringComparison.Ordinal))).Select(s => $"42{s:00}").ToList();
            var gaps = missingTiles.Where(f => scales.Any(s => f.StartsWith($"index-42{s:00}-", StringComparison.Ordinal)))
                .Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
            if (files.Count == 0) {
                yield return new PreflightItem("Index tiles", PreflightStatus.Warn, $"no astrometry.net index files in {string.Join(", ", dirs)}",
                    $"Download index {needed} (4200 series) into {dirs.FirstOrDefault()}");
            } else if (absent.Count > 0 || gaps.Count > 0) {
                var what = new List<string>();
                if (absent.Count > 0) {
                    what.Add($"scales {string.Join(", ", absent)} missing");
                }
                if (gaps.Count > 0) {
                    what.Add($"{gaps.Count} tile{(gaps.Count == 1 ? "" : "s")} missing: {string.Join(", ", gaps)}");
                }
                yield return new PreflightItem("Index tiles", PreflightStatus.Warn,
                    $"{train} ({widthArcmin:0.0}′ wide) needs {needed}: {string.Join("; ", what)}. solve-field cannot solve fields in those sky regions",
                    $"Download the missing files from data.astrometry.net/4200/ into {dirs.FirstOrDefault()}" + (description != null ? $" ({description})" : ""));
            } else {
                yield return new PreflightItem("Index tiles", PreflightStatus.Pass, $"{train} ({widthArcmin:0.0}′ wide) needs {needed}: all present ({files.Count} files)"
                    + (description != null ? $"; other scales have gaps ({missingTiles.Count} tiles)" : ""));
            }
        }

        private static IEnumerable<PreflightItem> SirilChecks(PreflightContext c) {
            yield return File.Exists(c.SirilCli)
                ? new PreflightItem("siril-cli", PreflightStatus.Pass, c.SirilCli)
                : new PreflightItem("siril-cli", PreflightStatus.Warn, $"not found at {c.SirilCli}: stacking after the night needs Siril 1.4",
                    "Install Siril 1.4 in /Applications (siril.org)");
            var dir = c.SirilConfigDirectory ?? Path.Combine(c.Home, "Library", "Application Support", "org.siril.Siril", "siril");
            string config = null;
            try {
                config = Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "config*.ini").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    : null;
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                config = null;
            }
            if (config == null) {
                yield return new PreflightItem("Siril binned pixel size", PreflightStatus.Info, "no Siril configuration found (Siril not run yet?)",
                    "When Siril first runs: Preferences › FITS › untick 'Update pixel size of binned images'");
                yield break;
            }
            var setting = File.ReadAllLines(config).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("binning_update=", StringComparison.Ordinal));
            if (setting == null) {
                yield return new PreflightItem("Siril binned pixel size", PreflightStatus.Info, $"binning_update not in {Path.GetFileName(config)}",
                    "Check Siril › Preferences › FITS › 'Update pixel size of binned images' is unticked");
            } else if (setting.EndsWith("true", StringComparison.OrdinalIgnoreCase)) {
                yield return new PreflightItem("Siril binned pixel size", PreflightStatus.Warn,
                    $"'Update pixel size of binned images' is ticked ({setting} in {Path.GetFileName(config)}): Siril would double the 5.8 µm binned pixel to 11.6 µm",
                    "Siril › Preferences › FITS › untick 'Update pixel size of binned images', then quit Siril so it saves (the engine's siril-cli runs start from this configuration too)");
            } else {
                yield return new PreflightItem("Siril binned pixel size", PreflightStatus.Pass, "'Update pixel size of binned images' is unticked");
            }
        }

        private static IEnumerable<PreflightItem> StorageChecks(PreflightContext c) {
            var root = c.DataPaths?.ImagesRoot ?? Path.Combine(c.Home, "Astro", "Nightglass");
            var cloud = c.DataPaths?.CloudSyncWarning(root);
            var writable = WritableAncestor(root);
            if (writable == null) {
                yield return new PreflightItem("Images folder", PreflightStatus.Fail, $"{root} is not writable", "Pick another folder in Settings › Storage (e.g. ~/Astro/Nightglass)");
            } else if (cloud != null) {
                yield return new PreflightItem("Images folder", PreflightStatus.Fail, cloud, "Settings › Storage › Images folder: ~/Astro/Nightglass (outside Documents/Desktop)");
            } else {
                yield return new PreflightItem("Images folder", PreflightStatus.Pass, $"{root} (writable, not cloud-synced)");
            }

            var site = new GeoSite(c.Settings.Site.LatitudeDegrees, c.Settings.Site.LongitudeDegrees);
            var night = SkyMath.AstronomicalNight(c.Clock.Now, site);
            var hours = night is { } n ? Math.Max(1, (n.Dawn - n.Dusk).TotalHours + 1.5) : 10;
            var frame = FrameBytes(c.Settings.Optics);
            var framesPerHour = 3600 / Math.Max(1, c.SubSeconds);
            var perHour = frame * framesPerHour;
            var need = (long)(perHour * hours);
            var stackPerHour = (long)(StackingBytesPerLight(c.Settings.Optics) * framesPerHour);
            var free = writable == null ? null : c.FreeSpace(writable);
            var budget = string.Create(CultureInfo.InvariantCulture,
                $"bin-{c.Settings.Optics.Bin} frame {frame / 1e6:0.0} MB, {c.SubSeconds:0} s subs: {perHour / 1e9:0.0} GB/h, {need / 1e9:0.0} GB for {hours:0.#} h");
            var stacking = string.Create(CultureInfo.InvariantCulture,
                $"Siril's 32-bit RGB pp_ and r_ frames take {StackingBytesPerLight(c.Settings.Optics) / 1e6:0} MB per light (~{(double)StackingBytesPerLight(c.Settings.Optics) / frame:0}x the lights): {stackPerHour / 1e9:0} GB per hour of lights stacked");
            const string stackFix = "Free space before stacking (move old nights off the Mac), or delete each target's process/ folder once its stack is checked; stack long nights a target at a time";
            if (free is not long bytes) {
                yield return new PreflightItem("Free disk", PreflightStatus.Info, $"free space unknown; {budget}");
                yield return new PreflightItem("Stacking space", PreflightStatus.Info, $"free space unknown; {stacking}");
                yield break;
            }
            if (bytes < need) {
                yield return new PreflightItem("Free disk", PreflightStatus.Fail, string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB free; {budget}"),
                    "Move last nights' frames off the Mac (keep the Siril results) to free space");
            } else if (bytes < need + stackPerHour) {
                yield return new PreflightItem("Free disk", PreflightStatus.Warn,
                    string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB free; {budget}; after the lights, not enough left to stack even one hour of them ({stackPerHour / 1e9:0} GB)"),
                    stackFix);
            } else {
                yield return new PreflightItem("Free disk", PreflightStatus.Pass, string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0} GB free; {budget}"));
            }
            // After the night the lights are on disk; what is left is what Siril's intermediates can use
            var stackableHours = Math.Max(0, bytes - need) / (double)Math.Max(1, stackPerHour);
            if (stackableHours >= hours) {
                yield return new PreflightItem("Stacking space", PreflightStatus.Pass,
                    string.Create(CultureInfo.InvariantCulture, $"room to stack all {hours:0.#} h of lights; {stacking}"));
            } else {
                yield return new PreflightItem("Stacking space", stackableHours >= 1 ? PreflightStatus.Info : PreflightStatus.Warn,
                    string.Create(CultureInfo.InvariantCulture, $"after the night's lights, room to stack about {stackableHours:0.#} h of them, not all {hours:0.#} h ({(long)(stackPerHour * hours) / 1e9:0} GB); {stacking}"),
                    stackFix);
            }
        }

        private static PreflightItem BatteryCheck(PreflightContext c) {
            var p = c.Power?.Read() ?? PowerSourceInfo.Unavailable;
            if (!p.HasBattery || p.BatteryPercent is not int percent) {
                return new PreflightItem("Battery", p.Source == PowerSourceKind.AC ? PreflightStatus.Pass : PreflightStatus.Info, p.Summary);
            }
            if (percent < 30 && !p.IsCharging && p.Source != PowerSourceKind.AC) {
                return new PreflightItem("Battery", PreflightStatus.Fail, $"{p.Summary}: not enough for a night", "Charge to 100 % before carrying out (an M1 Air runs a night at ~10-20 %/h with the screen dim)");
            }
            if (percent < 90) {
                return new PreflightItem("Battery", PreflightStatus.Warn, p.Summary, "Charge to 100 % before going out");
            }
            return new PreflightItem("Battery", PreflightStatus.Pass, p.Summary);
        }

        private static IEnumerable<PreflightItem> SiteChecks(PreflightContext c) {
            var s = c.Settings.Site;
            var dLat = Math.Abs(s.LatitudeDegrees - PreflightContext.SiteLatitude) * 60;
            var dLon = Math.Abs(s.LongitudeDegrees - PreflightContext.SiteLongitude) * 60;
            yield return dLat <= 1.0 && dLon <= 1.0
                ? new PreflightItem("Site", PreflightStatus.Pass, string.Create(CultureInfo.InvariantCulture, $"{s.Name}: {s.LatitudeDegrees:0.0000}° N, {s.LongitudeDegrees:0.0000}° E (within 1′ of 22.25 N 114.18 E)"))
                : new PreflightItem("Site", PreflightStatus.Fail,
                    string.Create(CultureInfo.InvariantCulture, $"{s.LatitudeDegrees:0.0000}°, {s.LongitudeDegrees:0.0000}° is {Math.Max(dLat, dLon):0.0}′ from Deep Water Bay (22.25 N, 114.18 E)"),
                    "Settings › Site: latitude 22.25, longitude 114.18 (east positive), Save");
            var macOffset = TimeZoneInfo.Local.GetUtcOffset(c.Clock.Now.UtcDateTime).TotalHours;
            yield return Math.Abs(macOffset - s.UtcOffsetHours) < 0.01
                ? new PreflightItem("Time zone", PreflightStatus.Pass, string.Create(CultureInfo.InvariantCulture, $"Mac and site both UTC{s.UtcOffsetHours:+0.#;-0.#}"))
                : new PreflightItem("Time zone", PreflightStatus.Warn,
                    string.Create(CultureInfo.InvariantCulture, $"the Mac is on UTC{macOffset:+0.#;-0.#}, the site on UTC{s.UtcOffsetHours:+0.#;-0.#}: night folders and NINA's file names use the Mac's zone"),
                    "System Settings › General › Date & Time: set the time zone to Hong Kong");
        }

        private static PreflightItem HorizonCheck(PreflightContext c) {
            if (c.HorizonError != null) {
                return new PreflightItem("Horizon", PreflightStatus.Warn, c.HorizonError, "Fix or re-save it on Target › Horizon");
            }
            if (c.Horizon == null) {
                return new PreflightItem("Horizon", PreflightStatus.Warn, "no horizon: only the mathematical horizon guards slews", "Target › Horizon: enter or record the horizon, Save");
            }
            if (c.Horizon.IsEstimate) {
                return new PreflightItem("Horizon", PreflightStatus.Warn, $"estimate - measure it: {c.Horizon.Describe()}",
                    "Target › Horizon: at dusk point the scope along the skyline (every ~15° of azimuth), 'Record point from mount', then Save");
            }
            return new PreflightItem("Horizon", PreflightStatus.Pass, c.Horizon.Describe());
        }

        private static IEnumerable<PreflightItem> NightChecks(PreflightContext c) {
            var s = c.Settings.Site;
            var site = new GeoSite(s.LatitudeDegrees, s.LongitudeDegrees);
            var offset = TimeSpan.FromHours(s.UtcOffsetHours);
            var night = SkyMath.AstronomicalNight(c.Clock.Now, site);
            if (night is not { } n) {
                yield return new PreflightItem("Tonight", PreflightStatus.Warn, "no astronomical darkness in the next 24 h", "Check Settings › Site");
                yield break;
            }
            var dusk = n.Dusk.ToOffset(offset);
            var dawn = n.Dawn.ToOffset(offset);
            yield return new PreflightItem("Tonight", PreflightStatus.Pass, string.Create(CultureInfo.InvariantCulture,
                $"{(n.DarkNow ? "dark now" : $"astronomical dusk {dusk:ddd HH:mm}")}, dawn {dawn:ddd HH:mm} ({(n.Dawn - n.Dusk).TotalHours:0.0} h dark)"));

            var illumination = SkyMath.MoonIllumination(n.Dusk + ((n.Dawn - n.Dusk) / 2));
            DateTimeOffset? up = null;
            DateTimeOffset? down = null;
            var upMinutes = 0;
            var maxAlt = -90.0;
            for (var t = n.Dusk; t <= n.Dawn; t += TimeSpan.FromMinutes(10)) {
                var alt = SkyMath.MoonAltAz(t, site).Altitude;
                if (alt > 0) {
                    up ??= t;
                    down = t;
                    upMinutes += 10;
                    maxAlt = Math.Max(maxAlt, alt);
                }
            }
            var share = upMinutes / Math.Max(1, (n.Dawn - n.Dusk).TotalMinutes);
            var detail = up == null
                ? string.Create(CultureInfo.InvariantCulture, $"{illumination * 100:0}% lit, below the horizon all dark hours")
                : string.Create(CultureInfo.InvariantCulture, $"{illumination * 100:0}% lit, up {up.Value.ToOffset(offset):HH:mm}-{down.Value.ToOffset(offset):HH:mm} (max {maxAlt:0}°)");
            yield return illumination > 0.6 && share > 0.5
                ? new PreflightItem("Moon", PreflightStatus.Info, detail + ": a bright Moon; the dual-band filter helps, pick targets far from it")
                : new PreflightItem("Moon", PreflightStatus.Info, detail);
        }

        private static PreflightItem OpticsCheck(PreflightContext c) {
            var o = c.Settings.Optics;
            var scale1 = 206.264806 * o.PixelSizeMicrons / o.EffectiveFocalLengthMm;
            var fieldW = o.SensorWidth * scale1 / 3600.0;
            var fieldH = o.SensorHeight * scale1 / 3600.0;
            var train = o.UseReducer ? $"f/6.3 reducer {o.ReducerFocalLengthMm:0} mm" : $"native f/10 {o.FocalLengthMm:0} mm";
            var detail = string.Create(CultureInfo.InvariantCulture,
                $"{train}: {scale1:0.00}″/px bin 1, {o.PixelScaleArcsec:0.00}″/px bin {o.Bin}; field {fieldW:0.000}° × {fieldH:0.000}°; the field-rotation max sub is the same on both trains");
            if (fieldH < AstapD80MinimumFieldDegrees) {
                return new PreflightItem("Optical train", PreflightStatus.Warn,
                    string.Create(CultureInfo.InvariantCulture, $"{detail}. Field height {fieldH:0.0000}° is below ASTAP D80's {AstapD80MinimumFieldDegrees}° minimum (plan risk 4): solves may fail"),
                    "First night: fit the f/6.3 reducer and tick Settings › Optics › Reducer fitted (or Connect › Optical train), Save");
            }
            return new PreflightItem("Optical train", PreflightStatus.Pass, detail + " (matches what is fitted? the solver's scale comes from here)");
        }

        private static bool IsExecutable(string path) {
            try {
                return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PlatformNotSupportedException) {
                return true;
            }
        }

        /// <summary>The path, or its nearest existing parent, if a file can be created there; null otherwise.</summary>
        internal static string WritableAncestor(string path) {
            var dir = path;
            while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) {
                dir = Path.GetDirectoryName(dir);
            }
            if (string.IsNullOrEmpty(dir)) {
                return null;
            }
            var probe = Path.Combine(dir, $".preflight-{Guid.NewGuid():N}");
            try {
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                return null;
            }
        }

        internal static long? DefaultFreeSpace(string path) {
            try {
                return new DriveInfo(path).AvailableFreeSpace;
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) {
                return null;
            }
        }
    }

    /// <summary>Builds preflight contexts for the app and runs <c>--preflight [--with-devices]</c> from the command line.</summary>
    public static class PreflightRunner {
        public const string Flag = "--preflight";
        public const string WithDevicesFlag = "--with-devices";

        /// <summary>
        /// The app's own state: its settings, horizon, serial ports (read on the calling thread, the UI thread), the connected
        /// camera and engine. USB is scanned for the camera only when <paramref name="withDevices"/> and the camera is not
        /// already connected here.
        /// </summary>
        public static PreflightContext ForServices(AppServices services, bool withDevices) {
            ArgumentNullException.ThrowIfNull(services);
            services.Mount.RefreshPorts();
            var ports = services.Mount.AvailablePorts.ToArray();
            var camera = services.Camera.State == DeviceConnectionState.Connected ? services.Camera.Info?.Model ?? "camera" : null;
            return new PreflightContext {
                Settings = services.Settings.Current.Clone(),
                SettingsFile = (services.Settings as JsonSettingsStore)?.FilePath,
                SettingsWarning = services.SettingsLoadWarning,
                DataPaths = services.DataPaths,
                Clock = services.Clock,
                Power = services.Power.Source,
                SerialPorts = () => ports,
                ExpectedPort = services.Engine != null ? services.Mount.PortName : Preflight.PortFromProfile(services.DataPaths.EngineDataDirectory),
                Horizon = services.Horizon,
                HorizonError = services.HorizonError,
                ActiveDeviceSource = services.ActiveDeviceSource,
                EngineError = services.EngineError,
                WithDevices = withDevices && camera == null && !services.DevicesSimulated,
                ConnectedCamera = services.DevicesSimulated ? null : camera,
                LiveEphemerisCheck = services.Engine != null,
            };
        }

        /// <summary>
        /// The context for the command line and the smoke test: the saved settings and horizon of this Mac's user (or of
        /// <paramref name="homeDirectory"/>, for tests). Leaves nothing behind, not even a copy of a bad settings file.
        /// </summary>
        public static PreflightContext ForUser(bool withDevices, bool liveEphemeris, string homeDirectory = null) {
            var info = AppInfo.Current;
            var basePaths = new UserDataPaths(info.Identity, homeDirectory);
            // Read without keeping a bad file aside: a preflight writes nothing
            var settings = JsonSettingsStore.Peek(basePaths.SettingsFile, out var settingsWarning);
            HorizonProfile horizon;
            string horizonError = null;
            if (File.Exists(basePaths.HorizonFile)) {
                try {
                    horizon = HorizonProfile.Load(basePaths.HorizonFile, out _);
                } catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) {
                    horizonError = $"The horizon file {basePaths.HorizonFile} could not be read ({ex.Message}); the app uses the built-in estimate.";
                    horizon = HorizonProfile.SiteEstimate();
                }
            } else {
                horizon = HorizonProfile.SiteEstimate();
            }
            return new PreflightContext {
                Settings = settings,
                SettingsFile = basePaths.SettingsFile,
                SettingsWarning = settingsWarning,
                DataPaths = new UserDataPaths(info.Identity, homeDirectory, settings.ImagesRoot),
                Power = OperatingSystem.IsMacOS() ? new MacPowerSource() : null,
                ExpectedPort = Preflight.PortFromProfile(basePaths.EngineDataDirectory),
                Horizon = horizon,
                HorizonError = horizonError,
                ActiveDeviceSource = settings.DeviceSource,
                WithDevices = withDevices,
                LiveEphemerisCheck = liveEphemeris,
            };
        }

        /// <summary>
        /// <c>--preflight [--with-devices]</c>: prints one PASS/INFO/WARN/FAIL line per check with its fix; exit 0 when nothing
        /// failed, 1 otherwise, 2 when the preflight itself could not run. NOVAS is checked live in a throw-away engine data folder,
        /// so a running app's NINA logs and profile are not touched.
        /// </summary>
        public static int RunCli(string[] args, TextWriter output) {
            var info = AppInfo.Current;
            var withDevices = args.Contains(WithDevicesFlag);
            var temp = Path.Combine(Path.GetTempPath(), $"{info.ShortName}-preflight-{Guid.NewGuid():N}");
            try {
                var live = false;
                try {
                    if (EngineRuntime.DataDirectory == null) {
                        EngineRuntime.Initialize(temp);
                    }
                    live = true;
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) {
                    output.WriteLine($"(NOVAS live check skipped: {ex.Message})");
                }
                output.WriteLine($"{info.DisplayName} {info.Version} preflight{(withDevices ? " (with devices: the ZWO SDK scans USB)" : " (no devices opened)")}");
                var report = Preflight.Run(ForUser(withDevices, live));
                output.Write(report.Format());
                return report.ExitCode;
            } catch (Exception ex) {
                output.WriteLine($"Preflight could not run: {ex.GetType().Name}: {ex.Message}");
                return 2;
            } finally {
                try {
                    if (Directory.Exists(temp)) {
                        Directory.Delete(temp, recursive: true);
                    }
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                    // NINA's log file may still be open; it is in the temporary folder
                }
            }
        }
    }
}
