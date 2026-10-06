#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// macOS (headless) copy of upstream NINA.Sequencer/Editing/SequenceEditContext.cs, trimmed to its WPF-free members.
// Removed, because only XAML and WPF behaviours use them: the attached properties Command/Operation and
// History/IsRecordingEnabled with their accessors, StepExpression (ExprStepperControl.xaml.cs) and the private
// HistoryCommand. Every other member is upstream's text, unchanged. Without an open editor no SequenceEditHistory is
// registered, so Find returns null and every helper simply runs its action, as upstream does when no editor is open.
// SequencerParityTest pins a hash of the upstream file: re-sync this copy when upstream changes it.

using NINA.Core.Enum;
using NINA.Core.Locale;
using NINA.Sequencer.Container;
using NINA.Sequencer.DragDrop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace NINA.Sequencer.Editing {
    /// <summary>Inherited editor context. Custom plugin views can register their own reversible edits.</summary>
    public static class SequenceEditContext {
        internal static ICommand CreateCommand(ISequenceEntity owner, SequenceEditOperation operation, ICommand command) =>
            new SequenceEditCommand(command, parameter => ExecuteOperation(owner, operation, command, parameter));

        // A dialog or async operation starts recording itself around the accepted model update.
        internal static ICommand SelfRecordingCommand(ICommand command) => new SequenceEditCommand(command, command.Execute);

        internal static void ExecuteCommand(ISequenceEntity owner, SequenceEditOperation operation, ICommand command, object parameter) {
            if (command is SequenceEditCommand) command.Execute(parameter);
            else ExecuteOperation(owner, operation, command, parameter);
        }
        private static void ExecuteOperation(ISequenceEntity owner, SequenceEditOperation operation, ICommand command, object parameter) {
            void Apply() => command.Execute(parameter);
            if (operation == SequenceEditOperation.Toggle) { Toggle(owner, Apply); return; }
            string label = operation switch {
                SequenceEditOperation.Delete => "Lbl_SequenceHistory_DeleteAction",
                SequenceEditOperation.Duplicate => "Lbl_SequenceHistory_DuplicateAction",
                SequenceEditOperation.Place => "Lbl_SequenceHistory_PlaceAction",
                _ => "Lbl_SequenceHistory_MoveAction"
            };
            if (operation == SequenceEditOperation.Place) {
                var drop = parameter as DropIntoParameters;
                Structure(owner, label, Apply, drop?.Source as ISequenceEntity, drop?.Duplicate == true ? SequenceEditOperation.Duplicate : operation);
            } else Placement(owner, label, Apply, operation);
        }

        private static readonly List<WeakReference<SequenceEditHistory>> sessions = new();
        internal static void Register(SequenceEditHistory history) {
            lock (sessions) {
                sessions.RemoveAll(entry => !entry.TryGetTarget(out _));
                sessions.Add(new(history));
            }
        }
        internal static void Unregister(SequenceEditHistory history) {
            lock (sessions) sessions.RemoveAll(entry => !entry.TryGetTarget(out var target) || ReferenceEquals(target, history));
        }
        internal static SequenceEditHistory Find(ISequenceEntity entity) {
            if (entity == null) return null;
            SequenceEditHistory[] active;
            lock (sessions) active = sessions.Select(entry => entry.TryGetTarget(out var value) ? value : null).Where(value => value != null).Reverse().ToArray();
            return active.FirstOrDefault(history => history.Graph.OwnerPath(entity, false) != null)
                ?? active.FirstOrDefault(history => history.Graph.OwnerPath(entity) != null);
        }
        internal static void Placement(ISequenceEntity owner, string label, Action action, SequenceEditOperation? operation = null) =>
            CaptureStructure(Find(owner)?.ForOwner(owner), owner, label, action, owner, operation);

        internal static void Structure(ISequenceEntity owner, string label, Action action, ISequenceEntity subject = null, SequenceEditOperation? operation = null) {
            var session = Find(owner);
            CaptureStructure(owner is ISequenceContainer container ? session?.ForContents(container) : session?.ForOwner(owner), owner, label, action, subject, operation);
        }
        private static void CaptureStructure(SequenceEditHistory history, ISequenceEntity owner, string label, Action action, ISequenceEntity subject, SequenceEditOperation? operation) {
            if (history == null) action();
            else history.CaptureStructure(string.Format(Loc.Instance[label], SequenceEditDetails.Name(owner)), action, subject, operation);
        }
        internal static void Target(ISequenceEntity owner, Action action, Func<SequencePropertyCapture> captureTarget = null) {
            SequenceEditHistory history = Find(owner)?.ForOwner(owner);
            if (history?.IsRecording != true) { action(); return; }
            string description = string.Format(Loc.Instance["Lbl_SequenceHistory_TargetAction"], SequenceEditDetails.Name(owner));
            history.CaptureEdit(description, () => captureTarget != null ? captureTarget() : SequencePropertyCapture.Target(owner), action);
        }
        internal static void Property<T>(ISequenceEntity owner, string name, Func<T> read, Action<T> write, T value) {
            SequenceEditHistory history = Find(owner)?.ForOwner(owner);
            if (history?.IsRecording != true) { write(value); return; }
            string description = string.Format(Loc.Instance["Lbl_SequenceHistory_EditAction"], SequenceEditDetails.Name(SequenceEditDetails.VisibleOwner(owner)), name);
            history.CaptureEdit(description, () => SequencePropertyCapture.Capture(description, read, write, context: SequenceEditDetails.Context(owner), summaryContext: SequenceEditDetails.Context(owner, compact: true)), () => write(value));
        }
        internal static void Toggle(ISequenceEntity entity, Action action) {
            SequenceEditHistory history = Find(entity)?.ForOwner(entity);
            if (history?.IsRecording != true) { action(); return; }
            string description = string.Format(Loc.Instance["Lbl_SequenceHistory_ToggleAction"], SequenceEditDetails.Name(entity));
            history.CaptureEdit(description, () => SequencePropertyCapture.Capture(description,
                () => entity.Status != SequenceEntityStatus.DISABLED,
                enabled => entity.Status = enabled ? SequenceEntityStatus.CREATED : SequenceEntityStatus.DISABLED,
                context: SequenceEditDetails.Context(entity), summaryContext: SequenceEditDetails.Context(entity, compact: true), format: enabled => Loc.Instance[enabled ? "LblEnabled" : "LblDisabled"]), action);
        }
    }
}