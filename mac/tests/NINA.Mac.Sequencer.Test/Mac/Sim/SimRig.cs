#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Mac;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Mac.Sequencing.Headless;
using NINA.Profile.Interfaces;
using System.Collections.Concurrent;

namespace NINA.Mac.Sequencer.Test.Sim {

    /// <summary>
    /// A mount other than <see cref="SimMount"/> (e.g. the LX200 driver over the Autostar simulator): its equipment provider, the
    /// device id the profile selects, and where it really points (J2000), which the simulated solver reports.
    /// </summary>
    internal sealed record MountUnderTest(IEquipmentProvider<ITelescope> Provider, string DeviceId, Func<Coordinates> Truth);

    /// <summary>
    /// The headless engine against simulated equipment, composed the way the app will compose it: a real NINA Profile with this
    /// rig's settings, the <see cref="HeadlessHost"/> composition root, NINA.Equipment.Mac's TelescopeChooser (with the
    /// simulated mount as an equipment provider, as the LX200 driver plugs in) and GuiderChooser (DirectGuider, "Mount Dither"),
    /// and a fixed list holding the simulated camera.
    /// </summary>
    internal sealed class SimRig : IAsyncDisposable {
        public const string FilePattern = "$$TARGETNAME$$\\$$IMAGETYPE$$\\$$TARGETNAME$$_$$EXPOSURETIME$$s_G$$GAIN$$_$$FRAMENR$$";

        private SimRig(string name, Coordinates mountStart, double gotoErrorRaArcmin, double gotoErrorDecArcmin, INighttimeCalculator? nighttime, Action<NINA.Profile.Profile>? configure,
                Func<IProfileService, IExposureDataFactory, IDeviceChooserVM>? cameraChooser = null, Func<IProfileService, MountUnderTest>? mount = null) {
            Folder = TestHost.NewFolder(name);
            ImageFolder = Path.Combine(Folder, "images");
            Directory.CreateDirectory(ImageFolder);
            Profile = new NINA.Profile.Profile("Nightglass sim " + name);
            Configure(Profile);
            configure?.Invoke(Profile);
            var service = new Mock<IProfileService>();
            service.SetupGet(x => x.ActiveProfile).Returns(Profile);
            ProfileService = service.Object;

            Camera = new SimCamera(ExposureDataFactory(ProfileService));
            Mount = new SimMount(mountStart, gotoErrorRaArcmin, gotoErrorDecArcmin);
            MountUnderTest = mount?.Invoke(ProfileService);
            if (MountUnderTest != null) {
                Profile.TelescopeSettings.Id = MountUnderTest.DeviceId;
            }
            Solver = new SimSolver(MountUnderTest?.Truth ?? (() => Mount.True), Folder);
            Solver.WriteFakeAstap();
            Profile.PlateSolveSettings.ASTAPLocation = Solver.AstapPath;
            // Each exposure is "solved" (drift check) at the pointing it was taken at
            Camera.ExposureStarted = (_, _) => Solver.WriteTruth(Solver.Truth());

            Host = new HeadlessHost(new HeadlessHostOptions {
                ProfileService = ProfileService,
                CameraChooser = cameraChooser?.Invoke(ProfileService, ExposureDataFactory(ProfileService)) ?? new FixedDeviceChooser(Camera),
                TelescopeChooser = new TelescopeChooser(ProfileService, new[] { MountUnderTest?.Provider ?? new Provider<ITelescope>(Mount) }),
                NighttimeCalculator = nighttime,
                PlateSolverFactory = Solver.Factory
            });
            Host.ApplicationStatus.StatusUpdated += (_, status) => {
                if (!string.IsNullOrEmpty(status.Status)) {
                    Statuses.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {status.Source}: {status.Status}");
                }
            };
        }

        public string Folder { get; }
        public string ImageFolder { get; }
        public NINA.Profile.Profile Profile { get; }
        public IProfileService ProfileService { get; }
        public SimCamera Camera { get; }
        /// <summary>The simulated mount (not connected when <see cref="MountUnderTest"/> replaces it).</summary>
        public SimMount Mount { get; }

        public MountUnderTest? MountUnderTest { get; }
        public SimSolver Solver { get; }
        public HeadlessHost Host { get; }
        public ConcurrentQueue<string> Statuses { get; } = new();

        public static async Task<SimRig> Create(string name, Coordinates mountStart, double gotoErrorRaArcmin = 12, double gotoErrorDecArcmin = -7,
                INighttimeCalculator? nighttime = null, Action<NINA.Profile.Profile>? configure = null,
                Func<IProfileService, IExposureDataFactory, IDeviceChooserVM>? cameraChooser = null, Func<IProfileService, MountUnderTest>? mount = null) {
            var rig = new SimRig(name, mountStart, gotoErrorRaArcmin, gotoErrorDecArcmin, nighttime, configure, cameraChooser, mount);
            try {
                var connected = await rig.Host.ConnectAsync();
                if (!connected) {
                    throw new InvalidOperationException("The simulated rig did not connect: " + string.Join(" | ", rig.Statuses));
                }
                return rig;
            } catch {
                // Whatever did connect is disconnected again (the host switches a real camera's cooler off first)
                await rig.DisposeAsync();
                throw;
            }
        }

        /// <summary>The engine with the simulated devices listed but not connected (for tests of the catalogue, JSON and generator).</summary>
        public static SimRig CreateUnconnected(string name, Coordinates? mountStart = null, INighttimeCalculator? nighttime = null, Action<NINA.Profile.Profile>? configure = null) {
            return new SimRig(name, mountStart ?? new Coordinates(Angle.ByHours(12), Angle.ByDegree(0), Epoch.J2000), 0, 0, nighttime, configure);
        }

        /// <summary>The rig's profile, with timings shortened for simulation and NINA's prompts switched off.</summary>
        private void Configure(NINA.Profile.Profile p) {
            p.AstrometrySettings.Latitude = Sky.Latitude;
            p.AstrometrySettings.Longitude = Sky.Longitude;
            p.AstrometrySettings.Elevation = Sky.Elevation;
            p.CameraSettings.Id = SimCamera.DeviceId;
            p.CameraSettings.PixelSize = 2.9;
            p.CameraSettings.BinningX = 2;
            p.CameraSettings.BinningY = 2;
            p.CameraSettings.Gain = 252;
            p.CameraSettings.Offset = 15;
            p.TelescopeSettings.Id = SimMount.DeviceId;
            p.TelescopeSettings.Name = "Meade LX200GPS 10in";
            p.TelescopeSettings.FocalLength = 2500;
            p.TelescopeSettings.FocalRatio = 10;
            p.TelescopeSettings.SettleTime = 0;
            p.TelescopeSettings.TelescopeLocationSyncDirection = TelescopeLocationSyncDirection.NOSYNC;
            p.GuiderSettings.GuiderName = "Direct_Guider";
            p.GuiderSettings.DitherPixels = 5;
            p.GuiderSettings.SettleTime = 1;
            p.ApplicationSettings.DevicePollingInterval = 0.5;
            p.ImageFileSettings.FilePath = ImageFolder;
            p.ImageFileSettings.FilePattern = FilePattern;
            p.PlateSolveSettings.PlateSolverType = PlateSolverEnum.ASTAP;
            p.PlateSolveSettings.BlindSolverType = BlindSolverEnum.ASTAP;
            p.PlateSolveSettings.BlindFailoverEnabled = false;
            p.PlateSolveSettings.ExposureTime = 1;
            p.PlateSolveSettings.Binning = 2;
            p.PlateSolveSettings.Gain = 450;
            p.PlateSolveSettings.Threshold = 1;
            p.PlateSolveSettings.NumberOfAttempts = 1;
            p.PlateSolveSettings.ReattemptDelay = 0;
            p.PlateSolveSettings.SearchRadius = 5;
            p.PlateSolveSettings.DownSampleFactor = 2;
        }

        public static IExposureDataFactory ExposureDataFactory(IProfileService profileService) {
            // Upstream star detection and annotation are only constructed, never run (GDI+): the headless image panel does not detect stars
            var detection = new Mock<IPluggableBehaviorSelector<IStarDetection>>();
            detection.Setup(x => x.GetBehavior()).Returns(new StarDetection());
            var annotation = new Mock<IPluggableBehaviorSelector<IStarAnnotator>>();
            annotation.Setup(x => x.GetBehavior()).Returns(new StarAnnotator());
            var imageDataFactory = new ImageDataFactory(profileService, detection.Object, annotation.Object);
            return new NINA.Image.ImageData.ExposureDataFactory(imageDataFactory, profileService, detection.Object, annotation.Object);
        }

        /// <summary>All FITS files the sequence saved, relative to the image folder, sorted.</summary>
        public IReadOnlyList<string> SavedFiles() {
            return Directory.GetFiles(ImageFolder, "*.fits", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(ImageFolder, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        /// <summary>Waits until the save queue has written <paramref name="count"/> files (the save worker runs after the sequence item returns).</summary>
        public async Task<IReadOnlyList<string>> WaitForFiles(int count, TimeSpan timeout) {
            var deadline = DateTime.UtcNow + timeout;
            while (SavedFiles().Count < count && DateTime.UtcNow < deadline) {
                await Task.Delay(100);
            }
            return SavedFiles();
        }

        public async ValueTask DisposeAsync() {
            try {
                await Host.DisconnectAsync();
            } finally {
                Host.Dispose();
            }
        }

        private sealed class Provider<T> : IEquipmentProvider<T> where T : IDevice {
            private readonly T device;

            public Provider(T device) {
                this.device = device;
            }

            public string Name => "Simulator";

            public IList<T> GetEquipment() => new List<T> { device };
        }
    }
}
