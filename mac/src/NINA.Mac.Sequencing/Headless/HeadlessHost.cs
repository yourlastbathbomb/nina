#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MySwitch;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Mac;
using NINA.Mac.Sequencing.Catalogue;
using NINA.Mac.Sequencing.FieldRotation;
using NINA.Mac.Sequencing.Planning;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Logic;
using NINA.Sequencer.Mediator;
using NINA.Sequencer.Serialization;
using NINA.ViewModel;
using NINA.WPF.Base.Mediator;
using NINA.WPF.Base.ViewModel.Equipment.Camera;
using NINA.WPF.Base.ViewModel.Equipment.Guider;
using NINA.WPF.Base.ViewModel.Equipment.Telescope;
using System;
using System.Threading.Tasks;

namespace NINA.Mac.Sequencing.Headless {

    /// <summary>What the host plugs in. Only the profile and the camera and mount choosers are required.</summary>
    public sealed class HeadlessHostOptions {

        public IProfileService ProfileService { get; init; }

        /// <summary>The camera list, e.g. NINA.Equipment.Mac's CameraChooser (ZWO cameras plus provider cameras).</summary>
        public IDeviceChooserVM CameraChooser { get; init; }

        /// <summary>The mount list, e.g. NINA.Equipment.Mac's TelescopeChooser with the LX200 driver's equipment provider.</summary>
        public IDeviceChooserVM TelescopeChooser { get; init; }

        /// <summary>
        /// The guider list, built from the host's telescope mediator. Default: NINA.Equipment.Mac's GuiderChooser, which lists
        /// DummyGuider and DirectGuider ("Mount Dither": dithers by pulse-guiding the mount); the profile should name Direct_Guider.
        /// </summary>
        public Func<ITelescopeMediator, IDeviceChooserVM> GuiderChooser { get; init; }

        /// <summary>Dusk and dawn for the date-time providers. Default: NINA's NighttimeCalculator for the profile's site.</summary>
        public INighttimeCalculator NighttimeCalculator { get; init; }

        /// <summary>Solvers for Center, SolveAndSync and drift centring. Default: NINA's PlateSolverFactoryProxy (the profile's solvers).</summary>
        public IPlateSolverFactory PlateSolverFactory { get; init; }
    }

    /// <summary>
    /// The composition root of the headless engine: everything NINA's sequence entities need at run time, wired the way the NINA
    /// app's IoC bindings and PluginLoader wire it on Windows (mac/docs/m7-headless-sequencer-plan.md section 4):
    /// <list type="bullet">
    /// <item>NINA.WPF.Base's 15 mediators, every one with a registered handler before any entity is built (plan section 4.2): NINA's
    /// own CameraVM, TelescopeVM and GuiderVM for the camera, mount and guider, a <see cref="DisconnectedDevice"/> handler for
    /// each device the rig does not have (the focuser too, until the M8 app adds NINA's FocuserVM);</item>
    /// <item>imaging and saving through NINA's own ImagingVM and ImageSaveController (upstream files linked unchanged), with the
    /// headless image panel, statistics, history and status of this assembly;</item>
    /// <item>NINA's SymbolBroker, the field-rotation symbols, the NighttimeCalculator, the rig's entity catalogue, the headless
    /// sequencer factory and NINA's SequenceJsonConverter over it, the Target form generator and validator.</item>
    /// </list>
    /// As NINA requires, a <see cref="System.Windows.Application"/> must exist (TelescopeVM reads Application.Current.Resources in
    /// its constructor); the host creates one if the process has none (host contract item 2 in mac/src/README-engine.md).
    /// </summary>
    public sealed class HeadlessHost : IDisposable {
        private bool disposed;

        public HeadlessHost(HeadlessHostOptions options) {
            ArgumentNullException.ThrowIfNull(options);
            ProfileService = options.ProfileService ?? throw new ArgumentException("A profile service is required", nameof(options));
            if (options.CameraChooser == null || options.TelescopeChooser == null) {
                throw new ArgumentException("Camera and telescope choosers are required", nameof(options));
            }
            if (System.Windows.Application.Current == null) {
                _ = new System.Windows.Application();
            }

            // Status and application handlers first: device view models report status as soon as they exist
            ApplicationStatus = new HeadlessApplicationStatus();
            ApplicationStatusMediator.RegisterHandler(ApplicationStatus);
            ApplicationMediator.RegisterHandler(new HeadlessApplication());

            // Devices this rig does not have
            FilterWheelMediator.RegisterHandler(DisconnectedDevice.Create<IFilterWheelVM, FilterWheelInfo>());
            FocuserMediator.RegisterHandler(DisconnectedDevice.Create<IFocuserVM, FocuserInfo>());
            RotatorMediator.RegisterHandler(DisconnectedDevice.Create<IRotatorVM, RotatorInfo>());
            DomeMediator.RegisterHandler(DisconnectedDevice.Create<IDomeVM, DomeInfo>());
            FlatDeviceMediator.RegisterHandler(DisconnectedDevice.Create<IFlatDeviceVM, FlatDeviceInfo>());
            SafetyMonitorMediator.RegisterHandler(DisconnectedDevice.Create<ISafetyMonitorVM, SafetyMonitorInfo>());
            SwitchMediator.RegisterHandler(DisconnectedDevice.Create<ISwitchVM, SwitchInfo>());
            WeatherDataMediator.RegisterHandler(DisconnectedDevice.Create<IWeatherDataVM, WeatherDataInfo>());

            // Camera, mount, guider: NINA's own view models (each registers itself as its mediator's handler)
            Camera = new CameraVM(ProfileService, CameraMediator, FilterWheelMediator, ApplicationStatusMediator, options.CameraChooser);
            Telescope = new TelescopeVM(ProfileService, TelescopeMediator, ApplicationStatusMediator, DomeMediator, options.TelescopeChooser) {
                // Only used for the lat/long prompt, which a headless profile switches off (TelescopeLocationSyncDirection)
                WindowService = WindowServiceFactory.Create()
            };
            var guiderChooser = options.GuiderChooser?.Invoke(TelescopeMediator) ?? new GuiderChooser(ProfileService, TelescopeMediator);
            Guider = new GuiderVM(ProfileService, GuiderMediator, ApplicationStatusMediator, guiderChooser);

            // Saving, history, imaging
            ImageSaveController = new ImageSaveController(ProfileService, ImageSaveMediator, ApplicationStatusMediator);
            ImageHistory = new HeadlessImageHistory(ProfileService, ImageSaveMediator);
            ImageControl = new HeadlessImageControl(ProfileService);
            ImageStatistics = new HeadlessImageStatistics(ProfileService);
            imaging = new ImagingVM(ProfileService, ImagingMediator, CameraMediator, TelescopeMediator, FilterWheelMediator, FocuserMediator,
                RotatorMediator, GuiderMediator, WeatherDataMediator, ApplicationStatusMediator, ImageControl, ImageStatistics, ImageHistory);

            NighttimeCalculator = options.NighttimeCalculator ?? new NighttimeCalculator(ProfileService);
            PlateSolverFactory = options.PlateSolverFactory ?? new PlateSolverFactoryProxy();

            // The symbol broker registers itself as a consumer of every device mediator and of ImagingMediator.ImagePrepared
            SymbolBroker = new SymbolBroker(ProfileService, SwitchMediator, WeatherDataMediator, CameraMediator, DomeMediator, FlatDeviceMediator,
                FilterWheelMediator, RotatorMediator, SafetyMonitorMediator, FocuserMediator, TelescopeMediator, GuiderMediator, ImagingMediator);
            FieldRotation = new FieldRotationSymbols(SymbolBroker, ProfileService, TelescopeMediator, CameraMediator);

            Services = new SequencerServices {
                ProfileService = ProfileService,
                Camera = CameraMediator,
                Telescope = TelescopeMediator,
                Guider = GuiderMediator,
                Focuser = FocuserMediator,
                FilterWheel = FilterWheelMediator,
                Rotator = RotatorMediator,
                Dome = DomeMediator,
                DomeFollower = new NullDomeFollower(),
                FlatDevice = FlatDeviceMediator,
                SafetyMonitor = SafetyMonitorMediator,
                Switch = SwitchMediator,
                WeatherData = WeatherDataMediator,
                Imaging = ImagingMediator,
                ImageSave = ImageSaveMediator,
                ImageHistory = ImageHistory,
                ApplicationStatus = ApplicationStatusMediator,
                Application = ApplicationMediator,
                Sequence = SequenceMediator,
                SymbolBroker = SymbolBroker,
                NighttimeCalculator = NighttimeCalculator,
                FramingAssistant = new NullFramingAssistant(),
                PlanetariumFactory = new NullPlanetariumFactory(),
                PlateSolverFactory = PlateSolverFactory,
                WindowServiceFactory = WindowServiceFactory
            };
            Catalogue = new RigSequencerCatalogue(Services);
            Factory = new HeadlessSequencerFactory(Catalogue);
            Json = new SequenceJsonConverter(Factory);
            Generator = new SequenceTreeGenerator(Factory, ProfileService);
            Validator = new PlanValidator(ProfileService, NighttimeCalculator);
        }

        public IProfileService ProfileService { get; }

        public CameraMediator CameraMediator { get; } = new CameraMediator();
        public TelescopeMediator TelescopeMediator { get; } = new TelescopeMediator();
        public GuiderMediator GuiderMediator { get; } = new GuiderMediator();
        public FocuserMediator FocuserMediator { get; } = new FocuserMediator();
        public FilterWheelMediator FilterWheelMediator { get; } = new FilterWheelMediator();
        public RotatorMediator RotatorMediator { get; } = new RotatorMediator();
        public DomeMediator DomeMediator { get; } = new DomeMediator();
        public FlatDeviceMediator FlatDeviceMediator { get; } = new FlatDeviceMediator();
        public SafetyMonitorMediator SafetyMonitorMediator { get; } = new SafetyMonitorMediator();
        public SwitchMediator SwitchMediator { get; } = new SwitchMediator();
        public WeatherDataMediator WeatherDataMediator { get; } = new WeatherDataMediator();
        public ImagingMediator ImagingMediator { get; } = new ImagingMediator();
        public ImageSaveMediator ImageSaveMediator { get; } = new ImageSaveMediator();
        public ApplicationStatusMediator ApplicationStatusMediator { get; } = new ApplicationStatusMediator();
        public ApplicationMediator ApplicationMediator { get; } = new ApplicationMediator();

        /// <summary>No sequence navigation (editor) is registered: no rig entity calls into it (only SaveSequence does, which is not compiled).</summary>
        public SequenceMediator SequenceMediator { get; } = new SequenceMediator();

        public HeadlessWindowServiceFactory WindowServiceFactory { get; } = new HeadlessWindowServiceFactory();

        public CameraVM Camera { get; }
        public TelescopeVM Telescope { get; }
        public GuiderVM Guider { get; }
        public ImageSaveController ImageSaveController { get; }
        public HeadlessImageHistory ImageHistory { get; }
        public HeadlessImageControl ImageControl { get; }
        public HeadlessImageStatistics ImageStatistics { get; }
        public HeadlessApplicationStatus ApplicationStatus { get; }
        public INighttimeCalculator NighttimeCalculator { get; }
        public IPlateSolverFactory PlateSolverFactory { get; }
        public SymbolBroker SymbolBroker { get; }
        public FieldRotationSymbols FieldRotation { get; }
        public SequencerServices Services { get; }
        public RigSequencerCatalogue Catalogue { get; }
        public HeadlessSequencerFactory Factory { get; }
        public SequenceJsonConverter Json { get; }
        public SequenceTreeGenerator Generator { get; }
        public PlanValidator Validator { get; }

        private readonly ImagingVM imaging;

        /// <summary>
        /// Connects the camera, the mount and then the guider (DirectGuider needs the mount), each from its chooser's selection
        /// after a rescan, as the NINA app's "connect all" does. Returns false if any of the three did not connect.
        /// </summary>
        public async Task<bool> ConnectAsync() {
            await Camera.Rescan();
            var camera = await Camera.Connect();
            await Telescope.Rescan();
            var mount = await Telescope.Connect();
            await Guider.Rescan();
            var guider = await Guider.Connect();
            return camera && mount && guider;
        }

        /// <summary>
        /// Disconnects guider, mount and camera. The camera's cooler is switched off first, whatever happens to the other devices:
        /// NINA's ASICamera.Disconnect only closes the camera and would leave the cooler running (host contract item 7). A guider
        /// or mount that fails to disconnect is logged and does not keep the camera connected.
        /// </summary>
        public async Task DisconnectAsync() {
            SwitchCoolerOff();
            try {
                await Guider.Disconnect();
            } catch (Exception ex) {
                Logger.Error("Headless host: the guider did not disconnect cleanly", ex);
            }
            try {
                await Telescope.Disconnect();
            } catch (Exception ex) {
                Logger.Error("Headless host: the mount did not disconnect cleanly", ex);
            }
            await Camera.Disconnect();
        }

        private void SwitchCoolerOff() {
            try {
                if (CameraMediator.GetInfo()?.Connected == true && CameraMediator.GetDevice() is ICamera camera && camera.CanSetTemperature && camera.CoolerOn) {
                    Logger.Info("Headless host: switching the camera cooler off before disconnecting");
                    camera.CoolerOn = false;
                }
            } catch (Exception ex) {
                Logger.Error("Headless host: could not switch the cooler off", ex);
            }
        }

        public void Dispose() {
            if (disposed) {
                return;
            }
            disposed = true;
            FieldRotation.Dispose();
            SymbolBroker.Dispose();
            imaging.Dispose();
            ImageSaveController.Shutdown();
        }
    }
}
