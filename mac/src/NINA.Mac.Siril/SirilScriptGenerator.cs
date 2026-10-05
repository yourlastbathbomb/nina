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

namespace NINA.Mac.Siril {

    /// <summary>Where the lights' dark comes from.</summary>
    public enum DarkSource {

        /// <summary>Master from the shared library, chosen by Siril from the lights' header (default).</summary>
        Library,

        /// <summary>darks/ folder in the working directory, stacked like the stock script.</summary>
        Folder,

        /// <summary>No dark; lights get the flat-calibration bias instead (stock "WithoutDark" variant).</summary>
        None,
    }

    public enum FlatSource {
        Folder,
        None,
    }

    /// <summary>What is subtracted from the flats before they are stacked.</summary>
    public enum FlatCalibration {

        /// <summary>Master of the biases/ folder: dark flats (preferred) or biases (stock).</summary>
        BiasFrames,

        /// <summary>
        /// Uniform level -bias="=N*$OFFSET" from the OFFSET header (research SIR-M2); N measured once and rounded to a
        /// whole number, the only multiplier siril-cli 1.4.4 parses.
        /// </summary>
        SyntheticOffset,

        None,
    }

    public enum RegistrationReference {

        /// <summary>Stock: one pass, the first frame is the reference.</summary>
        FirstFrame,

        /// <summary>setref to the middle frame, then one pass: the stack keeps the mid-session orientation (alt-az default).</summary>
        MiddleFrame,

        /// <summary>register -2pass (Siril picks the reference) then seqapplyreg -framing=. RGB only.</summary>
        TwoPass,
    }

    /// <summary>seqapplyreg -framing= (used with <see cref="RegistrationReference.TwoPass"/>).</summary>
    public enum Framing {
        Current,
        Min,
        Max,
        Cog,
    }

    public enum ProcessingMode {

        /// <summary>OSC_Preprocessing: debayered RGB stack.</summary>
        Rgb,

        /// <summary>OSC_Extract_HaOIII: Ha and OIII stacks from undebayered CFA lights (dual-band nights).</summary>
        HaOIII,
    }

    /// <summary>Inputs of one per-target preprocessing script. Paths are absolute.</summary>
    public sealed class SirilPreprocessingPlan {

        public string Title { get; set; }

        public string WorkingDirectory { get; set; }

        public string LightsDirectory { get; set; }

        public string FlatsDirectory { get; set; }

        public string BiasesDirectory { get; set; }

        /// <summary>Only used with <see cref="DarkSource.Folder"/>.</summary>
        public string DarksDirectory { get; set; }

        public string MastersDirectory { get; set; }

        public string ProcessDirectory { get; set; }

        /// <summary>Absolute master-dark path with Siril header tokens (DarkLibrary.MasterDarkPathTemplate).</summary>
        public string MasterDarkPathTemplate { get; set; }

        public DarkSource Dark { get; set; } = DarkSource.Library;

        public FlatSource Flat { get; set; } = FlatSource.Folder;

        public FlatCalibration FlatCalibration { get; set; } = FlatCalibration.BiasFrames;

        /// <summary>
        /// N in -bias="=N*$OFFSET"; required (positive) for <see cref="FlatCalibration.SyntheticOffset"/>. Whole numbers
        /// only: siril-cli 1.4.4 aborts calibrate with "The offset value could not be parsed from expression" for 16.5,
        /// 15.75 and even 16.0 times $OFFSET (verified on this Mac). Round the measured bias/OFFSET slope; the pedestal
        /// error is then at most OFFSET/2 ADU.
        /// </summary>
        public int? SyntheticOffsetMultiplier { get; set; }

        public RegistrationReference Registration { get; set; } = RegistrationReference.MiddleFrame;

        public Framing Framing { get; set; } = Framing.Min;

        public ProcessingMode Mode { get; set; } = ProcessingMode.Rgb;

        /// <summary>Number of lights (for the middle reference); counted from <see cref="LightsDirectory"/> when null.</summary>
        public int? LightCount { get; set; }

        /// <summary>Default plan for a target of the layout: library darks, the target's (or the night's) flats and biases.</summary>
        public static SirilPreprocessingPlan ForTarget(TargetFolders target, DarkLibrary library) {
            return new SirilPreprocessingPlan {
                Title = $"{target.TargetName} ({SessionLayout.NightLabel(target.Night)})",
                WorkingDirectory = target.WorkingDirectory,
                LightsDirectory = target.Lights,
                FlatsDirectory = target.ResolveFlatsDirectory(),
                BiasesDirectory = target.ResolveBiasesDirectory(),
                DarksDirectory = Path.Combine(target.WorkingDirectory, SessionLayout.DarksFolder),
                MastersDirectory = target.Masters,
                ProcessDirectory = target.Process,
                MasterDarkPathTemplate = library?.MasterDarkPathTemplate,
            };
        }

        /// <summary>Plan for a stock-layout folder (biases/ flats/ darks/ lights/ next to each other), as OSC_Preprocessing expects.</summary>
        public static SirilPreprocessingPlan ForStockFolder(string workingDirectory) {
            var wd = Path.GetFullPath(workingDirectory);
            return new SirilPreprocessingPlan {
                Title = Path.GetFileName(wd),
                WorkingDirectory = wd,
                LightsDirectory = Path.Combine(wd, SessionLayout.LightsFolder),
                FlatsDirectory = Path.Combine(wd, SessionLayout.FlatsFolder),
                BiasesDirectory = Path.Combine(wd, SessionLayout.BiasesFolder),
                DarksDirectory = Path.Combine(wd, SessionLayout.DarksFolder),
                MastersDirectory = Path.Combine(wd, SessionLayout.MastersFolder),
                ProcessDirectory = Path.Combine(wd, SessionLayout.ProcessFolder),
                Dark = DarkSource.Folder,
                Registration = RegistrationReference.FirstFrame,
            };
        }

        internal bool UsesBiasMaster => FlatCalibration == FlatCalibration.BiasFrames && (Flat == FlatSource.Folder || Dark == DarkSource.None);
    }

    /// <summary>
    /// Generates the per-target Siril script: the steps of Siril 1.4.4's OSC_Preprocessing v1.4 (C. Richard; installed at
    /// /Applications/Siril.app/Contents/Resources/share/siril/scripts/OSC_Preprocessing.ssf) or OSC_Extract_HaOIII v1.5,
    /// with the same commands and parameters, adapted to the NINA-mac layout: the master dark comes from the library via
    /// path-parse tokens (Siril aborts when none matches), flats may be calibrated with a synthetic offset, and alt-az
    /// field rotation can use a mid-session reference. Paths are relative to the working directory, which the script
    /// enters first with an absolute cd. <see cref="PinOutputFormat"/> fixes the output format so file names and bit
    /// depth do not depend on the Siril preferences <see cref="SirilRunner"/> seeds from the GUI; like any 'set', those
    /// commands are saved into the ini siril-cli was started with, which the runner keeps separate from the user's GUI
    /// configuration.
    /// </summary>
    public static class SirilScriptGenerator {
        public const string RequiredSirilVersion = "1.3.4";
        public const string RgbScriptFileName = "nina_siril.ssf";
        public const string HaOIIIScriptFileName = "nina_siril_haoiii.ssf";

        /// <summary>Stacked result name (relative to the working directory), as in the stock script: result_&lt;LIVETIME&gt;s.fit.</summary>
        public const string ResultBaseName = "result_$LIVETIME:%d$s";

        public static SirilScript Generate(SirilPreprocessingPlan plan) {
            Check(plan);
            var warnings = new List<string>();
            var wd = Path.GetFullPath(plan.WorkingDirectory);
            var process = Path.GetFullPath(plan.ProcessDirectory ?? Path.Combine(wd, SessionLayout.ProcessFolder));
            var masters = Path.GetFullPath(plan.MastersDirectory ?? Path.Combine(wd, SessionLayout.MastersFolder));
            var lights = Path.GetFullPath(plan.LightsDirectory ?? Path.Combine(wd, SessionLayout.LightsFolder));
            var lightCount = plan.LightCount ?? SessionLayout.ListFitsFrames(lights).Count;
            if (lightCount == 0) {
                throw new InvalidOperationException($"No lights in {lights}");
            }
            if (lightCount > 9000) {
                warnings.Add($"{lightCount} lights: Siril raises the open-file limit to at most 10000 for FITS sequences (research SIR-M6); stack per night instead");
            }

            var b = new SirilScriptBuilder(wd);
            Header(b, plan, wd);
            b.Command("requires", RequiredSirilVersion);
            PinOutputFormat(b);
            b.CdAbsolute(wd);

            // Flat calibration level (bias master or synthetic offset)
            string biasArgument = null;
            if (plan.UsesBiasMaster) {
                var biases = Path.GetFullPath(plan.BiasesDirectory ?? Path.Combine(wd, SessionLayout.BiasesFolder));
                b.Blank();
                b.Comment("Convert Bias Frames (dark flats or biases) to .fit files");
                b.Cd(biases);
                b.Command("convert", "bias", "-out=" + b.Relative(process));
                b.Cd(process);
                b.Blank();
                b.Comment("Stack Bias Frames to bias_stacked.fit");
                b.Command("stack", "bias", "rej", "3", "3", "-nonorm", "-out=" + b.Relative(Path.Combine(masters, "bias_stacked")));
                b.Cd(wd);
                biasArgument = "-bias=" + Path.Combine(masters, "bias_stacked");
            } else if (plan.FlatCalibration == FlatCalibration.SyntheticOffset) {
                biasArgument = "-bias==" + plan.SyntheticOffsetMultiplier.Value.ToString(CultureInfo.InvariantCulture) + "*$OFFSET";
            }

            var flatMaster = Path.Combine(masters, "pp_flat_stacked");
            if (plan.Flat == FlatSource.Folder) {
                var flats = Path.GetFullPath(plan.FlatsDirectory ?? Path.Combine(wd, SessionLayout.FlatsFolder));
                b.Blank();
                b.Comment("Convert Flat Frames to .fit files");
                b.Cd(flats);
                b.Command("convert", "flat", "-out=" + b.Relative(process));
                b.Cd(process);
                var flatSequence = "flat";
                if (biasArgument != null) {
                    b.Blank();
                    b.Comment("Calibrate Flat Frames");
                    b.Command("calibrate", "flat", RelativeOption(b, biasArgument));
                    flatSequence = "pp_flat";
                }
                b.Blank();
                b.Comment("Stack Flat Frames to pp_flat_stacked.fit");
                b.Command("stack", flatSequence, "rej", "3", "3", "-norm=mul", "-out=" + b.Relative(flatMaster));
                b.Cd(wd);
            }

            string darkArgument = null;
            if (plan.Dark == DarkSource.Folder) {
                var darks = Path.GetFullPath(plan.DarksDirectory ?? Path.Combine(wd, SessionLayout.DarksFolder));
                var darkMaster = Path.Combine(masters, "dark_stacked");
                b.Blank();
                b.Comment("Convert Dark Frames to .fit files");
                b.Cd(darks);
                b.Command("convert", "dark", "-out=" + b.Relative(process));
                b.Cd(process);
                b.Blank();
                b.Comment("Stack Dark Frames to dark_stacked.fit");
                b.Command("stack", "dark", "rej", "3", "3", "-nonorm", "-out=" + b.Relative(darkMaster));
                b.Cd(wd);
                darkArgument = "-dark=" + darkMaster;
            } else if (plan.Dark == DarkSource.Library) {
                darkArgument = "-dark=" + plan.MasterDarkPathTemplate;
            }

            b.Blank();
            b.Comment("Convert Light Frames to .fit files");
            b.Cd(lights);
            b.Command("convert", "light", "-out=" + b.Relative(process));
            b.Cd(process);

            // calibrate light: stock argument order -dark -flat -cc=dark -cfa -equalize_cfa -debayer
            var calibrate = new List<string> { "light" };
            if (darkArgument != null) {
                calibrate.Add(plan.Dark == DarkSource.Library ? darkArgument : RelativeOption(b, darkArgument));
                if (plan.Dark == DarkSource.Library) {
                    b.Blank();
                    b.Comment("Master dark from the library: Siril fills the $KEY:fmt$ tokens from the first light's header and");
                    b.Comment("stops the script if no master matches (exposure, gain, offset, set-point, binning).");
                }
            } else if (biasArgument != null) {
                calibrate.Add(RelativeOption(b, biasArgument)); // stock "WithoutDark": lights get the bias instead
            }
            if (plan.Flat == FlatSource.Folder) {
                calibrate.Add(RelativeOption(b, "-flat=" + flatMaster));
            }
            if (darkArgument != null) {
                calibrate.Add("-cc=dark");
            }
            if (darkArgument != null || plan.Flat == FlatSource.Folder) {
                calibrate.Add("-cfa");
            }
            if (plan.Flat == FlatSource.Folder) {
                calibrate.Add("-equalize_cfa");
            }
            if (plan.Mode == ProcessingMode.Rgb) {
                calibrate.Add("-debayer");
            }
            if (calibrate.Count == 1) {
                throw new InvalidOperationException("Nothing to calibrate: Ha/OIII extraction without dark, bias or flat is not supported");
            }
            b.Blank();
            b.Comment("Calibrate Light Frames");
            b.Command("calibrate", calibrate.ToArray());

            if (plan.Mode == ProcessingMode.Rgb) {
                Register(b, plan, "pp_light", lightCount);
                b.Blank();
                b.Comment("Stack calibrated lights to result.fit");
                var stack = new List<string> { "r_pp_light", "rej", "3", "3", "-norm=addscale", "-output_norm", "-rgb_equal", "-32b" };
                if (plan.Registration == RegistrationReference.TwoPass && plan.Framing == Framing.Max) {
                    stack.Add("-maximize");
                }
                stack.Add("-out=result");
                b.Command("stack", stack.ToArray());
                b.Blank();
                b.Comment("flip if required");
                b.Command("load", "result");
                b.Command("mirrorx", "-bottomup");
                b.Command("save", b.Relative(Path.Combine(wd, ResultBaseName)));
            } else {
                b.Blank();
                b.Comment("Extract Ha and OIII");
                b.Command("seqextract_HaOIII", "pp_light", "-resample=ha");
                Register(b, plan, "Ha_pp_light", lightCount);
                b.Blank();
                b.Comment("Stack calibrated Ha lights to Ha_stack (temporary)");
                b.Command("stack", "r_Ha_pp_light", "rej", "3", "3", "-norm=addscale", "-output_norm", "-32b", "-out=results_00001");
                b.Blank();
                b.Comment("and flip if required");
                b.Command("mirrorx_single", "results_00001");
                Register(b, plan, "OIII_pp_light", lightCount);
                b.Blank();
                b.Comment("Stack calibrated OIII lights to OIII_stack (temporary)");
                b.Command("stack", "r_OIII_pp_light", "rej", "3", "3", "-norm=addscale", "-output_norm", "-32b", "-out=results_00002");
                b.Blank();
                b.Comment("and flip if required");
                b.Command("mirrorx_single", "results_00002");
                b.Blank();
                b.Comment("Align the result images, small shifts and chromatic aberrations can occur");
                b.Command("register", "results", "-transf=shift", "-interp=none");
                b.Blank();
                b.Comment("Renorm OIII to Ha using PixelMath");
                b.Command("pm", "$r_results_00002$*mad($r_results_00001$)/mad($r_results_00002$)-mad($r_results_00001$)/mad($r_results_00002$)*median($r_results_00002$)+median($r_results_00001$)");
                b.Command("save", b.Relative(Path.Combine(wd, "result_OIII_$LIVETIME:%d$s")));
                b.Blank();
                b.Comment("Save Ha final result");
                b.Command("load", "r_results_00001");
                b.Command("save", b.Relative(Path.Combine(wd, "result_Ha_$LIVETIME:%d$s")));
            }
            b.Blank();
            b.Cd(wd);
            b.Command("close");
            return new SirilScript(b.ToString(), wd, plan.Mode == ProcessingMode.Rgb ? RgbScriptFileName : HaOIIIScriptFileName, warnings);
        }

        /// <summary>
        /// Pins what siril-cli would otherwise take from the preferences copied out of the GUI configuration (siril-cli
        /// 1.4.4, verified with a seed holding the GUI values): 'setext fit' (else .fits/.fts names), 'set32bits' (else
        /// force_16bit=true saves masters, pp_ frames and the OIII result as 16-bit integers, clipping negative
        /// calibrated values to 0; only stack -32b overrides it) and 'setcompress 0' (else masters and results become
        /// .fit.fz, which the -dark= template, the validator and the result collection do not look for).
        /// </summary>
        internal static void PinOutputFormat(SirilScriptBuilder b) {
            b.Command("setext", "fit");
            b.Command("set32bits");
            b.Command("setcompress", "0");
        }

        private static void Header(SirilScriptBuilder b, SirilPreprocessingPlan plan, string wd) {
            b.Comment("############################################");
            b.Comment($"NINA-mac Siril preprocessing: {plan.Title ?? Path.GetFileName(wd)}");
            b.Comment(plan.Mode == ProcessingMode.Rgb
                ? "Steps of Siril 1.4.4 OSC_Preprocessing v1.4 (C) Cyril Richard"
                : "Steps of Siril 1.4.4 OSC_Extract_HaOIII v1.5 (C) Cyril Richard");
            b.Comment($"dark: {plan.Dark}, flats: {plan.Flat}, flat calibration: {plan.FlatCalibration}, registration: {plan.Registration}"
                + (plan.Registration == RegistrationReference.TwoPass ? $" (framing {plan.Framing})" : string.Empty));
            b.Comment("Generated by NINA.Mac.Siril; run with siril-cli -o -i <own ini> so the GUI settings are untouched");
            b.Comment("############################################");
            b.Blank();
        }

        private static void Register(SirilScriptBuilder b, SirilPreprocessingPlan plan, string sequence, int lightCount) {
            b.Blank();
            switch (plan.Registration) {
                case RegistrationReference.FirstFrame:
                    b.Comment("Align lights");
                    b.Command("register", sequence);
                    break;

                case RegistrationReference.MiddleFrame:
                    var middle = (lightCount + 1) / 2;
                    b.Comment($"Align lights on the middle frame ({middle} of {lightCount}): mid-session orientation for alt-az field rotation");
                    b.Command("setref", sequence, middle.ToString(CultureInfo.InvariantCulture));
                    b.Command("register", sequence);
                    break;

                case RegistrationReference.TwoPass:
                    b.Comment("Align lights: two passes (Siril picks the reference), then apply with framing");
                    b.Command("register", sequence, "-2pass");
                    b.Command("seqapplyreg", sequence, "-framing=" + plan.Framing.ToString().ToLowerInvariant());
                    break;
            }
        }

        /// <summary>Rewrites "-opt=/abs/path" as "-opt=relative/path" from the current directory (stock scripts use relative paths).</summary>
        private static string RelativeOption(SirilScriptBuilder b, string option) {
            var eq = option.IndexOf('=');
            var value = option.Substring(eq + 1);
            if (value.StartsWith('=') || !Path.IsPathRooted(value)) {
                return option;
            }
            return option.Substring(0, eq + 1) + b.Relative(value);
        }

        private static void Check(SirilPreprocessingPlan plan) {
            if (plan == null) {
                throw new ArgumentNullException(nameof(plan));
            }
            if (string.IsNullOrWhiteSpace(plan.WorkingDirectory) || !Path.IsPathRooted(plan.WorkingDirectory)) {
                throw new ArgumentException("WorkingDirectory must be an absolute path");
            }
            if (plan.Dark == DarkSource.Library) {
                if (string.IsNullOrWhiteSpace(plan.MasterDarkPathTemplate) || !Path.IsPathRooted(plan.MasterDarkPathTemplate)) {
                    throw new ArgumentException("DarkSource.Library needs an absolute MasterDarkPathTemplate");
                }
                EnsureSirilSafeDirectory(Path.GetDirectoryName(plan.MasterDarkPathTemplate), "master-dark library");
            }
            if (plan.FlatCalibration == FlatCalibration.SyntheticOffset && (!plan.SyntheticOffsetMultiplier.HasValue || plan.SyntheticOffsetMultiplier.Value <= 0)) {
                throw new ArgumentException("FlatCalibration.SyntheticOffset needs a positive whole SyntheticOffsetMultiplier (measure it once from biases and round it)");
            }
            if (plan.Mode == ProcessingMode.HaOIII && plan.Registration == RegistrationReference.TwoPass) {
                throw new NotSupportedException("Ha/OIII extraction aligns the two stacks with shifts only, so both must share one reference: use FirstFrame or MiddleFrame");
            }
        }

        /// <summary>
        /// Directories inside a path-parsed argument must not contain '$' (token delimiter) or ':' (key separator), see
        /// Siril 1.4.4 src/io/path_parse.c:376-436.
        /// </summary>
        internal static void EnsureSirilSafeDirectory(string directory, string what) {
            if (directory != null && (directory.Contains('$') || directory.Contains(':'))) {
                throw new ArgumentException($"The {what} path '{directory}' contains '$' or ':', which Siril's path parsing would misread");
            }
        }
    }
}
