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
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace NINA.Mac.Siril.Test {

    /// <summary>Runs the real siril-cli 1.4.4 on tiny scripts.</summary>
    [TestFixture]
    public class SirilRunnerTest {
        private string dir;

        [SetUp]
        public void SetUp() {
            TestEnv.RequireSiril();
            dir = TestEnv.NewTempDirectory("runner");
        }

        [TearDown]
        public void TearDown() => TestEnv.Delete(dir);

        private string Script(string name, string text) {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, text);
            return path;
        }

        [Test]
        public async Task SuccessfulScript_IsReportedAsSuccess_AndPythonNoiseIsIgnored() {
            var runner = TestEnv.Runner(dir);
            var run = await runner.RunAsync(Script("ok.ssf", "requires 1.3.4\nsetext fit\nclose\n"), dir);
            TestEnv.Print(run);

            run.Succeeded.Should().BeTrue();
            run.ExitCode.Should().Be(0);
            run.FinishedSuccessfully.Should().BeTrue();
            run.ErrorLines.Should().NotContain(l => l.Contains("Python"));
            File.ReadAllText(run.LogPath).Should().Contain("Script execution finished successfully");
            File.ReadAllText(runner.Options.OwnConfigPath).Should().Contain($"wd={dir}", "siril-cli writes its working directory into the ini given with -i");
        }

        [Test]
        public async Task FailingCommand_IsReportedWithLineAndCommand() {
            var runner = TestEnv.Runner(dir);
            var run = await runner.RunAsync(Script("bad.ssf", "requires 1.3.4\n# comment\nload /nonexistent/nothing_here.fit\nclose\n"), dir);
            TestEnv.Print(run);

            run.Succeeded.Should().BeFalse();
            run.ExitCode.Should().NotBe(0);
            run.ReportedFailure.Should().BeTrue();
            run.FailedLine.Should().Be(3);
            run.FailedCommand.Should().Be("load");
            run.Summary.Should().Contain("line 3 ('load')");
        }

        [Test]
        public async Task ScriptWithoutRequires_IsRefused_BecauseSirilWouldSkipItAndReportSuccess() {
            var runner = TestEnv.Runner(dir);
            var path = Script("norequires.ssf", "# x\nsetext fit\nclose\n");

            await FluentActions.Awaiting(() => runner.RunAsync(path, dir)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*requires*");
        }

        [Test]
        public async Task SirilItself_SkipsAScriptWithoutRequires_AndStillReportsSuccess() {
            // Why the runner refuses such scripts: observed siril-cli 1.4.4 behaviour, run here without the runner's check
            var ini = Path.Combine(dir, "raw.ini");
            File.WriteAllText(ini, string.Empty);
            var script = Script("skipped.ssf", "load /nonexistent/nothing_here.fit\nclose\n");
            var start = new System.Diagnostics.ProcessStartInfo(SirilRunnerOptions.DefaultSirilCli) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-o", "-i", ini, "-d", dir, "-s", script }) {
                start.ArgumentList.Add(a);
            }
            using var process = System.Diagnostics.Process.Start(start);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdout + await stderr;

            process.ExitCode.Should().Be(0);
            output.Should().Contain("\"requires\" command is missing").And.Contain("Script execution finished successfully").And.NotContain("Running command: load");
        }

        [Test]
        public void OwnConfig_MayNotBeTheSirilGuiConfig() {
            var runner = new SirilRunner(new SirilRunnerOptions { OwnConfigPath = SirilRunnerOptions.UserSirilConfig });
            FluentActions.Invoking(() => runner.PrepareConfig()).Should().Throw<InvalidOperationException>();
        }

        [Test]
        public async Task DefaultSeed_CopiesTheUserConfig_AndLeavesTheOriginalUntouched() {
            var user = SirilRunnerOptions.UserSirilConfig;
            if (!File.Exists(user)) {
                Assert.Ignore($"No Siril GUI configuration at {user}");
            }
            var before = SHA256.HashData(File.ReadAllBytes(user));
            var mtime = File.GetLastWriteTimeUtc(user);
            var runner = new SirilRunner(new SirilRunnerOptions { OwnConfigPath = Path.Combine(dir, "own.ini"), Timeout = TimeSpan.FromMinutes(2) });

            var run = await runner.RunAsync(Script("ok.ssf", "requires 1.3.4\nsetext fit\nclose\n"), dir);
            TestEnv.Print(run);

            run.Succeeded.Should().BeTrue();
            SHA256.HashData(File.ReadAllBytes(user)).Should().Equal(before, "the GUI configuration is only read");
            File.GetLastWriteTimeUtc(user).Should().Be(mtime);
            run.LogLines.Should().Contain(l => l.Contains("Reading configuration file") && l.Contains(Path.Combine(dir, "own.ini")));
            File.ReadAllLines(Path.Combine(dir, "own.ini")).Should().Contain($"wd={dir}");
        }

        [Test]
        public void Parse_ExtractsMissingFilesAndFailure() {
            var result = new SirilRunResult();
            SirilRunner.Parse(result, new[] {
                "log: ERROR: Python validation failed.",
                "log: /lib/masters/dark_30s_G252_O50_T0_B2.fit.[any_allowed_extension] not found.",
                "log: Error in line 30 ('calibrate'): invalid arguments.",
                "log: Exiting batch processing.",
                "log: Script execution failed.",
            }.ToList());

            result.MissingFiles.Should().Equal("/lib/masters/dark_30s_G252_O50_T0_B2.fit");
            result.FailedLine.Should().Be(30);
            result.FailedCommand.Should().Be("calibrate");
            result.FailureReason.Should().Be("invalid arguments");
            result.ReportedFailure.Should().BeTrue();
            result.ErrorLines.Should().HaveCount(2).And.NotContain(l => l.Contains("Python"));
        }
    }
}
