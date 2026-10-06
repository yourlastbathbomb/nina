#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// macOS (headless) copy of upstream NINA.Sequencer/Editing/SequencePropertyCapture.cs, trimmed to its WPF-free members.
// Removed: Create(ISequenceEntity, BindingExpression), which captures an edit from a WPF binding, and FindPath, which only
// Create uses (their callers, SequenceEditContext.StepExpression and SequenceEditBindingResolver, are WPF-only and not
// compiled). ISequenceEditCapture, Capture<T>, Target, BoundSnapshot and Excluded are upstream's text, unchanged.
// SequencerParityTest pins a hash of the upstream file: re-sync this copy when upstream changes it.

using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Locale;
using NINA.Sequencer.Container;
using NINA.Sequencer.Logic;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace NINA.Sequencer.Editing {
    internal interface ISequenceEditCapture { ISequenceEdit Complete(); }

    /// <summary>Captures only the configuration addressed by an editor, never the whole live graph.</summary>
    internal sealed class SequencePropertyCapture : ISequenceEditCapture {
        private readonly Func<ISequenceEdit> complete;
        private SequencePropertyCapture(Func<ISequenceEdit> complete) { this.complete = complete; }
        public ISequenceEdit Complete() => complete();

        internal static SequencePropertyCapture Capture<T>(string description, Func<T> read, Action<T> write, Func<T, T, bool> equal = null, string context = null, Func<T, string> format = null, string summaryContext = null) {
            T before = read();
            format ??= value => SequenceEditDetails.Value(value);
            string beforeText = format(before);
            equal ??= EqualityComparer<T>.Default.Equals;
            return new SequencePropertyCapture(() => {
                T after = read();
                if (equal(before, after)) return null;
                string afterText = format(after);
                return new PropertySequenceEdit<T>(description, read, write, before, after, equal,
                    SequenceEditDetails.Change(context, beforeText, afterText),
                    SequenceEditDetails.Change(summaryContext ?? context, beforeText, afterText));
            });
        }

        private sealed class BoundSnapshot : ISequenceEditSnapshot {
            private readonly ISequenceEditSnapshot snapshot;
            private readonly ISequenceEntity owner;
            private readonly ISequenceContainer parent;
            private readonly Action refresh;
            public BoundSnapshot(ISequenceEditSnapshot snapshot, ISequenceEntity owner, ISequenceContainer parent, Action refresh) {
                this.snapshot = snapshot;
                this.owner = owner;
                this.parent = parent;
                this.refresh = refresh;
            }
            public string Description => snapshot.Description;
            public bool IsCurrent => ReferenceEquals(owner.Parent, parent) && snapshot.IsCurrent;
            public void Restore() {
                if (!ReferenceEquals(owner.Parent, parent)) throw new SequenceEditConflictException();
                snapshot.Restore();
                refresh();
            }
        }

        private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal) {
            "Parent", "Status", "IsExpanded", "Expanded", "ShowMenu", "CompletedIterations",
            "Progress", "ProgressExposures", "Executed", "ExposureInfoListExpanded"
        };

        internal static SequencePropertyCapture Target(ISequenceEntity owner) {
            string description = string.Format(Loc.Instance["Lbl_SequenceHistory_TargetAction"], SequenceEditDetails.Name(owner));
            string context = SequenceEditDetails.Context(owner);
            string summaryContext = SequenceEditDetails.Context(owner, compact: true);
            if (owner is not IDeepSkyObjectContainer dso) return null;
            return Capture(description,
                () => (dso.Name, Target: SequenceTargetState.Capture(dso.Target)),
                value => { dso.Name = value.Name; value.Target.Restore(dso.Target); },
                context: context, summaryContext: summaryContext, format: value => SequenceEditDetails.Target(value.Target));
        }

    }
}