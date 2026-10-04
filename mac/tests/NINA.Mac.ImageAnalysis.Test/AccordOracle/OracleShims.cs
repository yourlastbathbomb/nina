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

namespace NINA.Core.Utility {

    /// <summary>
    /// Test-only stand-in for NINA.Core's MyStopWatch so the linked upstream BayerFilter16bpp compiles without
    /// pulling in NINA.Core (which is WPF-bound). It only times; nothing is logged.
    /// </summary>
    public sealed class MyStopWatch : IDisposable {

        public static MyStopWatch Measure(string memberName = "", string filePath = "") {
            return new MyStopWatch();
        }

        public void Dispose() {
        }
    }
}
