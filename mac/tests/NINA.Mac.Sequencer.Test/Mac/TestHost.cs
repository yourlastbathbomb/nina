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
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// Process-wide host setup, as an app head does it (mac/src/README-engine.md, host contract): NINA's data folder
    /// (CoreUtil.APPLICATIONTEMPPATH: Logs/, Profiles/, the solvers' working folder) points at a fresh temp folder, and a
    /// System.Windows.Application exists (NINA's TelescopeVM and ProfileService need Application.Current), both before any test
    /// touches NINA. A module initializer is used because upstream's [SetUpFixture] (NINA.Test/Usings.cs) logs first.
    /// </summary>
    internal static class TestHost {

        public static string DataRoot { get; private set; } = string.Empty;

        public static string NinaRoot => typeof(TestHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "NinaRoot").Value!;

        [ModuleInitializer]
        internal static void Initialize() {
            // A space in the path on purpose, like ~/Library/Application Support
            DataRoot = Path.Combine(Path.GetTempPath(), "nina mac sequencer test " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataRoot);
            CoreUtil.APPLICATIONTEMPPATH = DataRoot;
            if (System.Windows.Application.Current == null) {
                _ = new System.Windows.Application();
            }
        }

        /// <summary>A new empty folder under the data folder.</summary>
        public static string NewFolder(string name) {
            var folder = Path.Combine(DataRoot, "Work", name + " " + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}

/// <summary>
/// Runs once after every fixture in the assembly (global namespace): flushes the log and removes the data folder.
/// Set NINA_MAC_KEEP_TEST_DATA=1 to keep it (Logs/, Work/ with the simulated nights' FITS files) for inspection.
/// </summary>
[SetUpFixture]
public class MacSequencerTestTeardown {

    [OneTimeTearDown]
    public void Stop() {
        Logger.CloseAndFlush();
        if (Environment.GetEnvironmentVariable("NINA_MAC_KEEP_TEST_DATA") == "1") {
            TestContext.Progress.WriteLine($"Test data kept in {NINA.Mac.Sequencer.Test.TestHost.DataRoot}");
            return;
        }
        try {
            Directory.Delete(NINA.Mac.Sequencer.Test.TestHost.DataRoot, true);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}
