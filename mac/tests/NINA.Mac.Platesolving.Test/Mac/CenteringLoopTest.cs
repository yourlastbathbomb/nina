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
using Moq;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>
    /// NINA's centring loop (CenteringSolver, the code behind the sequencer's Center and CenterAfterDrift) against a simulated
    /// mount and a simulated solver, with everything between them real: CenteringSolver creates its own CaptureSolver, which
    /// captures through the imaging mediator, renders the frame (NINA.Image, thumbnail included) and solves through ImageSolver,
    /// with its blind failover. The mount reports JNow, as the LX200GPS does, and carries a pointing error the loop has to remove;
    /// the solver reports where the mount really points. Numbers are this rig's: 1' threshold, a 15' x 8.6' field.
    /// </summary>
    [TestFixture]
    public class CenteringLoopTest {

        private static readonly Coordinates M51 = SolverKit.Coordinates(202.4696, 47.1952);

        [Test]
        public async Task SyncAccepted_CentresAfterOneCorrection() {
            var rig = new Rig(errorRaArcmin: 12, errorDecArcmin: -7);

            var result = await rig.Center(noSync: false);

            result.Success.Should().BeTrue();
            result.Attempts.Should().HaveCount(2);
            rig.Mount.Syncs.Should().Be(1);
            rig.Mount.Slews.Should().Be(1);
            rig.Solver.Calls.Should().Be(2);
            rig.Captures.Should().Be(2);
            rig.TrueErrorArcmin().Should().BeLessThan(0.01);
            result.Separation.Distance.ArcMinutes.Should().BeLessThan(1);
            // The first solve measured the 13.9' goto error
            result.Attempts.First().PlateSolveResult.Separation.Distance.ArcMinutes.Should().BeApproximately(Math.Sqrt(12 * 12 + 7 * 7), 0.05);
        }

        [Test]
        public async Task NoSync_UsesTheMeasuredCorrection_AndCentres() {
            var rig = new Rig(errorRaArcmin: 12, errorDecArcmin: -7);
            var expected = rig.PredictMeasuredCorrectionResidualArcmin();

            var result = await rig.Center(noSync: true);

            result.Success.Should().BeTrue();
            result.Attempts.Should().HaveCount(2);
            rig.Mount.Syncs.Should().Be(0);
            rig.Mount.Slews.Should().Be(1);
            rig.TrueErrorArcmin().Should().BeApproximately(expected, 0.5 / 60, "the slew went to the target rotated by the solved-to-reported rotation");
            rig.TrueErrorArcmin().Should().BeLessThan(1);
            TestContext.Out.WriteLine($"residual after the measured correction: {rig.TrueErrorArcmin() * 60:0.0}\" (predicted {expected * 60:0.0}\")");
        }

        [Test]
        public async Task MountIgnoresSyncSilently_IsDetected_AndTheMeasuredCorrectionCentres() {
            var rig = new Rig(errorRaArcmin: 12, errorDecArcmin: -7);
            rig.Mount.SyncBehaviour = SyncBehaviour.IgnoreSilently;
            var expected = rig.PredictMeasuredCorrectionResidualArcmin();

            var result = await rig.Center(noSync: false);

            result.Success.Should().BeTrue();
            result.Attempts.Should().HaveCount(2);
            rig.Mount.Syncs.Should().Be(1);
            rig.TrueErrorArcmin().Should().BeApproximately(expected, 0.5 / 60, "a sync that moves the position by less than 1\" falls back to the measured correction");
            rig.TrueErrorArcmin().Should().BeLessThan(1);
        }

        [Test]
        public async Task MountRejectsSync_TheMeasuredCorrectionCentres() {
            var rig = new Rig(errorRaArcmin: -20, errorDecArcmin: 9);
            rig.Mount.SyncBehaviour = SyncBehaviour.Reject;
            var expected = rig.PredictMeasuredCorrectionResidualArcmin();

            var result = await rig.Center(noSync: false);

            result.Success.Should().BeTrue();
            result.Attempts.Should().HaveCount(2);
            rig.TrueErrorArcmin().Should().BeApproximately(expected, 0.5 / 60);
            rig.TrueErrorArcmin().Should().BeLessThan(1);
        }

        [Test]
        public async Task ScatteredSlews_ConvergeWithinAFewIterations() {
            // Each slew lands up to about 0.8' off (alt-az goto repeatability), and each solve is off by up to 2"
            var rig = new Rig(errorRaArcmin: 25, errorDecArcmin: 15, slewScatterArcmin: 0.8, solveNoiseArcsec: 2, seed: 7);

            var result = await rig.Center(noSync: false);

            result.Success.Should().BeTrue();
            result.Attempts.Count.Should().BeInRange(2, 5);
            rig.TrueErrorArcmin().Should().BeLessThan(1.0 + 2.0 / 60);
            TestContext.Out.WriteLine($"{result.Attempts.Count} attempts; separations: {string.Join(", ", result.Attempts.Select(a => $"{a.PlateSolveResult.Separation?.Distance.ArcMinutes:0.00}'"))}");
        }

        [Test]
        public async Task NearSolveFails_BlindFailoverRescuesTheCentring() {
            var rig = new Rig(errorRaArcmin: 12, errorDecArcmin: -7);
            rig.Solver.FailNextCalls = 1;

            var result = await rig.Center(noSync: false);

            result.Success.Should().BeTrue();
            rig.BlindSolver.Calls.Should().Be(1, "the first near solve failed, so ImageSolver retried without coordinates");
            rig.BlindSolver.Parameters.Single().Coordinates.Should().BeNull();
            rig.Solver.Parameters.Should().OnlyContain(p => p.Coordinates != null);
            rig.TrueErrorArcmin().Should().BeLessThan(0.01);
        }

        [Test]
        public async Task NothingSolves_GivesUpWithoutMovingTheMount() {
            var rig = new Rig(errorRaArcmin: 12, errorDecArcmin: -7);
            rig.Solver.FailNextCalls = int.MaxValue;
            rig.BlindSolver.FailNextCalls = int.MaxValue;

            var result = await rig.Center(noSync: false, attempts: 2);

            result.Success.Should().BeFalse();
            result.Attempts.Should().HaveCount(1);
            rig.Captures.Should().Be(2, "CaptureSolver captured and solved twice (Attempts = 2), each with the blind failover");
            rig.BlindSolver.Calls.Should().Be(2);
            rig.Mount.Slews.Should().Be(0);
            rig.Mount.Syncs.Should().Be(0);
        }

        [Test]
        public async Task AMountThatCannotHitTheTarget_StopsAfterTenSlews() {
            var rig = new Rig(errorRaArcmin: 12, errorDecArcmin: -7, slewScatterArcmin: 6, seed: 3);

            var result = await rig.Center(noSync: false);

            result.Success.Should().BeFalse();
            result.Attempts.Should().HaveCount(10);
            rig.Mount.Slews.Should().Be(10);
        }

        [Test]
        public async Task SolveProgress_CarriesTheRenderedThumbnailAndEachResult() {
            var rig = new Rig(errorRaArcmin: 12, errorDecArcmin: -7);
            var progress = new List<PlateSolveProgress>();

            await rig.Center(noSync: false, solveProgress: new ListProgress<PlateSolveProgress>(progress));

            progress.Where(p => p.Thumbnail != null).Should().HaveCount(2, "NINA.Image renders a thumbnail of each solve frame on macOS");
            progress.Where(p => p.Thumbnail != null).Should().OnlyContain(p => p.Thumbnail.PixelWidth == 300);
            var results = progress.Where(p => p.PlateSolveResult != null).Select(p => p.PlateSolveResult).ToList();
            results.Should().HaveCount(4, "CaptureSolver reports each result, then CenteringSolver reports it again with its separation");
            results.Distinct().Should().HaveCount(2).And.OnlyContain(r => r.Separation != null);
            rig.PrepareParameters.Should().OnlyContain(p => p.DetectStars == false, "solve frames skip star detection");
        }

        private enum SyncBehaviour { Accept, IgnoreSilently, Reject }

        /// <summary>
        /// A mount with a pointing error: it reports (RA + dRA, Dec + dDec) when it really points at (RA, Dec). Sync moves the
        /// error so that the current pointing reports as the synced coordinates; a slew puts the reported position on the target.
        /// Reports JNow, receives JNow (CenteringSolver converts to the mount's epoch).
        /// </summary>
        private sealed class SimulatedMount {
            private readonly Random random;
            private readonly double scatterArcmin;
            private double dRa, dDec;

            public SimulatedMount(Coordinates truePointing, double errorRaArcmin, double errorDecArcmin, double scatterArcmin, int seed) {
                True = truePointing;
                dRa = errorRaArcmin / 60 / Math.Cos(AstroUtil.ToRadians(truePointing.Dec));
                dDec = errorDecArcmin / 60;
                this.scatterArcmin = scatterArcmin;
                random = new Random(seed);
            }

            public Coordinates True { get; private set; }
            public SyncBehaviour SyncBehaviour { get; set; } = SyncBehaviour.Accept;
            public int Syncs { get; private set; }
            public int Slews { get; private set; }

            public Coordinates Reported() => new Coordinates(Angle.ByDegree(True.RADegrees + dRa), Angle.ByDegree(True.Dec + dDec), Epoch.J2000).Transform(Epoch.JNOW);

            public bool Sync(Coordinates coordinates) {
                Syncs++;
                if (SyncBehaviour == SyncBehaviour.Reject) {
                    return false;
                }
                if (SyncBehaviour == SyncBehaviour.Accept) {
                    var j2000 = coordinates.Transform(Epoch.J2000);
                    dRa = j2000.RADegrees - True.RADegrees;
                    dDec = j2000.Dec - True.Dec;
                }
                return true;
            }

            public bool Slew(Coordinates target) {
                Slews++;
                var j2000 = target.Transform(Epoch.J2000);
                var scatterRa = Gaussian() * scatterArcmin / 60 / Math.Cos(AstroUtil.ToRadians(j2000.Dec));
                var scatterDec = Gaussian() * scatterArcmin / 60;
                True = new Coordinates(Angle.ByDegree(j2000.RADegrees - dRa + scatterRa), Angle.ByDegree(j2000.Dec - dDec + scatterDec), Epoch.J2000);
                return true;
            }

            private double Gaussian() {
                return Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()) / Math.Sqrt(2);
            }
        }

        /// <summary>Solves to where the mount really points (plus noise), or fails on request.</summary>
        private sealed class SimulatedSolver : IPlateSolver {
            private readonly Func<Coordinates> truth;
            private readonly double noiseArcsec;
            private readonly Random random;

            public SimulatedSolver(Func<Coordinates> truth, double noiseArcsec, int seed) {
                this.truth = truth;
                this.noiseArcsec = noiseArcsec;
                random = new Random(seed);
            }

            public int Calls { get; private set; }
            public int FailNextCalls { get; set; }
            public List<PlateSolveParameter> Parameters { get; } = new();

            public Task<PlateSolveResult> SolveAsync(IImageData source, PlateSolveParameter parameter, IProgress<ApplicationStatus> progress, CancellationToken canceltoken) {
                Calls++;
                Parameters.Add(parameter);
                source.Properties.Width.Should().BeGreaterThan(0);
                if (FailNextCalls > 0) {
                    FailNextCalls--;
                    return Task.FromResult(new PlateSolveResult { Success = false });
                }
                var t = truth();
                var noiseRa = (random.NextDouble() * 2 - 1) * noiseArcsec / 3600 / Math.Cos(AstroUtil.ToRadians(t.Dec));
                var noiseDec = (random.NextDouble() * 2 - 1) * noiseArcsec / 3600;
                return Task.FromResult(new PlateSolveResult {
                    Success = true,
                    Coordinates = new Coordinates(Angle.ByDegree(t.RADegrees + noiseRa), Angle.ByDegree(t.Dec + noiseDec), Epoch.J2000),
                    Pixscale = 0.4785,
                    PositionAngle = 30,
                    Radius = 0.146
                });
            }
        }

        private sealed class Rig {
            private readonly Coordinates target = M51;

            public Rig(double errorRaArcmin, double errorDecArcmin, double slewScatterArcmin = 0, double solveNoiseArcsec = 0, int seed = 1) {
                // The goto put the reported position on the target, so the mount really points off by the error
                Mount = new SimulatedMount(target, errorRaArcmin, errorDecArcmin, 0, seed);
                Mount.Slew(target.Transform(Epoch.JNOW));
                Mount = new SimulatedMount(Mount.True, errorRaArcmin, errorDecArcmin, slewScatterArcmin, seed);
                Solver = new SimulatedSolver(() => Mount.True, solveNoiseArcsec, seed + 100);
                BlindSolver = new SimulatedSolver(() => Mount.True, solveNoiseArcsec, seed + 200);
            }

            public SimulatedMount Mount { get; }
            public SimulatedSolver Solver { get; }
            public SimulatedSolver BlindSolver { get; }
            public int Captures { get; private set; }
            public List<PrepareImageParameters> PrepareParameters { get; } = new();

            public double TrueErrorArcmin() => (Mount.True - target).Distance.ArcMinutes;

            /// <summary>
            /// Where the first correction slew should land when sync is off or does not work, computed independently of NINA:
            /// the target rotated about solved x reported by the angle between them (CenteringSolver's measured correction), then
            /// through the mount's pointing error. The simulated error is a shift in RA/Dec, not a rotation, so a few arcseconds remain.
            /// </summary>
            public double PredictMeasuredCorrectionResidualArcmin() {
                var solved = Vector(Mount.True);
                var reported = Vector(Mount.Reported().Transform(Epoch.J2000));
                var axis = Cross(solved, reported);
                var sin = Math.Sqrt(Dot(axis, axis));
                var cos = Dot(solved, reported);
                (double X, double Y, double Z) k = (axis.X / sin, axis.Y / sin, axis.Z / sin);
                var angle = Math.Atan2(sin, cos);
                var t = Vector(target);
                // Rodrigues: v cos a + (k x v) sin a + k (k . v)(1 - cos a)
                var kxv = Cross(k, t);
                var kv = Dot(k, t);
                (double X, double Y, double Z) corrected = (
                    t.X * Math.Cos(angle) + kxv.X * Math.Sin(angle) + k.X * kv * (1 - Math.Cos(angle)),
                    t.Y * Math.Cos(angle) + kxv.Y * Math.Sin(angle) + k.Y * kv * (1 - Math.Cos(angle)),
                    t.Z * Math.Cos(angle) + kxv.Z * Math.Sin(angle) + k.Z * kv * (1 - Math.Cos(angle)));
                var correctedRa = AstroUtil.EuclidianModulus(Math.Atan2(corrected.Y, corrected.X) * 180 / Math.PI, 360);
                var correctedDec = Math.Asin(corrected.Z) * 180 / Math.PI;
                // The mount reports (RA + dRA, Dec + dDec) where it points, so it points at (corrected - offset)
                var reportedJ2000 = Mount.Reported().Transform(Epoch.J2000);
                var dRa = reportedJ2000.RADegrees - Mount.True.RADegrees;
                var dDec = reportedJ2000.Dec - Mount.True.Dec;
                var landed = new Coordinates(Angle.ByDegree(correctedRa - dRa), Angle.ByDegree(correctedDec - dDec), Epoch.J2000);
                return (landed - target).Distance.ArcMinutes;
            }

            private static (double X, double Y, double Z) Vector(Coordinates c) {
                double ra = c.RADegrees * Math.PI / 180, dec = c.Dec * Math.PI / 180;
                return (Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
            }

            private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) {
                return (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
            }

            private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

            /// <summary>A small 16:9 frame (the solver is simulated, so only its size and metadata matter).</summary>
            private static IImageData Frame() {
                const int width = 640, height = 360;
                var pixels = new ushort[width * height];
                for (var i = 0; i < pixels.Length; i++) {
                    pixels[i] = (ushort)(500 + (i % width));
                }
                var meta = new ImageMetaData();
                meta.Target.Name = "M 51";
                return SolverKit.ImageData(pixels, width, height, metaData: meta);
            }

            public async Task<CenteringSolveResult> Center(bool noSync, int attempts = 1, IProgress<PlateSolveProgress>? solveProgress = null) {
                var telescope = new Mock<ITelescopeMediator>();
                telescope.Setup(x => x.GetCurrentPosition()).Returns(() => Mount.Reported());
                telescope.Setup(x => x.Sync(It.IsAny<Coordinates>())).Returns((Coordinates c) => Task.FromResult(Mount.Sync(c)));
                telescope.Setup(x => x.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                    .Returns((Coordinates c, CancellationToken _) => Task.FromResult(Mount.Slew(c)));

                var imaging = new Mock<IImagingMediator>();
                imaging.Setup(x => x.CaptureAndPrepareImage(It.IsAny<CaptureSequence>(), It.IsAny<PrepareImageParameters>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
                    .Returns((CaptureSequence _, PrepareImageParameters p, CancellationToken _, IProgress<ApplicationStatus> _) => {
                        Captures++;
                        PrepareParameters.Add(p);
                        return Task.FromResult(Frame().RenderImage());
                    });
                var dome = new Mock<IDomeMediator>();
                dome.Setup(x => x.GetInfo()).Returns(new DomeInfo { Connected = false });

                var centering = new CenteringSolver(Solver, BlindSolver, imaging.Object, telescope.Object,
                    new Mock<IFilterWheelMediator>().Object, dome.Object, new Mock<IDomeFollower>().Object);
                var parameter = new CenterSolveParameter {
                    Attempts = attempts,
                    ReattemptDelay = TimeSpan.Zero,
                    Binning = SolverKit.RigBinning,
                    Coordinates = target,
                    DownSampleFactor = 2,
                    FocalLength = SolverKit.RigFocalLength,
                    MaxObjects = 500,
                    PixelSize = SolverKit.RigPixelSize,
                    SearchRadius = 5,
                    Threshold = 1,
                    NoSync = noSync,
                    BlindFailoverEnabled = true,
                    DisableNotifications = true
                };
                var sequence = new CaptureSequence { ExposureTime = 15, Gain = 450 };
                return await centering.CenterWithMeasurements(sequence, parameter, solveProgress, null, CancellationToken.None);
            }
        }

        private sealed class ListProgress<T> : IProgress<T> {
            private readonly List<T> values;

            public ListProgress(List<T> values) {
                this.values = values;
            }

            public void Report(T value) => values.Add(value);
        }
    }
}
