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
using NINA.Core.Utility;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace NINA.Mac.Engine.Test {

    [TestFixture]
    [NonParallelizable]
    public class ProfileSmokeTest {

        [OneTimeSetUp]
        public void CreateProfileFolder() {
            Directory.CreateDirectory(Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Profiles"));
        }

        [Test]
        public void Profile_RoundTripsThroughDisk() {
            Guid id;
            string location;
            var orange = Color.FromArgb(0x80, 0xFF, 0xA5, 0x00);
            using (var profile = new NINA.Profile.Profile("mac smoke")) {
                profile.AstrometrySettings.Latitude = 22.25;
                profile.AstrometrySettings.Longitude = 114.18;
                profile.AstrometrySettings.Elevation = 40;
                profile.ImageFileSettings.FilePath = "/Users/astro/NINA";
                profile.ImageFileSettings.FilePattern = "$$IMAGETYPE$$/$$DATETIME$$";
                profile.GuiderSettings.GuideChartDeclinationColor = orange;
                // Only the "Custom" schemas persist their colours; stock schemas are reset to the built-in palette on load
                profile.ColorSchemaSettings.CopyToCustom();
                profile.ColorSchemaSettings.ColorSchema.PrimaryColor = Colors.Teal;
                profile.Save();
                id = profile.Id;
                location = profile.Location;
            }

            location.Should().Be(Path.Combine(EngineTestHost.DataRoot, "Profiles", $"{id}.profile"));
            var xml = File.ReadAllText(location);
            // WPF Color is written by DataContractSerializer as a plain POCO in the System.Windows.Media contract namespace
            xml.Should().Contain("http://schemas.datacontract.org/2004/07/System.Windows.Media");
            xml.Should().Contain(">128</").And.Contain(">165</");

            NINA.Profile.Profile.Peek(location).Name.Should().Be("mac smoke");
            using (var loaded = NINA.Profile.Profile.Load(location)) {
                loaded.Id.Should().Be(id);
                loaded.Name.Should().Be("mac smoke");
                loaded.AstrometrySettings.Latitude.Should().Be(22.25);
                loaded.AstrometrySettings.Longitude.Should().Be(114.18);
                loaded.AstrometrySettings.Elevation.Should().Be(40);
                loaded.ImageFileSettings.FilePath.Should().Be("/Users/astro/NINA");
                loaded.ImageFileSettings.FilePattern.Should().Be("$$IMAGETYPE$$/$$DATETIME$$");
                loaded.GuiderSettings.GuideChartDeclinationColor.Should().Be(orange);
                loaded.ColorSchemaSettings.ColorSchema.PrimaryColor.Should().Be(Colors.Teal);
                loaded.LastUsed.Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(1));

                var clone = NINA.Profile.Profile.Clone(loaded);
                clone.Name.Should().Be("mac smoke Copy");
                clone.Id.Should().NotBe(id);
                clone.GuiderSettings.GuideChartDeclinationColor.Should().Be(orange);
            }
        }

        [Test]
        public void ProfileService_LoadsAProfile_AndRaisesEventsThroughTheApplicationDispatcher() {
            Application.Current.Should().NotBeNull("the host creates it, as the WPF app does");
            var service = new ProfileService();
            try {
                service.TryLoad(null).Should().BeTrue();
                service.ActiveProfile.Should().NotBeNull();
                service.Profiles.Should().Contain(p => p.Id == service.ActiveProfile.Id && p.IsActive);
                Application.Current.Resources["ActiveProfile"].Should().BeSameAs(service.ActiveProfile);

                var locationChanged = 0;
                service.LocationChanged += (s, e) => locationChanged++;
                service.ChangeLatitude(22.25);
                service.ChangeLongitude(114.18);
                locationChanged.Should().Be(2);
                service.ActiveProfile.AstrometrySettings.Latitude.Should().Be(22.25);

                var second = service.Profiles.First(p => p.Id == service.ActiveProfile.Id);
                IProfile oldProfile = null;
                service.ProfileChanged += (s, e) => oldProfile = ((ProfileChangedEventArgs)e).OldProfile;
                service.Add();
                var added = service.Profiles.First(p => p.Id != second.Id && !p.IsActive);
                service.SelectProfile(added).Should().BeTrue();
                oldProfile.Should().NotBeNull();
                oldProfile.Id.Should().Be(second.Id);
                service.ActiveProfile.Id.Should().Be(added.Id);
            } finally {
                service.Release();
            }
        }
    }
}
