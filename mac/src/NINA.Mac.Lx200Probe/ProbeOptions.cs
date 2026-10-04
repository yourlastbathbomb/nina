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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NINA.Mac.Lx200Probe {

    /// <summary>"--key value" / "--flag" options; a key may repeat (--sim-quirk).</summary>
    internal sealed class ProbeOptions {
        private readonly Dictionary<string, List<string>> values = new(StringComparer.OrdinalIgnoreCase);

        public static ProbeOptions Parse(IEnumerable<string> args) {
            var o = new ProbeOptions();
            var list = args.ToList();
            for (var i = 0; i < list.Count; i++) {
                if (!list[i].StartsWith("--", StringComparison.Ordinal)) {
                    throw new ArgumentException($"Unexpected argument '{list[i]}'");
                }
                var key = list[i][2..];
                var hasValue = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal);
                if (!o.values.TryGetValue(key, out var bucket)) {
                    o.values[key] = bucket = new List<string>();
                }
                bucket.Add(hasValue ? list[++i] : "true");
            }
            return o;
        }

        public bool Has(string key) => values.ContainsKey(key);

        public bool Flag(string key) => values.TryGetValue(key, out var v) && v[^1] == "true";

        public string String(string key, string fallback) => values.TryGetValue(key, out var v) ? v[^1] : fallback;

        public IReadOnlyList<string> All(string key) => values.TryGetValue(key, out var v) ? v : Array.Empty<string>();

        public int Int(string key, int fallback) => values.TryGetValue(key, out var v) ? int.Parse(v[^1], CultureInfo.InvariantCulture) : fallback;

        public double Double(string key, double fallback) => values.TryGetValue(key, out var v) ? double.Parse(v[^1], CultureInfo.InvariantCulture) : fallback;

        public IEnumerable<string> Keys => values.Keys;

        public static string ExpandHome(string path) {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path == "~") {
                return home;
            }
            return path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) : Path.GetFullPath(path);
        }
    }
}
