#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Mac;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.PlateSolving.Solvers {

    /// <summary>
    /// Mac replacement for upstream NINA.Platesolving/Solvers/LocalPlateSolver.cs (excluded in the csproj): the local
    /// astrometry.net solver (PlateSolverEnum.LOCAL, BlindSolverEnum.LOCAL), same internal type and constructor, so
    /// PlateSolverFactory creates it unchanged. Upstream runs cmd.exe, then Cygwin bash, then /usr/bin/solve-field and wcsinfo.
    /// Here solve-field and wcsinfo run directly from the folder in CygwinLocation (empty: /opt/homebrew/bin).
    /// <para>Differences from upstream, all deliberate:</para>
    /// <list type="bullet">
    /// <item>The option list is upstream's, except that upstream's "-center" (which getopt_long reads as "-c enter", a code
    /// tolerance of 0) is the intended "--crpix-center" (plan P4), and "--config" names the Nightglass-owned astrometry.cfg
    /// (<see cref="AstrometryNetSetup"/>).</item>
    /// <item>solve-field runs under /bin/sh, which sets PATH (solve-field calls netpbm's pnmfile through the shell, and an app
    /// started from Finder has no Homebrew folder on its PATH) and sends its output to a log file next to the image. CLISolver
    /// redirects the solver's standard output but never reads it, so a chatty blind solve could fill the pipe and stall
    /// until the timeout; the log is archived with the other files when a solve fails, and its tail is logged.</item>
    /// <item>The .axy and .solved files that solve-field leaves next to the image are deleted with it (upstream leaves them).</item>
    /// <item>solve-field's own temporary files (an uncompressed copy and a PPM/PGM conversion of the image, 2 MB for a bin-2
    /// frame) go to a folder next to the image ("--temp-dir"), which is deleted after every solve. By default they go to /tmp,
    /// and solve-field leaves them there whenever it does not finish by itself, e.g. when the timeout kills it.</item>
    /// <item>A result needs wcsinfo's ra_center and dec_center. Upstream reports success with coordinates 0/0 whenever the .wcs
    /// file exists, even if wcsinfo printed nothing.</item>
    /// <item>The solver timeout is the engine's CPU limit plus a minute (<see cref="AstrometryNetSetup.SolverTimeout"/>), not
    /// CLISolver's 10 minutes. The engine overshoots its CPU limit, so for a failing solve the timeout usually ends it.</item>
    /// </list>
    /// </summary>
    internal class LocalPlateSolver : CLISolver {

        /// <summary>The launcher. Its script sets PATH, redirects the output and execs solve-field ($0) with its arguments.</summary>
        internal const string Shell = "/bin/sh";

        /// <summary>
        /// /bin/sh -c script. Positional parameters: $0 solve-field, $1 PATH, $2 log file, $3 solve-field's temporary folder (created
        /// here, passed on as --temp-dir), then solve-field's own arguments. Paths travel as arguments, never inside the script, so no
        /// path is ever parsed by the shell.
        /// </summary>
        internal const string LauncherScript = "PATH=$1; export PATH; log=$2; mkdir -p \"$3\" 2>\"$log\" || exit 1; shift 2; exec \"$0\" --temp-dir \"$@\" >\"$log\" 2>&1";

        /// <summary>The PATH solve-field runs with, after its own folder (netpbm and the Python helpers live there too).</summary>
        internal const string SystemPath = "/usr/bin:/bin:/usr/sbin:/sbin";

        private readonly string binDirectory;

        /// <summary>The temporary folder of the solve running in this async flow, recorded by GetArguments for the clean-up.</summary>
        private static readonly AsyncLocal<StrongBox<string>> currentTempDirectory = new AsyncLocal<StrongBox<string>>();

        public LocalPlateSolver(string cygwinRoot)
            : base(Shell) {
            this.binDirectory = AstrometryNetSetup.ResolveBinDirectory(cygwinRoot);
        }

        internal string BinDirectory => binDirectory;

        internal string SolveFieldPath => Path.Combine(binDirectory, "solve-field");

        internal string WcsInfoPath => Path.Combine(binDirectory, "wcsinfo");

        protected override TimeSpan SolverTimeout => AstrometryNetSetup.SolverTimeout;

        /// <summary>CLISolver's solve, then the temporary folder is deleted, whatever the outcome (solved, failed, timed out, cancelled).</summary>
        protected override async Task<PlateSolveResult> SolveAsyncImpl(
            IImageData source,
            PlateSolveParameter parameter,
            PlateSolveImageProperties imageProperties,
            IProgress<ApplicationStatus> progress,
            CancellationToken cancelToken) {
            var tempDirectory = new StrongBox<string>();
            currentTempDirectory.Value = tempDirectory;
            try {
                return await base.SolveAsyncImpl(source, parameter, imageProperties, progress, cancelToken);
            } finally {
                DeleteTempDirectory(tempDirectory.Value);
            }
        }

        protected override void EnsureSolverValid(PlateSolveParameter parameter) {
            if (!File.Exists(SolveFieldPath)) {
                throw new FileNotFoundException(
                    $"astrometry.net solve-field not found at {SolveFieldPath}. Install it (brew install astrometry-net) or set the " +
                    $"local plate solver folder to the folder that holds solve-field (empty means {AstrometryNetSetup.HomebrewBinDirectory}).",
                    SolveFieldPath);
            }
            if (!File.Exists(WcsInfoPath)) {
                throw new FileNotFoundException($"astrometry.net wcsinfo not found at {WcsInfoPath}", WcsInfoPath);
            }
            if (binDirectory.Contains(':')) {
                // The folder goes onto solve-field's PATH, where ':' separates entries
                throw new ArgumentException($"The astrometry.net folder must not contain ':' ({binDirectory})");
            }
            AstrometryNetSetup.WriteConfig();
        }

        protected override string GetArguments(
            string imageFilePath,
            string outputFilePath,
            PlateSolveParameter parameter,
            PlateSolveImageProperties imageProperties) {
            List<string> options = new List<string>();

            options.Add("--overwrite");
            options.Add("--index-xyls none");
            options.Add("--corr none");
            options.Add("--rdls none");
            options.Add("--match none");
            options.Add("--new-fits none");
            // Upstream passes "-center", which getopt_long parses as "-c enter" (code tolerance 0); the intent is --crpix-center
            options.Add("--crpix-center");
            options.Add($"--objs {parameter.MaxObjects}");
            options.Add("--no-plots");
            options.Add("--resort");
            options.Add($"--downsample {parameter.DownSampleFactor}");
            var lowArcSecPerPix = imageProperties.ArcSecPerPixel - 0.2;
            var highArcSecPerPix = imageProperties.ArcSecPerPixel + 0.2;
            options.Add("--scale-units arcsecperpix");
            options.Add(string.Format("-L {0}", lowArcSecPerPix.ToString("0.00", CultureInfo.InvariantCulture)));
            options.Add(string.Format("-H {0}", highArcSecPerPix.ToString("0.00", CultureInfo.InvariantCulture)));

            if (parameter.SearchRadius > 0 && parameter.Coordinates != null) {
                options.Add($"--ra {parameter.Coordinates.RADegrees.ToString("0.00", CultureInfo.InvariantCulture)}");
                options.Add($"--dec {parameter.Coordinates.Dec.ToString("0.00", CultureInfo.InvariantCulture)}");
                options.Add($"--radius {parameter.SearchRadius.ToString("0.00", CultureInfo.InvariantCulture)}");
            }

            var tempDirectory = GetTempDirectory(imageFilePath);
            if (currentTempDirectory.Value != null) {
                currentTempDirectory.Value.Value = tempDirectory;
            }
            var launcher = new List<string> {
                "-c", Quote(LauncherScript),
                Quote(SolveFieldPath),
                Quote(binDirectory + ":" + SystemPath),
                Quote(GetLogPath(imageFilePath)),
                Quote(tempDirectory),
                "--config", Quote(AstrometryNetSetup.ConfigFilePath)
            };
            return string.Join(" ", launcher.Concat(options).Append(Quote(imageFilePath)));
        }

        protected override PlateSolveResult ReadResult(string outputFilePath, PlateSolveParameter parameter, PlateSolveImageProperties imageProperties) {
            if (!File.Exists(outputFilePath)) {
                Logger.Error($"astrometry.net - Plate solve failed. No WCS file found.{LogTail(outputFilePath)}");
                return new PlateSolveResult() { Success = false };
            }
            var lines = RunWcsInfo(outputFilePath);
            var result = ParseWcsInfo(lines, imageProperties);
            if (!result.Success) {
                Logger.Error($"astrometry.net - wcsinfo gave no field center for {outputFilePath}: {string.Join(" | ", lines)}");
            }
            return result;
        }

        /// <summary>
        /// Turns wcsinfo's "key value" lines into NINA's result, as upstream's ReadResult does: crval/crpix/cd for the parity,
        /// ra_center/dec_center for the coordinates, orientation_center for the position angle, pixscale for the scale and radius.
        /// </summary>
        internal static PlateSolveResult ParseWcsInfo(IEnumerable<string> lines, PlateSolveImageProperties imageProperties) {
            var result = new PlateSolveResult() { Success = false };
            Dictionary<string, string> wcsinfo = new Dictionary<string, string>();
            foreach (var line in lines) {
                if (line != null) {
                    var valuepair = line.Split(' ');
                    if (valuepair != null && valuepair.Length == 2) {
                        wcsinfo[valuepair[0]] = valuepair[1];
                    }
                }
            }

            if (!wcsinfo.TryGetValue("ra_center", out var raValue) || !wcsinfo.TryGetValue("dec_center", out var decValue)) {
                return result;
            }

            if (wcsinfo.ContainsKey("crval0")
                && wcsinfo.ContainsKey("crval1")
                && wcsinfo.ContainsKey("crpix0")
                && wcsinfo.ContainsKey("crpix1")
                && wcsinfo.ContainsKey("cd11")
                && wcsinfo.ContainsKey("cd12")
                && wcsinfo.ContainsKey("cd21")
                && wcsinfo.ContainsKey("cd22")) {
                var crval1 = double.Parse(wcsinfo["crval0"], CultureInfo.InvariantCulture);
                var crval2 = double.Parse(wcsinfo["crval1"], CultureInfo.InvariantCulture);
                var crpix1 = double.Parse(wcsinfo["crpix0"], CultureInfo.InvariantCulture);
                var crpix2 = double.Parse(wcsinfo["crpix1"], CultureInfo.InvariantCulture);
                var cd11 = double.Parse(wcsinfo["cd11"], CultureInfo.InvariantCulture);
                var cd12 = double.Parse(wcsinfo["cd12"], CultureInfo.InvariantCulture);
                var cd21 = double.Parse(wcsinfo["cd21"], CultureInfo.InvariantCulture);
                var cd22 = double.Parse(wcsinfo["cd22"], CultureInfo.InvariantCulture);

                var wcs = new WorldCoordinateSystem(
                    crval1,
                    crval2,
                    crpix1,
                    crpix2,
                    cd11,
                    cd12,
                    cd21,
                    cd22
                );

                result.Flipped = !wcs.Flipped;
            }

            var ra = double.Parse(raValue, CultureInfo.InvariantCulture);
            var dec = double.Parse(decValue, CultureInfo.InvariantCulture);
            if (wcsinfo.TryGetValue("orientation_center", out string value)) {
                result.PositionAngle = 360 - (180 - double.Parse(value, CultureInfo.InvariantCulture) + 360);
            }
            if (wcsinfo.TryGetValue("pixscale", out value)) {
                result.Pixscale = double.Parse(value, CultureInfo.InvariantCulture);
                if (!double.IsNaN(result.Pixscale)) {
                    result.Radius = AstroUtil.ArcsecToDegree(Math.Sqrt(Math.Pow(imageProperties.ImageWidth * result.Pixscale, 2) + Math.Pow(imageProperties.ImageHeight * result.Pixscale, 2)) / 2d);
                }
            }

            result.Coordinates = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees);
            result.Success = true;
            return result;
        }

        protected override string GetLocalizedPlateSolverName() {
            return "astrometry.net solve-field not found";
        }

        protected override string GetOutputPath(string imageFilePath) {
            return Path.Combine(Path.GetDirectoryName(imageFilePath), Path.GetFileNameWithoutExtension(imageFilePath)) + ".wcs";
        }

        /// <summary>
        /// solve-field's own leftovers next to the image (source list and solved flag) and the launcher's log. None when the frame
        /// was never saved: CLISolver's clean-up calls this with a null path when saving the frame failed or was cancelled, and the
        /// real outcome (a quiet failure on cancel, the save's own exception otherwise) must not turn into an ArgumentNullException
        /// (upstream's base returns an empty list; its ASTAPSolver override has the same latent problem on every OS).
        /// </summary>
        protected override List<string> GetSideCarFilePaths(string imageFilePath) {
            if (string.IsNullOrEmpty(imageFilePath)) {
                return new List<string>();
            }
            var stem = Path.Combine(Path.GetDirectoryName(imageFilePath), Path.GetFileNameWithoutExtension(imageFilePath));
            return new List<string>() {
                stem + ".axy",
                stem + ".solved",
                GetLogPath(imageFilePath)
            };
        }

        /// <summary>solve-field's --temp-dir for an image: a folder next to it in NINA's PlateSolver working folder.</summary>
        internal static string GetTempDirectory(string imageFilePath) {
            return Path.Combine(Path.GetDirectoryName(imageFilePath), Path.GetFileNameWithoutExtension(imageFilePath)) + ".solve-field-tmp";
        }

        private static void DeleteTempDirectory(string directory) {
            if (directory == null || !Directory.Exists(directory)) {
                return;
            }
            try {
                Directory.Delete(directory, recursive: true);
            } catch (Exception ex) {
                Logger.Error($"astrometry.net - could not delete the temporary folder {directory}: {ex.Message}");
            }
        }

        internal static string GetLogPath(string imageFilePath) {
            return Path.Combine(Path.GetDirectoryName(imageFilePath), Path.GetFileNameWithoutExtension(imageFilePath)) + ".solve-field.log";
        }

        private List<string> RunWcsInfo(string wcsFile) {
            var startInfo = new ProcessStartInfo(WcsInfoPath) {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(wcsFile);
            using var process = Process.Start(startInfo);
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();
            string line;
            while ((line = process.StandardOutput.ReadLine()) != null) {
                lines.Add(line);
            }
            if (!process.WaitForExit(30000)) {
                process.Kill(entireProcessTree: true);
            }
            if (process.HasExited && process.ExitCode != 0) {
                Logger.Error($"astrometry.net - wcsinfo exited with {process.ExitCode}: {stderr.Result}");
            }
            return lines;
        }

        /// <summary>The last lines of the solve-field log that belongs to the image whose WCS path is given, for the error log.</summary>
        private static string LogTail(string outputFilePath) {
            try {
                var log = Path.ChangeExtension(outputFilePath, null) + ".solve-field.log";
                if (!File.Exists(log)) {
                    return string.Empty;
                }
                var tail = File.ReadLines(log).Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(8);
                return Environment.NewLine + string.Join(Environment.NewLine, tail);
            } catch (Exception ex) {
                return $" (log unreadable: {ex.Message})";
            }
        }

        /// <summary>
        /// Quotes one argument for ProcessStartInfo.Arguments, which .NET splits with the Windows rules on every OS. Inside
        /// double quotes a backslash is literal unless a quote follows it, so embedded quotes are escaped and a path may not end
        /// in a backslash.
        /// </summary>
        internal static string Quote(string argument) {
            if (argument.EndsWith('\\')) {
                throw new ArgumentException($"Cannot pass an argument that ends in a backslash: {argument}", nameof(argument));
            }
            if (argument.Contains("\\\"", StringComparison.Ordinal)) {
                throw new ArgumentException($"Cannot pass an argument that contains a backslash before a quote: {argument}", nameof(argument));
            }
            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }
    }
}
