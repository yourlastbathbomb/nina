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
using System.IO;

namespace NINA.Mac.Platform {

    /// <summary>The app's names. <see cref="ShortName"/> names folders; it must be a single path segment.</summary>
    public sealed record AppIdentity {

        public AppIdentity(string displayName, string shortName, string bundleId) {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentException.ThrowIfNullOrWhiteSpace(shortName);
            ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);
            if (shortName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || shortName == "." || shortName == "..") {
                throw new ArgumentException($"Short name must be one folder name: '{shortName}'", nameof(shortName));
            }
            DisplayName = displayName;
            ShortName = shortName;
            BundleId = bundleId;
        }

        public string DisplayName { get; }

        public string ShortName { get; }

        public string BundleId { get; }
    }

    /// <summary>
    /// Per-user folders. Images go to <c>~/Astro/&lt;ShortName&gt;</c>, outside <c>~/Documents</c>, which iCloud syncs on
    /// this Mac (MAC_PORT_PLAN.md section 7). Settings use Application Support, logs ~/Library/Logs and caches
    /// ~/Library/Caches, the standard macOS homes. Every path is absolute and built from the home directory.
    /// </summary>
    public sealed class UserDataPaths {

        public UserDataPaths(AppIdentity identity, string homeDirectory = null, string imagesRootOverride = null) {
            ArgumentNullException.ThrowIfNull(identity);
            Identity = identity;
            Home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(Home) || !Path.IsPathFullyQualified(Home)) {
                throw new PlatformServiceException($"Cannot determine an absolute home directory (got '{Home}')");
            }
            Home = Path.TrimEndingDirectorySeparator(Home);
            DefaultImagesRoot = Path.Combine(Home, "Astro", identity.ShortName);
            ImagesRoot = string.IsNullOrWhiteSpace(imagesRootOverride) ? DefaultImagesRoot : ExpandHome(imagesRootOverride.Trim(), Home);
            if (!Path.IsPathFullyQualified(ImagesRoot)) {
                throw new ArgumentException($"Images folder must be absolute or start with ~/: '{imagesRootOverride}'", nameof(imagesRootOverride));
            }
            var library = Path.Combine(Home, "Library");
            SettingsDirectory = Path.Combine(library, "Application Support", identity.ShortName);
            LogsDirectory = Path.Combine(library, "Logs", identity.ShortName);
            CachesDirectory = Path.Combine(library, "Caches", identity.BundleId);
        }

        public AppIdentity Identity { get; }

        public string Home { get; }

        public string DefaultImagesRoot { get; }

        /// <summary>Root of the image folders (Siril session layout lives below it).</summary>
        public string ImagesRoot { get; }

        public string SettingsDirectory { get; }

        public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

        public string LogsDirectory { get; }

        public string CachesDirectory { get; }

        /// <summary>Creates the settings and logs folders (not the images root; that is created when the first frame is saved).</summary>
        public void EnsureAppDirectories() {
            Directory.CreateDirectory(SettingsDirectory);
            Directory.CreateDirectory(LogsDirectory);
        }

        /// <summary>A warning when <paramref name="path"/> is inside a folder iCloud may sync (Desktop, Documents, iCloud Drive); otherwise null.</summary>
        public string CloudSyncWarning(string path) {
            if (string.IsNullOrWhiteSpace(path)) {
                return null;
            }
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ExpandHome(path, Home)));
            foreach (var synced in new[] { "Documents", "Desktop", Path.Combine("Library", "Mobile Documents") }) {
                var root = Path.Combine(Home, synced);
                if (full.Equals(root, StringComparison.Ordinal) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
                    return $"{full} is inside ~/{synced}, which iCloud can sync. Gigabytes of FITS frames will upload and may be evicted to the cloud mid-session; prefer ~/Astro.";
                }
            }
            return null;
        }

        public static string ExpandHome(string path, string home) {
            if (path == "~") {
                return home;
            }
            if (path.StartsWith("~/", StringComparison.Ordinal)) {
                return Path.Combine(home, path[2..]);
            }
            return path;
        }
    }
}
