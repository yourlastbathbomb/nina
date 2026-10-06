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
using System.Text.RegularExpressions;

namespace NINA.Mac.Siril {

    /// <summary>
    /// What a set of frames was taken with, as the dark library compares it: exposure, gain, offset, binning, size, the
    /// cooler's set point and the sensor temperature actually reached, and the camera (INSTRUME).
    /// </summary>
    public sealed class FrameSettings {

        public double ExposureSeconds { get; init; } = double.NaN;

        public int? Gain { get; init; }

        public int? Offset { get; init; }

        public int? BinX { get; init; }

        public int? Width { get; init; }

        public int? Height { get; init; }

        /// <summary>SET-TEMP: the cooler's set point; null when the frames have none (cooler never set).</summary>
        public double? SetTemperature { get; init; }

        /// <summary>CCD-TEMP: the sensor temperature (median over the frames); null when missing.</summary>
        public double? SensorTemperature { get; init; }

        public string Camera { get; init; }

        public int FrameCount { get; init; }

        /// <summary>
        /// The temperature the dark current followed: the sensor temperature when it is known, else the set point. A cooler
        /// that never reached its set point (no 12 V supply, a hot night) makes these differ, and the sensor wins.
        /// </summary>
        public double? EffectiveTemperature => SensorTemperature ?? SetTemperature;

        /// <summary>The cooler held the set point within <paramref name="toleranceC"/> (or one of the two is unknown).</summary>
        public bool CoolerAtSetPoint(double toleranceC) =>
            !SetTemperature.HasValue || !SensorTemperature.HasValue || Math.Abs(SetTemperature.Value - SensorTemperature.Value) <= toleranceC;

        public string Describe() {
            var parts = new List<string> {
                string.Create(CultureInfo.InvariantCulture, $"{ExposureSeconds:0.###} s"),
                $"gain {Text(Gain)}",
                $"offset {Text(Offset)}",
                $"bin {Text(BinX)}",
            };
            if (SetTemperature.HasValue || SensorTemperature.HasValue) {
                parts.Add(SensorTemperature.HasValue && SetTemperature.HasValue && Math.Abs(SensorTemperature.Value - SetTemperature.Value) > 0.05
                    ? string.Create(CultureInfo.InvariantCulture, $"sensor {SensorTemperature.Value:0.0} °C (set point {SetTemperature.Value:0.0} °C)")
                    : string.Create(CultureInfo.InvariantCulture, $"{EffectiveTemperature.Value:0.0} °C"));
            } else {
                parts.Add("temperature unknown");
            }
            if (Width.HasValue && Height.HasValue) {
                parts.Add($"{Width}x{Height}");
            }
            if (!string.IsNullOrEmpty(Camera)) {
                parts.Add(Camera);
            }
            return string.Join(", ", parts);
        }

        private static string Text(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "?";

        /// <summary>Settings of a set of frames (lights of one target): the first frame's values, the median sensor temperature.</summary>
        public static FrameSettings FromHeaders(IReadOnlyList<FitsHeader> headers) {
            if (headers == null || headers.Count == 0) {
                throw new ArgumentException("no frames", nameof(headers));
            }
            var first = headers[0];
            var sensor = headers.Select(h => h.GetDouble("CCD-TEMP")).Where(t => t.HasValue).Select(t => t.Value).OrderBy(t => t).ToList();
            return new FrameSettings {
                ExposureSeconds = first.GetDouble("EXPTIME") ?? first.GetDouble("EXPOSURE") ?? double.NaN,
                Gain = first.GetInt("GAIN"),
                Offset = first.GetInt("OFFSET"),
                BinX = first.GetInt("XBINNING"),
                Width = first.GetInt("NAXIS1"),
                Height = first.GetInt("NAXIS2"),
                SetTemperature = first.GetDouble("SET-TEMP"),
                SensorTemperature = sensor.Count == 0 ? null : sensor[sensor.Count / 2],
                Camera = first.GetString("INSTRUME")?.Trim(),
                FrameCount = headers.Count,
            };
        }
    }

    /// <summary>One master dark in the library's masters/ folder.</summary>
    public sealed class MasterDark {

        internal MasterDark(string path, FrameSettings settings, int? stackCount, IReadOnlyList<string> notes) {
            Path = path;
            Settings = settings;
            StackCount = stackCount;
            Notes = notes;
        }

        public string Path { get; }

        public string FileName => System.IO.Path.GetFileName(Path);

        public FrameSettings Settings { get; }

        /// <summary>STACKCNT: how many raw darks Siril stacked.</summary>
        public int? StackCount { get; }

        /// <summary>Values taken from the file name because the header lacked them, and similar remarks.</summary>
        public IReadOnlyList<string> Notes { get; }
    }

    /// <summary>Tolerances of <see cref="MasterDarkIndex.Find"/>.</summary>
    public sealed class DarkMatchOptions {

        /// <summary>Exposure times closer than this are the same (NINA writes the requested time).</summary>
        public double ExposureToleranceSeconds { get; set; } = 0.05;

        /// <summary>
        /// Largest difference between the lights' and the master's temperature (sensor temperature where known). Dark current
        /// of the IMX585 roughly doubles every 5-6 °C, so 2 °C keeps it within about 30 %.
        /// </summary>
        public double TemperatureToleranceC { get; set; } = 2.0;

        /// <summary>Refuse a master whose INSTRUME names another camera (a master without INSTRUME is accepted with a warning).</summary>
        public bool RequireSameCamera { get; set; } = true;
    }

    /// <summary>The verdict of <see cref="MasterDarkIndex.Find"/>: the master to use, or why there is none.</summary>
    public sealed class DarkMatch {

        internal DarkMatch(FrameSettings lights, MasterDark master, string message, IReadOnlyList<string> warnings,
                           IReadOnlyList<(MasterDark Master, string Why)> rejected, string sirilTemplateName) {
            Lights = lights;
            Master = master;
            Message = message;
            Warnings = warnings;
            Rejected = rejected;
            SirilTemplateName = sirilTemplateName;
        }

        public FrameSettings Lights { get; }

        /// <summary>The best match; null when none fits.</summary>
        public MasterDark Master { get; }

        public bool Found => Master != null;

        /// <summary>One sentence for the operator: which master, or why none matches and what to shoot.</summary>
        public string Message { get; }

        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Every master that was not taken, with the first reason.</summary>
        public IReadOnlyList<(MasterDark Master, string Why)> Rejected { get; }

        /// <summary>The master name Siril's header-token template gives for these lights (null when the header cannot name one).</summary>
        public string SirilTemplateName { get; }

        /// <summary>
        /// The matching master is not the one Siril's template would pick from the first light (a tolerated temperature
        /// difference, or a different file name), so the script must name it explicitly.
        /// </summary>
        public bool NeedsExplicitPath => Found && !string.Equals(Master.FileName, SirilTemplateName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The dark library's masters with the settings they were taken with, and the lookup a night's lights need
    /// (MAC_PORT_PLAN section 6 and decision 7: a dark library at 0 °C for 10/20/30 s). Siril's own lookup (the "-dark="
    /// header-token template) matches exposure, gain, offset, set point and binning exactly after %d truncation and stops the
    /// script without a word about what is missing; this index matches the same keys with tolerances, also checks the
    /// camera, the frame size and the sensor temperature the lights actually reached (a cooler without its 12 V supply never
    /// reaches the set point), picks the best master, and says in plain words why nothing matches and which darks to shoot.
    /// Masters are read from library/masters/*.fit; values missing from a master's header are taken from its file name when
    /// it follows the default template (<see cref="SirilPathTemplate.MasterDarkFileName"/>).
    /// </summary>
    public sealed class MasterDarkIndex {

        private static readonly Regex DefaultName = new(@"^dark_(?<exp>-?\d+)s_G(?<gain>-?\d+)_O(?<offset>-?\d+)_T(?<temp>-?\d+)_B(?<bin>\d+)\.fit$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private MasterDarkIndex(DarkLibrary library, IReadOnlyList<MasterDark> masters, IReadOnlyList<string> unreadable) {
            Library = library;
            Masters = masters;
            Unreadable = unreadable;
        }

        public DarkLibrary Library { get; }

        public IReadOnlyList<MasterDark> Masters { get; }

        /// <summary>Masters that could not be read, with the reason.</summary>
        public IReadOnlyList<string> Unreadable { get; }

        /// <summary>Reads the headers of every master in the library.</summary>
        public static MasterDarkIndex Scan(DarkLibrary library) {
            ArgumentNullException.ThrowIfNull(library);
            var masters = new List<MasterDark>();
            var unreadable = new List<string>();
            foreach (var path in library.ListMasters()) {
                try {
                    masters.Add(Read(path));
                } catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) {
                    unreadable.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
            return new MasterDarkIndex(library, masters, unreadable);
        }

        private static MasterDark Read(string path) {
            var header = FitsFile.ReadHeader(path);
            var notes = new List<string>();
            var name = DefaultName.Match(Path.GetFileName(path));
            double? FromName(string group) => name.Success ? double.Parse(name.Groups[group].Value, CultureInfo.InvariantCulture) : null;
            double? Value(string key, string group) {
                var v = header.GetDouble(key);
                if (v.HasValue) {
                    return v;
                }
                var n = FromName(group);
                if (n.HasValue) {
                    notes.Add($"{key} taken from the file name");
                }
                return n;
            }
            var exposure = Value("EXPTIME", "exp") ?? header.GetDouble("EXPOSURE") ?? double.NaN;
            var settings = new FrameSettings {
                ExposureSeconds = exposure,
                Gain = (int?)Value("GAIN", "gain"),
                Offset = (int?)Value("OFFSET", "offset"),
                BinX = (int?)Value("XBINNING", "bin"),
                Width = header.GetInt("NAXIS1"),
                Height = header.GetInt("NAXIS2"),
                SetTemperature = Value("SET-TEMP", "temp"),
                SensorTemperature = header.GetDouble("CCD-TEMP"),
                Camera = header.GetString("INSTRUME")?.Trim(),
                FrameCount = header.GetInt("STACKCNT") ?? 0,
            };
            return new MasterDark(path, settings, header.GetInt("STACKCNT"), notes);
        }

        /// <summary>Settings of the FITS lights in <paramref name="lightsDirectory"/>; null when it holds none that can be read.</summary>
        public static FrameSettings ReadLights(string lightsDirectory) {
            var headers = new List<FitsHeader>();
            foreach (var frame in SessionLayout.ListFitsFrames(lightsDirectory)) {
                try {
                    headers.Add(FitsFile.ReadHeader(frame));
                } catch (Exception ex) when (ex is IOException || ex is InvalidDataException) {
                    // the session validator reports unreadable lights
                }
            }
            return headers.Count == 0 ? null : FrameSettings.FromHeaders(headers);
        }

        /// <summary>The best master for lights taken with <paramref name="lights"/>, or a message saying why there is none.</summary>
        public DarkMatch Find(FrameSettings lights, DarkMatchOptions options = null, FitsHeader firstLight = null) {
            ArgumentNullException.ThrowIfNull(lights);
            options ??= new DarkMatchOptions();
            var warnings = new List<string>(Unreadable.Select(u => $"unreadable master {u}"));
            var rejected = new List<(MasterDark, string)>();
            var candidates = new List<(MasterDark Master, double TemperatureDifference, List<string> Warnings)>();
            string templateName = null;
            if (firstLight != null) {
                try {
                    templateName = SirilPathTemplate.Resolve(Library.MasterDarkFileTemplate, firstLight);
                } catch (SirilPathTemplateException) {
                    templateName = null;
                }
            }
            if (!lights.CoolerAtSetPoint(1.0)) {
                warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"the lights' sensor was at {lights.SensorTemperature:0.0} °C, not the {lights.SetTemperature:0.0} °C set point: the cooler did not hold it (12 V supply connected?); darks are matched to the sensor temperature"));
            }
            foreach (var master in Masters) {
                var why = Mismatch(lights, master.Settings, options, out var temperatureDifference, out var masterWarnings);
                if (why != null) {
                    rejected.Add((master, why));
                } else {
                    candidates.Add((master, temperatureDifference, masterWarnings));
                }
            }
            if (candidates.Count == 0) {
                return new DarkMatch(lights, null, NoMatchMessage(lights, rejected, options), warnings, rejected, templateName);
            }
            var best = candidates
                .OrderBy(c => c.TemperatureDifference)
                .ThenByDescending(c => c.Master.StackCount ?? 0)
                .ThenBy(c => c.Master.FileName, StringComparer.Ordinal)
                .First();
            warnings.AddRange(best.Warnings);
            warnings.AddRange(best.Master.Notes.Select(n => $"{best.Master.FileName}: {n}"));
            if (best.Master.StackCount is int stacked && stacked < 15) {
                warnings.Add($"{best.Master.FileName} was stacked from only {stacked} darks; 30-50 make a quieter master");
            }
            foreach (var other in candidates.Where(c => !ReferenceEquals(c.Master, best.Master))) {
                rejected.Add((other.Master, string.Create(CultureInfo.InvariantCulture, $"also fits, but {best.Master.FileName} is closer in temperature or has more frames")));
            }
            var message = string.Create(CultureInfo.InvariantCulture,
                $"Master dark {best.Master.FileName} ({best.Master.Settings.Describe()}{(best.Master.StackCount.HasValue ? $", {best.Master.StackCount} frames" : string.Empty)}) for lights at {lights.Describe()}");
            return new DarkMatch(lights, best.Master, message, warnings, rejected, templateName);
        }

        /// <summary>Why <paramref name="master"/> does not fit the lights (null when it does).</summary>
        private static string Mismatch(FrameSettings lights, FrameSettings master, DarkMatchOptions options, out double temperatureDifference, out List<string> warnings) {
            warnings = new List<string>();
            temperatureDifference = 0;
            if (double.IsNaN(master.ExposureSeconds) || double.IsNaN(lights.ExposureSeconds)
                    || Math.Abs(master.ExposureSeconds - lights.ExposureSeconds) > options.ExposureToleranceSeconds) {
                return string.Create(CultureInfo.InvariantCulture, $"exposure {master.ExposureSeconds:0.###} s, the lights {lights.ExposureSeconds:0.###} s");
            }
            if (master.Gain != lights.Gain) {
                return $"gain {master.Gain}, the lights {lights.Gain}";
            }
            if (master.Offset != lights.Offset) {
                return $"offset {master.Offset}, the lights {lights.Offset}";
            }
            if (master.BinX != lights.BinX) {
                return $"binning {master.BinX}, the lights {lights.BinX}";
            }
            if (master.Width.HasValue && lights.Width.HasValue && (master.Width != lights.Width || master.Height != lights.Height)) {
                return $"{master.Width}x{master.Height} pixels, the lights {lights.Width}x{lights.Height}";
            }
            if (!string.IsNullOrEmpty(master.Camera) && !string.IsNullOrEmpty(lights.Camera)) {
                if (options.RequireSameCamera && !string.Equals(master.Camera, lights.Camera, StringComparison.OrdinalIgnoreCase)) {
                    return $"camera {master.Camera}, the lights {lights.Camera}";
                }
            } else if (string.IsNullOrEmpty(master.Camera)) {
                warnings.Add("the master does not say which camera took it (no INSTRUME)");
            }
            var masterTemperature = master.EffectiveTemperature;
            var lightsTemperature = lights.EffectiveTemperature;
            if (!masterTemperature.HasValue && lightsTemperature.HasValue) {
                // a master that might be a 0 °C dark must not calibrate lights known to be warm (no 12 V), and cannot be
                // checked against cold ones either: refuse it and say how to make it usable
                return string.Create(CultureInfo.InvariantCulture,
                    $"temperature unknown, the lights at {lightsTemperature.Value:0.0} °C (add CCD-TEMP or SET-TEMP to its header, or name it like {DefaultNameExample(master)} with its real temperature after _T)");
            }
            if (!lightsTemperature.HasValue) {
                // NINA writes CCD-TEMP for a cooled camera, so this is a foreign or uncooled set of lights
                warnings.Add(masterTemperature.HasValue
                    ? "the lights' temperature is unknown (no CCD-TEMP or SET-TEMP); the master's temperature was not compared"
                    : "the lights' temperature is unknown (no CCD-TEMP or SET-TEMP), and so is the master's; not compared");
                temperatureDifference = double.MaxValue / 4;
                return null;
            }
            temperatureDifference = Math.Abs(masterTemperature.Value - lightsTemperature.Value);
            if (temperatureDifference > options.TemperatureToleranceC) {
                return string.Create(CultureInfo.InvariantCulture,
                    $"taken at {masterTemperature.Value:0.0} °C, the lights at {lightsTemperature.Value:0.0} °C (tolerance {options.TemperatureToleranceC:0.#} °C)");
            }
            return null;
        }

        /// <summary>The default-template file name of a 0 °C master with these settings, as an example for the operator.</summary>
        private static string DefaultNameExample(FrameSettings master) =>
            string.Create(CultureInfo.InvariantCulture, $"dark_{(int)master.ExposureSeconds}s_G{master.Gain}_O{master.Offset}_T0_B{master.BinX}.fit");

        private string NoMatchMessage(FrameSettings lights, IReadOnlyList<(MasterDark Master, string Why)> rejected, DarkMatchOptions options) {
            var sb = new StringBuilder();
            sb.Append("No master dark matches lights at ").Append(lights.Describe()).Append('.');
            if (Masters.Count == 0) {
                sb.Append(" The library has no masters (").Append(Library.MastersDirectory).Append(").");
            } else {
                // the nearest: same exposure, gain, offset and binning first
                var nearest = rejected
                    .OrderBy(r => Score(lights, r.Master.Settings))
                    .Take(3)
                    .Select(r => $"{r.Master.FileName} ({r.Why})");
                sb.Append(" Nearest: ").Append(string.Join("; ", nearest)).Append('.');
            }
            var temperature = lights.EffectiveTemperature;
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $" Shoot 30-50 darks of {lights.ExposureSeconds:0.###} s at gain {lights.Gain}, offset {lights.Offset}, bin {lights.BinX}"));
            if (temperature.HasValue) {
                sb.Append(string.Create(CultureInfo.InvariantCulture, $", within {options.TemperatureToleranceC:0.#} °C of {temperature.Value:0.0} °C"));
            }
            sb.Append(" (they go to ").Append(Library.DarksDirectory).Append("), then build the masters.");
            return sb.ToString();
        }

        /// <summary>How far a master is from the lights, for ordering the "nearest" list: settings that must match weigh most.</summary>
        private static double Score(FrameSettings lights, FrameSettings master) {
            var score = 0.0;
            if (Math.Abs(master.ExposureSeconds - lights.ExposureSeconds) > 0.05 || double.IsNaN(master.ExposureSeconds)) {
                score += 1000;
            }
            if (master.Gain != lights.Gain) {
                score += 1000;
            }
            if (master.Offset != lights.Offset) {
                score += 1000;
            }
            if (master.BinX != lights.BinX) {
                score += 10000;
            }
            if (master.EffectiveTemperature.HasValue && lights.EffectiveTemperature.HasValue) {
                score += Math.Abs(master.EffectiveTemperature.Value - lights.EffectiveTemperature.Value);
            }
            return score;
        }
    }
}
