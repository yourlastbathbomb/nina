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
    }

    public sealed record PlanReport(DateTime Dusk, DateTime Dawn, IReadOnlyList<TargetWindow> Windows, IReadOnlyList<PlanIssue> Issues) {
        public bool HasErrors => Issues.Any(i => i.Severity == PlanIssueSeverity.Error);
    }

    /// <summary>
    /// Checks a <see cref="NightPlan"/> against tonight's sky before it becomes a sequence (plan section 5.3). Each target is
    /// sampled once a minute between dusk and the night's dawn stop with NINA.Astrometry's own transform
    /// (Coordinates.Transform to topocentric altitude and azimuth) and the profile's custom horizon, the same models the
    /// sequence's AboveHorizonCondition, AltitudeCondition and MaxAltitudeCondition use; field rotation comes from
    /// NINA.Mac.RigTools. It reports, per target: the window; the zenith keyhole; where the exposure exceeds the field-rotation
    /// max sub (decision 4: warn at 1 px of corner blur); targets that never become observable; and targets that cannot be
    /// reached because an earlier target without an exposure count runs past the end of their window. These are warnings: the
    /// only errors are plans the generator would refuse. Dusk and dawn come from NINA's NighttimeCalculator.
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
            var horizon = astrometry.Horizon;
            foreach (var t in plan.Targets) {
                if (t.Coordinates == null || string.IsNullOrWhiteSpace(t.Name) || !(t.ExposureSeconds > 0)) {
                    issues.Add(new PlanIssue(PlanIssueSeverity.Error, t.Name, "Needs a name, coordinates and an exposure longer than 0 s"));
                    continue;
                }
                windows.Add(Window(t, start, end, astrometry.Latitude, astrometry.Longitude, astrometry.Elevation, horizon, issues));
            }

            // A target after one that has no exposure count only starts when that one's window is over
            DateTime? busyUntil = null;
            string busyWith = null;
            for (var i = 0; i < windows.Count; i++) {
                var w = windows[i];
                var t = plan.Targets.First(x => x.Name == w.Target);
                if (busyUntil != null && w.End != null && w.End <= busyUntil) {
                    issues.Add(new PlanIssue(PlanIssueSeverity.Warning, w.Target, $"Never reached: {busyWith} has no exposure count and images until {Format(busyUntil.Value)}, after this target's window ends at {Format(w.End.Value)}"));
                }
                if (t.Count == null && w.End != null && (busyUntil == null || w.End > busyUntil)) {
                    busyUntil = w.End;
                    busyWith = w.Target;
                }
            }
            return new PlanReport(start, end, windows, issues);
        }

        private TargetWindow Window(TargetPlan t, DateTime start, DateTime end, double latitude, double longitude, double elevation, CustomHorizon horizon, List<PlanIssue> issues) {
            var coordinates = t.Coordinates.Transform(Epoch.J2000);
            var train = new ImagingTrain(t.Name, SensorWidthPx, SensorHeightPx, ImagingTrain.Asi585PixelSizeUm, ImagingTrain.NativeFocalLengthMm, Math.Max((short)1, t.Binning));
            DateTime? first = null, last = null, keyFirst = null, keyLast = null, rotFirst = null, rotLast = null;
            var peak = double.NegativeInfinity;
            var peakTime = start;
            var shortest = double.PositiveInfinity;
            for (var time = start; time <= end; time = time.AddMinutes(1)) {
                var altaz = coordinates.Transform(Angle.ByDegree(latitude), Angle.ByDegree(longitude), elevation, time);
                var altitude = altaz.Altitude.Degree;
                var azimuth = altaz.Azimuth.Degree;
                if (altitude > peak) {
                    peak = altitude;
                    peakTime = time;
                }
                if (altitude > t.MaxAltitudeDeg) {
                    keyFirst ??= time;
                    keyLast = time;
                }
                var horizonAltitude = (horizon?.GetAltitude(azimuth) ?? 0) + t.HorizonOffsetDeg;
                var observable = altitude >= horizonAltitude && altitude <= t.MaxAltitudeDeg && (t.MinAltitudeDeg == null || altitude >= t.MinAltitudeDeg);
                if (!observable) {
                    continue;
                }
                first ??= time;
                last = time;
                var maxSub = RigTools.Rotation.FieldRotation.MaxSubSeconds(latitude, altitude, azimuth, train, t.BlurTolerancePx);
                shortest = Math.Min(shortest, maxSub);
                if (t.ExposureSeconds > maxSub) {
                    rotFirst ??= time;
                    rotLast = time;
                }
            }

            if (first == null) {
                issues.Add(new PlanIssue(PlanIssueSeverity.Warning, t.Name,
                    string.Format(CultureInfo.InvariantCulture, "Not observable tonight between {0} and {1} (peak altitude {2:0.0}° at {3}, horizon offset {4:0.#}°, max altitude {5:0.#}°)",
                        Format(start), Format(end), peak, Format(peakTime), t.HorizonOffsetDeg, t.MaxAltitudeDeg)));
            }
            if (keyFirst != null) {
                issues.Add(new PlanIssue(PlanIssueSeverity.Warning, t.Name,
                    string.Format(CultureInfo.InvariantCulture, "Above the {0:0.#}° maximum altitude (zenith keyhole) from {1} to {2}{3}",
                        t.MaxAltitudeDeg, Format(keyFirst.Value), Format(keyLast.Value),
                        t.Keyhole == KeyholePolicy.Skip ? "; imaging stops when it climbs above" : "; imaging pauses until it has sunk below again")));
            }
            if (rotFirst != null) {
                issues.Add(new PlanIssue(PlanIssueSeverity.Warning, t.Name,
                    string.Format(CultureInfo.InvariantCulture, "{0:0.##} s subs exceed the {1:0.#} px field-rotation limit from {2} to {3} (shortest max sub {4:0.0} s){5}",
                        t.ExposureSeconds, t.BlurTolerancePx, Format(rotFirst.Value), Format(rotLast.Value), shortest,
                        t.FieldRotation == FieldRotationPolicy.Stop ? "; imaging stops there" : "")));
            }
            return new TargetWindow {
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
        }

        private static string Format(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
