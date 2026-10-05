#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace NINA.Mac.App.Test {

    /// <summary>Paths in the mac/ tree, from the test output folder mac/tests/NINA.Mac.App.Test/bin/&lt;Config&gt;/&lt;TFM&gt;/&lt;RID&gt;.</summary>
    public static class MacRepo {

        private static DirectoryInfo TestDir => new(TestContext.CurrentContext.TestDirectory);

        public static string Root => TestDir.Parent.Parent.Parent.Parent.Parent.Parent.FullName;

        /// <summary>The app's own build output for the same configuration (src/NINA.Mac.App/bin/&lt;Config&gt;/&lt;TFM&gt;/&lt;RID&gt;/NINA.Mac.App).</summary>
        public static string AppBinary {
            get {
                var testDir = TestDir;
                return Path.Combine(Root, "src", "NINA.Mac.App", "bin", testDir.Parent.Parent.Name, testDir.Parent.Name, testDir.Name, "NINA.Mac.App");
            }
        }

        public static string AppProject => Path.Combine(Root, "src", "NINA.Mac.App", "NINA.Mac.App.csproj");

        /// <summary>
        /// mac/artifacts/&lt;AppDisplayName&gt;.app/Contents/MacOS/&lt;AppExecutableName&gt; as mac/packaging/package-app.sh
        /// builds it (the executable is the short name without spaces, as NINA.Mac.App.csproj derives it).
        /// </summary>
        public static string PackagedAppExecutable =>
            Path.Combine(Root, "artifacts", AppInfo.Current.DisplayName + ".app", "Contents", "MacOS", AppInfo.Current.ShortName.Replace(" ", ""));

        /// <summary>The user-local .NET 10 SDK wrapper (plain dotnet on PATH is .NET 8).</summary>
        public static string Dotnet => Path.Combine(Root, "dotnet");

        public static string BundleTools => Path.Combine(Root, "packaging", "bundle_tools.py");

        public static string NativeStage => Path.Combine(Root, "native", "stage");

        public static string NinaRoot => Directory.GetParent(Root).FullName;
    }

    /// <summary>A finished child process.</summary>
    public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr) {
        public string Output => StdOut + StdErr;
    }

    public static class ChildProcess {

        /// <summary>Runs <paramref name="file"/> with <paramref name="arguments"/> (no shell quoting) and waits for it.</summary>
        public static ProcessResult Run(string file, IEnumerable<string> arguments, string workingDirectory = null, IDictionary<string, string> environment = null, int timeoutMs = 120000) {
            var psi = new ProcessStartInfo(file) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            };
            foreach (var a in arguments) {
                psi.ArgumentList.Add(a);
            }
            foreach (var (key, value) in environment ?? new Dictionary<string, string>()) {
                if (value == null) {
                    psi.Environment.Remove(key);
                } else {
                    psi.Environment[key] = value;
                }
            }
            using var p = Process.Start(psi);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) {
                p.Kill(true);
                throw new TimeoutException($"{file} {string.Join(' ', arguments)} timed out");
            }
            p.WaitForExit();
            return new ProcessResult(p.ExitCode, stdout.Result, stderr.Result);
        }
    }
}
