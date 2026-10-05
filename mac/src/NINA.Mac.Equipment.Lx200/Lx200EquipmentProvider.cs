#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;

namespace NINA.Mac.Equipment.Lx200 {

    /// <summary>
    /// Hands the LX200GPS mount and its #1209 focuser to NINA.Equipment.Mac's <c>TelescopeChooser</c> and <c>FocuserChooser</c>
    /// (as <see cref="IEquipmentProvider{T}"/>, the contract upstream's plugin providers use). It always lists both devices,
    /// whether or not a cable is plugged in, and always the same instances, so a connected device survives a chooser refresh.
    /// The port is resolved at connect (<see cref="Lx200Settings.PortPath"/>, or the only USB serial adapter); both devices
    /// share one <see cref="Lx200Link"/> through the pool.
    /// </summary>
    public sealed class Lx200EquipmentProvider : IEquipmentProvider<ITelescope>, IEquipmentProvider<IFocuser> {
        private readonly Lazy<Lx200Telescope> telescope;
        private readonly Lazy<Lx200Focuser> focuser;

        public Lx200EquipmentProvider(IProfileService profileService, Lx200LinkPool pool = null, Lx200Clock clock = null) {
            ArgumentNullException.ThrowIfNull(profileService);
            pool ??= Lx200LinkPool.Shared;
            telescope = new Lazy<Lx200Telescope>(() => new Lx200Telescope(profileService, pool, clock));
            focuser = new Lazy<Lx200Focuser>(() => new Lx200Focuser(profileService, pool));
        }

        public string Name => "Meade LX200GPS (native serial)";

        public Lx200Telescope Telescope => telescope.Value;

        public Lx200Focuser Focuser => focuser.Value;

        IList<ITelescope> IEquipmentProvider<ITelescope>.GetEquipment() => new List<ITelescope> { Telescope };

        IList<IFocuser> IEquipmentProvider<IFocuser>.GetEquipment() => new List<IFocuser> { Focuser };
    }
}
