#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Runtime.CompilerServices;

// Upstream Properties/AssemblyInfo.cs grants NINA.Test (and NINA) access to internals; the mac test project and the mac
// sequencing assembly get the same, for upstream's internal test seams and for CenterAfterDriftTrigger's centring factories.
[assembly: InternalsVisibleTo("NINA.Mac.Sequencer.Test")]
[assembly: InternalsVisibleTo("NINA.Mac.Sequencing")]
