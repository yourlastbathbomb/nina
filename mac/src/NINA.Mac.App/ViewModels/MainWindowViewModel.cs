#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Mac.App.ViewModels {

    /// <summary>Shell: navigation through the night's screens, status bar, connection-lost banner, night vision.</summary>
    public sealed partial class MainWindowViewModel : ViewModelBase, INavigator {
        private readonly AppServices services;
        private readonly IThemeController theme;

        public MainWindowViewModel(AppServices services, IThemeController theme) {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            this.theme = theme;
            StatusBar = new StatusBarViewModel(services);
            Banner = new ConnectionBannerViewModel(services);
            About = new AboutViewModel(services.Info);
            Connect = new ConnectViewModel(services);
            Cool = new CoolViewModel(services);
            Focus = new FocusViewModel(services);
            Calibrate = new CalibrateViewModel(services);
            // A run started during a calibration can cancel it (the camera is needed for the lights)
            Run = new RunViewModel(services, () => Calibrate.CancelCommand.Execute(null));
            Target = new TargetViewModel(services, this, Run);
            Teardown = new TeardownViewModel(services);
            Settings = new SettingsViewModel(services, theme, About);
            Pages = new PageViewModel[] { Connect, Cool, Focus, Target, Run, Calibrate, Teardown, Settings };
            NightPages = Pages.Where(p => p.Kind != PageKind.Settings).ToArray();
            CurrentPage = Connect;
            IsNightVision = services.Settings.Current.NightVision;
        }

        public string Title => services.Info.DisplayName;

        public StatusBarViewModel StatusBar { get; }

        public ConnectionBannerViewModel Banner { get; }

        public AboutViewModel About { get; }

        public ConnectViewModel Connect { get; }

        public CoolViewModel Cool { get; }

        public FocusViewModel Focus { get; }

        public TargetViewModel Target { get; }

        public RunViewModel Run { get; }

        public CalibrateViewModel Calibrate { get; }

        public TeardownViewModel Teardown { get; }

        public SettingsViewModel Settings { get; }

        public IReadOnlyList<PageViewModel> Pages { get; }

        /// <summary>Steps 1-7 for the sidebar; Settings sits apart at the bottom.</summary>
        public IReadOnlyList<PageViewModel> NightPages { get; }

        [ObservableProperty]
        public partial PageViewModel CurrentPage { get; set; }

        [ObservableProperty]
        public partial bool IsNightVision { get; set; }

        /// <summary>Raised when the About box should open (the view owns windows).</summary>
        public event EventHandler AboutRequested;

        public bool IsSettingsSelected => CurrentPage?.Kind == PageKind.Settings;

        public void NavigateTo(PageKind page) => CurrentPage = Pages.First(p => p.Kind == page);

        partial void OnCurrentPageChanged(PageViewModel value) {
            value?.Refresh();
            OnPropertyChanged(nameof(IsSettingsSelected));
            OnPropertyChanged(nameof(SidebarSelection));
        }

        /// <summary>Sidebar list selection: the current page unless it is Settings.</summary>
        public PageViewModel SidebarSelection {
            get => CurrentPage?.Kind == PageKind.Settings ? null : CurrentPage;
            set {
                if (value != null) {
                    CurrentPage = value;
                }
            }
        }

        [RelayCommand]
        private void Navigate(PageKind page) => NavigateTo(page);

        [RelayCommand]
        private void OpenSettings() => NavigateTo(PageKind.Settings);

        [RelayCommand]
        private void ToggleNightVision() {
            IsNightVision = !IsNightVision;
            var s = services.Settings.Current.Clone();
            s.NightVision = IsNightVision;
            services.Settings.Save(s);
            theme?.Apply(IsNightVision);
        }

        [RelayCommand]
        private void ShowAbout() => AboutRequested?.Invoke(this, EventArgs.Empty);
    }
}
