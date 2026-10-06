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
using NINA.Mac.Siril.Test.Synthetic;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.Siril.Test {

    /// <summary>
    /// "Stack last night" end to end with the real siril-cli (ignored when it is not installed): a synthetic night with two
    /// targets, raw darks in the library that the run first builds into a master, and <see cref="SirilNightProcessor"/>
    /// matching, generating, running and reporting. One target has flats, dark flats and a matching dark; the other was shot
    /// with a cooler that never reached its set point, so no dark matches and it is stacked without one, as the report says.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class SirilNightTest {
        private string tmp;
        private SessionLayout layout;
        private SyntheticRig rig;
        private SirilNightReport report;
        private readonly ConcurrentQueue<string> streamed = new();
        private static readonly DateOnly Night = new(2026, 10, 3);

        private static DateTime Hkt(int y, int mo, int d, int h, int mi, int s = 0) =>
            TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified), SessionLayout.HongKong);

        [OneTimeSetUp]
        public async Task ShootANightAndStackIt() {
            TestEnv.RequireSiril();
            tmp = TestEnv.NewTempDirectory("night");
            layout = new SessionLayout(new SessionLayoutOptions { Root = Path.Combine(tmp, "Astro Nightglass") });
            rig = new SyntheticRig();

            // NGC 253: 8 x 20 s with drift and alt-az field rotation, flats and dark flats at dawn
            var start = Hkt(2026, 10, 3, 21, 0);
            for (var k = 0; k < 8; k++) {
                var offset = new FrameOffset(3.0 * Math.Sin(k) + 0.3, 2.0 * Math.Cos(k) - 0.2, (k - 3.5) * 0.15);
                NinaFrames.Save(layout, NinaFrames.Info("LIGHT", start.AddSeconds(30 * k), k + 1, 20, "NGC 253", rig), rig, rig.Light(20, offset, 100 + k));
            }
            for (var k = 0; k < 8; k++) {
                NinaFrames.Save(layout, NinaFrames.Info("FLAT", Hkt(2026, 10, 4, 4, 40).AddSeconds(5 * k), k + 1, 1.0, "NGC 253", rig), rig, rig.Flat(200 + k));
                NinaFrames.Save(layout, NinaFrames.Info("DARK", Hkt(2026, 10, 4, 4, 45).AddSeconds(5 * k), k + 1, 1.0, "NGC 253", rig, darkFlat: true), rig, rig.Dark(1.0, 300 + k));
            }
            // M 42: 6 x 20 s with the cooler unpowered (sensor at 24 °C, set point 0 °C)
            for (var k = 0; k < 6; k++) {
                var info = NinaFrames.Info("LIGHT", Hkt(2026, 10, 4, 2, 0).AddSeconds(30 * k), k + 1, 20, "M 42", rig);
                info.SensorTemperature = 24.0;
                NinaFrames.Save(layout, info, rig, rig.Light(20, new FrameOffset(k * 0.7, -k * 0.4, 0), 400 + k));
            }
            // Raw darks in the library (20 s, 0 °C); no master yet: the run builds it
            for (var k = 0; k < 12; k++) {
                NinaFrames.Save(layout, NinaFrames.Info("DARK", Hkt(2026, 9, 28, 22, 0).AddSeconds(30 * k), k + 1, 20, null, rig), rig, rig.Dark(20, 500 + k));
            }

            report = await SirilNightProcessor.RunAsync(layout, Night, TestEnv.Runner(tmp), log: streamed.Enqueue);
            foreach (var line in report.Lines) {
                TestContext.Progress.WriteLine(line);
            }
            foreach (var t in report.Targets.Where(t => t.Result?.Run != null)) {
                TestEnv.Print(t.Result.Run);
            }
        }

        [OneTimeTearDown]
        public void Cleanup() => TestEnv.Delete(tmp);

        [Test]
        public void TheMissingMasterIsBuiltFirst_FromTheLibrarysRawDarks() {
            report.MasterBuild.Should().NotBeNull("the library had a raw set without its master");
            report.MasterBuild.Succeeded.Should().BeTrue(report.MasterBuild.Run?.Summary);
            layout.Library.ListMasters().Select(Path.GetFileName).Should().Equal("dark_20s_G252_O50_T0_B2.fit");
        }

        [Test]
        public void BothTargetsAreStacked_IntoOneColourImageEach() {
            report.Succeeded.Should().BeTrue(string.Join(Environment.NewLine, report.Lines));
            foreach (var target in report.Targets) {
                target.Stacks.Should().ContainSingle();
                var stack = FitsFile.ReadImage(target.Stacks[0]);
                stack.PlaneCount.Should().Be(3, "debayered RGB");
                (stack.Width, stack.Height).Should().Be((rig.Width, rig.Height));
                Path.GetDirectoryName(target.Stacks[0]).Should().Be(target.Folders.WorkingDirectory);
                Path.GetFileName(target.Stacks[0]).Should().StartWith("result_");
            }
        }

        [Test]
        public void NGC253_UsesTheLibraryMaster_TheFlats_AndTheMidSessionReference() {
            var ngc = report.Targets.Single(t => t.TargetName == "NGC 253");
            ngc.Dark.Found.Should().BeTrue(ngc.Dark.Message);
            ngc.Plan.MasterDarkPath.Should().EndWith("dark_20s_G252_O50_T0_B2.fit");
            ngc.Plan.Flat.Should().Be(FlatSource.Folder);
            ngc.Plan.FlatCalibration.Should().Be(FlatCalibration.BiasFrames);
            var script = ngc.Result.Script.Text;
            script.Should().Contain("setref pp_light 4").And.Contain("-dark=").And.Contain("-flat=").And.Contain("-debayer");
            ngc.Result.Run.LogLines.Should().Contain(l => l.Contains("Script execution finished successfully"));
        }

        [Test]
        public void M42_HasNoMatchingDark_IsStackedWithoutOne_AndTheReportSaysWhy() {
            var m42 = report.Targets.Single(t => t.TargetName == "M 42");
            m42.Dark.Found.Should().BeFalse();
            m42.Plan.Dark.Should().Be(DarkSource.None);
            m42.Notes.Should().Contain(n => n.StartsWith("processing WITHOUT a dark") && n.Contains("sensor 24.0 °C (set point 0.0 °C)"));
            m42.Notes.Should().Contain(n => n.Contains("12 V"));
            m42.Succeeded.Should().BeTrue(m42.Summary);
        }

        [Test]
        public void SirilsLog_IsStreamed_LineByLine_WithTheTarget() {
            var lines = streamed.ToArray();
            lines.Should().Contain(l => l.StartsWith("[NGC 253] ", StringComparison.Ordinal) && l.Contains("Script execution finished successfully"));
            lines.Should().Contain(l => l.StartsWith("[M 42] ", StringComparison.Ordinal));
            lines.Should().Contain(l => l.StartsWith("Siril: NGC 253: Master dark dark_20s_G252_O50_T0_B2.fit", StringComparison.Ordinal));
            lines.Should().Contain(l => l.StartsWith("Siril: NGC 253: stacked 8 lights into ", StringComparison.Ordinal));
        }
    }
}
