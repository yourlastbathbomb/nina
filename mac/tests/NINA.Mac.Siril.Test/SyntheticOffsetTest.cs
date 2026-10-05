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
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.Siril.Test {

    /// <summary>The synthetic-offset flat step (calibrate flat "-bias==N*$OFFSET") run by the real siril-cli 1.4.4.</summary>
    [TestFixture]
    public class SyntheticOffsetTest {
        private string root;
        private SessionLayout layout;
        private SyntheticRig rig;
        private TargetFolders target;

        [SetUp]
        public void SetUp() {
            TestEnv.RequireSiril();
            root = TestEnv.NewTempDirectory("offset");
            layout = new SessionLayout(new SessionLayoutOptions { Root = Path.Combine(root, "Astro NINA") });
            rig = new SyntheticRig(64, 48);
            target = null;
            for (var k = 0; k < 4; k++) {
                var info = NinaFrames.Info("FLAT", new DateTime(2026, 10, 3, 20, 40, 5 * k, DateTimeKind.Utc), k + 1, 1.0, "NGC 253", rig);
                NinaFrames.Save(layout, info, rig, rig.Flat(100 + k));
                target ??= layout.GetTargetFolders(info);
            }
            SessionLayout.ListFitsFrames(target.Flats).Should().HaveCount(4);
        }

        [TearDown]
        public void TearDown() => TestEnv.Delete(root);

        /// <summary>The generated script up to the master flat (the light steps need a full night), then close.</summary>
        private string FlatStepScript(int multiplier) {
            var plan = SirilPreprocessingPlan.ForTarget(target, layout.Library);
            plan.FlatCalibration = FlatCalibration.SyntheticOffset;
            plan.SyntheticOffsetMultiplier = multiplier;
            plan.LightCount = 3;
            var lines = SirilScriptGenerator.Generate(plan).Text.Split('\n').ToList();
            var stackFlat = lines.FindIndex(l => l.StartsWith("stack pp_flat ", StringComparison.Ordinal));
            stackFlat.Should().BePositive();
            return string.Join("\n", lines.Take(stackFlat + 1)) + "\nclose\n";
        }

        private string Save(string name, string text) {
            var path = Path.Combine(target.WorkingDirectory, name);
            File.WriteAllText(path, text);
            return path;
        }

        [Test]
        public async Task GeneratedFlatStep_RunsInSiril_WithAWholeMultiplierOtherThanTheRigs16() {
            var script = FlatStepScript(17);
            script.Should().Contain("calibrate flat -bias==17*$OFFSET");

            var run = await TestEnv.Runner(root).RunAsync(Save("flat17.ssf", script), target.WorkingDirectory);
            TestEnv.Print(run);

            run.Succeeded.Should().BeTrue(run.Summary);
            run.LogLines.Should().Contain(l => l.Contains($"Synthetic offset: Level = {17 * rig.Offset}"));
            File.Exists(Path.Combine(target.Masters, "pp_flat_stacked.fit")).Should().BeTrue();
        }

        [Test]
        public async Task SirilItself_RejectsAFractionalMultiplier_SoThePlanOnlyTakesWholeNumbers() {
            // Why SyntheticOffsetMultiplier is an int: observed siril-cli 1.4.4 behaviour (16.5, 15.75 and 16.0 all fail)
            typeof(SirilPreprocessingPlan).GetProperty(nameof(SirilPreprocessingPlan.SyntheticOffsetMultiplier)).PropertyType.Should().Be(typeof(int?));
            var script = FlatStepScript(16).Replace("-bias==16*$OFFSET", "-bias==16.5*$OFFSET", StringComparison.Ordinal);
            script.Should().Contain("-bias==16.5*$OFFSET");

            var run = await TestEnv.Runner(root).RunAsync(Save("flat16.5.ssf", script), target.WorkingDirectory);
            TestEnv.Print(run);

            run.Succeeded.Should().BeFalse();
            run.FailedCommand.Should().Be("calibrate");
            run.LogLines.Should().Contain(l => l.Contains("The offset value could not be parsed from expression: 16.5*$OFFSET"));
            File.Exists(Path.Combine(target.Masters, "pp_flat_stacked.fit")).Should().BeFalse();
        }
    }
}
