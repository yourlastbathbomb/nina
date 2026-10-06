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
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.PlateSolving.Mac;
using NINA.Profile.Interfaces;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NINA.Mac.Platesolving.Test.LocalData {

    /// <summary>
    /// Real solves of star-field FITS files on this Mac with both solvers, through NINA's own classes: PlateSolverFactory builds
    /// the solvers from profile settings (ASTAP through the database launcher, LOCAL = solve-field with the Nightglass
    /// astrometry.cfg), ImageSolver runs them, CLISolver writes the frame with NINA's FITS writer and parses the result.
    /// <para>
    /// The files are the user's own and are only read: their paths come from NINA_MAC_SOLVE_FRAMES ('|'-separated), nothing is
    /// copied into the repository. The reference for each solve is the WCS already in the file's header (Seestar or Siril),
    /// evaluated by <see cref="ReferenceWcs"/>; a file without one is compared with its RA/DEC cards and between the solvers.
    /// </para>
    /// <para>
    /// RigGeometry_* builds this rig's native f/10 bin-2 frame (1920 x 1080 at 0.4785"/px, 15.3' x 8.6', ASTAP -fov 0.1436)
    /// from patches of those stacks and asks whether ASTAP with D80 solves it (MAC_PORT_PLAN risk 4). See the summary it prints
    /// for what that construction can and cannot say. NINA_MAC_SOLVE_GRID (default 3x3) sets the patches per frame,
    /// NINA_MAC_SOLVE_BLIND=1 adds blind solve-field runs on them, NINA_MAC_SOLVE_REPORT=&lt;file&gt; writes the tables as Markdown.
    /// </para>
    /// </summary>
    [TestFixture]
    [Explicit("Reads FITS files from this Mac (NINA_MAC_SOLVE_FRAMES) and runs ASTAP and solve-field")]
    [Category("LocalData")]
    [NonParallelizable]
    public class RealSolveTest {

        private static readonly double RigScale = AstroUtil.ArcsecPerPixel(SolverKit.RigPixelSize * SolverKit.RigBinning, SolverKit.RigFocalLength);

        /// <summary>The f/6.3 reducer train (MAC_PORT_PLAN risk 4: "first night with the reducer"): about 1600 mm, 0.748"/px at bin 2, 0.224 deg high.</summary>
        private const double ReducerFocalLength = 1600;

        private static readonly double ReducerScale = AstroUtil.ArcsecPerPixel(SolverKit.RigPixelSize * SolverKit.RigBinning, ReducerFocalLength);

        private IPlateSolveSettings settings = null!;
        private readonly StringBuilder report = new();

        [OneTimeSetUp]
        public void SetUp() {
            if (!SolverKit.HasAstap) {
                Assert.Ignore($"ASTAP or D80 missing ({SolverKit.AstapExecutable}, {SolverKit.AstapDatabase})");
            }
            if (!SolverKit.HasSolveField) {
                Assert.Ignore($"solve-field, wcsinfo or index files missing ({SolverKit.AstrometryBin}, {SolverKit.AstrometryIndex})");
            }
            if (Frames().Count == 0) {
                Assert.Ignore("Set NINA_MAC_SOLVE_FRAMES to one or more FITS paths separated by '|'");
            }
            AstrometryNetSetup.IndexDirectories = new[] { SolverKit.AstrometryIndex };
            var mock = new Mock<IPlateSolveSettings>();
            mock.SetupAllProperties();
            settings = mock.Object;
            RigPlateSolveDefaults.Apply(settings, AstapSetup.Resolve(SolverKit.AstapExecutable, SolverKit.AstapDatabase), SolverKit.AstrometryBin);
            report.AppendLine($"Real solves, {DateTime.Now:yyyy-MM-dd HH:mm} local time. ASTAP: {settings.ASTAPLocation} -> {SolverKit.AstapExecutable} -d {SolverKit.AstapDatabase}; " +
                $"solve-field: {SolverKit.AstrometryBin}, index folder {SolverKit.AstrometryIndex} ({AstrometryNetSetup.FindIndexFiles().Count} files), config {AstrometryNetSetup.ConfigFilePath}");
        }

        [OneTimeTearDown]
        public void TearDown() {
            var path = Environment.GetEnvironmentVariable("NINA_MAC_SOLVE_REPORT");
            if (!string.IsNullOrWhiteSpace(path)) {
                File.WriteAllText(path, report.ToString());
                TestContext.Progress.WriteLine($"Report written to {path}");
            }
        }

        internal static List<string> Frames() {
            return (Environment.GetEnvironmentVariable("NINA_MAC_SOLVE_FRAMES") ?? string.Empty)
                .Split(new[] { '|', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        [Test]
        public async Task FullFrames_BothSolvers_AgreeWithTheHeaderWcs() {
            var rows = new List<string> {
                "| Frame | Size, scale | Reference | Solver | Result | Time (s) | Solved center (RA, Dec deg) | Center error (\") | Scale (\"/px) | PA (deg) | Flipped |",
                "|---|---|---|---|---|---|---|---|---|---|---|"
            };
            var failures = new List<string>();
            foreach (var path in Frames()) {
                var fits = FitsCube.Read(path);
                var wcs = ReferenceWcs.FromHeader(fits);
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                var pixelSize = fits.OptionalDouble("XPIXSZ") ?? fits.OptionalDouble("PIXSIZE1") ?? throw new InvalidDataException($"{name}: no pixel size");
                var focalLength = fits.OptionalDouble("FOCALLEN") ?? throw new InvalidDataException($"{name}: no focal length");
                var image = SolverKit.ImageData(FitsCube.ToUShort(fits.Mono(), Scaling(fits)), fits.Width, fits.Height, metaData: Meta(name));

                (double Ra, double Dec) reference;
                string referenceText;
                double headerPa = double.NaN;
                bool headerFlipped = false;
                if (wcs != null) {
                    reference = wcs.PixelToSky((fits.Width + 1) / 2d, (fits.Height + 1) / 2d);
                    // The header's position angle and parity at the frame center, in NINA's convention (ASTAPSolver.ReadResult:
                    // PositionAngle = 360 - (Rotation - 180), Flipped = !Flipped of the CD matrix). Both solvers put their reference
                    // point at the center; the header's CRVAL can be far from it, where north points elsewhere (M51: 1.2 deg)
                    var local = wcs.LocalCd((fits.Width + 1) / 2d, (fits.Height + 1) / 2d);
                    var headerWcs = new WorldCoordinateSystem(0, 0, 0, 0, local.CD11, local.CD12, local.CD21, local.CD22);
                    headerPa = AstroUtil.EuclidianModulus(360 - (headerWcs.Rotation - 180), 360);
                    headerFlipped = !headerWcs.Flipped;
                    var crvalWcs = new WorldCoordinateSystem(0, 0, 0, 0, wcs.CD.CD11, wcs.CD.CD12, wcs.CD.CD21, wcs.CD.CD22);
                    var crvalPa = AstroUtil.EuclidianModulus(360 - (crvalWcs.Rotation - 180), 360);
                    referenceText = $"header WCS{(wcs.HasSip ? "+SIP" : "")} at the center, {wcs.Scale * 3600:0.000}\"/px, PA {headerPa:0.00} (at CRVAL {crvalPa:0.00}), flipped {headerFlipped}";
                    // Cross-check of the reference itself: astrometry.net's wcs-xy2rd on the same header and pixel. Siril 1.2.5 wrote
                    // some stacks without CTYPE cards, which wcs-xy2rd then reads as a linear (non-celestial) WCS, so there is
                    // nothing to compare; ReferenceWcs assumes TAN for them, and the solves below check that assumption
                    var check = fits.Has("CTYPE1") ? WcsXy2Rd(path, (fits.Width + 1) / 2d, (fits.Height + 1) / 2d) : null;
                    if (!fits.Has("CTYPE1")) {
                        referenceText += " (no CTYPE cards: TAN assumed)";
                    }
                    if (check != null) {
                        var d = ReferenceWcs.SeparationArcsec(reference.Ra, reference.Dec, check.Value.Ra, check.Value.Dec);
                        referenceText += $"; wcs-xy2rd differs by {d:0.00}\"";
                        if (d > 0.5) {
                            failures.Add($"{name}: ReferenceWcs and wcs-xy2rd differ by {d:0.00}\" at the center");
                        }
                    }
                } else {
                    reference = (fits.Double("RA"), fits.Double("DEC"));
                    referenceText = "header RA/DEC only (the target, not the frame center)";
                }
                var hint = fits.Has("RA") && fits.Has("DEC") ? SolverKit.Coordinates(fits.Double("RA"), fits.Double("DEC")) : SolverKit.Coordinates(reference.Ra, reference.Dec);

                var results = new List<(string Solver, PlateSolveResult Result)>();
                foreach (var (solver, parameter) in new[] {
                    ("ASTAP D80, -z 2", Parameter(focalLength, pixelSize, hint, 2)),
                    ("ASTAP D80, -z 0 (NINA default, P1)", Parameter(focalLength, pixelSize, hint, 0)),
                    ("solve-field near (hint, 5 deg)", Parameter(focalLength, pixelSize, hint, 2)),
                    ("solve-field blind", Parameter(focalLength, pixelSize, null, 2)),
                }) {
                    var (result, seconds) = await Solve(solver.StartsWith("ASTAP", StringComparison.Ordinal), image, parameter);
                    results.Add((solver, result));
                    var error = result.Success ? ReferenceWcs.SeparationArcsec(result.Coordinates.RADegrees, result.Coordinates.Dec, reference.Ra, reference.Dec) : double.NaN;
                    rows.Add($"| {name} | {fits.Width}x{fits.Height}, {AstroUtil.ArcsecPerPixel(pixelSize, focalLength):0.00}\"/px nominal | {referenceText} | {solver} | " +
                        $"{(result.Success ? "solved" : "FAILED")} | {seconds:0.0} | " +
                        $"{(result.Success ? $"{result.Coordinates.RADegrees:0.00000}, {result.Coordinates.Dec:+0.00000;-0.00000}" : "-")} | {Format(error, "0.0")} | {Format(result.Pixscale, "0.0000")} | {Format(result.PositionAngle, "0.00")} | {(result.Success ? result.Flipped.ToString() : "-")} |");
                    if (wcs != null) {
                        if (!result.Success) {
                            failures.Add($"{name} / {solver}: not solved");
                        } else if (error > 30) {
                            failures.Add($"{name} / {solver}: {error:0.0}\" from the header WCS");
                        } else if (Math.Abs(result.Pixscale / (wcs.Scale * 3600) - 1) > 0.02) {
                            failures.Add($"{name} / {solver}: scale {result.Pixscale:0.0000}\"/px, header {wcs.Scale * 3600:0.0000}");
                        } else if (Math.Abs(AstroUtil.EuclidianModulus(result.PositionAngle - headerPa + 180, 360) - 180) > 0.5 || result.Flipped != headerFlipped) {
                            failures.Add($"{name} / {solver}: PA {result.PositionAngle:0.00}, flipped {result.Flipped}; header at the center {headerPa:0.00}, {headerFlipped}");
                        }
                    }
                }
                var solved = results.Where(r => r.Result.Success).ToList();
                for (var i = 1; i < solved.Count; i++) {
                    var d = ReferenceWcs.SeparationArcsec(solved[0].Result.Coordinates.RADegrees, solved[0].Result.Coordinates.Dec, solved[i].Result.Coordinates.RADegrees, solved[i].Result.Coordinates.Dec);
                    if (d > 30) {
                        failures.Add($"{name}: {solved[0].Solver} and {solved[i].Solver} disagree by {d:0.0}\"");
                    }
                    // Both read the same NINA-written FITS, so upstream's two position-angle conventions (ASTAP's CD matrix,
                    // wcsinfo's orientation_center) and parity rules must agree
                    var paDifference = Math.Abs(AstroUtil.EuclidianModulus(solved[i].Result.PositionAngle - solved[0].Result.PositionAngle + 180, 360) - 180);
                    if (paDifference > 0.5 || solved[i].Result.Flipped != solved[0].Result.Flipped) {
                        failures.Add($"{name}: {solved[0].Solver} and {solved[i].Solver} report PA {solved[0].Result.PositionAngle:0.00}/{solved[i].Result.PositionAngle:0.00}, flipped {solved[0].Result.Flipped}/{solved[i].Result.Flipped}");
                    }
                }
            }
            Emit("Full frames", rows);
            failures.Should().BeEmpty();
        }

        [Test]
        public async Task RigGeometry_PatchesResampledToTheNativeField() {
            var grid = (Environment.GetEnvironmentVariable("NINA_MAC_SOLVE_GRID") ?? "3x3").Split('x').Select(int.Parse).ToArray();
            var blind = Environment.GetEnvironmentVariable("NINA_MAC_SOLVE_BLIND") == "1";
            // solve-field gives up after astrometry.cfg's cpulimit; 300 s per unsolvable patch would take hours, so the patches use
            // NINA_MAC_SOLVE_CPULIMIT (default 60 s, the research's suggested near-solve budget)
            var cpuLimit = int.Parse(Environment.GetEnvironmentVariable("NINA_MAC_SOLVE_CPULIMIT") ?? "60", CultureInfo.InvariantCulture);
            var savedCpuLimit = AstrometryNetSetup.CpuLimitSeconds;
            AstrometryNetSetup.CpuLimitSeconds = cpuLimit;
            try {
                await RigGeometry(grid, blind, cpuLimit);
            } finally {
                AstrometryNetSetup.CpuLimitSeconds = savedCpuLimit;
            }
        }

        private async Task RigGeometry(int[] grid, bool blind, int cpuLimit) {
            var rows = new List<string> {
                "| Frame | Patch center (RA, Dec) | Stars >5 sigma in the patch | Variant | Result | Time (s) | Error (\") |",
                "|---|---|---|---|---|---|---|"
            };
            var tally = new SortedDictionary<string, (int Solved, int Total, List<double> Seconds)>(StringComparer.Ordinal);
            var falseSolutions = new List<string>();

            foreach (var path in Frames()) {
                var fits = FitsCube.Read(path);
                var wcs = ReferenceWcs.FromHeader(fits);
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (wcs == null) {
                    rows.Add($"| {name} | - | skipped: no header WCS to place the patches | | | | |");
                    continue;
                }
                var sourceScale = wcs.Scale * 3600;
                var ratio = RigScale / sourceScale;
                var mono = fits.Mono();
                var scaling = Scaling(fits);

                for (var gy = 0; gy < grid[1]; gy++) {
                    for (var gx = 0; gx < grid[0]; gx++) {
                        // Patch center in 0-based source pixels, spread over the inner part of the frame
                        var cx = fits.Width * (gx + 1d) / (grid[0] + 1);
                        var cy = fits.Height * (gy + 1d) / (grid[1] + 1);
                        var truth = wcs.PixelToSky(cx + 1, cy + 1);
                        // The mount's idea of where it points after a goto: 8' east and 5' south of the truth
                        var hint = SolverKit.Coordinates(truth.Ra + 8.0 / 60 / Math.Cos(truth.Dec * Math.PI / 180), truth.Dec - 5.0 / 60);
                        var label = $"{name}#{gy * grid[0] + gx + 1}";
                        var where = $"{truth.Ra:0.000}, {truth.Dec:+0.000;-0.000}";

                        var rigMono = Resample(mono, fits.Width, fits.Height, cx, cy, ratio, SolverKit.RigWidth, SolverKit.RigHeight);
                        // Each variant gets its own target name, so CLISolver's failed-solve archive keeps one set of files per run
                        var rigPixels = FitsCube.ToUShort(rigMono, scaling);
                        IImageData RigImage(string variant) => SolverKit.ImageData(rigPixels, SolverKit.RigWidth, SolverKit.RigHeight, metaData: Meta(label + "-" + variant));
                        var rigImage = RigImage("A");
                        var runs = new List<(string Variant, bool Astap, IImageData Image, PlateSolveParameter Parameter, (double Ra, double Dec) Truth)> {
                            ("A: rig frame, mono, ASTAP D80 -z 2", true, rigImage, Parameter(SolverKit.RigFocalLength, SolverKit.RigPixelSize, hint, 2, SolverKit.RigBinning), truth),
                        };
                        if (fits.Planes.Length == 3) {
                            var bayer = Bayer(fits, cx, cy, ratio);
                            var meta = Meta(label + "-B");
                            meta.Camera.SensorType = SensorType.RGGB;
                            var bayerImage = SolverKit.ImageData(FitsCube.ToUShort(bayer, scaling), SolverKit.RigWidth, SolverKit.RigHeight, isBayered: true, metaData: meta);
                            runs.Add(("B: rig frame, RGGB mosaic, ASTAP D80 -z 2", true, bayerImage, Parameter(SolverKit.RigFocalLength, SolverKit.RigPixelSize, hint, 2, SolverKit.RigBinning), truth));
                        }
                        // The same 15.3' x 8.6' of sky at the stack's own resolution: no interpolation, the stars as the stack recorded them
                        var (nativePixels, nativeWidth, nativeHeight, x0, y0) = Crop(mono, fits.Width, cx, cy, ratio);
                        var nativeTruth = wcs.PixelToSky(x0 + (nativeWidth + 1) / 2d, y0 + (nativeHeight + 1) / 2d);
                        var stars = CountStars(nativePixels, nativeWidth, nativeHeight);
                        var nativeImage = SolverKit.ImageData(FitsCube.ToUShort(nativePixels, scaling), nativeWidth, nativeHeight, metaData: Meta(label + "-C"));
                        var nativeFocal = 206.264806 * SolverKit.RigPixelSize / sourceScale;
                        runs.Add(($"C: same field at {sourceScale:0.00}\"/px ({nativeWidth}x{nativeHeight}), ASTAP D80 -z 1", true, nativeImage, Parameter(nativeFocal, SolverKit.RigPixelSize, hint, 1), nativeTruth));
                        runs.Add(("D: rig frame, mono, solve-field near", false, RigImage("D"), Parameter(SolverKit.RigFocalLength, SolverKit.RigPixelSize, hint, 2, SolverKit.RigBinning), truth));
                        // The same patch center through the reducer: a 1.56 times larger field, above D80's documented minimum
                        var reducerPixels = FitsCube.ToUShort(Resample(mono, fits.Width, fits.Height, cx, cy, ReducerScale / sourceScale, SolverKit.RigWidth, SolverKit.RigHeight), scaling);
                        var reducerImage = SolverKit.ImageData(reducerPixels, SolverKit.RigWidth, SolverKit.RigHeight, metaData: Meta(label + "-R"));
                        runs.Add(($"R: reducer frame ({ReducerScale:0.000}\"/px), mono, ASTAP D80 -z 2", true, reducerImage, Parameter(ReducerFocalLength, SolverKit.RigPixelSize, hint, 2, SolverKit.RigBinning), truth));
                        if (blind) {
                            runs.Add(("E: rig frame, mono, solve-field blind", false, RigImage("E"), Parameter(SolverKit.RigFocalLength, SolverKit.RigPixelSize, null, 2, SolverKit.RigBinning), truth));
                        }

                        foreach (var run in runs) {
                            var (result, seconds) = await Solve(run.Astap, run.Image, run.Parameter);
                            var error = result.Success ? ReferenceWcs.SeparationArcsec(result.Coordinates.RADegrees, result.Coordinates.Dec, run.Truth.Ra, run.Truth.Dec) : double.NaN;
                            rows.Add($"| {label} | {where} | {stars} | {run.Variant} | {(result.Success ? "solved" : "not solved")} | {seconds:0.0} | {Format(error, "0.0")} |");
                            (int Solved, int Total, List<double> Seconds) t = tally.TryGetValue(run.Variant, out var v) ? v : (0, 0, new List<double>());
                            t.Seconds.Add(seconds);
                            tally[run.Variant] = (t.Solved + (result.Success ? 1 : 0), t.Total + 1, t.Seconds);
                            if (result.Success && error > 15) {
                                falseSolutions.Add($"{label} / {run.Variant}: solved {error:0.0}\" from the reference");
                            }
                        }
                    }
                }
            }

            var summary = new List<string> { "| Variant | Solved | Median time (s) | Max time (s) |", "|---|---|---|---|" };
            summary.AddRange(tally.Select(t => {
                var sorted = t.Value.Seconds.OrderBy(s => s).ToList();
                return $"| {t.Key} | {t.Value.Solved}/{t.Value.Total} | {sorted[sorted.Count / 2]:0.0} | {sorted[^1]:0.0} |";
            }));
            Emit($"Rig geometry (1920x1080 at {RigScale:0.0000}\"/px, -fov {Math.Round(1080 * RigScale / 3600, 6).ToString(CultureInfo.InvariantCulture)}; solve-field cpulimit {cpuLimit} s): summary", summary);
            Emit("Rig geometry: every patch", rows);
            falseSolutions.Should().BeEmpty("a reported solution must be where the header WCS says the patch is");
        }

        private async Task<(PlateSolveResult Result, double Seconds)> Solve(bool astap, IImageData image, PlateSolveParameter parameter) {
            IPlateSolver solver = astap ? PlateSolverFactory.GetPlateSolver(settings) : PlateSolverFactory.GetBlindSolver(settings);
            var imageSolver = new ImageSolver(solver, solver);
            var stopwatch = Stopwatch.StartNew();
            var result = await imageSolver.Solve(image, parameter, null, CancellationToken.None);
            stopwatch.Stop();
            return (result, stopwatch.Elapsed.TotalSeconds);
        }

        /// <summary>astrometry.net's wcs-xy2rd (the header's WCS, SIP included) at a FITS 1-based pixel, or null when it is not installed.</summary>
        private static (double Ra, double Dec)? WcsXy2Rd(string fitsPath, double x, double y) {
            var tool = System.IO.Path.Combine(SolverKit.AstrometryBin, "wcs-xy2rd");
            if (!File.Exists(tool)) {
                return null;
            }
            var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-w", fitsPath, "-x", x.ToString("R", CultureInfo.InvariantCulture), "-y", y.ToString("R", CultureInfo.InvariantCulture) }) {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            // "Pixel (1080.5000000000, 1920.5000000000) -> RA,Dec (85.2764049983, -2.4591307976)"
            var match = System.Text.RegularExpressions.Regex.Match(output, @"RA,Dec \(([-0-9.eE+]+), ([-0-9.eE+]+)\)");
            return match.Success
                ? (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
                : null;
        }

        private static PlateSolveParameter Parameter(double focalLength, double pixelSize, Coordinates? hint, int downSample, int binning = 1) {
            return new PlateSolveParameter {
                FocalLength = focalLength,
                PixelSize = pixelSize,
                Binning = binning,
                SearchRadius = RigPlateSolveDefaults.SearchRadius,
                DownSampleFactor = downSample,
                MaxObjects = 500,
                DisableNotifications = true,
                BlindFailoverEnabled = false,
                Coordinates = hint
            };
        }

        private static ImageMetaData Meta(string name) {
            var meta = new ImageMetaData();
            meta.Target.Name = name;
            return meta;
        }

        /// <summary>Float FITS data (0..1, as Siril writes it) is scaled to 16 bits; integer data is already 0..65535.</summary>
        internal static double Scaling(FitsCube fits) => fits.Planes[0].Take(100000).Max() <= 1.5f ? 65535 : 1;

        /// <summary>Bilinear resampling of a source patch centered on (cx, cy) onto a width x height grid, ratio source px per output px.</summary>
        private static float[] Resample(float[] source, int sourceWidth, int sourceHeight, double cx, double cy, double ratio, int width, int height) {
            var output = new float[width * height];
            Parallel.For(0, height, j => {
                var sy = cy + (j - (height - 1) / 2d) * ratio;
                var y0 = Math.Clamp((int)Math.Floor(sy), 0, sourceHeight - 2);
                var fy = Math.Clamp(sy - y0, 0, 1);
                for (var i = 0; i < width; i++) {
                    var sx = cx + (i - (width - 1) / 2d) * ratio;
                    var x0 = Math.Clamp((int)Math.Floor(sx), 0, sourceWidth - 2);
                    var fx = Math.Clamp(sx - x0, 0, 1);
                    var top = source[y0 * sourceWidth + x0] * (1 - fx) + source[y0 * sourceWidth + x0 + 1] * fx;
                    var bottom = source[(y0 + 1) * sourceWidth + x0] * (1 - fx) + source[(y0 + 1) * sourceWidth + x0 + 1] * fx;
                    output[j * width + i] = (float)(top * (1 - fy) + bottom * fy);
                }
            });
            return output;
        }

        /// <summary>An RGGB mosaic (R at even row and column) of the resampled R, G and B planes, as a bin-2 OSC frame keeps it.</summary>
        private static float[] Bayer(FitsCube fits, double cx, double cy, double ratio) {
            int w = SolverKit.RigWidth, h = SolverKit.RigHeight;
            var r = Resample(fits.Planes[0], fits.Width, fits.Height, cx, cy, ratio, w, h);
            var g = Resample(fits.Planes[1], fits.Width, fits.Height, cx, cy, ratio, w, h);
            var b = Resample(fits.Planes[2], fits.Width, fits.Height, cx, cy, ratio, w, h);
            var mosaic = new float[w * h];
            for (var y = 0; y < h; y++) {
                for (var x = 0; x < w; x++) {
                    var i = y * w + x;
                    mosaic[i] = (y % 2, x % 2) switch {
                        (0, 0) => r[i],
                        (1, 1) => b[i],
                        _ => g[i]
                    };
                }
            }
            return mosaic;
        }

        /// <summary>The source pixels covering the rig's field around (cx, cy), uninterpolated.</summary>
        private static (float[] Pixels, int Width, int Height, int X0, int Y0) Crop(float[] source, int sourceWidth, double cx, double cy, double ratio) {
            var width = (int)Math.Round(SolverKit.RigWidth * ratio);
            var height = (int)Math.Round(SolverKit.RigHeight * ratio);
            var x0 = (int)Math.Round(cx - width / 2d);
            var y0 = (int)Math.Round(cy - height / 2d);
            var pixels = new float[width * height];
            for (var y = 0; y < height; y++) {
                Array.Copy(source, (y0 + y) * sourceWidth + x0, pixels, y * width, width);
            }
            return (pixels, width, height, x0, y0);
        }

        /// <summary>
        /// Stars the source data shows in the patch, independently of either solver: local maxima (3 x 3) more than 5 robust sigmas
        /// (1.4826 x MAD) above the median, at the stack's own resolution.
        /// </summary>
        private static int CountStars(float[] pixels, int width, int height) {
            var sorted = pixels.OrderBy(v => v).ToArray();
            var median = sorted[sorted.Length / 2];
            var deviations = pixels.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToArray();
            var threshold = median + 5 * 1.4826 * deviations[deviations.Length / 2];
            var count = 0;
            for (var y = 1; y < height - 1; y++) {
                for (var x = 1; x < width - 1; x++) {
                    var v = pixels[y * width + x];
                    if (v <= threshold) {
                        continue;
                    }
                    var isMax = true;
                    for (var dy = -1; dy <= 1 && isMax; dy++) {
                        for (var dx = -1; dx <= 1; dx++) {
                            if ((dx != 0 || dy != 0) && pixels[(y + dy) * width + x + dx] > v) {
                                isMax = false;
                                break;
                            }
                        }
                    }
                    if (isMax) {
                        count++;
                    }
                }
            }
            return count;
        }

        private static string Format(double value, string format) => double.IsNaN(value) ? "-" : value.ToString(format, CultureInfo.InvariantCulture);

        private void Emit(string title, List<string> rows) {
            var text = $"\n### {title}\n\n{string.Join("\n", rows)}\n";
            report.Append(text);
            TestContext.Progress.WriteLine(text);
        }
    }
}
