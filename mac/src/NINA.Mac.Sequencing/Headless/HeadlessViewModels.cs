#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using CommunityToolkit.Mvvm.Input;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace NINA.Mac.Sequencing.Headless {

    /// <summary>
    /// NINA's status bar without a window: the handler of the ApplicationStatusMediator. Every status update that NINA's view
    /// models and sequence items report (camera, mount, imaging, saving, plate solving) is raised as <see cref="StatusUpdated"/>,
    /// for a UI or a log.
    /// </summary>
    public class HeadlessApplicationStatus : IApplicationStatusVM {

        public event EventHandler<ApplicationStatus> StatusUpdated;

        public void StatusUpdate(ApplicationStatus status) {
            if (status != null) {
                StatusUpdated?.Invoke(this, status);
            }
        }
    }

    /// <summary>
    /// The main window's view model, which the headless engine does not have. NINA.Sequencer only calls ChangeTab (from the
    /// deep-sky container's "show in framing assistant" command); it is ignored. Commands are null.
    /// </summary>
    public class HeadlessApplication : IApplicationVM {
        public IRelayCommand CheckASCOMPlatformVersionCommand => null;
        public IRelayCommand ClosingCommand => null;
        public IRelayCommand ExitCommand => null;
        public IRelayCommand MaximizeWindowCommand => null;
        public IRelayCommand MinimizeWindowCommand => null;
        public IRelayCommand OpenManualCommand => null;
        public int TabIndex { get; set; }
        public string Title => "Nightglass";
        public string Version => CoreUtil.Version;

        public void ChangeTab(ApplicationTab tab) {
        }

        public Task LoadImagingLayout(string filePath, CancellationToken token) {
            return Task.CompletedTask;
        }
    }

    /// <summary>The framing assistant, which the headless engine does not have: DeepSkyObjectContainer's "open in framing" command reports false.</summary>
    public class NullFramingAssistant : IFramingAssistantVM {

        public Task<bool> SetCoordinates(DeepSkyObject dso) {
            return Task.FromResult(false);
        }
    }

    /// <summary>No planetarium program on this rig (Stellarium, Cartes du Ciel and friends are not compiled): GetPlanetarium returns null.</summary>
    public class NullPlanetariumFactory : IPlanetariumFactory {

        public IPlanetarium GetPlanetarium() {
            return null;
        }

        public IPlanetarium GetPlanetarium(PlanetariumEnum planetarium) {
            return null;
        }
    }

    /// <summary>
    /// The dome follower for a rig without a dome. Never following, always synchronized; Center only asks it to sync when a dome
    /// is connected, which never happens here, and a request succeeds at once.
    /// </summary>
    public class NullDomeFollower : IDomeFollower {

        public event PropertyChangedEventHandler PropertyChanged {
            add { }
            remove { }
        }

        public bool IsSynchronized => true;

        public bool IsFollowing => false;

        public Task Stop() {
            return Task.CompletedTask;
        }

        public Task Start() {
            return Task.CompletedTask;
        }

        public Task<bool> TriggerTelescopeSync() {
            return Task.FromResult(true);
        }

        public Task WaitForDomeSynchronization(CancellationToken cancellationToken) {
            return Task.CompletedTask;
        }

        public TopocentricCoordinates GetSynchronizedDomeCoordinates(TelescopeInfo telescopeInfo) {
            return null;
        }

        public bool IsDomeWithinTolerance(Angle currentDomeAzimuth, TopocentricCoordinates targetDomeCoordinates) {
            return true;
        }

        public Task<bool> SyncToScopeCoordinates(Coordinates coordinates, PierSide sideOfPier, CancellationToken cancellationToken) {
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Window service for the headless engine. Sequence items open non-modal status windows for information only (Center and
    /// SolveAndSync show their plate-solving progress through Show); with no window system those calls do nothing, and the
    /// progress still reaches the status handler. NINA.Core.Mac's own WindowService throws in Show, which would fail every
    /// centring, so the headless host passes this factory to the items (and to CenterAfterDriftTrigger's centring run).
    /// ShowDialog asks for an answer that only a UI can give, so it still throws.
    /// </summary>
    public class HeadlessWindowServiceFactory : IWindowServiceFactory {

        public IWindowService Create() {
            return new HeadlessWindowService();
        }

        private sealed class HeadlessWindowService : IWindowService {

            public void Show(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None) {
                Logger.Debug($"Headless: window \"{title}\" not shown");
            }

            public IDispatcherOperationWrapper ShowDialog(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None, ICommand closeCommand = null) {
                throw new PlatformNotSupportedException($"The headless sequencer cannot show the dialog \"{title}\".");
            }

            public event EventHandler OnDialogResultChanged {
                add { }
                remove { }
            }

            public event EventHandler OnClosed {
                add { }
                remove { }
            }

            public void DelayedClose(TimeSpan t) {
            }

            public Task Close() {
                return Task.CompletedTask;
            }
        }
    }
}
