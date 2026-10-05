#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Mac {

    /// <summary>
    /// Mac counterpart of NINA.WPF.Base's DeviceChooserVM&lt;T&gt; (NINA.WPF.Base/ViewModel/Equipment/DeviceChooserVM.cs), which
    /// the mac build does not compile: the upstream choosers reference every vendor SDK. The device list, the selection and
    /// <see cref="DetermineSelectedDevice"/> are ported unchanged. The setup-dialog command is left out: upstream opens the
    /// driver's dialog on an STA thread (Thread.SetApartmentState throws off Windows), and no mac device has a setup dialog,
    /// so <see cref="SetupDialogOpen"/> is always false.
    /// Devices from outside NINA.Equipment (the LX200 mount and focuser) come in as <see cref="IEquipmentProvider{T}"/>s, the
    /// contract upstream's plugin providers use; the host passes them in instead of NINA's plugin scan.
    /// </summary>
    public abstract class DeviceChooser<T> : BaseINPC, IDeviceChooserVM where T : IDevice {
        protected readonly IProfileService profileService;
        private readonly IReadOnlyList<IEquipmentProvider<T>> equipmentProviders;

        // As upstream: GetEquipment serializes on the semaphore, SelectedDevice locks its monitor
        protected SemaphoreSlim lockObj = new SemaphoreSlim(1, 1);

        protected DeviceChooser(IProfileService profileService, IEnumerable<IEquipmentProvider<T>> equipmentProviders) {
            this.profileService = profileService;
            this.equipmentProviders = (equipmentProviders ?? Enumerable.Empty<IEquipmentProvider<T>>()).ToList();
            this.Devices = new List<IDevice>();
        }

        private IList<IDevice> devices;

        public IList<IDevice> Devices {
            get => devices;
            protected set {
                devices = value;
                RaisePropertyChanged();
            }
        }

        public abstract Task GetEquipment();

        private IDevice selectedDevice;

        public IDevice SelectedDevice {
            get {
                lock (lockObj) {
                    return selectedDevice;
                }
            }
            set {
                lock (lockObj) {
                    selectedDevice = value;
                    RaisePropertyChanged();
                }
            }
        }

        /// <summary>Always false: there is no setup dialog on macOS (see the class summary).</summary>
        public bool SetupDialogOpen => false;

        /// <summary>
        /// Adds every device the providers report, as upstream's "Plugin Providers" block does in each chooser: a provider that
        /// throws is logged and skipped.
        /// </summary>
        protected void AddProviderDevices(List<IDevice> devices, string kind) {
            foreach (var provider in equipmentProviders) {
                try {
                    var providerDevices = provider.GetEquipment();
                    Logger.Info($"Found {providerDevices?.Count} {provider.Name} {kind}");
                    devices.AddRange(providerDevices.Cast<IDevice>());
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }
        }

        /// <summary>
        /// Upstream DeviceChooserVM.DetermineSelectedDevice, unchanged: select the device whose id the profile stored; if it is not
        /// in the list, put an <see cref="OfflineDevice"/> with that id and name at the top and select it. An empty list stays empty.
        /// </summary>
        protected void DetermineSelectedDevice(IList<IDevice> d, string id, string name) {
            if (d.Count > 0) {
                var items = (from device in d where device.Id == id select device);
                if (items.Any()) {
                    SelectedDevice = items.First();
                } else {
                    var offlineDevice = new OfflineDevice(id, name);
                    d.Insert(0, offlineDevice);
                    SelectedDevice = offlineDevice;
                }
            }
            Devices = d;
        }
    }
}
