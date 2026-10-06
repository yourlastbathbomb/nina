#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using NINA.Astrometry.Body;
using NINA.Astrometry.Interfaces;
using NINA.Astrometry.RiseAndSet;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;

namespace NINA.Mac.Sequencer.Test.Sim {

    /// <summary>A device list with fixed devices, the first selected (the simulated camera; NINA.Equipment.Mac's CameraChooser would open real ZWO cameras).</summary>
    internal sealed class FixedDeviceChooser : IDeviceChooserVM {

        public FixedDeviceChooser(params IDevice[] devices) {
            Devices = devices.ToList();
            SelectedDevice = Devices.First();
        }

        public IDevice SelectedDevice { get; set; }

        public bool SetupDialogOpen => false;

        public IList<IDevice> Devices { get; }

        public Task GetEquipment() => Task.CompletedTask;
    }

    /// <summary>
    /// A NighttimeCalculator whose twilight times the test chooses (NINA's DawnProvider and friends read them through
    /// INighttimeCalculator.Calculate), so the real dawn providers can fire seconds from now. Sunset ("dusk", the providers'
    /// rollover time) is an hour ago.
    /// </summary>
    internal sealed class FixedNighttimeCalculator : INighttimeCalculator {
        private readonly NighttimeData data;

        public FixedNighttimeCalculator(DateTime astronomicalDawn, DateTime? nauticalDawn = null, DateTime? civilDawn = null, DateTime? dusk = null) {
            var sunset = DateTime.Now.AddHours(-1);
            var evening = dusk ?? sunset.AddMinutes(30);
            data = new NighttimeData(DateTime.Now, DateTime.Today, AstroUtil.MoonPhase.Unknown, null,
                new Fixed(evening, astronomicalDawn),
                new Fixed(evening, nauticalDawn ?? astronomicalDawn.AddMinutes(25)),
                new Fixed(sunset, astronomicalDawn.AddMinutes(75)),
                new Fixed(null, null),
                new Fixed(sunset.AddMinutes(10), civilDawn ?? astronomicalDawn.AddMinutes(50)));
        }

        public event EventHandler OnReferenceDayChanged {
            add { }
            remove { }
        }

        public NighttimeData Calculate(DateTime? selectedDate = null) => data;

        private sealed class Fixed : RiseAndSetEvent {
            private readonly DateTime? set;
            private readonly DateTime? rise;

            public Fixed(DateTime? set, DateTime? rise) : base(DateTime.Today, Sky.Latitude, Sky.Longitude, Sky.Elevation) {
                this.set = set;
                this.rise = rise;
            }

            public override DateTime? Rise => rise;

            public override DateTime? Set => set;

            protected override double AdjustAltitude(BasicBody body) => throw new NotSupportedException();

            protected override BasicBody GetBody(DateTime date) => throw new NotSupportedException();
        }
    }
}
