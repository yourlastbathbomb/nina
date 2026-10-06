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

namespace NINA.Mac.Platesolving.Test {

    /// <summary>
    /// Process-wide host setup, as an app head does it (see mac/src/README-engine.md): NINA's data folder
    /// (CoreUtil.APPLICATIONTEMPPATH, which holds Logs/, Profiles/, the solvers' PlateSolver/ working folder and the generated
    /// Solvers/ files) points at a fresh temp folder before any test touches Logger or a solver. A module initializer is used
    /// because upstream's own [SetUpFixture] (NINA.Test/Usings.cs) logs before any other setup fixture is guaranteed to run, and
    /// BaseSolver's static constructor fixes its working folder from APPLICATIONTEMPPATH the first time a solver is created.
    /// </summary>
    internal static class TestHost {

        public static string DataRoot { get; private set; } = string.Empty;

        public static string NinaRoot => typeof(TestHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "NinaRoot").Value!;

        [ModuleInitializer]
        internal static void Initialize() {
            // The path contains a space on purpose, like ~/Library/Application Support: every solver argument must survive it
            DataRoot = Path.Combine(Path.GetTempPath(), "nina mac platesolving test " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataRoot);
            CoreUtil.APPLICATIONTEMPPATH = DataRoot;
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
/// Set NINA_MAC_KEEP_TEST_DATA=1 to keep it (Logs/, PlateSolver/, Solvers/, Work/) for inspection; its path is written to the
/// test output.
/// </summary>
[SetUpFixture]
public class MacPlatesolvingTestTeardown {

    [OneTimeTearDown]
    public void Stop() {
        Logger.CloseAndFlush();
        if (Environment.GetEnvironmentVariable("NINA_MAC_KEEP_TEST_DATA") == "1") {
            TestContext.Progress.WriteLine($"Test data kept in {NINA.Mac.Platesolving.Test.TestHost.DataRoot}");
            return;
        }
        try {
            Directory.Delete(NINA.Mac.Platesolving.Test.TestHost.DataRoot, true);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}
