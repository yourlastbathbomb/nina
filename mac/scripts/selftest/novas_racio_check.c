/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

/*
 * Check program for the set_racio_file shim in libnovas31.dylib
 * (mac/src/NINA.Mac.Native/csrc/novas_racio.c). build-astrometry-natives.sh --selftest runs it.
 *
 *   novas_racio_check write-native FILE  write a synthetic cio_ra.bin with this platform's layout
 *                                        (header: 3 doubles + 8-byte long)
 *   novas_racio_check write-win FILE     the same data with the Windows layout (4-byte long)
 *   novas_racio_check run [FILE]         call set_racio_file(FILE) if FILE is given, then print the
 *                                        cio_location result for JD(TDB) 2451545.0
 *
 * The synthetic table holds 1000 daily records from JD 2451000.5, all with RA = 3600 arcsec. If the
 * file is used, cio_location interpolates that constant, so it returns 3600 / 54000 h and ref_sys 1.
 * If the file is not used, it returns the equinox-based value and ref_sys 2.
 */
#include <stdint.h>
#include <stdio.h>
#include <string.h>

void set_racio_file(char *name);
short int cio_location(double jd_tdb, short int accuracy, double *ra_cio, short int *ref_sys);

static int write_table(const char *path, int native_long) {
    const double jd_beg = 2451000.5, t_int = 1.0, ra_arcsec = 3600.0;
    const long n_recs = 1000;
    const double jd_end = jd_beg + (n_recs - 1) * t_int;
    FILE *f = fopen(path, "wb");
    long i;

    if (f == NULL) {
        perror(path);
        return 1;
    }
    fwrite(&jd_beg, sizeof(double), 1, f);
    fwrite(&jd_end, sizeof(double), 1, f);
    fwrite(&t_int, sizeof(double), 1, f);
    if (native_long) {
        fwrite(&n_recs, sizeof(long), 1, f);
    } else {
        int32_t n32 = (int32_t)n_recs;
        fwrite(&n32, sizeof n32, 1, f);
    }
    for (i = 0; i < n_recs; i++) {
        double t = jd_beg + i * t_int;
        fwrite(&t, sizeof(double), 1, f);
        fwrite(&ra_arcsec, sizeof(double), 1, f);
    }
    return fclose(f) == 0 ? 0 : 1;
}

int main(int argc, char *argv[]) {
    double ra_cio = 0.0;
    short int ref_sys = 0, error;

    if (argc == 3 && strcmp(argv[1], "write-native") == 0) {
        return write_table(argv[2], 1);
    }
    if (argc == 3 && strcmp(argv[1], "write-win") == 0) {
        return write_table(argv[2], 0);
    }
    if ((argc == 2 || argc == 3) && strcmp(argv[1], "run") == 0) {
        if (argc == 3) {
            set_racio_file(argv[2]);
        }
        error = cio_location(2451545.0, 0, &ra_cio, &ref_sys);
        printf("error=%d ref_sys=%d ra_cio=%.15f\n", error, ref_sys, ra_cio);
        return error;
    }
    fprintf(stderr, "usage: %s write-native FILE | write-win FILE | run [FILE]\n", argv[0]);
    return 2;
}
