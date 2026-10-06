#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.Sequencing.Conditions;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem.Camera;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Guider;
using NINA.Sequencer.Trigger.Platesolving;
using System.Globalization;
using System.Text;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// A readable dump of a sequence tree: every container with its conditions, triggers and items, and the settings the Target
    /// form generator sets. Used as the golden structure of generated trees and to compare a tree with its JSON reload.
    /// </summary>
    internal static class TreeDump {

        public static string Of(ISequenceContainer root) {
            var text = new StringBuilder();
            Container(root, 0, text);
            return text.ToString();
        }

        private static void Container(ISequenceContainer container, int depth, StringBuilder text) {
            Line(container, depth, text);
            if (container is IConditionable conditionable) {
                foreach (var condition in conditionable.GetConditionsSnapshot()) {
                    Line(condition, depth + 1, text, "[condition] ");
                }
            }
            if (container is ITriggerable triggerable) {
                foreach (var trigger in triggerable.GetTriggersSnapshot()) {
                    Line(trigger, depth + 1, text, "[trigger] ");
                }
            }
            if (container is IImmutableContainer && container is not SequenceRootContainer) {
                return;
            }
            foreach (var item in container.GetItemsSnapshot()) {
                if (item is ISequenceContainer child) {
                    Container(child, depth + 1, text);
                } else {
                    Line(item, depth + 1, text);
                }
            }
        }

        private static void Line(ISequenceEntity entity, int depth, StringBuilder text, string prefix = "") {
            text.Append(new string(' ', depth * 2)).Append(prefix).Append(entity.GetType().Name);
            var settings = Settings(entity);
            if (settings.Length > 0) {
                text.Append(' ').Append(settings);
            }
            text.Append('\n');
        }

        private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        private static string Settings(ISequenceEntity e) {
            return e switch {
                SequenceRootContainer r => $"\"{r.Name}\"",
                DeepSkyObjectContainer d => $"\"{d.Name}\" target \"{d.Target.TargetName}\" RA {F(d.Target.InputCoordinates.Coordinates.RA)} h Dec {F(d.Target.InputCoordinates.Coordinates.Dec)}",
                SequentialContainer s => $"\"{s.Name}\"",
                LoopCondition l => $"Iterations={l.Iterations}",
                TimeCondition t => $"Provider={t.SelectedProvider?.GetType().Name} MinutesOffset={t.MinutesOffset}",
                AboveHorizonCondition h => $"Offset={F(h.Offset)}",
                MaxAltitudeCondition m => $"MaxAltitude={F(m.MaxAltitude)}",
                AltitudeCondition a => $"Offset={F(a.Offset)}",
                LoopWhile w => $"Predicate=\"{w.PredicateExpression.Definition}\"",
                DitherAfterExposures d => $"AfterExposures={d.AfterExposures}",
                CenterAfterDriftTrigger c => $"DistanceArcMinutes={F(c.DistanceArcMinutes)} AfterExposures={c.AfterExposures}",
                TakeExposure x => $"ExposureTime={F(x.ExposureTime)} Gain={x.Gain} Offset={x.Offset} Binning={x.Binning?.Name} ImageType={x.ImageType}",
                CoolCamera c => $"Temperature={F(c.Temperature)} Duration={F(c.Duration)}",
                WarmCamera w => $"Duration={F(w.Duration)}",
                DewHeater d => $"OnOff={d.OnOff}",
                WaitUntilAboveHorizon w => $"Offset={F(w.Offset)}",
                WaitForAltitude w => $"AboveOrBelow={w.AboveOrBelow} Offset={F(w.Offset)}",
                ExternalScript s => $"Script=\"{s.Script}\"",
                Center c => $"Inherited={c.Inherited}",
                _ => string.Empty
            };
        }
    }
}
