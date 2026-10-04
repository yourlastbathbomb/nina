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
using NINA.Mac.RigTools.Horizon;
using NINA.Mac.RigTools.Optics;
using NINA.Mac.RigTools.Rotation;
using System;
using System.Collections.Generic;

namespace NINA.Mac.RigTools.Planning {

    /// <summary>Planner settings. Defaults are the rig's (MAC_PORT_PLAN.md sections 6 and 9).</summary>
    public sealed class NightPlanOptions {

        public Site Site { get; init; } = Site.DeepWaterBay;

        /// <summary>Obstruction profile. Default: the shipped PLACEHOLDER "no northern sky" profile; measure the real one.</summary>
        public HorizonProfile Horizon { get; init; } = SiteHorizons.DeepWaterBayPlaceholder;

        /// <summary>Lowest useful altitude regardless of obstructions (airmass ~3.9 at 15 deg).</summary>
        public double MinAltitudeDeg { get; init; } = 15.0;

        /// <summary>Zenith keyhole (decision 5 default: fixed 75 deg). Objects near Dec +22 pass overhead at 22.25 N.</summary>
        public double MaxAltitudeDeg { get; init; } = 75.0;

        /// <summary>Sun altitude that counts as dark: -18 astronomical (default), -12 nautical, -6 civil.</summary>
        public double SunAltitudeDeg { get; init; } = LowPrecisionSun.AstronomicalTwilightDeg;

        /// <summary>Optical train for the field-rotation limit (default native f/10, bin 2x2; the limit is the same with the reducer).</summary>
        public ImagingTrain Train { get; init; } = ImagingTrain.Asi585Native(2);

        /// <summary>Allowed corner blur in binned pixels (decision 4 default: warn at 1 px).</summary>
        public double AllowedBlurPx { get; init; } = FieldRotation.DefaultAllowedBlurPx;

        /// <summary>
        /// If set, times whose field-rotation max sub is below this are treated as unusable (decision 5's
        /// "limit by field rotation" alternative). Null (default) only warns.
        /// </summary>
        public double? MinSubSeconds { get; init; }

        /// <summary>Dark-library exposure steps (decision 7: 10/20/30 s, +5 s). The recommended sub is the longest step within the limit.</summary>
        public IReadOnlyList<double> ExposureStepsSeconds { get; init; } = new[] { 5.0, 10.0, 20.0, 30.0 };

        /// <summary>Fine sampling step for window edges and statistics; edges are then refined to 0.5 s.</summary>
        public int SampleSeconds { get; init; } = 60;

        /// <summary>Spacing of the per-target report samples.</summary>
        public int ReportIntervalMinutes { get; init; } = 30;

        internal void Validate() {
            if (Site == null) { throw new ArgumentException("Site is required"); }
            if (Horizon == null) { throw new ArgumentException("Horizon is required (use HorizonProfile.Flat() for none)"); }
            if (Train == null) { throw new ArgumentException("Train is required"); }
            if (!(MinAltitudeDeg >= -90 && MinAltitudeDeg < MaxAltitudeDeg && MaxAltitudeDeg <= 90)) {
                throw new ArgumentException("Need -90 <= MinAltitudeDeg < MaxAltitudeDeg <= 90");
            }
            if (!(SunAltitudeDeg >= -90 && SunAltitudeDeg <= 90)) { throw new ArgumentException("SunAltitudeDeg out of range"); }
            if (!(AllowedBlurPx > 0)) { throw new ArgumentException("AllowedBlurPx must be positive"); }
            if (SampleSeconds < 5 || SampleSeconds > 600) { throw new ArgumentException("SampleSeconds must be within [5, 600]"); }
            if (ReportIntervalMinutes < 1) { throw new ArgumentException("ReportIntervalMinutes must be positive"); }
            if (ExposureStepsSeconds == null || ExposureStepsSeconds.Count == 0) { throw new ArgumentException("ExposureStepsSeconds is empty"); }
        }
    }

    /// <summary>Reasons a moment is not usable, or why a window starts/ends there.</summary>
    [Flags]
    public enum PlanConstraint {
        None = 0,
        Darkness = 1,
        Horizon = 2,
        MinAltitude = 4,
        MaxAltitude = 8,
        FieldRotation = 16,
        Meridian = 32
    }

    [Flags]
    public enum TargetFlags {
        None = 0,

        /// <summary>Transit altitude below 0 at this latitude (Dec &lt; latitude - 90).</summary>
        NeverRises = 1,

        /// <summary>Never sets below the geometric horizon (Dec &gt; 90 - latitude).</summary>
        Circumpolar = 2,

        /// <summary>Rises, but never clears the horizon profile and the minimum altitude at any hour angle.</summary>
        BlockedByHorizon = 4,

        /// <summary>Clears the limits at some hour angle, but not during tonight's darkness.</summary>
        NotUpInDarkness = 8,

        /// <summary>Transit altitude above the max altitude: the object passes through the zenith keyhole.</summary>
        PassesZenithKeyhole = 16,

        /// <summary>The Sun never reaches the darkness limit this night.</summary>
        NoDarkness = 32,

        /// <summary>Some window's field-rotation limit drops below the shortest exposure step.</summary>
        RotationLimited = 64
    }

    public enum MeridianSide {
        East,
        West
    }

    /// <summary>One moment of a target's night.</summary>
    public sealed class PlanSample {
        public DateTimeOffset Time { get; init; }
        public double AltitudeDeg { get; init; }
        public double AzimuthDeg { get; init; }

        /// <summary>Hour angle in (-12, 12]; negative = east of the meridian.</summary>
        public double HourAngleHours { get; init; }

        public double HorizonAltitudeDeg { get; init; }
        public double ParallacticAngleDeg { get; init; }

        /// <summary>Signed field-rotation rate (deg/h).</summary>
        public double RotationRateDegPerHour { get; init; }

        /// <summary>Longest sub for the allowed corner blur; +infinity when the rate is zero.</summary>
        public double MaxSubSeconds { get; init; }

        /// <summary>Longest exposure step not above <see cref="MaxSubSeconds"/>, or null if even the shortest is too long.</summary>
        public double? RecommendedSubSeconds { get; init; }

        public PlanConstraint Violations { get; init; }
        public bool Usable => Violations == PlanConstraint.None;
        public MeridianSide Side => HourAngleHours < 0 ? MeridianSide.East : MeridianSide.West;
    }

    /// <summary>A continuous stretch of usable time on one side of the meridian.</summary>
    public sealed class ImagingWindow {
        public MeridianSide Side { get; init; }
        public DateTimeOffset Start { get; init; }
        public DateTimeOffset End { get; init; }
        public TimeSpan Duration => End - Start;

        /// <summary>What ends the unusable time before the window (Darkness when it opens at dusk).</summary>
        public PlanConstraint StartLimit { get; init; }

        /// <summary>What closes the window (Meridian for the east half of a split, Darkness at dawn, ...).</summary>
        public PlanConstraint EndLimit { get; init; }

        public double StartAltitudeDeg { get; init; }
        public double EndAltitudeDeg { get; init; }
        public double PeakAltitudeDeg { get; init; }

        /// <summary>Worst (shortest) field-rotation max sub anywhere in the window, and when it occurs.</summary>
        public double ShortestMaxSubSeconds { get; init; }

        public DateTimeOffset ShortestMaxSubAt { get; init; }

        /// <summary>One fixed exposure for the whole window: the longest step within the worst-case limit, or null.</summary>
        public double? RecommendedSubSeconds { get; init; }

        /// <summary>Span of frame rotation on the sky over the window (deg).</summary>
        public double FrameRotationDeg { get; init; }
    }

    public sealed class TargetPlan {
        public PlanTarget Target { get; init; }

        /// <summary>Position precessed to the middle of the night (mean equinox of date).</summary>
        public EquatorialCoordinates OfDate { get; init; }

        public TargetFlags Flags { get; init; }

        /// <summary>Transit (upper culmination) nearest to the middle of the night, site local time.</summary>
        public DateTimeOffset Transit { get; init; }

        public double TransitAltitudeDeg { get; init; }
        public double TransitAzimuthDeg { get; init; }
        public bool TransitInDarkness { get; init; }

        /// <summary>When the object is above the max altitude around this transit (null unless it passes the keyhole).</summary>
        public (DateTimeOffset From, DateTimeOffset To)? Keyhole { get; init; }

        /// <summary>Time per transit above the minimum altitude, ignoring the horizon profile and the Sun.</summary>
        public TimeSpan TimeAboveMinAltitude { get; init; }

        public IReadOnlyList<ImagingWindow> Windows { get; init; } = Array.Empty<ImagingWindow>();

        /// <summary>Report samples every <see cref="NightPlanOptions.ReportIntervalMinutes"/> while the object is above the geometric horizon in darkness.</summary>
        public IReadOnlyList<PlanSample> Samples { get; init; } = Array.Empty<PlanSample>();

        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

        public TimeSpan UsableTime {
            get {
                var total = TimeSpan.Zero;
                foreach (var w in Windows) { total += w.Duration; }
                return total;
            }
        }
    }

    public sealed class NightPlan {
        public DateOnly EveningDate { get; init; }
        public NightPlanOptions Options { get; init; }

        /// <summary>Start and end of darkness (site local time), or null if the Sun never gets low enough.</summary>
        public DateTimeOffset? Dusk { get; init; }

        public DateTimeOffset? Dawn { get; init; }
        public IReadOnlyList<TargetPlan> Targets { get; init; } = Array.Empty<TargetPlan>();
    }
}
