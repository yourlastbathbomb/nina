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
using NINA.Equipment.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NINA.Equipment.Mac {

    /// <summary>
    /// Mac counterpart of NINA.WPF.Base's TelescopeChooserVM (NINA.WPF.Base/ViewModel/Equipment/Telescope/TelescopeChooserVM.cs)
    /// without ASCOM and Alpaca: "No mount", then the providers' mounts (the LX200GPS driver, M4), in upstream's order. The
    /// selection follows the profile's TelescopeSettings.Id / LastDeviceName.
    /// </summary>
    public class TelescopeChooser : DeviceChooser<ITelescope> {

        public TelescopeChooser(IProfileService profileService, IEnumerable<IEquipmentProvider<ITelescope>> equipmentProviders = null)
            : base(profileService, equipmentProviders) {
        }

        public override async Task GetEquipment() {
            await lockObj.WaitAsync();
            try {
                var devices = new List<IDevice>();

                devices.Add(new DummyDevice(Loc.Instance["LblNoMount"]));

                /* Plugin Providers */
                AddProviderDevices(devices, "Telescopes");

                DetermineSelectedDevice(devices, profileService.ActiveProfile.TelescopeSettings.Id, profileService.ActiveProfile.TelescopeSettings.LastDeviceName);
            } finally {
                lockObj.Release();
            }
        }
    }
}
