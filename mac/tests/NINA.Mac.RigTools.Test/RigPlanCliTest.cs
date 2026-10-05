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
using NINA.Mac.RigTools.Cli;
using NUnit.Framework;
using System;
using System.IO;

namespace NINA.Mac.RigTools.Test {

    [TestFixture]
    public class RigPlanCliTest {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 4, 18, 30, 0, TimeSpan.FromHours(8));

        private static string Run(params string[] args) {
            var w = new StringWriter();
            Program.Run(args, w, Now).Should().Be(0);
            return w.ToString();
        }

        [Test]
        public void Tonight_PrintsTheSixClassicTargets() {
            var text = Run("--no-samples");

            text.Should().Contain("Night of Sun 2026-10-04");
            foreach (var name in new[] { "M42", "M83", "NGC 253", "M8 ", "M20", "Omega Centauri" }) {
                text.Should().Contain(name);
            }
            text.Should().NotContain("    time   alt");
        }

        [Test]
        public void Table_ReproducesThePlanSection6Numbers() {
            var text = Run("table");

            text.Should().Contain("1101.5 px");
            text.Should().MatchRegex(@"-30\s+37\.8°\s+10\.6 s\s+11\.4 s\s+13\.4 s\s+16\.6 s\s+65°");
            text.Should().MatchRegex(@"\+0\s+67\.8°\s+5\.1 s\s+7\.4 s\s+14\.7 s\s+28\.7 s");
            text.Should().MatchRegex(@"\+10\s+77\.8°\s+2\.9 s\s+7\.1 s\s+22\.3 s\s+64\.7 s\s+126°");
        }

        [Test]
        public void Table_IsIdenticalWithTheReducer() {
            Run("table", "--reducer").Should().Contain("1575 mm").And.Contain("10.6 s");
            Run("table", "--reducer", "1617").Should().Contain("1617 mm");
            Run("table").Replace("ASI585MC @ 2500 mm (f/10)", "X").Replace("0.479\"/px, 15.31' x 8.61'", "Y")
                .Should().Be(Run("table", "--reducer").Replace("ASI585MC @ 1575 mm (f/6.3 reducer)", "X").Replace("0.760\"/px, 24.31' x 13.67'", "Y"));
        }

        [Test]
        public void CustomTargetAndOptions() {
            var text = Run("--date", "2026-12-15", "--target", "M1,05:34:31.94,+22:00:52.2", "--twilight", "nautical", "--horizon", "flat", "--max-alt", "70", "--every", "60");

            text.Should().Contain("Night of Tue 2026-12-15");
            text.Should().Contain("Sun below -12°");
            text.Should().Contain("altitude 15°-70°");
            text.Should().Contain("Zenith keyhole");
            text.Should().NotContain("M42");
            text.Should().NotContain("PLACEHOLDER");
        }

        [Test]
        public void HorizonCommand_PrintsTheProfile_AndReportsBadLinesOfAFile() {
            Run("horizon").Should().Contain("PLACEHOLDER").And.Contain("60.1    25");

            var path = Path.Combine(Path.GetTempPath(), "rigplan-" + Guid.NewGuid().ToString("N") + ".hrz");
            try {
                File.WriteAllText(path, "0 30\n180 5   # sea\nbad line here\n360 30\n");
                var text = Run("horizon", "--horizon", path);
                text.Should().Contain("warning: horizon line 3: expected 2 values").And.Contain("180     5");
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void HorizonFileWithoutZeroOr360_IsReportedByEveryCommand() {
            // Review F1: the 0/360 grooming copies an end point instead of interpolating across north.
            var path = Path.Combine(Path.GetTempPath(), "rigplan-" + Guid.NewGuid().ToString("N") + ".hrz");
            try {
                File.WriteAllText(path, "15 10\n30 10\n60 15\n90 15\n180 4\n270 15\n300 60\n330 60\n345 60\n");
                const string warning = "warning: horizon file has no point at azimuth 0 or 360: both get 10°, copied from azimuth 15 ";
                Run("horizon", "--horizon", path).Should().Contain(warning).And.Contain("360     10");
                Run("--no-samples", "--horizon", path).Should().StartWith(warning);
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void Help_AndUnknownArguments() {
            Run("--help").Should().Contain("usage:");
            FluentActions.Invoking(() => Program.Run(new[] { "--frobnicate" }, new StringWriter(), Now)).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => Program.Run(new[] { "--date" }, new StringWriter(), Now)).Should().Throw<ArgumentException>();
        }

        private static (int Exit, string Output, string Error) Execute(params string[] args) {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = Program.Execute(args, output, error, Now);
            return (exit, output.ToString(), error.ToString());
        }

        [Test]
        public void Mw4HorizonFiles_StringNumbersLoad_AndBadFilesExitWith2() {
            // Review F2: both inputs used to escape Main as unhandled exceptions (exit 134).
            var dir = Path.Combine(Path.GetTempPath(), "rigplan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var strings = Path.Combine(dir, "str.hpts");
                File.WriteAllText(strings, "[[\"10\",\"0\"],[\"20\",\"180\"]]");
                var ok = Execute("horizon", "--horizon", strings);
                ok.Exit.Should().Be(0);
                ok.Error.Should().BeEmpty();
                ok.Output.Should().Contain("180     20").And.Contain("360     10");

                var truncated = Path.Combine(dir, "truncated.hpts");
                File.WriteAllText(truncated, "[[10, 0], [20, 180");
                var bad = Execute("horizon", "--horizon", truncated);
                bad.Exit.Should().Be(2);
                bad.Output.Should().BeEmpty();
                bad.Error.Should().StartWith($"rigplan: MW4-formatted horizon file {truncated} is not valid JSON: ");

                File.WriteAllText(truncated, "[[true, 0], [20, 180]]");
                Execute("--horizon", truncated).Exit.Should().Be(2);
            } finally {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void BadArgumentsAndFiles_ExitWith2_WithAMessage() {
            foreach (var args in new[] {
                new[] { "--frobnicate" },
                new[] { "--date" },
                new[] { "--date", "2026-13-01" },
                new[] { "--bin", "99999999999" },
                new[] { "--horizon", Path.Combine(Path.GetTempPath(), "rigplan-missing-" + Guid.NewGuid().ToString("N") + ".hrz") },
                new[] { "--horizon", Path.GetTempPath() },
                new[] { "--no-samples", "--steps", "0/-5/5" },
                new[] { "--no-samples", "--steps", "5/NaN" } }) {
                var result = Execute(args);
                result.Exit.Should().Be(2, string.Join(" ", args));
                result.Error.Should().StartWith("rigplan: ", string.Join(" ", args));
            }
        }

        [Test]
        public void NonPositiveSteps_AreRejectedNotRecommended() {
            // Review F4: '--steps 0/-5/5' used to print '-> use 0 s' and hide the rotation-limit note.
            var result = Execute("--date", "2026-10-04", "--no-samples", "--target", "Dec+5,1.0,5", "--steps", "0/-5/5");

            result.Exit.Should().Be(2);
            result.Output.Should().BeEmpty();
            result.Error.Should().Be("rigplan: Exposure steps must be finite and positive seconds, got 0" + Environment.NewLine);
        }

        [Test]
        public void UnreadableHorizonFile_ExitsWith2() {
            if (OperatingSystem.IsWindows()) {
                Assert.Ignore("Uses Unix file modes.");
                return;
            }
            var path = Path.Combine(Path.GetTempPath(), "rigplan-" + Guid.NewGuid().ToString("N") + ".hrz");
            File.WriteAllText(path, "0 10\n180 20\n");
            try {
                File.SetUnixFileMode(path, UnixFileMode.None);
                try {
                    File.ReadAllText(path);
                    Assert.Ignore("The file is still readable (running as root?), so this cannot be tested here.");
                } catch (UnauthorizedAccessException) {
                }
                var result = Execute("horizon", "--horizon", path);
                result.Exit.Should().Be(2);
                result.Error.Should().StartWith("rigplan: ");
            } finally {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Delete(path);
            }
        }
    }
}
