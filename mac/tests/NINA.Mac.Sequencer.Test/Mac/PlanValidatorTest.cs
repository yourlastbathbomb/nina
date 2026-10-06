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

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// The plan validator's warnings (plan section 5.3): the zenith keyhole, field rotation beyond the 1 px limit, targets that are
    /// not observable, and targets an earlier open-ended target never lets through. The night is made to run from now for six
    /// hours so the expectations do not depend on the date.
    /// </summary>
    [TestFixture]
    public class PlanValidatorTest {

        private static PlanReport Validate(params TargetPlan[] targets) => Validate(null, targets);

        private static PlanReport Validate(Action<NINA.Profile.Profile>? configure, params TargetPlan[] targets) {
            var now = DateTime.Now;
            var rig = SimRig.CreateUnconnected("validator", nighttime: new FixedNighttimeCalculator(now.AddHours(6), dusk: now), configure: configure);
            try {
                var report = rig.Host.Validator.Validate(new NightPlan { Targets = targets });
                foreach (var issue in report.Issues) {
                    TestContext.Out.WriteLine(issue);
                }
                return report;
            } finally {
                rig.Host.Dispose();
            }
        }

        [Test]
        public void ATargetPassingNearTheZenith_WarnsOfTheKeyhole() {
            // Dec +20 transits at 87.75 degrees; transit about two hours from now
            var target = Sky.TargetAt(50, rising: true, decDeg: 20, DateTime.Now);
            var report = Validate(new TargetPlan { Name = "Zenith", Coordinates = target, ExposureSeconds = 10 });
            report.HasErrors.Should().BeFalse();
            var window = report.Windows.Single();
            window.KeyholeStart.Should().NotBeNull();
            window.KeyholeEnd.Should().BeAfter(window.KeyholeStart!.Value);
            window.PeakAltitudeDeg.Should().BeApproximately(87.75, 0.3);
            report.Issues.Should().Contain(i => i.Target == "Zenith" && i.Message.Contains("zenith keyhole"));
        }

        [Test]
        public void TenSecondSubsAtDec0_ExceedTheOnePixelLimitAroundTransit() {
            // Dec 0 transits due south at 67.75 degrees, where the plan's table allows 5.1 s (bin 2, 1 px corner blur)
            var target = Sky.TargetAt(40, rising: true, decDeg: 0, DateTime.Now);
            var report = Validate(new TargetPlan { Name = "Rotation", Coordinates = target, ExposureSeconds = 10 });
            var window = report.Windows.Single();
            window.RotationLimitedStart.Should().NotBeNull();
            window.RotationLimitedEnd.Should().BeAfter(window.RotationLimitedStart!.Value);
            window.ShortestMaxSubSeconds.Should().BeApproximately(5.1, 0.1);
            window.PeakTime.Should().BeAfter(window.RotationLimitedStart.Value).And.BeBefore(window.RotationLimitedEnd!.Value);
            report.Issues.Should().ContainSingle(i => i.Target == "Rotation" && i.Message.Contains("field-rotation limit"));
        }

        [Test]
        public void ShortSubsLowInTheSouth_GiveNoWarning() {
            var target = Sky.TargetAt(30, rising: true, decDeg: -30, DateTime.Now);
            var report = Validate(new TargetPlan { Name = "Low", Coordinates = target, ExposureSeconds = 5 });
            report.Issues.Should().BeEmpty();
            report.Windows.Single().Start.Should().NotBeNull();
        }

        [Test]
        public void ATargetThatNeverRises_IsReportedAsNotObservable() {
            var report = Validate(new TargetPlan { Name = "South pole", Coordinates = new Coordinates(Angle.ByHours(0), Angle.ByDegree(-80), Epoch.J2000) });
            report.Windows.Single().Start.Should().BeNull();
            report.Issues.Should().ContainSingle(i => i.Target == "South pole" && i.Message.StartsWith("Not observable tonight"));
        }

        [Test]
        public void ATargetBehindAnOpenEndedOne_ThatSetsFirst_IsNeverReached() {
            var now = DateTime.Now;
            var long_ = Sky.TargetAt(20, rising: true, decDeg: 0, now);
            var setting = Sky.TargetAt(40, rising: false, decDeg: 0, now);
            var report = Validate(
                new TargetPlan { Name = "Open ended", Coordinates = long_, ExposureSeconds = 5 },
                new TargetPlan { Name = "Setting", Coordinates = setting, ExposureSeconds = 5 });
            report.Issues.Should().Contain(i => i.Target == "Setting" && i.Message.StartsWith("Never reached"));
            var counted = Validate(
                new TargetPlan { Name = "Counted", Coordinates = long_, ExposureSeconds = 5, Count = 10 },
                new TargetPlan { Name = "Setting", Coordinates = setting, ExposureSeconds = 5 });
            counted.Issues.Should().NotContain(i => i.Message.StartsWith("Never reached"));
        }

        /// <summary>The rig's real case: the northern sky is behind a high custom horizon (80° from azimuth 271 through north to 89, 10° south).</summary>
        private static void NorthBlocked(NINA.Profile.Profile p) {
            using var reader = new StringReader("0 80\n89 80\n90 10\n270 10\n271 80\n359 80\n");
            p.AstrometrySettings.Horizon = NINA.Core.Model.CustomHorizon.FromReader_Standard(reader);
        }

        [Test]
        public void ATargetBehindTheCustomHorizon_ThatNinasValidationPasses_HoldsTheNight_AndTheTargetsAfterItAreNeverReached() {
            // Review M7-2. Dec +60 stays within 33° of north and peaks at 52°, below the 80° northern horizon, but above the horizon's
            // lowest point (10°), so NINA's WaitUntilAboveHorizon.Validate passes and the wait runs until dawn
            var now = DateTime.Now;
            var north = Sky.TargetAt(20, rising: true, decDeg: 60, now);
            var south = Sky.TargetAt(45, rising: true, decDeg: -10, now);
            var report = Validate(NorthBlocked,
                new TargetPlan { Name = "North", Coordinates = north, ExposureSeconds = 5 },
                new TargetPlan { Name = "South", Coordinates = south, ExposureSeconds = 5, Count = 20 });
            report.Issues.Should().Contain(i => i.Target == "North" && i.Message.StartsWith("Not observable tonight"));
            report.Issues.Should().Contain(i => i.Target == "North" && i.Message.StartsWith("Holds the sequence until dawn") && i.Message.Contains("rise above its horizon"));
            report.Issues.Should().Contain(i => i.Target == "South" && i.Message.StartsWith("Never reached") && i.Message.Contains("North waits until dawn"));
            var southWindow = report.Windows.Single(w => w.Target == "South");
            southWindow.Start.Should().NotBeNull("South is observable tonight");
            southWindow.ExpectedStart.Should().BeNull("but the run never gets to it");
            southWindow.ExpectedStop.Should().Be(ExpectedStop.NotImaged);

            // In the other order South is imaged first; North then holds only the end of the night, which the warning still says
            var reordered = Validate(NorthBlocked,
                new TargetPlan { Name = "South", Coordinates = south, ExposureSeconds = 5, Count = 20 },
                new TargetPlan { Name = "North", Coordinates = north, ExposureSeconds = 5 });
            reordered.Issues.Should().NotContain(i => i.Target == "South");
            var imaged = reordered.Windows.Single(w => w.Target == "South");
            imaged.ExpectedStart.Should().NotBeNull();
            imaged.ExpectedStop.Should().Be(ExpectedStop.Count);
            (imaged.ExpectedEnd!.Value - imaged.ExpectedStart!.Value).TotalSeconds.Should().BeApproximately(100, 1, "20 x 5 s, without overheads");
            reordered.Issues.Should().Contain(i => i.Target == "North" && i.Message.StartsWith("Holds the sequence until dawn"));
        }

        [Test]
        public void ATargetThatNeverRisesGeometrically_IsSkippedByNinasValidation_AndDoesNotHoldTheNight() {
            // Dec -80 never rises at 22.25 N: NINA's validation skips its wait, so the next target is imaged (the reviewer's probe: 5 lights)
            var now = DateTime.Now;
            var south = Sky.TargetAt(45, rising: true, decDeg: -10, now);
            var report = Validate(
                new TargetPlan { Name = "Pole", Coordinates = new Coordinates(Angle.ByHours(0), Angle.ByDegree(-80), Epoch.J2000) },
                new TargetPlan { Name = "South", Coordinates = south, ExposureSeconds = 5, Count = 20 });
            report.Issues.Should().NotContain(i => i.Message.StartsWith("Holds the sequence"));
            report.Issues.Should().NotContain(i => i.Target == "South");
            report.Windows.Single(w => w.Target == "South").ExpectedStart.Should().NotBeNull();
        }

        [Test]
        public void ASkipTarget_EndsAtTheKeyhole_SoTheTargetAfterItIsReached() {
            // Review M7-5: an open-ended Skip target images only until it climbs into the keyhole, not until its window (which
            // includes the west side of the keyhole) ends; the next target is reached then
            var now = DateTime.Now;
            var climbing = Sky.TargetAt(60, rising: true, decDeg: 10, now);     // transits at 77.75°, above the 75° limit
            var setting = Sky.TargetAt(50, rising: false, decDeg: 0, now);      // sets in about 3.5 h
            var report = Validate(
                new TargetPlan { Name = "Climbing", Coordinates = climbing, ExposureSeconds = 2 },
                new TargetPlan { Name = "Setting", Coordinates = setting, ExposureSeconds = 2 });
            var a = report.Windows.Single(w => w.Target == "Climbing");
            var b = report.Windows.Single(w => w.Target == "Setting");
            a.KeyholeStart.Should().NotBeNull();
            a.End.Should().BeAfter(b.End!.Value, "the static window runs on past the keyhole, west of the meridian");
            a.ExpectedStop.Should().Be(ExpectedStop.Keyhole);
            a.ExpectedEnd.Should().BeCloseTo(a.KeyholeStart!.Value, TimeSpan.FromMinutes(1));
            b.ExpectedStart.Should().BeCloseTo(a.ExpectedEnd!.Value, TimeSpan.FromMinutes(1));
            b.ExpectedStop.Should().Be(ExpectedStop.Horizon);
            report.Issues.Should().NotContain(i => i.Message.StartsWith("Never reached"));
        }

        [Test]
        public void ASkipTarget_AlreadyInTheKeyholeAtItsTurn_IsReportedAsSkipped() {
            var now = DateTime.Now;
            var transiting = Sky.TransitingAt(10, now.AddMinutes(5));          // 77.75° for the next half hour or so
            var report = Validate(new TargetPlan { Name = "Zenith", Coordinates = transiting, ExposureSeconds = 2, Count = 10 });
            report.Issues.Should().Contain(i => i.Target == "Zenith" && i.Message.StartsWith("Skipped: it is above the 75° maximum altitude at its turn"));
            report.Windows.Single().ExpectedStart.Should().BeNull();
        }

        [Test]
        public void WaitUntilBelowWithFieldRotationStop_SaysTheBlockEndsBeforeTheKeyhole_AndDoesNotResume() {
            // Review M7-5: the keyhole message said "imaging pauses until it has sunk below again", but the rotation Stop ends the block
            // near transit, before the keyhole is over, and nothing resumes it
            var now = DateTime.Now;
            var climbing = Sky.TargetAt(60, rising: true, decDeg: 10, now);
            var report = Validate(new TargetPlan {
                Name = "Both", Coordinates = climbing, ExposureSeconds = 10, Keyhole = KeyholePolicy.WaitUntilBelow, FieldRotation = FieldRotationPolicy.Stop });
            var w = report.Windows.Single();
            w.ExpectedStop.Should().Be(ExpectedStop.FieldRotation);
            w.ExpectedEnd.Should().BeBefore(w.KeyholeEnd!.Value);
            report.Issues.Should().Contain(i => i.Message.StartsWith("Above the 75° maximum altitude (zenith keyhole)") && i.Message.Contains("does not resume"));
            report.Issues.Should().NotContain(i => i.Message.Contains("imaging pauses"));

            var warnOnly = Validate(new TargetPlan {
                Name = "Wait", Coordinates = climbing, ExposureSeconds = 10, Keyhole = KeyholePolicy.WaitUntilBelow });
            warnOnly.Issues.Should().Contain(i => i.Message.StartsWith("Above the 75° maximum altitude (zenith keyhole)") && i.Message.Contains("imaging pauses until it has sunk below again"));
            warnOnly.Windows.Single().ExpectedStop.Should().NotBe(ExpectedStop.Keyhole, "WaitUntilBelow does not end the block at the keyhole");
        }
    }
}
