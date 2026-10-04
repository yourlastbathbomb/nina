#!/usr/bin/env python3
#region "copyright"
#
#    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
#
#    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.
#
#    This Source Code Form is subject to the terms of the Mozilla Public
#    License, v. 2.0. If a copy of the MPL was not distributed with this
#    file, You can obtain one at http://mozilla.org/MPL/2.0/.
#
#endregion "copyright"
"""Independent oracle for the absolute times pinned in AlmanacRegressionTest.cs.

Run: python3 almanac_oracle.py   (standard library only)

It shares no code or model with NINA.Mac.RigTools, and is more complete than it:
  * Time: TT = UTC + 69.184 s for the ephemerides, UT1 = UTC. GMST from the IAU 2006 Earth-rotation-angle
    expression; apparent sidereal time = GMST + equation of the equinoxes.
    (RigTools: IAU 1982 GMST, mean sidereal time, UTC throughout.)
  * Nutation: IAU 1980 main terms (Meeus ch. 22). Obliquity: IAU 2006. (RigTools: none.)
  * Sun: Meeus ch. 25 geometric longitude and radius vector, plus nutation and aberration (25.10).
    (RigTools: Astronomical Almanac low-precision formula.)
  * Stars: J2000 place -> annual aberration (Earth velocity from the Sun vector) -> IAU 2006 P03 precession
    matrix -> nutation matrix. Hour angle from apparent sidereal time.
    (RigTools: IAU 1976 precession of the mean place, no aberration or nutation.)
  * Crossings: 5-minute scan, then bisection to 1 ms. Altitudes are geometric (no refraction), the same
    convention RigTools and NINA's twilight limits use.

Self-checks printed first (all against Meeus, Astronomical Algorithms 2nd ed.):
  12.a apparent sidereal time, 23.a apparent place of theta Persei, 25.a apparent Sun.
"""
import math
from datetime import datetime, timedelta, timezone

D2R = math.pi / 180.0
AS2R = D2R / 3600.0
LAT, LON = 22.25, 114.18          # Deep Water Bay, Hong Kong (Site.DeepWaterBay)
HKT = timezone(timedelta(hours=8))
J2000_UTC = datetime(2000, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
C_AU_PER_DAY = 173.1446326846693
TT_MINUS_UTC = 69.184


def mat_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def mat_vec(a, v):
    return [sum(a[i][k] * v[k] for k in range(3)) for i in range(3)]


def transpose(a):
    return [[a[j][i] for j in range(3)] for i in range(3)]


def rx(t):
    c, s = math.cos(t), math.sin(t)
    return [[1, 0, 0], [0, c, s], [0, -s, c]]


def ry(t):
    c, s = math.cos(t), math.sin(t)
    return [[c, 0, -s], [0, 1, 0], [s, 0, c]]


def rz(t):
    c, s = math.cos(t), math.sin(t)
    return [[c, s, 0], [-s, c, 0], [0, 0, 1]]


def jd_utc(t):
    return 2451545.0 + (t - J2000_UTC).total_seconds() / 86400.0


def tt_centuries(t):
    return (jd_utc(t) + TT_MINUS_UTC / 86400.0 - 2451545.0) / 36525.0


def mean_obliquity(T):  # IAU 2006
    return (84381.406 - 46.836769 * T - 0.0001831 * T ** 2 + 0.00200340 * T ** 3) * AS2R


def nutation(T):  # IAU 1980 main terms, Meeus ch. 22
    Ls = (280.4665 + 36000.7698 * T) * D2R
    Lm = (218.3165 + 481267.8813 * T) * D2R
    Om = (125.04452 - 1934.136261 * T) * D2R
    dpsi = (-17.20 * math.sin(Om) - 1.32 * math.sin(2 * Ls) - 0.23 * math.sin(2 * Lm) + 0.21 * math.sin(2 * Om)) * AS2R
    deps = (9.20 * math.cos(Om) + 0.57 * math.cos(2 * Ls) + 0.10 * math.cos(2 * Lm) - 0.09 * math.cos(2 * Om)) * AS2R
    return dpsi, deps


def precession_matrix(T):  # IAU 2006 (P03) zeta_A, z_A, theta_A
    zeta = (2.650545 + 2306.083227 * T + 0.2988499 * T ** 2 + 0.01801828 * T ** 3 - 0.000005971 * T ** 4 - 0.0000003173 * T ** 5) * AS2R
    z = (-2.650545 + 2306.077181 * T + 1.0927348 * T ** 2 + 0.01826837 * T ** 3 - 0.000028596 * T ** 4 - 0.0000002904 * T ** 5) * AS2R
    theta = (2004.191903 * T - 0.4294934 * T ** 2 - 0.04182264 * T ** 3 - 0.000007089 * T ** 4 - 0.0000001274 * T ** 5) * AS2R
    return mat_mul(rz(-z), mat_mul(ry(theta), rz(-zeta)))


def nutation_matrix(T):
    eps0 = mean_obliquity(T)
    dpsi, deps = nutation(T)
    return mat_mul(rx(-(eps0 + deps)), mat_mul(rz(-dpsi), rx(eps0)))


def gast_rad(t):
    jd = jd_utc(t)
    era = 2 * math.pi * ((0.7790572732640 + 1.00273781191135448 * (jd - 2451545.0)) % 1.0)
    T = tt_centuries(t)
    gmst = era + (0.014506 + 4612.156534 * T + 1.3915817 * T ** 2 - 0.00000044 * T ** 3) * AS2R
    dpsi, _ = nutation(T)
    return gmst + dpsi * math.cos(mean_obliquity(T))


def sun_geometric_ecliptic(T):  # Meeus ch. 25
    L0 = 280.46646 + 36000.76983 * T + 0.0003032 * T ** 2
    M = (357.52911 + 35999.05029 * T - 0.0001537 * T ** 2) * D2R
    e = 0.016708634 - 0.000042037 * T - 0.0000001267 * T ** 2
    C = (1.914602 - 0.004817 * T - 0.000014 * T ** 2) * math.sin(M) + (0.019993 - 0.000101 * T) * math.sin(2 * M) + 0.000289 * math.sin(3 * M)
    nu = M + C * D2R
    R = 1.000001018 * (1 - e * e) / (1 + e * math.cos(nu))
    return (L0 + C) * D2R, R


def sun_vector_of_date(T):
    lon, R = sun_geometric_ecliptic(T)
    return mat_vec(rx(-mean_obliquity(T)), [R * math.cos(lon), R * math.sin(lon), 0.0])


def sun_apparent_radec(t):
    T = tt_centuries(t)
    lon, R = sun_geometric_ecliptic(T)
    dpsi, deps = nutation(T)
    lam = lon + dpsi - (20.4898 / R) * AS2R
    eps = mean_obliquity(T) + deps
    return math.atan2(math.cos(eps) * math.sin(lam), math.cos(lam)), math.asin(math.sin(eps) * math.sin(lam))


def earth_velocity_j2000(t):  # AU/day, = -d(geocentric Sun)/dt, rotated back to the J2000 frame
    T = tt_centuries(t)
    h = 0.01 / 36525.0
    vp, vm = sun_vector_of_date(T + h), sun_vector_of_date(T - h)
    vel = [-(p - m) / (2 * h * 36525.0) for p, m in zip(vp, vm)]
    return mat_vec(transpose(precession_matrix(T)), vel)


def star_apparent_radec(t, ra_h, dec_d):
    T = tt_centuries(t)
    a, d = ra_h * 15 * D2R, dec_d * D2R
    u = [math.cos(d) * math.cos(a), math.cos(d) * math.sin(a), math.sin(d)]
    v = earth_velocity_j2000(t)
    u = [ui + vi / C_AU_PER_DAY for ui, vi in zip(u, v)]
    n = math.sqrt(sum(x * x for x in u))
    u = mat_vec(mat_mul(nutation_matrix(T), precession_matrix(T)), [x / n for x in u])
    return math.atan2(u[1], u[0]), math.asin(u[2])


def hour_angle(t, ra):
    ha = (gast_rad(t) + LON * D2R - ra) % (2 * math.pi)
    return ha - 2 * math.pi if ha > math.pi else ha


def altitude(ha, dec):
    phi = LAT * D2R
    return math.asin(math.sin(phi) * math.sin(dec) + math.cos(phi) * math.cos(dec) * math.cos(ha)) / D2R


def sun_alt(t):
    ra, dec = sun_apparent_radec(t)
    return altitude(hour_angle(t, ra), dec)


def star_alt(t, ra_h, dec_d):
    ra, dec = star_apparent_radec(t, ra_h, dec_d)
    return altitude(hour_angle(t, ra), dec)


def star_ha(t, ra_h, dec_d):
    return hour_angle(t, star_apparent_radec(t, ra_h, dec_d)[0])


def bisect(f, a, b):
    fa = f(a)
    while (b - a).total_seconds() > 0.001:
        m = a + (b - a) / 2
        fm = f(m)
        if (fm > 0) == (fa > 0):
            a, fa = m, fm
        else:
            b = m
    return a + (b - a) / 2


def crossings(f, start, end, step_min=5):
    out, t, prev = [], start, f(start)
    while t < end:
        n = t + timedelta(minutes=step_min)
        cur = f(n)
        if (prev > 0) != (cur > 0):
            out.append(bisect(f, t, n))
        t, prev = n, cur
    return out


def fmt(t):
    return t.astimezone(HKT).strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]


def hms(h, m, s):
    return h + m / 60 + s / 3600


def dms(sign, d, m, s):
    return sign * (d + m / 60 + s / 3600)


def self_checks():
    tt = lambda dt: dt - timedelta(seconds=TT_MINUS_UTC)
    g = (math.degrees(gast_rad(datetime(1987, 4, 10, tzinfo=timezone.utc))) % 360) / 15
    print("self-check 12.a apparent sidereal time: %.4f s past 13h10m (Meeus 46.1351 s)" % ((g - hms(13, 10, 0)) * 3600))
    ra, dec = star_apparent_radec(tt(datetime(2028, 11, 13, tzinfo=timezone.utc) + timedelta(days=0.19)), 41.054063 / 15, 49.227750)
    print("self-check 23.a theta Per apparent: ra %.7f dec %.7f deg (Meeus 41.5599646 / 49.3520685)" % (math.degrees(ra) % 360, math.degrees(dec)))
    ra, dec = sun_apparent_radec(tt(datetime(1992, 10, 13, tzinfo=timezone.utc)))
    print("self-check 25.a Sun apparent: ra %.5f dec %.5f deg (Meeus 198.38083 / -7.78507; VSOP87 198.37818 / -7.78387)" % (math.degrees(ra) % 360, math.degrees(dec)))


def main():
    self_checks()
    noon = datetime(2026, 10, 4, 12, 0, tzinfo=HKT)
    end = noon + timedelta(days=1)
    print("Night of 2026-10-04, Deep Water Bay 22.25N 114.18E, HKT (UTC+8):")
    for limit in (-6, -12, -18):
        print("  Sun %4d deg: %s" % (limit, ", ".join(fmt(x) for x in crossings(lambda t: sun_alt(t) - limit, noon, end))))
    targets = {  # ClassicTargets J2000 positions
        "NGC 253": (hms(0, 47, 33.1), dms(-1, 25, 17, 18)),
        "M42": (hms(5, 35, 17.3), dms(-1, 5, 23, 28)),
        "M8": (hms(18, 3, 37.0), dms(-1, 24, 23, 12)),
        "M20": (hms(18, 2, 23.0), dms(-1, 23, 1, 48)),
    }
    for name, (ra, dec) in targets.items():
        up = crossings(lambda t: star_alt(t, ra, dec) - 15, noon, end)
        transit = [x for x in crossings(lambda t: star_ha(t, ra, dec), noon, end) if star_alt(x, ra, dec) > 0]
        print("  %-8s alt 15: %s | transit: %s" % (name, ", ".join(fmt(x) for x in up), ", ".join(fmt(x) for x in transit)))


if __name__ == "__main__":
    main()
