/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

/*
 * macOS shim linked into libnovas31.dylib (see build-astrometry-natives.sh): set_racio_file and the
 * cio_ra.bin lookup for novas.c.
 *
 * set_racio_file (char *name)
 *   NINA.Astrometry/NOVAS.cs imports "set_racio_file" but never calls it. The Windows NOVAS31lib.dll
 *   that NINA ships exports it. The NOVAS C3.1 source in this repo does not define it, so without
 *   this shim a call would throw EntryPointNotFoundException.
 *
 *   name is the path of the CIO RA file to use; NULL or "" means no file. A name of PATH_MAX bytes or
 *   more also means no file. novas.c decides once per process, on the first call of cio_location,
 *   whether a file is used, and cio_array keeps it open after that. Call set_racio_file before the
 *   first CIO-based computation (sidereal_time with method 0, place/ter2cel/cel2ter in CIO mode, ...).
 *
 * Default: no file (set_racio_file never called)
 *   cio_location then takes its built-in path: the CIO right ascension comes from the equinox-based
 *   formula (ira_equinox) and ref_sys = 2. NINA gets the same on Windows today. NINA copies cio_ra.bin
 *   to External/x64/NOVAS/, but stock novas.c only opens "cio_ra.bin" in the current working
 *   directory and NINA never calls set_racio_file, so the file is never found. Unlike stock novas.c,
 *   this build never looks in the working directory, so the result cannot change with the CWD (a
 *   Finder-launched app runs with CWD '/', a terminal launch with whatever directory you are in).
 *   cio_ra.bin is not shipped with the macOS build.
 *
 * Format guard
 *   cio_array reads the file header as 3 doubles and one C long, then 2-double records. A C long is
 *   8 bytes on macOS arm64. The cio_ra.bin that NINA ships for Windows was written with a 4-byte long
 *   (2,922,540 B = 28 + 16 x 182,657), so NOVAS here would misread it without any error. A file is
 *   therefore accepted only if its size is exactly 3*8 + sizeof(long) + 16 * n_recs. Otherwise the
 *   open fails with errno EINVAL and NOVAS falls back as if no file had been set. To make a valid
 *   file, build and run NOVAS31/NOVAS31/cio_file.c on the Mac, from CIO_RA.TXT (a Git LFS object that
 *   this checkout does not contain).
 *
 * Not thread-safe, like NOVAS itself. NINA serializes every NOVAS call (NOVAS.cs lockObj).
 */
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <string.h>
#include <sys/types.h>

#define NINA_MAC_EXPORT __attribute__((visibility("default")))

/* The only file name novas.c opens; see novas.c cio_location and cio_array. */
static const char cio_file_name[] = "cio_ra.bin";

/* Path of the CIO RA file to use; "" = none. */
static char racio_path[PATH_MAX];

NINA_MAC_EXPORT void set_racio_file(char *name) {
    size_t len;

    if (name == NULL) {
        racio_path[0] = '\0';
        return;
    }
    len = strlen(name);
    if (len >= sizeof racio_path) {
        racio_path[0] = '\0';
        return;
    }
    memcpy(racio_path, name, len + 1);
}

/* 1 if f has the layout cio_array expects on this platform (header: 3 doubles + 1 long). */
static int is_native_cio_file(FILE *f) {
    double header[3];
    long n_recs;
    off_t size;
    const off_t header_size = (off_t)(3 * sizeof(double) + sizeof(long));
    const off_t record_size = (off_t)(2 * sizeof(double));

    if (fread(header, sizeof(double), 3, f) != 3 || fread(&n_recs, sizeof(long), 1, f) != 1) {
        return 0;
    }
    if (fseeko(f, 0, SEEK_END) != 0 || (size = ftello(f)) < header_size) {
        return 0;
    }
    if (n_recs <= 0 || (size - header_size) % record_size != 0 || (size - header_size) / record_size != (off_t)n_recs) {
        return 0;
    }
    return fseeko(f, 0, SEEK_SET) == 0;
}

/* Replaces fopen inside novas.c (novas_racio.h). Not exported. */
FILE *nina_mac_novas_fopen(const char *name, const char *mode) {
    FILE *f;

    if (strcmp(name, cio_file_name) != 0) {
        return fopen(name, mode);
    }
    if (racio_path[0] == '\0') {
        errno = ENOENT;
        return NULL;
    }
    f = fopen(racio_path, mode);
    if (f != NULL && !is_native_cio_file(f)) {
        fclose(f);
        errno = EINVAL;
        return NULL;
    }
    return f;
}
