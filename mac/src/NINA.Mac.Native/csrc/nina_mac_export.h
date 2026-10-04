/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

/*
 * Forced-include header (clang -include) used by mac/scripts/build-astrometry-natives.sh when it
 * compiles the in-repo IAU SOFA (SOFA/SOFA/src) and USNO NOVAS 3.1 (NOVAS31/NOVAS31) C sources into
 * macOS dylibs. The vendor files themselves are compiled unchanged.
 *
 * sofa.h, novas.h, eph_manager.h, nutation.h and solarsystem.h all contain the unconditional line
 *
 *     #define EXPORT __declspec(dllexport)
 *
 * __declspec is MSVC-only, and clang rejects it when targeting Darwin. Redefining EXPORT with -D does
 * not help, because the headers redefine it. Instead this header turns __declspec(...) into
 * default symbol visibility. The libraries are compiled with -fvisibility=hidden, so a dylib exports
 * exactly the functions marked EXPORT, which are the same functions the Windows DLLs export.
 *
 * sofa.h also writes "EXPORT typedef struct {...}" for iauASTROM and iauLDBODY. Both MSVC and clang
 * ignore a visibility/dllexport attribute on a typedef; clang says so with -Wignored-attributes, which
 * this header silences for these translation units only.
 */
#ifndef NINA_MAC_EXPORT_H
#define NINA_MAC_EXPORT_H

#pragma clang diagnostic ignored "-Wignored-attributes"

#define __declspec(x) __attribute__((visibility("default")))

#endif
