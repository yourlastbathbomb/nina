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

namespace NINA.Astrometry.Mac {

    /// <summary>
    /// Mac-only: installs NINA.Mac.Native's DllImport resolver for this assembly, so upstream SOFA.cs and NOVAS.cs
    /// ("SOFA_2023_10_11.dll", "NOVAS31lib.dll") load libsofa.dylib and libnovas31.dylib from the app folder (or
    /// Contents/Frameworks). On Windows, DllLoader.LoadDll preloads the DLLs from External/x64 instead; off Windows it
    /// does nothing.
    /// </summary>
    internal static class NativeRegistration {

        // A module initializer runs before any code in this assembly, so the resolver is in place before the static
        // constructors of SOFA and NOVAS make their first P/Invoke, whichever host (app, test runner) loads the assembly.
#pragma warning disable CA2255 // ModuleInitializer in a library: deliberate, the resolver must exist before any NINA.Astrometry code runs
        [ModuleInitializer]
        internal static void Register() {
            NativeLibraries.Register(typeof(SOFA).Assembly);
        }
#pragma warning restore CA2255
    }
}
