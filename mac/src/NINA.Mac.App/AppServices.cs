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
using NINA.Mac.App.Engine;
using NINA.Mac.App.Services;
using NINA.Mac.App.Services.Simulation;
using NINA.Mac.Platform;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

        /// <summary>Use the simulators whatever the settings say (--gui-smoke: a launch on screen must never open a device).</summary>
        public bool ForceSimulatedDevices { get; init; }

        /// <summary>
        /// Adjusts the engine's options before it is composed (tests: a fake camera list, the LX200 simulator's cable, a fake
        /// solver). Called with the defaults AppServices fills in.
        /// </summary>
        public Action<EngineDevicesOptions> ConfigureEngine { get; init; }
    }

    /// <summary>
    /// Composition root. Owns the platform services and the device services the view-models talk to: the simulators, or with
    /// <see cref="AppSettings.DeviceSource"/> = Real the headless engine (<see cref="EngineDevices"/>: NINA's ZWO camera driver,
    /// the LX200 driver, plate solving, NINA's sequencer). The choice is made once, at start-up. If the engine cannot start
    /// (missing ephemeris or catalogue data, say), the app falls back to the simulators and says why (<see cref="EngineError"/>).
    /// </summary>
    public sealed class AppServices : IDisposable {
        private int ticks;
        private bool devicesShutDown;

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

        /// <summary>The simulators; null with Real devices.</summary>
        public SimulatedCamera SimCamera { get; private set; }
        public SimulatedMount SimMount { get; private set; }
        public SimulatedFocuser SimFocuser { get; private set; }

        /// <summary>The engine with Real devices; null with the simulators.</summary>
        public EngineDevices Engine { get; private set; }

        /// <summary>Why Real devices were asked for but could not start (the simulators run instead); null otherwise.</summary>
        public string EngineError { get; private set; }

        /// <summary>Why the last saved settings did not reach the engine's NINA profile; null when they did (or are waiting for a run to end).</summary>
        public string EngineSettingsError { get; private set; }

        public ICameraService Camera { get; private set; }
        public IMountService Mount { get; private set; }
        public IFocuserService Focuser { get; private set; }
        public ISessionService Session { get; private set; }
        public ICalibrationService Calibration { get; private set; }
        public ICentringService Centring { get; private set; }

        public bool DevicesSimulated => Engine == null;

        /// <summary>The device source in use (the setting may say Real while <see cref="EngineError"/> forced the simulators).</summary>
        public DeviceSource ActiveDeviceSource => Engine == null ? DeviceSource.Simulated : DeviceSource.Real;

        /// <summary>Folders derived from the identity and the current settings (images root override).</summary>
        public UserDataPaths DataPaths => new(Info.Identity, HomeDirectory, Settings.Current.ImagesRoot);

        /// <summary>
        /// Tonight's image folders: the simulators' layout, or with Real devices NINA.Mac.Siril's layout, which NINA's file
        /// patterns write.
        /// </summary>
        public IImageFolders CurrentLayout() => Engine != null
            ? Engine.CurrentFolders()
            : new SessionLayout(DataPaths.ImagesRoot, Clock.Now, Settings.Current.Site.UtcOffsetHours);

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

            if (s.Settings.Current.DeviceSource == DeviceSource.Real && !options.ForceSimulatedDevices) {
                try {
                    s.CreateEngineDevices(options);
                } catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.IOException || ex is UnauthorizedAccessException
                        || ex is ArgumentException || ex is DllNotFoundException || ex is TypeInitializationException) {
                    s.Engine = null;
                    s.EngineError = ex.Message;
                }
            }
            if (s.Engine == null) {
                s.CreateSimulatedDevices(options);
            }
            s.KeepAwake = new KeepAwakeCoordinator(s.KeepAwakeService, s.Camera, s.Mount, s.Session, s.Settings, $"{s.Info.ShortName}: imaging session in progress");
            s.Mount.RefreshPorts();
            s.Power.Refresh();
            return s;
        }

        private void CreateSimulatedDevices(AppServicesOptions options) {
            SimMount = new SimulatedMount(Clock, () => Settings.Current, options.SerialPortLister);
            SimFocuser = new SimulatedFocuser(Clock, SimMount, Math.Clamp(Settings.Current.FocuserSpeed, 1, 4));
            SimCamera = new SimulatedCamera(Clock, () => SimFocuser.FocusErrorMicrons);
            if (options.FastSimulation) {
                SimCamera.ConnectDelay = TimeSpan.Zero;
                SimMount.ConnectDelay = TimeSpan.Zero;
            }
            Camera = SimCamera;
            Mount = SimMount;
            Focuser = SimFocuser;
            Session = new SimulatedSession(Camera, Mount, Clock, () => Settings.Current, () => DataPaths.ImagesRoot);
            Calibration = new SimulatedCalibration(Camera);
            Centring = new SimulatedCentring(Mount);
        }

        private void CreateEngineDevices(AppServicesOptions options) {
            var engineOptions = new EngineDevicesOptions {
                Settings = () => Settings.Current,
                ImagesRoot = () => DataPaths.ImagesRoot,
                DataDirectory = EngineRuntime.DataDirectory == null ? new UserDataPaths(Info.Identity, HomeDirectory).EngineDataDirectory : null,
                Clock = Clock,
                HomeDirectory = HomeDirectory,
                SerialPortLister = options.SerialPortLister,
            };
            options.ConfigureEngine?.Invoke(engineOptions);
            Engine = EngineDevices.Create(engineOptions);
            Camera = Engine.Camera;
            Mount = Engine.Mount;
            Focuser = Engine.Focuser;
            Session = Engine.Session;
            // Flats, darks and biases work on any ICameraService; the engine camera saves the kept frames
            Calibration = new SimulatedCalibration(Camera);
            Centring = Engine.Centring;
            // A crash must not leave the cooler running or a host-timed motion without its halt (host contract: EmergencyStop)
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            // Settings › Save reaches the engine's NINA profile (site, optics, gain, solvers, image folder); during a run, at its end
            Settings.Changed += OnSettingsChanged;
        }

        private void OnSettingsChanged(object sender, EventArgs e) {
            // Never throws: the other Changed subscribers (keep-awake) must still see the save
            EngineSettingsError = null;
            try {
                Engine?.ApplySettings();
            } catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is ArgumentException) {
                NINA.Core.Utility.Logger.Error("Nightglass: applying the saved settings to the engine failed", ex);
                EngineSettingsError = ex.Message;
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) {
            if (!devicesShutDown) {
                Engine?.EmergencyStop("Nightglass crashed");
            }
        }

        /// <summary>The app calls this once a second: refresh device readouts; poll the power source every 30 s.</summary>
        public void Tick() {
            SimCamera?.Tick();
            SimMount?.Tick();
            (Engine?.Camera as IPolledService)?.Tick();
            (Engine?.Mount as IPolledService)?.Tick();
            if (++ticks % 30 == 0) {
                Power.Refresh();
            }
        }

        /// <summary>
        /// End of the app with Real devices: stop the run, cooler off, disconnect camera, focuser and mount, save the profile.
        /// Call it before <see cref="Dispose"/> (the app does, from ShutdownRequested); does nothing with the simulators.
        /// </summary>
        public async Task ShutdownDevicesAsync() {
            if (Engine == null || devicesShutDown) {
                return;
            }
            devicesShutDown = true;
            await Engine.ShutdownAsync();
        }

        public void Dispose() {
            KeepAwake?.Dispose();
            KeepAwakeService?.Dispose();
            if (Engine != null) {
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                Settings.Changed -= OnSettingsChanged;
                if (!devicesShutDown) {
                    // Last resort when the orderly shutdown did not run: halt the mount's motions and switch the cooler off
                    Engine.EmergencyStop("Nightglass is quitting");
                }
                Engine.Dispose();
            }
        }

        private sealed class UnavailablePowerSource : IPowerSource {
            public PowerSourceInfo Read() => PowerSourceInfo.Unavailable;
        }
    }
}
