#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NINA.Astrometry;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Mac;
using NINA.PlateSolving.Solvers;
using NINA.Profile.Interfaces;
using System.Diagnostics;
using System.Text;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>
    /// Shared pieces for the mac solver tests: this rig's solve parameters, NINA image data as the capture path builds it,
    /// stand-in programs written as /bin/sh scripts, the installed solvers (found or not), and test subclasses that expose the
    /// solvers' protected steps, as upstream's own fixtures do.
    /// </summary>
    internal static class SolverKit {

        /// <summary>The rig: 2500 mm, 2.9 µm pixels at bin 2, a 1920 x 1080 frame.</summary>
        public const double RigFocalLength = 2500;
        public const double RigPixelSize = 2.9;
        public const int RigBinning = 2;
        public const int RigWidth = 1920;
        public const int RigHeight = 1080;

        public static PlateSolveParameter RigParameter(Coordinates? hint, int downSampleFactor = 2) {
            return new PlateSolveParameter {
                FocalLength = RigFocalLength,
                PixelSize = RigPixelSize,
                Binning = RigBinning,
                SearchRadius = 5,
                DownSampleFactor = downSampleFactor,
                MaxObjects = 500,
                DisableNotifications = true,
                Coordinates = hint
            };
        }

        public static PlateSolveImageProperties ImageProperties(PlateSolveParameter parameter, int width = RigWidth, int height = RigHeight) {
            var image = new Mock<IImageData>();
            image.SetupGet(x => x.Properties).Returns(new ImageProperties(width, height, 16, false, 0, 0));
            return PlateSolveImageProperties.Create(parameter, image.Object);
        }

        /// <summary>A real NINA BaseImageData over the pixels, as ExposureDataFactory builds it (star detection constructed, never run).</summary>
        public static IImageData ImageData(ushort[] pixels, int width, int height, bool isBayered = false, ImageMetaData? metaData = null) {
            var profile = new Mock<IProfileService>();
            return new BaseImageData(pixels, width, height, 16, isBayered, metaData ?? new ImageMetaData(), profile.Object, new StarDetection(), new StarAnnotator());
        }

        /// <summary>A small synthetic frame (a gradient), enough for NINA's FITS writer and for stand-in solvers.</summary>
        public static IImageData SmallImage(string? targetName = null, Coordinates? telescope = null) {
            const int width = 64, height = 36;
            var pixels = new ushort[width * height];
            for (var i = 0; i < pixels.Length; i++) {
                pixels[i] = (ushort)(1000 + i);
            }
            var meta = new ImageMetaData();
            if (targetName != null) {
                meta.Target.Name = targetName;
            }
            if (telescope != null) {
                meta.Telescope.Coordinates = telescope;
            }
            return ImageData(pixels, width, height, metaData: meta);
        }

        public static Coordinates Coordinates(double raDegrees, double decDegrees) {
            return new Coordinates(Angle.ByDegree(raDegrees), Angle.ByDegree(decDegrees), Epoch.J2000);
        }

        /// <summary>Writes an executable /bin/sh script.</summary>
        public static string WriteScript(string path, string body) {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n"), new UTF8Encoding(false));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            return path;
        }

        public static string Sq(string text) => AstapSetup.ShellQuote(text);

        public static bool ProcessIsAlive(int pid) {
            try {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            } catch (ArgumentException) {
                return false;
            } catch (InvalidOperationException) {
                return false;
            }
        }

        // Installed solvers, overridable through the environment. Tests that need one are Ignored when it is missing.
        public static string AstapExecutable => Environment.GetEnvironmentVariable("NINA_MAC_ASTAP")
            ?? Path.Combine(Home, "Astro", "astap", "cli", "astap_cli");

        public static string AstapDatabase => Environment.GetEnvironmentVariable("NINA_MAC_ASTAP_DB")
            ?? Path.Combine(Home, "Astro", "astap", "d80");

        public static string AstrometryBin => Environment.GetEnvironmentVariable("NINA_MAC_ASTROMETRY_BIN")
            ?? AstrometryNetSetup.HomebrewBinDirectory;

        public static string AstrometryIndex => Environment.GetEnvironmentVariable("NINA_MAC_ASTROMETRY_INDEX")
            ?? AstrometryNetSetup.DefaultIndexDirectory;

        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static bool HasAstap => File.Exists(AstapExecutable) && Directory.Exists(AstapDatabase);

        public static bool HasWcsInfo => File.Exists(Path.Combine(AstrometryBin, "wcsinfo"));

        public static bool HasSolveField => File.Exists(Path.Combine(AstrometryBin, "solve-field")) && HasWcsInfo
            && Directory.Exists(AstrometryIndex) && Directory.GetFiles(AstrometryIndex, "index-*.fits").Length > 0;
    }

    /// <summary>Exposes LocalPlateSolver's protected steps, as upstream's TestableLocalPlateSolver does.</summary>
    internal class TestableLocalPlateSolver : LocalPlateSolver {

        public TestableLocalPlateSolver(string binDirectory) : base(binDirectory) {
        }

        public TimeSpan? TimeoutOverride { get; set; }

        /// <summary>Called by ReadResult before the base implementation, while the log and sidecars still exist.</summary>
        public Action<string>? BeforeReadResult { get; set; }

        protected override TimeSpan SolverTimeout => TimeoutOverride ?? base.SolverTimeout;

        public string Arguments(string imageFilePath, string outputFilePath, PlateSolveParameter parameter, PlateSolveImageProperties imageProperties) =>
            GetArguments(imageFilePath, outputFilePath, parameter, imageProperties);

        public PlateSolveResult Result(string outputFilePath, PlateSolveParameter parameter, PlateSolveImageProperties imageProperties) =>
            ReadResult(outputFilePath, parameter, imageProperties);

        public void Validate(PlateSolveParameter parameter) => EnsureSolverValid(parameter);

        public List<string> SideCars(string imageFilePath) => GetSideCarFilePaths(imageFilePath);

        public string Output(string imageFilePath) => GetOutputPath(imageFilePath);

        public TimeSpan Timeout => SolverTimeout;

        public static string WorkingDirectory => WORKING_DIRECTORY;

        public static string FailedDirectory => FAILED_DIRECTORY;

        public static string FailedPrefix => FAILED_FILENAME;

        protected override PlateSolveResult ReadResult(string outputFilePath, PlateSolveParameter parameter, PlateSolveImageProperties imageProperties) {
            BeforeReadResult?.Invoke(outputFilePath);
            return base.ReadResult(outputFilePath, parameter, imageProperties);
        }
    }

    /// <summary>Exposes ASTAPSolver's protected steps, as upstream's TestableASTAPSolver does.</summary>
    internal sealed class TestableAstapSolver : ASTAPSolver {

        public TestableAstapSolver(string executableLocation) : base(executableLocation) {
        }

        public string Arguments(string imageFilePath, string outputFilePath, PlateSolveParameter parameter, PlateSolveImageProperties imageProperties) =>
            GetArguments(imageFilePath, outputFilePath, parameter, imageProperties);

        public PlateSolveResult Result(string outputFilePath, PlateSolveParameter parameter, PlateSolveImageProperties imageProperties) =>
            ReadResult(outputFilePath, parameter, imageProperties);

        public void Validate(PlateSolveParameter parameter) => EnsureSolverValid(parameter);
    }
}
