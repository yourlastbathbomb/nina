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
using NINA.PlateSolving.Mac;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>
    /// The Nightglass-owned astrometry.cfg and the local solver's folders. The config is written to NINA's data folder, never to
    /// Homebrew's etc/ or the index folder, which is only listed.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class AstrometryNetSetupTest {

        private string folder = string.Empty;
        private IReadOnlyList<string> savedIndexDirectories = Array.Empty<string>();

        [SetUp]
        public void SetUp() {
            folder = TestHost.NewFolder("astrometry setup");
            savedIndexDirectories = AstrometryNetSetup.IndexDirectories;
        }

        [TearDown]
        public void TearDown() {
            AstrometryNetSetup.IndexDirectories = savedIndexDirectories;
            AstrometryNetSetup.ConfigDirectory = null;
            AstrometryNetSetup.CpuLimitSeconds = 300;
        }

        [Test]
        public void Defaults_AreHomebrewAndTheUsersAstrometryFolder() {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AstrometryNetSetup.DefaultIndexDirectory.Should().Be(Path.Combine(home, "Library", "Application Support", "Astrometry"));
            savedIndexDirectories.Should().Equal(AstrometryNetSetup.DefaultIndexDirectory);
            AstrometryNetSetup.ConfigFilePath.Should().Be(Path.Combine(TestHost.DataRoot, "Solvers", "astrometry.cfg"));
            AstrometryNetSetup.CpuLimitSeconds.Should().Be(300);
            AstrometryNetSetup.SolverTimeout.Should().Be(TimeSpan.FromSeconds(360));
        }

        [TestCase("", "/opt/homebrew/bin")]
        [TestCase("   ", "/opt/homebrew/bin")]
        [TestCase(null, "/opt/homebrew/bin")]
        [TestCase("/opt/local/bin/", "/opt/local/bin")]
        [TestCase("/usr/local/bin", "/usr/local/bin")]
        [TestCase("/", "/")]
        public void ResolveBinDirectory_TreatsCygwinLocationAsTheFolderWithSolveField(string? setting, string expected) {
            AstrometryNetSetup.ResolveBinDirectory(setting!).Should().Be(expected);
        }

        [Test]
        public void ResolveBinDirectory_ExpandsHomeAndAcceptsTheExecutableItself() {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AstrometryNetSetup.ResolveBinDirectory("~/astrometry/bin").Should().Be(Path.Combine(home, "astrometry", "bin"));
            var exe = SolverKit.WriteScript(Path.Combine(folder, "my bin", "solve-field"), "exit 0\n");
            AstrometryNetSetup.ResolveBinDirectory(exe).Should().Be(Path.Combine(folder, "my bin"));
        }

        [Test]
        public void BuildConfig_ListsEveryIndexFolderVerbatim() {
            AstrometryNetSetup.IndexDirectories = new[] { "/Users/me/Library/Application Support/Astrometry", "/Volumes/Data/index 5200" };
            AstrometryNetSetup.CpuLimitSeconds = 120;

            AstrometryNetSetup.BuildConfig().Should().Be(
                "# astrometry.net engine configuration written by Nightglass (NINA.Platesolving on macOS) before every local solve.\n" +
                "# Edits are overwritten. Homebrew's own astrometry.cfg is not used; the index folders below are only read.\n" +
                "cpulimit 120\n" +
                "add_path /Users/me/Library/Application Support/Astrometry\n" +
                "add_path /Volumes/Data/index 5200\n" +
                "autoindex\n");
            AstrometryNetSetup.SolverTimeout.Should().Be(TimeSpan.FromSeconds(180));
        }

        [TestCase("/a/b ")]
        [TestCase(" /a/b")]
        [TestCase("/a\nb")]
        [TestCase("")]
        public void IndexDirectories_RejectWhatAstrometryCfgCannotExpress(string bad) {
            FluentActions.Invoking(() => AstrometryNetSetup.IndexDirectories = new[] { bad }).Should().Throw<ArgumentException>();
            AstrometryNetSetup.IndexDirectories.Should().Equal(savedIndexDirectories);
        }

        [Test]
        public void CpuLimit_MustBePositive() {
            FluentActions.Invoking(() => AstrometryNetSetup.CpuLimitSeconds = 0).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Test]
        public void WriteConfig_WritesIntoTheConfigFolder_AndOnlyReadsTheIndexFolder() {
            var index = Directory.CreateDirectory(Path.Combine(folder, "Application Support", "Astrometry")).FullName;
            File.WriteAllText(Path.Combine(index, "index-4207-00.fits"), "stand-in");
            File.WriteAllText(Path.Combine(index, "fetch.log"), "not an index");
            var before = Snapshot(index);
            AstrometryNetSetup.IndexDirectories = new[] { index };
            AstrometryNetSetup.ConfigDirectory = Path.Combine(folder, "Solvers");

            var path = AstrometryNetSetup.WriteConfig();

            path.Should().Be(Path.Combine(folder, "Solvers", "astrometry.cfg"));
            File.ReadAllText(path).Should().Be(AstrometryNetSetup.BuildConfig());
            File.ReadAllText(path).Should().Contain($"\nadd_path {index}\n");
            Snapshot(index).Should().Equal(before, "the index folder is only listed");
            AstrometryNetSetup.FindIndexFiles().Should().Equal(Path.Combine(index, "index-4207-00.fits"));

            var written = File.GetLastWriteTimeUtc(path);
            Thread.Sleep(20);
            AstrometryNetSetup.WriteConfig();
            File.GetLastWriteTimeUtc(path).Should().Be(written, "an unchanged config is not rewritten");
            Directory.GetFiles(Path.Combine(folder, "Solvers")).Should().Equal(new[] { path }, "no temporary file is left behind");
        }

        [Test]
        public void WriteConfig_RefusesWhenNoIndexFileExists() {
            var empty = Directory.CreateDirectory(Path.Combine(folder, "no index")).FullName;
            AstrometryNetSetup.IndexDirectories = new[] { empty, Path.Combine(folder, "missing") };
            AstrometryNetSetup.ConfigDirectory = Path.Combine(folder, "Solvers");

            FluentActions.Invoking(AstrometryNetSetup.WriteConfig).Should().Throw<InvalidOperationException>()
                .WithMessage($"*no index files*'{empty}'*");
            File.Exists(AstrometryNetSetup.ConfigFilePath).Should().BeFalse();
        }

        [Test]
        public void TheUsersIndexFolder_IsReadable_WhenPresent() {
            if (!Directory.Exists(AstrometryNetSetup.DefaultIndexDirectory)) {
                Assert.Ignore($"No index folder at {AstrometryNetSetup.DefaultIndexDirectory}");
            }
            AstrometryNetSetup.IndexDirectories = new[] { AstrometryNetSetup.DefaultIndexDirectory };
            var files = AstrometryNetSetup.FindIndexFiles();
            TestContext.Out.WriteLine($"{files.Count} index files in {AstrometryNetSetup.DefaultIndexDirectory}: " +
                string.Join(", ", files.Select(f => Path.GetFileNameWithoutExtension(f)).GroupBy(n => n.Length > 10 ? n[..10] : n).Select(g => $"{g.Key} x{g.Count()}")));
            files.Should().NotBeEmpty();
            files.Should().OnlyContain(f => Path.GetFileName(f).StartsWith("index-", StringComparison.Ordinal) && f.EndsWith(".fits", StringComparison.Ordinal));
        }

        private static List<string> Snapshot(string dir) {
            return Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => $"{Path.GetFileName(f)} {new FileInfo(f).Length} {File.GetLastWriteTimeUtc(f):O}").ToList();
        }
    }
}
