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
using System.Runtime.CompilerServices;

namespace NINA.Mac.Astrometry.Test {

    /// <summary>
    /// Process-wide host setup, as an app head does it (see mac/src/README-engine.md): NINA's data folder
    /// (CoreUtil.APPLICATIONTEMPPATH, which holds Logs/, Profiles/ and the default NINA.sqlite) points at a fresh temp
    /// folder before any test touches Logger, ProfileService or DatabaseInteraction. A module initializer is used because
    /// upstream's own [SetUpFixture] (NINA.Test/Usings.cs) logs before any other setup fixture is guaranteed to run.
    /// No System.Windows.Application is created: upstream NINA.Test does not create one outside its WPF fixtures.
    /// </summary>
    internal static class TestHost {

        public static string DataRoot { get; private set; } = string.Empty;

        /// <summary>CoreUtil.APPLICATIONTEMPPATH as NINA computes it on this machine, before the test host moves it.</summary>
        public static string DefaultApplicationTempPath { get; private set; } = string.Empty;

        [ModuleInitializer]
        internal static void Initialize() {
            DefaultApplicationTempPath = CoreUtil.APPLICATIONTEMPPATH;
            DataRoot = Path.Combine(Path.GetTempPath(), "nina-mac-astrometry-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataRoot);
            CoreUtil.APPLICATIONTEMPPATH = DataRoot;
        }
    }
}

/// <summary>
/// Runs once after every fixture in the assembly (global namespace): flushes the log and removes the data folder.
/// Set NINA_MAC_KEEP_TEST_DATA=1 to keep it (Logs/, NINA.sqlite) for inspection; its path is written to the test output.
/// </summary>
[SetUpFixture]
public class MacAstrometryTestTeardown {

    [OneTimeTearDown]
    public void Stop() {
        Logger.CloseAndFlush();
        System.Data.SQLite.SQLiteConnection.ClearAllPools();
        if (Environment.GetEnvironmentVariable("NINA_MAC_KEEP_TEST_DATA") == "1") {
            TestContext.Progress.WriteLine($"Test data kept in {NINA.Mac.Astrometry.Test.TestHost.DataRoot}");
            return;
        }
        try {
            Directory.Delete(NINA.Mac.Astrometry.Test.TestHost.DataRoot, true);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}
