#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NINA.Equipment.Mac {

    /// <summary>
    /// Mac counterpart of NINA.WPF.Base's GuiderChooserVM (NINA.WPF.Base/ViewModel/Equipment/Guider/GuiderChooserVM.cs) without
    /// PHD2, MetaGuide, SkyGuard and MGEN: "No guider" (<see cref="DummyGuider"/>), the mount-only <see cref="DirectGuider"/>
    /// ("Mount Dither", which dithers through <see cref="ITelescopeMediator.PulseGuide"/>), then any provider's guiders, in
    /// upstream's order. The selection follows the profile's GuiderSettings.GuiderName / LastDeviceName.
    /// As upstream, every <see cref="GetEquipment"/> creates a new DirectGuider, which registers itself with the telescope
    /// mediator; the earlier ones are not disposed.
    /// </summary>
    public class GuiderChooser : DeviceChooser<IGuider> {
        private readonly ITelescopeMediator telescopeMediator;

        public GuiderChooser(IProfileService profileService, ITelescopeMediator telescopeMediator, IEnumerable<IEquipmentProvider<IGuider>> equipmentProviders = null)
            : base(profileService, equipmentProviders) {
            this.telescopeMediator = telescopeMediator;
        }

        public override async Task GetEquipment() {
            await lockObj.WaitAsync();
            try {
                var devices = new List<IDevice>();
                devices.Add(new DummyGuider(profileService));
                devices.Add(new DirectGuider(profileService, telescopeMediator));

                /* Plugin Providers */
                AddProviderDevices(devices, "Guiders");

                DetermineSelectedDevice(devices, profileService.ActiveProfile.GuiderSettings.GuiderName, profileService.ActiveProfile.GuiderSettings.LastDeviceName);
            } finally {
                lockObj.Release();
            }
        }
    }
}
