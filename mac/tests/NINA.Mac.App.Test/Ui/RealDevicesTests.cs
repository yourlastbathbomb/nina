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
using FluentAssertions;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Mac.App.Diagnostics;
using NINA.Mac.App.Engine;
using NINA.Mac.App.Services;
using NINA.Mac.App.Theming;
using NINA.Mac.App.ViewModels;
using NINA.Mac.App.Views;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.Test.Ui {

    /// <summary>
    /// Settings › Devices = Real: AppServices composes the engine (NINA.Mac.App.Engine) instead of the simulators, the screens
    /// render on it, and a failed engine start falls back to the simulators with the reason shown. No device is opened: the
    /// camera list is a test list (the real one would scan USB) and nothing connects the mount.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class RealDevicesTests : HeadlessTestBase {
        private static readonly string EngineData = Path.Combine(Path.GetTempPath(), "nightglass app test engine " + Guid.NewGuid().ToString("N"));

        private static ThemeManager Theme => ((NINA.Mac.App.App)Application.Current).Theme;

        [OneTimeTearDown]
        public void RemoveEngineData() {
            if (!Directory.Exists(EngineData)) {
                return;
            }
            // NINA's log file in the engine data folder stays open until its logger is flushed
            NINA.Core.Utility.Logger.CloseAndFlush();
            try {
                Directory.Delete(EngineData, recursive: true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }

        private static AppServices CreateReal(EmptyCameraList cameras, Action<EngineDevicesOptions> more = null) {
            var settings = new AppSettings { DeviceSource = DeviceSource.Real };
            return AppServices.Create(new AppServicesOptions {
                Clock = new ManualClock(TestTimes.EveningOct10),
                Settings = new MemorySettingsStore(settings),
                KeepAwake = new RecordingKeepAwake(),
                PowerSource = new FakePowerSource(),
                HomeDirectory = "/Users/test",
                SerialPortLister = () => new[] { new NINA.Mac.Platform.SerialPortInfo("/dev/cu.usbserial-A10KX5Z3", true) },
                ConfigureEngine = o => {
                    o.DataDirectory = EngineRuntime.DataDirectory == null ? EngineData : null;
                    o.CameraChooser = (_, _) => cameras;
                    more?.Invoke(o);
                },
            });
        }

        [Test]
        public void RealDevices_ComposeTheEngine_AndEveryScreenRenders() {
            OnUi(() => {
                var cameras = new EmptyCameraList();
                using var services = CreateReal(cameras);
                services.EngineError.Should().BeNull();
                services.DevicesSimulated.Should().BeFalse();
                services.ActiveDeviceSource.Should().Be(DeviceSource.Real);
                services.Engine.Should().NotBeNull();
                services.SimCamera.Should().BeNull();
                services.Camera.Should().BeOfType<EngineCameraService>();
                services.Mount.Should().BeOfType<EngineMountService>();
                services.Focuser.Should().BeOfType<EngineFocuserService>();
                services.Session.Should().BeOfType<EngineSessionService>();
                services.Centring.Should().BeOfType<EngineCentringService>();
                services.CurrentLayout().Should().BeOfType<SirilFolders>();
                services.Mount.PortName.Should().Be("/dev/cu.usbserial-A10KX5Z3", "the port picker filled the empty choice");
                cameras.Scans.Should().Be(0, "composing never scans for cameras");

                Theme.Apply(false);
                var vm = new MainWindowViewModel(services, Theme);
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
                window.Show();
                try {
                    var frames = ScreenRenderer.RenderAllPages(window, vm, ScreenshotDirectory, "-real");
                    frames.Should().HaveCount(8);
                    foreach (var frame in frames) {
                        frame.View.Should().Be($"{frame.Page}View");
                        frame.Content.LooksRendered.Should().BeTrue($"{frame.Page} page area should not be blank: {frame.Content}");
                    }
                    vm.Settings.ActiveDevicesText.Should().Contain("Real devices");
                    vm.Connect.SimulationNote.Should().BeNull();
                    vm.StatusBar.SimulationNote.Should().BeNull();
                    vm.Run.PlanText.Should().StartWith("No plan yet");
                } finally {
                    window.Close();
                }
            });
        }

        [Test]
        public void RealDevices_CameraConnect_SaysWhenNoCameraIsFound() {
            var cameras = new EmptyCameraList();
            AppServices services = null;
            OnUi(() => services = CreateReal(cameras));
            try {
                var vm = OnUi(() => new MainWindowViewModel(services, Theme));
                OnUiAsync(() => vm.Connect.ConnectCameraCommand.ExecuteAsync(null));
                OnUi(() => {
                    cameras.Scans.Should().Be(1, "the camera list is scanned by Connect, once");
                    vm.Connect.ErrorMessage.Should().Contain("No ZWO camera found");
                    services.Camera.State.Should().Be(DeviceConnectionState.Disconnected);
                    services.Camera.LastError.Should().Contain("No ZWO camera found");
                });
            } finally {
                OnUi(() => services.Dispose());
            }
        }

        [Test]
        public void RealDevices_ThatCannotStart_FallBackToTheSimulators_AndSayWhy() {
            OnUi(() => {
                using var services = CreateReal(new EmptyCameraList(), o => o.Settings = null);
                services.Engine.Should().BeNull();
                services.EngineError.Should().Contain("Settings and ImagesRoot are required");
                services.DevicesSimulated.Should().BeTrue();
                services.Camera.Should().BeSameAs(services.SimCamera);
                var vm = new MainWindowViewModel(services, Theme);
                vm.Connect.SimulationNote.Should().Contain("could not start");
                vm.Settings.ActiveDevicesText.Should().Contain("could not start");
            });
        }

        [Test]
        public void RealDevices_SavedSettings_ReachTheEngineProfile() {
            OnUi(() => {
                using var services = CreateReal(new EmptyCameraList());
                var vm = new MainWindowViewModel(services, Theme);
                var p = services.Engine.Profile.ActiveProfile;
                p.TelescopeSettings.FocalLength.Should().Be(2500);

                vm.Settings.Draft.Optics.UseReducer = true;
                vm.Settings.Draft.Gain = 100;
                vm.Settings.Draft.Site.ElevationMeters = 120;
                vm.Settings.SaveCommand.Execute(null);

                vm.Settings.ErrorMessage.Should().BeNull();
                vm.Settings.SavedMessage.Should().NotContain("Restart").And.NotContain("could not");
                services.EngineSettingsError.Should().BeNull();
                p.TelescopeSettings.FocalLength.Should().Be(1575, "the reducer reaches NINA's profile (ASTAP's scale hint) without a restart");
                p.CameraSettings.Gain.Should().Be(100);
                p.AstrometrySettings.Elevation.Should().Be(120);
            });
        }

        [Test]
        public void ForceSimulatedDevices_IgnoresTheSetting() {
            OnUi(() => {
                using var services = AppServices.Create(new AppServicesOptions {
                    Clock = new ManualClock(TestTimes.EveningOct10),
                    Settings = new MemorySettingsStore(new AppSettings { DeviceSource = DeviceSource.Real }),
                    KeepAwake = new RecordingKeepAwake(),
                    PowerSource = new FakePowerSource(),
                    HomeDirectory = "/Users/test",
                    SerialPortLister = () => Array.Empty<NINA.Mac.Platform.SerialPortInfo>(),
                    ForceSimulatedDevices = true,
                });
                services.DevicesSimulated.Should().BeTrue("--gui-smoke shows the real window and must never open a device");
                services.Engine.Should().BeNull();
                services.EngineError.Should().BeNull();
            });
        }

        [Test]
        public void DeviceSource_IsSavedAsText_AndSavingAskForARestart() {
            var folder = Path.Combine(Path.GetTempPath(), "nightglass settings " + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(folder, "settings.json");
            try {
                SaveAndReload(file);
            } finally {
                Directory.Delete(folder, recursive: true);
            }

            OnUi(() => {
                using var services = CreateServices();
                var vm = new MainWindowViewModel(services, Theme);
                vm.Settings.Draft.DeviceSource = DeviceSource.Real;
                vm.Settings.SaveCommand.Execute(null);
                vm.Settings.SavedMessage.Should().Contain("Restart");
                services.Settings.Current.DeviceSource.Should().Be(DeviceSource.Real);
                services.DevicesSimulated.Should().BeTrue("the switch takes effect at the next start");
            });
        }

        private static void SaveAndReload(string file) {
            var store = new JsonSettingsStore(file);
            store.Current.DeviceSource.Should().Be(DeviceSource.Simulated, "Simulated stays the default until the hardware path is proven");
            var s = store.Current.Clone();
            s.DeviceSource = DeviceSource.Real;
            s.Solver.AstapExecutable = "~/Astro/astap/cli/astap_cli";
            store.Save(s);
            File.ReadAllText(file).Should().Contain("\"deviceSource\": \"Real\"");
            new JsonSettingsStore(file).Current.DeviceSource.Should().Be(DeviceSource.Real);
            new JsonSettingsStore(file).Current.Solver.AstapExecutable.Should().Be("~/Astro/astap/cli/astap_cli");
        }

        /// <summary>A camera list with no camera, counting its scans.</summary>
        private sealed class EmptyCameraList : IDeviceChooserVM {
            public int Scans { get; private set; }

            public IDevice SelectedDevice { get; set; }

            public bool SetupDialogOpen => false;

            public IList<IDevice> Devices { get; } = new List<IDevice>();

            public Task GetEquipment() {
                Scans++;
                return Task.CompletedTask;
            }
        }
    }
}
