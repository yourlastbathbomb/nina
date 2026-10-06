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
using NINA.PlateSolving.Mac;
using NINA.Profile.Interfaces;
using System.Diagnostics;
using System.Globalization;

namespace NINA.Mac.Platesolving.Test.LocalData {

    /// <summary>
    /// NINA's centring loop with the real ASTAP in it. The mount is simulated (a goto error it reports in JNow, sync and slew as
    /// in CenteringLoopTest), but the camera is a window onto a real stacked star field from this Mac: each capture cuts a
    /// 1280 x 720 frame out of the stack, centred where the mount really points (the header WCS gives the pixel), and NINA's
    /// CenteringSolver solves it with ASTAP and D80 through PlateSolverFactory, ImageSolver and CLISolver, syncs, re-slews and
    /// repeats until the target is within the 1' threshold. The frames have the stack's own scale (about 3.7"/px), not this
    /// rig's; the rig's own field is RealSolveTest.RigGeometry_*'s subject.
    /// The files are only read; paths come from NINA_MAC_SOLVE_FRAMES as for RealSolveTest, and frames without a header WCS
    /// are skipped. A near solve that ASTAP cannot do goes to NINA's blind failover (solve-field, as the rig profile sets it); the
    /// table counts those, and centring must still converge.
    /// </summary>
    [TestFixture]
    [Explicit("Reads FITS files from this Mac (NINA_MAC_SOLVE_FRAMES) and runs ASTAP")]
    [Category("LocalData")]
    [NonParallelizable]
    public class RealCenteringTest {

        private const int FrameWidth = 1280;
        private const int FrameHeight = 720;

        /// <summary>The goto error the loop has to remove: 12' east, 7' south (13.9').</summary>
        private const double ErrorRaArcmin = 12;
        private const double ErrorDecArcmin = -7;

        [Test]
        public async Task CenteringSolver_WithRealAstap_RemovesAGotoErrorOnARealStarField() {
            if (!SolverKit.HasAstap) {
                Assert.Ignore($"ASTAP or D80 missing ({SolverKit.AstapExecutable}, {SolverKit.AstapDatabase})");
            }
            var frames = RealSolveTest.Frames();
            if (frames.Count == 0) {
                Assert.Ignore("Set NINA_MAC_SOLVE_FRAMES to one or more FITS paths separated by '|'");
            }
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupAllProperties();
            AstrometryNetSetup.IndexDirectories = new[] { SolverKit.AstrometryIndex };
            RigPlateSolveDefaults.Apply(settings.Object, AstapSetup.Resolve(SolverKit.AstapExecutable, SolverKit.AstapDatabase), SolverKit.AstrometryBin);

            var rows = new List<string> {
                "| Frame | Attempts | Measured separations (') | True error before / after (') | ASTAP solve times (s) | Blind solves | Centring time (s) |",
                "|---|---|---|---|---|---|---|"
            };
            var failures = new List<string>();
            foreach (var path in frames) {
                var fits = FitsCube.Read(path);
                var wcs = ReferenceWcs.FromHeader(fits);
                var name = Path.GetFileNameWithoutExtension(path);
                if (wcs == null) {
                    rows.Add($"| {name} | skipped: no header WCS | | | | | |");
                    continue;
                }
                var sky = new StackCamera(fits, wcs, RealSolveTest.Scaling(fits));
                var targetPosition = wcs.PixelToSky((fits.Width + 1) / 2d, (fits.Height + 1) / 2d);
                var target = SolverKit.Coordinates(targetPosition.Ra, targetPosition.Dec);
                var mount = new Mount(target, ErrorRaArcmin, ErrorDecArcmin);
                var near = new TimedSolver(PlateSolverFactory.GetPlateSolver(settings.Object));
                var blind = new TimedSolver(PlateSolverFactory.GetBlindSolver(settings.Object));
                var trueErrorBefore = (mount.True - target).Distance.ArcMinutes;

                var telescope = new Mock<ITelescopeMediator>();
                telescope.Setup(x => x.GetCurrentPosition()).Returns(() => mount.Reported());
                telescope.Setup(x => x.Sync(It.IsAny<Coordinates>())).Returns((Coordinates c) => Task.FromResult(mount.Sync(c)));
                telescope.Setup(x => x.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                    .Returns((Coordinates c, CancellationToken _) => Task.FromResult(mount.Slew(c)));
                var imaging = new Mock<IImagingMediator>();
                imaging.Setup(x => x.CaptureAndPrepareImage(It.IsAny<CaptureSequence>(), It.IsAny<PrepareImageParameters>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
                    .Returns((CaptureSequence _, PrepareImageParameters _, CancellationToken _, IProgress<ApplicationStatus> _) =>
                        Task.FromResult(sky.Capture(mount.True, name).RenderImage()));
                var dome = new Mock<IDomeMediator>();
                dome.Setup(x => x.GetInfo()).Returns(new DomeInfo { Connected = false });

                var centering = new CenteringSolver(near, blind, imaging.Object, telescope.Object,
                    new Mock<IFilterWheelMediator>().Object, dome.Object, new Mock<IDomeFollower>().Object);
                var parameter = new CenterSolveParameter {
                    Attempts = 1,
                    ReattemptDelay = TimeSpan.Zero,
                    Binning = 1,
                    Coordinates = target,
                    DownSampleFactor = RigPlateSolveDefaults.DownSampleFactor,
                    FocalLength = fits.OptionalDouble("FOCALLEN") ?? throw new InvalidDataException($"{name}: no focal length"),
                    MaxObjects = 500,
                    PixelSize = fits.OptionalDouble("XPIXSZ") ?? throw new InvalidDataException($"{name}: no pixel size"),
                    SearchRadius = RigPlateSolveDefaults.SearchRadius,
                    Threshold = 1,
                    NoSync = false,
                    BlindFailoverEnabled = true,
                    DisableNotifications = true
                };

                var stopwatch = Stopwatch.StartNew();
                var result = await centering.CenterWithMeasurements(new CaptureSequence { ExposureTime = 15 }, parameter, null, null, CancellationToken.None);
                stopwatch.Stop();
                var trueErrorAfter = (mount.True - target).Distance.ArcMinutes;
                var separations = result.Attempts.Select(a => a.PlateSolveResult?.Separation?.Distance.ArcMinutes ?? double.NaN).ToList();

                rows.Add($"| {name} | {result.Attempts.Count} | {string.Join(", ", separations.Select(s => s.ToString("0.00", CultureInfo.InvariantCulture)))} | " +
                    $"{trueErrorBefore:0.00} / {trueErrorAfter:0.00} | {string.Join(", ", near.Seconds.Select(s => s.ToString("0.0", CultureInfo.InvariantCulture)))} | " +
                    $"{blind.Seconds.Count} | {stopwatch.Elapsed.TotalSeconds:0.0} |");

                if (!result.Success) {
                    failures.Add($"{name}: centring failed after {result.Attempts.Count} attempts");
                    continue;
                }
                if (trueErrorAfter > parameter.Threshold) {
                    failures.Add($"{name}: the mount ended {trueErrorAfter:0.00}' from the target");
                }
                // The first solve measured the goto error (the frame's centre is the mount's true pointing to half a pixel)
                var firstSolve = result.Attempts.First().PlateSolveResult;
                var measuredError = Math.Abs(firstSolve.Separation.Distance.ArcMinutes - trueErrorBefore) * 60;
                if (measuredError > 10) {
                    failures.Add($"{name}: the first solve measured {firstSolve.Separation.Distance.ArcMinutes:0.00}', the goto error was {trueErrorBefore:0.00}'");
                }
            }

            var text = $"\n### Centring with the real ASTAP ({FrameWidth}x{FrameHeight} frames cut from the stack, goto error {Math.Sqrt(ErrorRaArcmin * ErrorRaArcmin + ErrorDecArcmin * ErrorDecArcmin):0.0}', threshold 1')\n\n{string.Join("\n", rows)}\n";
            TestContext.Progress.WriteLine(text);
            var report = Environment.GetEnvironmentVariable("NINA_MAC_CENTER_REPORT");
            if (!string.IsNullOrWhiteSpace(report)) {
                File.WriteAllText(report, text);
            }
            failures.Should().BeEmpty();
        }

        /// <summary>
        /// The mount reports (RA + dRA, Dec + dDec) in JNow when it really points at (RA, Dec) J2000. The goto left the reported
        /// position on the target. Sync makes the current pointing report as the synced coordinates; a slew puts the reported
        /// position on the requested one.
        /// </summary>
        private sealed class Mount {
            private double dRa, dDec;

            public Mount(Coordinates target, double errorRaArcmin, double errorDecArcmin) {
                dRa = errorRaArcmin / 60 / Math.Cos(AstroUtil.ToRadians(target.Dec));
                dDec = errorDecArcmin / 60;
                True = SolverKit.Coordinates(target.RADegrees - dRa, target.Dec - dDec);
            }

            public Coordinates True { get; private set; }

            public Coordinates Reported() => SolverKit.Coordinates(True.RADegrees + dRa, True.Dec + dDec).Transform(Epoch.JNOW);

            public bool Sync(Coordinates coordinates) {
                var j2000 = coordinates.Transform(Epoch.J2000);
                dRa = j2000.RADegrees - True.RADegrees;
                dDec = j2000.Dec - True.Dec;
                return true;
            }

            public bool Slew(Coordinates target) {
                var j2000 = target.Transform(Epoch.J2000);
                True = SolverKit.Coordinates(j2000.RADegrees - dRa, j2000.Dec - dDec);
                return true;
            }
        }

        /// <summary>A camera looking at the stack: a FrameWidth x FrameHeight cut-out centred on the pixel the WCS gives for a sky position.</summary>
        private sealed class StackCamera {
            private readonly FitsCube fits;
            private readonly ReferenceWcs wcs;
            private readonly float[] mono;
            private readonly double scaling;

            public StackCamera(FitsCube fits, ReferenceWcs wcs, double scaling) {
                this.fits = fits;
                this.wcs = wcs;
                this.scaling = scaling;
                mono = fits.Mono();
            }

            public IImageData Capture(Coordinates pointing, string name) {
                var (x, y) = wcs.SkyToPixel(pointing.RADegrees, pointing.Dec);
                // FITS 1-based centre to the 0-based first column and row of the cut-out
                var x0 = (int)Math.Round(x - 1 - (FrameWidth - 1) / 2d);
                var y0 = (int)Math.Round(y - 1 - (FrameHeight - 1) / 2d);
                if (x0 < 0 || y0 < 0 || x0 + FrameWidth > fits.Width || y0 + FrameHeight > fits.Height) {
                    throw new InvalidOperationException($"{name}: the mount points off the stack ({x:0}, {y:0})");
                }
                var pixels = new float[FrameWidth * FrameHeight];
                for (var row = 0; row < FrameHeight; row++) {
                    Array.Copy(mono, (y0 + row) * fits.Width + x0, pixels, row * FrameWidth, FrameWidth);
                }
                var meta = new ImageMetaData();
                meta.Target.Name = name;
                return SolverKit.ImageData(FitsCube.ToUShort(pixels, scaling), FrameWidth, FrameHeight, metaData: meta);
            }
        }

        /// <summary>Times each solve of the wrapped solver.</summary>
        private sealed class TimedSolver : IPlateSolver {
            private readonly IPlateSolver solver;

            public TimedSolver(IPlateSolver solver) {
                this.solver = solver;
            }

            public List<double> Seconds { get; } = new();

            public async Task<PlateSolveResult> SolveAsync(IImageData source, PlateSolveParameter parameter, IProgress<ApplicationStatus> progress, CancellationToken canceltoken) {
                var stopwatch = Stopwatch.StartNew();
                try {
                    return await solver.SolveAsync(source, parameter, progress, canceltoken);
                } finally {
                    Seconds.Add(stopwatch.Elapsed.TotalSeconds);
                }
            }
        }
    }
}
