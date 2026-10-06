#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Services.Simulation {

    /// <summary>Centring with simulated devices: the goto only (there is no sky to solve).</summary>
    public sealed class SimulatedCentring : ICentringService {
        private readonly IMountService mount;

        public SimulatedCentring(IMountService mount) {
            this.mount = mount ?? throw new ArgumentNullException(nameof(mount));
        }

        public bool CanPlateSolve => false;

        public async Task<CentringResult> SlewAndCentreAsync(double rightAscensionHours, double declinationDegrees, IProgress<string> progress = null, CancellationToken ct = default) {
            progress?.Report("Slewing…");
            await mount.SlewToAsync(rightAscensionHours, declinationDegrees, ct);
            return new CentringResult(false, null, 0, "On target by goto. Simulated devices: no plate solve (switch to Real devices in Settings).");
        }
    }
}
