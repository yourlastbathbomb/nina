#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Locale;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NINA.Equipment.Mac {

    /// <summary>
    /// Mac counterpart of NINA.WPF.Base's CameraChooserVM (NINA.WPF.Base/ViewModel/Equipment/Camera/CameraChooserVM.cs) without
    /// the other vendors: "No camera", the ZWO ASI cameras (the ASI block, ported unchanged), then any provider's cameras, in
    /// upstream's order. The selection follows the profile's CameraSettings.Id / LastDeviceName.
    /// As CameraVM does when it connects (CameraVM.ChooseCamera), the host wraps the selected camera in a
    /// <see cref="PersistSettingsCameraDecorator"/> before calling Connect.
    /// </summary>
    public class CameraChooser : DeviceChooser<ICamera> {
        private readonly IExposureDataFactory exposureDataFactory;

        public CameraChooser(IProfileService profileService, IExposureDataFactory exposureDataFactory, IEnumerable<IEquipmentProvider<ICamera>> equipmentProviders = null)
            : base(profileService, equipmentProviders) {
            this.exposureDataFactory = exposureDataFactory;
        }

        public override async Task GetEquipment() {
            await lockObj.WaitAsync();
            try {
                var devices = new List<IDevice>();

                devices.Add(new DummyDevice(Loc.Instance["LblNoCamera"]));

                /* ASI */
                try {
                    var asiCameras = ASICameras.Count;
                    Logger.Info($"Found {asiCameras} ASI Cameras");
                    for (int cameraIndex = 0; cameraIndex < asiCameras; cameraIndex++) {
                        var cam = ASICameras.GetCamera(cameraIndex, profileService, exposureDataFactory);
                        if (!string.IsNullOrEmpty(cam.Name)) {
                            Logger.Trace(string.Format("Adding {0}", cam.Name));
                            devices.Add(cam);
                        }
                    }
                } catch (Exception ex) {
                    Logger.Error(ex);
                }

                /* Plugin Providers */
                AddProviderDevices(devices, "Cameras");

                DetermineSelectedDevice(devices, profileService.ActiveProfile.CameraSettings.Id, profileService.ActiveProfile.CameraSettings.LastDeviceName);
            } finally {
                lockObj.Release();
            }
        }
    }
}
