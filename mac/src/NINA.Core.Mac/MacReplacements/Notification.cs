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

namespace NINA.Core.Utility.Notification {

    /// <summary>
    /// macOS replacement for upstream Utility/Notification/Notification.cs (WPF toast windows). Same public API.
    /// Upstream drops every notification when no WPF Application exists; here every call raises <see cref="Posted"/>
    /// with the header and lifetime upstream would show, and is a no-op when nobody subscribes. A macOS UI subscribes
    /// to render toasts. Placement (<see cref="ConfigurePosition"/>), CloseAll and Dispose belong to that UI.
    /// </summary>
    public static class Notification {

        /// <summary>mac only: raised for every notification. Handlers may run on any thread.</summary>
        public static event EventHandler<NotificationPostedEventArgs> Posted;

        public static void ConfigurePosition(NotificationWorkArea workArea, NotificationCorner corner) {
            if (!System.Enum.IsDefined(workArea)) {
                throw new ArgumentOutOfRangeException(nameof(workArea), workArea, null);
            }
        }

        public static void ShowInformation(string message) {
            ShowInformation(message, TimeSpan.FromSeconds(10));
        }

        public static void ShowInformation(string message, TimeSpan lifetime) {
            Post(NotificationKind.Information, "LblInfo", null, message, lifetime);
        }

        public static void ShowSuccess(string message) {
            Post(NotificationKind.Success, "LblSuccess", null, message, TimeSpan.FromSeconds(10));
        }

        public static void ShowWarning(string message) {
            ShowWarning(message, TimeSpan.FromSeconds(30));
        }

        public static void ShowWarning(string message, TimeSpan lifetime) {
            Post(NotificationKind.Warning, "LblWarning", null, message, lifetime);
        }

        public static void ShowError(string message) {
            Post(NotificationKind.Error, "LblError", null, message, TimeSpan.FromHours(24));
        }

        public static void ShowExternalError(string message, string header) {
            Post(NotificationKind.ExternalError, "LblExternalError", header, message, TimeSpan.FromHours(24));
        }

        public static void ShowExternalWarning(string message, string header) {
            Post(NotificationKind.ExternalWarning, "LblExternalError", header, message, TimeSpan.FromHours(24));
        }

        public static void CloseAll() {
        }

        public static void Dispose() {
        }

        private static void Post(NotificationKind kind, string defaultHeaderKey, string header, string message, TimeSpan lifetime) {
            var handler = Posted;
            if (handler == null) {
                return;
            }
            if (string.IsNullOrWhiteSpace(header)) {
                header = Locale.Loc.Instance[defaultHeaderKey];
            }
            handler(null, new NotificationPostedEventArgs(kind, header, message, lifetime));
        }
    }

    /// <summary>mac only: which upstream Show* method produced a notification.</summary>
    public enum NotificationKind {
        Information,
        Success,
        Warning,
        Error,
        ExternalError,
        ExternalWarning
    }

    /// <summary>mac only: payload of <see cref="Notification.Posted"/>.</summary>
    public class NotificationPostedEventArgs : EventArgs {

        public NotificationPostedEventArgs(NotificationKind kind, string header, string message, TimeSpan lifetime) {
            Kind = kind;
            Header = header;
            Message = message;
            Lifetime = lifetime;
        }

        public NotificationKind Kind { get; }
        public string Header { get; }
        public string Message { get; }
        public TimeSpan Lifetime { get; }
    }
}
