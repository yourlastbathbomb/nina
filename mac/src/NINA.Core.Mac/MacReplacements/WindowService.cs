#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace NINA.Core.Utility.WindowService {

    /// <summary>
    /// macOS replacement for the WindowService class in upstream Utility/WindowService/WindowService.cs, which opens WPF
    /// windows. There are no WPF windows on macOS: Show and ShowDialog throw PlatformNotSupportedException.
    /// Close and DelayedClose do nothing, as upstream does when no window is open. The events are never raised.
    /// </summary>
    public class WindowService : IWindowService {

        public void Show(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None) {
            throw new PlatformNotSupportedException($"WindowService.Show(\"{title}\") opens a WPF window, which does not exist on macOS.");
        }

        public IDispatcherOperationWrapper ShowDialog(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None, ICommand closeCommand = null) {
            throw new PlatformNotSupportedException($"WindowService.ShowDialog(\"{title}\") opens a WPF window, which does not exist on macOS.");
        }

        public void DelayedClose(TimeSpan t) {
        }

        public Task Close() {
            return Task.CompletedTask;
        }

        public event EventHandler OnDialogResultChanged {
            add { }
            remove { }
        }

        public event EventHandler OnClosed {
            add { }
            remove { }
        }
    }

    // The contracts below are copied unchanged from upstream WindowService.cs (Equipment and Sequencer code uses them).

    public interface IWindowService {

        void Show(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None);

        IDispatcherOperationWrapper ShowDialog(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None, ICommand closeCommand = null);

        event EventHandler OnDialogResultChanged;

        event EventHandler OnClosed;

        void DelayedClose(TimeSpan t);

        Task Close();
    }

    public interface IDispatcherOperationWrapper {
        Dispatcher Dispatcher { get; }
        DispatcherPriority Priority { get; set; }
        DispatcherOperationStatus Status { get; }
        Task Task { get; }
        object Result { get; }

        TaskAwaiter GetAwaiter();

        DispatcherOperationStatus Wait();

        DispatcherOperationStatus Wait(TimeSpan timeout);

        bool Abort();

        event EventHandler Aborted;

        event EventHandler Completed;
    }

    public class DialogResultEventArgs : EventArgs {

        public DialogResultEventArgs(bool? dialogResult) {
            DialogResult = dialogResult;
        }

        public bool? DialogResult { get; set; }
    }
}
