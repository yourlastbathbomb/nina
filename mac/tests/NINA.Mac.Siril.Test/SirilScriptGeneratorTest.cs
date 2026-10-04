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
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace NINA.Mac.Siril.Test {

    [TestFixture]
    public class SirilScriptGeneratorTest {
        private const string Root = "/Users/someone/Astro NINA";
        private SessionLayout layout;
        private TargetFolders target;

        [SetUp]
        public void SetUp() {
            layout = new SessionLayout(new SessionLayoutOptions { Root = Root });
            target = layout.GetTargetFolders(new DateOnly(2026, 10, 3), "NGC 253");
        }

        private SirilPreprocessingPlan Plan() {
            var plan = SirilPreprocessingPlan.ForTarget(target, layout.Library);
            plan.LightCount = 16;
            return plan;
        }

        [TestCase("abc", "abc")]
        [TestCase("-out=../process", "-out=../process")]
        [TestCase("-out=../proc ess", "\"-out=../proc ess\"")]
        [TestCase("Thor's Helmet", "\"Thor's Helmet\"")]
        [TestCase("say \"hi\" now", "'say \"hi\" now'")]
        [TestCase("'x", "\"'x\"")]
        [TestCase("a'b", "a'b")]
        public void Quote_WholeWordsOnlyWhenNeeded(string word, string expected) {
            SirilQuote.Word(word).Should().Be(expected);
        }

        [TestCase("both ' and \" quotes")]
        [TestCase("line\nbreak")]
        [TestCase("")]
        public void Quote_RejectsWordsSirilCannotRead(string word) {
            FluentActions.Invoking(() => SirilQuote.Word(word)).Should().Throw<ArgumentException>();
        }

        [Test]
        public void DefaultPlan_IsOscPreprocessingWithLibraryDarkAndMiddleReference() {
            var script = SirilScriptGenerator.Generate(Plan());
            var commands = TestEnv.Commands(script.Text);

            commands.Should().Equal(
                "requires 1.3.4",
                "setext fit",
                $"cd \"{Root}/2026-10-03/NGC 253\"",
                "cd biases",
                "convert bias -out=../process",
                "cd ../process",
                "stack bias rej 3 3 -nonorm -out=../masters/bias_stacked",
                "cd ..",
                "cd flats",
                "convert flat -out=../process",
                "cd ../process",
                "calibrate flat -bias=../masters/bias_stacked",
                "stack pp_flat rej 3 3 -norm=mul -out=../masters/pp_flat_stacked",
                "cd ..",
                "cd lights",
                "convert light -out=../process",
                "cd ../process",
                $"calibrate light \"-dark={Root}/library/masters/dark_$EXPTIME:%d$s_G$GAIN:%d$_O$OFFSET:%d$_T$SET-TEMP:%d$_B$XBINNING:%d$.fit\" -flat=../masters/pp_flat_stacked -cc=dark -cfa -equalize_cfa -debayer",
                "setref pp_light 8",
                "register pp_light",
                "stack r_pp_light rej 3 3 -norm=addscale -output_norm -rgb_equal -32b -out=result",
                "load result",
                "mirrorx -bottomup",
                "save ../result_$LIVETIME:%d$s",
                "cd ..",
                "close");
            script.FileName.Should().Be("nina_siril.ssf");
            script.WorkingDirectory.Should().Be($"{Root}/2026-10-03/NGC 253");
            script.Text.Should().NotContain("\r");
        }

        [Test]
        public void StockFolderPlan_HasExactlyTheStockOscPreprocessingCommands() {
            var stockPath = Path.Combine(TestEnv.StockScripts, "OSC_Preprocessing.ssf");
            if (!File.Exists(stockPath)) {
                Assert.Ignore("Siril's stock scripts are not installed");
            }
            var plan = SirilPreprocessingPlan.ForStockFolder("/tmp/stock run");
            plan.LightCount = 5;

            var generated = TestEnv.Commands(SirilScriptGenerator.Generate(plan).Text);
            var stock = TestEnv.Commands(File.ReadAllText(stockPath));

            generated[0].Should().Be(stock[0], "both start with requires");
            generated.Skip(1).Take(2).Should().Equal("setext fit", "cd \"/tmp/stock run\"");
            generated.Skip(3).Should().Equal(stock.Skip(1), "after pinning the extension and the folder, the commands are the stock ones");
        }

        [Test]
        public void HaOIIIFolderPlan_HasTheStockExtractHaOIIICommands_SavingNextToTheRgbResult() {
            var stockPath = Path.Combine(TestEnv.StockScripts, "OSC_Extract_HaOIII.ssf");
            if (!File.Exists(stockPath)) {
                Assert.Ignore("Siril's stock scripts are not installed");
            }
            var plan = SirilPreprocessingPlan.ForStockFolder("/tmp/stock");
            plan.Mode = ProcessingMode.HaOIII;
            plan.LightCount = 5;

            var generated = TestEnv.Commands(SirilScriptGenerator.Generate(plan).Text);
            var expected = TestEnv.Commands(File.ReadAllText(stockPath))
                .Skip(1)
                .Select(c => c.Replace("save ../results/", "save ../", StringComparison.Ordinal))
                .ToList();
            expected[^1].Should().Be("close");
            expected.Insert(expected.Count - 1, "cd .."); // the generator leaves process/ before closing, like OSC_Preprocessing

            generated.Skip(3).Should().Equal(expected);
        }

        [Test]
        public void SyntheticOffset_CalibratesFlatsWithoutBiases() {
            var plan = Plan();
            plan.FlatCalibration = FlatCalibration.SyntheticOffset;
            plan.SyntheticOffsetMultiplier = 16;

            var commands = TestEnv.Commands(SirilScriptGenerator.Generate(plan).Text);

            commands.Should().Contain("calibrate flat -bias==16*$OFFSET");
            commands.Should().NotContain(c => c.StartsWith("convert bias", StringComparison.Ordinal));
        }

        [TestCase(Framing.Min, "")]
        [TestCase(Framing.Max, " -maximize")]
        public void TwoPass_AppliesRegistrationWithFraming(Framing framing, string stackExtra) {
            var plan = Plan();
            plan.Registration = RegistrationReference.TwoPass;
            plan.Framing = framing;

            var commands = TestEnv.Commands(SirilScriptGenerator.Generate(plan).Text);

            commands.Should().ContainInOrder("register pp_light -2pass", $"seqapplyreg pp_light -framing={framing.ToString().ToLowerInvariant()}",
                $"stack r_pp_light rej 3 3 -norm=addscale -output_norm -rgb_equal -32b{stackExtra} -out=result");
        }

        [Test]
        public void WithoutDark_LightsGetTheBias_LikeStockWithoutDark() {
            var plan = Plan();
            plan.Dark = DarkSource.None;

            var commands = TestEnv.Commands(SirilScriptGenerator.Generate(plan).Text);

            commands.Should().Contain("calibrate light -bias=../masters/bias_stacked -flat=../masters/pp_flat_stacked -cfa -equalize_cfa -debayer");
        }

        [Test]
        public void NoCalibration_IsStockWithoutDbf() {
            var plan = Plan();
            plan.Dark = DarkSource.None;
            plan.Flat = FlatSource.None;
            plan.FlatCalibration = FlatCalibration.None;

            var commands = TestEnv.Commands(SirilScriptGenerator.Generate(plan).Text);

            commands.Should().Contain("calibrate light -debayer");
            commands.Should().NotContain(c => c.StartsWith("cd flats", StringComparison.Ordinal) || c.StartsWith("cd biases", StringComparison.Ordinal));
        }

        [Test]
        public void NightLevelFlats_AreReachedWithRelativePaths() {
            var plan = Plan();
            plan.FlatsDirectory = target.NightFlats;

            var commands = TestEnv.Commands(SirilScriptGenerator.Generate(plan).Text);

            commands.Should().ContainInOrder("cd ../flats", "convert flat \"-out=../NGC 253/process\"", "cd \"../NGC 253/process\"");
        }

        [Test]
        public void InvalidPlans_Throw() {
            var offset = Plan();
            offset.FlatCalibration = FlatCalibration.SyntheticOffset;
            FluentActions.Invoking(() => SirilScriptGenerator.Generate(offset)).Should().Throw<ArgumentException>();

            var haTwoPass = Plan();
            haTwoPass.Mode = ProcessingMode.HaOIII;
            haTwoPass.Registration = RegistrationReference.TwoPass;
            FluentActions.Invoking(() => SirilScriptGenerator.Generate(haTwoPass)).Should().Throw<NotSupportedException>();

            var noLights = Plan();
            noLights.LightCount = 0;
            FluentActions.Invoking(() => SirilScriptGenerator.Generate(noLights)).Should().Throw<InvalidOperationException>();

            FluentActions.Invoking(() => new DarkLibrary("/Users/x/$HOME/library")).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => new DarkLibrary("/Users/x/library", "dark_$EXPTIME:%d$.fits")).Should().Throw<ArgumentException>();
        }
    }
}
