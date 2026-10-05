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
using Moq;
using NINA.Core.Locale;
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Mac;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;

namespace NINA.Mac.Equipment.Test {

    /// <summary>
    /// The vendor-free device choosers (NINA.Equipment.Mac). The first three cases are upstream's DeviceChooserVMTest cases
    /// for DetermineSelectedDevice (NINA.Test/ViewModel/DeviceChooserVMTest.cs) with the same expectations, run against the
    /// mac base class; the rest check each chooser's device list and order against its upstream chooser minus the vendors.
    /// The profile is a real NINA Profile with its defaults.
    /// </summary>
    [TestFixture]
    public class DeviceChooserTest {

        private static (IProfileService Service, NINA.Profile.Profile Profile) RealProfile() {
            var profile = new NINA.Profile.Profile("Equipment test");
            var service = new Mock<IProfileService>();
            service.SetupGet(x => x.ActiveProfile).Returns(profile);
            return (service.Object, profile);
        }

        private static Mock<IDevice> CreateDevice(string id, string name) {
            var device = new Mock<IDevice>();
            device.SetupGet(x => x.Id).Returns(id);
            device.SetupGet(x => x.Name).Returns(name);
            device.SetupGet(x => x.DisplayName).Returns(name);
            return device;
        }

        private static IEquipmentProvider<T> Provider<T>(string name, params T[] devices) where T : IDevice {
            var provider = new Mock<IEquipmentProvider<T>>();
            provider.SetupGet(x => x.Name).Returns(name);
            provider.Setup(x => x.GetEquipment()).Returns(devices.ToList());
            return provider.Object;
        }

        private static T Device<T>(string id, string name) where T : class, IDevice {
            var device = new Mock<T>();
            device.SetupGet(x => x.Id).Returns(id);
            device.SetupGet(x => x.Name).Returns(name);
            device.SetupGet(x => x.DisplayName).Returns(name);
            return device.Object;
        }

        private sealed class TestDeviceChooser : DeviceChooser<IDevice> {

            public TestDeviceChooser(IProfileService profileService) : base(profileService, null) {
            }

            public override Task GetEquipment() {
                return Task.CompletedTask;
            }

            public void Determine(IList<IDevice> devices, string id, string name) {
                DetermineSelectedDevice(devices, id, name);
            }
        }

        [Test]
        public void DetermineSelectedDevice_WhenPersistedIdExists_SelectsMatchingDevice() {
            var chooser = new TestDeviceChooser(Mock.Of<IProfileService>());
            var matchingDevice = CreateDevice("camera-1", "Camera One");
            IList<IDevice> devices = new List<IDevice> { CreateDevice("camera-0", "Camera Zero").Object, matchingDevice.Object };

            chooser.Determine(devices, "camera-1", "Saved Camera");

            chooser.SelectedDevice.Should().BeSameAs(matchingDevice.Object);
            chooser.Devices.Should().Equal(devices);
            chooser.Devices.Should().HaveCount(2);
        }

        [Test]
        public void DetermineSelectedDevice_WhenPersistedIdIsMissing_InsertsOfflineDeviceAtTop() {
            var chooser = new TestDeviceChooser(Mock.Of<IProfileService>());
            IList<IDevice> devices = new List<IDevice> { CreateDevice("camera-0", "Camera Zero").Object };

            chooser.Determine(devices, "missing-camera", "Saved Camera");

            chooser.SelectedDevice.Id.Should().Be("missing-camera");
            chooser.SelectedDevice.Name.Should().Be("Saved Camera (OFFLINE)");
            chooser.Devices[0].Should().BeSameAs(chooser.SelectedDevice);
            chooser.Devices.Should().HaveCount(2);
        }

        [Test]
        public void DetermineSelectedDevice_WhenDeviceListIsEmpty_LeavesSelectionUnset() {
            var chooser = new TestDeviceChooser(Mock.Of<IProfileService>());

            chooser.Determine(new List<IDevice>(), "missing-camera", "Saved Camera");

            chooser.SelectedDevice.Should().BeNull();
            chooser.Devices.Should().BeEmpty();
        }

        [Test]
        public void SetupDialog_IsNeverOpen_AndChooserRaisesDevicesChanged() {
            var chooser = new TestDeviceChooser(Mock.Of<IProfileService>());
            var changed = new List<string?>();
            chooser.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            chooser.Determine(new List<IDevice> { CreateDevice("a", "A").Object }, "a", "A");

            chooser.SetupDialogOpen.Should().BeFalse();
            changed.Should().Equal(nameof(IDeviceChooserVM.SelectedDevice), nameof(IDeviceChooserVM.Devices));
        }

        [Test]
        public async Task TelescopeChooser_ListsNoMountThenProviders_AndSelectsTheProfilesMount() {
            var (profileService, profile) = RealProfile();
            var lx200 = Device<ITelescope>("Nightglass_LX200GPS", "Meade LX200GPS");
            profile.TelescopeSettings.Id = "Nightglass_LX200GPS";
            var chooser = new TelescopeChooser(profileService, new[] { Provider("Nightglass", lx200) });

            await chooser.GetEquipment();

            chooser.Devices.Select(d => d.Id).Should().Equal("No_Device", "Nightglass_LX200GPS");
            chooser.Devices[0].Name.Should().Be(Loc.Instance["LblNoMount"]);
            chooser.SelectedDevice.Should().BeSameAs(lx200);
        }

        [Test]
        public async Task TelescopeChooser_DefaultProfile_SelectsNoMount() {
            var (profileService, _) = RealProfile();
            var chooser = new TelescopeChooser(profileService);

            await chooser.GetEquipment();

            chooser.Devices.Should().ContainSingle().Which.Should().BeOfType<DummyDevice>();
            chooser.SelectedDevice.Id.Should().Be("No_Device");
        }

        [Test]
        public async Task FocuserChooser_ProviderThatThrows_IsSkipped_AndAMissingFocuserShowsOffline() {
            var (profileService, profile) = RealProfile();
            profile.FocuserSettings.Id = "Nightglass_1209";
            profile.FocuserSettings.LastDeviceName = "Meade #1209";
            var broken = new Mock<IEquipmentProvider<IFocuser>>();
            broken.SetupGet(x => x.Name).Returns("Broken");
            broken.Setup(x => x.GetEquipment()).Throws(new InvalidOperationException("serial port gone"));
            var chooser = new FocuserChooser(profileService, new[] { broken.Object });

            await chooser.GetEquipment();

            chooser.Devices.Select(d => d.Id).Should().Equal("Nightglass_1209", "No_Device");
            chooser.Devices[0].Should().BeOfType<OfflineDevice>();
            chooser.SelectedDevice.Name.Should().Be("Meade #1209 (OFFLINE)");
            chooser.Devices[1].Name.Should().Be(Loc.Instance["LblNoFocuser"]);
        }

        [Test]
        public async Task GuiderChooser_ListsNoGuiderAndMountDither_AndRegistersDirectGuiderWithTheMount() {
            var (profileService, profile) = RealProfile();
            profile.GuiderSettings.GuiderName = "Direct_Guider";
            var telescopeMediator = new Mock<ITelescopeMediator>();
            var chooser = new GuiderChooser(profileService, telescopeMediator.Object);

            await chooser.GetEquipment();

            chooser.Devices.Select(d => d.GetType()).Should().Equal(typeof(DummyGuider), typeof(DirectGuider));
            chooser.Devices.Select(d => d.Id).Should().Equal("No_Guider", "Direct_Guider");
            chooser.SelectedDevice.Should().BeOfType<DirectGuider>().Which.Name.Should().Be("Mount Dither");
            telescopeMediator.Verify(x => x.RegisterConsumer((DirectGuider)chooser.SelectedDevice), Times.Once);
        }

        [Test]
        public async Task GuiderChooser_DefaultProfile_ShowsUpstreamsDefaultPhd2AsOffline() {
            // NINA's default GuiderName is "PHD2", which the mac list does not have: as upstream would for any missing guider, it
            // becomes an offline placeholder. A mac host's default profile should name "Direct_Guider" or "No_Guider" instead.
            var (profileService, profile) = RealProfile();
            profile.GuiderSettings.GuiderName.Should().Be("PHD2");
            var chooser = new GuiderChooser(profileService, Mock.Of<ITelescopeMediator>());

            await chooser.GetEquipment();

            chooser.Devices.Select(d => d.Id).Should().Equal("PHD2", "No_Guider", "Direct_Guider");
            chooser.SelectedDevice.Should().BeOfType<OfflineDevice>();
        }

        [Test]
        public async Task CameraChooser_WithoutACamera_ListsNoCameraAndProviders_AndKeepsTheSavedAsiOffline() {
            if (ASICameras.Count > 0) {
                Assert.Ignore("A ZWO camera is connected. The chooser would open it to read its alias (ASICamera's constructor), which only the Hardware tests may do.");
            }
            var (profileService, profile) = RealProfile();
            profile.CameraSettings.Id = "ZWOptical_ZWO ASI585MC Pro_";
            profile.CameraSettings.LastDeviceName = "ZWO ASI585MC Pro";
            var other = Device<ICamera>("Provider_Camera", "Provider camera");
            var chooser = new CameraChooser(profileService, Mock.Of<IExposureDataFactory>(), new[] { Provider("Provider", other) });

            await chooser.GetEquipment();

            chooser.Devices.Select(d => d.Id).Should().Equal("ZWOptical_ZWO ASI585MC Pro_", "No_Device", "Provider_Camera");
            chooser.Devices[1].Name.Should().Be(Loc.Instance["LblNoCamera"]);
            chooser.SelectedDevice.Should().BeOfType<OfflineDevice>().Which.Name.Should().Be("ZWO ASI585MC Pro (OFFLINE)");
        }
    }
}
