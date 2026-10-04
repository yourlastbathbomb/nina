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
using NINA.Mac.RigTools.Astronomy;
using NINA.Mac.RigTools.Horizon;
using NINA.Mac.RigTools.Optics;
using NINA.Mac.RigTools.Planning;
using NINA.Mac.RigTools.Rotation;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace NINA.Mac.RigTools.Test {

    [TestFixture]
    public class NightPlannerTest {
        private static readonly DateOnly Tonight = new DateOnly(2026, 10, 4);
        private static readonly PlanTarget Polaris = new PlanTarget("Polaris", 2 + 31 / 60.0 + 49.09 / 3600.0, 89 + 15 / 60.0 + 50.8 / 3600.0);
        private static readonly PlanTarget Smc = new PlanTarget("SMC", 0 + 52 / 60.0 + 44.8 / 3600.0, -(72 + 49 / 60.0 + 43 / 3600.0));
        private static readonly PlanTarget M1 = new PlanTarget("M1", 5 + 34 / 60.0 + 31.94 / 3600.0, 22 + 0 / 60.0 + 52.2 / 3600.0);

        private static TargetPlan PlanOne(PlanTarget target, DateOnly? date = null, NightPlanOptions options = null) {
            var plan = NightPlanner.Plan(date ?? Tonight, new[] { target }, options);
            return plan.Targets.Single();
        }

        private static void AssertWindowsAreConsistent(NightPlan plan, TargetPlan t) {
            var o = plan.Options;
            foreach (var w in t.Windows) {
                w.Start.Should().BeOnOrAfter(plan.Dusk.Value);
                w.End.Should().BeOnOrBefore(plan.Dawn.Value);
                w.End.Should().BeOnOrAfter(w.Start);
                // Every minute inside the window is usable and on the window's side of the meridian.
                for (var time = w.Start; time <= w.End; time = time.AddMinutes(1)) {
                    var ofDate = t.OfDate;
                    var ha = o.Site.HourAngleHours(ofDate, time);
                    var hz = o.Site.ToHorizontal(ofDate, time);
                    hz.AltitudeDeg.Should().BeGreaterThanOrEqualTo(o.MinAltitudeDeg - 1e-6);
                    hz.AltitudeDeg.Should().BeLessThanOrEqualTo(o.MaxAltitudeDeg + 1e-6);
                    hz.AltitudeDeg.Should().BeGreaterThanOrEqualTo(o.Horizon.GetAltitude(hz.AzimuthDeg) - 1e-6);
                    (ha < 0 ? MeridianSide.East : MeridianSide.West).Should().Be(w.Side, $"at {time:HH:mm:ss}");
                }
                if (w.StartLimit == PlanConstraint.MinAltitude) { w.StartAltitudeDeg.Should().BeApproximately(o.MinAltitudeDeg, 0.01); }
                if (w.EndLimit == PlanConstraint.MinAltitude) { w.EndAltitudeDeg.Should().BeApproximately(o.MinAltitudeDeg, 0.01); }
                if (w.StartLimit == PlanConstraint.MaxAltitude) { w.StartAltitudeDeg.Should().BeApproximately(o.MaxAltitudeDeg, 0.01); }
                if (w.EndLimit == PlanConstraint.MaxAltitude) { w.EndAltitudeDeg.Should().BeApproximately(o.MaxAltitudeDeg, 0.01); }
                if (w.StartLimit == PlanConstraint.Darkness) { w.Start.Should().Be(plan.Dusk.Value); }
                if (w.EndLimit == PlanConstraint.Darkness) { w.End.Should().Be(plan.Dawn.Value); }
            }
        }

        [Test]
        public void Ngc253_IsSplitAtTheMeridian() {
            var plan = NightPlanner.Plan(Tonight, new[] { ClassicTargets.Ngc253 });
            var t = plan.Targets.Single();

            t.Flags.Should().Be(TargetFlags.None);
            t.TransitInDarkness.Should().BeTrue();
            t.Windows.Should().HaveCount(2);
            var east = t.Windows[0];
            var west = t.Windows[1];
            east.Side.Should().Be(MeridianSide.East);
            east.StartLimit.Should().Be(PlanConstraint.MinAltitude);
            east.EndLimit.Should().Be(PlanConstraint.Meridian);
            west.Side.Should().Be(MeridianSide.West);
            west.StartLimit.Should().Be(PlanConstraint.Meridian);
            west.EndLimit.Should().Be(PlanConstraint.MinAltitude);
            (east.End - t.Transit).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1));
            (west.Start - t.Transit).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1));
            AssertWindowsAreConsistent(plan, t);

            // Worst case is at the meridian: the transit value of the rotation table for this declination.
            var transitLimit = FieldRotation.MaxSubSecondsAtHourAngle(22.25, 0, t.OfDate.DecDeg, plan.Options.Train);
            east.ShortestMaxSubSeconds.Should().BeApproximately(transitLimit, 0.01);
            east.RecommendedSubSeconds.Should().Be(5, "the 9.9 s limit at transit is below the 10 s step");
            east.FrameRotationDeg.Should().BeGreaterThan(0);
        }

        [Test]
        public void M42_OpensAtMinAltitudeAndRunsIntoDawn() {
            var plan = NightPlanner.Plan(Tonight, new[] { ClassicTargets.M42 });
            var t = plan.Targets.Single();

            t.Windows.Should().ContainSingle();
            t.Windows[0].Side.Should().Be(MeridianSide.East);
            t.Windows[0].StartLimit.Should().Be(PlanConstraint.MinAltitude);
            t.Windows[0].EndLimit.Should().Be(PlanConstraint.Darkness);
            t.TransitInDarkness.Should().BeFalse("M42 transits just after astronomical dawn in early October");
            AssertWindowsAreConsistent(plan, t);
        }

        [Test]
        public void SpringTargets_AreNotUpInDarknessInOctober() {
            var plan = NightPlanner.Plan(Tonight, new[] { ClassicTargets.M83, ClassicTargets.OmegaCentauri });

            plan.Targets.Should().OnlyContain(t => t.Windows.Count == 0 && t.Flags == TargetFlags.NotUpInDarkness);
            plan.Targets.Should().OnlyContain(t => t.Notes.Any(n => n.StartsWith("Not within the limits during darkness")));
        }

        [Test]
        public void CircumpolarButBlockedNorth_HasNoWindows() {
            var t = PlanOne(Polaris);

            t.Flags.Should().HaveFlag(TargetFlags.Circumpolar).And.HaveFlag(TargetFlags.BlockedByHorizon);
            t.Flags.Should().NotHaveFlag(TargetFlags.NotUpInDarkness);
            t.Windows.Should().BeEmpty();
            t.TransitAzimuthDeg.Should().Be(0);
            t.Notes.Should().Contain(n => n.StartsWith("Circumpolar")).And.Contain(n => n.StartsWith("Never clears the horizon profile"));

            // A Dec +75 field never leaves the 60 deg northern wall either (azimuth stays within ~16 deg of north).
            PlanOne(new PlanTarget("Dec+75", 12, 75)).Flags.Should().HaveFlag(TargetFlags.BlockedByHorizon).And.HaveFlag(TargetFlags.Circumpolar);
        }

        [Test]
        public void CircumpolarWithFlatHorizon_IsUsableAllNight() {
            var plan = NightPlanner.Plan(Tonight, new[] { Polaris }, new NightPlanOptions { Horizon = HorizonProfile.Flat() });
            var t = plan.Targets.Single();

            t.Flags.Should().Be(TargetFlags.Circumpolar);
            (t.UsableTime - (plan.Dawn.Value - plan.Dusk.Value)).Duration().Should().BeLessThan(TimeSpan.FromSeconds(3));
            t.Windows.Should().OnlyContain(w => w.StartLimit == PlanConstraint.Darkness || w.StartLimit == PlanConstraint.Meridian);
            AssertWindowsAreConsistent(plan, t);
        }

        [Test]
        public void CircumpolarNote_NamesTheDirectionOfLowerCulmination() {
            // Review F4: lower culmination is towards the elevated pole, so due south from a southern site.
            PlanOne(Polaris).Notes.Should().Contain(n => n.StartsWith("Circumpolar at 22.25°N") && n.EndsWith("(due north)."));

            const double lat = -33.9;
            var capeTown = new Site("Cape Town", lat, 18.4, TimeSpan.FromHours(2));
            var options = new NightPlanOptions { Site = capeTown, Horizon = HorizonProfile.Flat() };
            var t = PlanOne(new PlanTarget("Dec-80", 6, -80), options: options);

            // Independent of the note's code path: the lowest point (H = 12 h) is at azimuth 180, alt |phi + delta| - 90.
            var lowest = SphericalAstronomy.EquatorialToHorizontal(12, t.OfDate.DecDeg, lat);
            lowest.AzimuthDeg.Should().BeApproximately(180, 1e-9);
            lowest.AltitudeDeg.Should().BeApproximately(23.9, 0.05);
            t.Flags.Should().HaveFlag(TargetFlags.Circumpolar);
            t.Notes.Should().Contain(Invariant($"Circumpolar at 33.90°S: lowest altitude {lowest.AltitudeDeg:0.0}° (due south)."));
        }

        private static string Invariant(FormattableString s) {
            return FormattableString.Invariant(s);
        }

        [Test]
        public void NeverRises_IsFlaggedAndNotBlamedOnTheHorizon() {
            var t = PlanOne(Smc);

            t.Flags.Should().Be(TargetFlags.NeverRises);
            t.Windows.Should().BeEmpty();
            t.TransitAltitudeDeg.Should().BeLessThan(0);
            t.TimeAboveMinAltitude.Should().Be(TimeSpan.Zero);
            t.Notes.Should().ContainSingle().Which.Should().StartWith("Never rises at 22.25°N");
        }

        [Test]
        public void ZenithPass_IsFlaggedAndCutOutOfTheWindows() {
            var date = new DateOnly(2026, 12, 15);
            var plan = NightPlanner.Plan(date, new[] { M1 });
            var t = plan.Targets.Single();

            t.TransitInDarkness.Should().BeTrue();
            t.TransitAltitudeDeg.Should().BeGreaterThan(89.5);
            t.Flags.Should().HaveFlag(TargetFlags.PassesZenithKeyhole);
            t.Keyhole.Should().NotBeNull();
            t.Notes.Should().Contain(n => n.StartsWith("Zenith keyhole"));

            t.Windows.Should().HaveCount(2);
            var east = t.Windows[0];
            var west = t.Windows[1];
            east.Side.Should().Be(MeridianSide.East);
            east.EndLimit.Should().Be(PlanConstraint.MaxAltitude);
            west.Side.Should().Be(MeridianSide.West);
            west.StartLimit.Should().Be(PlanConstraint.MaxAltitude);
            (east.End - t.Keyhole.Value.From).Duration().Should().BeLessThan(TimeSpan.FromSeconds(2));
            (west.Start - t.Keyhole.Value.To).Duration().Should().BeLessThan(TimeSpan.FromSeconds(2));
            t.Windows.Should().NotContain(w => w.EndLimit == PlanConstraint.Meridian, "the meridian lies inside the keyhole");
            AssertWindowsAreConsistent(plan, t);
        }

        [Test]
        public void MinSub_TrimsTheRotationLimitedStretchAroundTransit() {
            var options = new NightPlanOptions { MinSubSeconds = 15 };
            var plan = NightPlanner.Plan(Tonight, new[] { ClassicTargets.Ngc253 }, options);
            var t = plan.Targets.Single();

            t.Windows.Should().HaveCount(2);
            t.Windows[0].EndLimit.Should().Be(PlanConstraint.FieldRotation);
            t.Windows[1].StartLimit.Should().Be(PlanConstraint.FieldRotation);
            t.Windows.Should().OnlyContain(w => w.ShortestMaxSubSeconds >= 15 && w.RecommendedSubSeconds == 10);
            t.Windows[0].End.Should().BeBefore(t.Transit);
            t.Windows[1].Start.Should().BeAfter(t.Transit);
            AssertWindowsAreConsistent(plan, t);
        }

        [Test]
        public void RotationLimited_WhenNoStepFitsNearTransit() {
            // Dec +5 transits at alt 72.75 (inside the 75 deg limit) where 1 px allows only ~4.0 s.
            var t = PlanOne(new PlanTarget("Dec+5", 1.0, 5.0));

            t.Flags.Should().HaveFlag(TargetFlags.RotationLimited);
            t.Windows.Should().Contain(w => w.RecommendedSubSeconds == null && w.ShortestMaxSubSeconds < 5);
            t.Notes.Should().Contain(n => n.Contains("below the shortest step (5 s)"));
        }

        [Test]
        public void NauticalDarkness_OpensEveningTargetsEarlier() {
            var astro = NightPlanner.Plan(Tonight, new[] { ClassicTargets.M8 });
            var nautical = NightPlanner.Plan(Tonight, new[] { ClassicTargets.M8 }, new NightPlanOptions { SunAltitudeDeg = LowPrecisionSun.NauticalTwilightDeg });

            nautical.Dusk.Value.Should().BeBefore(astro.Dusk.Value);
            var a = astro.Targets.Single().Windows.Single();
            var n = nautical.Targets.Single().Windows.Single();
            a.StartLimit.Should().Be(PlanConstraint.Darkness);
            n.Start.Should().Be(nautical.Dusk.Value);
            n.Duration.Should().BeGreaterThan(a.Duration);
            (n.End - a.End).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1), "both close when M8 drops below the minimum altitude");
        }

        [Test]
        public void NoDarkness_IsFlaggedForEveryTarget() {
            var options = new NightPlanOptions { Site = new Site("Tromso", 69.65, 18.96, TimeSpan.FromHours(2)), Horizon = HorizonProfile.Flat() };

            var plan = NightPlanner.Plan(new DateOnly(2026, 6, 21), new[] { ClassicTargets.M42, Polaris }, options);

            plan.Dusk.Should().BeNull();
            plan.Targets.Should().OnlyContain(t => t.Flags.HasFlag(TargetFlags.NoDarkness) && t.Windows.Count == 0);
        }

        [Test]
        public void ReportSamples_AreOnTheClockGridAndMatchTheRotationMaths() {
            var t = PlanOne(ClassicTargets.Ngc253);

            t.Samples.Should().NotBeEmpty();
            t.Samples.Should().OnlyContain(s => s.Time.Minute % 30 == 0 && s.Time.Second == 0 && s.AltitudeDeg > 0);
            foreach (var s in t.Samples) {
                s.MaxSubSeconds.Should().BeApproximately(FieldRotation.MaxSubSeconds(22.25, s.AltitudeDeg, s.AzimuthDeg, ImagingTrain.Asi585Native(2)), 1e-9);
                s.RecommendedSubSeconds.Should().Be(NightPlanner.RecommendedStep(s.MaxSubSeconds, new[] { 5.0, 10.0, 20.0, 30.0 }));
            }
        }

        [TestCase(9.9, 5.0)]
        [TestCase(10.0, 10.0)]
        [TestCase(30.1, 30.0)]
        [TestCase(double.PositiveInfinity, 30.0)]
        [TestCase(4.0, null)]
        public void RecommendedStep_IsTheLongestStepWithinTheLimit(double limit, double? expected) {
            NightPlanner.RecommendedStep(limit, new[] { 5.0, 10.0, 20.0, 30.0 }).Should().Be(expected);
        }

        [Test]
        public void EveningDate_BeforeNoonBelongsToLastNight() {
            var site = Site.DeepWaterBay;
            NightPlanner.EveningDateFor(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.FromHours(8)), site).Should().Be(new DateOnly(2026, 10, 4));
            NightPlanner.EveningDateFor(new DateTimeOffset(2026, 10, 4, 18, 0, 0, TimeSpan.FromHours(8)), site).Should().Be(new DateOnly(2026, 10, 4));
            // 2026-10-04 20:00 UTC is 04:00 on the 5th in Hong Kong: still the night of the 4th.
            NightPlanner.EveningDateFor(new DateTimeOffset(2026, 10, 4, 20, 0, 0, TimeSpan.Zero), site).Should().Be(new DateOnly(2026, 10, 4));
        }

        [Test]
        public void Options_AreValidated() {
            FluentActions.Invoking(() => NightPlanner.Plan(Tonight, new[] { Polaris }, new NightPlanOptions { MinAltitudeDeg = 80, MaxAltitudeDeg = 75 }))
                .Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => NightPlanner.Plan(Tonight, new[] { Polaris }, new NightPlanOptions { SampleSeconds = 1 }))
                .Should().Throw<ArgumentException>();
        }

        [Test]
        public void PlanTarget_ParsesNameRaDec() {
            var t = PlanTarget.Parse("M1, Crab,05:34:31.94,+22:00:52.2");
            t.Name.Should().Be("M1, Crab");
            t.J2000.RaHours.Should().BeApproximately(5.575539, 1e-6);
            t.J2000.DecDeg.Should().BeApproximately(22.014500, 1e-6);
            FluentActions.Invoking(() => PlanTarget.Parse("M1,25,0")).Should().Throw<FormatException>();
            FluentActions.Invoking(() => PlanTarget.Parse("M1")).Should().Throw<FormatException>();
        }

        [Test]
        public void Report_ShowsWindowsFlagsAndPlaceholderWarning() {
            var plan = NightPlanner.Plan(Tonight, new[] { ClassicTargets.Ngc253, Polaris, Smc });
            var w = new StringWriter();

            NightPlanReport.Write(plan, w);
            var text = w.ToString();

            text.Should().Contain("Night of Sun 2026-10-04 at Deep Water Bay, Hong Kong (22.25N 114.18E, UTC+08:00)");
            text.Should().Contain("PLACEHOLDER");
            text.Should().Contain("  EAST ").And.Contain("  WEST ").And.Contain("opens: meridian");
            text.Should().Contain("! Never rises").And.Contain("! Circumpolar").And.Contain("! Never clears the horizon profile");
        }
    }
}
