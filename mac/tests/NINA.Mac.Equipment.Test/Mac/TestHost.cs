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

namespace NINA.Mac.Equipment.Test {

    /// <summary>
    /// Process-wide host setup, as an app head does it (see mac/src/README-engine.md): NINA's data folder
    /// (CoreUtil.APPLICATIONTEMPPATH, which holds Logs/ and Profiles/) points at a fresh temp folder before any test touches
    /// Logger or ProfileService. A module initializer is used because upstream's own [SetUpFixture] (NINA.Test/Usings.cs)
    /// logs before any other setup fixture is guaranteed to run. NINA.Equipment registers its own DllImport resolver.
    /// </summary>
    internal static class TestHost {

        public static string DataRoot { get; private set; } = string.Empty;

        /// <summary>Folder for the images the tests save; each test uses its own sub-folder.</summary>
        public static string ImageRoot => Path.Combine(DataRoot, "Images");

        public static string NinaRoot => typeof(TestHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "NinaRoot").Value!;

        [ModuleInitializer]
        internal static void Initialize() {
            DataRoot = Path.Combine(Path.GetTempPath(), "nina-mac-equipment-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataRoot);
            CoreUtil.APPLICATIONTEMPPATH = DataRoot;
        }

        /// <summary>A new empty folder under <see cref="ImageRoot"/>.</summary>
        public static string NewImageFolder(string name) {
            var folder = Path.Combine(ImageRoot, name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}

/// <summary>
/// Runs once after every fixture in the assembly (global namespace): flushes the log and removes the data folder.
/// Set NINA_MAC_KEEP_TEST_DATA=1 to keep it (Logs/, Images/) for inspection; its path is written to the test output.
/// </summary>
[SetUpFixture]
public class MacEquipmentTestTeardown {

    [OneTimeTearDown]
    public void Stop() {
        Logger.CloseAndFlush();
        if (Environment.GetEnvironmentVariable("NINA_MAC_KEEP_TEST_DATA") == "1") {
            TestContext.Progress.WriteLine($"Test data kept in {NINA.Mac.Equipment.Test.TestHost.DataRoot}");
            return;
        }
        try {
            Directory.Delete(NINA.Mac.Equipment.Test.TestHost.DataRoot, true);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}
