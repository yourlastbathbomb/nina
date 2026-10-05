#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.Native;
using System.Runtime.CompilerServices;

namespace NINA.Equipment.Mac {

    /// <summary>
    /// Mac-only: installs NINA.Mac.Native's DllImport resolver for NINA.Equipment, as NINA.Astrometry's and NINA.Image's
    /// NativeRegistration do, so the ZWO binding's "ASICamera2.dll" (ZWOptical.ASISDK.ASICameraDll, 29 imports) resolves to
    /// libASICamera2.dylib in the app folder (or Contents/Frameworks). It is the only native library the kept NINA.Equipment
    /// files import. Upstream's own DllLoader.LoadDll call in ASICameraDll's static constructor is a no-op off Windows.
    /// </summary>
    internal static class NativeRegistration {

        // A module initializer runs before any code in this assembly, so the resolver is in place before the first P/Invoke
        // (ASICameras.Count from a device chooser, for example), whichever host (app, test runner) loads the assembly.
#pragma warning disable CA2255 // ModuleInitializer in a library: deliberate, the resolver must exist before any NINA.Equipment code runs
        [ModuleInitializer]
        internal static void Register() {
            NativeLibraries.Register(typeof(NativeRegistration).Assembly);
        }
#pragma warning restore CA2255
    }
}
