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
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NINA.Mac.App.Diagnostics;
using NINA.Mac.App.Theming;
using NINA.Mac.App.ViewModels;
using NINA.Mac.App.Views;
using System;

namespace NINA.Mac.App {

    public partial class App : Application {
        private DispatcherTimer ticker;

        public AppServices Services { get; private set; }

        public ThemeManager Theme { get; private set; }

        public override void Initialize() {
            AvaloniaXamlLoader.Load(this);
            Name = AppInfo.Current.ShortName;
            Theme = new ThemeManager(this);
        }

        public override void OnFrameworkInitializationCompleted() {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
                Services = AppServices.Create();
                Theme.Apply(Services.Settings.Current.NightVision);
                var viewModel = new MainWindowViewModel(Services, Theme);
                desktop.MainWindow = new MainWindow { DataContext = viewModel };
                GuiSmoke.Active?.Attach(desktop, viewModel); // --gui-smoke only: watch the real window, then quit
                desktop.Exit += (_, _) => Shutdown();
                NativeMenu.SetMenu(this, BuildAppMenu(viewModel));
                ticker = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Services.Tick());
                ticker.Start();
            }
            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>Items for the macOS application menu (About, Settings, Night vision).</summary>
        internal static NativeMenu BuildAppMenu(MainWindowViewModel viewModel) {
            var about = new NativeMenuItem($"About {viewModel.About.Info.ShortName}") { Command = viewModel.ShowAboutCommand };
            var settings = new NativeMenuItem("Settings…") { Command = viewModel.OpenSettingsCommand, Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta) };
            var night = new NativeMenuItem("Night Vision (Red)") {
                Command = viewModel.ToggleNightVisionCommand,
                Gesture = new KeyGesture(Key.R, KeyModifiers.Meta | KeyModifiers.Shift),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = viewModel.IsNightVision,
            };
            viewModel.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(MainWindowViewModel.IsNightVision)) {
                    night.IsChecked = viewModel.IsNightVision;
                }
            };
            var menu = new NativeMenu();
            menu.Add(about);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(settings);
            menu.Add(night);
            return menu;
        }

        private void Shutdown() {
            ticker?.Stop();
            Services?.Dispose();
        }
    }
}
