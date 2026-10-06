#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Mac.RigTools.Optics;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NINA.Mac.Sequencing.Planning {

    public enum PlanIssueSeverity {
        Warning,
        Error
    }

    public sealed record PlanIssue(PlanIssueSeverity Severity, string Target, string Message) {
        public override string ToString() => $"{Severity}: {(Target == null ? "" : Target + ": ")}{Message}";
    }

    /// <summary>Why the validator's simulated run ends a target's imaging (or never starts it).</summary>
    public enum ExpectedStop {

        /// <summary>The target is not imaged: never reached, skipped by NINA's validation, or skipped at its turn.</summary>
        NotImaged,

        /// <summary>The exposure count is done (count x sub length, without overheads: a lower bound).</summary>
        Count,

        /// <summary>It sets below the custom horizon plus its offset (AboveHorizonCondition).</summary>
        Horizon,

        /// <summary>It sinks below the minimum altitude (AltitudeCondition).</summary>
        MinimumAltitude,

        /// <summary>It climbs above the maximum altitude (MaxAltitudeCondition, keyhole policy Skip).</summary>
        Keyhole,

        /// <summary>The sub length exceeds the field-rotation limit (LoopWhile, field-rotation policy Stop).</summary>
        FieldRotation,

        /// <summary>The night's dawn stop.</summary>
        Dawn
    }

    /// <summary>When a target can be imaged tonight under its own rules, from the validator's 1-minute samples.</summary>
    public sealed record TargetWindow {
        public string Target { get; init; }

        /// <summary>First and last sample (local time) that satisfy horizon, minimum and maximum altitude before dawn; null if none.</summary>
        public DateTime? Start { get; init; }

        public DateTime? End { get; init; }

        /// <summary>Highest altitude between dusk and dawn, and when.</summary>
        public double PeakAltitudeDeg { get; init; }

        public DateTime PeakTime { get; init; }

        /// <summary>Time above the maximum altitude (the zenith keyhole) between dusk and dawn; null if none.</summary>
        public DateTime? KeyholeStart { get; init; }

        public DateTime? KeyholeEnd { get; init; }

        /// <summary>Shortest field-rotation max sub inside the window, seconds (at the plan's binning and blur tolerance).</summary>
        public double ShortestMaxSubSeconds { get; init; } = double.NaN;

        /// <summary>The part of the window where the planned exposure exceeds the field-rotation max sub; null if none.</summary>
        public DateTime? RotationLimitedStart { get; init; }

        public DateTime? RotationLimitedEnd { get; init; }

        /// <summary>
        /// When the sequence is expected to image this target, from a run of the targets in order (see <see cref="PlanValidator"/>);
        /// null when it is not imaged at all.
        /// </summary>
        public DateTime? ExpectedStart { get; init; }

        public DateTime? ExpectedEnd { get; init; }

        /// <summary>What ends (or prevents) the target's imaging in that run.</summary>
        public ExpectedStop ExpectedStop { get; init; }
    }

    public sealed record PlanReport(DateTime Dusk, DateTime Dawn, IReadOnlyList<TargetWindow> Windows, IReadOnlyList<PlanIssue> Issues) {
        public bool HasErrors => Issues.Any(i => i.Severity == PlanIssueSeverity.Error);
    }

    /// <summary>
    /// Checks a <see cref="NightPlan"/> against tonight's sky before it becomes a sequence (plan section 5.3). Each target is
    /// sampled once a minute between dusk and the night's dawn stop with NINA.Astrometry's own transform
    /// (Coordinates.Transform to topocentric altitude and azimuth) and the profile's custom horizon, the same models the
    /// sequence's waits and conditions use; field rotation comes from NINA.Mac.RigTools. It reports, per target: the window;
    /// the zenith keyhole; where the exposure exceeds the field-rotation max sub (decision 4: warn at 1 px of corner blur); and
    /// targets that never become observable.
    /// <para>
    /// It then runs the generated sequence's logic over the samples, target after target in order, from the later of dusk and
    /// now: each target first waits as its Prepare block does (WaitUntilAboveHorizon, WaitForAltitude above the minimum,
    /// below the maximum for the WaitUntilBelow policy), then images until its block's stop (count, horizon, minimum altitude,
    /// keyhole for Skip, field rotation for Stop, dawn), and the next target starts there. A wait that is never satisfied
    /// before dawn holds the whole rest of the night, as it does at run time: NINA skips a WaitUntilAboveHorizon only when the
    /// target's transit altitude is below the custom horizon's lowest point, so a target behind a high part of the horizon (the
    /// northern sky here) passes NINA's validation and waits until dawn. The run reports targets that are never reached and why,
    /// a target whose wait holds the sequence until dawn, and targets skipped at their turn. Counted targets take count x sub
    /// length (no download, dither or centring time: a lower bound, so a "never reached" is certain, not a guess).
    /// </para>
    /// These are warnings: the only errors are plans the generator would refuse. Dusk and dawn come from NINA's
    /// NighttimeCalculator.
    /// </summary>
    public sealed class PlanValidator {
        private readonly IProfileService profileService;
        private readonly INighttimeCalculator nighttimeCalculator;

        public PlanValidator(IProfileService profileService, INighttimeCalculator nighttimeCalculator) {
            this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            this.nighttimeCalculator = nighttimeCalculator ?? throw new ArgumentNullException(nameof(nighttimeCalculator));
        }

        /// <summary>Sensor size used for the field-rotation limit (the ASI585MC unless a host sets another camera's).</summary>
        public int SensorWidthPx { get; set; } = ImagingTrain.Asi585WidthPx;

        public int SensorHeightPx { get; set; } = ImagingTrain.Asi585HeightPx;

        /// <param name="plan">The night.</param>
        /// <param name="referenceTime">The night to check (NINA's NighttimeCalculator reference), and the earliest time the run can
        /// start; null = tonight, starting now (or at dusk, if that is later).</param>
        public PlanReport Validate(NightPlan plan, DateTime? referenceTime = null) {
            ArgumentNullException.ThrowIfNull(plan);
            var issues = new List<PlanIssue>();
            var windows = new List<TargetWindow>();
            var night = nighttimeCalculator.Calculate(referenceTime);
            var twilight = plan.Dawn switch {
                DawnStop.Nautical => night.NauticalTwilightRiseAndSet,
                DawnStop.Civil => night.CivilTwilightRiseAndSet,
                _ => night.TwilightRiseAndSet
            };
            var dusk = twilight?.Set;
            var dawn = twilight?.Rise;
            if (dusk == null || dawn == null) {
                issues.Add(new PlanIssue(PlanIssueSeverity.Warning, null, $"No {plan.Dawn.ToString().ToLowerInvariant()} twilight tonight; the dawn stop never fires"));
                return new PlanReport(DateTime.MinValue, DateTime.MinValue, windows, issues);
            }
            var start = dusk.Value;
            var end = dawn.Value.AddMinutes(plan.DawnOffsetMinutes);
            if (end <= start) {
                end = end.AddDays(1);
            }
            if (plan.Targets == null || plan.Targets.Count == 0) {
                issues.Add(new PlanIssue(PlanIssueSeverity.Error, null, "The night has no target"));
                return new PlanReport(start, end, windows, issues);
            }

            var astrometry = profileService.ActiveProfile.AstrometrySettings;
            var site = new Site(astrometry.Latitude, astrometry.Longitude, astrometry.Elevation, astrometry.Horizon);
            var sampled = new List<Sampled>();
            foreach (var t in plan.Targets) {
                if (t.Coordinates == null || string.IsNullOrWhiteSpace(t.Name) || !(t.ExposureSeconds > 0)) {
                    issues.Add(new PlanIssue(PlanIssueSeverity.Error, t.Name, "Needs a name, coordinates and an exposure longer than 0 s"));
                    continue;
                }
                sampled.Add(Sample(t, start, end, site));
            }

            var runStart = referenceTime ?? DateTime.Now;
            if (runStart < start) {
                runStart = start;
            }
            var runs = Run(sampled, runStart, end, site);

            for (var i = 0; i < sampled.Count; i++) {
                var s = sampled[i];
                var run = runs[i];
                windows.Add(s.Window with {
                    ExpectedStart = run.ImagingStart,
                    ExpectedEnd = run.ImagingEnd,
                    ExpectedStop = run.Stop
                });
                Report(s, run, start, end, issues);
            }
            return new PlanReport(start, end, windows, issues);
        }

        private sealed record Site(double Latitude, double Longitude, double Elevation, CustomHorizon Horizon);

        /// <summary>One sample of a target: altitude, its horizon (custom horizon at its azimuth plus the offset), the rotation limit.</summary>
        private readonly record struct Point(DateTime Time, double Altitude, double HorizonAltitude, double MaxSub);

        private sealed record Sampled(TargetPlan Plan, IReadOnlyList<Point> Points, TargetWindow Window, double TransitAltitude);

        /// <summary>The outcome of the simulated run for one target.</summary>
        private sealed record RunResult {
            public DateTime? Turn { get; init; }
            public DateTime? ImagingStart { get; init; }
            public DateTime? ImagingEnd { get; init; }
            public ExpectedStop Stop { get; init; }

            /// <summary>Why it was not imaged (null when it was).</summary>
            public string NotImagedBecause { get; init; }

            /// <summary>A wait of this target that dawn ends (it holds the rest of the night), and what it waits for.</summary>
            public string HoldsUntilDawn { get; init; }

            /// <summary>What decided when its turn came: the run's start, or the target before it and how long it held the run.</summary>
            public string TurnSetBy { get; init; }
        }

        private Sampled Sample(TargetPlan t, DateTime start, DateTime end, Site site) {
            var coordinates = t.Coordinates.Transform(Epoch.J2000);
            var train = new ImagingTrain(t.Name, SensorWidthPx, SensorHeightPx, ImagingTrain.Asi585PixelSizeUm, ImagingTrain.NativeFocalLengthMm, Math.Max((short)1, t.Binning));
            var points = new List<Point>();
            DateTime? first = null, last = null, keyFirst = null, keyLast = null, rotFirst = null, rotLast = null;
            var peak = double.NegativeInfinity;
            var peakTime = start;
            var shortest = double.PositiveInfinity;
            for (var time = start; time <= end; time = time.AddMinutes(1)) {
                var altaz = coordinates.Transform(Angle.ByDegree(site.Latitude), Angle.ByDegree(site.Longitude), site.Elevation, time);
                var altitude = altaz.Altitude.Degree;
                var azimuth = altaz.Azimuth.Degree;
                var horizonAltitude = (site.Horizon?.GetAltitude(azimuth) ?? 0) + t.HorizonOffsetDeg;
                var maxSub = RigTools.Rotation.FieldRotation.MaxSubSeconds(site.Latitude, altitude, azimuth, train, t.BlurTolerancePx);
                points.Add(new Point(time, altitude, horizonAltitude, maxSub));
                if (altitude > peak) {
                    peak = altitude;
                    peakTime = time;
                }
                if (altitude > t.MaxAltitudeDeg) {
                    keyFirst ??= time;
                    keyLast = time;
                }
                var observable = altitude >= horizonAltitude && altitude <= t.MaxAltitudeDeg && (t.MinAltitudeDeg == null || altitude >= t.MinAltitudeDeg);
                if (!observable) {
                    continue;
                }
                first ??= time;
                last = time;
                shortest = Math.Min(shortest, maxSub);
                if (t.ExposureSeconds > maxSub) {
                    rotFirst ??= time;
                    rotLast = time;
                }
            }
            var window = new TargetWindow {
                Target = t.Name,
                Start = first,
                End = last,
                PeakAltitudeDeg = peak,
                PeakTime = peakTime,
                KeyholeStart = keyFirst,
                KeyholeEnd = keyLast,
                ShortestMaxSubSeconds = double.IsPositiveInfinity(shortest) ? double.NaN : shortest,
                RotationLimitedStart = rotFirst,
                RotationLimitedEnd = rotLast
            };
            // NINA's own reachability test (WaitUntilAboveHorizon.Validate, WaitForAltitude.Validate): the transit altitude
            return new Sampled(t, points, window, AstroUtil.GetAltitude(0, site.Latitude, coordinates.Dec));
        }

        /// <summary>The generated sequence's logic over the samples, target after target, from <paramref name="runStart"/>.</summary>
        private static List<RunResult> Run(IReadOnlyList<Sampled> targets, DateTime runStart, DateTime end, Site site) {
            var results = new List<RunResult>();
            var cursor = runStart;
            var turnSetBy = $"the run starts at {Format(runStart)}";
            foreach (var s in targets) {
                var t = s.Plan;
                var turn = cursor;
                var setBy = turnSetBy;
                RunResult NotImaged(string why, string holds = null) => new RunResult { Turn = turn, Stop = ExpectedStop.NotImaged, NotImagedBecause = why, HoldsUntilDawn = holds, TurnSetBy = setBy };

                if (cursor >= end) {
                    results.Add(NotImaged("dawn"));
                    continue;
                }
                // NINA's run-time validation skips a wait whose altitude the target never reaches at transit; then nothing is imaged
                var lowestHorizon = (site.Horizon?.GetMinAltitude() ?? 0) + t.HorizonOffsetDeg;
                if (s.TransitAltitude < lowestHorizon || (t.MinAltitudeDeg is double unreachable && s.TransitAltitude < unreachable)) {
                    results.Add(NotImaged("it never rises high enough, so NINA's validation skips it"));
                    continue;
                }
                var points = s.Points;
                var i = FirstIndexAtOrAfter(points, cursor);
                if (i < 0) {
                    results.Add(NotImaged("dawn"));
                    continue;
                }
                if (t.Keyhole == KeyholePolicy.Skip && points[i].Altitude > t.MaxAltitudeDeg) {
                    // Prepare's and Imaging's MaxAltitudeCondition fail at once
                    results.Add(NotImaged(string.Format(CultureInfo.InvariantCulture, "it is above the {0:0.#}° maximum altitude at its turn ({1}), so the keyhole Skip policy skips it", t.MaxAltitudeDeg, Format(turn))));
                    continue;
                }

                // Prepare: the waits, in the generated order
                var waitedFor = "rise above its horizon";
                var j = FindFrom(points, i, p => p.Altitude > p.HorizonAltitude);
                if (j >= 0 && t.MinAltitudeDeg is double minimum) {
                    waitedFor = string.Format(CultureInfo.InvariantCulture, "climb above its {0:0.#}° minimum altitude", minimum);
                    j = FindFrom(points, j, p => p.Altitude > minimum);
                }
                if (j >= 0 && t.Keyhole == KeyholePolicy.WaitUntilBelow) {
                    waitedFor = string.Format(CultureInfo.InvariantCulture, "sink below its {0:0.#}° maximum altitude", t.MaxAltitudeDeg);
                    j = FindFrom(points, j, p => p.Altitude < t.MaxAltitudeDeg);
                }
                if (j < 0) {
                    cursor = end;
                    turnSetBy = $"{t.Name} waits until dawn ({Format(end)}) for it to {waitedFor}, which it does not do tonight";
                    results.Add(NotImaged($"it does not {waitedFor} before dawn", waitedFor));
                    continue;
                }

                // Imaging: until the block's first stop
                var imagingStart = points[j].Time > cursor ? points[j].Time : cursor;
                var remaining = t.Count is int count ? count * t.ExposureSeconds : double.PositiveInfinity;
                var stop = ExpectedStop.Dawn;
                var imagingEnd = end;
                for (var k = j; k < points.Count; k++) {
                    var p = points[k];
                    var stopHere = p.Altitude < p.HorizonAltitude ? ExpectedStop.Horizon
                        : t.MinAltitudeDeg is double min && p.Altitude < min ? ExpectedStop.MinimumAltitude
                        : t.Keyhole == KeyholePolicy.Skip && p.Altitude > t.MaxAltitudeDeg ? ExpectedStop.Keyhole
                        : t.FieldRotation == FieldRotationPolicy.Stop && t.ExposureSeconds > p.MaxSub ? ExpectedStop.FieldRotation
                        : (ExpectedStop?)null;
                    var at = p.Time > imagingStart ? p.Time : imagingStart;
                    if (stopHere is ExpectedStop why) {
                        stop = why;
                        imagingEnd = at;
                        break;
                    }
                    var stepEnd = k + 1 < points.Count ? points[k + 1].Time : end;
                    var step = (stepEnd - at).TotalSeconds;
                    // WaitUntilBelow: the keyhole trigger holds the loop while the target is above the limit
                    var paused = t.Keyhole == KeyholePolicy.WaitUntilBelow && p.Altitude > t.MaxAltitudeDeg;
                    if (!paused && step > 0) {
                        if (remaining <= step) {
                            stop = ExpectedStop.Count;
                            imagingEnd = at.AddSeconds(remaining);
                            break;
                        }
                        remaining -= step;
                    }
                }
                cursor = imagingEnd;
                turnSetBy = imagingEnd <= imagingStart
                    ? $"{t.Name} waits until {Format(imagingStart)}"
                    : t.Count == null
                        ? $"{t.Name} has no exposure count and images until {Format(imagingEnd)}"
                        : $"{t.Name} images until {Format(imagingEnd)} at the earliest";
                if (imagingEnd <= imagingStart) {
                    var why = stop == ExpectedStop.FieldRotation
                        ? string.Format(CultureInfo.InvariantCulture, "its {0:0.##} s subs already exceed the field-rotation limit at its turn ({1}), so the Stop policy ends its block at once", t.ExposureSeconds, Format(imagingStart))
                        : $"its block stops at once at its turn ({Format(imagingStart)}): {Describe(stop)}";
                    results.Add(NotImaged(why) with { Stop = ExpectedStop.NotImaged });
                    continue;
                }
                results.Add(new RunResult { Turn = turn, ImagingStart = imagingStart, ImagingEnd = imagingEnd, Stop = stop, TurnSetBy = setBy });
            }
            return results;
        }

        private void Report(Sampled s, RunResult run, DateTime start, DateTime end, List<PlanIssue> issues) {
            var t = s.Plan;
            var w = s.Window;
            if (w.Start == null) {
                issues.Add(new PlanIssue(PlanIssueSeverity.Warning, t.Name,
                    string.Format(CultureInfo.InvariantCulture, "Not observable tonight between {0} and {1} (peak altitude {2:0.0}° at {3}, horizon offset {4:0.#}°, max altitude {5:0.#}°)",
                        Format(start), Format(end), w.PeakAltitudeDeg, Format(w.PeakTime), t.HorizonOffsetDeg, t.MaxAltitudeDeg)));
            }
            if (w.KeyholeStart != null) {
                string policy;
                if (t.Keyhole == KeyholePolicy.Skip) {
                    policy = "; imaging stops when it climbs above";
                } else if (run.Stop == ExpectedStop.FieldRotation && run.ImagingEnd != null && run.ImagingEnd <= w.KeyholeEnd) {
                    policy = string.Format(CultureInfo.InvariantCulture, "; the field-rotation Stop policy ends its imaging at {0}, before the keyhole is over, and the block does not resume after it", Format(run.ImagingEnd.Value));
                } else {
                    policy = "; imaging pauses until it has sunk below again, then the target is centred again";
                }
                issues.Add(new PlanIssue(PlanIssueSeverity.Warning, t.Name,
                    string.Format(CultureInfo.InvariantCulture, "Above the {0:0.#}° maximum altitude (zenith keyhole) from {1} to {2}{3}",
                        t.MaxAltitudeDeg, Format(w.KeyholeStart.Value), Format(w.KeyholeEnd.Value), policy)));
            }
            if (w.RotationLimitedStart != null) {
                issues.Add(new PlanIssue(PlanIssueSeverity.Warning, t.Name,
                    string.Format(CultureInfo.InvariantCulture, "{0:0.##} s subs exceed the {1:0.#} px field-rotation limit from {2} to {3} (shortest max sub {4:0.0} s){5}",
                        t.ExposureSeconds, t.BlurTolerancePx, Format(w.RotationLimitedStart.Value), Format(w.RotationLimitedEnd.Value), w.ShortestMaxSubSeconds,
                        t.FieldRotation == FieldRotationPolicy.Stop ? "; imaging stops there" : "")));
            }
            if (run.HoldsUntilDawn != null) {
                var late = w.End != null && run.Turn > w.End
                    ? string.Format(CultureInfo.InvariantCulture, " (its window ended at {0}, before its turn: {1})", Format(w.End.Value), run.TurnSetBy)
                    : string.Empty;
                issues.Add(new PlanIssue(PlanIssueSeverity.Warning, t.Name,
                    string.Format(CultureInfo.InvariantCulture,
                        "Holds the sequence until dawn ({0}): from its turn at {1} it waits to {2}, which it does not do tonight{3}; NINA's validation does not skip such a wait, so nothing after it runs before dawn",
                        Format(end), Format(run.Turn.Value), run.HoldsUntilDawn, late)));
            }
            if (run.ImagingStart == null && w.Start != null && run.HoldsUntilDawn == null) {
                var turn = run.Turn ?? end;
                var message = turn >= end || turn > w.End
                    ? string.Format(CultureInfo.InvariantCulture, "Never reached: {0}, after this target's window ends at {1}", run.TurnSetBy, Format(w.End.Value))
                    : "Skipped: " + run.NotImagedBecause;
                issues.Add(new PlanIssue(PlanIssueSeverity.Warning, t.Name, message));
            }
        }

        private static string Describe(ExpectedStop stop) => stop switch {
            ExpectedStop.Horizon => "it is below its horizon",
            ExpectedStop.MinimumAltitude => "it is below its minimum altitude",
            ExpectedStop.Keyhole => "it is above its maximum altitude",
            ExpectedStop.FieldRotation => "its subs exceed the field-rotation limit",
            ExpectedStop.Dawn => "dawn",
            _ => stop.ToString()
        };

        private static int FirstIndexAtOrAfter(IReadOnlyList<Point> points, DateTime time) {
            for (var i = 0; i < points.Count; i++) {
                if (points[i].Time >= time) {
                    return i;
                }
            }
            return -1;
        }

        private static int FindFrom(IReadOnlyList<Point> points, int from, Func<Point, bool> predicate) {
            for (var i = from; i < points.Count; i++) {
                if (predicate(points[i])) {
                    return i;
                }
            }
            return -1;
        }

        private static string Format(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
