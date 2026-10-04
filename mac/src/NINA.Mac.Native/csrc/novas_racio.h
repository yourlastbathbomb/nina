/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

/*
 * Forced-include header for NOVAS31/NOVAS31/novas.c only (see build-astrometry-natives.sh).
 *
 * novas.c opens its CIO right-ascension file with fopen ("cio_ra.bin", "rb") in cio_location and
 * cio_array, which is relative to the process working directory. This header routes the fopen calls
 * in novas.c through nina_mac_novas_fopen (novas_racio.c), which decides which file, if any, is used.
 * Every other fopen in that translation unit is passed through unchanged. novas.c itself is not edited.
 */
#ifndef NINA_MAC_NOVAS_RACIO_H
#define NINA_MAC_NOVAS_RACIO_H

#include <stdio.h>

FILE *nina_mac_novas_fopen(const char *name, const char *mode);

#define fopen nina_mac_novas_fopen

#endif
