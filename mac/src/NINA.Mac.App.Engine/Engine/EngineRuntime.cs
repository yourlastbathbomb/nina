#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NINA.Mac.App.Engine {

    /// <summary>One NINA notification (Notification.Show*), as the engine services see it.</summary>
    public sealed record EngineNotification(DateTime Time, NotificationKind Kind, string Message);

    /// <summary>
    /// Process-wide set-up of the headless engine, items 1-3 of the host contract in mac/src/README-engine.md:
    /// <list type="number">
    /// <item>NINA's data folder (<see cref="CoreUtil.APPLICATIONTEMPPATH"/>: Logs/, Profiles/, PlateSolver/, Solvers/, NINA.sqlite)
    /// moves into the app's own Application Support folder, before anything touches NINA's Logger;</item>
    /// <item>one <c>System.Windows.Application</c> exists, created on the calling thread (the UI thread in the app, after Avalonia
    /// installed its synchronization context, so the WpfCompat dispatcher marshals through the UI loop);</item>
    /// <item>NINA's notifications (Notification.Posted) are collected, so a failed connect can say why.</item>
    /// </list>
    /// NINA keeps this state in statics, so it is set once per process.
    /// </summary>
    public static class EngineRuntime {
        private const int MaxNotifications = 200;

        private static readonly object lockobj = new();
        private static readonly List<EngineNotification> notifications = new();
        private static string dataDirectory;

        /// <summary>NINA's data folder, or null before <see cref="Initialize"/>.</summary>
        public static string DataDirectory {
            get {
                lock (lockobj) {
                    return dataDirectory;
                }
            }
        }

        /// <summary>Raised for every NINA notification, on the thread that posted it.</summary>
        public static event EventHandler<EngineNotification> NotificationPosted;

        /// <summary>
        /// Points NINA at <paramref name="folder"/> and creates the WPF-compat Application. Calling it again with the same folder
        /// does nothing; another folder throws, because NINA's Logger and solvers already captured the first one.
        /// </summary>
        public static void Initialize(string folder) {
            ArgumentException.ThrowIfNullOrWhiteSpace(folder);
            var full = Path.GetFullPath(folder);
            lock (lockobj) {
                if (dataDirectory != null) {
                    if (!string.Equals(dataDirectory, full, StringComparison.Ordinal)) {
                        throw new InvalidOperationException($"The engine already uses the data folder {dataDirectory}; it cannot move to {full} in the same process");
                    }
                    return;
                }
                Directory.CreateDirectory(full);
                CoreUtil.APPLICATIONTEMPPATH = full;
                if (System.Windows.Application.Current == null) {
                    _ = new System.Windows.Application();
                }
                Notification.Posted += OnPosted;
                dataDirectory = full;
            }
            Logger.Info($"Nightglass engine: NINA data folder {full}");
        }

        /// <summary>Notifications posted since <paramref name="since"/> (UTC), oldest first.</summary>
        public static IReadOnlyList<EngineNotification> NotificationsSince(DateTime since) {
            lock (lockobj) {
                return notifications.Where(n => n.Time >= since).ToList();
            }
        }

        /// <summary>The newest error or warning posted since <paramref name="since"/> (UTC), or null.</summary>
        public static string LastProblemSince(DateTime since) {
            lock (lockobj) {
                return notifications.LastOrDefault(n => n.Time >= since && n.Kind is NotificationKind.Error or NotificationKind.ExternalError or NotificationKind.Warning or NotificationKind.ExternalWarning)?.Message;
            }
        }

        private static void OnPosted(object sender, NotificationPostedEventArgs e) {
            var entry = new EngineNotification(DateTime.UtcNow, e.Kind, e.Message);
            lock (lockobj) {
                notifications.Add(entry);
                if (notifications.Count > MaxNotifications) {
                    notifications.RemoveAt(0);
                }
            }
            NotificationPosted?.Invoke(null, entry);
        }
    }
}
