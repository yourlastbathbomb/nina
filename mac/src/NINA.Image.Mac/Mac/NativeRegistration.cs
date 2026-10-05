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

namespace NINA.Image.Mac {

    /// <summary>
    /// Mac-only: installs NINA.Mac.Native's DllImport resolver for NINA.Image, as NINA.Astrometry's NativeRegistration
    /// does, so its native imports resolve to dylibs in the app folder (or Contents/Frameworks) once NativeLibraries maps
    /// them. NINA.Image imports "cfitsionative.dll" (CfitsioNative: FITS reading and compressed FITS writing) and
    /// "libraw_0_22_1.dll" (LibRawConverter: DSLR RAW). Neither is mapped or shipped yet, so those calls throw
    /// DllNotFoundException, as on a Windows install without the DLLs. The managed FITS writer (the default,
    /// FITSUseLegacyWriter) and XISF reading and writing need no native code. CFITSIO also needs the C long fix
    /// (plan P5) before it can work on LP64; see ../README-engine.md.
    /// </summary>
    internal static class NativeRegistration {

        // A module initializer runs before any code in this assembly, so the resolver is in place before the first P/Invoke,
        // whichever host (app, test runner) loads the assembly.
#pragma warning disable CA2255 // ModuleInitializer in a library: deliberate, the resolver must exist before any NINA.Image code runs
        [ModuleInitializer]
        internal static void Register() {
            NativeLibraries.Register(typeof(NativeRegistration).Assembly);
        }
#pragma warning restore CA2255
    }
}
