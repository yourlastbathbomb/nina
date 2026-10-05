#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NINA.Astrometry.Mac;

namespace NINA.Mac.Astrometry.Test {

    /// <summary>
    /// EngineData: the startup check a host runs so that a missing JPLEPH or catalogue SQL fails loudly. Without JPLEPH, NOVAS
    /// itself does not fail: app_planet returns NaN and a Moon place comes back wrong with error code 0.
    /// </summary>
    [TestFixture]
    public class EngineDataTest {
        private string folder = string.Empty;

        [SetUp]
        public void SetUp() {
            folder = Path.Combine(TestHost.DataRoot, "enginedata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        [TearDown]
        public void TearDown() {
            Directory.Delete(folder, true);
        }

        [Test]
        public void EnsureAvailable_Passes_ForTheBuildOutput() {
            EngineData.Check().Should().BeEmpty();
            FluentActions.Invoking(EngineData.EnsureAvailable).Should().NotThrow();
        }

        [Test]
        public void FindMissingFiles_ReportsEveryMissingFile() {
            var missing = EngineData.FindMissingFiles(folder);

            missing.Should().HaveCount(4);
            missing[0].Should().Contain(Path.Combine(folder, "External", "JPLEPH")).And.Contain("NaN or wrong");
            missing[1].Should().Contain(Path.Combine(folder, "Database", "Initial", "initial_schema.sql"));
            missing[2].Should().Contain(Path.Combine(folder, "Database", "Initial", "initial_data.sql"));
            missing[3].Should().Contain(Path.Combine(folder, "Database", "Migration"));

            Directory.CreateDirectory(Path.Combine(folder, "Database", "Migration"));
            EngineData.FindMissingFiles(folder).Should().HaveCount(4, "an empty Migration folder is as bad as none");
        }

        [Test]
        public void FindMissingFiles_AcceptsTheBundleLayout_WithRelativeSymlinksIntoResources() {
            // Contents/MacOS/External/JPLEPH -> ../../Resources/JPLEPH and Contents/MacOS/Database -> ../Resources/Database
            var macos = Path.Combine(folder, "Contents", "MacOS");
            var resources = Path.Combine(folder, "Contents", "Resources");
            Directory.CreateDirectory(Path.Combine(macos, "External"));
            Directory.CreateDirectory(Path.Combine(resources, "Database", "Initial"));
            Directory.CreateDirectory(Path.Combine(resources, "Database", "Migration"));
            File.WriteAllText(Path.Combine(resources, "JPLEPH"), "x");
            File.WriteAllText(Path.Combine(resources, "Database", "Initial", "initial_schema.sql"), "x");
            File.WriteAllText(Path.Combine(resources, "Database", "Initial", "initial_data.sql"), "x");
            File.WriteAllText(Path.Combine(resources, "Database", "Migration", "1.sql"), "x");
            File.CreateSymbolicLink(Path.Combine(macos, "External", "JPLEPH"), "../../Resources/JPLEPH");
            Directory.CreateSymbolicLink(Path.Combine(macos, "Database"), "../Resources/Database");

            EngineData.FindMissingFiles(macos).Should().BeEmpty();

            // Dangling symlinks count as missing (File.Exists alone would report them as present)
            File.Delete(Path.Combine(resources, "JPLEPH"));
            Directory.Delete(Path.Combine(resources, "Database"), true);
            File.Exists(Path.Combine(macos, "External", "JPLEPH")).Should().BeTrue("this is the trap the check avoids");
            EngineData.FindMissingFiles(macos).Should().HaveCount(4);
        }
    }
}
