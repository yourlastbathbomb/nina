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
using NINA.Core.Utility;
using NINA.Mac.Native;
using System.Globalization;
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

        [OneTimeSetUp]
        public void RegisterResolverForThisAssembly() {
            // Only for the Sofa class below; NINA.Astrometry registers itself
            NativeLibraries.Register(typeof(AstrometryEngineTest).Assembly);
        }

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
        /// without the ephemeris NOVAS does not fail: app_planet returns error 0 with NaN, so PlanetApparentCoordinates returns
        /// NaN coordinates (and AstroUtil.GetMoonPosition a wrong position). EngineData.Check catches that at startup.
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
            AssertAgreesWithNovasApparentPlace(raDegrees, decDegrees, jnow, instant);
        }

        /// <summary>
        /// The same comparison at fixed instants, through NINA's own clock hook (ICustomDateTime), so it is reproducible.
        /// The clock hands NINA local time (Hong Kong, UTC+8, on this machine), as the system clock does, so the local-to-UTC
        /// step is part of what is checked. The JNow result must also carry that instant and transform back to the J2000
        /// input: SOFA's iauAtic13 inverts iauAtci13 to far below 1 mas.
        /// </summary>
        [TestCase(83.82208333, -5.39111111, "2026-10-04T14:00:00Z", TestName = "J2000ToJNow_AtAFixedInstant_AgreesWithNovasApparentPlace(M42, 22:00 HKT 2026-10-04)")]
        [TestCase(37.95456067, 89.26410897, "2026-04-16T22:00:00Z", TestName = "J2000ToJNow_AtAFixedInstant_AgreesWithNovasApparentPlace(Polaris, 2026-04-16)")]
        [TestCase(201.29824736, -11.16131949, "2024-04-08T18:00:00Z", TestName = "J2000ToJNow_AtAFixedInstant_AgreesWithNovasApparentPlace(Spica, 2024-04-08)")]
        [TestCase(279.23473479, 38.78368896, "2035-08-01T12:00:00Z", TestName = "J2000ToJNow_AtAFixedInstant_AgreesWithNovasApparentPlace(Vega, 2035-08-01)")]
        public void J2000ToJNow_AtAFixedInstant_AgreesWithNovasApparentPlace(double raDegrees, double decDegrees, string utcText) {
            var utc = ParseUtc(utcText);
            var clock = new FixedClock(utc);
            var j2000 = new Coordinates(Angle.ByDegree(raDegrees), Angle.ByDegree(decDegrees), Epoch.J2000, clock);

            var jnow = j2000.Transform(Epoch.JNOW);
            var back = jnow.Transform(Epoch.J2000);

            AssertAgreesWithNovasApparentPlace(raDegrees, decDegrees, jnow, utc);
            jnow.DateTime.Should().BeSameAs(clock);
            var backRaMas = (AstroUtil.EuclidianModulus(back.RADegrees - raDegrees + 180.0, 360.0) - 180.0) * Math.Cos(decDegrees * Math.PI / 180.0) * MasPerDegree;
            var backDecMas = (back.Dec - decDegrees) * MasPerDegree;
            TestContext.Out.WriteLine($"JNow -> J2000 round trip: RA*cos(dec) {backRaMas:F6} mas, Dec {backDecMas:F6} mas");
            back.Epoch.Should().Be(Epoch.J2000);
            Math.Abs(backRaMas).Should().BeLessThan(0.1);
            Math.Abs(backDecMas).Should().BeLessThan(0.1);
        }

        private static void AssertAgreesWithNovasApparentPlace(double raDegrees, double decDegrees, Coordinates jnow, DateTime instant) {
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

        /// <summary>
        /// Local apparent sidereal time at the rig's site against SOFA's Greenwich apparent sidereal time (iauGst06a, the
        /// IAU 2006/2000A model, from libsofa.dylib) at the same UT1 and TT, plus the longitude. NINA computes it with NOVAS
        /// (sidereal_time, equinox-based, full accuracy, which implements the same IAU 2006 precession and IAU 2000A nutation),
        /// so this compares two independent implementations of one model: they agree to within a microsecond of time, and 1 ms
        /// (15 mas) is a generous bound. UT1 = TT - DeltaT with NINA's DeltaT, which the Meeus test above checks on its own.
        /// </summary>
        [TestCase("2000-01-01T12:00:00Z")]
        [TestCase("2024-04-08T18:00:00Z")]
        [TestCase("2026-10-04T14:00:00Z")]
        [TestCase("2035-08-01T12:00:00Z")]
        public void LocalSiderealTime_AtHongKong_AgreesWithSofaGst06a(string utcText) {
            const double longitude = 114.18;
            var utc = ParseUtc(utcText);

            var lst = AstroUtil.GetLocalSiderealTime(utc, longitude);

            var deltaT = AstroUtil.DeltaT(utc);
            var (tt1, tt2) = AstroUtil.GetJulianDateTTParts(utc);
            var gastRadians = Sofa.Gst06a(tt1, tt2 - deltaT / 86400.0, tt1, tt2);
            var expectedHours = AstroUtil.EuclidianModulus((gastRadians * 180.0 / Math.PI + longitude) / 15.0, 24.0);

            var differenceMilliseconds = (AstroUtil.EuclidianModulus(lst - expectedHours + 12.0, 24.0) - 12.0) * 3_600_000.0;
            TestContext.Out.WriteLine($"{utc:O}: DeltaT {deltaT:F3} s, NINA LST {lst:F10} h, SOFA GAST + lon {expectedHours:F10} h, difference {differenceMilliseconds:F6} ms");
            Math.Abs(differenceMilliseconds).Should().BeLessThan(1.0);
        }

        private static DateTime ParseUtc(string text) {
            return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }

        /// <summary>NINA's clock hook, frozen at one instant. Now is local time, as SystemDateTime returns it.</summary>
        private sealed class FixedClock : ICustomDateTime {
            private readonly DateTime utc;

            public FixedClock(DateTime utc) {
                this.utc = utc;
            }

            public DateTime Now => utc.ToLocalTime();
            public DateTime UtcNow => utc;
        }

        /// <summary>SOFA functions NINA.Astrometry/SOFA.cs does not import, from the same libsofa.dylib.</summary>
        private static class Sofa {
            [DllImport("SOFA_2023_10_11.dll", EntryPoint = "iauGst06a", CallingConvention = CallingConvention.Cdecl)]
            public static extern double Gst06a(double uta, double utb, double tta, double ttb);
        }
    }
}
