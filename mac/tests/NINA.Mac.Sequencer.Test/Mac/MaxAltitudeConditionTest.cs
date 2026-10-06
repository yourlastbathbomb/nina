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
using Moq;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Mac.Sequencer.Test.Sim;
using NINA.Mac.Sequencing.Conditions;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// MaxAltitudeCondition at fixed coordinates: it holds while the target is at or below the limit and fails above it, rising
    /// or setting alike; it takes the target from its deep-sky object container and reports a validation issue without one; it
    /// clones and serialises its limit. The interruption of a running exposure is shown in SimulatedNightTest.
    /// </summary>
    [TestFixture]
    public class MaxAltitudeConditionTest {

        private static IProfileService Profile() {
            var profile = new NINA.Profile.Profile("max altitude");
            profile.AstrometrySettings.Latitude = Sky.Latitude;
            profile.AstrometrySettings.Longitude = Sky.Longitude;
            var service = new Mock<IProfileService>();
            service.SetupGet(x => x.ActiveProfile).Returns(profile);
            return service.Object;
        }

        private static (MaxAltitudeCondition Condition, DeepSkyObjectContainer Target) InTarget(IProfileService profile, Coordinates coordinates, double max) {
            var dso = new DeepSkyObjectContainer(profile, new FixedNighttimeCalculator(DateTime.Now.AddHours(2)), null, null, null, null, null, null);
            dso.Target = new InputTarget(Angle.ByDegree(Sky.Latitude), Angle.ByDegree(Sky.Longitude), null) { TargetName = "T" };
            dso.Target.InputCoordinates.Coordinates = coordinates;
            var block = new SequentialContainer();
            dso.Add(block);
            var condition = new MaxAltitudeCondition(profile) { MaxAltitude = max };
            block.Add(condition);
            return (condition, dso);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void HoldsAtOrBelowTheLimit_FailsAboveIt_RisingOrSetting(bool rising) {
            var profile = Profile();
            var coordinates = Sky.TargetAt(60, rising, decDeg: 10, DateTime.Now);
            InTarget(profile, coordinates, 65).Condition.Check(null, null).Should().BeTrue();
            InTarget(profile, coordinates, 61).Condition.Check(null, null).Should().BeTrue();
            var below = InTarget(profile, coordinates, 55).Condition;
            below.Check(null, null).Should().BeFalse("there is no rising or setting exemption: the keyhole is unsafe both ways");
            below.Data.CurrentAltitude.Should().BeApproximately(60, 0.05);
        }

        [Test]
        public void TakesTheTargetFromItsDeepSkyObjectContainer() {
            var profile = Profile();
            var coordinates = Sky.TargetAt(40, rising: true, decDeg: 0, DateTime.Now);
            var (condition, dso) = InTarget(profile, coordinates, 75);
            condition.HasDsoParent.Should().BeTrue();
            condition.Data.Coordinates.Coordinates.RA.Should().BeApproximately(coordinates.RA, 1e-9);
            condition.Validate().Should().BeTrue();

            // Moving the target moves the condition's coordinates (DeepSkyObjectContainer follows InputTarget.CoordinatesChanged)
            var high = Sky.TargetAt(80, rising: true, decDeg: 20, DateTime.Now);
            dso.Target.InputCoordinates.Coordinates = high;
            condition.Check(null, null).Should().BeFalse();
            condition.Data.Coordinates.Coordinates.Dec.Should().BeApproximately(high.Dec, 1e-6);
        }

        [Test]
        public void WithoutATarget_NeverStops_AndReportsAValidationIssue() {
            var condition = new MaxAltitudeCondition(Profile());
            var block = new SequentialContainer();
            block.Add(condition);
            condition.HasDsoParent.Should().BeFalse();
            condition.Check(null, null).Should().BeTrue();
            condition.Validate().Should().BeFalse();
            condition.Issues.Should().ContainSingle(i => i.Contains("needs a target"));
        }

        [Test]
        public void DefaultsToDecisionFivesFixed75Degrees_AndComparesGreaterThanForItsExpectedTime() {
            var condition = new MaxAltitudeCondition(Profile());
            condition.MaxAltitude.Should().Be(75);
            condition.Data.Offset.Should().Be(75);
            condition.Data.Comparator.Should().Be(ComparisonOperatorEnum.GREATER_THAN);
            condition.Validate();
            condition.MaxAltitudeExpression.Error.Should().BeNull();
            condition.MaxAltitude = 95;
            condition.Validate().Should().BeFalse("the limit is an altitude, 0 to 90 degrees");
        }

        [Test]
        public void ExpectedTime_IsNowAboveTheLimit_AndLaterWhileClimbingTowardsIt() {
            var profile = Profile();
            var coordinates = Sky.TargetAt(50, rising: true, decDeg: 10, DateTime.Now);
            var above = InTarget(profile, coordinates, 45).Condition;
            above.CalculateExpectedTime();
            above.Data.ExpectedTime.Should().Be(NINA.Core.Locale.Loc.Instance["LblNow"]);
            var climbing = InTarget(profile, coordinates, 70).Condition;
            climbing.CalculateExpectedTime();
            climbing.Data.ExpectedTime.Should().NotBe(NINA.Core.Locale.Loc.Instance["LblNow"]);
            climbing.Data.ExpectedDateTime.Should().BeAfter(DateTime.Now).And.BeBefore(DateTime.Now.AddHours(4));
        }

        [Test]
        public void Clone_KeepsTheLimitAndData_ButNotTheParent() {
            var profile = Profile();
            var (condition, _) = InTarget(profile, Sky.TargetAt(40, true, 0, DateTime.Now), 68.5);
            var clone = (MaxAltitudeCondition)condition.Clone();
            clone.MaxAltitude.Should().Be(68.5);
            clone.Data.Should().NotBeSameAs(condition.Data);
            clone.Data.Offset.Should().Be(68.5);
            clone.Parent.Should().BeNull();
        }
    }
}
