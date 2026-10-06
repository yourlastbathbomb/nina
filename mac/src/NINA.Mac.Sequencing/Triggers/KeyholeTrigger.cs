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
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Sequencing.Triggers {

    /// <summary>
    /// Trigger for the "wait out the zenith keyhole" policy of an alt-az mount (MAC_PORT_PLAN.md section 6, decision 5): before a
    /// light frame, when the target is above <see cref="MaxAltitude"/>, it runs its instructions, which the Target form generator
    /// fills with a WaitForAltitude below the limit and then a Center (or a slew when the target is not centred). The target is
    /// therefore imaged east of the meridian, waits out the keyhole, is centred again, and is imaged west of it, all in one
    /// imaging block with one exposure count. NINA has no such trigger; it follows upstream's CustomTrigger for its instruction
    /// list (the list runs against an isolated context that carries the deep-sky object's target, so the instructions take the
    /// target's coordinates and never re-trigger this trigger).
    /// <list type="bullet">
    /// <item>The check uses the target's computed altitude (NINA.Astrometry's transform, the profile's site, now), the same
    /// unrounded value WaitForAltitude compares, not the mount's reported altitude: no polling lag, so no light frame starts
    /// inside the keyhole and the recentre is never missed.</item>
    /// <item>The mount keeps tracking the target through the keyhole while the instructions wait. Stopping it (":AL#") is not
    /// done: whether the LX200GPS keeps its alignment over ":AL#"/":AA#" is an open bench question, and firmware 4.0g cannot report
    /// a lost alignment (":GW#" is not answered). The re-centre after the wait corrects the pointing.</item>
    /// <item>Without a deep-sky object parent the trigger never fires and reports a validation issue.</item>
    /// </list>
    /// Sequences that contain it serialise it as <c>NINA.Mac.Sequencing.Triggers.KeyholeTrigger, NINA.Mac.Sequencing</c>; Windows
    /// NINA loads such an entry as an unknown trigger, which never fires.
    /// </summary>
    [ExportMetadata("Name", "Wait out the zenith keyhole")]
    [ExportMetadata("Description", "Before a light frame, when the target is above the maximum altitude, runs its instructions: wait until the target has sunk below the limit, then centre it again (alt-az mount)")]
    [ExportMetadata("Icon", "WaitForAltitudeSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Telescope")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class KeyholeTrigger : SequenceTrigger, IValidatable {

        /// <summary>Plan decision 5: a fixed 75 degree limit.</summary>
        public const double DefaultMaxAltitude = 75;

        private readonly IProfileService profileService;
        private double maxAltitude = DefaultMaxAltitude;
        private IList<string> issues = new List<string>();

        [ImportingConstructor]
        public KeyholeTrigger(IProfileService profileService) : base() {
            this.profileService = profileService;
        }

        private KeyholeTrigger(KeyholeTrigger cloneMe) : this(cloneMe.profileService) {
            CopyMetaData(cloneMe);
            MaxAltitude = cloneMe.MaxAltitude;
            TriggerRunner = (SequentialContainer)cloneMe.TriggerRunner.Clone();
        }

        public override object Clone() {
            return new KeyholeTrigger(this);
        }

        /// <summary>The keyhole limit in degrees (0-90]: above it no light frame starts.</summary>
        [JsonProperty]
        public double MaxAltitude {
            get => maxAltitude;
            set {
                maxAltitude = value;
                RaisePropertyChanged();
            }
        }

        public IList<string> Issues {
            get => issues;
            set {
                issues = value.ToList();
                RaisePropertyChanged();
            }
        }

        /// <summary>The target's altitude now, degrees (NaN without a deep-sky object parent).</summary>
        public double CurrentAltitude() {
            var coordinates = ItemUtility.RetrieveContextCoordinates(Parent)?.Coordinates;
            if (coordinates == null || profileService == null) {
                return double.NaN;
            }
            var astrometry = profileService.ActiveProfile.AstrometrySettings;
            return coordinates.Transform(Angle.ByDegree(astrometry.Latitude), Angle.ByDegree(astrometry.Longitude), astrometry.Elevation, DateTime.Now).Altitude.Degree;
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (nextItem is not IExposureItem exposure || exposure.ImageType != "LIGHT") {
                return false;
            }
            var altitude = CurrentAltitude();
            var above = !double.IsNaN(altitude) && altitude > MaxAltitude;
            if (above) {
                Logger.Info(string.Format(CultureInfo.InvariantCulture, "{0}: target at {1:0.000}° is above the {2:0.##}° keyhole limit; waiting it out before the next light frame", nameof(KeyholeTrigger), altitude, MaxAltitude));
            }
            return above;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var originalParent = TriggerRunner.Parent;
            // As upstream's CustomTrigger: the instructions run against an isolated context proxy that carries the target and the
            // root, so they find the target's coordinates but never walk back into this trigger set and trigger it again
            var runtimeParent = ItemUtility.CreateTriggerRunnerContext(context ?? Parent);
            if (!ReferenceEquals(TriggerRunner.Parent, runtimeParent)) {
                TriggerRunner.AttachNewParent(runtimeParent);
            }
            try {
                TriggerRunner.ResetAll();
                await TriggerRunner.Run(progress, token);
            } finally {
                if (!ReferenceEquals(TriggerRunner.Parent, originalParent)) {
                    TriggerRunner.AttachNewParent(originalParent);
                }
            }
        }

        [OnDeserialized]
        public void OnDeserialized(StreamingContext context) {
            AttachTriggerRunnerToContext();
        }

        public override void AfterParentChanged() {
            AttachTriggerRunnerToContext();
            Validate();
        }

        private void AttachTriggerRunnerToContext() {
            TriggerRunner?.AttachNewParent(ItemUtility.CreateTriggerRunnerContext(Parent));
        }

        public bool Validate() {
            var i = new List<string>();
            if (Parent != null && ItemUtility.RetrieveContextCoordinates(Parent) == null) {
                i.Add("The keyhole trigger needs a target: place it inside a deep-sky object (target) container");
            }
            if (!(MaxAltitude > 0 && MaxAltitude <= 90)) {
                i.Add(string.Format(CultureInfo.InvariantCulture, "The keyhole limit must be in (0, 90] degrees, not {0}", MaxAltitude));
            }
            if (TriggerRunner != null) {
                TriggerRunner.Validate();
                if (TriggerRunner.Issues != null) {
                    i.AddRange(TriggerRunner.Issues);
                }
            }
            Issues = i;
            return i.Count == 0;
        }

        public override string ToString() {
            return string.Format(CultureInfo.InvariantCulture, "Trigger: {0}, Altitude > {1}", nameof(KeyholeTrigger), MaxAltitude);
        }
    }
}
