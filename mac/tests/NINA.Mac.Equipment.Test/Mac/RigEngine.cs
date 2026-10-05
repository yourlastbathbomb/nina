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
using NINA.Core.Interfaces;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;

namespace NINA.Mac.Equipment.Test {

    /// <summary>
    /// The engine pieces a headless capture needs, as a host would bind them: a real NINA Profile (upstream defaults plus this
    /// rig's optics and site) behind IProfileService, and NINA.Image's ExposureDataFactory. Star detection and annotation are
    /// upstream's behaviours, which are only constructed, never run (GDI+; see README-engine.md, "Star detection").
    /// </summary>
    internal static class RigEngine {

        public static (IProfileService Service, NINA.Profile.Profile Profile) Profile(string? imageFolder = null) {
            var profile = new NINA.Profile.Profile("Nightglass rig");
            profile.CameraSettings.PixelSize = 2.9;
            profile.TelescopeSettings.Name = "Meade LX200GPS 10in";
            profile.TelescopeSettings.FocalLength = 2500;
            profile.TelescopeSettings.FocalRatio = 10;
            profile.AstrometrySettings.Latitude = 22.3;
            profile.AstrometrySettings.Longitude = 114.18;
            if (imageFolder != null) {
                profile.ImageFileSettings.FilePath = imageFolder;
            }
            var service = new Mock<IProfileService>();
            service.SetupGet(x => x.ActiveProfile).Returns(profile);
            return (service.Object, profile);
        }

        public static ExposureDataFactory ExposureDataFactory(IProfileService profileService) {
            var detection = new Mock<IPluggableBehaviorSelector<IStarDetection>>();
            detection.Setup(x => x.GetBehavior()).Returns(new StarDetection());
            var annotation = new Mock<IPluggableBehaviorSelector<IStarAnnotator>>();
            annotation.Setup(x => x.GetBehavior()).Returns(new StarAnnotator());
            var imageDataFactory = new ImageDataFactory(profileService, detection.Object, annotation.Object);
            return new ExposureDataFactory(imageDataFactory, profileService, detection.Object, annotation.Object);
        }
    }
}
