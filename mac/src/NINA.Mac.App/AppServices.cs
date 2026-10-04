#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.App.Astro;
using NINA.Mac.App.Services;
using NINA.Mac.App.Services.Simulation;
using NINA.Mac.Platform;
using System;
using System.Collections.Generic;

namespace NINA.Mac.App {

    /// <summary>Knobs for <see cref="AppServices.Create"/>; every null means "the real macOS thing".</summary>
    public sealed class AppServicesOptions {
        public AppInfo Info { get; init; }
        public IClock Clock { get; init; }
        public ISettingsStore Settings { get; init; }
        public IKeepAwake KeepAwake { get; init; }
        public IPowerSource PowerSource { get; init; }
        public string HomeDirectory { get; init; }
        public ResourcePaths Paths { get; init; }
        public Func<IReadOnlyList<SerialPortInfo>> SerialPortLister { get; init; }

        /// <summary>Zero connect/slew delays (tests, smoke test).</summary>
        public bool FastSimulation { get; init; }
    }

    /// <summary>
    /// Composition root. Owns the platform services and the device services the view-models talk to. Devices are
    /// simulated until the headless engine exists; swapping them for engine-backed implementations is the M8 job.
    /// </summary>
    public sealed class AppServices : IDisposable {
        private int ticks;

        private AppServices() {
        }

        public AppInfo Info { get; private set; }
        public IClock Clock { get; private set; }
        public ResourcePaths Paths { get; private set; }
        public ISettingsStore Settings { get; private set; }
        public string HomeDirectory { get; private set; }
        public ITargetCatalog Catalog { get; private set; }
        public PowerMonitor Power { get; private set; }
        public IKeepAwake KeepAwakeService { get; private set; }
        public KeepAwakeCoordinator KeepAwake { get; private set; }

        public SimulatedCamera SimCamera { get; private set; }
        public SimulatedMount SimMount { get; private set; }
        public SimulatedFocuser SimFocuser { get; private set; }

        public ICameraService Camera => SimCamera;
        public IMountService Mount => SimMount;
        public IFocuserService Focuser => SimFocuser;
        public ISessionService Session { get; private set; }
        public ICalibrationService Calibration { get; private set; }

        public bool DevicesSimulated => true;

        /// <summary>Folders derived from the identity and the current settings (images root override).</summary>
        public UserDataPaths DataPaths => new(Info.Identity, HomeDirectory, Settings.Current.ImagesRoot);

        public SessionLayout CurrentLayout() => new(DataPaths.ImagesRoot, Clock.Now, Settings.Current.Site.UtcOffsetHours);

        public GeoSite Site => new(Settings.Current.Site.LatitudeDegrees, Settings.Current.Site.LongitudeDegrees);

        public static AppServices Create(AppServicesOptions options = null) {
            options ??= new AppServicesOptions();
            var s = new AppServices {
                Info = options.Info ?? AppInfo.Current,
                Clock = options.Clock ?? SystemClock.Instance,
                Paths = options.Paths ?? ResourcePaths.Current,
                HomeDirectory = options.HomeDirectory,
                Catalog = new BuiltInTargetCatalog(),
            };
            s.Settings = options.Settings ?? new JsonSettingsStore(new UserDataPaths(s.Info.Identity, s.HomeDirectory).SettingsFile);
            s.Power = new PowerMonitor(options.PowerSource ?? (OperatingSystem.IsMacOS() ? new MacPowerSource() : new UnavailablePowerSource()));
            s.KeepAwakeService = options.KeepAwake ?? (OperatingSystem.IsMacOS() ? new MacKeepAwake() : new NullKeepAwake());

            s.SimMount = new SimulatedMount(s.Clock, () => s.Settings.Current, options.SerialPortLister);
            s.SimFocuser = new SimulatedFocuser(s.Clock, s.SimMount, Math.Clamp(s.Settings.Current.FocuserSpeed, 1, 4));
            s.SimCamera = new SimulatedCamera(s.Clock, () => s.SimFocuser.FocusErrorMicrons);
            if (options.FastSimulation) {
                s.SimCamera.ConnectDelay = TimeSpan.Zero;
                s.SimMount.ConnectDelay = TimeSpan.Zero;
            }
            s.Session = new SimulatedSession(s.Camera, s.Mount, s.Clock, () => s.Settings.Current, () => s.DataPaths.ImagesRoot);
            s.Calibration = new SimulatedCalibration(s.Camera);
            s.KeepAwake = new KeepAwakeCoordinator(s.KeepAwakeService, s.Camera, s.Mount, s.Session, s.Settings, $"{s.Info.ShortName}: imaging session in progress");
            s.SimMount.RefreshPorts();
            s.Power.Refresh();
            return s;
        }

        /// <summary>The app calls this once a second: refresh device readouts; poll the power source every 30 s.</summary>
        public void Tick() {
            SimCamera.Tick();
            SimMount.Tick();
            if (++ticks % 30 == 0) {
                Power.Refresh();
            }
        }

        public void Dispose() {
            KeepAwake?.Dispose();
            KeepAwakeService?.Dispose();
        }

        private sealed class UnavailablePowerSource : IPowerSource {
            public PowerSourceInfo Read() => PowerSourceInfo.Unavailable;
        }
    }
}
