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
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NINA.Mac.App.ViewModels;
using NINA.Mac.Platform;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Diagnostics {

    /// <summary>
    /// <c>--gui-smoke [--screenshots DIR]</c>: the real launch, on screen, for a few seconds. It starts
    /// <see cref="Program.BuildAvaloniaApp"/> (Avalonia.Native + Skia + HarfBuzz, Dock icon on) with the classic desktop
    /// lifetime exactly as a Finder launch does; <see cref="App"/> builds the main window with the real services and the
    /// lifetime shows it. Once the window has opened this checks that it is a native NSWindow, that the native compositor
    /// rendered frames, and renders every page with the real platform's renderer. Then the app quits by itself and the
    /// process exits 0 if every check passed. A watchdog ends the process if the window never opens or never finishes.
    /// The only difference from a normal launch: the window is shown without taking focus (ShowActivated = false), so
    /// it does not catch keystrokes meant for another app. Needs a logged-in GUI session with the screen unlocked.
    /// </summary>
    public sealed class GuiSmoke {

        public const string Flag = "--gui-smoke";

        private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

        private readonly TextWriter output;
        private readonly string screenshots;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<(string Name, bool Ok)> results = new();
        private AppBuilder builder;
        private int finished;
        private int exitCode = 1;

        private GuiSmoke(TextWriter output, string screenshots) {
            this.output = output;
            this.screenshots = screenshots;
        }

        /// <summary>Set while <c>--gui-smoke</c> runs; <see cref="App.OnFrameworkInitializationCompleted"/> hands it the main window.</summary>
        internal static GuiSmoke Active { get; private set; }

        public static int Run(string[] args, TextWriter output, TimeSpan timeout) {
            var info = AppInfo.Current;
            var smoke = new GuiSmoke(output, ArgValue(args, "--screenshots"));
            output.WriteLine($"{info.DisplayName} {info.Version} GUI smoke test (pid {Environment.ProcessId}, cwd '{Environment.CurrentDirectory}'): real launch, the window shows for a moment");
            if (MacDisplays.NoActiveDisplayReason() is { } noDisplay) {
                smoke.Record("display", false, noDisplay);
                smoke.Summarise();
                return smoke.exitCode;
            }
            using var watchdog = new Timer(_ => smoke.Abort(timeout), null, timeout, Timeout.InfiniteTimeSpan);
            Active = smoke;
            try {
                smoke.builder = Program.BuildAvaloniaApp();
                smoke.builder.StartWithClassicDesktopLifetime(args);
            } catch (Exception ex) {
                // e.g. "No text shaping system configured" from AppBuilder.Setup(): the window never opens
                smoke.Record("launch", false, $"{ex.GetType().Name}: {ex.Message}");
            } finally {
                Active = null;
            }
            if (Interlocked.CompareExchange(ref smoke.finished, 1, 0) == 0) {
                smoke.Record("main window", false, "the app ended before the main window opened and finished its checks");
                smoke.Summarise();
            }
            return smoke.exitCode;
        }

        /// <summary>Called by the app once the main window exists and before the lifetime shows it.</summary>
        internal void Attach(IClassicDesktopStyleApplicationLifetime desktop, MainWindowViewModel viewModel) {
            var window = desktop.MainWindow ?? throw new InvalidOperationException("the app built no main window");
            window.ShowActivated = false;
            Dispatcher.UIThread.UnhandledException += (_, e) => {
                Record("UI thread", false, $"unhandled {e.Exception.GetType().Name}: {e.Exception.Message}");
                e.Handled = true;
                Finish(desktop);
            };
            window.Opened += async (_, _) => {
                try {
                    await CheckWindowAsync(window, viewModel);
                } catch (Exception ex) {
                    Record("main window", false, $"{ex.GetType().Name}: {ex.Message}");
                }
                Finish(desktop);
            };
        }

        private async Task CheckWindowAsync(Window window, MainWindowViewModel viewModel) {
            var opened = clock.Elapsed;
            Check("platform", () => {
                // Avalonia.Native leaves WindowingSubsystemName empty; the next check proves the windowing (an NSWindow)
                if (string.IsNullOrEmpty(builder.TextShapingSubsystemName) || string.IsNullOrEmpty(builder.RenderingSubsystemName) ||
                    (builder.WindowingSubsystemName ?? "").Contains("Headless", StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidOperationException($"unexpected subsystems: windowing '{builder.WindowingSubsystemName}', rendering '{builder.RenderingSubsystemName}', text shaping '{builder.TextShapingSubsystemName}'");
                }
                return $"rendering {builder.RenderingSubsystemName}, text shaping {builder.TextShapingSubsystemName}, platform settings {Application.Current.PlatformSettings?.GetType().Name ?? "?"}; " +
                    $"main window opened {opened.TotalMilliseconds:0} ms after launch";
            });

            Check("native window", () => {
                var handle = window.TryGetPlatformHandle();
                if (handle?.HandleDescriptor != "NSWindow" || handle.Handle == IntPtr.Zero) {
                    throw new InvalidOperationException($"platform handle is {handle?.HandleDescriptor ?? "null"}, not an NSWindow");
                }
                if (!window.IsVisible || !(window.ClientSize.Width > 0) || !(window.ClientSize.Height > 0)) {
                    throw new InvalidOperationException($"window not visible (IsVisible {window.IsVisible}, client {window.ClientSize})");
                }
                var screen = window.Screens.ScreenFromWindow(window);
                return $"NSWindow 0x{handle.Handle:x} '{window.Title}', client {window.ClientSize.Width:0}x{window.ClientSize.Height:0} pt at scale {window.RenderScaling:0.##}" +
                    (screen == null ? "" : $" on '{screen.DisplayName}' ({screen.Bounds.Width}x{screen.Bounds.Height} as Avalonia reports it)");
            });

            var compositor = ElementComposition.GetElementVisual(window)?.Compositor;
            await CheckAsync("native frames", async () => {
                if (compositor == null) {
                    throw new InvalidOperationException("the window has no compositor");
                }
                var sw = Stopwatch.StartNew();
                await WithTimeout(compositor.RequestCompositionBatchCommitAsync().Rendered, "the first frame to render");
                var first = sw.Elapsed;
                var ticks = 0;
                var ticked = new TaskCompletionSource();
                void Tick(TimeSpan _) {
                    if (++ticks >= 3) {
                        ticked.TrySetResult();
                    } else {
                        window.RequestAnimationFrame(Tick);
                    }
                }
                window.RequestAnimationFrame(Tick);
                await WithTimeout(ticked.Task, "3 animation frames");
                return $"compositor rendered a frame in {first.TotalMilliseconds:0} ms; render loop ticked {ticks} times in {(sw.Elapsed - first).TotalMilliseconds:0} ms";
            });

            Check("text", () => {
                var text = new FormattedText("Nightglass −12.5 °C 87%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.White);
                var title = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == viewModel.About.Info.ShortName);
                if (!(text.Width > 0) || title == null || !(title.Bounds.Width > 0) || !(title.Bounds.Height > 0)) {
                    throw new InvalidOperationException($"text not laid out: measured {text.Width}x{text.Height}, sidebar title {title?.Bounds.ToString() ?? "missing"}");
                }
                return $"default font '{FontManager.Current.DefaultFontFamily.Name}', sidebar title '{title.Text}' {title.Bounds.Width:0.0}x{title.Bounds.Height:0.0}";
            });

            await CheckAsync("every page, real renderer", async () => {
                var start = viewModel.CurrentPage;
                var frames = new List<PageFrame>();
                foreach (var page in viewModel.Pages) {
                    viewModel.NavigateTo(page.Kind);
                    await WithTimeout(compositor.RequestCompositionBatchCommitAsync().Rendered, $"a frame of {page.Kind}");
                    frames.Add(Capture(window, page));
                }
                if (start != null) {
                    viewModel.NavigateTo(start.Kind);
                }
                var bad = frames.Where(f => !f.LooksRendered).Select(f => $"{f.Page} (view {f.View ?? "none"}, page {f.Content.DistinctColors}c)").ToArray();
                if (bad.Length > 0) {
                    throw new InvalidOperationException($"blank or missing pages: {string.Join(", ", bad)}");
                }
                return string.Join(", ", frames.Select(f => $"{f.Page} {f.Stats.Width}x{f.Stats.Height}/{f.Content.DistinctColors}c"));
            });

            Check("app menu", () => {
                var items = NativeMenu.GetMenu(Application.Current)?.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).Select(i => i.Header).ToArray()
                    ?? throw new InvalidOperationException("no application menu set");
                if (!items.Contains($"About {viewModel.About.Info.ShortName}")) {
                    throw new InvalidOperationException($"no About item: [{string.Join(", ", items)}]");
                }
                return string.Join(", ", items);
            });
        }

        /// <summary>The window drawn by the real platform's renderer (Skia on the Avalonia.Native platform), at its backing scale.</summary>
        private PageFrame Capture(Window window, PageViewModel page) {
            window.UpdateLayout();
            var scale = window.RenderScaling;
            var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            if (screenshots != null) {
                Directory.CreateDirectory(screenshots);
                bitmap.Save(Path.Combine(screenshots, $"gui-{(int)page.Kind + 1}-{page.Kind}.png"), PngBitmapEncoderOptions.Default);
            }
            return ScreenRenderer.AnalyzePage(window, page, bitmap);
        }

        private static async Task WithTimeout(Task task, string what) {
            if (await Task.WhenAny(task, Task.Delay(FrameTimeout)) != task) {
                throw new TimeoutException($"waited {FrameTimeout.TotalSeconds:0} s for {what} (is the screen locked or the window off screen?)");
            }
            await task;
        }

        private void Check(string name, Func<string> body) {
            var sw = Stopwatch.StartNew();
            try {
                var detail = body();
                Record(name, true, $"({sw.Elapsed.TotalMilliseconds:0} ms) {detail}");
            } catch (Exception ex) {
                Record(name, false, $"({sw.Elapsed.TotalMilliseconds:0} ms) {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task CheckAsync(string name, Func<Task<string>> body) {
            var sw = Stopwatch.StartNew();
            try {
                var detail = await body();
                Record(name, true, $"({sw.Elapsed.TotalMilliseconds:0} ms) {detail}");
            } catch (Exception ex) {
                Record(name, false, $"({sw.Elapsed.TotalMilliseconds:0} ms) {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Record(string name, bool ok, string detail) {
            lock (results) {
                results.Add((name, ok));
                output.WriteLine($"[{(ok ? "ok" : "FAIL")}] {name} {detail}");
            }
        }

        private void Summarise() {
            lock (results) {
                var failed = results.Count(r => !r.Ok);
                exitCode = failed == 0 && results.Count > 0 ? 0 : 1;
                output.WriteLine($"{(exitCode == 0 ? "PASS" : "FAIL")}: {results.Count - failed}/{results.Count} GUI checks in {clock.Elapsed.TotalMilliseconds:0} ms");
                output.Flush();
            }
        }

        private void Finish(IClassicDesktopStyleApplicationLifetime desktop) {
            if (Interlocked.CompareExchange(ref finished, 1, 0) != 0) {
                return;
            }
            Summarise();
            desktop.Shutdown(exitCode);
        }

        private void Abort(TimeSpan timeout) {
            if (Interlocked.CompareExchange(ref finished, 1, 0) != 0) {
                return;
            }
            Record("watchdog", false, $"the main window did not open and finish its checks within {timeout.TotalSeconds:0} s");
            Summarise();
            Environment.Exit(3);
        }

        private static string ArgValue(string[] args, string name) {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
        }
    }
}
