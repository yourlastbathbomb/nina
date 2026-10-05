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
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NINA.Mac.App.Diagnostics;
using NINA.Mac.App.Services;
using NINA.Mac.App.Theming;
using NINA.Mac.App.ViewModels;
using NINA.Mac.App.Views;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NINA.Mac.App.Test.Ui {

    /// <summary>Collects Avalonia binding/property warnings so tests can fail on runtime binding errors.</summary>
    public sealed class CapturingLogSink : ILogSink {
        public ConcurrentQueue<string> Messages { get; } = new();

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object source, string messageTemplate) => Add(level, area, source, messageTemplate, Array.Empty<object>());

        public void Log(LogEventLevel level, string area, object source, string messageTemplate, params object[] propertyValues) => Add(level, area, source, messageTemplate, propertyValues);

        private void Add(LogEventLevel level, string area, object source, string template, object[] values) {
            if (level >= LogEventLevel.Warning) {
                Messages.Enqueue($"[{level}] {area} {source?.GetType().Name}: {template} {string.Join(" | ", values ?? Array.Empty<object>())}");
            }
        }
    }

    /// <summary>The real views, rendered with Skia under Avalonia.Headless; input goes through the headless window.</summary>
    [TestFixture]
    [NonParallelizable]
    public class ViewTests : HeadlessTestBase {
        private static readonly CapturingLogSink sink = new();

        [OneTimeSetUp]
        public void InstallLogSink() => Logger.Sink = sink;

        [OneTimeTearDown]
        public void RemoveLogSink() => Logger.Sink = null;

        private static ThemeManager Theme => ((NINA.Mac.App.App)Application.Current).Theme;

        private static (AppServices Services, MainWindowViewModel Vm, MainWindow Window) Open() {
            var services = CreateServices();
            Theme.Apply(false);
            var vm = new MainWindowViewModel(services, Theme);
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            ScreenRenderer.Pump();
            return (services, vm, window);
        }

        private static T Find<T>(Visual root, string name) where T : Control {
            ScreenRenderer.Pump();
            return root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
                ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}");
        }

        private static void Click(Window window, Control control) {
            ScreenRenderer.Pump();
            control.IsEffectivelyVisible.Should().BeTrue($"{control.Name} must be visible to click");
            var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException($"{control.Name} is not in the window");
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            ScreenRenderer.Pump();
        }

        private static List<string> BindingErrors() => sink.Messages.Where(m => m.Contains("Binding", StringComparison.Ordinal) || m.Contains("Property", StringComparison.Ordinal)).ToList();

        [Test]
        public void EveryScreen_RendersInDarkAndNightVision_WithoutBindingErrors() {
            OnUi(() => {
                sink.Messages.Clear();
                var (services, vm, window) = Open();
                try {
                    // Populate the screens: connected, cooling, a focus frame, a finished run, a calibration
                    vm.Connect.StartNightCommand.Execute(null);
                    ((ManualClock)services.Clock).Advance(TimeSpan.FromMinutes(4));
                    services.Tick();
                    vm.Focus.TakeOneCommand.Execute(null);
                    vm.Target.SearchText = "NGC 253";
                    vm.Target.FrameCount = 3;
                    vm.Target.UseForRunCommand.Execute(null);
                    vm.Run.StartCommand.Execute(null);
                    ScreenRenderer.Pump();

                    var dark = ScreenRenderer.RenderAllPages(window, vm, ScreenshotDirectory, "-dark");
                    Theme.Apply(true);
                    var night = ScreenRenderer.RenderAllPages(window, vm, ScreenshotDirectory, "-night");

                    dark.Should().HaveCount(8);
                    foreach (var frame in dark.Concat(night)) {
                        // The page's own view is shown and its area (not just the sidebar and status bar) has content
                        frame.View.Should().Be($"{frame.Page}View", $"{frame.Page} must show its view");
                        frame.Content.LooksRendered.Should().BeTrue($"{frame.Page} page area should not be blank: {frame.Content}");
                        frame.LooksRendered.Should().BeTrue($"{frame.Page} should not be blank: {frame}");
                        frame.Stats.Width.Should().Be(1280);
                    }
                    foreach (var frame in night) {
                        frame.Stats.NonRedSamples.Should().Be(0, $"{frame.Page} in night vision must be red only: {frame.Stats}");
                        frame.Stats.MaxGreenBlueToRed.Should().BeLessThan(0.45, $"{frame.Page} night palette ratio: {frame.Stats}");
                    }
                    // Night vision really is darker and redder than the dark theme
                    night.Average(n => n.Stats.MeanLuma).Should().BeLessThan(dark.Average(d => d.Stats.MeanLuma));
                    BindingErrors().Should().BeEmpty();
                } finally {
                    Theme.Apply(false);
                    window.Close();
                }
            });
        }

        [Test]
        public void RenderAllPages_FlagsAPageWhoseViewIsMissing() {
            OnUi(() => {
                var (_, vm, window) = Open();
                try {
                    // Only the shell renders: the page host shows an empty panel instead of the page's view
                    var host = Find<ContentControl>(window, "PageHost");
                    host.ContentTemplate = new FuncDataTemplate<object>((_, _) => new Panel());
                    var frames = ScreenRenderer.RenderAllPages(window, vm);
                    frames.Should().OnlyContain(f => f.Stats.LooksRendered, "the sidebar and status bar still render");
                    frames.Should().OnlyContain(f => f.View == null && !f.Content.LooksRendered && !f.LooksRendered);
                } finally {
                    window.Close();
                }
            });
        }

        private static WriteableBitmap Solid(byte r, byte g, byte b) {
            var bitmap = new WriteableBitmap(new PixelSize(64, 48), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var fb = bitmap.Lock();
            var row = new byte[fb.RowBytes];
            for (var x = 0; x < 64; x++) {
                row[(x * 4) + 0] = b;
                row[(x * 4) + 1] = g;
                row[(x * 4) + 2] = r;
                row[(x * 4) + 3] = 255;
            }
            for (var y = 0; y < 48; y++) {
                System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + (y * fb.RowBytes), row.Length);
            }
            return bitmap;
        }

        [TestCase(255, 255, 255, true)]   // white
        [TestCase(128, 128, 128, true)]   // grey
        [TestCase(40, 40, 40, true)]      // dark grey
        [TestCase(30, 120, 40, true)]     // green
        [TestCase(30, 40, 160, true)]     // blue
        [TestCase(200, 140, 40, true)]    // amber (the dark theme's warning colour family)
        [TestCase(192, 50, 42, false)]    // night text #C0322A
        [TestCase(255, 90, 78, false)]    // night error #FF5A4E
        [TestCase(224, 69, 58, false)]    // night warning #E0453A
        [TestCase(46, 5, 5, false)]       // night border #2E0505
        [TestCase(0, 0, 0, false)]        // black
        [TestCase(3, 2, 2, false)]        // anti-aliasing noise near black
        public void NightVisionRedCheck_RejectsWhiteGreyGreenAndBlue(int r, int g, int b, bool nonRed) {
            ScreenRenderer.IsNonRed((byte)r, (byte)g, (byte)b).Should().Be(nonRed);
            OnUi(() => {
                using var bitmap = Solid((byte)r, (byte)g, (byte)b);
                var stats = ScreenRenderer.Analyze(bitmap);
                stats.Samples.Should().BeGreaterThan(0);
                (stats.NonRedSamples == stats.Samples).Should().Be(nonRed, stats.ToString());
                stats.NonRedSamples.Should().Be(nonRed ? stats.Samples : 0);
            });
        }

        [Test]
        public void Analyze_Region_SamplesOnlyThatRegion() {
            OnUi(() => {
                using var bitmap = Solid(0, 0, 0);
                using (var fb = bitmap.Lock()) {
                    // A white block in the top-left quarter only
                    var white = Enumerable.Repeat((byte)255, 32 * 4).ToArray();
                    for (var y = 0; y < 24; y++) {
                        System.Runtime.InteropServices.Marshal.Copy(white, 0, fb.Address + (y * fb.RowBytes), white.Length);
                    }
                }
                ScreenRenderer.Analyze(bitmap, new PixelRect(32, 24, 32, 24)).NonRedSamples.Should().Be(0);
                var corner = ScreenRenderer.Analyze(bitmap, new PixelRect(0, 0, 32, 24));
                corner.NonRedSamples.Should().Be(corner.Samples);
                corner.Width.Should().Be(32);
                ScreenRenderer.Analyze(bitmap, new PixelRect(40, 40, 100, 100)).Height.Should().Be(8, "the region is clipped to the bitmap");
            });
        }

        [Test]
        public void ThemeManager_ShadowsTheFluentBrushes() {
            OnUi(() => {
                Theme.ShadowedBrushCount.Should().BeGreaterThan(100);
                ThemeManager.ToNightRed(Color.Parse("#C5CCD5")).G.Should().BeLessThan(40);
                var red = ThemeManager.ToNightRed(Colors.White);
                red.R.Should().Be(255);
                (red.G <= red.R && red.B <= red.R).Should().BeTrue();
                Application.Current.TryGetResource("ButtonBackground", null, out var before).Should().BeTrue();
                var brush = (ISolidColorBrush)before;
                var day = brush.Color;
                Theme.Apply(true);
                brush.Color.G.Should().BeLessThanOrEqualTo(brush.Color.R);
                Theme.Apply(false);
                brush.Color.Should().Be(day);
            });
        }

        [Test]
        public void SidebarClick_Navigates() {
            OnUi(() => {
                var (_, vm, window) = Open();
                try {
                    var nav = Find<ListBox>(window, "Nav");
                    var focusItem = nav.ContainerFromIndex(2) ?? throw new InvalidOperationException("no container");
                    Click(window, focusItem);
                    vm.CurrentPage.Should().BeSameAs(vm.Focus);
                    Find<TextBlock>(window, "PageTitle").Text.Should().Be("Focus");
                    Click(window, Find<ToggleButton>(window, "SettingsButton"));
                    vm.CurrentPage.Should().BeSameAs(vm.Settings);
                    nav.SelectedItem.Should().BeNull();
                } finally {
                    window.Close();
                }
            });
        }

        [Test]
        public void ConnectAllButton_ConnectsAndTheStatusBarShowsTemperature() {
            OnUi(() => {
                var (services, vm, window) = Open();
                try {
                    Click(window, Find<Button>(window, "StartNightButton"));
                    services.Camera.State.Should().Be(DeviceConnectionState.Connected);
                    services.Mount.State.Should().Be(DeviceConnectionState.Connected);
                    Find<TextBlock>(window, "CameraStatus").Text.Should().Contain("°C · cooler");
                    Find<TextBlock>(window, "MountStatus").Text.Should().StartWith("Alt 30.0° Az 180.0°");
                    Find<TextBlock>(window, "KeepAwakeStatus").Text.Should().StartWith("Awake:");
                    Find<TextBlock>(window, "PowerStatus").Text.Should().StartWith("Battery 76%");
                    Find<Button>(window, "DisconnectCameraButton").IsVisible.Should().BeTrue();
                } finally {
                    window.Close();
                }
            });
        }

        [Test]
        public void TypingInTargetSearch_FiltersAndShowsDetails() {
            OnUi(() => {
                var (_, vm, window) = Open();
                try {
                    vm.NavigateTo(PageKind.Target);
                    var search = Find<TextBox>(window, "SearchBox");
                    Click(window, search);
                    window.KeyTextInput("helix");
                    ScreenRenderer.Pump();
                    vm.Target.SearchText.Should().Be("helix");
                    vm.Target.Results.Select(t => t.Name).Should().Equal("NGC 7293");
                    Find<TextBlock>(window, "TargetName").Text.Should().Be("NGC 7293  Helix Nebula");
                    Find<TextBlock>(window, "MaxSub").Text.Should().StartWith("Max sub now:");
                } finally {
                    window.Close();
                }
            });
        }

        [Test]
        public void FocuserNudgeButtons_MoveTheVirtualPosition() {
            OnUi(() => {
                var (_, vm, window) = Open();
                try {
                    vm.Connect.StartNightCommand.Execute(null);
                    vm.NavigateTo(PageKind.Focus);
                    Click(window, Find<Button>(window, "InLarge"));
                    Find<TextBlock>(window, "FocuserPosition").Text.Should().Be("29750 ms");
                    Click(window, Find<Button>(window, "OutSmall"));
                    Find<TextBlock>(window, "FocuserPosition").Text.Should().Be("29800 ms");
                    Click(window, Find<Button>(window, "RecenterButton"));
                    Find<TextBlock>(window, "FocuserPosition").Text.Should().Be("30000 ms");
                } finally {
                    window.Close();
                }
            });
        }

        [Test]
        public void ConnectionLostBanner_AppearsAndReconnectButtonClearsIt() {
            OnUi(() => {
                var (services, vm, window) = Open();
                try {
                    vm.Connect.StartNightCommand.Execute(null);
                    var banner = Find<Border>(window, "Banner");
                    banner.IsVisible.Should().BeFalse();
                    services.SimCamera.SimulateConnectionLoss();
                    ScreenRenderer.Pump();
                    banner.IsVisible.Should().BeTrue();
                    ScreenRenderer.Capture(window, Path.Combine(ScreenshotDirectory, "banner.png")).LooksRendered.Should().BeTrue();
                    Click(window, Find<Button>(window, "ReconnectButton"));
                    services.Camera.State.Should().Be(DeviceConnectionState.Connected);
                    banner.IsVisible.Should().BeFalse();
                } finally {
                    window.Close();
                }
            });
        }

        [Test]
        public void AboutBox_CreditsNinaWithMpl() {
            OnUi(() => {
                var (_, vm, window) = Open();
                try {
                    Click(window, Find<Button>(window, "AboutButton"));
                    var about = window.LastAboutWindow;
                    about.Should().NotBeNull();
                    about.Title.Should().Be("About Nightglass");
                    Find<TextBlock>(about, "BasedOn").Text.Should().StartWith("Based on N.I.N.A.");
                    Find<TextBlock>(about, "License").Text.Should().Contain("Mozilla Public License");
                    ScreenRenderer.Capture(about, Path.Combine(ScreenshotDirectory, "about.png")).LooksRendered.Should().BeTrue();
                    Click(about, Find<Button>(about, "CloseButton"));
                    about.IsVisible.Should().BeFalse();
                } finally {
                    window.Close();
                }
            });
        }

        [Test]
        public void AppMenu_HasAboutSettingsAndNightVision() {
            OnUi(() => {
                var services = CreateServices();
                var vm = new MainWindowViewModel(services, Theme);
                var menu = NINA.Mac.App.App.BuildAppMenu(vm);
                menu.Items.OfType<NativeMenuItemSeparator>().Should().ContainSingle();
                var items = menu.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToArray();
                items.Select(i => i.Header).Should().Equal("About Nightglass", "Settings…", "Night Vision (Red)");
                items[1].Gesture.Should().Be(new KeyGesture(Key.OemComma, KeyModifiers.Meta));
                var raised = 0;
                vm.AboutRequested += (_, _) => raised++;
                items[0].Command.Execute(null);
                raised.Should().Be(1);
                items[2].Command.Execute(null);
                items[2].IsChecked.Should().BeTrue();
                Theme.IsNightVision.Should().BeTrue();
                items[2].Command.Execute(null);
                Theme.IsNightVision.Should().BeFalse();
            });
        }

        [Test]
        public void ViewLocator_CoversEveryPage() {
            OnUi(() => {
                var locator = new ViewLocator();
                var services = CreateServices();
                var vm = new MainWindowViewModel(services, Theme);
                foreach (var page in vm.Pages.Cast<object>().Append(vm.About)) {
                    locator.Match(page).Should().BeTrue(page.GetType().Name);
                    locator.Build(page).Should().NotBeOfType<TextBlock>();
                }
                locator.Match(new object()).Should().BeFalse();
            });
        }
    }
}
