#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Sequencing.Runner {

    /// <summary>
    /// Runs a sequence without NINA's sequencer window. <see cref="Validate"/> is a port of the private Sequencer.Validate
    /// (NINA.Sequencer/Sequencer.cs), so a host can show the pre-flight issues itself instead of NINA's message box;
    /// <see cref="RunAsync"/> starts NINA's own Sequencer with skipIssuePrompt, which initializes every entity, runs the root
    /// container and tears everything down again, exactly as on Windows.
    /// </summary>
    public sealed class HeadlessSequenceRunner {

        public HeadlessSequenceRunner(ISequenceRootContainer root) {
            Root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public ISequenceRootContainer Root { get; }

        /// <summary>
        /// The issues NINA would list in its pre-sequence checklist (enabled conditions, triggers and items that implement
        /// IValidatable, recursively, skipping the inside of immutable containers), without duplicates.
        /// </summary>
        public IReadOnlyList<string> Validate() {
            return Validate(Root).Distinct().ToList();
        }

        /// <summary>Entities NINA could not load (types this build does not have): they are skipped at run time.</summary>
        public IReadOnlyList<ISequenceEntity> UnknownEntities() {
            var unknown = new List<ISequenceEntity>();
            Walk(Root, entity => {
                if (entity is UnknownSequenceItem || entity is UnknownSequenceCondition || entity is UnknownSequenceTrigger || entity is UnknownSequenceContainer) {
                    unknown.Add(entity);
                }
            });
            return unknown;
        }

        /// <summary>Runs the sequence to its end, or until <paramref name="token"/> is cancelled. Never prompts.</summary>
        public Task RunAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
            return new NINA.Sequencer.Sequencer(Root).Start(progress, token, skipIssuePrompt: true);
        }

        private static IList<string> Validate(ISequenceContainer container) {
            var issues = new List<string>();
            if (container is IConditionable conditionable) {
                foreach (var condition in conditionable.GetConditionsSnapshot()) {
                    if (condition.Status != SequenceEntityStatus.DISABLED && condition is IValidatable v) {
                        v.Validate();
                        Add(issues, v);
                    }
                }
            }
            if (container is ITriggerable triggerable) {
                foreach (var trigger in triggerable.GetTriggersSnapshot()) {
                    if (trigger.Status != SequenceEntityStatus.DISABLED && trigger is IValidatable v) {
                        v.Validate();
                        Add(issues, v);
                    }
                }
            }
            foreach (var item in container.GetItemsSnapshot()) {
                if (item.Status != SequenceEntityStatus.DISABLED && item is IValidatable v) {
                    v.Validate();
                    Add(issues, v);
                }
                if (item is ISequenceContainer child && item is not IImmutableContainer && item.Status != SequenceEntityStatus.DISABLED) {
                    issues.AddRange(Validate(child));
                }
            }
            return issues;
        }

        private static void Add(List<string> issues, IValidatable validatable) {
            if (validatable.Issues != null) {
                issues.AddRange(validatable.Issues);
            }
        }

        /// <summary>Visits every entity of the tree: containers, their conditions, triggers and items.</summary>
        public static void Walk(ISequenceContainer container, Action<ISequenceEntity> visit) {
            visit(container);
            if (container is IConditionable conditionable) {
                foreach (var condition in conditionable.GetConditionsSnapshot()) {
                    visit(condition);
                }
            }
            if (container is ITriggerable triggerable) {
                foreach (var trigger in triggerable.GetTriggersSnapshot()) {
                    visit(trigger);
                }
            }
            foreach (var item in container.GetItemsSnapshot()) {
                if (item is ISequenceContainer child) {
                    Walk(child, visit);
                } else {
                    visit(item);
                }
            }
        }
    }
}
