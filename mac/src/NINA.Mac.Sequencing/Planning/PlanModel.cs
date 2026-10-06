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
using System;
using System.Collections.Generic;

namespace NINA.Mac.Sequencing.Planning {

    /// <summary>What to do when a target passes through the zenith keyhole (above <see cref="TargetPlan.MaxAltitudeDeg"/>).</summary>
    public enum KeyholePolicy {

        /// <summary>
        /// Plan default: stop imaging the target when it climbs above the limit (MaxAltitudeCondition, which also interrupts the
        /// running exposure), or skip it if it is already above.
        /// </summary>
        Skip,

        /// <summary>
        /// Image east of the meridian, wait out the keyhole, image west of it (MAC_PORT_PLAN.md section 6): a WaitForAltitude
        /// below the limit before centring and before every exposure holds the target's loop while it is above the limit, so one
        /// exposure count covers both sides. An exposure that starts just below the limit is not interrupted; the drift trigger
        /// (if on) recentres after the wait.
        /// </summary>
        WaitUntilBelow
    }

    /// <summary>Field-rotation policy (MAC_PORT_PLAN.md decision 4).</summary>
    public enum FieldRotationPolicy {

        /// <summary>Plan default: nothing is added to the sequence; the validator warns and FieldRotation_MaxSub shows the limit live.</summary>
        Warn,

        /// <summary>
        /// Stop the target's imaging block when rotation outpaces the sub length: a LoopWhile on
        /// <c>FieldRotation_MaxSub &gt;= exposure</c>, or <c>FieldRotation_MaxSub * B &gt;= exposure</c> for a blur tolerance B other than
        /// 1 px (the symbols of <see cref="FieldRotation.FieldRotationSymbols"/>, published for 1 px). The target is not resumed later.
        /// </summary>
        Stop
    }

    /// <summary>Which morning twilight ends the night.</summary>
    public enum DawnStop {

        /// <summary>Sun 18 degrees below the horizon (NINA's DawnProvider). Plan default.</summary>
        Astronomical,

        /// <summary>Sun 12 degrees below (NauticalDawnProvider).</summary>
        Nautical,

        /// <summary>Sun 6 degrees below (CivilDawnProvider).</summary>
        Civil
    }

    /// <summary>
    /// One target of the Target form: what, how long, how often to dither and recentre, and when to stop. Defaults are the rig's
    /// (MAC_PORT_PLAN.md: bin 2, dither every 5, recentre beyond 1.5', max altitude 75 degrees, warn at 1 px of corner blur).
    /// </summary>
    public sealed record TargetPlan {

        /// <summary>Target name (FITS OBJECT, the $$TARGETNAME$$ file pattern).</summary>
        public string Name { get; init; }

        /// <summary>J2000 coordinates (from the deep-sky database or entered by hand).</summary>
        public Coordinates Coordinates { get; init; }

        public double PositionAngle { get; init; }

        public double ExposureSeconds { get; init; } = 10;

        /// <summary>-1 = the camera's default gain (NINA's convention).</summary>
        public int Gain { get; init; } = -1;

        /// <summary>-1 = the camera's default offset.</summary>
        public int Offset { get; init; } = -1;

        public short Binning { get; init; } = 2;

        /// <summary>Number of exposures; null = until a stop condition ends the block.</summary>
        public int? Count { get; init; }

        /// <summary>Dither after every N light frames (DitherAfterExposures.AfterExposures); 0 = no dithering.</summary>
        public int DitherEvery { get; init; } = 5;

        /// <summary>Centre the target (plate solve, sync, re-slew) before imaging. When false the block only slews.</summary>
        public bool CenterFirst { get; init; } = true;

        /// <summary>Recentre when a drift check finds the frame more than this far off (CenterAfterDriftTrigger); 0 = no drift checks.</summary>
        public double RecenterArcmin { get; init; } = 1.5;

        /// <summary>Plate solve every Nth light frame for the drift check.</summary>
        public int RecenterEvery { get; init; } = 5;

        /// <summary>Altitude above the profile's custom horizon (degrees) at which the target counts as risen.</summary>
        public double HorizonOffsetDeg { get; init; }

        /// <summary>Optional lower limit in addition to the horizon (AltitudeCondition / WaitForAltitude).</summary>
        public double? MinAltitudeDeg { get; init; }

        /// <summary>Zenith keyhole limit (MaxAltitudeCondition); decision 5: a fixed 75 degrees.</summary>
        public double MaxAltitudeDeg { get; init; } = 75;

        public KeyholePolicy Keyhole { get; init; } = KeyholePolicy.Skip;

        public FieldRotationPolicy FieldRotation { get; init; } = FieldRotationPolicy.Warn;

        /// <summary>Allowed field-rotation blur at the frame corner, in binned pixels (decision 4: 1 px).</summary>
        public double BlurTolerancePx { get; init; } = 1.0;
    }

    /// <summary>
    /// A night: the start (cool, unpark), the targets in order, and the end (warm, park, optional script). The whole night stops at
    /// the chosen dawn twilight plus <see cref="DawnOffsetMinutes"/>.
    /// </summary>
    public sealed record NightPlan {

        public string Name { get; init; } = "Night";

        public IReadOnlyList<TargetPlan> Targets { get; init; } = Array.Empty<TargetPlan>();

        /// <summary>Cooler set point in degrees C; null = leave the cooler alone.</summary>
        public double? CoolToC { get; init; } = 0;

        /// <summary>Minutes over which to cool (CoolCamera.Duration) and warm (WarmCamera.Duration).</summary>
        public double CoolMinutes { get; init; } = 5;

        public double WarmMinutes { get; init; } = 5;

        /// <summary>Switch the ASI anti-dew heater on at the start and off at the end.</summary>
        public bool DewHeater { get; init; } = true;

        public bool UnparkAtStart { get; init; } = true;

        public DawnStop Dawn { get; init; } = DawnStop.Astronomical;

        /// <summary>Minutes added to the dawn time (negative = stop earlier).</summary>
        public int DawnOffsetMinutes { get; init; }

        public bool WarmAtEnd { get; init; } = true;

        /// <summary>Park at the end (the LX200 driver's soft park; never :hP#).</summary>
        public bool ParkAtEnd { get; init; } = true;

        /// <summary>Reconnect the camera and retry once when a download fails (ReconnectOnDownloadFailure on the root).</summary>
        public bool ReconnectOnDownloadFailure { get; init; } = true;

        /// <summary>Optional command line run at the end (ExternalScript), e.g. siril-cli with the night's script (decision 8).</summary>
        public string EndScript { get; init; }
    }
}
