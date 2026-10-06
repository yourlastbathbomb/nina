#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Generators;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using static NINA.Sequencer.Utility.ItemUtility;

namespace NINA.Mac.Sequencing.Conditions {

    /// <summary>
    /// Loop condition that ends a target's imaging when it climbs above a maximum altitude: the zenith keyhole of an alt-az mount
    /// (MAC_PORT_PLAN.md section 6 and decision 5, default a fixed 75 degrees). NINA has no such condition. It mirrors upstream
    /// NINA.Sequencer/Conditions/AltitudeCondition.cs with the comparison inverted:
    /// <list type="bullet">
    /// <item>Check is <c>CurrentAltitude &lt;= MaxAltitude</c>, with no rising/setting exemption: near the zenith an alt-az mount's
    /// azimuth rate diverges and field rotation peaks, in both directions.</item>
    /// <item>The target's coordinates come from the enclosing deep-sky object container (ItemUtility.RetrieveContextCoordinates),
    /// exactly as AltitudeCondition takes them, and are refreshed on every check; without such a parent it reports a validation
    /// issue and never stops anything (no coordinates, altitude unknown).</item>
    /// <item>The watchdog of LoopForAltitudeBase checks every 5 s while the sequence runs and interrupts the running instruction
    /// set (an exposure in progress included) as soon as the check fails.</item>
    /// <item>The expected stop time is NINA's own estimate (ItemUtility.CalculateExpectedTimeCommon with GREATER_THAN and
    /// until = true): when the target climbs above the limit. Because the class is not named "AltitudeCondition", the rising
    /// exemption of that estimate does not apply.</item>
    /// </list>
    /// It uses the target's computed altitude, not the mount's reported one, so it predicts before the slew and keeps working
    /// while the mount is disconnected (unlike <c>LoopWhile Mount_Altitude &lt;= 75</c>, which throws on an uninitialised symbol).
    /// Sequences that contain it serialise it as <c>NINA.Mac.Sequencing.Conditions.MaxAltitudeCondition, NINA.Mac.Sequencing</c>;
    /// Windows NINA loads such an entry as an unknown condition.
    /// </summary>
    [ExportMetadata("Name", "Loop until above max altitude")]
    [ExportMetadata("Description", "Loops while the target is at or below the maximum altitude, and interrupts the instruction set when it climbs above it (zenith keyhole of an alt-az mount)")]
    [ExportMetadata("Icon", "WaitForAltitudeSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Condition")]
    [Export(typeof(ISequenceCondition))]
    [JsonObject(MemberSerialization.OptIn)]
    [UsesExpressions(GenerateValidation = true)]
    public partial class MaxAltitudeCondition : LoopForAltitudeBase, IValidatable {

        /// <summary>Plan decision 5: a fixed 75 degree limit.</summary>
        public const double DefaultMaxAltitude = 75;

        private bool hasDsoParent;

        [ImportingConstructor]
        public MaxAltitudeCondition(IProfileService profileService) : base(profileService, useCustomHorizon: false) {
            Data.Offset = DefaultMaxAltitude;
            Data.Comparator = ComparisonOperatorEnum.GREATER_THAN;
        }

        private MaxAltitudeCondition(MaxAltitudeCondition cloneMe) : this(cloneMe.ProfileService) {
            CopyMetaData(cloneMe);
        }

        // As AltitudeCondition: the generated Clone calls this on the original with the new instance
        partial void AfterClone(MaxAltitudeCondition clone) {
            clone.Data = Data.Clone();
            clone.Data.Coordinates = Data.Coordinates?.Clone();
        }

        [IsExpression(Default = DefaultMaxAltitude, Range = [0, 90], Proxy = "Data.Offset")]
        public partial double MaxAltitude { get; set; }

        [JsonProperty]
        public bool HasDsoParent {
            get => hasDsoParent;
            set {
                hasDsoParent = value;
                RaisePropertyChanged();
            }
        }

        public override void AfterParentChanged() {
            var coordinates = RetrieveContextCoordinates(this.Parent);
            if (coordinates != null) {
                Data.Coordinates.Coordinates = coordinates.Coordinates;
                HasDsoParent = true;
            } else {
                HasDsoParent = false;
            }
            Validate();
            RunWatchdogIfInsideSequenceRoot();
        }

        public override bool Check(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (!HasDsoParent) {
                // No target, so no altitude: never the reason a loop stops (Validate reports the problem)
                return true;
            }
            var coordinates = RetrieveContextCoordinates(this.Parent)?.Coordinates;
            if (coordinates != null) {
                Data.Coordinates.Coordinates = coordinates;
            }

            CalculateExpectedTime();
            var check = Data.CurrentAltitude <= MaxAltitude;
            if (!check && IsActive()) {
                InterruptReason = $"Target above the maximum altitude ({Data.CurrentAltitude:0.00}° > {MaxAltitude:0.00}°)";
                Logger.Info($"{nameof(MaxAltitudeCondition)} finished. Current / Max: {Data.CurrentAltitude}° / {MaxAltitude}°");
            }
            return check;
        }

        public double GetCurrentAltitude(DateTime time, ObserverInfo observer) {
            var altaz = Data.Coordinates.Coordinates.Transform(Angle.ByDegree(observer.Latitude), Angle.ByDegree(observer.Longitude), observer.Elevation, time);
            return altaz.Altitude.Degree;
        }

        public override void CalculateExpectedTime() {
            _ = MaxAltitude; // Refresh the limit consumed by the shared altitude calculator (Data.Offset)
            Data.CurrentAltitude = GetCurrentAltitude(DateTime.Now, Data.Observer);
            CalculateExpectedTimeCommon(Data, until: true, 30, GetCurrentAltitude);
        }

        partial void ValidateAdditional(IList<string> issues) {
            if (!HasDsoParent) {
                issues.Add("The maximum-altitude condition needs a target: place it inside a deep-sky object (target) container");
            }
        }

        public override string ToString() {
            return $"Condition: {nameof(MaxAltitudeCondition)}, Altitude <= {MaxAltitude}";
        }
    }
}
