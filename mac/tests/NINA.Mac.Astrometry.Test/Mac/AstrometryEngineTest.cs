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
using NINA.Astrometry;
using NINA.Mac.Native;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NINA.Mac.Astrometry.Test {

    /// <summary>
    /// The macOS build of upstream NINA.Astrometry (mac/src/NINA.Astrometry.Mac): what the engine needs on top of the
    /// linked upstream fixtures. The upstream fixtures already cover SOFA/NOVAS-backed results against their own expected
    /// values; these tests cover the mac plumbing (resolver, ephemeris path) and cross-check the J2000 -> JNow and sidereal
    /// time paths against independent references.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class AstrometryEngineTest {

        private const double MasPerDegree = 3_600_000.0;

        [Test]
        public void NinaAstrometry_IsTheMacBuildOfUpstream() {
            var assembly = typeof(Coordinates).Assembly;
            assembly.GetName().Name.Should().Be("NINA.Astrometry");
            assembly.GetName().Version!.ToString().Should().StartWith("3.3.0.");
            RuntimeInformation.ProcessArchitecture.Should().Be(Architecture.Arm64);
            // No WPF assembly is referenced; the WPF types it names come from NINA.Mac.WpfCompat
            assembly.GetReferencedAssemblies().Select(a => a.Name).Should()
                .Contain("NINA.Mac.WpfCompat").And.NotContain(new[] { "WindowsBase", "PresentationCore", "PresentationFramework", "System.Xaml" });
        }

        /// <summary>
        /// NINA.Astrometry installs NINA.Mac.Native's resolver for itself (module initializer, Mac/NativeRegistration.cs).
        /// Nothing in this test registers it: if the module initializer were missing, SOFA_2023_10_11.dll would not resolve.
        /// </summary>
        [Test]
        public void SofaAndNovas_ResolveThroughTheResolverThatNinaAstrometryInstallsItself() {
            SOFA.Anp(-1.0).Should().BeApproximately(2.0 * Math.PI - 1.0, 1e-15);
            NOVAS.JulianDate(2000, 1, 1, 12.0).Should().Be(2451545.0);
            NativeLibraries.Locate("SOFA_2023_10_11.dll").Should().Be(Path.Combine(AppContext.BaseDirectory, "libsofa.dylib"));
            NativeLibraries.Locate("NOVAS31lib.dll").Should().Be(Path.Combine(AppContext.BaseDirectory, "libnovas31.dylib"));
        }

        /// <summary>
        /// NOVAS.cs opens External/JPLEPH next to the assembly in its static constructor. A planet place needs it:
        /// without the ephemeris app_planet fails and PlanetApparentCoordinates throws.
        /// </summary>
        [Test]
        public void Novas_OpensJplephFromTheOutputFolder() {
            NOVAS.EphemerisLocation.Should().Be(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "External", "JPLEPH"));
            File.Exists(NOVAS.EphemerisLocation).Should().BeTrue("NINA.Mac.Native copies mac/native/ephemeris/JPLEPH there");

            var moon = NOVAS.PlanetApparentCoordinates(2460950.5, NOVAS.Body.Moon);

            moon.RADegrees.Should().BeInRange(0.0, 360.0);
            moon.Dec.Should().BeInRange(-30.0, 30.0);
        }

        /// <summary>
        /// J2000 -> JNow (Coordinates.Transform, SOFA iauAtci13 minus the equation of the origins) against NOVAS's
        /// equinox-of-date apparent place (place(), coordinate system 1, full accuracy, JPL DE421) for the same instant.
        /// They are two independent implementations of the IAU 2006/2000A apparent place, so they agree to better than
        /// 1 mas (observed: below 0.01 mas); a marshalling, time-scale or ABI fault would show up as arcseconds or more.
        /// The shift being checked is about 20 arcminutes (26 years of precession plus nutation and aberration).
        /// </summary>
        [TestCase(83.82208333, -5.39111111, TestName = "J2000ToJNow_AgreesWithNovasApparentPlace(M42)")]
        [TestCase(37.95456067, 89.26410897, TestName = "J2000ToJNow_AgreesWithNovasApparentPlace(Polaris)")]
        [TestCase(201.29824736, -11.16131949, TestName = "J2000ToJNow_AgreesWithNovasApparentPlace(Spica)")]
        [TestCase(0.0, 0.0, TestName = "J2000ToJNow_AgreesWithNovasApparentPlace(RA0Dec0)")]
        [TestCase(279.23473479, 38.78368896, TestName = "J2000ToJNow_AgreesWithNovasApparentPlace(Vega)")]
        public void J2000ToJNow_AgreesWithNovasApparentPlace(double raDegrees, double decDegrees) {
            var j2000 = new Coordinates(Angle.ByDegree(raDegrees), Angle.ByDegree(decDegrees), Epoch.J2000);

            var before = DateTime.Now;
            var jnow = j2000.Transform(Epoch.JNOW);
            var after = DateTime.Now;
            (after - before).Should().BeLessThan(TimeSpan.FromSeconds(5), "precession over the bracket must stay negligible");

            var instant = before + TimeSpan.FromTicks((after - before).Ticks / 2);
            var jdTt = AstroUtil.GetJulianDateTT(instant);
            var star = new NOVAS.CelestialObject {
                Type = (short)NOVAS.ObjectType.ObjectLocatedOutsideSolarSystem,
                Number = 0,
                Name = "TEST",
                Star = new NOVAS.CatalogueEntry { StarName = "TEST", Catalog = "TST", StarNumber = 0, RA = raDegrees / 15.0, Dec = decDegrees }
            };
            var geocenter = new NOVAS.Observer { Where = (short)NOVAS.ObserverLocation.EarthGeoCenter, NearEarth = new NOVAS.InSpace { ScPos = new double[3], ScVel = new double[3] } };
            var position = new NOVAS.SkyPosition { RHat = new double[3] };
            NOVAS.Place(jdTt, star, geocenter, AstroUtil.DeltaT(instant), NOVAS.CoordinateSystem.EquinoxOfDate, NOVAS.Accuracy.Full, ref position)
                .Should().Be(0);

            jnow.Epoch.Should().Be(Epoch.JNOW);
            var deltaRaMas = (AstroUtil.EuclidianModulus(jnow.RADegrees - position.RA * 15.0 + 180.0, 360.0) - 180.0) * Math.Cos(decDegrees * Math.PI / 180.0) * MasPerDegree;
            var deltaDecMas = (jnow.Dec - position.Dec) * MasPerDegree;
            // Coordinates' own '-' operator first converts both sides to one epoch, so measure the raw great-circle shift here
            var shiftArcMinutes = SeparationDegrees(raDegrees, decDegrees, jnow.RADegrees, jnow.Dec) * 60.0;
            TestContext.Out.WriteLine($"JNow at {instant:O}: SOFA {jnow.RADegrees:F9} {jnow.Dec:F9}, NOVAS {position.RA * 15.0:F9} {position.Dec:F9}, "
                + $"diff RA*cos(dec) {deltaRaMas:F3} mas, Dec {deltaDecMas:F3} mas; shift from J2000 {shiftArcMinutes:F2} arcmin");
            Math.Abs(deltaRaMas).Should().BeLessThan(1.0);
            Math.Abs(deltaDecMas).Should().BeLessThan(1.0);
            shiftArcMinutes.Should().BeGreaterThan(5.0, "the transform must actually move the coordinates to the equinox of date");
        }

        private static double SeparationDegrees(double ra1, double dec1, double ra2, double dec2) {
            double ToRad(double d) => d * Math.PI / 180.0;
            var h = Math.Pow(Math.Sin(ToRad(dec2 - dec1) / 2.0), 2) + Math.Cos(ToRad(dec1)) * Math.Cos(ToRad(dec2)) * Math.Pow(Math.Sin(ToRad(ra2 - ra1) / 2.0), 2);
            return 2.0 * Math.Asin(Math.Sqrt(h)) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Local apparent sidereal time at the rig's site (Hong Kong, 114.18 E) against the IAU 1982 GMST polynomial
        /// (Meeus, Astronomical Algorithms, eq. 12.4) plus the longitude. NINA returns apparent sidereal time with UT1 from the
        /// database, so the two differ by the equation of the equinoxes (at most about 1.2 s) plus UT1 - UTC (below 0.9 s).
        /// This catches a sign, time-zone (UTC+8) or longitude-unit error, which would be minutes to hours. The same instant as
        /// UTC and as local time must give the same value.
        /// </summary>
        [TestCase(2026, 10, 4, 12, 0, 0)]
        [TestCase(2026, 12, 31, 18, 30, 0)]
        [TestCase(2027, 3, 15, 2, 15, 30)]
        public void LocalSiderealTime_AtHongKong_AgreesWithMeeusGmst(int year, int month, int day, int hour, int minute, int second) {
            const double longitude = 114.18;
            var utc = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);

            var lst = AstroUtil.GetLocalSiderealTime(utc, longitude);
            var lstFromLocal = AstroUtil.GetLocalSiderealTime(utc.ToLocalTime(), longitude);

            var jd = 2451545.0 + (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
            var t = (jd - 2451545.0) / 36525.0;
            var gmstDegrees = 280.46061837 + 360.98564736629 * (jd - 2451545.0) + 0.000387933 * t * t - t * t * t / 38710000.0;
            var expectedHours = AstroUtil.EuclidianModulus((gmstDegrees + longitude) / 15.0, 24.0);

            var differenceSeconds = (AstroUtil.EuclidianModulus(lst - expectedHours + 12.0, 24.0) - 12.0) * 3600.0;
            TestContext.Out.WriteLine($"{utc:O}: NINA LST {lst:F8} h, Meeus GMST + lon {expectedHours:F8} h, difference {differenceSeconds:F3} s");
            Math.Abs(differenceSeconds).Should().BeLessThan(2.1);
            lstFromLocal.Should().Be(lst);
        }
    }
}
