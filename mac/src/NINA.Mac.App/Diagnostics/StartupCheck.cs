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
using Avalonia.Media;
using NINA.Mac.App.Services;
using NINA.Mac.App.ViewModels;
using NINA.Mac.App.Views;
using NINA.Mac.Platform;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace NINA.Mac.App.Diagnostics {

    /// <summary>
    /// <c>--startup-check</c>: sets up the real desktop platform exactly as a Finder launch does
    /// (<see cref="Program.BuildAvaloniaApp"/>: Avalonia.Native + Skia + HarfBuzz), with the Dock icon off, then lays out
    /// the main window once without showing it. Avalonia allows one platform setup per process and the smoke test
    /// uses the headless one, so the smoke test runs this in a child process (<see cref="RunInChildProcess"/>).
    /// Needs a logged-in GUI session (AppKit) and an active display, like the app itself. With every display asleep
    /// Avalonia.Native cannot start (<see cref="MacDisplays"/>), so it then checks only the builder's configuration and
    /// prints a "partial:" result; <c>--gui-smoke</c> is the full check of a real launch.
    /// </summary>
    public static class StartupCheck {

        public const string Flag = "--startup-check";

        public static int Run(TextWriter output) {
            try {
                var builder = Program.BuildAvaloniaApp().With(new MacOSPlatformOptions { ShowInDock = false });
                if (string.IsNullOrEmpty(builder.TextShapingSubsystemName)) {
                    throw new InvalidOperationException("Program.BuildAvaloniaApp() configures no text shaping subsystem (UseHarfBuzz() missing); AppBuilder.Setup() would throw on every launch");
                }
                if (string.IsNullOrEmpty(builder.RenderingSubsystemName)) {
                    throw new InvalidOperationException("Program.BuildAvaloniaApp() configures no rendering subsystem (UseSkia() missing)");
                }
                if (MacDisplays.NoActiveDisplayReason() is { } noDisplay) {
                    output.WriteLine($"partial: rendering {builder.RenderingSubsystemName} and text shaping {builder.TextShapingSubsystemName} configured; platform not started: {noDisplay}");
                    return 0;
                }
                builder.SetupWithoutStarting();

                var text = new FormattedText("Nightglass −12.5 °C 87%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.White);
                if (!(text.Width > 0) || !(text.Height > 0)) {
                    throw new InvalidOperationException($"FormattedText measured {text.Width}x{text.Height}");
                }

                // The main window as OnFrameworkInitializationCompleted builds it, created on the native platform and laid out,
                // but Show() is never called. Simulated services keep this off the real Mac's settings and power state.
                var app = (App)Application.Current;
                using var services = AppServices.Create(new AppServicesOptions {
                    Clock = new ManualClock(new DateTimeOffset(2026, 10, 10, 21, 0, 0, TimeSpan.FromHours(8))),
                    Settings = new MemorySettingsStore(),
                    KeepAwake = new NullKeepAwake(),
                    FastSimulation = true,
                });
                var viewModel = new MainWindowViewModel(services, app.Theme);
                var window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
                window.Measure(new Size(1280, 800));
                window.Arrange(new Rect(0, 0, 1280, 800));
                var size = window.DesiredSize;
                window.Close();
                if (!(size.Width > 0) || !(size.Height > 0)) {
                    throw new InvalidOperationException($"main window measured {size}");
                }

                output.WriteLine($"ok: rendering {builder.RenderingSubsystemName}, text shaping {builder.TextShapingSubsystemName}, windowing {(Application.Current.PlatformSettings?.GetType().Name ?? "?")}, " +
                    $"default font '{FontManager.Current.DefaultFontFamily.Name}', text {text.Width:0.0}x{text.Height:0.0}, main window laid out at {size.Width:0}x{size.Height:0}");
                return 0;
            } catch (Exception ex) {
                output.WriteLine($"FAIL: {ex}");
                return 1;
            }
        }

        /// <summary>
        /// Runs <c>--startup-check</c> in a fresh process of this app and returns its one-line result ("ok: ..." or, with
        /// no active display, "partial: ..."); throws if it fails.
        /// </summary>
        public static string RunInChildProcess(TimeSpan timeout) {
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("Environment.ProcessPath is unknown");
            var psi = new ProcessStartInfo(self) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = "/",
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(self), "dotnet", StringComparison.OrdinalIgnoreCase)) {
                // Started as "dotnet NINA.Mac.App.dll": run the same assembly again
                psi.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? typeof(StartupCheck).Assembly.Location);
            }
            psi.ArgumentList.Add(Flag);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {self}");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds)) {
                p.Kill(true);
                throw new TimeoutException($"{Flag} did not finish within {timeout.TotalSeconds:0} s");
            }
            p.WaitForExit();
            var lines = (stdout.Result + stderr.Result).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (p.ExitCode != 0) {
                throw new InvalidOperationException($"{Flag} exited {p.ExitCode}: {string.Join(" | ", lines.Take(6))}");
            }
            return lines.LastOrDefault(l => l.StartsWith("ok:", StringComparison.Ordinal) || l.StartsWith("partial:", StringComparison.Ordinal)) ?? throw new InvalidOperationException($"{Flag} printed no result: {string.Join(" | ", lines)}");
        }
    }
}
