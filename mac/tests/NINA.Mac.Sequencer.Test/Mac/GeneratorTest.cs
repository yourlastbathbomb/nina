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
using NINA.Mac.Sequencer.Test.Sim;
using NINA.Mac.Sequencing.Planning;
using NINA.Mac.Sequencing.Runner;
using NINA.Sequencer;
using NINA.Sequencer.Interfaces;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// The Target form generator (plan section 5.2): the exact tree for a plan that uses every option and for the defaults, the
    /// policies' variants, and the plans it refuses.
    /// </summary>
    [TestFixture]
    public class GeneratorTest {
        private SimRig rig = null!;

        private static readonly Coordinates M42 = new Coordinates(Angle.ByHours(5.588), Angle.ByDegree(-5.39), Epoch.J2000);
        private static readonly Coordinates M83 = new Coordinates(Angle.ByHours(13.617), Angle.ByDegree(-29.866), Epoch.J2000);

        [OneTimeSetUp]
        public void Create() {
            rig = SimRig.CreateUnconnected("generator");
        }

        [OneTimeTearDown]
        public void Dispose() {
            rig.Host.Dispose();
        }

        [Test]
        public void EveryOption_GivesThePlannedTree() {
            var plan = new NightPlan {
                Name = "Golden",
                CoolToC = -5,
                CoolMinutes = 7,
                WarmMinutes = 8,
                Dawn = DawnStop.Nautical,
                DawnOffsetMinutes = -10,
                EndScript = "/opt/homebrew/bin/siril-cli -s night.ssf",
                Targets = new[] {
                    new TargetPlan { Name = "M 42", Coordinates = M42, ExposureSeconds = 10, Gain = 252, Offset = 15, Count = 120, DitherEvery = 5,
                        RecenterArcmin = 1.5, RecenterEvery = 5, HorizonOffsetDeg = 2, MinAltitudeDeg = 25, MaxAltitudeDeg = 72,
                        Keyhole = KeyholePolicy.WaitUntilBelow, FieldRotation = FieldRotationPolicy.Stop },
                    new TargetPlan { Name = "M 83", Coordinates = M83, ExposureSeconds = 15, Binning = 1 }
                }
            };
            TreeDump.Of(rig.Host.Generator.Generate(plan)).Should().Be(
                "SequenceRootContainer \"Golden\"\n" +
                "  [trigger] ReconnectOnDownloadFailure\n" +
                "  StartAreaContainer \"Start\"\n" +
                "    DewHeater OnOff=True\n" +
                "    CoolCamera Temperature=-5 Duration=7\n" +
                "    UnparkScope\n" +
                "  TargetAreaContainer \"Targets\"\n" +
                "    DeepSkyObjectContainer \"M 42\" target \"M 42\" RA 5.588 h Dec -5.39\n" +
                "      SequentialContainer \"Prepare M 42\"\n" +
                "        [condition] LoopCondition Iterations=1\n" +
                "        [condition] TimeCondition Provider=NauticalDawnProvider MinutesOffset=-10\n" +
                "        WaitUntilAboveHorizon Offset=2\n" +
                "        WaitForAltitude AboveOrBelow=> Offset=25\n" +
                "        WaitForAltitude AboveOrBelow=< Offset=72\n" +
                "        Center Inherited=True\n" +
                "      SequentialContainer \"Imaging M 42 10s\"\n" +
                "        [condition] LoopCondition Iterations=120\n" +
                "        [condition] TimeCondition Provider=NauticalDawnProvider MinutesOffset=-10\n" +
                "        [condition] AboveHorizonCondition Offset=2\n" +
                "        [condition] AltitudeCondition Offset=25\n" +
                "        [condition] LoopWhile Predicate=\"FieldRotation_MaxSubBin1 * 2 >= 10\"\n" +
                "        [trigger] KeyholeTrigger MaxAltitude=72\n" +
                "          WaitForAltitude AboveOrBelow=< Offset=72\n" +
                "          Center Inherited=True\n" +
                "        [trigger] DitherAfterExposures AfterExposures=5\n" +
                "        [trigger] CenterAfterDriftTrigger DistanceArcMinutes=1.5 AfterExposures=5\n" +
                "        TakeExposure ExposureTime=10 Gain=252 Offset=15 Binning=2x2 ImageType=LIGHT\n" +
                "    DeepSkyObjectContainer \"M 83\" target \"M 83\" RA 13.617 h Dec -29.866\n" +
                "      SequentialContainer \"Prepare M 83\"\n" +
                "        [condition] LoopCondition Iterations=1\n" +
                "        [condition] TimeCondition Provider=NauticalDawnProvider MinutesOffset=-10\n" +
                "        [condition] MaxAltitudeCondition MaxAltitude=75\n" +
                "        WaitUntilAboveHorizon Offset=0\n" +
                "        Center Inherited=True\n" +
                "      SequentialContainer \"Imaging M 83 15s\"\n" +
                "        [condition] TimeCondition Provider=NauticalDawnProvider MinutesOffset=-10\n" +
                "        [condition] AboveHorizonCondition Offset=0\n" +
                "        [condition] MaxAltitudeCondition MaxAltitude=75\n" +
                "        [trigger] DitherAfterExposures AfterExposures=5\n" +
                "        [trigger] CenterAfterDriftTrigger DistanceArcMinutes=1.5 AfterExposures=5\n" +
                "        TakeExposure ExposureTime=15 Gain=-1 Offset=-1 Binning=1x1 ImageType=LIGHT\n" +
                "  EndAreaContainer \"End\"\n" +
                "    WarmCamera Duration=8\n" +
                "    DewHeater OnOff=False\n" +
                "    ParkScope\n" +
                "    ExternalScript Script=\"/opt/homebrew/bin/siril-cli -s night.ssf\"\n");
        }

        [Test]
        public void Defaults_AreThePlansRigDefaults() {
            var root = rig.Host.Generator.Generate(new NightPlan { Targets = new[] { new TargetPlan { Name = "Defaults", Coordinates = M83 } } });
            TreeDump.Of(root).Should().Be(
                "SequenceRootContainer \"Night\"\n" +
                "  [trigger] ReconnectOnDownloadFailure\n" +
                "  StartAreaContainer \"Start\"\n" +
                "    DewHeater OnOff=True\n" +
                "    CoolCamera Temperature=0 Duration=5\n" +
                "    UnparkScope\n" +
                "  TargetAreaContainer \"Targets\"\n" +
                "    DeepSkyObjectContainer \"Defaults\" target \"Defaults\" RA 13.617 h Dec -29.866\n" +
                "      SequentialContainer \"Prepare Defaults\"\n" +
                "        [condition] LoopCondition Iterations=1\n" +
                "        [condition] TimeCondition Provider=DawnProvider MinutesOffset=0\n" +
                "        [condition] MaxAltitudeCondition MaxAltitude=75\n" +
                "        WaitUntilAboveHorizon Offset=0\n" +
                "        Center Inherited=True\n" +
                "      SequentialContainer \"Imaging Defaults 10s\"\n" +
                "        [condition] TimeCondition Provider=DawnProvider MinutesOffset=0\n" +
                "        [condition] AboveHorizonCondition Offset=0\n" +
                "        [condition] MaxAltitudeCondition MaxAltitude=75\n" +
                "        [trigger] DitherAfterExposures AfterExposures=5\n" +
                "        [trigger] CenterAfterDriftTrigger DistanceArcMinutes=1.5 AfterExposures=5\n" +
                "        TakeExposure ExposureTime=10 Gain=-1 Offset=-1 Binning=2x2 ImageType=LIGHT\n" +
                "  EndAreaContainer \"End\"\n" +
                "    WarmCamera Duration=5\n" +
                "    DewHeater OnOff=False\n" +
                "    ParkScope\n");
        }

        [Test]
        public void KeyholePolicies_SkipEndsTheBlock_WaitUntilBelowWaitsItOutAndRecentresBeforeTheNextExposure() {
            TargetPlan Target(KeyholePolicy keyhole) => new TargetPlan { Name = "K", Coordinates = M83, Count = 10, MaxAltitudeDeg = 70, Keyhole = keyhole, DitherEvery = 0, RecenterArcmin = 0 };
            var skip = TreeDump.Of(rig.Host.Generator.Generate(new NightPlan { Targets = new[] { Target(KeyholePolicy.Skip) } }));
            skip.Should().Contain(
                "      SequentialContainer \"Imaging K 10s\"\n" +
                "        [condition] LoopCondition Iterations=10\n" +
                "        [condition] TimeCondition Provider=DawnProvider MinutesOffset=0\n" +
                "        [condition] AboveHorizonCondition Offset=0\n" +
                "        [condition] MaxAltitudeCondition MaxAltitude=70\n" +
                "        TakeExposure ExposureTime=10 Gain=-1 Offset=-1 Binning=2x2 ImageType=LIGHT\n");
            skip.Should().NotContain("WaitForAltitude");

            // One loop, one count for both sides of the meridian; no MaxAltitudeCondition, which would end the block. The keyhole
            // trigger waits it out and then centres again, because the mount tracked through the keyhole meanwhile
            var wait = TreeDump.Of(rig.Host.Generator.Generate(new NightPlan { Targets = new[] { Target(KeyholePolicy.WaitUntilBelow) } }));
            wait.Should().Contain(
                "      SequentialContainer \"Prepare K\"\n" +
                "        [condition] LoopCondition Iterations=1\n" +
                "        [condition] TimeCondition Provider=DawnProvider MinutesOffset=0\n" +
                "        WaitUntilAboveHorizon Offset=0\n" +
                "        WaitForAltitude AboveOrBelow=< Offset=70\n" +
                "        Center Inherited=True\n" +
                "      SequentialContainer \"Imaging K 10s\"\n" +
                "        [condition] LoopCondition Iterations=10\n" +
                "        [condition] TimeCondition Provider=DawnProvider MinutesOffset=0\n" +
                "        [condition] AboveHorizonCondition Offset=0\n" +
                "        [trigger] KeyholeTrigger MaxAltitude=70\n" +
                "          WaitForAltitude AboveOrBelow=< Offset=70\n" +
                "          Center Inherited=True\n" +
                "        TakeExposure ExposureTime=10 Gain=-1 Offset=-1 Binning=2x2 ImageType=LIGHT\n");
            wait.Should().NotContain("MaxAltitudeCondition");

            // Not centring: the trigger re-slews instead
            var slew = TreeDump.Of(rig.Host.Generator.Generate(new NightPlan { Targets = new[] { Target(KeyholePolicy.WaitUntilBelow) with { CenterFirst = false } } }));
            slew.Should().Contain(
                "        [trigger] KeyholeTrigger MaxAltitude=70\n" +
                "          WaitForAltitude AboveOrBelow=< Offset=70\n" +
                "          SlewScopeToRaDec\n");
        }

        [Test]
        public void KeyholeTrigger_InstructionsInheritTheTarget_AndTheTriggerFiresOnlyAboveTheLimitBeforeALight() {
            var root = rig.Host.Generator.Generate(new NightPlan { Targets = new[] {
                new TargetPlan { Name = "K", Coordinates = M83, Count = 10, MaxAltitudeDeg = 70, Keyhole = KeyholePolicy.WaitUntilBelow } } });
            var entities = new List<ISequenceEntity>();
            HeadlessSequenceRunner.Walk(root, entities.Add);
            var keyhole = entities.OfType<NINA.Mac.Sequencing.Triggers.KeyholeTrigger>().Single();
            var instructions = keyhole.TriggerRunner.GetItemsSnapshot();
            var wait = instructions.OfType<NINA.Sequencer.SequenceItem.Utility.WaitForAltitude>().Single();
            wait.Inherited.Should().BeTrue("the wait takes the target's coordinates through the trigger's context");
            wait.Data.Coordinates.Coordinates.RA.Should().BeApproximately(M83.RA, 1e-6);
            wait.Data.Coordinates.Coordinates.Dec.Should().BeApproximately(M83.Dec, 1e-6);
            instructions.OfType<NINA.Sequencer.SequenceItem.Platesolving.Center>().Single().Inherited.Should().BeTrue();
            keyhole.Validate();
            keyhole.Issues.Should().NotContain(i => i.Contains("needs a target"));

            var light = entities.OfType<NINA.Sequencer.SequenceItem.Imaging.TakeExposure>().Single();
            var altitude = keyhole.CurrentAltitude();
            double.IsNaN(altitude).Should().BeFalse();
            keyhole.MaxAltitude = altitude + 1;
            keyhole.ShouldTrigger(null, light).Should().BeFalse("the target is below the limit");
            keyhole.MaxAltitude = altitude - 1;
            keyhole.ShouldTrigger(null, light).Should().BeTrue("the target is above the limit");
            keyhole.ShouldTrigger(null, wait).Should().BeFalse("only before a light frame");
            light.ImageType = "DARK";
            keyhole.ShouldTrigger(null, light).Should().BeFalse("only before a light frame");

            var clone = (NINA.Mac.Sequencing.Triggers.KeyholeTrigger)keyhole.Clone();
            clone.MaxAltitude.Should().Be(keyhole.MaxAltitude);
            clone.TriggerRunner.GetItemsSnapshot().Select(i => i.GetType()).Should().Equal(instructions.Select(i => i.GetType()));
            clone.TriggerRunner.Should().NotBeSameAs(keyhole.TriggerRunner);
        }

        [Test]
        public void FieldRotationStop_ScalesTheBin1Limit_ByTheTargetsBlurToleranceAndPlannedBinning() {
            // The bin-1 symbol, never the camera-binning one: Center's solve frames change the camera's binning (review M7-1)
            TargetPlan Target(double blur) => new TargetPlan { Name = "R", Coordinates = M83, ExposureSeconds = 12.5, FieldRotation = FieldRotationPolicy.Stop, BlurTolerancePx = blur };
            TreeDump.Of(rig.Host.Generator.Generate(new NightPlan { Targets = new[] { Target(1) } }))
                .Should().Contain("[condition] LoopWhile Predicate=\"FieldRotation_MaxSubBin1 * 2 >= 12.5\"\n", "1 px at the default bin 2");
            TreeDump.Of(rig.Host.Generator.Generate(new NightPlan { Targets = new[] { Target(2.5) } }))
                .Should().Contain("[condition] LoopWhile Predicate=\"FieldRotation_MaxSubBin1 * 5 >= 12.5\"\n", "2.5 px at bin 2");
            TreeDump.Of(rig.Host.Generator.Generate(new NightPlan { Targets = new[] { Target(1) with { Binning = 1 } } }))
                .Should().Contain("[condition] LoopWhile Predicate=\"FieldRotation_MaxSubBin1 >= 12.5\"\n", "1 px at bin 1");
            TreeDump.Of(rig.Host.Generator.Generate(new NightPlan { Targets = new[] { Target(1.5) with { Binning = 3 } } }))
                .Should().Contain("[condition] LoopWhile Predicate=\"FieldRotation_MaxSubBin1 * 4.5 >= 12.5\"\n");
            TreeDump.Of(rig.Host.Generator.Generate(new NightPlan { Targets = new[] { Target(1) with { FieldRotation = FieldRotationPolicy.Warn } } }))
                .Should().NotContain("LoopWhile", "Warn (decision 4's default) adds nothing to the sequence");
        }

        [Test]
        public void MinimalNight_LeavesOutCoolingDewWarmParkDitherAndDriftChecks() {
            var root = rig.Host.Generator.Generate(new NightPlan {
                CoolToC = null, DewHeater = false, WarmAtEnd = false, ParkAtEnd = false, UnparkAtStart = false, ReconnectOnDownloadFailure = false,
                Dawn = DawnStop.Civil,
                Targets = new[] { new TargetPlan { Name = "T", Coordinates = M42, DitherEvery = 0, RecenterArcmin = 0, CenterFirst = false } }
            });
            var dump = TreeDump.Of(root);
            dump.Should().NotContain("CoolCamera").And.NotContain("DewHeater").And.NotContain("WarmCamera").And.NotContain("ParkScope")
                .And.NotContain("UnparkScope").And.NotContain("Dither").And.NotContain("CenterAfterDrift").And.NotContain("ReconnectOnDownloadFailure")
                .And.NotContain("Center Inherited").And.Contain("CivilDawnProvider");
            dump.Should().Contain("SlewScopeToRaDec");
        }

        [Test]
        public void EveryCoordinatesItem_InheritsTheTarget_AndValidatesWithoutTargetIssues() {
            var root = rig.Host.Generator.Generate(new NightPlan { Targets = new[] { new TargetPlan { Name = "T", Coordinates = M83, MinAltitudeDeg = 20 } } });
            var issues = new HeadlessSequenceRunner(root).Validate();
            TestContext.Out.WriteLine(string.Join("\n", issues));
            issues.Should().NotContain(i => i.Contains(NINA.Core.Locale.Loc.Instance["LblNoTarget"]), "every drift trigger and altitude condition found its target");
            issues.Should().NotContain(i => i.Contains("needs a target"));
        }

        [Test]
        public void NothingGenerated_CanScheduleAMeridianFlip() {
            var root = rig.Host.Generator.Generate(new NightPlan { Targets = new[] { new TargetPlan { Name = "T", Coordinates = M83 } } });
            var entities = new List<ISequenceEntity>();
            HeadlessSequenceRunner.Walk(root, entities.Add);
            entities.Should().NotContain(e => e is IMeridianFlipTrigger);
        }

        [Test]
        public void InvalidPlans_AreRefused() {
            var generator = rig.Host.Generator;
            var ok = new TargetPlan { Name = "T", Coordinates = M83 };
            generator.Invoking(g => g.Generate(new NightPlan())).Should().Throw<ArgumentException>().WithMessage("*no target*");
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { Name = " " } } })).Should().Throw<ArgumentException>();
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { Coordinates = null! } } })).Should().Throw<ArgumentException>();
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { ExposureSeconds = 0 } } })).Should().Throw<ArgumentException>();
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { Count = 0 } } })).Should().Throw<ArgumentException>();
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { MaxAltitudeDeg = 91 } } })).Should().Throw<ArgumentException>();
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { DitherEvery = -1 } } })).Should().Throw<ArgumentException>();
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { BlurTolerancePx = 0 } } })).Should().Throw<ArgumentException>();
            // Contradictory altitude limits would never image (review M7-6)
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { MinAltitudeDeg = 80, MaxAltitudeDeg = 70 } } }))
                .Should().Throw<ArgumentException>().WithMessage("*minimum altitude (80°) must be below the maximum altitude (70°)*");
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { MinAltitudeDeg = 75 } } }))
                .Should().Throw<ArgumentException>("equal limits leave no altitude to image at");
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { MinAltitudeDeg = -91 } } })).Should().Throw<ArgumentException>();
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { MinAltitudeDeg = double.NaN } } })).Should().Throw<ArgumentException>();
            generator.Invoking(g => g.Generate(new NightPlan { Targets = new[] { ok with { MinAltitudeDeg = 74.9 } } })).Should().NotThrow();
        }
    }
}
