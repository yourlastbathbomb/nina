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
using NINA.Mac.App.Diagnostics;
using NINA.Mac.App.Services;
using NINA.Mac.App.Theming;
using NINA.Mac.App.ViewModels;
using NINA.Mac.App.Views;
using NINA.Mac.Platform;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NINA.Mac.App {

    /// <summary>
    /// <c>--smoke-test [--screenshots DIR]</c>: initialise the real platform services and the whole UI headlessly,
    /// run a simulated night through the view-models, print one line per check and exit 0 only if all pass. It also
    /// runs <c>--startup-check</c> in a child process to cover the real Avalonia.Native setup. Nothing is shown on
    /// screen. Used on the packaged .app binary by mac/packaging/package-app.sh.
    /// </summary>
    public static class SmokeTest {

        public static int Run(string[] args, TextWriter output) {
            var total = Stopwatch.StartNew();
            var screenshots = ArgValue(args, "--screenshots");
            var results = new List<(string Name, bool Ok, string Detail, double Ms)>();
            var info = AppInfo.Current;

            void Check(string name, Func<string> body) {
                var sw = Stopwatch.StartNew();
                try {
                    var detail = body();
                    results.Add((name, true, detail, sw.Elapsed.TotalMilliseconds));
                } catch (Exception ex) {
                    results.Add((name, false, $"{ex.GetType().Name}: {ex.Message}", sw.Elapsed.TotalMilliseconds));
                }
                var r = results[^1];
                output.WriteLine($"[{(r.Ok ? "ok" : "FAIL")}] {r.Name} ({r.Ms:0} ms): {r.Detail}");
            }

            output.WriteLine($"{info.DisplayName} {info.Version} smoke test (pid {Environment.ProcessId}, cwd '{Environment.CurrentDirectory}')");

            Check("identity", () => {
                var names = new List<(string What, string Value)> {
                    ("display name", info.DisplayName),
                    ("short name", info.ShortName),
                    ("bundle id", info.BundleId),
                };
                var inBundle = ResourcePaths.Current.IsAppBundle && OperatingSystem.IsMacOS();
                if (inBundle) {
                    // What the Dock, menu bar and Finder show comes from the packaged Info.plist and executable name
                    names.Add(("executable", Path.GetFileName(Environment.ProcessPath)));
                    foreach (var key in new[] { "CFBundleName", "CFBundleDisplayName", "CFBundleExecutable", "CFBundleIdentifier" }) {
                        names.Add(($"Info.plist {key}", MacBundle.MainBundleInfoString(key)
                            ?? throw new InvalidOperationException($"Info.plist has no string {key}")));
                    }
                }
                var upstream = names.Where(n => AppInfo.ContainsNina(n.Value)).Select(n => $"{n.What} '{n.Value}'").ToArray();
                if (upstream.Length > 0) {
                    throw new InvalidOperationException($"contains NINA: {string.Join(", ", upstream)}");
                }
                return $"'{info.DisplayName}', short '{info.ShortName}', bundle id {info.BundleId}, based on N.I.N.A. {info.NinaBaseVersion}" +
                    (inBundle ? $"; {names.Count - 3} bundle names checked" : "");
            });

            var paths = ResourcePaths.Current;
            Check("resource paths", () => {
                var detail = $"bundle={paths.IsAppBundle}, base={paths.BaseDirectory}, resources={paths.ResourcesDirectory}, frameworks={paths.FrameworksDirectory}";
                if (paths.IsAppBundle && OperatingSystem.IsMacOS()) {
                    var nsBundle = MacBundle.MainBundlePath();
                    if (!string.Equals(Path.GetFullPath(nsBundle), paths.BundlePath, StringComparison.Ordinal)) {
                        throw new InvalidOperationException($"NSBundle.mainBundle is '{nsBundle}' but ResourcePaths found '{paths.BundlePath}'");
                    }
                    var license = paths.GetResource("LICENSE.txt");
                    detail += $"; NSBundle agrees; {Path.GetFileName(license)} found; bundle id {MacBundle.MainBundleIdentifier()}";
                }
                return detail;
            });

            Check("user data paths", () => {
                var data = new UserDataPaths(info.Identity);
                data.EnsureAppDirectories();
                return $"images {data.ImagesRoot}, settings {data.SettingsFile}, logs {data.LogsDirectory}" +
                    (data.CloudSyncWarning(data.ImagesRoot) is { } w ? $" WARNING {w}" : "");
            });

            Check("settings", () => {
                var store = new JsonSettingsStore(new UserDataPaths(info.Identity).SettingsFile);
                return (File.Exists(store.FilePath) ? "loaded " : "defaults (no file yet) ") + $"site {store.Current.Site.Name}, {store.Current.Optics.EffectiveFocalLengthMm} mm" +
                    (store.LoadWarning is { } w ? $" WARNING {w}" : "");
            });

            Check("power source", () => new MacPowerSource().Read().Summary);

            Check("keep-awake (IOKit + NSProcessInfo)", () => {
                using var keepAwake = new MacKeepAwake();
                var reason = $"{info.ShortName} smoke test {Guid.NewGuid():N}";
                var state = keepAwake.Engage(reason, KeepAwakeOptions.Default);
                var listed = PowerAssertion.ListForProcess(Environment.ProcessId).Where(a => a.Name == reason).Select(a => a.Type).ToArray();
                keepAwake.Release();
                var after = PowerAssertion.ListForProcess(Environment.ProcessId).Count(a => a.Name == reason);
                if (!state.SystemSleepPrevented || !state.DisplaySleepPrevented || !state.AppNapPrevented) {
                    throw new InvalidOperationException($"not fully engaged: {state.Summary} {state.Error}");
                }
                if (!listed.Contains(PowerAssertion.ToIOKitType(PowerAssertionType.PreventUserIdleSystemSleep)) || !listed.Contains(PowerAssertion.ToIOKitType(PowerAssertionType.PreventUserIdleDisplaySleep))) {
                    throw new InvalidOperationException($"IOKit does not list both assertions, saw [{string.Join(", ", listed)}]");
                }
                if (after != 0) {
                    throw new InvalidOperationException($"{after} assertions still listed after release");
                }
                return $"engaged ({string.Join(", ", listed)}), released";
            });

            Check("native libraries", () => {
                var loaded = new List<string>();
                var candidates = paths.VendorLibraries().ToList();
                foreach (var name in new[] { "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib", "libAvaloniaNative.dylib" }) {
                    if (paths.FindNativeLibrary(name) is { } p && !candidates.Contains(p)) {
                        candidates.Add(p);
                    }
                }
                foreach (var lib in candidates) {
                    if (!NativeLibrary.TryLoad(lib, out var handle)) {
                        throw new DllNotFoundException($"dlopen failed for {lib}");
                    }
                    loaded.Add(Path.GetFileName(lib));
                    NativeLibrary.Free(handle);
                }
                return loaded.Count == 0 ? "none found" : string.Join(", ", loaded);
            });

            // Real devices composed through the headless engine without opening anything (engine assemblies, NINA's data, natives)
            Check("engine (Real devices composed, nothing opened)", EngineSmoke.Run);

            // The real launch path (Avalonia.Native + Skia + HarfBuzz) in a child process: the headless platform below
            // registers its own text shaper, so it cannot show that Program.BuildAvaloniaApp() is complete.
            Check("native startup (Avalonia.Native, child process)", () => StartupCheck.RunInChildProcess(TimeSpan.FromSeconds(60)));

            MainWindowViewModel viewModel = null;
            AppServices services = null;
            MainWindow window = null;
            Check("avalonia headless (skia)", () => {
                AppBuilder.Configure<App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .SetupWithoutStarting();
                var app = (App)Application.Current;
                services = AppServices.Create(new AppServicesOptions {
                    Clock = new ManualClock(new DateTimeOffset(2026, 10, 10, 21, 0, 0, TimeSpan.FromHours(8))),
                    Settings = new MemorySettingsStore(),
                    KeepAwake = new NullKeepAwake(),
                    FastSimulation = true,
                });
                viewModel = new MainWindowViewModel(services, app.Theme);
                window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
                window.Show();
                return $"{Application.Current.GetType().Name} on {AvaloniaLocatorName()}";
            });

            if (window != null) {
                Check("render all screens", () => {
                    var frames = ScreenRenderer.RenderAllPages(window, viewModel, screenshots);
                    var bad = frames.Where(f => !f.LooksRendered).Select(f => $"{f.Page} (view {f.View ?? "none"}, page {f.Content.DistinctColors}c)").ToArray();
                    if (bad.Length > 0) {
                        throw new InvalidOperationException($"blank or missing pages: {string.Join(", ", bad)}");
                    }
                    return string.Join(", ", frames.Select(f => $"{f.Page} {f.Stats.Width}x{f.Stats.Height}/{f.Stats.DistinctColors}c (page {f.Content.DistinctColors}c)"));
                });

                Check("simulated night", () => {
                    Wait(viewModel.Connect.StartNightCommand.ExecuteAsync(null));
                    services.SimCamera.SetCooler(true, 0);
                    ((ManualClock)services.Clock).Advance(TimeSpan.FromMinutes(10));
                    services.Tick();
                    Wait(viewModel.Focus.TakeOneCommand.ExecuteAsync(null));
                    viewModel.Target.SearchText = "NGC 253";
                    viewModel.Target.FrameCount = 3;
                    viewModel.Target.UseForRunCommand.Execute(null);
                    Wait(viewModel.Run.StartCommand.ExecuteAsync(null));
                    var progress = services.Session.Progress;
                    Wait(viewModel.Teardown.RunTeardownCommand.ExecuteAsync(null));
                    var steps = string.Join(" ", viewModel.Teardown.Steps.Select(s => s.Status.ToString()[0]));
                    if (viewModel.Teardown.Steps.Any(s => s.Status == TeardownStepStatus.Failed)) {
                        throw new InvalidOperationException($"teardown failed: {viewModel.Teardown.ErrorMessage}");
                    }
                    return $"session {progress.FramesDone}/{progress.FrameCount} ({progress.StopReason}), HFR {progress.LastHfr:0.00}, teardown [{steps}]";
                });

                Check("night vision", () => {
                    viewModel.ToggleNightVisionCommand.Execute(null);
                    var stats = ScreenRenderer.Capture(window, screenshots == null ? null : Path.Combine(screenshots, "night-vision.png"));
                    viewModel.ToggleNightVisionCommand.Execute(null);
                    if (stats.NonRedSamples > 0) {
                        throw new InvalidOperationException($"{stats.NonRedSamples}/{stats.Samples} samples are not red (max G {stats.MaxGreen}, max B {stats.MaxBlue}, max G,B/R {stats.MaxGreenBlueToRed:0.00})");
                    }
                    return $"red only (max R/G/B {stats.MaxRed}/{stats.MaxGreen}/{stats.MaxBlue}, max G,B/R {stats.MaxGreenBlueToRed:0.00})";
                });
                window.Close();
            }

            var failed = results.Count(r => !r.Ok);
            output.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")}: {results.Count - failed}/{results.Count} checks in {total.Elapsed.TotalMilliseconds:0} ms");
            return failed == 0 ? 0 : 1;
        }

        private static void Wait(Task task) {
            var sw = Stopwatch.StartNew();
            while (!task.IsCompleted) {
                ScreenRenderer.Pump();
                if (sw.Elapsed > TimeSpan.FromSeconds(30)) {
                    throw new TimeoutException("Simulated step did not finish");
                }
            }
            task.GetAwaiter().GetResult();
        }

        private static string AvaloniaLocatorName() => typeof(AvaloniaHeadlessPlatformOptions).Assembly.GetName().Name;

        private static string ArgValue(string[] args, string name) {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
        }
    }
}
