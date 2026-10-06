#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NINA.Mac.App.Services;
using NINA.Mac.App.ViewModels;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.Test.Ui {

    /// <summary>View-model behaviour on simulated services and a manual clock (no UI thread needed).</summary>
    [TestFixture]
    public class ViewModelTests : HeadlessTestBase {

        private sealed class FakeTheme : IThemeController {
            public bool IsNightVision { get; private set; }

            public void Apply(bool nightVision) => IsNightVision = nightVision;
        }

        private static (AppServices Services, MainWindowViewModel Vm, ManualClock Clock, FakeTheme Theme, RecordingKeepAwake KeepAwake) Create() {
            var keepAwake = new RecordingKeepAwake();
            var services = CreateServices(keepAwake: keepAwake);
            var theme = new FakeTheme();
            return (services, new MainWindowViewModel(services, theme), (ManualClock)services.Clock, theme, keepAwake);
        }

        [Test]
        public void Shell_HasTheEightScreensInNightOrder() {
            var (_, vm, _, _, _) = Create();
            vm.Pages.Select(p => p.Title).Should().Equal("Connect", "Cool", "Focus", "Target", "Run", "Calibrate", "Teardown", "Settings");
            vm.NightPages.Should().HaveCount(7);
            vm.Pages.Select(p => p.Step).Should().Equal("1", "2", "3", "4", "5", "6", "7", "");
            vm.CurrentPage.Should().BeSameAs(vm.Connect);
            vm.Title.Should().Be(AppInfo.Current.DisplayName);
        }

        [Test]
        public void Navigation() {
            var (_, vm, _, _, _) = Create();
            vm.NavigateCommand.Execute(PageKind.Focus);
            vm.CurrentPage.Should().BeSameAs(vm.Focus);
            vm.SidebarSelection.Should().BeSameAs(vm.Focus);
            vm.OpenSettingsCommand.Execute(null);
            vm.CurrentPage.Should().BeSameAs(vm.Settings);
            vm.IsSettingsSelected.Should().BeTrue();
            vm.SidebarSelection.Should().BeNull();
            vm.SidebarSelection = vm.Teardown;
            vm.CurrentPage.Should().BeSameAs(vm.Teardown);
        }

        [Test]
        public async Task ConnectAll_ConnectsBothAndStartsCooling_StatusBarFollows() {
            var (services, vm, clock, _, keepAwake) = Create();
            vm.Connect.Ports.Select(p => p.Path).Should().Contain("/dev/cu.usbserial-A10KX5Z3");
            vm.Connect.SelectedPort.Path.Should().Be("/dev/cu.usbserial-A10KX5Z3", "the USB serial adapter is chosen by default");
            vm.Connect.CanStartNight.Should().BeTrue();

            await vm.Connect.StartNightCommand.ExecuteAsync(null);

            services.Camera.State.Should().Be(DeviceConnectionState.Connected);
            services.Mount.State.Should().Be(DeviceConnectionState.Connected);
            services.Focuser.State.Should().Be(DeviceConnectionState.Connected);
            services.Camera.CoolerOn.Should().BeTrue();
            vm.Connect.CanStartNight.Should().BeFalse();
            vm.StatusBar.Camera.IsConnected.Should().BeTrue();
            vm.StatusBar.Mount.Detail.Should().StartWith("Alt 30.0° Az 180.0°");
            vm.StatusBar.KeepAwakeText.Should().Be("Awake: no idle sleep, display on, no App Nap");
            keepAwake.State.IsEngaged.Should().BeTrue();

            clock.Advance(TimeSpan.FromMinutes(2));
            services.Tick();
            vm.StatusBar.Camera.Detail.Should().Be("18.0 °C · cooler 100 %");
            vm.Cool.SensorTemperatureText.Should().Be("18.0 °C");
            vm.Cool.StatusText.Should().StartWith("Cooling to 0.0 °C");
            vm.StatusBar.PowerText.Should().Be("Battery 76% (on battery, 2:00 left)");
        }

        [Test]
        public async Task ColdDisconnect_NeedsASecondClick() {
            var (services, vm, clock, _, _) = Create();
            await vm.Connect.StartNightCommand.ExecuteAsync(null);
            clock.Advance(TimeSpan.FromMinutes(10));
            await vm.Connect.DisconnectCameraCommand.ExecuteAsync(null);
            services.Camera.State.Should().Be(DeviceConnectionState.Connected);
            vm.Connect.ColdDisconnectWarning.Should().Contain("Warm it first");
            await vm.Connect.DisconnectCameraCommand.ExecuteAsync(null);
            services.Camera.State.Should().Be(DeviceConnectionState.Disconnected);
            vm.Connect.ColdDisconnectWarning.Should().BeNull();
        }

        [Test]
        public async Task ConnectionLost_ShowsBanner_ReconnectRestoresCooling() {
            var (services, vm, clock, _, _) = Create();
            await vm.Connect.StartNightCommand.ExecuteAsync(null);
            clock.Advance(TimeSpan.FromMinutes(10));
            vm.Banner.IsVisible.Should().BeFalse();

            vm.Settings.SimulateCameraLossCommand.Execute(null);
            vm.Banner.IsVisible.Should().BeTrue();
            vm.Banner.Message.Should().Contain("Camera").And.Contain("cooler is no longer managed");
            vm.StatusBar.Camera.IsLost.Should().BeTrue();

            vm.Settings.SimulateMountLossCommand.Execute(null);
            vm.Banner.Message.Should().Contain("Mount");
            services.Focuser.State.Should().Be(DeviceConnectionState.Lost);

            await vm.Banner.ReconnectCommand.ExecuteAsync(null);
            vm.Banner.IsVisible.Should().BeFalse();
            services.Camera.State.Should().Be(DeviceConnectionState.Connected);
            services.Mount.State.Should().Be(DeviceConnectionState.Connected);
            services.Camera.CoolerOn.Should().BeTrue("the setpoint in force before the loss is restored");
            services.Camera.TargetTemperature.Should().Be(0);
        }

        [Test]
        public async Task Focus_NudgesSpeedAndRecentre() {
            var (services, vm, _, _, _) = Create();
            var focus = vm.Focus;
            await focus.MoveInLargeCommand.ExecuteAsync(null);
            focus.ErrorMessage.Should().Contain("connect the mount");
            await vm.Connect.StartNightCommand.ExecuteAsync(null);
            focus.ErrorMessage = null;

            focus.FocuserReady.Should().BeTrue();
            await focus.MoveInLargeCommand.ExecuteAsync(null);
            focus.Position.Should().Be(30000 - 250);
            await focus.MoveOutSmallCommand.ExecuteAsync(null);
            focus.Position.Should().Be(30000 - 250 + 50);
            focus.CustomMs = 1000;
            await focus.MoveInCustomCommand.ExecuteAsync(null);
            focus.Position.Should().Be(30000 - 250 + 50 - 1000);

            focus.SelectedSpeed = 3;
            services.Focuser.Speed.Should().Be(3);
            focus.Position.Should().Be(30000);
            focus.PositionNote.Should().Contain("recentred");

            await focus.TakeOneCommand.ExecuteAsync(null);
            focus.HfrText.Should().EndWith(" px");
            focus.HfrHistory.Should().HaveCount(1);
            focus.BahtinovText.Should().Contain("move in", "the simulator starts outside focus");
        }

        [Test]
        public async Task Target_UseForRun_HandsThePlanToRun_AndRunCompletes() {
            var (services, vm, _, _, _) = Create();
            vm.NavigateTo(PageKind.Target);
            vm.Target.SearchText = "ngc 253";
            vm.Target.Results.Select(t => t.Name).Should().Equal("NGC 253");
            vm.Target.SelectedTarget.Name.Should().Be("NGC 253");
            vm.Target.AltAzText.Should().Contain("rising, east");
            vm.Target.MaxSubText.Should().StartWith("Max sub now:");
            vm.Target.FrameCount = 4;
            vm.Target.UseForRunCommand.Execute(null);
            vm.CurrentPage.Should().BeSameAs(vm.Run);
            vm.Run.Plan.TargetName.Should().Be("NGC 253");
            vm.Run.Plan.FrameCount.Should().Be(4);
            vm.Run.LimitsText.Should().Contain("75°").And.Contain("astronomical dawn (05:");

            await vm.Run.StartCommand.ExecuteAsync(null);
            vm.Run.ErrorMessage.Should().Contain("Connect the camera");

            await vm.Connect.StartNightCommand.ExecuteAsync(null);
            await vm.Run.StartCommand.ExecuteAsync(null);
            vm.Run.ErrorMessage.Should().BeNull();
            vm.Run.StateText.Should().Be("Finished");
            vm.Run.FramesText.Should().Be("4 / 4");
            vm.Run.OutputFolder.Should().Be("/Users/test/Astro/Nightglass/2026-10-10/NGC 253/lights");
            vm.Run.Log.Should().Contain(l => l.Contains("Session end: All frames taken"));
            vm.StatusBar.SessionText.Should().Be("Finished 4/4");
            services.Mount.Altitude.Should().BeGreaterThan(20);
        }

        [Test]
        public void Target_Andromeda_WarnsAboutTheBlockedNorth() {
            var (_, vm, clock, _, _) = Create();
            clock.Set(new DateTimeOffset(2026, 10, 10, 23, 50, 0, TimeSpan.FromHours(8))); // close to M31's transit
            vm.Target.SearchText = "M31";
            vm.Target.Refresh();
            vm.Target.IsObservable.Should().BeFalse();
            vm.Target.Warnings.Should().Contain(w => w.Contains("blocked northern sky"));
        }

        [Test]
        public async Task Calibrate_FlatsThenDarksNeedCooling() {
            var (_, vm, clock, _, _) = Create();
            vm.Connect.CoolAfterConnect = false;
            await vm.Connect.StartNightCommand.ExecuteAsync(null);
            vm.Calibrate.FlatCount = 3;
            await vm.Calibrate.RunFlatsCommand.ExecuteAsync(null);
            vm.Calibrate.Status.Should().StartWith("Done: 3 flats");
            vm.Calibrate.DarkCount = 2;
            vm.Calibrate.DarkExposuresText = "10, 20";
            await vm.Calibrate.RunDarksCommand.ExecuteAsync(null);
            vm.Calibrate.ErrorMessage.Should().Contain("cool the camera first");
            vm.Cool.TargetTemperature = 0;
            vm.Cool.StartCoolingCommand.Execute(null);
            clock.Advance(TimeSpan.FromMinutes(10));
            await vm.Calibrate.RunDarksCommand.ExecuteAsync(null);
            vm.Calibrate.Status.Should().Be("Done: 4 darks (10, 20 s)");
            vm.Calibrate.DarksFolder.Should().EndWith("/library/darks", "the dark library is shared by every night (NINA.Mac.Siril)");
        }

        [TestCase("10, 20, 30", new[] { 10.0, 20, 30 })]
        [TestCase("10s 20s", new[] { 10.0, 20 })]
        [TestCase("5;5;10", new[] { 5.0, 10 })]
        public void Calibrate_ParsesDarkExposures(string text, double[] expected) => CalibrateViewModel.ParseExposures(text).Should().Equal(expected);

        [TestCase("")]
        [TestCase("ten")]
        [TestCase("-5")]
        public void Calibrate_RejectsBadDarkExposures(string text) => CalibrateViewModel.ParseExposures(text).Should().BeNull();

        [Test]
        public async Task Teardown_RunsInOrder_AndLetsTheMacSleep() {
            var (services, vm, clock, _, keepAwake) = Create();
            await vm.Connect.StartNightCommand.ExecuteAsync(null);
            clock.Advance(TimeSpan.FromMinutes(10));
            keepAwake.State.IsEngaged.Should().BeTrue();

            await vm.Teardown.RunTeardownCommand.ExecuteAsync(null);

            vm.Teardown.ErrorMessage.Should().BeNull();
            vm.Teardown.Steps.Select(s => s.Status).Should().OnlyContain(s => s == TeardownStepStatus.Done);
            vm.Teardown.StopSession.Detail.Should().Be("no session running");
            vm.Teardown.WarmCamera.Detail.Should().StartWith("cooler off, sensor 2");
            vm.Teardown.SoftPark.Detail.Should().Be("Alt 30° Az 180°, tracking off");
            services.Camera.State.Should().Be(DeviceConnectionState.Disconnected);
            services.Mount.State.Should().Be(DeviceConnectionState.Disconnected);
            keepAwake.State.IsEngaged.Should().BeFalse();
            vm.Teardown.ReleaseKeepAwake.Detail.Should().Be("Sleep allowed");
            vm.Teardown.SirilCommand.Should().Contain("<target>");
        }

        [Test]
        public void Settings_SaveValidatesAndAppliesNightVision() {
            var (services, vm, _, theme, _) = Create();
            var settings = vm.Settings;
            settings.Draft.MaxAltitudeDegrees = 10; // below the minimum
            settings.SaveCommand.Execute(null);
            settings.ErrorMessage.Should().Contain("Altitude limits");
            services.Settings.Current.MaxAltitudeDegrees.Should().Be(75);

            settings.RevertCommand.Execute(null);
            settings.Draft.NightVision = true;
            settings.ImagesRootText = "~/Documents/astro";
            settings.StorageWarning.Should().Contain("iCloud");
            settings.ImagesRootText = "~/AstroFrames";
            settings.StorageWarning.Should().BeNull();
            settings.SaveCommand.Execute(null);
            settings.ErrorMessage.Should().BeNull();
            services.Settings.Current.NightVision.Should().BeTrue();
            services.Settings.Current.ImagesRoot.Should().Be("~/AstroFrames");
            services.DataPaths.ImagesRoot.Should().Be("/Users/test/AstroFrames");
            theme.IsNightVision.Should().BeTrue();
            settings.SavedMessage.Should().StartWith("Saved to /Users/test/Library/Application Support/Nightglass/settings.json");
        }

        [Test]
        public void NightVisionToggle_PersistsAndAppliesTheme() {
            var (services, vm, _, theme, _) = Create();
            vm.ToggleNightVisionCommand.Execute(null);
            vm.IsNightVision.Should().BeTrue();
            theme.IsNightVision.Should().BeTrue();
            services.Settings.Current.NightVision.Should().BeTrue();
            vm.ToggleNightVisionCommand.Execute(null);
            theme.IsNightVision.Should().BeFalse();
        }

        [Test]
        public void About_CreditsNinaAndMpl_AndTheNameAvoidsNina() {
            var (_, vm, _, _, _) = Create();
            var raised = 0;
            vm.AboutRequested += (_, _) => raised++;
            vm.ShowAboutCommand.Execute(null);
            raised.Should().Be(1);
            vm.About.AppName.Should().Be("Nightglass");
            vm.About.AppName.Replace(".", "").Should().NotContainEquivalentOf("NINA");
            vm.About.BasedOnText.Should().StartWith("Based on N.I.N.A.").And.Contain("3.3.0");
            vm.About.LicenseText.Should().Contain("Mozilla Public License").And.Contain("MPL-2.0");
            vm.About.ThirdParty.Should().Contain(t => t.StartsWith("libusb") && t.Contains("LGPL-2.1"));
            // Everything bundled is credited; SOFA's licence (clause 3(a)) requires its statement
            vm.About.ThirdParty.Should().Contain(t => t.StartsWith("IAU SOFA")
                && t.Contains("uses routines and computations derived by its developers from software provided by SOFA under license to them")
                && t.Contains("does not itself constitute software provided by and/or endorsed by SOFA"));
            vm.About.ThirdParty.Should().Contain(t => t.StartsWith("NOVAS") && t.Contains("U.S. Naval Observatory"));
            vm.About.ThirdParty.Should().Contain(t => t.StartsWith("JPL") && t.Contains("Jet Propulsion Laboratory"));
            vm.About.ThirdParty.Should().Contain(t => t.Contains("Inter") && t.Contains("SIL Open Font License"));
        }
    }
}
