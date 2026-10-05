#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;
using System.Windows.Threading;

namespace System.Windows.Input {

    /// <summary>
    /// Stand-in for WPF's CommandManager requery plumbing used by NINA's legacy RelayCommand/AsyncCommand.
    /// WPF semantics kept: handlers are held through weak references (subscribers must keep their delegate alive),
    /// handlers and requests are per thread, and <see cref="InvalidateRequerySuggested"/> raises the event later
    /// through the calling thread's <see cref="Dispatcher"/> at Background priority, coalescing repeated requests.
    /// Without a UI loop an idle dispatcher runs work inline, so the event is raised before the call returns; if the
    /// dispatcher is busy on another thread, the requery is queued and raised on a thread-pool thread afterwards.
    /// WPF also requeries on every keyboard/mouse input; there is no input system here.
    /// </summary>
    public sealed class CommandManager {

        [ThreadStatic]
        private static CommandManager current;

        private readonly List<WeakReference<EventHandler>> handlers = new List<WeakReference<EventHandler>>();
        private DispatcherOperation pendingRequery;

        private CommandManager() {
        }

        private static CommandManager Current => current ??= new CommandManager();

        public static event EventHandler RequerySuggested {
            add {
                if (value != null) {
                    var manager = Current;
                    lock (manager.handlers) {
                        manager.handlers.Add(new WeakReference<EventHandler>(value));
                    }
                }
            }
            remove {
                if (value != null) {
                    var manager = Current;
                    lock (manager.handlers) {
                        manager.handlers.RemoveAll(reference => !reference.TryGetTarget(out var handler) || handler == value);
                    }
                }
            }
        }

        public static void InvalidateRequerySuggested() {
            Current.ScheduleRequery();
        }

        private void ScheduleRequery() {
            if (pendingRequery != null && pendingRequery.Status == DispatcherOperationStatus.Pending) {
                return;
            }
            pendingRequery = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RaiseRequerySuggested));
        }

        private void RaiseRequerySuggested() {
            var alive = new List<EventHandler>();
            lock (handlers) {
                handlers.RemoveAll(reference => {
                    if (reference.TryGetTarget(out var handler)) {
                        alive.Add(handler);
                        return false;
                    }
                    return true;
                });
            }
            foreach (var handler in alive) {
                handler(null, EventArgs.Empty);
            }
        }
    }
}
