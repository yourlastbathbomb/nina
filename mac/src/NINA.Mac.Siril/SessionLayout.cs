#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NINA.Mac.Siril {

    /// <summary>Where a frame belongs in the Siril layout.</summary>
    public enum FrameKind {
        Light,
        Snapshot,
        Flat,

        /// <summary>Dark taken at the flat exposure. NINA 3 writes these with IMAGETYP = DARK, so the engine must say so.</summary>
        DarkFlat,

        Bias,
        Dark,
    }

    /// <summary>The capture metadata the layout needs (a subset of NINA's ImageMetaData).</summary>
    public sealed class FrameInfo {

        /// <summary>NINA image type: LIGHT, SNAPSHOT, FLAT, DARK, BIAS (DARKFLAT is accepted too).</summary>
        public string ImageType { get; set; }

        /// <summary>Set for dark flats, which NINA types as DARK.</summary>
        public bool IsDarkFlat { get; set; }

        /// <summary>Exposure start. Unspecified kind is treated as UTC.</summary>
        public DateTime ExposureStart { get; set; }

        public int ExposureNumber { get; set; }

        public double ExposureTime { get; set; } = double.NaN;

        public string TargetName { get; set; }

        public int Gain { get; set; } = -1;

        public int Offset { get; set; } = -1;

        public int BinX { get; set; } = 1;

        public int BinY { get; set; } = 1;

        public double SensorTemperature { get; set; } = double.NaN;

        public double SetPoint { get; set; } = double.NaN;

        /// <summary>NINA's ImageMetaData.Camera.Binning (NINA.Image/ImageData/ImageMetaData.cs:168).</summary>
        public string Binning => $"{BinX}x{BinY}";

        public DateTime ExposureStartUtc => ExposureStart.Kind == DateTimeKind.Local ? ExposureStart.ToUniversalTime() : DateTime.SpecifyKind(ExposureStart, DateTimeKind.Utc);
    }

    public sealed class SessionLayoutOptions {

        /// <summary>Image root. Default ~/Astro/NINA: outside iCloud-synced ~/Documents (research SIR-15).</summary>
        public string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Astro", "NINA");

        /// <summary>Time zone of the observing site. The night label is the local date of (exposure start - 12 h).</summary>
        public TimeZoneInfo SiteTimeZone { get; set; } = SessionLayout.HongKong;

        /// <summary>Local hour at which the night label rolls over; 12 matches NINA's $$DATEMINUS12$$.</summary>
        public int NightRolloverHour { get; set; } = 12;

        public string LibraryFolderName { get; set; } = "library";

        /// <summary>Master-dark file name template with Siril header tokens (see <see cref="SirilPathTemplate"/>).</summary>
        public string MasterDarkFileTemplate { get; set; } = SirilPathTemplate.MasterDarkFileName;

        /// <summary>Extension of frames written by NINA's legacy FITS writer.</summary>
        public string FrameExtension { get; set; } = ".fits";
    }

    /// <summary>Folders for one target on one night.</summary>
    public sealed class TargetFolders {

        internal TargetFolders(DateOnly night, string targetName, string nightDirectory, string folderName) {
            Night = night;
            TargetName = targetName;
            FolderName = folderName;
            NightDirectory = nightDirectory;
            WorkingDirectory = Path.Combine(nightDirectory, folderName);
            Lights = Path.Combine(WorkingDirectory, SessionLayout.LightsFolder);
            Flats = Path.Combine(WorkingDirectory, SessionLayout.FlatsFolder);
            Biases = Path.Combine(WorkingDirectory, SessionLayout.BiasesFolder);
            Snapshots = Path.Combine(WorkingDirectory, SessionLayout.SnapshotsFolder);
            Masters = Path.Combine(WorkingDirectory, SessionLayout.MastersFolder);
            Process = Path.Combine(WorkingDirectory, SessionLayout.ProcessFolder);
            NightFlats = Path.Combine(nightDirectory, SessionLayout.FlatsFolder);
            NightBiases = Path.Combine(nightDirectory, SessionLayout.BiasesFolder);
        }

        public DateOnly Night { get; }

        public string TargetName { get; }

        /// <summary>Sanitised target folder name.</summary>
        public string FolderName { get; }

        public string NightDirectory { get; }

        /// <summary>Siril working directory for this target (holds the script, logs and the result).</summary>
        public string WorkingDirectory { get; }

        public string Lights { get; }

        public string Flats { get; }

        /// <summary>Flat-calibration frames: dark flats (preferred) or biases. The stock script uses them for the flats only.</summary>
        public string Biases { get; }

        /// <summary>Snapshots are kept out of lights/ so Siril never stacks them.</summary>
        public string Snapshots { get; }

        /// <summary>Siril output: bias_stacked, pp_flat_stacked.</summary>
        public string Masters { get; }

        /// <summary>Siril intermediates (symlinks, pp_ and r_ frames); safe to delete once stacked.</summary>
        public string Process { get; }

        /// <summary>Night-level flats shared by every target of the night (flats taken without a target).</summary>
        public string NightFlats { get; }

        public string NightBiases { get; }

        /// <summary>The target's flats/ if it holds frames, else the night's shared flats/ if that does, else the target's flats/.</summary>
        public string ResolveFlatsDirectory() => FirstWithFrames(Flats, NightFlats) ?? Flats;

        /// <summary>The target's biases/ if it holds frames, else the night's shared biases/ if that does, else the target's biases/.</summary>
        public string ResolveBiasesDirectory() => FirstWithFrames(Biases, NightBiases) ?? Biases;

        private static string FirstWithFrames(params string[] directories) => directories.FirstOrDefault(d => SessionLayout.ListFitsFrames(d).Count > 0);
    }

    /// <summary>
    /// Per-night Siril layout (MAC_PORT_PLAN.md section 6; research rig_verify_siril.md table 2):
    /// <code>
    /// root/
    ///   2026-10-03/                 night = local date of (exposure start - 12 h), Asia/Hong_Kong
    ///     flats/ biases/            night-level shared flats and dark flats (frames taken without a target)
    ///     NGC 253/                  Siril working directory of one target
    ///       lights/ flats/ biases/  biases/ = dark flats or biases (used for the flats only)
    ///       snapshots/              never stacked
    ///       masters/ process/       Siril output
    ///   library/
    ///     darks/20.00s_g252_o50_0.00C_2x2/   raw darks, one folder per exposure/gain/offset/set-point/binning
    ///     masters/dark_20s_G252_O50_T0_B2.fit  master darks named by the Siril path-parse template
    /// </code>
    /// Folder and file names use NINA's token formats (BaseImageData.GetImagePatterns), so stock NINA with the
    /// patterns in <see cref="NinaFilePatterns"/> writes the same paths for target names that
    /// <see cref="SanitizeFolderName"/> leaves unchanged apart from NINA's own '\' and '/' to '-'. NINA (with this fork's
    /// CoreUtil patch) maps Windows' invalid file-name characters to '_' on every OS, as this layout does, but keeps '$'
    /// and a leading '.', which this layout does not, so frames of such a target would land in a folder
    /// <see cref="GetTargetFolders(DateOnly, string)"/> never looks in. The engine therefore saves through
    /// <see cref="GetFramePath"/>; see <see cref="KeepsNinaFolderName"/>.
    /// </summary>
    public sealed class SessionLayout {
        public const string LightsFolder = "lights";
        public const string FlatsFolder = "flats";
        public const string BiasesFolder = "biases";
        public const string SnapshotsFolder = "snapshots";
        public const string DarksFolder = "darks";
        public const string MastersFolder = "masters";
        public const string ProcessFolder = "process";

        /// <summary>Folder for lights and snapshots taken without a target name.</summary>
        public const string UntitledTarget = "untitled";

        /// <summary>FITS extensions Siril's convert picks up (Siril 1.4.4 src/io/conversion.c:159-246, FITS subset).</summary>
        private static readonly string[] FitsExtensions = { ".fit", ".fits", ".fts", ".fit.fz", ".fits.fz", ".fts.fz" };

        public static readonly TimeZoneInfo HongKong = TimeZoneInfo.FindSystemTimeZoneById("Asia/Hong_Kong");

        public SessionLayout(SessionLayoutOptions options) {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.Root)) {
                throw new ArgumentException("Root is required", nameof(options));
            }
            Root = Path.GetFullPath(options.Root);
            Library = new DarkLibrary(Path.Combine(Root, options.LibraryFolderName), options.MasterDarkFileTemplate);
        }

        public SessionLayoutOptions Options { get; }

        public string Root { get; }

        public DarkLibrary Library { get; }

        /// <summary>Night label: local date of (exposure start - rollover hour), like NINA's $$DATEMINUS12$$ (BaseImageData.cs:231-236).</summary>
        public DateOnly NightOf(DateTime exposureStart) {
            var utc = exposureStart.Kind == DateTimeKind.Local ? exposureStart.ToUniversalTime() : DateTime.SpecifyKind(exposureStart, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, Options.SiteTimeZone);
            return DateOnly.FromDateTime(local.AddHours(-Options.NightRolloverHour));
        }

        public static string NightLabel(DateOnly night) => night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public string NightDirectory(DateOnly night) => Path.Combine(Root, NightLabel(night));

        public TargetFolders GetTargetFolders(DateOnly night, string targetName) {
            var folder = string.IsNullOrWhiteSpace(targetName) ? UntitledTarget : SanitizeFolderName(targetName);
            return new TargetFolders(night, targetName, NightDirectory(night), folder);
        }

        public TargetFolders GetTargetFolders(FrameInfo frame) => GetTargetFolders(NightOf(frame.ExposureStartUtc), frame.TargetName);

        public static FrameKind Classify(FrameInfo frame) {
            var type = (frame.ImageType ?? string.Empty).Trim().ToUpperInvariant();
            switch (type) {
                case "LIGHT":
                    return FrameKind.Light;

                case "SNAPSHOT":
                    return FrameKind.Snapshot;

                case "FLAT":
                    return FrameKind.Flat;

                case "BIAS":
                    return FrameKind.Bias;

                case "DARKFLAT":
                case "DARK FLAT":
                    return FrameKind.DarkFlat;

                case "DARK":
                    return frame.IsDarkFlat ? FrameKind.DarkFlat : FrameKind.Dark;

                default:
                    throw new ArgumentException($"Unknown image type '{frame.ImageType}'");
            }
        }

        /// <summary>Directory a frame is saved to.</summary>
        public string GetFrameDirectory(FrameInfo frame) {
            var kind = Classify(frame);
            if (kind == FrameKind.Dark) {
                return Path.Combine(Library.DarksDirectory, DarkSetFolderName(frame));
            }
            var night = NightOf(frame.ExposureStartUtc);
            var hasTarget = !string.IsNullOrWhiteSpace(frame.TargetName);
            if (!hasTarget && (kind == FrameKind.Flat || kind == FrameKind.DarkFlat || kind == FrameKind.Bias)) {
                // Calibration frames without a target are shared by the whole night
                return Path.Combine(NightDirectory(night), kind == FrameKind.Flat ? FlatsFolder : BiasesFolder);
            }
            var target = GetTargetFolders(night, frame.TargetName);
            return kind switch {
                FrameKind.Light => target.Lights,
                FrameKind.Snapshot => target.Snapshots,
                FrameKind.Flat => target.Flats,
                _ => target.Biases,
            };
        }

        /// <summary>File name: $$DATETIME$$_$$EXPOSURETIME$$s_$$BINNING$$_g$$GAIN$$_$$SENSORTEMP$$C_$$FRAMENR$$ in NINA's formats.</summary>
        public string GetFileName(FrameInfo frame) {
            var v = NinaTokenValues.From(frame, Options.SiteTimeZone);
            return SanitizeFileName($"{v.DateTime}_{v.ExposureTime}s_{v.Binning}_g{v.Gain}_{v.SensorTemp}C_{v.FrameNr}") + Options.FrameExtension;
        }

        public string GetFramePath(FrameInfo frame) => Path.Combine(GetFrameDirectory(frame), GetFileName(frame));

        /// <summary>Raw-dark set folder: $$EXPOSURETIME$$s_g$$GAIN$$_o$$OFFSET$$_$$TEMPERATURESETPOINT$$C_$$BINNING$$.</summary>
        public string DarkSetFolderName(FrameInfo frame) {
            var v = NinaTokenValues.From(frame, Options.SiteTimeZone);
            return SanitizeFileName($"{v.ExposureTime}s_g{v.Gain}_o{v.Offset}_{v.SetPoint}C_{v.Binning}");
        }

        /// <summary>
        /// Target folder name. NINA's own sanitising (CoreUtil.ReplaceAllInvalidFilenameChars: '\' and '/' to '-', then
        /// Windows' invalid file-name characters to '_' on every OS, so names survive a copy to Windows or exFAT; this
        /// covers '"' (Siril cannot quote a word holding both quote kinds), ':' (path-parse key separator; Finder shows it
        /// as '/') and control characters) plus '$' (Siril path-parse token delimiter). Spaces and apostrophes are kept:
        /// generated scripts quote whole words (research SIR-M4).
        /// </summary>
        public static string SanitizeFolderName(string name) {
            var trimmed = (name ?? string.Empty).Trim().Replace('\\', '-').Replace('/', '-');
            var sb = new StringBuilder(trimmed.Length);
            foreach (var c in trimmed) {
                sb.Append(c == '$' || char.IsControl(c) || Array.IndexOf(NinaTokenValues.PortableInvalidFileNameChars, c) >= 0 ? '_' : c);
            }
            var result = sb.ToString().Trim();
            if (result.StartsWith('.')) {
                result = "_" + result.Substring(1);
            }
            return result.Length == 0 ? UntitledTarget : result;
        }

        /// <summary>
        /// True when NINA's own sanitising of <paramref name="targetName"/> (CoreUtil.ReplaceAllInvalidFilenameChars,
        /// ported as NinaTokenValues.Sanitize) gives the folder name <see cref="SanitizeFolderName"/> gives, i.e. when
        /// frames saved with the <see cref="NinaFilePatterns"/> profile patterns land where this layout looks. False for
        /// names with '$' or a leading '.', and for an empty name (NINA drops the empty
        /// $$TARGETNAME$$ segment, so its lights go to &lt;night&gt;/lights, not &lt;night&gt;/untitled/lights).
        /// </summary>
        public static bool KeepsNinaFolderName(string targetName) {
            if (string.IsNullOrWhiteSpace(targetName)) {
                return false;
            }
            return string.Equals(SanitizeFolderName(targetName), NinaTokenValues.Sanitize(targetName).Trim(), StringComparison.Ordinal);
        }

        private static string SanitizeFileName(string name) => SanitizeFolderName(name);

        /// <summary>FITS frames at the top level of a folder, sorted by name (Siril's convert reads the top level only and skips dotfiles).</summary>
        public static IReadOnlyList<string> ListFitsFrames(string directory) {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) {
                return Array.Empty<string>();
            }
            return Directory.EnumerateFiles(directory)
                .Where(f => !Path.GetFileName(f).StartsWith('.') && IsFitsFile(f))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
        }

        public static bool IsFitsFile(string path) => FitsExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        /// <summary>Non-FITS files Siril's convert would also pick up as images (JPEG, PNG, TIFF, XISF, raw...).</summary>
        public static IReadOnlyList<string> ListOtherImages(string directory) {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) {
                return Array.Empty<string>();
            }
            string[] imageExtensions = { ".tif", ".tiff", ".jpg", ".jpeg", ".png", ".xisf", ".bmp", ".pgm", ".ppm", ".pnm", ".pic", ".heic", ".heif", ".avif", ".jxl", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".ser", ".avi", ".mov", ".mp4" };
            return Directory.EnumerateFiles(directory)
                .Where(f => !Path.GetFileName(f).StartsWith('.') && imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// NINA's pattern token values for a frame, formatted like NINA.Image/ImageData/BaseImageData.cs:223-283 with
    /// NINA.Core/Model/ImagePattern.cs:148-170 (doubles "F2", ints "0", sanitised via CoreUtil.ReplaceAllInvalidFilenameChars).
    /// Unset values are empty strings, as NINA leaves them null and string.Replace substitutes "".
    /// </summary>
    internal sealed class NinaTokenValues {
        public string Date = string.Empty;
        public string DateMinus12 = string.Empty;
        public string DateTime = string.Empty;
        public string FrameNr = string.Empty;
        public string ImageType = string.Empty;
        public string TargetName = string.Empty;
        public string ExposureTime = string.Empty;
        public string Binning = string.Empty;
        public string SensorTemp = string.Empty;
        public string SetPoint = string.Empty;
        public string Gain = string.Empty;
        public string Offset = string.Empty;

        public static NinaTokenValues From(FrameInfo frame, TimeZoneInfo zone) {
            var local = TimeZoneInfo.ConvertTimeFromUtc(frame.ExposureStartUtc, zone);
            var v = new NinaTokenValues {
                Date = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTime = local.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture),
                FrameNr = frame.ExposureNumber.ToString("0000", CultureInfo.InvariantCulture),
                ImageType = Sanitize(frame.ImageType),
                TargetName = Sanitize(frame.TargetName),
                Binning = Sanitize(frame.Binning),
            };
            if (frame.ExposureStartUtc > System.DateTime.MinValue.AddHours(12)) {
                v.DateMinus12 = local.AddHours(-12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            if (!double.IsNaN(frame.ExposureTime)) {
                v.ExposureTime = F2(frame.ExposureTime);
            }
            if (!double.IsNaN(frame.SensorTemperature)) {
                v.SensorTemp = F2(frame.SensorTemperature);
            }
            if (!double.IsNaN(frame.SetPoint)) {
                v.SetPoint = F2(frame.SetPoint);
            }
            if (frame.Gain >= 0) {
                v.Gain = frame.Gain.ToString("0", CultureInfo.InvariantCulture);
            }
            if (frame.Offset >= 0) {
                v.Offset = frame.Offset.ToString("0", CultureInfo.InvariantCulture);
            }
            return v;
        }

        private static string F2(double value) => Sanitize(value.ToString("F2", CultureInfo.InvariantCulture));

        /// <summary>
        /// Windows' invalid file-name characters (control characters, " &lt; &gt; | : * ? \ /). This fork's
        /// CoreUtil.ReplaceInvalidFilenameChars uses them on every OS, not macOS's own set ('\0' and '/').
        /// </summary>
        internal static readonly char[] PortableInvalidFileNameChars = BuildPortableInvalidFileNameChars();

        private static char[] BuildPortableInvalidFileNameChars() {
            var chars = new List<char>();
            for (var c = 0; c < 32; c++) { chars.Add((char)c); }
            chars.AddRange("\"<>|:*?\\/");
            return chars.ToArray();
        }

        /// <summary>NINA.Core/Utility/CoreUtil.cs ReplaceAllInvalidFilenameChars, with this fork's portable character set.</summary>
        internal static string Sanitize(string value) {
            if (value == null) {
                return string.Empty;
            }
            var s = value.Trim().Replace("\\", "-").Replace("/", "-");
            return string.Join("_", s.Split(PortableInvalidFileNameChars));
        }
    }
}
