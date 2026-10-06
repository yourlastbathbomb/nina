#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry.Interfaces;
using NINA.Astrometry.Mac;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Mac;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Mac.App.Services;
using NINA.Mac.Equipment.Lx200;
using NINA.Mac.Platform;
using NINA.Mac.Sequencing.Headless;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine {

    /// <summary>What <see cref="EngineDevices.Create"/> needs from the app, and the seams tests use instead of hardware.</summary>
    public sealed class EngineDevicesOptions {

        /// <summary>The app's current settings (site, optics, gain, solver paths). Required.</summary>
        public Func<AppSettings> Settings { get; set; }

        /// <summary>Root of the image folders (NINA's ImageFileSettings.FilePath). Required.</summary>
        public Func<string> ImagesRoot { get; set; }

        /// <summary>NINA's data folder (CoreUtil.APPLICATIONTEMPPATH). Required unless <see cref="EngineRuntime"/> is already initialised.</summary>
        public string DataDirectory { get; set; }

        public IClock Clock { get; set; }

        /// <summary>Home folder for "~/" in the solver paths; null = the user's.</summary>
        public string HomeDirectory { get; set; }

        /// <summary>Where Changed events are raised; null = the creating thread's context (the UI thread in the app), inline when it has none.</summary>
        public SynchronizationContext UiContext { get; set; }

        /// <summary>The camera list. Default: NINA.Equipment.Mac's CameraChooser (the ZWO ASI cameras), behind the gate.</summary>
        public Func<IProfileService, IExposureDataFactory, IDeviceChooserVM> CameraChooser { get; set; }

        /// <summary>Serial links for the LX200 driver. Default: <see cref="Lx200LinkPool.Shared"/> (System.IO.Ports on /dev/cu.*).</summary>
        public Lx200LinkPool LinkPool { get; set; }

        public Lx200Clock Lx200Clock { get; set; }

        /// <summary>Solvers for centring and the sequence's Center. Default: NINA's PlateSolverFactoryProxy (the profile's ASTAP and solve-field).</summary>
        public IPlateSolverFactory PlateSolverFactory { get; set; }

        /// <summary>Dusk and dawn for the sequence. Default: NINA's NighttimeCalculator for the profile's site.</summary>
        public INighttimeCalculator NighttimeCalculator { get; set; }

        /// <summary>Serial port list for the port picker. Default: /dev/cu.* (NINA.Mac.Platform).</summary>
        public Func<IReadOnlyList<SerialPortInfo>> SerialPortLister { get; set; }

        /// <summary>Check the ephemeris and catalogue data (EngineData.EnsureAvailable) before anything else.</summary>
        public bool CheckEngineData { get; set; } = true;

        public StuckSensorGuardOptions StuckSensor { get; set; }

        /// <summary>Temperature readings this soon after a connect are ignored (the SDK reports 0.0 °C before its first poll, M1 finding 2).</summary>
        public TimeSpan FirstReadingDelay { get; set; } = TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// The Real device services over one headless engine (NINA.Mac.Sequencing's <see cref="HeadlessHost"/>): NINA's CameraVM over
    /// the gated ZWO camera list, TelescopeVM over the LX200 driver, DirectGuider for mount dithers, NINA's imaging and image saving,
    /// the generated sequence and its runner. Composing it opens nothing: the serial port is opened by the mount's Connect and
    /// the USB camera list is scanned only by the camera's Connect.
    /// </summary>
    public sealed class EngineDevices : IDisposable {
        private readonly object settingsLock = new();
        private bool disposed;
        private bool settingsPending;
        private int appliedFocuserSpeed;

        private EngineDevices() {
        }

        public EngineDevicesOptions Options { get; private set; }

        public EngineProfileService Profile { get; private set; }

        public HeadlessHost Host { get; private set; }

        public GatedDeviceChooser CameraGate { get; private set; }

        public Lx200EquipmentProvider Lx200 { get; private set; }

        public SolverSetup Solvers { get; private set; }

        /// <summary>Settings were saved during a run; they reach the profile when it ends (<see cref="ApplySettings"/>).</summary>
        public bool SettingsPending {
            get {
                lock (settingsLock) {
                    return settingsPending;
                }
            }
        }

        public IClock Clock { get; private set; }

        internal UiPoster Ui { get; private set; }

        public EngineCameraService Camera { get; private set; }

        public EngineMountService Mount { get; private set; }

        public EngineFocuserService Focuser { get; private set; }

        public EngineSessionService Session { get; private set; }

        public EngineCentringService Centring { get; private set; }

        internal AppSettings Settings => Options.Settings();

        internal string ImagesRoot => Options.ImagesRoot();

        /// <summary>The image folders of the night that includes now (NINA.Mac.Siril's layout, as NINA's file patterns write it).</summary>
        public IImageFolders CurrentFolders() => new SirilFolders(ImagesRoot, Clock.Now);

        public static EngineDevices Create(EngineDevicesOptions options) {
            ArgumentNullException.ThrowIfNull(options);
            if (options.Settings == null || options.ImagesRoot == null) {
                throw new ArgumentException("Settings and ImagesRoot are required", nameof(options));
            }
            if (options.DataDirectory != null) {
                EngineRuntime.Initialize(options.DataDirectory);
            } else if (EngineRuntime.DataDirectory == null) {
                throw new ArgumentException("DataDirectory is required for the first engine of the process", nameof(options));
            }
            if (options.CheckEngineData) {
                EngineData.EnsureAvailable();
            }

            var e = new EngineDevices {
                Options = options,
                Clock = options.Clock ?? SystemClock.Instance,
                Ui = new UiPoster(options.UiContext ?? SynchronizationContext.Current),
            };
            e.Profile = new EngineProfileService();
            try {
                var settings = options.Settings();
                e.Solvers = RigProfile.Apply(e.Profile, settings, options.ImagesRoot(), options.HomeDirectory);
                e.appliedFocuserSpeed = Math.Clamp(settings.FocuserSpeed, 1, 4);
                e.Profile.Save();

                var detection = EngineBehaviors.StarDetectionSelector();
                var annotation = EngineBehaviors.StarAnnotatorSelector();
                var exposureDataFactory = new ExposureDataFactory(new ImageDataFactory(e.Profile, detection, annotation), e.Profile, detection, annotation);
                var cameras = options.CameraChooser?.Invoke(e.Profile, exposureDataFactory) ?? new CameraChooser(e.Profile, exposureDataFactory);
                e.CameraGate = new GatedDeviceChooser(cameras);
                e.Lx200 = new Lx200EquipmentProvider(e.Profile, options.LinkPool ?? Lx200LinkPool.Shared, options.Lx200Clock);

                e.Host = new HeadlessHost(new HeadlessHostOptions {
                    ProfileService = e.Profile,
                    CameraChooser = e.CameraGate,
                    TelescopeChooser = new TelescopeChooser(e.Profile, new IEquipmentProvider<ITelescope>[] { e.Lx200 }),
                    NighttimeCalculator = options.NighttimeCalculator,
                    PlateSolverFactory = options.PlateSolverFactory,
                });

                e.Camera = new EngineCameraService(e, options.StuckSensor, options.FirstReadingDelay);
                e.Mount = new EngineMountService(e, options.SerialPortLister);
                e.Focuser = new EngineFocuserService(e);
                e.Session = new EngineSessionService(e);
                e.Centring = new EngineCentringService(e);
            } catch {
                e.Profile.Release();
                throw;
            }
            Logger.Info($"Nightglass engine composed: profile {EngineProfileService.ProfilePath}, ASTAP {(e.Solvers.Problem ?? e.Solvers.AstapLocation)}, solve-field in {e.Solvers.AstrometryBinDirectory}");
            return e;
        }

        /// <summary>
        /// Copies the app's current settings into the rig profile again (Settings › Save): site, optics (the focal length is
        /// CenteringSolver's scale hint), camera defaults, NINA's image folder, the solvers. A run keeps the settings it started
        /// with, so while one is active the change waits for its end; returns false then (<see cref="SettingsPending"/>).
        /// The Focus screen's focuser speed is kept unless the Settings default itself changed.
        /// </summary>
        public bool ApplySettings() {
            lock (settingsLock) {
                if (Session.IsActive) {
                    settingsPending = true;
                    Logger.Info("Nightglass engine: settings changed during a run; they apply when it ends");
                    return false;
                }
                ApplySettingsLocked();
                return true;
            }
        }

        /// <summary>Applies settings saved during the run that just ended.</summary>
        internal void ApplyPendingSettings() {
            lock (settingsLock) {
                if (settingsPending && !Session.IsActive) {
                    ApplySettingsLocked();
                }
            }
        }

        private void ApplySettingsLocked() {
            settingsPending = false;
            var settings = Settings;
            var lx = Lx200.Focuser.Settings;
            var speedBefore = lx.FocuserSpeed;
            Solvers = RigProfile.Apply(Profile, settings, ImagesRoot, Options.HomeDirectory);
            var defaultSpeed = Math.Clamp(settings.FocuserSpeed, 1, 4);
            if (defaultSpeed == appliedFocuserSpeed) {
                // RigProfile wrote the unchanged default: keep the speed chosen on the Focus screen
                lx.FocuserSpeed = speedBefore;
            } else if (speedBefore != defaultSpeed) {
                // Positions only compare at one speed
                Lx200.Focuser.RecenterPosition();
            }
            appliedFocuserSpeed = defaultSpeed;
            Profile.Save();
            Logger.Info($"Nightglass engine: settings applied to the rig profile (focal length {Profile.ActiveProfile.TelescopeSettings.FocalLength} mm, images in {ImagesRoot})");
            Mount?.RaiseChanged();
            Focuser?.RaiseChanged();
        }

        /// <summary>
        /// End of the app: stops a run, switches the cooler off and disconnects every device (the camera's cooler first, as the
        /// host contract asks; the LX200 link sends any owed halts before it closes), then saves the profile.
        /// </summary>
        public async Task ShutdownAsync() {
            try {
                Session.RequestStop("App quit");
                await Session.WaitForIdleAsync(TimeSpan.FromSeconds(30));
            } catch (Exception ex) {
                Logger.Error("Nightglass engine: stopping the run at quit failed", ex);
            }
            try {
                await Camera.DisconnectAsync();
            } catch (Exception ex) {
                Logger.Error("Nightglass engine: camera disconnect at quit failed", ex);
            }
            try {
                await Mount.DisconnectAsync();
            } catch (Exception ex) {
                Logger.Error("Nightglass engine: mount disconnect at quit failed", ex);
            }
            try {
                Profile.Save();
            } catch (Exception ex) {
                Logger.Error("Nightglass engine: saving the profile at quit failed", ex);
            }
        }

        /// <summary>
        /// Abnormal exit (the orderly <see cref="ShutdownAsync"/> did not run): switch the cooler off and send the mount's halts
        /// (Lx200Link.EmergencyStop, host contract). Synchronous and best effort.
        /// </summary>
        public void EmergencyStop(string reason) {
            try {
                var camera = Host?.Camera;
                if (camera != null && camera.CameraInfo.Connected && camera.CameraInfo.CanSetTemperature) {
                    camera.SetCooler(false);
                }
            } catch (Exception ex) {
                Logger.Error("Nightglass engine: emergency cooler off failed", ex);
            }
            try {
                Lx200?.Telescope.Link?.EmergencyStop(reason);
            } catch (Exception ex) {
                Logger.Error("Nightglass engine: emergency stop of the mount failed", ex);
            }
        }

        public void Dispose() {
            if (disposed) {
                return;
            }
            disposed = true;
            Session?.Dispose();
            Camera?.Dispose();
            Mount?.Dispose();
            Host?.Dispose();
            Profile?.Release();
        }
    }
}
