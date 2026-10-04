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
using NINA.Mac.App.Services;
using System;

namespace NINA.Mac.App.ViewModels {

    public abstract class ViewModelBase : ObservableObject {
    }

    /// <summary>The eight screens of the night, in order.</summary>
    public enum PageKind {
        Connect,
        Cool,
        Focus,
        Target,
        Run,
        Calibrate,
        Teardown,
        Settings,
    }

    public interface INavigator {

        void NavigateTo(PageKind page);
    }

    /// <summary>Night-vision theme switch, implemented by the app's theme manager.</summary>
    public interface IThemeController {

        bool IsNightVision { get; }

        void Apply(bool nightVision);
    }

    public abstract partial class PageViewModel : ViewModelBase {

        protected PageViewModel(PageKind kind, string title, string subtitle) {
            Kind = kind;
            Title = title;
            Subtitle = subtitle;
        }

        public PageKind Kind { get; }

        /// <summary>1-based step number in the night; Settings has none.</summary>
        public string Step => Kind == PageKind.Settings ? "" : ((int)Kind + 1).ToString();

        public string Title { get; }

        public string Subtitle { get; }

        /// <summary>Last error shown at the top of the page.</summary>
        [ObservableProperty]
        public partial string ErrorMessage { get; set; }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

        /// <summary>Re-reads service state; called on service events and when the page is shown.</summary>
        public virtual void Refresh() {
        }

        protected void ShowError(Exception ex) => ErrorMessage = ex is OperationCanceledException ? null : ex.Message;
    }

    /// <summary>One device line: name, state dot, detail text.</summary>
    public sealed partial class DeviceStatusViewModel : ViewModelBase {

        public DeviceStatusViewModel(string name) {
            Name = name;
        }

        public string Name { get; }

        [ObservableProperty]
        public partial DeviceConnectionState State { get; set; }

        [ObservableProperty]
        public partial string Detail { get; set; }

        public bool IsConnected => State == DeviceConnectionState.Connected;

        public bool IsConnecting => State == DeviceConnectionState.Connecting;

        public bool IsLost => State == DeviceConnectionState.Lost;

        public bool IsDisconnected => State == DeviceConnectionState.Disconnected;

        public string StateText => State switch {
            DeviceConnectionState.Connected => "Connected",
            DeviceConnectionState.Connecting => "Connecting…",
            DeviceConnectionState.Lost => "Connection lost",
            _ => "Disconnected",
        };

        partial void OnStateChanged(DeviceConnectionState value) {
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsConnecting));
            OnPropertyChanged(nameof(IsLost));
            OnPropertyChanged(nameof(IsDisconnected));
            OnPropertyChanged(nameof(StateText));
        }
    }
}
