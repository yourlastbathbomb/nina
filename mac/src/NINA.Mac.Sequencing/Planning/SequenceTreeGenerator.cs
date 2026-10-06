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
using NINA.Core.Model.Equipment;
using NINA.Equipment.Model;
using NINA.Mac.Sequencing.Conditions;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.Camera;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Trigger.Connect;
using NINA.Sequencer.Trigger.Guider;
using NINA.Sequencer.Trigger.Platesolving;
using NINA.Sequencer.Utility.DateTimeProvider;
using System;
using System.Globalization;
using System.Linq;

namespace NINA.Mac.Sequencing.Planning {

    /// <summary>
    /// Target form to NINA sequence tree. The shape follows upstream's own target template (NINA/Sequencer/Examples/Basic
    /// Sequence Target.template.json) and SimpleDSOContainer.TransformToDSOContainer, minus autofocus, rotator, guiding and
    /// meridian flip, plus the stops this alt-az rig needs (mac/docs/m7-headless-sequencer-plan.md section 5.2):
    /// <code>
    /// SequenceRootContainer                       [trigger] ReconnectOnDownloadFailure (optional)
    /// ├─ StartAreaContainer:   DewHeater(on)?  CoolCamera(T, minutes)?  UnparkScope?
    /// ├─ TargetAreaContainer
    /// │  └─ DeepSkyObjectContainer per target     (target set first; no conditions, so it runs once)
    /// │     ├─ SequentialContainer "Prepare"      [LoopCondition 1, TimeCondition dawn, MaxAltitude (keyhole Skip)]
    /// │     │     WaitUntilAboveHorizon, WaitForAltitude &gt; min?, WaitForAltitude &lt; max (keyhole WaitUntilBelow)?, Center (or a slew)
    /// │     └─ SequentialContainer "Imaging"      [LoopCondition N?, TimeCondition dawn, AboveHorizon, MaxAltitude (keyhole Skip)?,
    /// │           WaitForAltitude &lt; max?          AltitudeCondition min?, LoopWhile FieldRotation_MaxSub [* B] &gt;= T (Stop)?]
    /// │           TakeExposure LIGHT              [DitherAfterExposures k?, CenterAfterDriftTrigger arcmin/n?]
    /// └─ EndAreaContainer:     WarmCamera?  DewHeater(off)?  ParkScope?  ExternalScript?
    /// </code>
    /// The keyhole policies differ only in the Imaging block: Skip ends it when the target climbs above the maximum altitude
    /// (MaxAltitudeCondition, whose watchdog also interrupts a running exposure); WaitUntilBelow keeps it and puts a
    /// WaitForAltitude &lt; max before every exposure, which passes at once while the target is at or below the limit and
    /// otherwise holds the loop until the target has crossed the meridian and sunk below it again: the target is imaged east of
    /// the meridian, waits out the keyhole, and is imaged west of it, with one exposure count (LoopCondition) for both sides.
    /// Every entity comes from the factory (a clone of the catalogue's prototype, so it carries its services and metadata), and
    /// only the generated values are set, through the entities' own properties. Containers are attached to their parent before
    /// their children are added, so each child's AfterParentChanged sees the deep-sky object's coordinates, as when a user
    /// builds the tree in the editor. Nothing here schedules a meridian flip: the catalogue has no flip trigger.
    /// </summary>
    public sealed class SequenceTreeGenerator {
        private readonly ISequencerFactory factory;
        private readonly IProfileService profileService;

        public SequenceTreeGenerator(ISequencerFactory factory, IProfileService profileService) {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        }

        public SequenceRootContainer Generate(NightPlan plan) {
            ArgumentNullException.ThrowIfNull(plan);
            Check(plan);

            var root = Get(factory.GetContainer<SequenceRootContainer>());
            root.Name = plan.Name;
            root.SequenceTitle = plan.Name;
            if (plan.ReconnectOnDownloadFailure) {
                root.Add(Get(factory.GetTrigger<ReconnectOnDownloadFailure>()));
            }

            var start = Get(factory.GetContainer<StartAreaContainer>());
            root.Add(start);
            if (plan.DewHeater) {
                var dew = Get(factory.GetItem<DewHeater>());
                dew.OnOff = true;
                start.Add(dew);
            }
            if (plan.CoolToC is double setPoint) {
                var cool = Get(factory.GetItem<CoolCamera>());
                cool.Temperature = setPoint;
                cool.Duration = plan.CoolMinutes;
                start.Add(cool);
            }
            if (plan.UnparkAtStart) {
                start.Add(Get(factory.GetItem<UnparkScope>()));
            }

            var targets = Get(factory.GetContainer<TargetAreaContainer>());
            root.Add(targets);
            foreach (var target in plan.Targets) {
                AddTarget(targets, plan, target);
            }

            var end = Get(factory.GetContainer<EndAreaContainer>());
            root.Add(end);
            if (plan.WarmAtEnd) {
                var warm = Get(factory.GetItem<WarmCamera>());
                warm.Duration = plan.WarmMinutes;
                end.Add(warm);
            }
            if (plan.DewHeater) {
                var dew = Get(factory.GetItem<DewHeater>());
                dew.OnOff = false;
                end.Add(dew);
            }
            if (plan.ParkAtEnd) {
                end.Add(Get(factory.GetItem<ParkScope>()));
            }
            if (!string.IsNullOrWhiteSpace(plan.EndScript)) {
                var script = Get(factory.GetItem<ExternalScript>());
                script.Script = plan.EndScript;
                end.Add(script);
            }
            return root;
        }

        private void AddTarget(TargetAreaContainer targets, NightPlan plan, TargetPlan t) {
            var astrometry = profileService.ActiveProfile.AstrometrySettings;
            var dso = Get(factory.GetContainer<DeepSkyObjectContainer>());
            var input = new InputTarget(Angle.ByDegree(astrometry.Latitude), Angle.ByDegree(astrometry.Longitude), astrometry.Horizon) {
                TargetName = t.Name,
                PositionAngle = t.PositionAngle
            };
            input.InputCoordinates.Coordinates = t.Coordinates.Transform(Epoch.J2000);
            // As SimpleDSOContainer.TransformToDSOContainer: the target before any child
            dso.Target = input;
            dso.Name = t.Name;
            targets.Add(dso);

            var prepare = Get(factory.GetContainer<SequentialContainer>());
            prepare.Name = $"Prepare {t.Name}";
            dso.Add(prepare);
            var once = Get(factory.GetCondition<LoopCondition>());
            once.Iterations = 1;
            prepare.Add(once);
            prepare.Add(Dawn(plan));
            if (t.Keyhole == KeyholePolicy.Skip) {
                prepare.Add(MaxAltitude(t));
            }
            var rise = Get(factory.GetItem<WaitUntilAboveHorizon>());
            prepare.Add(rise);
            rise.Offset = t.HorizonOffsetDeg;
            if (t.MinAltitudeDeg is double minimum) {
                var above = Get(factory.GetItem<WaitForAltitude>());
                prepare.Add(above);
                above.AboveOrBelow = ">";
                above.Offset = minimum;
            }
            if (t.Keyhole == KeyholePolicy.WaitUntilBelow) {
                // Never centre in the keyhole: if the target is above the limit, wait until it has sunk below it
                AddWaitBelowMaximum(prepare, t);
            }
            if (t.CenterFirst) {
                prepare.Add(Get(factory.GetItem<Center>()));
            } else {
                prepare.Add(Get(factory.GetItem<SlewScopeToRaDec>()));
            }

            var imaging = Get(factory.GetContainer<SequentialContainer>());
            imaging.Name = string.Format(CultureInfo.InvariantCulture, "Imaging {0} {1}s", t.Name, t.ExposureSeconds);
            dso.Add(imaging);
            if (t.Count is int count) {
                var loop = Get(factory.GetCondition<LoopCondition>());
                loop.Iterations = count;
                imaging.Add(loop);
            }
            imaging.Add(Dawn(plan));
            var horizon = Get(factory.GetCondition<AboveHorizonCondition>());
            imaging.Add(horizon);
            horizon.Offset = t.HorizonOffsetDeg;
            if (t.Keyhole == KeyholePolicy.Skip) {
                imaging.Add(MaxAltitude(t));
            }
            if (t.MinAltitudeDeg is double min) {
                var altitude = Get(factory.GetCondition<AltitudeCondition>());
                imaging.Add(altitude);
                altitude.Offset = min;
            }
            if (t.FieldRotation == FieldRotationPolicy.Stop) {
                var rotation = Get(factory.GetCondition<LoopWhile>());
                // FieldRotation_MaxSub is the limit for 1 px of corner blur; the limit grows linearly with the allowed blur
                rotation.PredicateExpression.Definition = t.BlurTolerancePx == 1
                    ? string.Format(CultureInfo.InvariantCulture, "{0} >= {1}", FieldRotation.FieldRotationSymbols.MaxSubSymbol, t.ExposureSeconds)
                    : string.Format(CultureInfo.InvariantCulture, "{0} * {1} >= {2}", FieldRotation.FieldRotationSymbols.MaxSubSymbol, t.BlurTolerancePx, t.ExposureSeconds);
                imaging.Add(rotation);
            }
            if (t.DitherEvery > 0) {
                var dither = Get(factory.GetTrigger<DitherAfterExposures>());
                dither.AfterExposures = t.DitherEvery;
                imaging.Add(dither);
            }
            if (t.RecenterArcmin > 0) {
                var drift = Get(factory.GetTrigger<CenterAfterDriftTrigger>());
                drift.DistanceArcMinutes = t.RecenterArcmin;
                drift.AfterExposures = Math.Max(1, t.RecenterEvery);
                imaging.Add(drift);
            }
            var exposure = Get(factory.GetItem<TakeExposure>());
            exposure.ExposureTime = t.ExposureSeconds;
            exposure.Gain = t.Gain;
            exposure.Offset = t.Offset;
            exposure.Binning = new BinningMode(t.Binning, t.Binning);
            exposure.ImageType = CaptureSequence.ImageTypes.LIGHT;
            if (t.Keyhole == KeyholePolicy.WaitUntilBelow) {
                // Before every exposure: hold the loop while the target is in the keyhole (passes at once below the limit)
                AddWaitBelowMaximum(imaging, t);
            }
            imaging.Add(exposure);
        }

        private void AddWaitBelowMaximum(SequentialContainer container, TargetPlan t) {
            var below = Get(factory.GetItem<WaitForAltitude>());
            // Attached first, so it inherits the target's coordinates (AfterParentChanged), then configured
            container.Add(below);
            below.AboveOrBelow = "<";
            below.Offset = t.MaxAltitudeDeg;
        }

        private TimeCondition Dawn(NightPlan plan) {
            var condition = Get(factory.GetCondition<TimeCondition>());
            IDateTimeProvider provider = plan.Dawn switch {
                DawnStop.Nautical => factory.DateTimeProviders.OfType<NauticalDawnProvider>().FirstOrDefault(),
                DawnStop.Civil => factory.DateTimeProviders.OfType<CivilDawnProvider>().FirstOrDefault(),
                _ => factory.DateTimeProviders.OfType<DawnProvider>().FirstOrDefault()
            };
            // The factory's own provider instance, never a new one: the JSON converter matches saved providers by type against it
            condition.SelectedProvider = provider ?? throw new InvalidOperationException($"The sequencer factory has no {plan.Dawn} dawn provider");
            condition.MinutesOffset = plan.DawnOffsetMinutes;
            return condition;
        }

        private MaxAltitudeCondition MaxAltitude(TargetPlan t) {
            var condition = Get(factory.GetCondition<MaxAltitudeCondition>());
            condition.MaxAltitude = t.MaxAltitudeDeg;
            return condition;
        }

        private static T Get<T>(T entity) where T : class {
            return entity ?? throw new InvalidOperationException($"The sequencer factory has no {typeof(T).Name} (it is not in the catalogue)");
        }

        private static void Check(NightPlan plan) {
            if (plan.Targets == null || plan.Targets.Count == 0) {
                throw new ArgumentException("The night has no target", nameof(plan));
            }
            foreach (var t in plan.Targets) {
                if (string.IsNullOrWhiteSpace(t.Name)) {
                    throw new ArgumentException("A target has no name", nameof(plan));
                }
                if (t.Coordinates == null) {
                    throw new ArgumentException($"Target {t.Name} has no coordinates", nameof(plan));
                }
                if (!(t.ExposureSeconds > 0)) {
                    throw new ArgumentException($"Target {t.Name}: the exposure must be longer than 0 s", nameof(plan));
                }
                if (t.Count is int n && n < 1) {
                    throw new ArgumentException($"Target {t.Name}: the exposure count must be at least 1", nameof(plan));
                }
                if (t.Binning < 1) {
                    throw new ArgumentException($"Target {t.Name}: binning must be at least 1", nameof(plan));
                }
                if (t.DitherEvery < 0 || t.RecenterEvery < 0 || t.RecenterArcmin < 0) {
                    throw new ArgumentException($"Target {t.Name}: dither and recentre settings cannot be negative", nameof(plan));
                }
                if (!(t.MaxAltitudeDeg > 0 && t.MaxAltitudeDeg <= 90)) {
                    throw new ArgumentException($"Target {t.Name}: the maximum altitude must be in (0, 90]", nameof(plan));
                }
                if (!(t.BlurTolerancePx > 0)) {
                    throw new ArgumentException($"Target {t.Name}: the blur tolerance must be positive", nameof(plan));
                }
            }
        }
    }
}
