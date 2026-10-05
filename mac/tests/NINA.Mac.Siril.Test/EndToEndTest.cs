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
using NINA.Mac.Siril.Test.Synthetic;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.Siril.Test {

    /// <summary>
    /// One synthetic night through the whole Siril output side with the real siril-cli 1.4.4: frames written by the
    /// layout, master darks built into the library, the generated script run, and the outputs measured.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class EndToEndTest {
        private const int LightCount = 16;
        private const double LightExposure = 20;
        private const string Target = "NGC 253";
        private const double ApertureRadius = 6;

        /// <summary>
        /// Allowed relative error of a master flat's corner/centre ratio against the rig's vignetting model: the box
        /// medians are good to about 0.05 %, and the 1 s of dark current a synthetic offset leaves in the flats moves the
        /// ratio by about 0.1 % (blue, the dimmest channel), while the 800 ADU bias left in raw flats moves it by more
        /// than 1 % in every channel.
        /// </summary>
        private const double FlatRatioTolerance = 0.004;

        /// <summary>
        /// Siril preferences as the GUI may hold them: .fits extension, 16-bit processed images (force_16bit=true is in
        /// William's GUI configuration) and FITS compression. Written by the tests; the user's configuration is not read.
        /// </summary>
        private const string GuiStyleSirilIni = "[core]\nextension=.fits\nforce_16bit=true\n[compression]\nenabled=true\nmethod=0\nquantization=16\nhcompress_scale=4\n";

        private string tmp;
        private SessionLayout layout;
        private SyntheticRig rig;
        private SirilRunner runner;
        private TargetFolders target;
        private FrameOffset[] offsets;
        private DarkLibraryBuildResult build;

        private static DateTime Hkt(int y, int mo, int d, int h, int mi, int s = 0) =>
            TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified), SessionLayout.HongKong);

        [OneTimeSetUp]
        public async Task WriteNightAndBuildDarkLibrary() {
            TestEnv.RequireSiril();
            tmp = TestEnv.NewTempDirectory("e2e");
            layout = new SessionLayout(new SessionLayoutOptions { Root = Path.Combine(tmp, "Astro NINA") });
            rig = new SyntheticRig();
            runner = TestEnv.Runner(tmp);

            // Lights: 21:00 HKT on 2026-10-03, a few pixels of drift and alt-az field rotation between frames
            offsets = Enumerable.Range(0, LightCount)
                .Select(k => new FrameOffset(4.0 * Math.Sin(0.9 * k) + 0.37, 3.0 * Math.Cos(1.3 * k) - 0.21, (k - 7.5) * 0.1))
                .ToArray();
            var start = Hkt(2026, 10, 3, 21, 0);
            for (var k = 0; k < LightCount; k++) {
                var info = NinaFrames.Info("LIGHT", start.AddSeconds(30 * k), k + 1, LightExposure, Target, rig);
                NinaFrames.Save(layout, info, rig, rig.Light(LightExposure, offsets[k], 1000 + k));
            }
            // A framing snapshot during the session: must not be stacked
            NinaFrames.Save(layout, NinaFrames.Info("SNAPSHOT", start.AddMinutes(3), 1, 2, Target, rig), rig, rig.Light(2, offsets[0], 999));

            // Flats and dark flats at dawn (still night 2026-10-03), same target
            for (var k = 0; k < 12; k++) {
                NinaFrames.Save(layout, NinaFrames.Info("FLAT", Hkt(2026, 10, 4, 4, 40).AddSeconds(5 * k), k + 1, 1.0, Target, rig), rig, rig.Flat(2000 + k));
                NinaFrames.Save(layout, NinaFrames.Info("DARK", Hkt(2026, 10, 4, 4, 45).AddSeconds(5 * k), k + 1, 1.0, Target, rig, darkFlat: true), rig, rig.Dark(1.0, 3000 + k));
            }

            // Dark library from a rainy night: 20 s and 10 s at gain 252, and 20 s at gain 100 with a mirrored ramp
            for (var k = 0; k < 12; k++) {
                NinaFrames.Save(layout, NinaFrames.Info("DARK", Hkt(2026, 9, 28, 22, 0).AddSeconds(30 * k), k + 1, 20, null, rig), rig, rig.Dark(20, 4000 + k));
                NinaFrames.Save(layout, NinaFrames.Info("DARK", Hkt(2026, 9, 28, 23, 0).AddSeconds(15 * k), k + 1, 10, null, rig), rig, rig.Dark(10, 5000 + k));
            }
            var (left, right) = (rig.DarkRampLeft, rig.DarkRampRight);
            rig.Gain = 100;
            (rig.DarkRampLeft, rig.DarkRampRight) = (right, left);
            for (var k = 0; k < 12; k++) {
                NinaFrames.Save(layout, NinaFrames.Info("DARK", Hkt(2026, 9, 29, 0, 0).AddSeconds(30 * k), k + 1, 20, null, rig), rig, rig.Dark(20, 6000 + k));
            }
            rig.Gain = 252;
            (rig.DarkRampLeft, rig.DarkRampRight) = (left, right);

            target = layout.GetTargetFolders(new DateOnly(2026, 10, 3), Target);
            build = await layout.Library.BuildMastersAsync(runner);
            TestEnv.Print(build.Run);
        }

        [OneTimeTearDown]
        public void Cleanup() => TestEnv.Delete(tmp);

        [Test, Order(1)]
        public void Layout_PutsEveryFrameWhereSirilExpectsIt() {
            SessionLayout.ListFitsFrames(target.Lights).Should().HaveCount(LightCount);
            SessionLayout.ListFitsFrames(target.Snapshots).Should().HaveCount(1, "the snapshot is kept out of lights/");
            SessionLayout.ListFitsFrames(target.Flats).Should().HaveCount(12);
            SessionLayout.ListFitsFrames(target.Biases).Should().HaveCount(12, "dark flats calibrate the flats");
            Directory.GetDirectories(layout.Library.DarksDirectory).Select(Path.GetFileName).Should()
                .BeEquivalentTo("10.00s_g252_o50_0.00C_2x2", "20.00s_g252_o50_0.00C_2x2", "20.00s_g100_o50_0.00C_2x2");
        }

        [Test, Order(2)]
        public void DarkLibrary_MastersAreNamedBySirilExactlyAsPredicted() {
            build.Run.Should().NotBeNull();
            build.Run.Succeeded.Should().BeTrue(build.Run.Summary);
            build.MissingMasters.Should().BeEmpty("Siril wrote the master names the C# side predicted");
            build.Built.Should().HaveCount(3);
            layout.Library.ListMasters().Select(Path.GetFileName).Should()
                .Equal("dark_10s_G252_O50_T0_B2.fit", "dark_20s_G100_O50_T0_B2.fit", "dark_20s_G252_O50_T0_B2.fit");

            // The masters hold what the darks hold: bias + ramp scaled by exposure (gain 100 set has the ramp mirrored)
            var master20 = Plane.FromImage(Adu(FitsFile.ReadImage(Path.Combine(layout.Library.MastersDirectory, "dark_20s_G252_O50_T0_B2.fit"))), 0);
            var master10 = Plane.FromImage(Adu(FitsFile.ReadImage(Path.Combine(layout.Library.MastersDirectory, "dark_10s_G252_O50_T0_B2.fit"))), 0);
            var master100 = Plane.FromImage(Adu(FitsFile.ReadImage(Path.Combine(layout.Library.MastersDirectory, "dark_20s_G100_O50_T0_B2.fit"))), 0);
            double Edge(Plane p, int x) => Measure.BoxMedian(p, x, 200, 8, 80);
            Edge(master20, 0).Should().BeApproximately(rig.BiasLevel + rig.DarkCurrent(3, 20), 4);
            Edge(master20, rig.Width - 8).Should().BeApproximately(rig.BiasLevel + rig.DarkCurrent(rig.Width - 5, 20), 4);
            Edge(master10, rig.Width - 8).Should().BeApproximately(rig.BiasLevel + rig.DarkCurrent(rig.Width - 5, 10), 4);
            Edge(master100, 0).Should().BeApproximately(rig.BiasLevel + rig.DarkRampRight, 4);

            // Second build: nothing to do
            var again = layout.Library.BuildMastersAsync(runner).GetAwaiter().GetResult();
            again.Run.Should().BeNull();
            again.Skipped.Should().HaveCount(3);
        }

        [Test, Order(3)]
        public async Task CalibratedPipeline_RemovesBiasDarkAndVignetting_AndStacksRegisteredRgb() {
            var plan = SirilPreprocessingPlan.ForTarget(target, layout.Library);
            SirilSessionValidator.Validate(plan).Where(i => i.Severity == SirilIssueSeverity.Error).Should().BeEmpty();

            var result = await SirilPreprocessor.RunAsync(plan, runner);
            TestEnv.Print(result.Run);

            result.Succeeded.Should().BeTrue(result.Run?.Summary);
            result.Results.Select(Path.GetFileName).Should().Equal($"result_{LightCount * (int)LightExposure}s.fit");
            result.Run.LogLines.Should().Contain(l => l.Contains($"Total: 0 failed, {LightCount} registered"));
            result.Run.LogLines.Should().Contain(l => l.Contains("Reading FITS: file dark_20s_G252_O50_T0_B2.fit"), "Siril picked the 20 s / gain 252 master");
            File.ReadAllText(result.ScriptPath).Should().Contain("setref pp_light 8");

            // Final stack: RGB, frame size (framing on the reference), normalised [0, 1], finite
            var stack = FitsFile.ReadImage(result.Results[0]);
            stack.PlaneCount.Should().Be(3);
            (stack.Width, stack.Height).Should().Be((rig.Width, rig.Height));
            var stats = Measure.Stats(stack);
            stats.NonFinite.Should().Be(0);
            stats.Min.Should().BeGreaterThanOrEqualTo(0);
            stats.Max.Should().BeLessThanOrEqualTo(1.0001);
            stats.Mean.Should().BeInRange(0.001, 0.5);

            var m = StackMetrics(stack, offsets[7], search: 6);
            Report("calibrated stack", m);
            m.FluxRatio.Should().BeInRange(0.92, 1.08, "flat-fielding gives the vignetted corner star its true flux");
            m.CornerOffset.Should().BeInRange(-0.08, 0.08, "no vignetting or un-subtracted pedestal left in the background");
            m.LeftRightOffset.Should().BeInRange(-0.08, 0.08, "the dark-current ramp is subtracted");

            // Master flat: the dark flats removed the pedestal, so each CFA channel falls off exactly like the vignetting
            AssertFlatHasNoPedestal(Path.Combine(target.Masters, "pp_flat_stacked.fit"), "dark-flat calibrated flats");

            // Registration: the stacked star is as compact as in the single reference frame
            var reference = Plane.FromImage(FitsFile.ReadImage(Path.Combine(target.Process, "pp_light_00008.fit")), 1);
            var (cx, cy) = rig.Transform(rig.CenterStar, offsets[7]);
            var single = Measure.Star(reference, cx, cy, 6, ApertureRadius);
            TestContext.Progress.WriteLine($"HFR single {single.Hfr:F3} px, stack {m.CenterHfr:F3} px");
            m.CenterHfr.Should().BeLessThan(single.Hfr * 1.15);

            // Calibrated light in ADU: colour ratios of the sky restored, background flat in every channel
            var pp = Adu(FitsFile.ReadImage(Path.Combine(target.Process, "pp_light_00001.fit")));
            pp.PlaneCount.Should().Be(3, "calibrate -debayer writes RGB");
            var bg = Enumerable.Range(0, 3).Select(c => Backgrounds(Plane.FromImage(pp, c))).ToArray();
            for (var c = 0; c < 3; c++) {
                TestContext.Progress.WriteLine($"pp_light channel {c}: centre {bg[c].Center:F1} corners {bg[c].Corners:F1} left {bg[c].Left:F1} right {bg[c].Right:F1} ADU");
                (bg[c].Corners / bg[c].Center).Should().BeInRange(0.97, 1.03, $"channel {c} has no vignetting left");
                (bg[c].Right / bg[c].Left).Should().BeInRange(0.97, 1.03, $"channel {c} has no dark ramp left");
            }
            (bg[0].Center / bg[1].Center).Should().BeApproximately(rig.ChannelResponse[0] / rig.ChannelResponse[1], 0.04 * 1.667, "R/G of the sky, so the pedestal is gone");
            (bg[2].Center / bg[1].Center).Should().BeApproximately(rig.ChannelResponse[2] / rig.ChannelResponse[1], 0.04 * 0.5, "B/G of the sky");

            // Control: the raw light really has the pedestal, the ramp and the vignetting the checks above rule out
            var raw = FitsFile.ReadImage(SessionLayout.ListFitsFrames(target.Lights)[0]);
            var rawG = Backgrounds(Plane.SuperPixel(raw, 1), halfSize: true);
            var rawR = Backgrounds(Plane.SuperPixel(raw, 0), halfSize: true);
            TestContext.Progress.WriteLine($"raw G: centre {rawG.Center:F1} corners {rawG.Corners:F1} left {rawG.Left:F1} right {rawG.Right:F1}; raw R/G {rawR.Center / rawG.Center:F3}");
            (rawG.Corners / rawG.Center).Should().BeLessThan(0.9);
            (rawG.Right - rawG.Left).Should().BeGreaterThan(200);
            (rawR.Center / rawG.Center).Should().BeLessThan(1.5);

            // Re-running cleans process/ (links only; the lights stay) and succeeds again
            var rerun = await SirilPreprocessor.RunAsync(plan, runner);
            rerun.Succeeded.Should().BeTrue(rerun.Run?.Summary);
            SessionLayout.ListFitsFrames(target.Lights).Should().HaveCount(LightCount);
        }

        [Test, Order(4)]
        public async Task UncalibratedControl_ShowsWhatTheCalibrationRemoved() {
            var plan = SeparateRun("uncalibrated");
            plan.Dark = DarkSource.None;
            plan.Flat = FlatSource.None;
            plan.FlatCalibration = FlatCalibration.None;

            var result = await SirilPreprocessor.RunAsync(plan, runner);
            TestEnv.Print(result.Run);

            result.Succeeded.Should().BeTrue(result.Run?.Summary);
            File.ReadAllText(result.ScriptPath).Should().Contain("calibrate light -debayer");
            var m = StackMetrics(FitsFile.ReadImage(result.Results.Single()), offsets[7], search: 6);
            Report("uncalibrated stack", m);
            m.FluxRatio.Should().BeLessThan(0.8, "the corner star stays vignetted");
            m.CornerOffset.Should().BeLessThan(-0.3, "pedestal plus vignetting makes the corners darker");
            m.LeftRightOffset.Should().BeGreaterThan(0.25, "the dark ramp is still there");
        }

        [Test, Order(5)]
        public async Task SyntheticOffsetFlats_AndTwoPassMinFraming_StillCalibrate() {
            var plan = SeparateRun("2pass");
            plan.FlatCalibration = FlatCalibration.SyntheticOffset;
            plan.SyntheticOffsetMultiplier = 16;
            plan.Registration = RegistrationReference.TwoPass;
            plan.Framing = Framing.Min;

            var result = await SirilPreprocessor.RunAsync(plan, runner);
            TestEnv.Print(result.Run);

            result.Succeeded.Should().BeTrue(result.Run?.Summary);
            result.Run.LogLines.Should().Contain(l => l.Contains($"Synthetic offset: Level = {(int)rig.BiasLevel}"));
            AssertFlatHasNoPedestal(Path.Combine(plan.MastersDirectory, "pp_flat_stacked.fit"), "synthetic-offset flats");
            var stack = FitsFile.ReadImage(result.Results.Single());
            stack.PlaneCount.Should().Be(3);
            stack.Width.Should().BeInRange(rig.Width - 40, rig.Width - 1, "min framing crops to the common area");
            stack.Height.Should().BeInRange(rig.Height - 40, rig.Height - 1);
            var m = StackMetrics(stack, offsets[7], search: 25);
            Report("synthetic offset + 2pass/min stack", m);
            m.FluxRatio.Should().BeInRange(0.9, 1.1);
            m.CornerOffset.Should().BeInRange(-0.1, 0.1);
            m.LeftRightOffset.Should().BeInRange(-0.1, 0.1);
        }

        [Test, Order(6)]
        public async Task HaOIIIMode_WritesHaAndOIIIStacks_UpsampledToTheFrameSize() {
            var plan = SeparateRun("HaOIII");
            plan.Mode = ProcessingMode.HaOIII;

            var result = await SirilPreprocessor.RunAsync(plan, runner);
            TestEnv.Print(result.Run);

            result.Succeeded.Should().BeTrue(result.Run?.Summary);
            result.Results.Select(Path.GetFileName).Should().BeEquivalentTo("result_Ha_320s.fit", "result_OIII_320s.fit");
            foreach (var path in result.Results) {
                var image = FitsFile.ReadImage(path);
                image.PlaneCount.Should().Be(1, Path.GetFileName(path));
                // seqextract_HaOIII -resample=ha (stock script) upsamples the half-size Ha to the OIII size (Siril 1.4.4
                // STR_EXTRACTHAOIII), so both results have the light-frame size; research SIR-14 expected half size
                (image.Width, image.Height).Should().Be((rig.Width, rig.Height));
                Measure.Stats(image).NonFinite.Should().Be(0);
                var plane = Plane.FromImage(image, 0);
                var (cx, cy) = rig.Transform(rig.CenterStar, offsets[7]);
                var star = Measure.Star(plane, cx, cy, 6, ApertureRadius);
                var noise = RobustSigma(plane, 20, 20, 60, 60);
                TestContext.Progress.WriteLine($"{Path.GetFileName(path)}: centre star peak {star.Peak:G4}, background sigma {noise:G4}");
                star.Peak.Should().BeGreaterThan(20 * noise, "the stacked star stands out");
            }
        }

        [Test, Order(7)]
        public async Task ExposureMismatch_SirilAbortsBeforeCalibrating() {
            var m83 = WriteLights("M 83", exposure: 30, gain: 252, count: 4, hour: 23);
            var plan = SirilPreprocessingPlan.ForTarget(m83, layout.Library);
            plan.Flat = FlatSource.None;
            plan.FlatCalibration = FlatCalibration.None;
            var expected = Path.Combine(layout.Library.MastersDirectory, "dark_30s_G252_O50_T0_B2.fit");

            SirilSessionValidator.Validate(plan).Should().Contain(i => i.Severity == SirilIssueSeverity.Error && i.Message.Contains("dark_30s_G252_O50_T0_B2.fit"));

            var result = await SirilPreprocessor.RunAsync(plan, runner, validate: false);
            TestEnv.Print(result.Run);

            result.Succeeded.Should().BeFalse();
            result.Run.ExitCode.Should().NotBe(0);
            result.Run.ReportedFailure.Should().BeTrue();
            result.Run.FailedCommand.Should().Be("calibrate");
            result.MissingMasterDark.Should().Be(expected);
            result.Results.Should().BeEmpty();
            Directory.EnumerateFiles(m83.WorkingDirectory, "result_*").Should().BeEmpty();
        }

        [Test, Order(8)]
        public async Task GainMismatch_SirilAbortsBeforeCalibrating() {
            var m83 = WriteLights("M 83 gain 200", exposure: 20, gain: 200, count: 4, hour: 23);
            var plan = SirilPreprocessingPlan.ForTarget(m83, layout.Library);
            plan.Flat = FlatSource.None;
            plan.FlatCalibration = FlatCalibration.None;

            var result = await SirilPreprocessor.RunAsync(plan, runner, validate: false);
            TestEnv.Print(result.Run);

            result.Succeeded.Should().BeFalse();
            result.Run.FailedCommand.Should().Be("calibrate");
            result.MissingMasterDark.Should().Be(Path.Combine(layout.Library.MastersDirectory, "dark_20s_G200_O50_T0_B2.fit"));
        }

        [Test, Order(9)]
        public async Task StockFolderPlan_GivesTheSameStackAsTheInstalledOscPreprocessingScript() {
            var stockScript = Path.Combine(TestEnv.StockScripts, "OSC_Preprocessing.ssf");
            if (!File.Exists(stockScript)) {
                Assert.Ignore("Siril's stock scripts are not installed");
            }
            var stockDir = StockFolder("stock script");
            var oursDir = StockFolder("generated");

            var stockRun = await runner.RunAsync(stockScript, stockDir, Path.Combine(stockDir, "stock.log"));
            TestEnv.Print(stockRun);
            var ours = await SirilPreprocessor.RunAsync(SirilPreprocessingPlan.ForStockFolder(oursDir), runner);
            TestEnv.Print(ours.Run);

            stockRun.Succeeded.Should().BeTrue(stockRun.Summary);
            ours.Succeeded.Should().BeTrue(ours.Run?.Summary);
            var a = FitsFile.ReadImage(Path.Combine(stockDir, "result_320s.fit"));
            var b = FitsFile.ReadImage(ours.Results.Single());
            (b.Width, b.Height, b.PlaneCount).Should().Be((a.Width, a.Height, a.PlaneCount));
            var maxDiff = 0.0;
            for (var p = 0; p < a.PlaneCount; p++) {
                for (var i = 0; i < a.Planes[p].Length; i++) {
                    maxDiff = Math.Max(maxDiff, Math.Abs(a.Planes[p][i] - b.Planes[p][i]));
                }
            }
            TestContext.Progress.WriteLine($"stock vs generated: max |difference| = {maxDiff:G3}");
            maxDiff.Should().BeLessThan(1e-5);
        }

        [Test, Order(10)]
        public async Task RawFlatsControl_LeaveThePedestalTheMasterFlatCheckCatches() {
            var plan = SeparateRun("raw flats");
            plan.FlatCalibration = FlatCalibration.None; // flats stacked with nothing subtracted

            var result = await SirilPreprocessor.RunAsync(plan, runner);
            TestEnv.Print(result.Run);

            result.Succeeded.Should().BeTrue(result.Run?.Summary);
            var measured = FlatRatioDeviations(Path.Combine(plan.MastersDirectory, "pp_flat_stacked.fit"));
            var predicted = RawFlatRatioDeviations();
            for (var c = 0; c < 3; c++) {
                TestContext.Progress.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"raw flats channel {c}: corner/centre ratio off the vignetting by {measured[c]:P2} (model with the bias left in: {predicted[c]:P2})"));
                predicted[c].Should().BeGreaterThan(2 * FlatRatioTolerance, "the check must be able to see this rig's bias in the flats");
                measured[c].Should().BeApproximately(predicted[c], FlatRatioTolerance, $"channel {c} keeps the 800 ADU pedestal");
                Math.Abs(measured[c]).Should().BeGreaterThan(FlatRatioTolerance, $"AssertFlatHasNoPedestal fails on channel {c}");
            }
        }

        [Test, Order(11)]
        public async Task GuiStylePreferences_16BitCompressionAndFits_DoNotChangeWhatTheScriptsWrite() {
            var seed = Path.Combine(tmp, "gui-style config.ini");
            File.WriteAllText(seed, GuiStyleSirilIni);
            var gui = new SirilRunner(new SirilRunnerOptions {
                SeedConfigPath = seed,
                OwnConfigPath = Path.Combine(tmp, "gui-style siril-cli.ini"),
                Timeout = TimeSpan.FromMinutes(10),
            });

            // Control: without the pinned output format these preferences really apply (compressed master, not found)
            var control = new DarkLibrary(Path.Combine(tmp, "gui-style control library"));
            CopyDirectory(Path.Combine(layout.Library.DarksDirectory, "20.00s_g252_o50_0.00C_2x2"), Path.Combine(control.DarksDirectory, "20.00s_g252_o50_0.00C_2x2"));
            var unpinned = control.CreateMasterBuildScript(control.ScanSets()).Text.Replace("set32bits\n", string.Empty).Replace("setcompress 0\n", string.Empty);
            unpinned.Should().Contain("setext fit").And.NotContain("set32bits").And.NotContain("setcompress");
            Directory.CreateDirectory(control.ProcessDirectory);
            var controlRun = await gui.RunAsync(new SirilScript(unpinned, control.Root, "unpinned.ssf", null).Save(Path.Combine(control.ProcessDirectory, "unpinned.ssf")), control.Root);
            TestEnv.Print(controlRun);
            controlRun.Succeeded.Should().BeTrue(controlRun.Summary);
            Directory.GetFiles(control.MastersDirectory).Select(Path.GetFileName).Should().Equal("dark_20s_G252_O50_T0_B2.fit.fz");
            control.ListMasters().Should().BeEmpty();

            // Library masters built with the generated script: .fit, 32-bit float
            var library = new DarkLibrary(Path.Combine(tmp, "gui-style library"));
            CopyDirectory(layout.Library.DarksDirectory, library.DarksDirectory);
            var built = await library.BuildMastersAsync(gui);
            TestEnv.Print(built.Run);
            built.Succeeded.Should().BeTrue(built.Run?.Summary);
            Directory.GetFiles(library.MastersDirectory).Select(Path.GetFileName).Should()
                .BeEquivalentTo("dark_10s_G252_O50_T0_B2.fit", "dark_20s_G100_O50_T0_B2.fit", "dark_20s_G252_O50_T0_B2.fit");
            foreach (var master in library.ListMasters()) {
                FitsFile.ReadHeader(master).GetInt("BITPIX").Should().Be(-32, Path.GetFileName(master));
            }

            // RGB: every Siril output .fit and 32-bit float, calibration as good as with Siril's defaults
            var plan = SeparateRun("gui-style prefs");
            plan.MasterDarkPathTemplate = library.MasterDarkPathTemplate;
            var result = await SirilPreprocessor.RunAsync(plan, gui);
            TestEnv.Print(result.Run);
            result.Succeeded.Should().BeTrue(result.Run?.Summary);
            result.Results.Select(Path.GetFileName).Should().Equal($"result_{LightCount * (int)LightExposure}s.fit");
            Directory.EnumerateFiles(plan.WorkingDirectory, "*.fz", SearchOption.AllDirectories).Should().BeEmpty();
            foreach (var output in new[] {
                    Path.Combine(plan.MastersDirectory, "bias_stacked.fit"),
                    Path.Combine(plan.MastersDirectory, "pp_flat_stacked.fit"),
                    Path.Combine(plan.ProcessDirectory, "pp_light_00001.fit"),
                    Path.Combine(plan.ProcessDirectory, "r_pp_light_00001.fit"),
                    result.Results[0] }) {
                FitsFile.ReadHeader(output).GetInt("BITPIX").Should().Be(-32, $"{Path.GetFileName(output)} keeps negative and fractional values");
            }
            AssertFlatHasNoPedestal(Path.Combine(plan.MastersDirectory, "pp_flat_stacked.fit"), "flats with GUI-style preferences");
            var m = StackMetrics(FitsFile.ReadImage(result.Results[0]), offsets[7], search: 6);
            Report("GUI-style preferences stack", m);
            m.FluxRatio.Should().BeInRange(0.92, 1.08);
            m.CornerOffset.Should().BeInRange(-0.08, 0.08);
            m.LeftRightOffset.Should().BeInRange(-0.08, 0.08);

            // Ha/OIII: the PixelMath OIII result is saved without -32b, so it showed the 16-bit preference
            var haoiii = SeparateRun("gui-style prefs HaOIII");
            haoiii.MasterDarkPathTemplate = library.MasterDarkPathTemplate;
            haoiii.Mode = ProcessingMode.HaOIII;
            var dual = await SirilPreprocessor.RunAsync(haoiii, gui);
            TestEnv.Print(dual.Run);
            dual.Succeeded.Should().BeTrue(dual.Run?.Summary);
            dual.Results.Select(Path.GetFileName).Should().BeEquivalentTo("result_Ha_320s.fit", "result_OIII_320s.fit");
            foreach (var output in dual.Results) {
                FitsFile.ReadHeader(output).GetInt("BITPIX").Should().Be(-32, Path.GetFileName(output));
            }

            // The pins were saved into the runner's own ini only; the seed (standing in for the GUI file) is unchanged
            File.ReadAllText(seed).Should().Be(GuiStyleSirilIni);
            File.ReadAllLines(gui.Options.OwnConfigPath).Should().Contain("force_16bit=false").And.Contain("extension=.fit");
        }

        // ---- helpers -------------------------------------------------------------------------------------------

        private static void CopyDirectory(string from, string to) {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from)) {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }
            foreach (var directory in Directory.GetDirectories(from)) {
                CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
            }
        }

        /// <summary>Super-pixel channel (as <see cref="Plane.SuperPixel"/>) of a model raw frame given per pixel.</summary>
        private Plane SuperPixelModel(int channel, Func<int, int, double> pixel) => new(rig.Width / 2, rig.Height / 2, (x, y) => channel switch {
            0 => pixel(2 * x, 2 * y),
            2 => pixel((2 * x) + 1, (2 * y) + 1),
            _ => 0.5 * (pixel((2 * x) + 1, 2 * y) + pixel(2 * x, (2 * y) + 1)),
        });

        private static double CornerToCentre(Plane p) {
            var bg = Backgrounds(p, halfSize: true);
            return bg.Corners / bg.Center;
        }

        /// <summary>
        /// Per CFA channel (R, G, B) of a master flat: its corner/centre background ratio over the ratio of the rig's
        /// pure vignetting, minus 1. A pedestal left in the flats (no, or a wrong, bias or offset subtracted) moves it
        /// from 0; the stack metrics barely do, because the light calibration divides most of it out again.
        /// </summary>
        private double[] FlatRatioDeviations(string masterFlat) {
            var flat = FitsFile.ReadImage(masterFlat);
            flat.PlaneCount.Should().Be(1, "the master flat is a CFA frame");
            return Enumerable.Range(0, 3)
                .Select(c => (CornerToCentre(Plane.SuperPixel(flat, c)) / CornerToCentre(SuperPixelModel(c, (x, y) => rig.Vignetting(x, y)))) - 1)
                .ToArray();
        }

        /// <summary>What <see cref="FlatRatioDeviations"/> gives for flats stacked raw: vignetted signal + bias + 1 s of dark current.</summary>
        private double[] RawFlatRatioDeviations() {
            return Enumerable.Range(0, 3)
                .Select(c => (CornerToCentre(SuperPixelModel(c, (x, y) => (rig.Vignetting(x, y) * rig.ChannelResponse[SyntheticRig.Channel(x, y)] * rig.FlatLevel) + rig.BiasLevel + rig.DarkCurrent(x, 1.0)))
                    / CornerToCentre(SuperPixelModel(c, (x, y) => rig.Vignetting(x, y)))) - 1)
                .ToArray();
        }

        private void AssertFlatHasNoPedestal(string masterFlat, string what) {
            var deviations = FlatRatioDeviations(masterFlat);
            for (var c = 0; c < 3; c++) {
                TestContext.Progress.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{what} channel {c}: corner/centre ratio off the vignetting by {deviations[c]:P2}"));
                deviations[c].Should().BeInRange(-FlatRatioTolerance, FlatRatioTolerance, $"{what}: channel {c} of the master flat has no pedestal left");
            }
        }

        private SirilPreprocessingPlan SeparateRun(string name) {
            var plan = SirilPreprocessingPlan.ForTarget(target, layout.Library);
            plan.WorkingDirectory = Path.Combine(target.NightDirectory, $"{Target} {name}");
            plan.MastersDirectory = Path.Combine(plan.WorkingDirectory, SessionLayout.MastersFolder);
            plan.ProcessDirectory = Path.Combine(plan.WorkingDirectory, SessionLayout.ProcessFolder);
            Directory.CreateDirectory(plan.WorkingDirectory);
            return plan;
        }

        private TargetFolders WriteLights(string name, double exposure, int gain, int count, int hour) {
            var previous = rig.Gain;
            rig.Gain = gain;
            for (var k = 0; k < count; k++) {
                var info = NinaFrames.Info("LIGHT", Hkt(2026, 10, 3, hour, 0).AddSeconds(exposure * k), k + 1, exposure, name, rig);
                NinaFrames.Save(layout, info, rig, rig.Light(exposure, offsets[k], 7000 + k));
            }
            rig.Gain = previous;
            return layout.GetTargetFolders(new DateOnly(2026, 10, 3), name);
        }

        /// <summary>Stock layout (biases/ flats/ darks/ lights/) filled with copies of the night's frames.</summary>
        private string StockFolder(string name) {
            var dir = Path.Combine(tmp, name);
            void Copy(string from, string to) {
                Directory.CreateDirectory(Path.Combine(dir, to));
                foreach (var f in SessionLayout.ListFitsFrames(from)) {
                    File.Copy(f, Path.Combine(dir, to, Path.GetFileName(f)));
                }
            }
            Copy(target.Biases, "biases");
            Copy(target.Flats, "flats");
            Copy(Path.Combine(layout.Library.DarksDirectory, "20.00s_g252_o50_0.00C_2x2"), "darks");
            Copy(target.Lights, "lights");
            return dir;
        }

        /// <summary>Converts Siril's [0,1] float data back to ADU so levels compare with the synthetic rig.</summary>
        private static FitsImage Adu(FitsImage image) {
            var bitpix = image.Header.GetInt("BITPIX");
            if (bitpix != -32 && bitpix != -64) {
                return image;
            }
            var planes = image.Planes.Select(p => p.Select(v => v * 65535f).ToArray()).ToArray();
            return new FitsImage(image.Header, image.Width, image.Height, planes);
        }

        private readonly record struct Bg(double Center, double Corners, double Left, double Right);

        /// <summary>Background medians: centre (60 px above the centre star), three corners away from the corner star, left and right edges.</summary>
        private static Bg Backgrounds(Plane p, bool halfSize = false) {
            var s = halfSize ? 2 : 1;
            int W = p.Width, H = p.Height, box = 40 / s, edge = 20 / s;
            var center = Measure.BoxMedian(p, (W / 2) - (box / 2), (H / 2) - (60 / s) - (box / 2), box, box);
            var corners = new[] {
                Measure.BoxMedian(p, W - edge - box, edge, box, box),
                Measure.BoxMedian(p, W - edge - box, H - edge - box, box, box),
                Measure.BoxMedian(p, edge, H - edge - box, box, box),
            }.Average();
            var left = Measure.BoxMedian(p, edge, (H / 2) - (box / 2), box, box);
            var right = Measure.BoxMedian(p, W - edge - box, (H / 2) - (box / 2), box, box);
            return new Bg(center, corners, left, right);
        }

        private readonly record struct Metrics(double FluxRatio, double CornerOffset, double LeftRightOffset, double CenterHfr, double CenterFlux);

        /// <summary>
        /// Calibration checks that survive Siril's output normalisation (any a*x+b): flux ratio of two equal stars,
        /// and background differences in units of the centre star's mean flux per aperture pixel.
        /// </summary>
        private Metrics StackMetrics(FitsImage stack, FrameOffset reference, int search) {
            var g = Plane.FromImage(stack, stack.PlaneCount == 3 ? 1 : 0);
            var (cx, cy) = rig.Transform(rig.CenterStar, reference);
            var (kx, ky) = rig.Transform(rig.CornerStar, reference);
            var center = Measure.Star(g, cx, cy, search, ApertureRadius);
            var corner = Measure.Star(g, kx, ky, search, ApertureRadius);
            var perPixel = center.Flux / (Math.PI * ApertureRadius * ApertureRadius);
            var bg = Backgrounds(g);
            return new Metrics(corner.Flux / center.Flux, (bg.Corners - bg.Center) / perPixel, (bg.Right - bg.Left) / perPixel, center.Hfr, center.Flux);
        }

        private static void Report(string what, Metrics m) {
            TestContext.Progress.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{what}: corner/centre star flux {m.FluxRatio:F3}, corner-centre background {m.CornerOffset:F3}, right-left background {m.LeftRightOffset:F3}, centre HFR {m.CenterHfr:F2} px"));
        }

        private static double RobustSigma(Plane p, int x0, int y0, int w, int h) {
            var values = new List<double>();
            for (var y = y0; y < y0 + h; y++) {
                for (var x = x0; x < x0 + w; x++) {
                    values.Add(p.At(x, y));
                }
            }
            var median = Measure.Median(values.ToList());
            var deviations = values.Select(v => Math.Abs(v - median)).ToList();
            return 1.4826 * Measure.Median(deviations);
        }
    }
}
