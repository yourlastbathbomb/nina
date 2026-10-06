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
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NINA.Image.Mac {

    /// <summary>
    /// What the macOS fork writes as SWCREATE into saved FITS and XISF files, e.g.
    /// "Nightglass 0.1.0 (based on N.I.N.A. 3.3.0.1064) (arm64)", instead of upstream's "N.I.N.A. 3.3.0.1064 (x64)", which names
    /// the wrong program and, on Apple silicon, the wrong architecture (DllLoader.IsX86 only tells 32-bit from 64-bit). The
    /// header writers read <see cref="CoreUtil.ImageFileCreator"/> (a one-line upstream hook whose default is upstream's own
    /// value, so Windows writes exactly what it wrote before); <see cref="Apply"/> points it here. The headless host applies it
    /// (NINA.Mac.Sequencing's HeadlessHost), so every frame the app saves carries it; code that saves without a host (upstream's
    /// own tests) keeps upstream's value.
    /// </summary>
    public static class MacImageFileIdentity {

        /// <summary>The fork's name. It never contains "NINA" (MPL grants no trademark rights; plan section 10).</summary>
        public const string ProductName = "Nightglass";

        /// <summary>FITS string values hold at most 68 characters (FITS standard 4.0, section 4.2.1.1).</summary>
        public const int MaxLength = 68;

        /// <summary>
        /// The SWCREATE value: "&lt;product&gt; &lt;version&gt; (based on N.I.N.A. &lt;upstream version&gt;) (&lt;process architecture&gt;)",
        /// shortened to <see cref="MaxLength"/> by dropping the version's pre-release or build suffix, then the "based on" part.
        /// </summary>
        public static string Describe(string productVersion, string upstreamVersion = null, Architecture? architecture = null) {
            var version = Clean(productVersion);
            var upstream = upstreamVersion ?? CoreUtil.Version;
            var arch = (architecture ?? RuntimeInformation.ProcessArchitecture).ToString().ToLowerInvariant();
            var full = $"{ProductName} {version} (based on N.I.N.A. {upstream}) ({arch})";
            if (full.Length <= MaxLength) {
                return full;
            }
            var shortVersion = version.Split('-', '+')[0];
            full = $"{ProductName} {shortVersion} (based on N.I.N.A. {upstream}) ({arch})";
            if (full.Length <= MaxLength) {
                return full;
            }
            full = $"{ProductName} {shortVersion} ({arch})";
            return full.Length <= MaxLength ? full : full.Substring(0, MaxLength);
        }

        /// <summary>
        /// Makes every FITS and XISF file this process saves say <see cref="Describe"/>. <paramref name="productVersion"/> null:
        /// <see cref="DetectProductVersion"/>.
        /// </summary>
        public static void Apply(string productVersion = null) {
            var value = Describe(productVersion ?? DetectProductVersion());
            CoreUtil.ImageFileCreator = () => value;
            Logger.Info($"FITS/XISF SWCREATE: {value}");
        }

        /// <summary>
        /// The app's version: the informational version of the entry assembly when it is the app (it carries the
        /// "NinaBaseVersion" assembly metadata the app's build stamps), without a "+commit" suffix; otherwise "dev" (a test host).
        /// </summary>
        public static string DetectProductVersion() {
            var entry = Assembly.GetEntryAssembly();
            if (entry != null && entry.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "NinaBaseVersion")) {
                var informational = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                    ?? entry.GetName().Version?.ToString();
                if (!string.IsNullOrWhiteSpace(informational)) {
                    return informational.Split('+')[0];
                }
            }
            return "dev";
        }

        private static string Clean(string version) {
            var v = string.IsNullOrWhiteSpace(version) ? "dev" : version.Trim();
            return new string(v.Where(c => c >= ' ' && c != '\'' && c < 127).ToArray());
        }
    }
}
