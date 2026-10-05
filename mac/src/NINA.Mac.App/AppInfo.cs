#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.Platform;
using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace NINA.Mac.App {

    /// <summary>
    /// The app's names and versions, baked in from MSBuild (AppDisplayName and friends in NINA.Mac.App.csproj).
    /// </summary>
    public sealed class AppInfo {
        private static readonly Lazy<AppInfo> current = new(() => FromAssembly(typeof(AppInfo).Assembly));

        public AppInfo(string displayName, string shortName, string bundleId, string version, string ninaBaseVersion) {
            DisplayName = displayName;
            ShortName = shortName;
            BundleId = bundleId;
            Version = version;
            NinaBaseVersion = ninaBaseVersion;
        }

        public static AppInfo Current => current.Value;

        /// <summary>"Nightglass" (AppDisplayName). Never contains "NINA" (enforced at build time).</summary>
        public string DisplayName { get; }

        /// <summary>Folder/executable-safe name, e.g. "Nightglass".</summary>
        public string ShortName { get; }

        public string BundleId { get; }

        public string Version { get; }

        /// <summary>Upstream N.I.N.A. version this fork is based on (CommonAssemblyInfo.cs).</summary>
        public string NinaBaseVersion { get; }

        public AppIdentity Identity => new(DisplayName, ShortName, BundleId);

        /// <summary>
        /// True when <paramref name="name"/> contains "NINA" once everything but letters is removed, ignoring case
        /// ("N.I.N.A.", "N I N A", "Nina", "local.nina.mac"). The fork must not use the upstream name (MPL-2.0 grants no
        /// trademark rights). NINA.Mac.App.csproj (CheckAppIdentity) and mac/packaging/package-app.sh apply the same rule.
        /// </summary>
        public static bool ContainsNina(string name) =>
            name != null && Regex.Replace(name, @"[^\p{L}]", "").Contains("NINA", StringComparison.OrdinalIgnoreCase);

        public static AppInfo FromAssembly(Assembly assembly) {
            string Meta(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = informational.IndexOf('+');
            var version = plus < 0 ? informational : informational[..plus];
            return new AppInfo(
                Meta("AppDisplayName") ?? "Nightglass",
                Meta("AppShortName") ?? "Nightglass",
                Meta("AppBundleId") ?? "local.nightglass.mac",
                version,
                Meta("NinaBaseVersion") ?? "unknown");
        }
    }
}
