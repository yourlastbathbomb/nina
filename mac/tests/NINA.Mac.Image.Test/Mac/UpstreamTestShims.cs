#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// Upstream NINA.Test/ImageDataTest.cs imports NINA.Equipment.Equipment.MyCamera but uses no type from it. NINA.Equipment
// is not part of this test assembly; declaring the (empty) namespace lets the file compile unchanged. Nothing is declared in it.
namespace NINA.Equipment.Equipment.MyCamera {
}
