#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace NINA.Core.Utility {

    /// <summary>
    /// Probe-only stand-in so the upstream ASICameraDll.cs compiles without NINA.Core. Its static constructor calls
    /// LoadDll; like the real DllLoader off Windows, this does nothing: NativeLibraries resolves ASICamera2.dll instead.
    /// </summary>
    internal static class DllLoader {

        public static void LoadDll(string dllSubPath) {
        }
    }
}
