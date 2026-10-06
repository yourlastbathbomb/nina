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
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Mac.Image.Test;
using NINA.Mac.Sequencer.Test.Sim;
using NINA.Mac.Sequencing.FieldRotation;
using NINA.Mac.Sequencing.Planning;
using NINA.Sequencer.Container;
using System.Collections.Concurrent;
using System.Globalization;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// Plan M7's "done when": a simulated night, generated from the Target form, runs end to end through NINA's own Sequencer
    /// and the headless engine against the simulated camera, mount and plate solver; writes correctly named FITS files through
    /// NINA's file patterns into a temp folder; and stops at the horizon, at the maximum altitude and at dawn. NINA's conditions
    /// read the real clock, so the targets are placed relative to now and each run takes 30-60 s. The stop tests check the end of
    /// the imaging block against the moment the sky crossed the limit, computed independently with NINA.Astrometry's transform.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class SimulatedNightTest {

        private static TargetPlan Target(string name, Coordinates coordinates, double exposure = 2, int? count = null) => new TargetPlan {
            Name = name,
            Coordinates = coordinates,
            ExposureSeconds = exposure,
            Count = count,
            DitherEvery = 0,
            RecenterArcmin = 0,
            CenterFirst = false
        };

        private static NightPlan Night(string name, params TargetPlan[] targets) => new NightPlan {
            Name = name,
            CoolToC = null,
            WarmAtEnd = false,
            DewHeater = false,
            Targets = targets
        };

        /// <summary>
        /// Angular separation in arcseconds by the haversine formula, which stays exact near zero; NINA's
        /// <c>(a - b).Distance</c> goes through an arc cosine and returns NaN for (numerically) identical coordinates.
        /// </summary>
        private static double SeparationArcsec(Coordinates a, Coordinates b) {
            a = a.Transform(Epoch.J2000);
            b = b.Transform(Epoch.J2000);
            var dDec = AstroUtil.ToRadians(b.Dec - a.Dec);
            var dRa = AstroUtil.ToRadians(b.RADegrees - a.RADegrees);
            var h = Math.Pow(Math.Sin(dDec / 2), 2) + Math.Cos(AstroUtil.ToRadians(a.Dec)) * Math.Cos(AstroUtil.ToRadians(b.Dec)) * Math.Pow(Math.Sin(dRa / 2), 2);
            return AstroUtil.ToDegree(2 * Math.Asin(Math.Min(1, Math.Sqrt(h)))) * 3600;
        }

        private static void Log(SimRig rig, NightRun run) {
            TestContext.Out.WriteLine($"run {run.Start:HH:mm:ss.fff} - {run.End:HH:mm:ss.fff}; issues: {string.Join(" | ", run.Issues)}");
            foreach (var (type, seconds, start) in rig.Camera.ExposureLog) {
                TestContext.Out.WriteLine($"  exposure {type} {seconds} s at {start:HH:mm:ss.fff}");
            }
            TestContext.Out.WriteLine($"  slews {rig.Mount.SlewTargets.Count}, syncs {rig.Mount.SyncTargets.Count}, pulses {rig.Mount.Pulses.Count}, parks {rig.Mount.Parks}, unparks {rig.Mount.Unparks}, aborts {rig.Camera.Aborts}");
        }

        [Test]
        public async Task FullNight_CoolsCentresImagesDithersEveryTwo_SavesNamedFiles_WarmsAndParks() {
            var now = DateTime.Now;
            // Rising in the east at 40 degrees, far from every limit; the night ends in an hour
            var coordinates = Sky.TargetAt(40, rising: true, decDeg: 0, now);
            await using var rig = await SimRig.Create("full night", coordinates, gotoErrorRaArcmin: 12, gotoErrorDecArcmin: -7,
                nighttime: new FixedNighttimeCalculator(now.AddHours(1)));
            var plan = new NightPlan {
                Name = "Full night",
                CoolToC = 0,
                CoolMinutes = 0,
                WarmAtEnd = true,
                WarmMinutes = 0,
                DewHeater = true,
                Targets = new[] {
                    new TargetPlan { Name = "NGC 0001 test", Coordinates = coordinates, ExposureSeconds = 2, Count = 6, DitherEvery = 2, RecenterArcmin = 0 }
                }
            };

            // Where the mount really points when the first light starts: after centring, before the first dither moves it
            Coordinates? atFirstLight = null;
            var solverHook = rig.Camera.ExposureStarted;
            rig.Camera.ExposureStarted = (sequence, start) => {
                if (sequence.ImageType == "LIGHT") {
                    Interlocked.CompareExchange(ref atFirstLight, rig.Mount.True, null);
                }
                solverHook?.Invoke(sequence, start);
            };

            // The generated tree goes through NINA's JSON first: the loaded copy is what runs
            var generated = rig.Host.Generator.Generate(plan);
            var json = rig.Host.Json.Serialize(generated);
            var loaded = (ISequenceRootContainer)rig.Host.Json.Deserialize(json);
            SequenceJson.WithoutInheritedCoordinates(rig.Host.Json.Serialize(loaded)).Should().Be(SequenceJson.WithoutInheritedCoordinates(json));

            var run = await NightRun.Run(loaded, TimeSpan.FromMinutes(3));
            var files = await rig.WaitForFiles(6, TimeSpan.FromSeconds(20));
            Log(rig, run);

            run.Issues.Should().BeEmpty();
            // Start area: dew heater on, cooled to 0 C, unparked; end area: warmed (cooler off), dew heater off, parked
            rig.Camera.CoolerWrites.Should().Equal(true, false);
            rig.Camera.DewHeaterOn.Should().BeFalse();
            rig.Mount.Unparks.Should().Be(1);
            rig.Mount.Parks.Should().Be(1);
            rig.Mount.MeridianFlipRequests.Should().Be(0);

            // Center: the 13.9' goto error was measured by a solve, synced out, and the re-slew landed on the target
            rig.Solver.Solver.Calls.Should().Be(2);
            rig.Mount.SyncTargets.Should().HaveCount(1);
            rig.Mount.SlewTargets.Should().HaveCount(2);
            // Measured before the dithers, which are random (NINA's DitherOffsetSelector draws Gaussian offsets, sigma = DitherPixels)
            atFirstLight.Should().NotBeNull();
            var target = coordinates.Transform(Epoch.J2000);
            SeparationArcsec(atFirstLight!, target).Should().BeLessThan(1, "after the sync and the re-slew the mount points at the target");
            var exposures = rig.Camera.ExposureLog.ToList();
            exposures.Where(e => e.ImageType == "SNAPSHOT").Should().HaveCount(2, "Center took one solve frame before and one after the correction");
            var lights = exposures.Where(e => e.ImageType == "LIGHT").ToList();
            lights.Should().HaveCount(6);
            lights.Should().OnlyContain(e => e.Seconds == 2);

            // Dither every 2 lights: before lights 3 and 5, one east/west and one north/south pulse each
            var pulses = rig.Mount.Pulses.ToList();
            pulses.Should().HaveCount(4);
            var pulseTimes = pulses.Select(p => p.At.ToLocalTime()).ToList();
            pulseTimes.Take(2).Should().OnlyContain(t => t > lights[1].Start && t < lights[2].Start);
            pulseTimes.Skip(2).Should().OnlyContain(t => t > lights[3].Start && t < lights[4].Start);
            // After the first light only the dithers moved the mount: its true pointing moved by exactly the sum of the pulses
            // (the simulated mount guides at 7.5205"/s on both axes; east is +RA, north +Dec)
            double east = 0, north = 0;
            foreach (var (direction, milliseconds, _) in pulses) {
                var arcsec = rig.Mount.GuideRateDeclinationArcsecPerSec * milliseconds / 1000.0;
                switch (direction) {
                    case GuideDirections.guideEast: east += arcsec; break;
                    case GuideDirections.guideWest: east -= arcsec; break;
                    case GuideDirections.guideNorth: north += arcsec; break;
                    case GuideDirections.guideSouth: north -= arcsec; break;
                }
            }
            var final = rig.Mount.True;
            var movedEast = AstroUtil.EuclidianModulus(final.RADegrees - atFirstLight!.RADegrees + 180, 360) - 180;
            TestContext.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "centring error {0:0.000}\", dither sum east {1:0.00}\" north {2:0.00}\", final error {3:0.00}\"",
                SeparationArcsec(atFirstLight, target), east, north, SeparationArcsec(final, target)));
            (movedEast * Math.Cos(AstroUtil.ToRadians(atFirstLight.Dec)) * 3600).Should().BeApproximately(east, 0.01, "east/west: the sum of the dither pulses");
            ((final.Dec - atFirstLight.Dec) * 3600).Should().BeApproximately(north, 0.01, "north/south: the sum of the dither pulses");

            // Files: the profile's pattern $$TARGETNAME$$\$$IMAGETYPE$$\$$TARGETNAME$$_$$EXPOSURETIME$$s_G$$GAIN$$_$$FRAMENR$$
            files.Should().Equal(Enumerable.Range(0, 6).Select(i => $"NGC 0001 test/LIGHT/NGC 0001 test_2.00s_G252_{i:0000}.fits"));
            foreach (var file in files) {
                var fits = FitsFile.Read(Path.Combine(rig.ImageFolder, file));
                fits.Width.Should().Be(1920);
                fits.Height.Should().Be(1080);
                fits.Value("IMAGETYP").Should().Be("LIGHT");
                fits.Value("OBJECT").Should().Be("NGC 0001 test");
                fits.Double("EXPTIME").Should().Be(2);
                fits.Integer("XBINNING").Should().Be(2);
                fits.Integer("GAIN").Should().Be(252);
                fits.Integer("OFFSET").Should().Be(15);
                fits.Value("BAYERPAT").Should().Be("RGGB");
                fits.Double("FOCALLEN").Should().Be(2500);
                fits.Double("RA").Should().BeApproximately(coordinates.RADegrees, 1.0 / 60, "the mount's reported position after centring");
                fits.Double("DEC").Should().BeApproximately(coordinates.Dec, 1.0 / 60);
            }
        }

        [Test]
        public async Task MaxAltitudeStop_EndsTheTargetWithinOneWatchdogPeriod_OfClimbingAboveTheLimit() {
            var now = DateTime.Now;
            // Dec +10 transits at 77.75 degrees here: a target climbing towards the zenith keyhole
            var coordinates = Sky.TargetAt(60, rising: true, decDeg: 10, now);
            await using var rig = await SimRig.Create("max altitude", coordinates, gotoErrorRaArcmin: 0, gotoErrorDecArcmin: 0,
                nighttime: new FixedNighttimeCalculator(now.AddHours(1)));
            var start = DateTime.Now;
            var limit = Math.Round(Sky.Altitude(coordinates, start.AddSeconds(30)), 2);
            var crossing = Sky.FirstTime(coordinates, altitude => altitude > limit, start, TimeSpan.FromMinutes(2));
            var target = Target("Keyhole test", coordinates) with { MaxAltitudeDeg = limit };
            var root = rig.Host.Generator.Generate(Night("Max altitude", target));

            var run = await NightRun.Run(root, TimeSpan.FromMinutes(2));
            Log(rig, run);
            var imagingEnd = run.Finished("Imaging Keyhole test 2s")!.Value;
            TestContext.Out.WriteLine($"limit {limit:0.0000} deg, crossing {crossing:HH:mm:ss.fff}, imaging ended {imagingEnd:HH:mm:ss.fff}");

            run.Issues.Should().BeEmpty();
            var lights = rig.Camera.ExposureLog.Where(e => e.ImageType == "LIGHT").ToList();
            lights.Should().HaveCountGreaterThan(5);
            lights.Should().OnlyContain(e => e.Start < crossing, "no exposure starts once the target is above the limit");
            imagingEnd.Should().BeOnOrAfter(crossing.AddMilliseconds(-200), "the block runs until the target is above the limit");
            imagingEnd.Should().BeBefore(crossing.AddSeconds(6.5), "the condition's 5 s watchdog interrupts the running exposure");
            rig.Mount.Parks.Should().Be(1, "the end area still runs");
        }

        [Test]
        public async Task KeyholeWait_ImagesEast_WaitsOutTheKeyhole_ThenImagesWest_WithOneExposureCount() {
            var now = DateTime.Now;
            // Dec 0 crosses the meridian (67.75 degrees) 40 s from now. The limit is the altitude 15 s before transit, so the target is
            // above it, in the "keyhole", for about 30 s around transit: the real night's hour, compressed into seconds
            var transit = now.AddSeconds(40);
            var coordinates = Sky.TransitingAt(0, transit);
            var limit = Sky.Altitude(coordinates, transit.AddSeconds(-15));
            // WaitForAltitude compares the unrounded altitude with its limit
            var keyholeStart = Sky.FirstTimeExact(coordinates, altitude => altitude > limit, now, TimeSpan.FromSeconds(60));
            var keyholeEnd = Sky.FirstTimeExact(coordinates, altitude => altitude <= limit, transit, TimeSpan.FromSeconds(60));
            await using var rig = await SimRig.Create("keyhole wait", coordinates, gotoErrorRaArcmin: 0, gotoErrorDecArcmin: 0,
                nighttime: new FixedNighttimeCalculator(now.AddHours(1)));
            // Up to 12 of the 2 s subs fit before the keyhole, so 20 always leave several for the west side
            var target = Target("Keyhole wait test", coordinates, exposure: 2, count: 20) with { MaxAltitudeDeg = limit, Keyhole = KeyholePolicy.WaitUntilBelow };
            var root = rig.Host.Generator.Generate(Night("Keyhole wait", target));
            // When each slew ended (the simulated mount reports every pointing change)
            var slewEnds = new ConcurrentQueue<DateTime>();
            var slewsSeen = 0;
            rig.Mount.PointingChanged = () => {
                var slews = rig.Mount.SlewTargets.Count;
                if (Interlocked.Exchange(ref slewsSeen, slews) < slews) {
                    slewEnds.Enqueue(DateTime.Now);
                }
            };

            var run = await NightRun.Run(root, TimeSpan.FromMinutes(3));
            Log(rig, run);
            TestContext.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "limit {0:0.000000} deg, keyhole {1:HH:mm:ss.fff} - {2:HH:mm:ss.fff}, transit {3:HH:mm:ss.fff} at {4:0.000000} deg",
                limit, keyholeStart, keyholeEnd, transit, Sky.Altitude(coordinates, transit)));

            run.Issues.Should().BeEmpty();
            var lights = rig.Camera.ExposureLog.Where(e => e.ImageType == "LIGHT").ToList();
            lights.Should().HaveCount(20, "one LoopCondition counts the exposures on both sides of the meridian");
            var east = lights.Where(e => e.Start < keyholeStart.AddMilliseconds(100)).ToList();
            var west = lights.Where(e => e.Start > keyholeEnd.AddMilliseconds(-100)).ToList();
            (east.Count + west.Count).Should().Be(20, "no exposure starts while the target is above the limit");
            east.Should().HaveCountGreaterThan(3, "the target is imaged east of the meridian until it climbs into the keyhole");
            west.Should().HaveCountGreaterThan(3, "and west of it once it has sunk below the limit again");
            // The mount tracked through the keyhole, so the keyhole trigger re-slews (centres, when centring is on) after the wait
            // (review M7-3): one slew in Prepare, one after the keyhole, before the first west light
            rig.Mount.SlewTargets.Should().HaveCount(2, "Prepare's slew, and the re-slew after the keyhole; the block was held, not restarted");
            var reslew = slewEnds.Last();
            TestContext.Out.WriteLine($"re-slew ended {reslew:HH:mm:ss.fff}, first west light {west.First().Start:HH:mm:ss.fff}");
            reslew.Should().BeAfter(keyholeEnd.AddMilliseconds(-100), "the re-slew comes after the wait");
            reslew.Should().BeOnOrBefore(west.First().Start, "and before the first light west of the meridian");
            SeparationArcsec(rig.Mount.ReportedJ2000, coordinates).Should().BeLessThan(1, "the re-slew went to the target");
            west.First().Start.Should().BeBefore(keyholeEnd.AddSeconds(2.5), "WaitForAltitude checks the altitude once a second, then the re-slew (0.3 s in the simulator) and NINA's wait for the next mount update after it (0.5 s polling)");
            rig.Mount.Parks.Should().Be(1, "the end area still runs");
        }

        [Test]
        public async Task FieldRotationStop_IsJudgedAtTheLightsBinning_NotAtThePlateSolveBinningCenterLeftTheCameraAt() {
            // Review M7-1: Center's solve frames use NINA's default PlateSolveSettings.Binning = 1, which leaves the camera at bin 1.
            // A Stop predicate on the camera-binning symbol then saw the bin-1 limit (half the bin-2 one) and skipped the whole block
            var now = DateTime.Now;
            // Dec 0 rising at 62 degrees (azimuth about 140): the 1 px limit is about 8.2 s at bin 2 and 4.1 s at bin 1
            var coordinates = Sky.TargetAt(62, rising: true, decDeg: 0, now);
            var azimuth = Sky.Azimuth(coordinates, now);
            var bin2 = FieldRotationSymbols.MaxSub(Sky.Latitude, 62, azimuth, 3840, 2160, 2, 1.0);
            var bin1 = FieldRotationSymbols.MaxSub(Sky.Latitude, 62, azimuth, 3840, 2160, 1, 1.0);
            TestContext.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "azimuth {0:0.0}: max sub {1:0.00} s at bin 2, {2:0.00} s at bin 1", azimuth, bin2, bin1));
            const double sub = 6.2;
            bin1.Should().BeLessThan(sub, "the case discriminates: at the solve binning the sub is too long");
            bin2.Should().BeGreaterThan(sub + 1, "at the lights' binning it fits, with margin for the target's motion during the run");
            await using var rig = await SimRig.Create("rotation binning", coordinates, gotoErrorRaArcmin: 3, gotoErrorDecArcmin: 2,
                nighttime: new FixedNighttimeCalculator(now.AddHours(1)), configure: p => p.PlateSolveSettings.Binning = 1);
            var target = Target("Rotation binning test", coordinates, exposure: sub, count: 2) with { CenterFirst = true, FieldRotation = FieldRotationPolicy.Stop };
            var root = rig.Host.Generator.Generate(Night("Rotation binning", target));

            var run = await NightRun.Run(root, TimeSpan.FromMinutes(2));
            Log(rig, run);

            run.Issues.Should().BeEmpty();
            rig.Camera.ExposureLog.Should().Contain(e => e.ImageType == "SNAPSHOT", "Center took its solve frames (at bin 1)");
            rig.Camera.ExposureLog.Where(e => e.ImageType == "LIGHT").Should().HaveCount(2, "the lights fit the field-rotation limit at their own binning, 2");
        }

        [Test]
        public async Task HorizonStop_EndsTheTargetWithinOneWatchdogPeriod_OfSettingBelowTheCustomHorizon() {
            var now = DateTime.Now;
            // A flat 20 degree custom horizon; the target sets through it about 30 s from now
            var coordinates = Sky.TargetAt(20.15, rising: false, decDeg: -10, now);
            await using var rig = await SimRig.Create("horizon", coordinates, gotoErrorRaArcmin: 0, gotoErrorDecArcmin: 0,
                nighttime: new FixedNighttimeCalculator(now.AddHours(1)),
                configure: p => p.AstrometrySettings.Horizon = CustomHorizon("0 20\n90 20\n180 20\n270 20\n"));
            var start = DateTime.Now;
            var crossing = Sky.FirstTime(coordinates, altitude => altitude < 20, start, TimeSpan.FromMinutes(2));
            var root = rig.Host.Generator.Generate(Night("Horizon", Target("Horizon test", coordinates)));

            var run = await NightRun.Run(root, TimeSpan.FromMinutes(2));
            Log(rig, run);
            var imagingEnd = run.Finished("Imaging Horizon test 2s")!.Value;
            TestContext.Out.WriteLine($"crossing {crossing:HH:mm:ss.fff}, imaging ended {imagingEnd:HH:mm:ss.fff}");

            run.Issues.Should().BeEmpty();
            var lights = rig.Camera.ExposureLog.Where(e => e.ImageType == "LIGHT").ToList();
            lights.Should().HaveCountGreaterThan(5);
            lights.Should().OnlyContain(e => e.Start < crossing.AddMilliseconds(100));
            imagingEnd.Should().BeOnOrAfter(crossing.AddMilliseconds(-200));
            imagingEnd.Should().BeBefore(crossing.AddSeconds(6.5));
        }

        [Test]
        public async Task DawnStop_EndsTheNightAtAstronomicalDawn_WithoutStartingAnExposureThatWouldRunPastIt() {
            var now = DateTime.Now;
            var coordinates = Sky.TargetAt(45, rising: true, decDeg: 0, now);
            // NINA's DawnProvider fires 25 s from now: the night calculator is the only stand-in, the provider and TimeCondition are NINA's
            var dawn = DateTime.Now.AddSeconds(25);
            dawn = dawn.AddTicks(-(dawn.Ticks % TimeSpan.TicksPerSecond)); // TimeCondition keeps whole seconds
            await using var rig = await SimRig.Create("dawn", coordinates, gotoErrorRaArcmin: 0, gotoErrorDecArcmin: 0,
                nighttime: new FixedNighttimeCalculator(dawn));
            var root = rig.Host.Generator.Generate(Night("Dawn", Target("Dawn test", coordinates)));

            var run = await NightRun.Run(root, TimeSpan.FromMinutes(2));
            Log(rig, run);
            var imagingEnd = run.Finished("Imaging Dawn test 2s")!.Value;
            TestContext.Out.WriteLine($"dawn {dawn:HH:mm:ss.fff}, imaging ended {imagingEnd:HH:mm:ss.fff}");

            run.Issues.Should().BeEmpty();
            var lights = rig.Camera.ExposureLog.Where(e => e.ImageType == "LIGHT").ToList();
            lights.Should().HaveCountGreaterThan(5);
            lights.Should().OnlyContain(e => e.Start.AddSeconds(e.Seconds) <= dawn.AddMilliseconds(500), "TimeCondition does not start an exposure that would end after dawn");
            imagingEnd.Should().BeAfter(dawn.AddSeconds(-2.5));
            imagingEnd.Should().BeBefore(dawn.AddSeconds(1.5), "the time watchdog checks every second");
            rig.Mount.Parks.Should().Be(1);
        }

        [Test]
        public async Task DriftCentring_SolvesEveryOtherFrame_AndRecentresOnceTheDriftExceedsTheThreshold() {
            var now = DateTime.Now;
            var coordinates = Sky.TargetAt(45, rising: true, decDeg: 0, now);
            // The centring threshold must be below the drift threshold, or Center finds the drifted frame good enough
            await using var rig = await SimRig.Create("drift", coordinates, gotoErrorRaArcmin: 6, gotoErrorDecArcmin: 4,
                nighttime: new FixedNighttimeCalculator(now.AddHours(1)), configure: p => p.PlateSolveSettings.Threshold = 0.3);
            var target = Target("Drift test", coordinates, exposure: 2, count: 14) with { CenterFirst = true, RecenterArcmin = 0.5, RecenterEvery = 2 };
            var root = rig.Host.Generator.Generate(Night("Drift", target));
            // Once centred, the mount's tracking drifts by 2"/s in RA (the true pointing walks off while the reported one stays): 0.5' after 15 s
            rig.Mount.PointingChanged = () => {
                if (!rig.Mount.SyncTargets.IsEmpty && rig.Mount.DriftRaArcsecPerSec == 0) {
                    rig.Mount.DriftRaArcsecPerSec = 2;
                }
            };

            var run = await NightRun.Run(root, TimeSpan.FromMinutes(3));
            Log(rig, run);
            TestContext.Out.WriteLine($"fake ASTAP calls: {rig.Solver.AstapCalls}; simulated solver calls: {rig.Solver.Solver.Calls}");
            foreach (var line in File.Exists(rig.Solver.ArgumentLog) ? File.ReadAllLines(rig.Solver.ArgumentLog) : Array.Empty<string>()) {
                TestContext.Out.WriteLine("  astap " + line);
            }

            run.Issues.Should().BeEmpty();
            rig.Solver.AstapCalls.Should().BeGreaterThan(1, "the drift check plate-solves every second light frame through NINA's ASTAPSolver");
            File.ReadAllLines(rig.Solver.ArgumentLog).Should().OnlyContain(l => l.Contains("-fov 0.14356") && l.Contains("-z 2"), "the rig's field and downsampling reach ASTAP");
            rig.Solver.Solver.Calls.Should().BeGreaterThan(2, "CenterAfterDrift ran NINA's Center again (through the host's solver factory)");
            rig.Mount.SyncTargets.Should().HaveCountGreaterThan(1, "the recentre measured the drift and synced it out");
            rig.Mount.SlewTargets.Should().HaveCountGreaterThan(2);
            rig.Camera.ExposureLog.Count(e => e.ImageType == "LIGHT").Should().Be(14);
        }

        private static NINA.Core.Model.CustomHorizon CustomHorizon(string text) {
            using var reader = new StringReader(text);
            return NINA.Core.Model.CustomHorizon.FromReader_Standard(reader);
        }
    }
}
