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
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.PlateSolving.Mac;
using NINA.PlateSolving.Solvers;
using NINA.Profile.Interfaces;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>
    /// The mac LocalPlateSolver (MacReplacements/LocalPlateSolver.cs, bug 2 of the M3b plan). Arguments are checked as text and,
    /// end to end, as the argv a stand-in solve-field receives when NINA's real CLISolver starts it: SolveAsync saves the frame
    /// with NINA's FITS writer, runs /bin/sh with the launcher script, then the stand-in wcsinfo, parses the result and cleans up.
    /// The stand-ins are sh scripts in a folder whose path has spaces, like everything under ~/Library/Application Support.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class LocalPlateSolverMacTest {

        private static readonly Coordinates M51 = SolverKit.Coordinates(202.4696, 47.1952);

        private string folder = string.Empty;
        private IReadOnlyList<string> savedIndexDirectories = Array.Empty<string>();

        [SetUp]
        public void SetUp() {
            folder = TestHost.NewFolder("local solver");
            savedIndexDirectories = AstrometryNetSetup.IndexDirectories;
            var index = Directory.CreateDirectory(Path.Combine(folder, "Application Support", "Astrometry")).FullName;
            File.WriteAllText(Path.Combine(index, "index-4205-00.fits"), "stand-in");
            AstrometryNetSetup.IndexDirectories = new[] { index };
        }

        [TearDown]
        public void TearDown() {
            AstrometryNetSetup.IndexDirectories = savedIndexDirectories;
            AstrometryNetSetup.ConfigDirectory = null;
        }

        [Test]
        public void Factory_CreatesTheMacSolver_WithHomebrewAsTheDefaultFolder() {
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupAllProperties();
            settings.Object.PlateSolverType = PlateSolverEnum.LOCAL;
            settings.Object.BlindSolverType = BlindSolverEnum.LOCAL;
            settings.Object.CygwinLocation = string.Empty;

            var near = PlateSolverFactory.GetPlateSolver(settings.Object);
            var blind = PlateSolverFactory.GetBlindSolver(settings.Object);

            near.Should().BeOfType<LocalPlateSolver>().Which.SolveFieldPath.Should().Be("/opt/homebrew/bin/solve-field");
            blind.Should().BeOfType<LocalPlateSolver>().Which.WcsInfoPath.Should().Be("/opt/homebrew/bin/wcsinfo");
            new LocalPlateSolver("/opt/local/bin").SolveFieldPath.Should().Be("/opt/local/bin/solve-field");
        }

        [Test]
        public void Arguments_ForTheRig_AreUpstreamsOptions_WithCrpixCenter_AndTheConfig() {
            var parameter = SolverKit.RigParameter(M51);
            var properties = SolverKit.ImageProperties(parameter);
            var solver = new TestableLocalPlateSolver("/opt/homebrew/bin");
            const string image = "/Users/me/Library/Application Support/NINA/PlateSolver/abc.fits";

            var args = solver.Arguments(image, "/x/abc.wcs", parameter, properties);

            properties.ArcSecPerPixel.Should().BeApproximately(0.4785, 5e-5);
            args.Should().Be(
                "-c \"PATH=$1; export PATH; log=$2; mkdir -p \\\"$3\\\" 2>\\\"$log\\\" || exit 1; shift 2; exec \\\"$0\\\" --temp-dir \\\"$@\\\" >\\\"$log\\\" 2>&1\" " +
                "\"/opt/homebrew/bin/solve-field\" " +
                "\"/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin\" " +
                "\"/Users/me/Library/Application Support/NINA/PlateSolver/abc.solve-field.log\" " +
                "\"/Users/me/Library/Application Support/NINA/PlateSolver/abc.solve-field-tmp\" " +
                $"--config \"{AstrometryNetSetup.ConfigFilePath}\" " +
                "--overwrite --index-xyls none --corr none --rdls none --match none --new-fits none --crpix-center " +
                "--objs 500 --no-plots --resort --downsample 2 --scale-units arcsecperpix -L 0.28 -H 0.68 " +
                "--ra 202.47 --dec 47.20 --radius 5.00 " +
                "\"/Users/me/Library/Application Support/NINA/PlateSolver/abc.fits\"");
            args.Split(' ').Should().NotContain("-center", "upstream's -center is read by getopt_long as -c enter");
        }

        [Test]
        public void Arguments_ForABlindSolve_HaveOnlyTheScaleHint() {
            var parameter = SolverKit.RigParameter(hint: null);
            var args = new TestableLocalPlateSolver("").Arguments("/d/f.fits", "/d/f.wcs", parameter, SolverKit.ImageProperties(parameter));

            args.Should().Contain("-L 0.28 -H 0.68 \"/d/f.fits\"");
            args.Should().NotContain("--ra").And.NotContain("--dec").And.NotContain("--radius");
        }

        [Test]
        public void Files_AreUpstreamsWcsOutput_PlusSolveFieldsLeftoversAndTheLog() {
            var solver = new TestableLocalPlateSolver("");

            solver.Output("/w/a b.fits").Should().Be("/w/a b.wcs");
            solver.SideCars("/w/a b.fits").Should().Equal("/w/a b.axy", "/w/a b.solved", "/w/a b.solve-field.log");
            LocalPlateSolver.GetTempDirectory("/w/a b.fits").Should().Be("/w/a b.solve-field-tmp");
            solver.Timeout.Should().Be(AstrometryNetSetup.SolverTimeout);
        }

        [Test]
        public void Validation_NamesWhatIsMissing() {
            var empty = Directory.CreateDirectory(Path.Combine(folder, "empty bin")).FullName;
            new TestableLocalPlateSolver(empty).Invoking(s => s.Validate(SolverKit.RigParameter(M51)))
                .Should().Throw<FileNotFoundException>().WithMessage($"*solve-field not found at {empty}/solve-field*brew install astrometry-net*");

            var bin = StandIns(SolveFieldMode.Success);
            File.Delete(Path.Combine(bin, "wcsinfo"));
            new TestableLocalPlateSolver(bin).Invoking(s => s.Validate(SolverKit.RigParameter(M51)))
                .Should().Throw<FileNotFoundException>().WithMessage("*wcsinfo not found*");

            var colon = Path.Combine(folder, "a:b");
            Directory.CreateDirectory(colon);
            File.Copy(Path.Combine(bin, "solve-field"), Path.Combine(colon, "solve-field"));
            File.WriteAllText(Path.Combine(colon, "wcsinfo"), "");
            new TestableLocalPlateSolver(colon).Invoking(s => s.Validate(SolverKit.RigParameter(M51)))
                .Should().Throw<ArgumentException>().WithMessage("*must not contain ':'*");

            bin = StandIns(SolveFieldMode.Success);
            AstrometryNetSetup.IndexDirectories = new[] { Directory.CreateDirectory(Path.Combine(folder, "no index")).FullName };
            new TestableLocalPlateSolver(bin).Invoking(s => s.Validate(SolverKit.RigParameter(M51)))
                .Should().Throw<InvalidOperationException>().WithMessage("*no index files*");
        }

        [Test]
        public async Task SolveAsync_RunsSolveFieldWithExactlyTheseArguments_ThenWcsInfo_AndCleansUp() {
            var bin = StandIns(SolveFieldMode.Success);
            var solver = new TestableLocalPlateSolver(bin);
            string? log = null, outputPath = null;
            var leftovers = new List<string>();
            var tempFilesDuringSolve = new List<string>();
            solver.BeforeReadResult = wcs => {
                outputPath = wcs;
                var image = Path.ChangeExtension(wcs, ".fits");
                leftovers.AddRange(solver.SideCars(image).Where(File.Exists));
                log = File.ReadAllText(LocalPlateSolver.GetLogPath(image));
                tempFilesDuringSolve.AddRange(Directory.GetFiles(LocalPlateSolver.GetTempDirectory(image)).Select(f => Path.GetFileName(f)));
            };
            var parameter = SolverKit.RigParameter(M51);

            var result = await ((IPlateSolver)solver).SolveAsync(SolverKit.SmallImage("LocalSuccess"), parameter, null, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Coordinates.RADegrees.Should().BeApproximately(202.4696, 1e-9);
            result.Coordinates.Dec.Should().BeApproximately(47.1952, 1e-9);
            result.Pixscale.Should().BeApproximately(0.4785, 1e-9);
            result.PositionAngle.Should().BeApproximately(AstroUtil.EuclidianModulus(-150.0 - 180, 360), 1e-9);

            var image = Path.ChangeExtension(outputPath!, ".fits");
            Path.GetDirectoryName(image).Should().Be(TestableLocalPlateSolver.WorkingDirectory);
            image.Should().Contain(" ", "NINA's data folder in this test has spaces, as ~/Library/Application Support does");
            File.ReadAllLines(Path.Combine(folder, "solve-field argv.txt")).Should().Equal(
                "$0=" + Path.Combine(bin, "solve-field"),
                "--temp-dir", LocalPlateSolver.GetTempDirectory(image),
                "--config", AstrometryNetSetup.ConfigFilePath,
                "--overwrite", "--index-xyls", "none", "--corr", "none", "--rdls", "none", "--match", "none", "--new-fits", "none",
                "--crpix-center", "--objs", "500", "--no-plots", "--resort", "--downsample", "2",
                "--scale-units", "arcsecperpix", "-L", "0.28", "-H", "0.68",
                "--ra", "202.47", "--dec", "47.20", "--radius", "5.00",
                image,
                "PATH=" + bin + ":/usr/bin:/bin:/usr/sbin:/sbin");
            File.ReadAllLines(Path.Combine(folder, "wcsinfo argv.txt")).Should().Equal(outputPath);
            File.ReadAllText(AstrometryNetSetup.ConfigFilePath).Should().Contain("add_path " + AstrometryNetSetup.IndexDirectories[0] + "\n");

            log.Should().Contain("Field 1: solved with index index-4205-05.fits.").And.Contain("stand-in stderr line", "stdout and stderr go to the log");
            leftovers.Select(Path.GetExtension).Should().BeEquivalentTo(new[] { ".axy", ".solved", ".log" });
            File.Exists(image).Should().BeFalse();
            File.Exists(outputPath).Should().BeFalse();
            leftovers.Should().OnlyContain(f => !File.Exists(f), "solve-field's .axy and .solved and the log go with the image");
            tempFilesDuringSolve.Should().Equal("tmp.ppm.standin");
            Directory.Exists(LocalPlateSolver.GetTempDirectory(image)).Should().BeFalse("solve-field's temporary folder is deleted after the solve");
        }

        [Test]
        public async Task SolveAsync_WhenSolveFieldFails_ArchivesTheImageTheSourceListAndTheLog() {
            var bin = StandIns(SolveFieldMode.NoSolution);
            IPlateSolver solver = new TestableLocalPlateSolver(bin);

            var result = await solver.SolveAsync(SolverKit.SmallImage("LocalFailure"), SolverKit.RigParameter(hint: null), null, CancellationToken.None);

            result.Success.Should().BeFalse();
            var archived = Directory.GetFiles(TestableLocalPlateSolver.FailedDirectory, "*.LocalFailure.blind.*");
            archived.Select(Path.GetExtension).Should().BeEquivalentTo(new[] { ".fits", ".axy", ".log" });
            Directory.Exists(RecordedTempDirectory()).Should().BeFalse("the temporary folder is deleted after a failed solve too");
            Directory.GetDirectories(TestableLocalPlateSolver.WorkingDirectory, "*.solve-field-tmp").Should().BeEmpty();
            File.ReadAllText(archived.Single(f => f.EndsWith(".log", StringComparison.Ordinal))).Should().Contain("Did not solve");
            File.Exists(Path.Combine(folder, "wcsinfo argv.txt")).Should().BeFalse("without a .wcs file wcsinfo is not run");
        }

        [Test]
        public async Task SolveAsync_AChattyBlindSolve_DoesNotStall() {
            // CLISolver redirects the solver's standard output and never reads it; a blind solve that prints a line per index
            // tried would block once the pipe is full. The launcher sends the output to the log instead (1.5 MB here).
            var bin = StandIns(SolveFieldMode.ChattySuccess);
            var solver = new TestableLocalPlateSolver(bin) { TimeoutOverride = TimeSpan.FromSeconds(30) };
            long logLength = 0;
            solver.BeforeReadResult = wcs => logLength = new FileInfo(LocalPlateSolver.GetLogPath(Path.ChangeExtension(wcs, ".fits"))).Length;

            var stopwatch = Stopwatch.StartNew();
            var result = await ((IPlateSolver)solver).SolveAsync(SolverKit.SmallImage("LocalChatty"), SolverKit.RigParameter(hint: null), null, CancellationToken.None);
            stopwatch.Stop();

            result.Success.Should().BeTrue();
            logLength.Should().BeGreaterThan(1_500_000);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        }

        [Test]
        public async Task SolveAsync_Timeout_KillsSolveFieldAndItsChildren() {
            var bin = StandIns(SolveFieldMode.Hang);
            var solver = new TestableLocalPlateSolver(bin) { TimeoutOverride = TimeSpan.FromSeconds(1.5) };

            var stopwatch = Stopwatch.StartNew();
            var result = await ((IPlateSolver)solver).SolveAsync(SolverKit.SmallImage("LocalTimeout"), SolverKit.RigParameter(hint: null), null, CancellationToken.None);
            stopwatch.Stop();

            result.Success.Should().BeFalse();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            var child = int.Parse(File.ReadAllText(Path.Combine(folder, "child.pid")).Trim(), CultureInfo.InvariantCulture);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (SolverKit.ProcessIsAlive(child) && DateTime.UtcNow < deadline) {
                await Task.Delay(50);
            }
            SolverKit.ProcessIsAlive(child).Should().BeFalse("CLISolver kills the whole process tree on timeout, and the launcher execs solve-field");
            File.Exists(Path.Combine(RecordedTempDirectory(), "tmp.ppm.standin")).Should().BeFalse();
            Directory.Exists(RecordedTempDirectory()).Should().BeFalse("solve-field cannot clean up when it is killed; the solver deletes its temporary folder");
        }

        [Test]
        public void ParseWcsInfo_ReadsUpstreamsKeys() {
            var parameter = SolverKit.RigParameter(M51);
            var result = LocalPlateSolver.ParseWcsInfo(WcsInfoFixture(), SolverKit.ImageProperties(parameter));

            result.Success.Should().BeTrue();
            result.Coordinates.RADegrees.Should().BeApproximately(202.4696, 1e-9);
            result.Coordinates.Dec.Should().BeApproximately(47.1952, 1e-9);
            result.Coordinates.Epoch.Should().Be(Epoch.J2000);
            result.Pixscale.Should().BeApproximately(0.4785, 1e-9);
            result.Radius.Should().BeApproximately(Math.Sqrt(1920.0 * 1920 + 1080.0 * 1080) * 0.4785 / 2 / 3600, 1e-9);
            // Upstream: PositionAngle = 360 - (180 - orientation_center + 360), i.e. orientation - 180
            result.PositionAngle.Should().BeApproximately(30, 1e-9);
            // Upstream's parity rule for both CLI solvers: Flipped = !WorldCoordinateSystem.Flipped. This CD matrix has a negative
            // determinant, for which WorldCoordinateSystem.Flipped is false
            result.Flipped.Should().BeTrue();
        }

        [Test]
        public void ParseWcsInfo_WithoutAFieldCenter_IsNotASuccess() {
            // Upstream reports success with coordinates 0/0 whenever the .wcs file exists; the mac solver requires the center
            var lines = WcsInfoFixture().Where(l => !l.StartsWith("ra_center ", StringComparison.Ordinal)).ToList();
            LocalPlateSolver.ParseWcsInfo(lines, SolverKit.ImageProperties(SolverKit.RigParameter(M51))).Success.Should().BeFalse();
            LocalPlateSolver.ParseWcsInfo(Array.Empty<string>(), SolverKit.ImageProperties(SolverKit.RigParameter(M51))).Success.Should().BeFalse();
        }

        [Test]
        public void ReadResult_WithTheRealWcsInfo_AgreesWithAstapsConvention() {
            if (!SolverKit.HasWcsInfo) {
                Assert.Ignore($"astrometry.net wcsinfo not installed in {SolverKit.AstrometryBin}");
            }
            // A .wcs file as solve-field writes it (a FITS header, no data) for the rig's 1920 x 1080 field at 0.4785"/px,
            // rotated by 30 degrees, read by the installed wcsinfo through ReadResult
            var scale = 0.4785 / 3600;
            var theta = 30 * Math.PI / 180;
            double cd11 = -scale * Math.Cos(theta), cd12 = scale * Math.Sin(theta), cd21 = scale * Math.Sin(theta), cd22 = scale * Math.Cos(theta);
            var wcsFile = Path.Combine(folder, "rig field.wcs");
            WriteWcsHeader(wcsFile, 202.4696, 47.1952, 960.5, 540.5, cd11, cd12, cd21, cd22, 1920, 1080);
            var parameter = SolverKit.RigParameter(M51);
            var properties = SolverKit.ImageProperties(parameter);

            var local = new TestableLocalPlateSolver(SolverKit.AstrometryBin).Result(wcsFile, parameter, properties);

            local.Success.Should().BeTrue();
            local.Coordinates.RADegrees.Should().BeApproximately(202.4696, 1e-7);
            local.Coordinates.Dec.Should().BeApproximately(47.1952, 1e-7);
            local.Pixscale.Should().BeApproximately(0.4785, 1e-6);

            // The same WCS as astap_cli would report it: both upstream conventions must give the same position angle and parity
            var ini = Path.Combine(folder, "rig field.ini");
            File.WriteAllLines(ini, new[] {
                "PLTSOLVD=T", "CRPIX1=960.5", "CRPIX2=540.5", "CRVAL1=202.4696", "CRVAL2=47.1952",
                "CD1_1=" + cd11.ToString("R", CultureInfo.InvariantCulture), "CD1_2=" + cd12.ToString("R", CultureInfo.InvariantCulture),
                "CD2_1=" + cd21.ToString("R", CultureInfo.InvariantCulture), "CD2_2=" + cd22.ToString("R", CultureInfo.InvariantCulture),
            });
            var astap = new TestableAstapSolver("/x/astap").Result(ini, parameter, properties);
            TestContext.Out.WriteLine($"position angle: solve-field {local.PositionAngle:0.0000}, ASTAP {astap.PositionAngle:0.0000}; flipped {local.Flipped}/{astap.Flipped}");
            local.PositionAngle.Should().BeApproximately(astap.PositionAngle, 1e-3);
            local.Flipped.Should().Be(astap.Flipped);
        }

        internal static IReadOnlyList<string> WcsInfoFixture() {
            // wcsinfo's output format ("key value" per line) for a 1920 x 1080 field at 0.4785"/px, up 150 degrees W of N
            return new[] {
                "crpix0 960.5", "crpix1 540.5", "crval0 202.4696", "crval1 47.1952",
                "ra_tangent 202.4696", "dec_tangent 47.1952", "pixx_tangent 960.5", "pixy_tangent 540.5",
                "imagew 1920", "imageh 1080",
                "cd11 -0.000115114583333", "cd12 6.6461e-05", "cd21 6.6461e-05", "cd22 0.000115114583333",
                "det -1.7667e-08", "parity 0", "pixscale 0.4785", "orientation -150", "ra_center 202.4696", "dec_center 47.1952",
                "orientation_center -150", "ra_center_hms 13:29:52.704", "dec_center_dms +47:11:42.720",
                "fieldarea 0.0365", "fieldw 15.312", "fieldh 8.613", "fieldunits arcminutes",
            };
        }

        private enum SolveFieldMode { Success, NoSolution, ChattySuccess, Hang }

        /// <summary>The --temp-dir that the stand-in solve-field received (and wrote a file into).</summary>
        private string RecordedTempDirectory() => File.ReadAllText(Path.Combine(folder, "temp dir.txt")).TrimEnd('\n');

        /// <summary>Stand-in solve-field and wcsinfo in "&lt;folder&gt;/stand in bin". They record their argv next to the folder.</summary>
        private string StandIns(SolveFieldMode mode) {
            var bin = Path.Combine(folder, "stand in bin " + mode);
            var argv = Path.Combine(folder, "solve-field argv.txt");
            var script = new StringBuilder();
            script.Append($"rec={SolverKit.Sq(argv)}\n");
            script.Append("printf '$0=%s\\n' \"$0\" > \"$rec\"\n");
            script.Append("for a in \"$@\"; do printf '%s\\n' \"$a\" >> \"$rec\"; done\n");
            script.Append("printf 'PATH=%s\\n' \"$PATH\" >> \"$rec\"\n");
            script.Append("prev=; tmpdir=\n");
            script.Append("for a in \"$@\"; do if [ \"$prev\" = --temp-dir ]; then tmpdir=$a; fi; prev=$a; done\n");
            script.Append($"printf '%s\\n' \"$tmpdir\" > {SolverKit.Sq(Path.Combine(folder, "temp dir.txt"))}\n");
            script.Append("printf 'ppm' > \"$tmpdir/tmp.ppm.standin\" || exit 3\n");
            script.Append("for img in \"$@\"; do :; done\n");
            script.Append("stem=\"${img%.*}\"\n");
            script.Append("printf 'axy' > \"$stem.axy\"\n");
            script.Append("echo \"Reading input file 1 of 1: $img\"\n");
            script.Append("echo 'stand-in stderr line' >&2\n");
            switch (mode) {
                case SolveFieldMode.Success:
                    script.Append("printf 'wcs' > \"$stem.wcs\"\n: > \"$stem.solved\"\necho 'Field 1: solved with index index-4205-05.fits.'\n");
                    break;
                case SolveFieldMode.NoSolution:
                    script.Append("echo 'Did not solve (or no WCS file was written).'\n");
                    break;
                case SolveFieldMode.ChattySuccess:
                    script.Append("i=0\nwhile [ $i -lt 16000 ]; do echo \"Field 1 did not solve (index index-4202-$i.fits, field objects 1-10). ........................\"; i=$((i+1)); done\n");
                    script.Append("printf 'wcs' > \"$stem.wcs\"\n");
                    break;
                case SolveFieldMode.Hang:
                    script.Append($"sleep 60 &\necho $! > {SolverKit.Sq(Path.Combine(folder, "child.pid"))}\nwait\n");
                    break;
            }
            SolverKit.WriteScript(Path.Combine(bin, "solve-field"), script.ToString());

            var wcsinfo = new StringBuilder();
            wcsinfo.Append($"printf '%s\\n' \"$@\" > {SolverKit.Sq(Path.Combine(folder, "wcsinfo argv.txt"))}\n");
            foreach (var line in WcsInfoFixture()) {
                wcsinfo.Append($"echo '{line}'\n");
            }
            SolverKit.WriteScript(Path.Combine(bin, "wcsinfo"), wcsinfo.ToString());
            return bin;
        }

        /// <summary>A header-only FITS file with a TAN WCS, as solve-field's .wcs output.</summary>
        internal static void WriteWcsHeader(string path, double crval1, double crval2, double crpix1, double crpix2,
            double cd11, double cd12, double cd21, double cd22, int width, int height) {
            var cards = new List<string> {
                Card("SIMPLE", "T"), Card("BITPIX", "8"), Card("NAXIS", "0"), Card("EXTEND", "T"), Card("WCSAXES", "2"),
                Card("CTYPE1", "'RA---TAN'"), Card("CTYPE2", "'DEC--TAN'"), Card("EQUINOX", "2000.0"),
                Card("LONPOLE", "180.0"), Card("LATPOLE", "0.0"),
                Card("CRVAL1", Num(crval1)), Card("CRVAL2", Num(crval2)), Card("CRPIX1", Num(crpix1)), Card("CRPIX2", Num(crpix2)),
                Card("CUNIT1", "'deg'"), Card("CUNIT2", "'deg'"),
                Card("CD1_1", Num(cd11)), Card("CD1_2", Num(cd12)), Card("CD2_1", Num(cd21)), Card("CD2_2", Num(cd22)),
                Card("IMAGEW", width.ToString(CultureInfo.InvariantCulture)), Card("IMAGEH", height.ToString(CultureInfo.InvariantCulture)),
                "END".PadRight(80),
            };
            var text = string.Concat(cards);
            text = text.PadRight((text.Length + 2879) / 2880 * 2880);
            File.WriteAllText(path, text, Encoding.ASCII);

            static string Card(string key, string value) => (key.PadRight(8) + "= " + value.PadLeft(20)).PadRight(80);
            static string Num(double v) => v.ToString("E15", CultureInfo.InvariantCulture);
        }
    }
}
