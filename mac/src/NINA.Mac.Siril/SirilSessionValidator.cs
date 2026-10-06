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

namespace NINA.Mac.Siril {

    public enum SirilIssueSeverity {
        Warning,
        Error,
    }

    public sealed class SirilIssue {

        public SirilIssue(SirilIssueSeverity severity, string message) {
            Severity = severity;
            Message = message;
        }

        public SirilIssueSeverity Severity { get; }

        public string Message { get; }

        public override string ToString() => $"{Severity}: {Message}";
    }

    /// <summary>
    /// The port-side checks Siril does not do (research SIR-03/04/07/11, SIR-M1): Siril calibrates by folder and only
    /// checks image sizes, and path parsing reads only the first light's header. Run before generating a script.
    /// </summary>
    public static class SirilSessionValidator {

        /// <summary>Keys that must be identical across lights (the master dark is chosen from the first light only).</summary>
        private static readonly string[] LightKeys = { "EXPTIME", "GAIN", "OFFSET", "SET-TEMP", "XBINNING", "YBINNING", "BAYERPAT", "NAXIS1", "NAXIS2" };

        public static IReadOnlyList<SirilIssue> Validate(SirilPreprocessingPlan plan) {
            var issues = new List<SirilIssue>();
            void Error(string m) => issues.Add(new SirilIssue(SirilIssueSeverity.Error, m));
            void Warn(string m) => issues.Add(new SirilIssue(SirilIssueSeverity.Warning, m));

            var wd = plan.WorkingDirectory;
            var lightsDir = plan.LightsDirectory ?? Path.Combine(wd, SessionLayout.LightsFolder);
            var lights = SessionLayout.ListFitsFrames(lightsDir);
            if (lights.Count == 0) {
                Error($"No FITS lights in {lightsDir}");
                return issues;
            }
            if (lights.Count < 3) {
                Warn($"Only {lights.Count} lights; rejection stacking needs at least 3");
            }
            if (lights.Count > 9000) {
                Warn($"{lights.Count} lights: above Siril's 10000 open-file ceiling margin (SIR-M6); stack per night");
            }
            foreach (var other in SessionLayout.ListOtherImages(lightsDir)) {
                Error($"{Path.GetFileName(other)} in lights/ would be converted and stacked as a light; move it out");
            }

            var headers = new List<(string Path, FitsHeader Header)>();
            foreach (var light in lights) {
                try {
                    headers.Add((light, FitsFile.ReadHeader(light)));
                } catch (Exception ex) when (ex is IOException || ex is InvalidDataException) {
                    Error($"{Path.GetFileName(light)}: {ex.Message}");
                }
            }
            if (headers.Count == 0) {
                return issues;
            }
            var reference = headers[0].Header;
            foreach (var key in LightKeys) {
                var values = headers.Select(h => h.Header.GetComparableValue(key)).Distinct().ToList();
                if (values.Count > 1) {
                    Error($"Lights differ in {key} ({string.Join(", ", values.Select(v => v ?? "missing"))}); Siril picks the master dark from the first light only, so one folder must hold one setting");
                }
            }
            var offTemperature = new List<(string Name, double Ccd, double Set)>();
            foreach (var (path, header) in headers) {
                var type = header.GetString("IMAGETYP");
                if (type != null && !type.Equals("LIGHT", StringComparison.OrdinalIgnoreCase)) {
                    Error($"{Path.GetFileName(path)}: IMAGETYP = {type} in lights/");
                }
                var set = header.GetDouble("SET-TEMP");
                var ccd = header.GetDouble("CCD-TEMP");
                if (set.HasValue && ccd.HasValue && Math.Abs(set.Value - ccd.Value) > 1.0) {
                    offTemperature.Add((Path.GetFileName(path), ccd.Value, set.Value));
                }
            }
            if (offTemperature.Count == 1) {
                var (name, ccd, set) = offTemperature[0];
                Warn(string.Create(CultureInfo.InvariantCulture, $"{name}: CCD-TEMP {ccd:0.0} is more than 1 °C from SET-TEMP {set:0.0}"));
            } else if (offTemperature.Count > 1) {
                // one line for the folder, not one per frame: a cooler without power puts every light here
                Warn(string.Create(CultureInfo.InvariantCulture,
                    $"{offTemperature.Count} of {headers.Count} lights have CCD-TEMP more than 1 °C from SET-TEMP (CCD-TEMP {offTemperature.Min(o => o.Ccd):0.0} to {offTemperature.Max(o => o.Ccd):0.0}, SET-TEMP {offTemperature[0].Set:0.0}; first {offTemperature[0].Name})"));
            }
            if (reference.GetString("BAYERPAT") == null) {
                Error("Lights have no BAYERPAT: ZWO Mono-bin was on or the profile Bayer pattern is None; Siril would debayer with its fallback pattern (SIR-07)");
            }
            var roworder = reference.GetString("ROWORDER");
            if (roworder != null && !roworder.Equals("TOP-DOWN", StringComparison.OrdinalIgnoreCase)) {
                Warn($"Lights have ROWORDER = {roworder}; NINA writes TOP-DOWN (SIR-06)");
            }
            var size = (reference.GetInt("NAXIS1"), reference.GetInt("NAXIS2"));
            var binning = reference.GetInt("XBINNING");

            if (plan.Flat == FlatSource.Folder) {
                CheckCalibrationFolder(plan.FlatsDirectory ?? Path.Combine(wd, SessionLayout.FlatsFolder), "FLAT", "flats", size, binning, Error);
            }
            if (plan.UsesBiasMaster) {
                CheckCalibrationFolder(plan.BiasesDirectory ?? Path.Combine(wd, SessionLayout.BiasesFolder), null, "biases", size, binning, Error);
            }
            if (plan.Dark == DarkSource.Folder) {
                CheckCalibrationFolder(plan.DarksDirectory ?? Path.Combine(wd, SessionLayout.DarksFolder), "DARK", "darks", size, binning, Error);
            }
            if (plan.FlatCalibration == FlatCalibration.SyntheticOffset) {
                if (reference.GetDouble("OFFSET") == null) {
                    Error("Synthetic flat offset needs the OFFSET header in the frames");
                }
                if (!plan.SyntheticOffsetMultiplier.HasValue || plan.SyntheticOffsetMultiplier.Value <= 0) {
                    Error("Synthetic flat offset needs a positive whole multiplier N for -bias=\"=N*$OFFSET\" (measure it once from biases and round it)");
                }
            }
            if (plan.Dark == DarkSource.Library && plan.MasterDarkPath != null) {
                CheckExplicitMaster(plan.MasterDarkPath, size, Error);
            } else if (plan.Dark == DarkSource.Library) {
                CheckLibraryMaster(plan.MasterDarkPathTemplate, reference, size, Error, Warn);
            }
            return issues;
        }

        /// <summary>
        /// The master Siril will resolve from the first light must exist and match the lights' size. Its header must also
        /// hold the lights' exact values for the template keys: %d truncation gives 20.0 s and 20.5 s darks the same name.
        /// </summary>
        private static void CheckLibraryMaster(string template, FitsHeader reference, (int?, int?) size, Action<string> error, Action<string> warn) {
            string expected;
            try {
                expected = Path.Combine(Path.GetDirectoryName(template), SirilPathTemplate.Resolve(Path.GetFileName(template), reference));
            } catch (SirilPathTemplateException ex) {
                error($"The first light's header cannot name a master dark ({ex.Message}); Siril would abort");
                return;
            }
            if (!File.Exists(expected)) {
                var available = Directory.Exists(Path.GetDirectoryName(template))
                    ? Directory.EnumerateFiles(Path.GetDirectoryName(template), "*.fit").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList()
                    : new List<string>();
                error($"No master dark {Path.GetFileName(expected)} in {Path.GetDirectoryName(template)} for these lights; Siril would abort. Available: {(available.Count == 0 ? "none" : string.Join(", ", available))}");
                return;
            }
            try {
                var master = FitsFile.ReadHeader(expected);
                if ((master.GetInt("NAXIS1"), master.GetInt("NAXIS2")) != size) {
                    error($"{Path.GetFileName(expected)} is {master.GetInt("NAXIS1")}x{master.GetInt("NAXIS2")}, lights are {size.Item1}x{size.Item2}");
                }
                foreach (var key in SirilPathTemplate.GetKeys(Path.GetFileName(template))) {
                    var light = reference.GetComparableValue(key);
                    var dark = master.GetComparableValue(key);
                    if (light != null && dark != null && !string.Equals(light, dark, StringComparison.Ordinal)) {
                        warn($"{Path.GetFileName(expected)} was stacked from darks with {key} = {dark}, the lights have {key} = {light} (same master name after %d truncation); Siril will use it anyway, shoot darks at {light} for an exact match");
                    }
                }
            } catch (Exception ex) when (ex is IOException || ex is InvalidDataException) {
                error($"{Path.GetFileName(expected)}: {ex.Message}");
            }
        }

        /// <summary>An explicit master (chosen by <see cref="MasterDarkIndex"/>) must exist and have the lights' size.</summary>
        private static void CheckExplicitMaster(string path, (int?, int?) size, Action<string> error) {
            if (!File.Exists(path)) {
                error($"The master dark {path} does not exist; Siril would abort");
                return;
            }
            try {
                var master = FitsFile.ReadHeader(path);
                if ((master.GetInt("NAXIS1"), master.GetInt("NAXIS2")) != size) {
                    error($"{Path.GetFileName(path)} is {master.GetInt("NAXIS1")}x{master.GetInt("NAXIS2")}, lights are {size.Item1}x{size.Item2}");
                }
            } catch (Exception ex) when (ex is IOException || ex is InvalidDataException) {
                error($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        private static void CheckCalibrationFolder(string directory, string expectedType, string label, (int?, int?) size, int? binning, Action<string> error) {
            var frames = SessionLayout.ListFitsFrames(directory);
            if (frames.Count == 0) {
                error($"No {label} in {directory}; Siril's convert aborts on an empty folder");
                return;
            }
            foreach (var other in SessionLayout.ListOtherImages(directory)) {
                error($"{Path.GetFileName(other)} in {label}/ would be converted too; move it out");
            }
            foreach (var frame in frames) {
                FitsHeader header;
                try {
                    header = FitsFile.ReadHeader(frame);
                } catch (Exception ex) when (ex is IOException || ex is InvalidDataException) {
                    error($"{Path.GetFileName(frame)}: {ex.Message}");
                    continue;
                }
                if ((header.GetInt("NAXIS1"), header.GetInt("NAXIS2")) != size) {
                    error($"{label}/{Path.GetFileName(frame)} is {header.GetInt("NAXIS1")}x{header.GetInt("NAXIS2")}, lights are {size.Item1}x{size.Item2}");
                }
                if (binning.HasValue && header.GetInt("XBINNING") is int b && b != binning.Value) {
                    error($"{label}/{Path.GetFileName(frame)} is binned {b}, lights {binning.Value}");
                }
                var type = header.GetString("IMAGETYP");
                if (expectedType != null && type != null && !type.Equals(expectedType, StringComparison.OrdinalIgnoreCase)) {
                    error($"{label}/{Path.GetFileName(frame)}: IMAGETYP = {type}, expected {expectedType}");
                }
            }
        }
    }
}
