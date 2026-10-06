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
using NINA.Astrometry;
using NINA.PlateSolving.Mac;
using System.Diagnostics;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>
    /// The ASTAP launcher: NINA's ASTAP solver passes no -d, and astap_cli only looks in /usr/local/opt/astap/ by itself, so the
    /// mac setup points ASTAPLocation at a generated sh script that adds -d. These tests run the launcher with a stand-in
    /// "astap" that records its argv, through the same ProcessStartInfo.Arguments string NINA's CLISolver builds.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class AstapSetupTest {

        private string folder = string.Empty;

        [SetUp]
        public void SetUp() {
            folder = TestHost.NewFolder("astap setup");
            AstapSetup.LauncherDirectory = Path.Combine(folder, "Solvers");
        }

        [TearDown]
        public void TearDown() {
            AstapSetup.LauncherDirectory = null;
        }

        [Test]
        public void LauncherDirectory_DefaultsToTheSolversFolderInNinasDataFolder() {
            AstapSetup.LauncherDirectory = null;
            AstapSetup.LauncherPath.Should().Be(Path.Combine(TestHost.DataRoot, "Solvers", "astap"));
        }

        [Test]
        public void Resolve_ReturnsTheExecutable_WhenTheDatabaseIsWhereAstapLooksByItself() {
            var exe = SolverKit.WriteScript(Path.Combine(folder, "bin", "astap_cli"), "exit 0\n");

            AstapSetup.Resolve(exe, null).Should().Be(exe);
            AstapSetup.Resolve(exe, "").Should().Be(exe);
            AstapSetup.Resolve(exe, "/usr/local/opt/astap/").Should().Be(exe);
            File.Exists(AstapSetup.LauncherPath).Should().BeFalse();
        }

        [Test]
        public void Resolve_WritesAnExecutableLauncher_OnlyWhenItChanges() {
            var exe = SolverKit.WriteScript(Path.Combine(folder, "bin", "astap_cli"), "exit 0\n");
            var database = Directory.CreateDirectory(Path.Combine(folder, "star database", "d80")).FullName;

            var launcher = AstapSetup.Resolve(exe, database + "/");

            launcher.Should().Be(AstapSetup.LauncherPath);
            File.ReadAllText(launcher).Should().Be(
                "#!/bin/sh\n" +
                "# ASTAP launcher written by Nightglass (NINA.Platesolving on macOS). NINA's ASTAP solver cannot pass the star\n" +
                "# database folder (-d), and ASTAP's macOS builds only look in /usr/local/opt/astap/ by themselves.\n" +
                $"exec '{exe}' -d '{database}' \"$@\"\n");
            File.GetUnixFileMode(launcher).Should().HaveFlag(UnixFileMode.UserExecute);

            var written = File.GetLastWriteTimeUtc(launcher);
            Thread.Sleep(20);
            AstapSetup.Resolve(exe, database).Should().Be(launcher);
            File.GetLastWriteTimeUtc(launcher).Should().Be(written, "an unchanged launcher is not rewritten");

            var other = Directory.CreateDirectory(Path.Combine(folder, "d50")).FullName;
            AstapSetup.Resolve(exe, other);
            File.ReadAllText(launcher).Should().Contain($"-d '{other}'");
            Directory.GetFiles(Path.GetDirectoryName(launcher)!).Should().Equal(new[] { launcher }, "no temporary file is left behind");
        }

        [Test]
        public void Resolve_RejectsAMissingExecutableOrDatabase() {
            var exe = SolverKit.WriteScript(Path.Combine(folder, "bin", "astap_cli"), "exit 0\n");

            FluentActions.Invoking(() => AstapSetup.Resolve(Path.Combine(folder, "nope"), null)).Should().Throw<FileNotFoundException>();
            FluentActions.Invoking(() => AstapSetup.Resolve("", null)).Should().Throw<FileNotFoundException>();
            FluentActions.Invoking(() => AstapSetup.Resolve(exe, Path.Combine(folder, "no database"))).Should().Throw<DirectoryNotFoundException>();
        }

        [Test]
        public void Launcher_PassesNinasArgumentsThroughUnchanged_WithSpacesAndQuotesInEveryPath() {
            // A stand-in astap in a folder whose name has a space and a single quote, a database folder with spaces, and an
            // image path with spaces (NINA's working folder is under "Application Support")
            var record = Path.Combine(folder, "argv.txt");
            var exe = SolverKit.WriteScript(Path.Combine(folder, "it's here", "astap_cli"),
                $"for a in \"$@\"; do printf '%s\\n' \"$a\"; done > {SolverKit.Sq(record)}\n");
            var database = Directory.CreateDirectory(Path.Combine(folder, "Star Databases", "d80")).FullName;
            var launcher = AstapSetup.Resolve(exe, database);
            var image = Path.Combine(folder, "Application Support", "PlateSolver", "a b.fits");
            var parameter = SolverKit.RigParameter(SolverKit.Coordinates(202.4696, 47.1952));
            var arguments = new TestableAstapSolver(launcher).Arguments(image, Path.ChangeExtension(image, ".ini"), parameter, SolverKit.ImageProperties(parameter));

            // As CLISolver.StartCLI starts it: FileName + one Arguments string, no shell
            using (var process = Process.Start(new ProcessStartInfo(launcher, arguments) { UseShellExecute = false, RedirectStandardOutput = true })!) {
                process.StandardOutput.ReadToEnd();
                process.WaitForExit(10000).Should().BeTrue();
                process.ExitCode.Should().Be(0);
            }

            File.ReadAllLines(record).Should().Equal(
                "-d", database,
                "-f", image,
                "-fov", "0.14356",
                "-z", "2",
                "-s", "500",
                "-r", "5",
                "-ra", Math.Round(parameter.Coordinates.RA, 6).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-spd", Math.Round(parameter.Coordinates.Dec + 90, 6).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        [TestCase("plain", "'plain'")]
        [TestCase("/a b/c", "'/a b/c'")]
        [TestCase("it's", "'it'\\''s'")]
        [TestCase("$HOME `x` \"q\"", "'$HOME `x` \"q\"'")]
        public void ShellQuote_KeepsTheTextVerbatim(string text, string quoted) {
            AstapSetup.ShellQuote(text).Should().Be(quoted);
        }

        [Test]
        public void ShellQuote_RoundTripsThroughSh() {
            const string text = "a 'b' \"c\" $d `e` \\f";
            using var process = Process.Start(new ProcessStartInfo("/bin/sh") {
                ArgumentList = { "-c", "printf '%s' " + AstapSetup.ShellQuote(text) },
                UseShellExecute = false,
                RedirectStandardOutput = true
            })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            output.Should().Be(text);
        }
    }
}
