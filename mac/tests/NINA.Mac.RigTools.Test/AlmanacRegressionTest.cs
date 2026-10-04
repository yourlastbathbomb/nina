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
using NINA.Mac.RigTools.Planning;
using NUnit.Framework;
using System;
using System.Linq;

namespace NINA.Mac.RigTools.Test {

    /// <summary>
    /// Absolute clock times for one night (2026-10-04, Deep Water Bay, HKT), pinned to an independent oracle:
    /// Oracle/almanac_oracle.py. It shares no code or model with RigTools and is more complete (IAU 2006 GMST plus
    /// the equation of the equinoxes, Meeus ch. 25 Sun with nutation and aberration, IAU 2006 precession plus
    /// nutation and annual aberration for the targets), and it reproduces Meeus 12.a to 1.2 ms of sidereal time,
    /// 23.a (apparent place) to 0.1" and 25.a (Sun) to 0.0003 deg of Meeus' value.
    /// <para>
    /// Tolerance: 5 s. Measured differences when these tests were written: twilight 0.1-0.9 s; target edges
    /// 0.2-2.4 s, the largest from the ~20" annual aberration RigTools deliberately ignores (NGC 253 setting
    /// edge 2.4 s, transit 1.6 s). Any modelling slip worth catching (UTC offset, solar instead of sidereal rate,
    /// missing precession, wrong twilight sign) moves these times by a minute or more; the earlier sanity test
    /// only bounded the dusk and dawn hours.
    /// </para>
    /// No external almanac (HKO/USNO) was consulted; see the README.
    /// </summary>
    [TestFixture]
    public class AlmanacRegressionTest {
        private static readonly DateOnly Night = new DateOnly(2026, 10, 4);
        private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(5);

        /// <summary>Oracle time in Hong Kong time (UTC+8), to the millisecond as the script prints it.</summary>
        private static DateTimeOffset Hkt(int day, int hour, int minute, double seconds) {
            return new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(8)).AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
        }

        private static void ShouldBeNear(DateTimeOffset actual, DateTimeOffset expected, string what) {
            (actual - expected).Duration().Should().BeLessThanOrEqualTo(Tolerance,
                $"{what}: RigTools {actual.ToOffset(expected.Offset):yyyy-MM-dd HH:mm:ss.fff}, oracle {expected:yyyy-MM-dd HH:mm:ss.fff}");
        }

        [TestCase(LowPrecisionSun.CivilTwilightDeg, 4, 18, 30, 42.900, 5, 5, 53, 27.620)]
        [TestCase(LowPrecisionSun.NauticalTwilightDeg, 4, 18, 56, 38.173, 5, 5, 27, 30.892)]
        [TestCase(LowPrecisionSun.AstronomicalTwilightDeg, 4, 19, 22, 33.549, 5, 5, 1, 34.277)]
        public void DarkInterval_MatchesTheOracle(double limit, int duskDay, int duskHour, int duskMinute, double duskSeconds,
                                                  int dawnDay, int dawnHour, int dawnMinute, double dawnSeconds) {
            var dark = LowPrecisionSun.DarkInterval(Night, Site.DeepWaterBay, limit);

            dark.Should().NotBeNull();
            ShouldBeNear(dark.Value.Dusk, Hkt(duskDay, duskHour, duskMinute, duskSeconds), $"dusk at {limit} deg");
            ShouldBeNear(dark.Value.Dawn, Hkt(dawnDay, dawnHour, dawnMinute, dawnSeconds), $"dawn at {limit} deg");
        }

        [Test]
        public void Planner_TransitAndWindowEdges_MatchTheOracle() {
            var plan = NightPlanner.Plan(Night, new[] { ClassicTargets.Ngc253, ClassicTargets.M42, ClassicTargets.M8, ClassicTargets.M20 });
            var ngc253 = plan.Targets[0];
            var m42 = plan.Targets[1];
            var m8 = plan.Targets[2];
            var m20 = plan.Targets[3];

            ShouldBeNear(plan.Dusk.Value, Hkt(4, 19, 22, 33.549), "astronomical dusk");
            ShouldBeNear(plan.Dawn.Value, Hkt(5, 5, 1, 34.277), "astronomical dawn");

            // NGC 253: rises through 15 deg, transits, sets through 15 deg; split at the meridian.
            ngc253.Windows.Select(w => (w.Side, w.StartLimit, w.EndLimit)).Should().Equal(
                (MeridianSide.East, PlanConstraint.MinAltitude, PlanConstraint.Meridian),
                (MeridianSide.West, PlanConstraint.Meridian, PlanConstraint.MinAltitude));
            ShouldBeNear(ngc253.Windows[0].Start, Hkt(4, 20, 19, 34.950), "NGC 253 rises through 15 deg");
            ShouldBeNear(ngc253.Transit, Hkt(5, 0, 18, 41.623), "NGC 253 transit");
            ShouldBeNear(ngc253.Windows[0].End, Hkt(5, 0, 18, 41.623), "NGC 253 east window ends at transit");
            ShouldBeNear(ngc253.Windows[1].Start, Hkt(5, 0, 18, 41.623), "NGC 253 west window starts at transit");
            ShouldBeNear(ngc253.Windows[1].End, Hkt(5, 4, 17, 48.294), "NGC 253 sets through 15 deg");

            // M42: rises through 15 deg after midnight, runs into astronomical dawn.
            m42.Windows.Should().ContainSingle();
            m42.Windows[0].StartLimit.Should().Be(PlanConstraint.MinAltitude);
            m42.Windows[0].EndLimit.Should().Be(PlanConstraint.Darkness);
            ShouldBeNear(m42.Windows[0].Start, Hkt(5, 0, 20, 53.844), "M42 rises through 15 deg");
            ShouldBeNear(m42.Transit, Hkt(5, 5, 5, 37.643), "M42 transit");

            // M8 and M20: evening targets, from dusk until they sink through 15 deg in the south-west.
            foreach (var (target, transit, sets) in new[] {
                (m8, Hkt(4, 17, 36, 9.571), Hkt(4, 21, 37, 28.800)),
                (m20, Hkt(4, 17, 34, 54.756), Hkt(4, 21, 40, 4.912)) }) {
                target.Windows.Should().ContainSingle();
                target.Windows[0].StartLimit.Should().Be(PlanConstraint.Darkness);
                target.Windows[0].EndLimit.Should().Be(PlanConstraint.MinAltitude);
                ShouldBeNear(target.Transit, transit, $"{target.Target.Name} transit");
                ShouldBeNear(target.Windows[0].End, sets, $"{target.Target.Name} sinks through 15 deg");
            }
        }
    }
}
