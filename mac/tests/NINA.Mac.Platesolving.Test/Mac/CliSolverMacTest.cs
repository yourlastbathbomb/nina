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
using NINA.Core.Model;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Solvers;
using System.Diagnostics;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>
    /// Upstream CLISolver's process handling on macOS, the behaviours upstream's CliSolverBehaviorTest checks with cmd.exe
    /// (skipped here, see MacPlatformSkips), run with /bin/sh instead: a successful solve deletes the temporary image, output
    /// and sidecar files; a failed one archives them; the solver-owned timeout kills a hung process. Plus one upstream
    /// behaviour the mac LocalPlateSolver works around: the solver's standard output is redirected and never read.
    /// The frame is a real NINA BaseImageData saved by NINA's FITS writer, not a mocked SaveToDisk.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class CliSolverMacTest {

        [Test]
        public async Task SolveAsync_Success_DeletesEveryTemporaryFile_AndFillsTheTargetFromTheTelescope() {
            var solver = new ShCliSolver("exit 0") { ShouldSucceed = true };
            var telescope = SolverKit.Coordinates(22.5, -11.25);
            IImageData image = SolverKit.SmallImage("CliSuccess", telescope);
            var statuses = new List<ApplicationStatus>();
            var parameter = new PlateSolveParameter { FocalLength = 2500, PixelSize = 2.9, Binning = 2, Coordinates = SolverKit.Coordinates(12, 34) };

            var result = await solver.SolveAsync(image, parameter, new Progress(statuses), CancellationToken.None);

            result.Success.Should().BeTrue();
            solver.OutputExistedDuringRead.Should().BeTrue();
            solver.ImageWasFits.Should().BeTrue("CLISolver hands the solver a FITS file written by NINA");
            image.MetaData.Target.Coordinates.RADegrees.Should().BeApproximately(22.5, 1e-10);
            File.Exists(solver.ImagePathSeen).Should().BeFalse();
            File.Exists(solver.OutputPathSeen).Should().BeFalse();
            File.Exists(solver.SidecarPathSeen).Should().BeFalse();
            statuses.Should().Contain(s => !string.IsNullOrWhiteSpace(s.Status));
            statuses.Last().Status.Should().BeEmpty();
        }

        [Test]
        public async Task SolveAsync_Failure_MovesTheTemporaryFilesToTheFailedArchive() {
            var solver = new ShCliSolver("exit 0") { ShouldSucceed = false };

            var result = await solver.SolveAsync(SolverKit.SmallImage("CliFailure"), new PlateSolveParameter { FocalLength = 2500, PixelSize = 2.9 }, null, CancellationToken.None);

            result.Success.Should().BeFalse();
            File.Exists(solver.ImagePathSeen).Should().BeFalse();
            File.Exists(solver.OutputPathSeen).Should().BeFalse();
            File.Exists(solver.SidecarPathSeen).Should().BeFalse();
            Directory.GetFiles(solver.FailedDirectory, $"{solver.FailedFilePrefix}.CliFailure.blind*").Should().HaveCount(3);
        }

        [Test]
        public async Task SolveAsync_TheSolverOwnedTimeout_KillsAHungProcess() {
            var solver = new ShCliSolver("sleep 6") { ShouldSucceed = true, Timeout = TimeSpan.FromMilliseconds(100) };

            var stopwatch = Stopwatch.StartNew();
            var result = await solver.SolveAsync(SolverKit.SmallImage("CliTimeout"), new PlateSolveParameter { FocalLength = 2500, PixelSize = 2.9 }, null, CancellationToken.None);
            stopwatch.Stop();

            result.Success.Should().BeFalse();
            solver.ReadResultCalled.Should().BeFalse();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        }

        [Test]
        public async Task SolveAsync_SolverOutputIsNeverRead_SoAChattySolverStallsUntilTheTimeout() {
            // Upstream CLISolver sets RedirectStandardOutput but never reads the pipe (no BeginOutputReadLine), so a program that
            // writes more than the pipe holds (64 KiB on macOS) blocks. Same code on Windows. ASTAP writes under 1 KiB; the mac
            // LocalPlateSolver sends solve-field's output to a log file (LocalPlateSolverMacTest.SolveAsync_AChattyBlindSolve_DoesNotStall).
            var solver = new ShCliSolver("head -c 1048576 /dev/zero; exit 0") { ShouldSucceed = true, Timeout = TimeSpan.FromSeconds(2) };

            var stopwatch = Stopwatch.StartNew();
            var result = await solver.SolveAsync(SolverKit.SmallImage("CliChatty"), new PlateSolveParameter { FocalLength = 2500, PixelSize = 2.9 }, null, CancellationToken.None);
            stopwatch.Stop();

            result.Success.Should().BeFalse("the program never finished writing");
            solver.ReadResultCalled.Should().BeFalse();
            stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9));

            var quiet = new ShCliSolver("head -c 16384 /dev/zero; exit 0") { ShouldSucceed = true, Timeout = TimeSpan.FromSeconds(10) };
            (await quiet.SolveAsync(SolverKit.SmallImage("CliQuiet"), new PlateSolveParameter { FocalLength = 2500, PixelSize = 2.9 }, null, CancellationToken.None))
                .Success.Should().BeTrue("16 KiB fits the pipe");
        }

        /// <summary>Upstream's TestableCliSolver with /bin/sh -c in place of cmd.exe /C.</summary>
        private sealed class ShCliSolver : CLISolver {

            private readonly string script;

            public ShCliSolver(string script) : base("/bin/sh") {
                this.script = script;
                var root = TestHost.NewFolder("cli solver");
                WORKING_DIRECTORY = Path.Combine(root, "Working");
                FAILED_DIRECTORY = Path.Combine(root, "Failed");
                FAILED_FILENAME = Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(WORKING_DIRECTORY);
                Directory.CreateDirectory(FAILED_DIRECTORY);
            }

            public bool ShouldSucceed { get; set; }
            public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(1);
            public bool OutputExistedDuringRead { get; private set; }
            public bool ReadResultCalled { get; private set; }
            public bool ImageWasFits { get; private set; }
            public string? ImagePathSeen { get; private set; }
            public string? OutputPathSeen { get; private set; }
            public string? SidecarPathSeen { get; private set; }
            public string FailedDirectory => FAILED_DIRECTORY;
            public string FailedFilePrefix => FAILED_FILENAME;

            protected override string GetLocalizedPlateSolverName() => "test solver";

            protected override TimeSpan SolverTimeout => Timeout;

            protected override string GetArguments(string imageFilePath, string outputFilePath, PlateSolveParameter parameter, PlateSolveImageProperties imageProperties) {
                ImagePathSeen = imageFilePath;
                OutputPathSeen = outputFilePath;
                SidecarPathSeen = GetSideCarFilePaths(imageFilePath).Single();
                using (var stream = File.OpenRead(imageFilePath)) {
                    var head = new byte[30];
                    stream.ReadExactly(head);
                    ImageWasFits = System.Text.Encoding.ASCII.GetString(head) == "SIMPLE  =                    T";
                }
                return "-c \"" + script + "\"";
            }

            protected override PlateSolveResult ReadResult(string outputFilePath, PlateSolveParameter parameter, PlateSolveImageProperties imageProperties) {
                ReadResultCalled = true;
                File.WriteAllText(outputFilePath, "solver output");
                File.WriteAllText(SidecarPathSeen!, "sidecar output");
                OutputExistedDuringRead = File.Exists(outputFilePath);
                return new PlateSolveResult { Success = ShouldSucceed, Coordinates = SolverKit.Coordinates(1, 2) };
            }

            protected override string GetOutputPath(string imageFilePath) => Path.ChangeExtension(imageFilePath, ".solverout");

            protected override List<string> GetSideCarFilePaths(string imageFilePath) => new() { imageFilePath + ".sidecar" };
        }

        private sealed class Progress : IProgress<ApplicationStatus> {
            private readonly List<ApplicationStatus> values;

            public Progress(List<ApplicationStatus> values) {
                this.values = values;
            }

            public void Report(ApplicationStatus value) => values.Add(value);
        }
    }
}
