#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.RigTools.Astronomy;
using NINA.Mac.RigTools.Rotation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NINA.Mac.RigTools.Planning {

    /// <summary>
    /// Night planner for an alt-az rig: for each target, the stretches of tonight's darkness when it is above
    /// both the horizon profile and the minimum altitude and below the zenith keyhole, split at the meridian
    /// (image east or west of it, not across it: MAC_PORT_PLAN.md section 6), with the field-rotation max sub
    /// over each window.
    /// <para>
    /// Method: sample every <see cref="NightPlanOptions.SampleSeconds"/> between dusk and dawn, classify each
    /// instant (usable east / usable west / unusable), bisect every change of class to 0.5 s, then take the worst
    /// max sub and the frame-rotation span over the fine samples of each window. Positions are J2000 precessed to
    /// the middle of the night; altitudes are geometric (see <see cref="SphericalAstronomy"/> for accuracy).
    /// </para>
    /// </summary>
    public static class NightPlanner {

        public static NightPlan Plan(DateOnly eveningDate, IEnumerable<PlanTarget> targets, NightPlanOptions options = null) {
            options ??= new NightPlanOptions();
            options.Validate();
            if (targets == null) { throw new ArgumentNullException(nameof(targets)); }

            var site = options.Site;
            var dark = LowPrecisionSun.DarkInterval(eveningDate, site, options.SunAltitudeDeg);
            var plans = targets.Select(t => PlanTarget(eveningDate, t, options, dark)).ToList();
            return new NightPlan {
                EveningDate = eveningDate,
                Options = options,
                Dusk = dark.HasValue ? site.ToLocal(dark.Value.Dusk) : null,
                Dawn = dark.HasValue ? site.ToLocal(dark.Value.Dawn) : null,
                Targets = plans
            };
        }

        /// <summary>The evening date whose night contains <paramref name="now"/>: before local noon it is still last night.</summary>
        public static DateOnly EveningDateFor(DateTimeOffset now, Site site) {
            var local = now.ToOffset(site.UtcOffset);
            var date = DateOnly.FromDateTime(local.DateTime);
            return local.Hour < 12 ? date.AddDays(-1) : date;
        }

        /// <summary>Largest exposure step not above the limit, or null if none fits.</summary>
        public static double? RecommendedStep(double maxSubSeconds, IReadOnlyList<double> stepsSeconds) {
            double? best = null;
            foreach (var step in stepsSeconds) {
                if (step <= maxSubSeconds && (best == null || step > best.Value)) {
                    best = step;
                }
            }
            return best;
        }

        private sealed class Evaluator {
            private readonly NightPlanOptions options;
            private readonly EquatorialCoordinates ofDate;

            public Evaluator(NightPlanOptions options, EquatorialCoordinates ofDate) {
                this.options = options;
                this.ofDate = ofDate;
            }

            public PlanSample At(DateTimeOffset t) {
                var site = options.Site;
                var ha = site.HourAngleHours(ofDate, t);
                var hz = SphericalAstronomy.EquatorialToHorizontal(ha, ofDate.DecDeg, site.LatitudeDeg);
                var horizonAlt = options.Horizon.GetAltitude(hz.AzimuthDeg);
                var rate = FieldRotation.RateRadPerSec(site.LatitudeDeg, hz.AltitudeDeg, hz.AzimuthDeg);
                var maxSub = FieldRotation.MaxSubSeconds(rate, options.Train.HalfDiagonalPx, options.AllowedBlurPx);
                var violations = PlanConstraint.None;
                if (hz.AltitudeDeg < horizonAlt) { violations |= PlanConstraint.Horizon; }
                if (hz.AltitudeDeg < options.MinAltitudeDeg) { violations |= PlanConstraint.MinAltitude; }
                if (hz.AltitudeDeg > options.MaxAltitudeDeg) { violations |= PlanConstraint.MaxAltitude; }
                if (options.MinSubSeconds.HasValue && maxSub < options.MinSubSeconds.Value) { violations |= PlanConstraint.FieldRotation; }
                return new PlanSample {
                    Time = site.ToLocal(t),
                    AltitudeDeg = hz.AltitudeDeg,
                    AzimuthDeg = hz.AzimuthDeg,
                    HourAngleHours = ha,
                    HorizonAltitudeDeg = horizonAlt,
                    ParallacticAngleDeg = SphericalAstronomy.ParallacticAngleDeg(ha, ofDate.DecDeg, site.LatitudeDeg),
                    RotationRateDegPerHour = rate * AngleMath.RadToDeg * 3600.0,
                    MaxSubSeconds = maxSub,
                    RecommendedSubSeconds = RecommendedStep(maxSub, options.ExposureStepsSeconds),
                    Violations = violations
                };
            }

            /// <summary>0 = unusable, 1 = usable east, 2 = usable west.</summary>
            public static int Class(PlanSample s) {
                return !s.Usable ? 0 : (s.Side == MeridianSide.East ? 1 : 2);
            }
        }

        private static TargetPlan PlanTarget(DateOnly eveningDate, PlanTarget target, NightPlanOptions options, (DateTimeOffset Dusk, DateTimeOffset Dawn)? dark) {
            var site = options.Site;
            var lat = site.LatitudeDeg;
            var localNoon = new DateTimeOffset(eveningDate.Year, eveningDate.Month, eveningDate.Day, 12, 0, 0, site.UtcOffset);
            var middle = dark.HasValue ? dark.Value.Dusk + TimeSpan.FromTicks((dark.Value.Dawn - dark.Value.Dusk).Ticks / 2) : localNoon.AddHours(12);
            var ofDate = Precession.FromJ2000(target.J2000, middle);
            var dec = ofDate.DecDeg;
            var evaluator = new Evaluator(options, ofDate);

            var transit = site.ToLocal(SphericalAstronomy.NearestTransit(middle, ofDate.RaHours, site.LongitudeDeg));
            var transitAlt = SphericalAstronomy.TransitAltitudeDeg(dec, lat);
            var transitAz = SphericalAstronomy.TransitAzimuthDeg(dec, lat);
            var flags = TargetFlags.None;
            var notes = new List<string>();

            if (SphericalAstronomy.NeverRises(dec, lat)) {
                flags |= TargetFlags.NeverRises;
                notes.Add(Invariant($"Never rises at {Lat(lat)}: transit altitude {transitAlt:0.0}° (needs Dec above {lat - 90:0.00}°)."));
            }
            if (SphericalAstronomy.IsCircumpolar(dec, lat)) {
                flags |= TargetFlags.Circumpolar;
                // Lower culmination is towards the elevated pole: due north from a northern site, due south from a southern one.
                notes.Add(Invariant($"Circumpolar at {Lat(lat)}: lowest altitude {SphericalAstronomy.LowerCulminationAltitudeDeg(dec, lat):0.0}° (due {(lat >= 0 ? "north" : "south")})."));
            }
            if (!flags.HasFlag(TargetFlags.NeverRises) && !ClearsLimitsAnyTime(evaluator, middle)) {
                flags |= TargetFlags.BlockedByHorizon;
                // Above the keyhole, the stretch below the max altitude is all behind the profile or under the minimum.
                var belowMax = transitAlt > options.MaxAltitudeDeg ? Invariant($" while below the {options.MaxAltitudeDeg:0.#}° maximum") : "";
                notes.Add(Invariant($"Never clears the horizon profile and the {options.MinAltitudeDeg:0}° minimum at any hour angle{belowMax} (highest point: alt {transitAlt:0.0}° at az {transitAz:0})."));
            }

            (DateTimeOffset, DateTimeOffset)? keyhole = null;
            if (transitAlt > options.MaxAltitudeDeg) {
                flags |= TargetFlags.PassesZenithKeyhole;
                var h = SphericalAstronomy.HourAngleAtAltitudeHours(dec, lat, options.MaxAltitudeDeg) / AstroTime.SiderealPerSolar;
                keyhole = (transit - TimeSpan.FromHours(h), transit + TimeSpan.FromHours(h));
                notes.Add(Invariant($"Zenith keyhole: transits at alt {transitAlt:0.0}° ({90 - transitAlt:0.0}° from the zenith) at {T(transit)}; above {options.MaxAltitudeDeg:0}° from {T(keyhole.Value.Item1)} to {T(keyhole.Value.Item2)}. The azimuth axis cannot keep up near the zenith and field rotation is extreme, so no imaging there."));
            }

            var transitInDarkness = dark.HasValue && transit >= dark.Value.Dusk && transit <= dark.Value.Dawn;
            var timeAboveMin = SphericalAstronomy.TimeAboveAltitude(dec, lat, options.MinAltitudeDeg);

            if (!dark.HasValue) {
                flags |= TargetFlags.NoDarkness;
                notes.Add(Invariant($"The Sun never gets below {options.SunAltitudeDeg:0}° this night."));
                return new TargetPlan {
                    Target = target, OfDate = ofDate, Flags = flags, Transit = transit, TransitAltitudeDeg = transitAlt,
                    TransitAzimuthDeg = transitAz, TransitInDarkness = false, Keyhole = keyhole, TimeAboveMinAltitude = timeAboveMin, Notes = notes
                };
            }

            var dusk = dark.Value.Dusk;
            var dawn = dark.Value.Dawn;
            var samples = FineSamples(evaluator, dusk, dawn, options.SampleSeconds);
            var windows = BuildWindows(evaluator, samples, options);

            if (windows.Count == 0 && (flags & (TargetFlags.NeverRises | TargetFlags.BlockedByHorizon)) == 0) {
                flags |= TargetFlags.NotUpInDarkness;
                notes.Add(Invariant($"Not within the limits during darkness ({T(site.ToLocal(dusk))}-{T(site.ToLocal(dawn))}); transit is at {T(transit)}."));
            }
            var smallestStep = options.ExposureStepsSeconds.Min();
            foreach (var w in windows.Where(w => w.RecommendedSubSeconds == null)) {
                flags |= TargetFlags.RotationLimited;
                notes.Add(Invariant($"{w.Side} window {T(w.Start)}-{T(w.End)}: field rotation allows only {w.ShortestMaxSubSeconds:0.0} s at {T(w.ShortestMaxSubAt)}, below the shortest step ({smallestStep:0.#} s). Shorten the window or accept more corner blur."));
            }

            return new TargetPlan {
                Target = target,
                OfDate = ofDate,
                Flags = flags,
                Transit = transit,
                TransitAltitudeDeg = transitAlt,
                TransitAzimuthDeg = transitAz,
                TransitInDarkness = transitInDarkness,
                Keyhole = keyhole,
                TimeAboveMinAltitude = timeAboveMin,
                Windows = windows,
                Samples = ReportSamples(evaluator, dusk, dawn, options.ReportIntervalMinutes, options.Site),
                Notes = notes
            };
        }

        /// <summary>
        /// Whether the target is within all the geometric limits (horizon profile, minimum and maximum altitude) at
        /// the same instant at some hour angle (one sidereal day, 2-minute steps). Darkness and the optional
        /// field-rotation cut are not part of it.
        /// </summary>
        private static bool ClearsLimitsAnyTime(Evaluator evaluator, DateTimeOffset start) {
            const PlanConstraint geometric = PlanConstraint.Horizon | PlanConstraint.MinAltitude | PlanConstraint.MaxAltitude;
            var step = TimeSpan.FromMinutes(2);
            var end = start + TimeSpan.FromHours(24 / AstroTime.SiderealPerSolar);
            for (var t = start; t <= end; t += step) {
                var s = evaluator.At(t);
                if ((s.Violations & geometric) == 0) {
                    return true;
                }
            }
            return false;
        }

        private static List<PlanSample> FineSamples(Evaluator evaluator, DateTimeOffset dusk, DateTimeOffset dawn, int sampleSeconds) {
            var list = new List<PlanSample>();
            var step = TimeSpan.FromSeconds(sampleSeconds);
            for (var t = dusk; t < dawn; t += step) {
                list.Add(evaluator.At(t));
            }
            list.Add(evaluator.At(dawn));
            return list;
        }

        /// <summary>Bisects a class change between a and b to 0.5 s; returns the last instant still in the old class and the first in the new one.</summary>
        private static (PlanSample LastSame, PlanSample FirstDifferent) Refine(Evaluator evaluator, PlanSample a, PlanSample b) {
            var classA = Evaluator.Class(a);
            while (b.Time - a.Time > TimeSpan.FromSeconds(0.5)) {
                var mid = evaluator.At(a.Time + TimeSpan.FromTicks((b.Time - a.Time).Ticks / 2));
                if (Evaluator.Class(mid) == classA) {
                    a = mid;
                } else {
                    b = mid;
                }
            }
            return (a, b);
        }

        private static List<ImagingWindow> BuildWindows(Evaluator evaluator, List<PlanSample> samples, NightPlanOptions options) {
            var windows = new List<ImagingWindow>();
            PlanSample windowStart = null;
            var startLimit = PlanConstraint.None;
            var inside = new List<PlanSample>();

            void Close(PlanSample lastInside, PlanConstraint endLimit) {
                windows.Add(Summarise(windowStart, lastInside, startLimit, endLimit, inside, options));
                windowStart = null;
                inside = new List<PlanSample>();
            }

            if (Evaluator.Class(samples[0]) != 0) {
                windowStart = samples[0];
                startLimit = PlanConstraint.Darkness;
            }
            for (var i = 1; i < samples.Count; i++) {
                var a = samples[i - 1];
                var b = samples[i];
                // Handle every class change between two fine samples, even several within one step.
                while (Evaluator.Class(a) != Evaluator.Class(b)) {
                    var (lastSame, firstDifferent) = Refine(evaluator, a, b);
                    var newClass = Evaluator.Class(firstDifferent);
                    if (windowStart != null) {
                        Close(lastSame, newClass == 0 ? firstDifferent.Violations : PlanConstraint.Meridian);
                    }
                    if (newClass != 0) {
                        windowStart = firstDifferent;
                        startLimit = Evaluator.Class(lastSame) == 0 ? lastSame.Violations : PlanConstraint.Meridian;
                    }
                    a = firstDifferent;
                }
                if (windowStart != null) {
                    inside.Add(b);
                }
            }
            if (windowStart != null) {
                Close(samples[samples.Count - 1], PlanConstraint.Darkness);
            }
            return windows;
        }

        private static ImagingWindow Summarise(PlanSample start, PlanSample end, PlanConstraint startLimit, PlanConstraint endLimit, List<PlanSample> inside, NightPlanOptions options) {
            var points = new List<PlanSample> { start };
            points.AddRange(inside.Where(s => s.Time > start.Time && s.Time < end.Time));
            if (end.Time > start.Time) { points.Add(end); }
            var worst = points.OrderBy(p => p.MaxSubSeconds).First();
            var rotation = FrameRotation.FromParallacticAngles(points.Select(p => p.ParallacticAngleDeg).ToList());
            return new ImagingWindow {
                Side = start.Side,
                Start = start.Time,
                End = end.Time,
                StartLimit = startLimit,
                EndLimit = endLimit,
                StartAltitudeDeg = start.AltitudeDeg,
                EndAltitudeDeg = end.AltitudeDeg,
                PeakAltitudeDeg = points.Max(p => p.AltitudeDeg),
                ShortestMaxSubSeconds = worst.MaxSubSeconds,
                ShortestMaxSubAt = worst.Time,
                RecommendedSubSeconds = RecommendedStep(worst.MaxSubSeconds, options.ExposureStepsSeconds),
                FrameRotationDeg = rotation.SpanDeg
            };
        }

        /// <summary>Samples on a clock-aligned grid (e.g. :00 and :30) inside darkness while the target is above the geometric horizon.</summary>
        private static List<PlanSample> ReportSamples(Evaluator evaluator, DateTimeOffset dusk, DateTimeOffset dawn, int intervalMinutes, Site site) {
            var list = new List<PlanSample>();
            var localDusk = site.ToLocal(dusk);
            var minutes = localDusk.Hour * 60 + localDusk.Minute;
            var first = new DateTimeOffset(localDusk.Year, localDusk.Month, localDusk.Day, 0, 0, 0, localDusk.Offset)
                .AddMinutes((minutes / intervalMinutes + 1) * intervalMinutes);
            for (var t = first; t <= dawn; t = t.AddMinutes(intervalMinutes)) {
                var s = evaluator.At(t);
                if (s.AltitudeDeg > 0) {
                    list.Add(s);
                }
            }
            return list;
        }

        private static string T(DateTimeOffset t) {
            return t.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        private static string Lat(double lat) {
            return Invariant($"{Math.Abs(lat):0.00}°{(lat >= 0 ? "N" : "S")}");
        }

        private static string Invariant(FormattableString s) {
            return FormattableString.Invariant(s);
        }
    }
}
