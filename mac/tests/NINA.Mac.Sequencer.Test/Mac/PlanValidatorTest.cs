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

        private static PlanReport Validate(params TargetPlan[] targets) {
            var now = DateTime.Now;
            var rig = SimRig.CreateUnconnected("validator", nighttime: new FixedNighttimeCalculator(now.AddHours(6), dusk: now));
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
    }
}
