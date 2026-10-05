#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NINA.Mac.Native;
using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NINA.Mac.Astrometry.Test {

    /// <summary>
    /// Smoke tests for the macOS arm64 builds of IAU SOFA (libsofa.dylib) and USNO NOVAS C3.1 (libnovas31.dylib)
    /// made by mac/scripts/build-astrometry-natives.sh. The P/Invokes below copy the DLL names, entry points,
    /// signatures and structs of NINA.Astrometry/SOFA.cs and NOVAS.cs, so the calls go through NINA.Mac.Native's
    /// resolver the same way upstream's will.
    ///
    /// Expected values are published references:
    /// - SOFA: the IAU's own test program SOFA/SOFA/src/t_sofa_c.c (release 2023-10-11), with its tolerances.
    /// - NOVAS: NOVAS31/NOVAS31/example.c and its expected output example-usno.txt (USNO). USNO made that file
    ///   with the DE405 ephemeris; NINA ships DE421. Star places and sidereal time do not depend on the choice
    ///   at the printed precision. The Moon and Mars do (LE405 -> LE421 moves the Moon by metres), so those
    ///   comparisons allow 10 mas. The observed differences are about 4 mas (Moon) and 0.6 mas (Mars), as
    ///   reported by build-astrometry-natives.sh --selftest.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class NativeSmokeTest {

        // Same expression as NOVAS.cs EphemerisLocation
        private static readonly string EphemerisLocation = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "External", "JPLEPH");

        private const double MasPerDegree = 3_600_000.0;
        private const double StarToleranceMas = 0.01;
        private const double SolarSystemToleranceMas = 10.0;

        private bool ephemerisOpen;

        [OneTimeSetUp]
        public void OneTimeSetUp() {
            NativeLibraries.Register(typeof(NativeSmokeTest).Assembly);
        }

        #region Resolver

        [TestCase("SOFA_2023_10_11.dll", "libsofa.dylib")]
        [TestCase("NOVAS31lib.dll", "libnovas31.dylib")]
        public void Resolver_FindsAstrometryDylib(string importName, string dylib) {
            NativeLibraries.DylibName(importName).Should().Be(dylib);
            NativeLibraries.Locate(importName).Should().NotBeNull("run mac/scripts/build-astrometry-natives.sh first")
                .And.EndWith("/" + dylib);
        }

        [TestCase("ASICamera2.dll", "libASICamera2.dylib")]
        [TestCase("ASICamera2", "libASICamera2.dylib")]
        [TestCase("NOVAS31lib", "libnovas31.dylib")]
        [TestCase("novas31lib.DLL", "libnovas31.dylib")]
        public void Resolver_MapsNameWithAndWithoutDllSuffix(string importName, string dylib) {
            NativeLibraries.DylibName(importName).Should().Be(dylib);
        }

        [Test]
        public void Resolver_LeavesUnknownNamesToTheRuntime() {
            NativeLibraries.DylibName("kernel32.dll").Should().BeNull();
            NativeLibraries.DylibName(null).Should().BeNull();
        }

        #endregion

        #region SOFA (expected values and tolerances from t_sofa_c.c)

        [Test]
        public void Sofa_Anp() {
            Sofa.Anp(-0.1).Should().BeApproximately(6.183185307179586477, 1e-12);
        }

        [Test]
        public void Sofa_Eo06a() {
            Sofa.Eo06a(2400000.5, 53736.0).Should().BeApproximately(-0.1332882371941833644e-2, 1e-15);
        }

        [Test]
        public void Sofa_Dtf2d() {
            double u1 = 0, u2 = 0;
            var j = Sofa.Dtf2d("UTC", 1994, 6, 30, 23, 59, 60.13599, ref u1, ref u2);

            j.Should().Be(0);
            (u1 + u2).Should().BeApproximately(2449534.49999, 1e-6);
        }

        [Test]
        public void Sofa_Utctai() {
            double u1 = 0, u2 = 0;
            var j = Sofa.Utctai(2453750.5, 0.892100694, ref u1, ref u2);

            j.Should().Be(0);
            u1.Should().BeApproximately(2453750.5, 1e-6);
            u2.Should().BeApproximately(0.8924826384444444444, 1e-12);
        }

        [Test]
        public void Sofa_Taitt() {
            double t1 = 0, t2 = 0;
            var j = Sofa.Taitt(2453750.5, 0.892482639, ref t1, ref t2);

            j.Should().Be(0);
            t1.Should().BeApproximately(2453750.5, 1e-6);
            t2.Should().BeApproximately(0.892855139, 1e-12);
        }

        [Test]
        public void Sofa_Ae2hd() {
            double h = 0, d = 0;
            // C iauAe2hd returns void; SOFA.cs declares short, so the return value is meaningless and ignored.
            Sofa.Ae2hd(5.5, 1.1, 0.7, ref h, ref d);

            h.Should().BeApproximately(0.5933291115507309663, 1e-14);
            d.Should().BeApproximately(0.9613934761647817620, 1e-14);
        }

        [Test]
        public void Sofa_Hd2ae() {
            double a = 0, e = 0;
            // C iauHd2ae returns void; SOFA.cs declares short, so the return value is meaningless and ignored.
            Sofa.Hd2ae(1.1, 1.2, 0.3, ref a, ref e);

            a.Should().BeApproximately(5.916889243730066194, 1e-13);
            e.Should().BeApproximately(0.4472186304990486228, 1e-14);
        }

        [Test]
        public void Sofa_Atco13() {
            double aob = 0, zob = 0, hob = 0, dob = 0, rob = 0, eo = 0;
            var j = Sofa.Atco13(2.71, 0.174, 1e-5, 5e-6, 0.1, 55.0,
                2456384.5, 0.969254051, 0.1550675, -0.527800806, -1.2345856, 2738.0, 2.47230737e-7, 1.82640464e-6,
                731.0, 12.8, 0.59, 0.55,
                ref aob, ref zob, ref hob, ref dob, ref rob, ref eo);

            j.Should().Be(0);
            aob.Should().BeApproximately(0.9251774485485515207e-1, 1e-12);
            zob.Should().BeApproximately(1.407661405256499357, 1e-12);
            hob.Should().BeApproximately(-0.9265154431529724692e-1, 1e-12);
            dob.Should().BeApproximately(0.1716626560072526200, 1e-12);
            rob.Should().BeApproximately(2.710260453504961012, 1e-12);
            eo.Should().BeApproximately(-0.003020548354802412839, 1e-14);
        }

        [TestCase("R", 2.710085107986886201, 0.1717653435758265198, 2.709956744659136129, 0.1741696500898471362)]
        [TestCase("H", -0.09247619879782006106, 0.1717653435758265198, 2.709956744659734086, 0.1741696500898471362)]
        [TestCase("A", 0.09233952224794989993, 1.407758704513722461, 2.709956744659734086, 0.1741696500898471366)]
        public void Sofa_Atoc13(string type, double ob1, double ob2, double expectedRc, double expectedDc) {
            double rc = 0, dc = 0;
            var j = Sofa.Atoc13(type, ob1, ob2,
                2456384.5, 0.969254051, 0.1550675, -0.527800806, -1.2345856, 2738.0, 2.47230737e-7, 1.82640464e-6,
                731.0, 12.8, 0.59, 0.55,
                ref rc, ref dc);

            j.Should().Be(0);
            rc.Should().BeApproximately(expectedRc, 1e-12);
            dc.Should().BeApproximately(expectedDc, 1e-12);
        }

        [Test]
        public void Sofa_Atci13() {
            double ri = 0, di = 0, eo = 0;
            Sofa.Atci13(2.71, 0.174, 1e-5, 5e-6, 0.1, 55.0, 2456165.5, 0.401182685, ref ri, ref di, ref eo);

            ri.Should().BeApproximately(2.710121572968696744, 1e-12);
            di.Should().BeApproximately(0.1729371367219539137, 1e-12);
            eo.Should().BeApproximately(-0.002900618712657375647, 1e-14);
        }

        [Test]
        public void Sofa_Atic13() {
            double rc = 0, dc = 0, eo = 0;
            Sofa.Atic13(2.710121572969038991, 0.1729371367218230438, 2456165.5, 0.401182685, ref rc, ref dc, ref eo);

            rc.Should().BeApproximately(2.710126504531716819, 1e-12);
            dc.Should().BeApproximately(0.1740632537627034482, 1e-12);
            eo.Should().BeApproximately(-0.002900618712657375647, 1e-14);
        }

        [Test]
        public void Sofa_Seps() {
            Sofa.Seps(1.0, 0.1, 0.2, -3.0).Should().BeApproximately(2.346722016996998842, 1e-14);
        }

        [Test]
        public void Sofa_Obl80() {
            Sofa.Obl80(2400000.5, 54388.0).Should().BeApproximately(0.4090751347643816218, 1e-14);
        }

        [Test]
        public void Sofa_Refco() {
            double refa = 0, refb = 0;
            Sofa.Refco(800.0, 10.0, 0.9, 0.4, ref refa, ref refb);

            refa.Should().BeApproximately(0.2264949956241415009e-3, 1e-15);
            refb.Should().BeApproximately(-0.2598658261729343970e-6, 1e-18);
        }

        #endregion

        #region NOVAS

        /// <summary>
        /// Struct sizes and offsets that clang -arch arm64 gives for novas.h (sizeof/offsetof). cat_entry.starnumber is a
        /// C long (8 bytes here) but an int in NOVAS.cs; the doubles after it still start at offset 64 in both.
        /// </summary>
        [Test]
        public void Novas_StructLayout_MatchesClangArm64() {
            Marshal.SizeOf<Novas.CatalogueEntry>().Should().Be(112);
            Marshal.OffsetOf<Novas.CatalogueEntry>(nameof(Novas.CatalogueEntry.StarNumber)).ToInt32().Should().Be(56);
            Marshal.OffsetOf<Novas.CatalogueEntry>(nameof(Novas.CatalogueEntry.RA)).ToInt32().Should().Be(64);
            Marshal.SizeOf<Novas.CelestialObject>().Should().Be(168);
            Marshal.OffsetOf<Novas.CelestialObject>(nameof(Novas.CelestialObject.Name)).ToInt32().Should().Be(4);
            Marshal.OffsetOf<Novas.CelestialObject>(nameof(Novas.CelestialObject.Star)).ToInt32().Should().Be(56);
            Marshal.SizeOf<Novas.OnSurface>().Should().Be(40);
            Marshal.SizeOf<Novas.InSpace>().Should().Be(48);
            Marshal.SizeOf<Novas.Observer>().Should().Be(96);
            Marshal.OffsetOf<Novas.Observer>(nameof(Novas.Observer.OnSurf)).ToInt32().Should().Be(8);
            Marshal.OffsetOf<Novas.Observer>(nameof(Novas.Observer.NearEarth)).ToInt32().Should().Be(48);
            Marshal.SizeOf<Novas.SkyPosition>().Should().Be(56);
            Marshal.OffsetOf<Novas.SkyPosition>(nameof(Novas.SkyPosition.RA)).ToInt32().Should().Be(24);
        }

        [Test]
        public void Novas_EphemOpen_ReadsDe421FromUpstreamLocation() {
            File.Exists(EphemerisLocation).Should().BeTrue($"NINA.Mac.Native copies mac/native/ephemeris/JPLEPH to {EphemerisLocation}");

            double begin = 0, end = 0;
            short de = 0;
            Novas.EphemOpen(EphemerisLocation, ref begin, ref end, ref de).Should().Be(0);
            ephemerisOpen = true;

            de.Should().Be(421);
            // Data span of the DE421 file in nina.external, from the start/end dates (SS) in its header record:
            // 1899-12-04 .. 2050-01-02. Its title text names the full DE421 span, 1899-07-29 .. 2053-10-09.
            begin.Should().Be(2414992.5);
            end.Should().Be(2469808.5);
        }

        [Test]
        public void Novas_JulianDate_AndCalDate() {
            Novas.JulianDate(2000, 1, 1, 12.0).Should().Be(2451545.0);

            short year = 0, month = 0, day = 0;
            double hour = 0;
            // C cal_date returns void; NOVAS.cs declares double, so the return value is meaningless and ignored.
            Novas.CalDate(2451545.0, ref year, ref month, ref day, ref hour);
            (year, month, day, hour).Should().Be(((short)2000, (short)1, (short)1, 12.0));

            var t = ExampleTimes();
            t.JdTt.Should().BeApproximately(2454580.942629, 5e-7);   // example-usno.txt, 6 decimals
            t.JdUt1.Should().BeApproximately(2454580.941871, 5e-7);
            t.DeltaT.Should().BeApproximately(65.571845, 1e-9);
        }

        [Test]
        public void Novas_SiderealTime_MatchesExample() {
            var t = ExampleTimes();
            double gast = 0;
            Novas.SiderealTime(t.JdUt1, 0.0, t.DeltaT, Novas.GstType.GreenwichApparentSiderealTime, Novas.Method.EquinoxBased, Novas.Accuracy.Full, ref gast)
                .Should().Be(0);
            gast.Should().BeApproximately(0.79362134148, 1e-10);   // example-usno.txt, 11 decimals

            // CIO-based method: without a CIO file NOVAS derives the CIO RA from the equinox, so both methods agree.
            double gastCio = 0;
            Novas.SiderealTime(t.JdUt1, 0.0, t.DeltaT, Novas.GstType.GreenwichApparentSiderealTime, Novas.Method.CIOBased, Novas.Accuracy.Full, ref gastCio)
                .Should().Be(0);
            gastCio.Should().BeApproximately(gast, 1e-10);
        }

        [Test]
        public void Novas_Place_StarFk6_1307_MatchesExample() {
            EnsureEphemeris();
            var t = ExampleTimes();
            Novas.MakeCatEntry("GMB 1830", "FK6", 1307, 11.88299133, 37.71867646, 4003.27, -5815.07, 109.21, -98.8, out var star).Should().Be(0);
            Novas.MakeObject(Novas.ObjectType.ObjectLocatedOutsideSolarSystem, 0, "GMB 1830", star, out var fk6).Should().Be(0);

            var geocenter = new Novas.Observer { Where = 0 };
            var position = new Novas.SkyPosition();
            Novas.Place(t.JdTt, ref fk6, ref geocenter, t.DeltaT, (short)Novas.CoordinateSystem.EquinoxOfDate, (short)Novas.Accuracy.Full, ref position).Should().Be(0);
            AssertRaDec(position.RA, position.Dec, 11.8915509892, 37.6586357955, StarToleranceMas);

            var observer = ExampleObserver();
            Novas.Place(t.JdTt, ref fk6, ref observer, t.DeltaT, (short)Novas.CoordinateSystem.EquinoxOfDate, (short)Novas.Accuracy.Full, ref position).Should().Be(0);
            AssertRaDec(position.RA, position.Dec, 11.8915479153, 37.6586695456, StarToleranceMas);
        }

        [Test]
        public void Novas_AppPlanet_Moon_MatchesExample() {
            EnsureEphemeris();
            var t = ExampleTimes();
            var moon = MakeMoon();

            Novas.AppPlanet(t.JdTt, moon, Novas.Accuracy.Full, out var ra, out var dec, out var dis).Should().Be(0);

            AssertRaDec(ra, dec, 17.1390774264, -27.5374448869, SolarSystemToleranceMas);
            dis.Should().BeApproximately(0.002710296515, 0.002710296515 * 1e-8);
        }

        [Test]
        public void Novas_Place_MoonTopocentric_MatchesExample() {
            EnsureEphemeris();
            var t = ExampleTimes();
            var moon = MakeMoon();
            var observer = ExampleObserver();
            var position = new Novas.SkyPosition();

            Novas.Place(t.JdTt, ref moon, ref observer, t.DeltaT, (short)Novas.CoordinateSystem.EquinoxOfDate, (short)Novas.Accuracy.Full, ref position).Should().Be(0);

            AssertRaDec(position.RA, position.Dec, 17.1031967646, -28.2902502967, SolarSystemToleranceMas);
            position.Dis.Should().BeApproximately(0.002703785126, 0.002703785126 * 1e-8);
        }

        /// <summary>
        /// example.c "Mars heliocentric ecliptic longitude and latitude and radius vector": ephemeris() for a major planet is
        /// solarsystem_hp, which NOVAS.cs BodyPositionAndVelocity calls directly.
        /// </summary>
        [Test]
        public void Novas_SolarSystemHp_MarsHeliocentric_MatchesExample() {
            EnsureEphemeris();
            var t = ExampleTimes();
            var pos = new double[3];
            var vel = new double[3];

            Novas.SolarSystemHp(new[] { t.JdTt, 0.0 }, Novas.Body.Mars, Novas.SolarSystemOrigin.SolarCenterOfMass, pos, vel).Should().Be(0);

            var ecliptic = new double[3];
            Novas.Equ2EclVec(2451545.0, 2, 0, pos, ecliptic).Should().Be(0);
            Novas.Vector2RaDec(ecliptic, out var lonHours, out var lat).Should().Be(0);
            var r = Math.Sqrt((ecliptic[0] * ecliptic[0]) + (ecliptic[1] * ecliptic[1]) + (ecliptic[2] * ecliptic[2]));

            var lon = lonHours * 15.0;
            (Math.Abs(lon - 148.0032235906) * MasPerDegree * Math.Cos(lat * Math.PI / 180.0)).Should().BeLessThan(SolarSystemToleranceMas);
            (Math.Abs(lat - 1.8288284075) * MasPerDegree).Should().BeLessThan(SolarSystemToleranceMas);
            r.Should().BeApproximately(1.664218258879, 1.664218258879 * 1e-8);
        }

        /// <summary>
        /// geo_posvel only rotates terra()'s vectors, so their lengths are the geocentric radius of the site on NOVAS'
        /// ellipsoid (ERAD 6378136.6 m, f = 1/298.25642) and that radius' rotation speed (ANGVEL 7.2921150e-5 rad/s).
        /// Checks the by-value Observer marshalling NINA.Astrometry uses.
        /// </summary>
        [Test]
        public void Novas_GeoPosVel_OnSurface_MatchesEllipsoid() {
            var t = ExampleTimes();
            var observer = ExampleObserver();
            var pos = new double[3];
            var vel = new double[3];

            Novas.GeoPosVel(t.JdTt, t.DeltaT, Novas.Accuracy.Full, observer, pos, vel).Should().Be(0);

            const double au = 1.4959787069098932e+11, a = 6378136.6, f = 1.0 / 298.25642, angvel = 7.2921150e-5;
            var phi = 42.0 * Math.PI / 180.0;
            var c = 1.0 / Math.Sqrt((Math.Cos(phi) * Math.Cos(phi)) + ((1.0 - f) * (1.0 - f) * Math.Sin(phi) * Math.Sin(phi)));
            var s = (1.0 - f) * (1.0 - f) * c;
            var radius = Math.Sqrt(Math.Pow(a * c * Math.Cos(phi), 2) + Math.Pow(a * s * Math.Sin(phi), 2)) / au;
            var speed = angvel * a * c * Math.Cos(phi) * 86400.0 / au;

            Norm(pos).Should().BeApproximately(radius, radius * 1e-12);
            Norm(vel).Should().BeApproximately(speed, speed * 1e-12);
        }

        /// <summary>
        /// NOVAS refract (Bennett's formula as documented in novas.c): r = 0.016667 / tan(h + 7.31 / (h + 4.4)) deg
        /// scaled by 0.28 p / (t + 273), with p = 1010 mbar * exp(-height / 9100 m) and t = 10 C for option 1, the site's
        /// weather for option 2, and 0 outside 0.1..91 degrees zenith distance.
        /// </summary>
        [TestCase(Novas.RefractionOption.StandardRefraction, 45.0, 0.0, 10.0, 1010.0)]
        [TestCase(Novas.RefractionOption.StandardRefraction, 85.0, 1500.0, -5.0, 850.0)]
        [TestCase(Novas.RefractionOption.LocationRefraction, 85.0, 1500.0, -5.0, 850.0)]
        [TestCase(Novas.RefractionOption.LocationRefraction, 95.0, 0.0, 10.0, 1010.0)]
        public void Novas_Refract_MatchesDocumentedFormula(Novas.RefractionOption option, double zd, double height, double temperature, double pressure) {
            var location = new Novas.OnSurface { Latitude = 22.3, Longitude = 114.2, Height = height, Temperature = temperature, Pressure = pressure };

            var p = option == Novas.RefractionOption.LocationRefraction ? pressure : 1010.0 * Math.Exp(-height / 9.1e3);
            var tc = option == Novas.RefractionOption.LocationRefraction ? temperature : 10.0;
            var h = 90.0 - zd;
            var expected = zd < 0.1 || zd > 91.0 ? 0.0 : 0.016667 / Math.Tan((h + (7.31 / (h + 4.4))) * Math.PI / 180.0) * (0.28 * p / (tc + 273.0));

            Novas.Refract(ref location, option, zd).Should().BeApproximately(expected, 1e-15);
        }

        /// <summary>
        /// set_racio_file is not in the in-repo NOVAS source; the mac build adds it (csrc/novas_racio.c). By default no
        /// CIO file is used, whatever the working directory, so cio_location reports its equinox-based fallback (2).
        /// </summary>
        [Test]
        public void Novas_SetRacioFile_IsExported_AndCioLocationUsesFallback() {
            var act = () => Novas.SetRACIOFile(null);
            act.Should().NotThrow();

            Novas.CioLocation(2451545.0, 0, out var raCio, out var refSys).Should().Be(0);
            refSys.Should().Be(2);
            double.IsFinite(raCio).Should().BeTrue();
        }

        #endregion

        #region Helpers

        private void EnsureEphemeris() {
            if (ephemerisOpen) {
                return;
            }
            File.Exists(EphemerisLocation).Should().BeTrue($"NINA.Mac.Native copies mac/native/ephemeris/JPLEPH to {EphemerisLocation}");
            double begin = 0, end = 0;
            short de = 0;
            Novas.EphemOpen(EphemerisLocation, ref begin, ref end, ref de).Should().Be(0);
            ephemerisOpen = true;
        }

        /// <summary>Time arguments exactly as NOVAS example.c computes them (2008-04-24 10:36:18 UTC).</summary>
        private static (double JdTt, double JdUt1, double DeltaT) ExampleTimes() {
            const short leapSeconds = 33;
            const double ut1Utc = -0.387845;
            var jdUtc = Novas.JulianDate(2008, 4, 24, 10.605);
            return (jdUtc + ((leapSeconds + 32.184) / 86400.0), jdUtc + (ut1Utc / 86400.0), 32.184 + leapSeconds - ut1Utc);
        }

        /// <summary>example.c site: 42 N, 70 W, 0 m, 10 C, 1010 mbar. NearEarth left null, like NOVAS.cs does.</summary>
        private static Novas.Observer ExampleObserver() {
            return new Novas.Observer {
                Where = 1,
                OnSurf = new Novas.OnSurface { Latitude = 42.0, Longitude = -70.0, Height = 0.0, Temperature = 10.0, Pressure = 1010.0 }
            };
        }

        /// <summary>The Moon as NOVAS.cs PlanetApparentCoordinates builds it.</summary>
        private static Novas.CelestialObject MakeMoon() {
            Novas.MakeCatEntry("DUMMY", "xxx", 0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, out var dummy).Should().Be(0);
            Novas.MakeObject(Novas.ObjectType.MajorPlanetSunOrMoon, (short)Novas.Body.Moon, nameof(Novas.Body.Moon), dummy, out var moon).Should().Be(0);
            return moon;
        }

        private static void AssertRaDec(double raHours, double decDegrees, double expectedRaHours, double expectedDecDegrees, double toleranceMas) {
            var raMas = Math.Abs(raHours - expectedRaHours) * 15.0 * MasPerDegree * Math.Cos(expectedDecDegrees * Math.PI / 180.0);
            var decMas = Math.Abs(decDegrees - expectedDecDegrees) * MasPerDegree;
            raMas.Should().BeLessThan(toleranceMas, $"RA {raHours:F10} h vs {expectedRaHours:F10} h");
            decMas.Should().BeLessThan(toleranceMas, $"Dec {decDegrees:F10} deg vs {expectedDecDegrees:F10} deg");
        }

        private static double Norm(double[] v) {
            return Math.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));
        }

        #endregion

        #region Native declarations (copied from NINA.Astrometry SOFA.cs / NOVAS.cs; test-only extras marked)

        private static class Sofa {
            private const string DLLNAME = "SOFA_2023_10_11.dll";

            [DllImport(DLLNAME, EntryPoint = "iauAtci13", CallingConvention = CallingConvention.Cdecl)]
            public static extern void Atci13(double rc, double dc, double pr, double pd, double px, double rv, double date1, double date2, ref double ri, ref double di, ref double eo);

            [DllImport(DLLNAME, EntryPoint = "iauAtic13", CallingConvention = CallingConvention.Cdecl)]
            public static extern void Atic13(double ri, double di, double date1, double date2, ref double rc, ref double dc, ref double eo);

            [DllImport(DLLNAME, EntryPoint = "iauAnp", CallingConvention = CallingConvention.Cdecl)]
            public static extern double Anp(double a);

            [DllImport(DLLNAME, EntryPoint = "iauEo06a", CallingConvention = CallingConvention.Cdecl)]
            public static extern double Eo06a(double date1, double date2);

            [DllImport(DLLNAME, EntryPoint = "iauDtf2d", CallingConvention = CallingConvention.Cdecl)]
            public static extern short Dtf2d(string scale, int iy, int im, int id, int ihr, int imn, double sec, ref double d1, ref double d2);

            [DllImport(DLLNAME, EntryPoint = "iauUtctai", CallingConvention = CallingConvention.Cdecl)]
            public static extern short Utctai(double utc1, double utc2, ref double tai1, ref double tai2);

            [DllImport(DLLNAME, EntryPoint = "iauTaitt", CallingConvention = CallingConvention.Cdecl)]
            public static extern short Taitt(double tai1, double tai2, ref double tt1, ref double tt2);

            [DllImport(DLLNAME, EntryPoint = "iauAe2hd", CallingConvention = CallingConvention.Cdecl)]
            public static extern short Ae2hd(double az, double el, double phi, ref double ha, ref double dec);

            [DllImport(DLLNAME, EntryPoint = "iauHd2ae", CallingConvention = CallingConvention.Cdecl)]
            public static extern short Hd2ae(double ha, double dec, double phi, ref double az, ref double el);

            [DllImport(DLLNAME, EntryPoint = "iauAtco13", CallingConvention = CallingConvention.Cdecl)]
            public static extern short Atco13(double rc, double dc, double pr, double pd, double px, double rv, double utc1, double utc2, double dut1, double elong, double phi, double hm, double xp, double yp, double phpa, double tc, double rh, double wl, ref double aob, ref double zob, ref double hob, ref double dob, ref double rob, ref double eo);

            [DllImport(DLLNAME, EntryPoint = "iauAtoc13", CallingConvention = CallingConvention.Cdecl)]
            public static extern short Atoc13(string type, double ob1, double ob2, double utc1, double utc2, double dut1, double elong, double phi, double hm, double xp, double yp, double phpa, double tc, double rh, double wl, ref double rc, ref double dc);

            [DllImport(DLLNAME, EntryPoint = "iauSeps", CallingConvention = CallingConvention.Cdecl)]
            public static extern double Seps(double al, double ap, double bl, double bp);

            [DllImport(DLLNAME, EntryPoint = "iauObl80", CallingConvention = CallingConvention.Cdecl)]
            public static extern double Obl80(double date1, double date2);

            [DllImport(DLLNAME, EntryPoint = "iauRefco", CallingConvention = CallingConvention.Cdecl)]
            public static extern void Refco(double phpa, double tc, double rh, double wl, ref double refa, ref double refb);
        }

        public static class Novas {
            private const string DLLNAME = "NOVAS31lib.dll";
            private const int SIZE_OF_OBJ_NAME = 51;
            private const int SIZE_OF_CAT_NAME = 4;

            [DllImport(DLLNAME, EntryPoint = "cal_date", CallingConvention = CallingConvention.Cdecl)]
            public static extern double CalDate(double tjd, ref short year, ref short month, ref short day, ref double hour);

            [DllImport(DLLNAME, EntryPoint = "julian_date", CallingConvention = CallingConvention.Cdecl)]
            public static extern double JulianDate(short year, short month, short day, double hour);

            [DllImport(DLLNAME, EntryPoint = "sidereal_time", CallingConvention = CallingConvention.Cdecl)]
            public static extern short SiderealTime(double jdHigh, double jdLow, double detlaT, GstType gstType, Method method, Accuracy accuracy, ref double gst);

            [DllImport(DLLNAME, EntryPoint = "place", CallingConvention = CallingConvention.Cdecl)]
            public static extern short Place(double jdTt, ref CelestialObject celObject, ref Observer observer, double deltaT, short coordinateSystem, short accuracy, ref SkyPosition position);

            [DllImport(DLLNAME, EntryPoint = "set_racio_file", CallingConvention = CallingConvention.Cdecl)]
            public static extern void SetRACIOFile([MarshalAs(UnmanagedType.LPStr)] string? Name);

            [DllImport(DLLNAME, EntryPoint = "ephem_open", CallingConvention = CallingConvention.Cdecl)]
            public static extern short EphemOpen([MarshalAs(UnmanagedType.LPStr)] string Ephem_Name, ref double JD_Begin, ref double JD_End, ref short DENumber);

            [DllImport(DLLNAME, EntryPoint = "refract", CallingConvention = CallingConvention.Cdecl)]
            public static extern double Refract(ref OnSurface location, RefractionOption refractionOption, double zdObs);

            [DllImport(DLLNAME, EntryPoint = "solarsystem_hp", CallingConvention = CallingConvention.Cdecl)]
            public static extern short SolarSystemHp(
                [In][MarshalAs(UnmanagedType.LPArray, SizeConst = 2)] double[] tjd,
                Body body,
                SolarSystemOrigin origin,
                [In, Out][MarshalAs(UnmanagedType.LPArray, SizeConst = 3)] double[] position,
                [In, Out][MarshalAs(UnmanagedType.LPArray, SizeConst = 3)] double[] velocity);

            [DllImport(DLLNAME, EntryPoint = "make_cat_entry", CallingConvention = CallingConvention.Cdecl)]
            public static extern short MakeCatEntry(
                [MarshalAs(UnmanagedType.LPTStr, SizeConst = SIZE_OF_OBJ_NAME)] string star_name,
                [MarshalAs(UnmanagedType.LPTStr, SizeConst = SIZE_OF_CAT_NAME)] string catalog,
                long star_num,
                double ra,
                double dec,
                double pm_ra,
                double pm_dec,
                double parallax,
                double rad_vel,
                [Out] out CatalogueEntry star);

            [DllImport(DLLNAME, EntryPoint = "make_object", CallingConvention = CallingConvention.Cdecl)]
            public static extern short MakeObject(
                ObjectType type,
                short number,
                [MarshalAs(UnmanagedType.LPTStr, SizeConst = SIZE_OF_OBJ_NAME)] string name,
                CatalogueEntry star_data,
                [Out] out CelestialObject cel_obj);

            [DllImport(DLLNAME, EntryPoint = "app_planet", CallingConvention = CallingConvention.Cdecl)]
            public static extern short AppPlanet(
                double jd_tt,
                CelestialObject ss_body,
                Accuracy accuracy,
                [Out] out double ra,
                [Out] out double dec,
                [Out] out double dis);

            [DllImport(DLLNAME, EntryPoint = "geo_posvel", CallingConvention = CallingConvention.Cdecl)]
            public static extern short GeoPosVel(
                double jdtt, double deltaT, Accuracy accuracy, Observer observer,
                [In, Out][MarshalAs(UnmanagedType.LPArray, SizeConst = 3)] double[] pos,
                [In, Out][MarshalAs(UnmanagedType.LPArray, SizeConst = 3)] double[] vel);

            // Test-only imports (exported by NOVAS, not used by NINA.Astrometry)

            [DllImport(DLLNAME, EntryPoint = "equ2ecl_vec", CallingConvention = CallingConvention.Cdecl)]
            public static extern short Equ2EclVec(double jd_tt, short coord_sys, short accuracy, [In] double[] pos1, [In, Out] double[] pos2);

            [DllImport(DLLNAME, EntryPoint = "vector2radec", CallingConvention = CallingConvention.Cdecl)]
            public static extern short Vector2RaDec([In] double[] pos, out double ra, out double dec);

            [DllImport(DLLNAME, EntryPoint = "cio_location", CallingConvention = CallingConvention.Cdecl)]
            public static extern short CioLocation(double jd_tdb, short accuracy, out double ra_cio, out short ref_sys);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            public struct CatalogueEntry {

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SIZE_OF_OBJ_NAME)]
                public string StarName;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SIZE_OF_CAT_NAME)]
                public string Catalog;

                public int StarNumber;
                public double RA;
                public double Dec;
                public double ProMoRA;
                public double ProMoDec;
                public double Parallax;
                public double RadialVelocity;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            public struct CelestialObject {
                public short Type;
                public short Number;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SIZE_OF_OBJ_NAME)]
                public string Name;

                public CatalogueEntry Star;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct OnSurface {
                public double Latitude;
                public double Longitude;
                public double Height;
                public double Temperature;
                public double Pressure;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct InSpace {

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3, ArraySubType = UnmanagedType.R8)]
                public double[] ScPos;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3, ArraySubType = UnmanagedType.R8)]
                public double[] ScVel;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct Observer {
                public short Where;
                public OnSurface OnSurf;
                public InSpace NearEarth;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            public struct SkyPosition {

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3, ArraySubType = UnmanagedType.R8)]
                public double[] RHat;

                public double RA;
                public double Dec;
                public double Dis;
                public double RV;
            }

            public enum ObjectType : short {
                MajorPlanetSunOrMoon = 0,
                MinorPlanet = 1,
                ObjectLocatedOutsideSolarSystem = 2
            }

            public enum Body : short {
                Mercury = 1,
                Venus = 2,
                Earth = 3,
                Mars = 4,
                Jupiter = 5,
                Saturn = 6,
                Uranus = 7,
                Neptune = 8,
                Pluto = 9,
                Sun = 10,
                Moon = 11
            }

            public enum CoordinateSystem : short {
                GCRS = 0,
                EquinoxOfDate = 1,
                CIOOfDate = 2,
                Astrometric = 3
            }

            public enum GstType : short {
                GreenwichMeanSiderealTime = 0,
                GreenwichApparentSiderealTime = 1
            }

            public enum Method : short {
                CIOBased = 0,
                EquinoxBased = 1
            }

            public enum Accuracy : short {
                Full = 0,
                Reduced = 1
            }

            public enum RefractionOption : int {
                NoRefraction = 0,
                StandardRefraction = 1,
                LocationRefraction = 2
            }

            public enum SolarSystemOrigin : short {
                Barycenter = 0,
                SolarCenterOfMass = 1
            }
        }

        #endregion
    }
}
