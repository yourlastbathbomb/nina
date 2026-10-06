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
using Moq;
using NINA.Core.Model;
using NINA.Mac.Sequencer.Test.Sim;
using NINA.Mac.Sequencing.Horizon;
using NINA.Profile.Interfaces;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// The engine's horizon reader (<see cref="HorizonFile"/>) against upstream's: a file with inline comments, which upstream's
    /// CustomHorizon.FromReader_Standard truncates, loads completely; unreadable lines are reported with their numbers; Save
    /// writes files upstream reads identically; and the headless host puts the mac reading into the profile, where every
    /// consumer (AboveHorizonCondition, WaitUntilAboveHorizon, PlanValidator, the LX200 slew guard) looks.
    /// </summary>
    [TestFixture]
    public class HorizonFileTest {

        /// <summary>Deep Water Bay style: blocked north, low over the water to the south, comments after the values.</summary>
        private const string InlineCommented =
            "# Deep Water Bay backyard, measured by eye\n" +
            "0 60      # house\n" +
            "60 60     # house corner\n" +
            "\n" +
            "90 25 # tree\n" +
            "135 12\n" +
            "180 4     # over the water\n" +
            "225 10    # hillside\n" +
            "270 30\n" +
            "300 60    # neighbour\n" +
            "360 60\n";

        private static readonly double[] Azimuths = Enumerable.Range(0, 73).Select(i => i * 5.0).ToArray();

        private static string WriteTemp(string name, string text) {
            var folder = TestHost.NewFolder("horizon");
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, text);
            return path;
        }

        [Test]
        public void InlineComments_UpstreamDropsThePoints_TheEngineReaderKeepsThemAll() {
            var path = WriteTemp("inline.hrz", InlineCommented);

            var upstream = CustomHorizon.FromFilePath(path);
            var mac = HorizonFile.Load(path);

            mac.Succeeded.Should().BeTrue();
            mac.RejectedLines.Should().BeEmpty("inline comments are comments");
            mac.Points.Select(p => (p.AzimuthDeg, p.AltitudeDeg)).Should().Equal(
                (0.0, 60.0), (60.0, 60.0), (90.0, 25.0), (135.0, 12.0), (180.0, 4.0), (225.0, 10.0), (270.0, 30.0), (300.0, 60.0), (360.0, 60.0));
            mac.Horizon!.GetAltitude(180).Should().Be(4);
            mac.Horizon.GetAltitude(157.5).Should().Be(8);
            mac.Horizon.GetMinAltitude().Should().Be(4);

            // upstream kept only "135 12", "270 30" and "360 60" (and copied 360 to 0): the south is wrong by up to 8°
            upstream.GetAltitude(180).Should().BeApproximately(12 + (30 - 12) * 45.0 / 135.0, 1e-9, "upstream interpolates 135..270 because 180 4 was dropped");
            upstream.GetMinAltitude().Should().Be(12);
        }

        [Test]
        public void UnreadableLines_AreReportedWithTheirLineNumbers_TheRestLoads() {
            var result = HorizonFile.Parse("0 10\n90 abc   # typo\n180 5 7\n\n270 12\n", "typo.hrz");

            result.Succeeded.Should().BeTrue();
            result.RejectedLines.Select(i => i.LineNumber).Should().Equal(2, 3);
            result.RejectedLines[0].Reason.Should().Contain("invalid altitude 'abc'");
            result.RejectedLines[1].Reason.Should().Contain("expected 2 values");
            result.Summary.Should().Contain("2 line(s) not read").And.Contain("line 2").And.Contain("line 3");
            result.Horizon!.GetAltitude(270).Should().Be(12);
        }

        [Test]
        public void Unusable_Files_SayWhy() {
            HorizonFile.Load(Path.Combine(TestHost.NewFolder("horizon-missing"), "none.hrz")).Error.Should().Be("the file does not exist");
            HorizonFile.Parse("# only a comment\n90 10\n", "one.hrz").Error.Should().Contain("not contain enough entries");
            HorizonFile.Load(WriteTemp("bad.hpts", "[[10, 0], [95, 180]]")).Error.Should().Contain("Invalid altitude 95");
        }

        [Test]
        public void Save_WritesWhatUpstreamReadsTheSame_CommentsOnTheirOwnLines() {
            var loaded = HorizonFile.Load(WriteTemp("inline.hrz", InlineCommented));
            var saved = Path.Combine(TestHost.NewFolder("horizon-save"), "saved.hrz");

            HorizonFile.Save(saved, loaded, new[] { "Deep Water Bay", "saved by the engine\nsecond line" });

            var lines = File.ReadAllLines(saved);
            lines.Take(3).Should().Equal("# Deep Water Bay", "# saved by the engine", "# second line");
            lines.Skip(3).Should().OnlyContain(l => !l.Contains('#'), "values never carry a comment");
            var upstream = CustomHorizon.FromFilePath(saved);
            var again = HorizonFile.Load(saved);
            again.RejectedLines.Should().BeEmpty();
            foreach (var az in Azimuths) {
                upstream.GetAltitude(az).Should().Be(loaded.Horizon!.GetAltitude(az), $"azimuth {az}");
                again.Horizon!.GetAltitude(az).Should().Be(loaded.Horizon.GetAltitude(az));
            }

            FluentActions.Invoking(() => HorizonFile.Save(saved, new[] { (10.0, 5.0) })).Should().Throw<ArgumentException>().WithMessage("*at least two*");
            FluentActions.Invoking(() => HorizonFile.Save(saved, new[] { (10.0, 5.0), (400.0, 5.0) })).Should().Throw<ArgumentException>().WithMessage("*azimuth 400*");
            FluentActions.Invoking(() => HorizonFile.Save(saved, new[] { (10.0, double.NaN), (20.0, 5.0) })).Should().Throw<ArgumentException>();
        }

        [Test]
        public void Watcher_ReplacesUpstreamsReading_FollowsThePath_AndKeepsAnInMemoryHorizonWithoutAFile() {
            var path = WriteTemp("inline.hrz", InlineCommented);
            var profile = new NINA.Profile.Profile("horizon watcher");
            var service = new Mock<IProfileService>();
            service.SetupGet(x => x.ActiveProfile).Returns(profile);

            // no file: a horizon a host set in memory stays
            var inMemory = CustomHorizon.FromReader_Standard(new StringReader("0 20\n360 20\n"));
            profile.AstrometrySettings.Horizon = inMemory;
            using (new ProfileHorizonWatcher(service.Object)) {
                profile.AstrometrySettings.Horizon.Should().BeSameAs(inMemory);
            }

            // what upstream's profile deserialisation leaves: the truncated reading
            profile.AstrometrySettings.HorizonFilePath = path;
            profile.AstrometrySettings.Horizon = CustomHorizon.FromFilePath(path);
            using var watcher = new ProfileHorizonWatcher(service.Object);
            profile.AstrometrySettings.Horizon!.GetAltitude(180).Should().Be(4, "the engine's reading replaced upstream's");
            watcher.Last!.Succeeded.Should().BeTrue();

            // a new path is loaded at once
            var other = WriteTemp("flat.hrz", "0 7 # all round\n360 7\n");
            profile.AstrometrySettings.HorizonFilePath = other;
            profile.AstrometrySettings.Horizon!.GetAltitude(180).Should().Be(7);

            // the same path with new content: HorizonChanged reloads it
            File.WriteAllText(other, "0 9\n360 9 # higher\n");
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddSeconds(5));
            service.Raise(x => x.HorizonChanged += null, EventArgs.Empty);
            profile.AstrometrySettings.Horizon!.GetAltitude(180).Should().Be(9);

            // a broken file: no horizon, and the error is kept
            File.WriteAllText(other, "# nothing\n");
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddSeconds(10));
            service.Raise(x => x.HorizonChanged += null, EventArgs.Empty);
            profile.AstrometrySettings.Horizon.Should().BeNull();
            watcher.Last!.Error.Should().Contain("not contain enough entries");
            profile.AstrometrySettings.HorizonFilePath.Should().Be(other, "the path stays, so the settings show which file failed");

            // the path emptied: no horizon, as upstream's ChangeHorizon("")
            profile.AstrometrySettings.HorizonFilePath = path;
            profile.AstrometrySettings.Horizon.Should().NotBeNull();
            profile.AstrometrySettings.HorizonFilePath = string.Empty;
            profile.AstrometrySettings.Horizon.Should().BeNull();
        }

        [Test]
        public async Task HeadlessHost_LoadsTheProfilesHorizonWithTheEngineReader_ForEveryConsumer() {
            var path = WriteTemp("inline.hrz", InlineCommented);
            await using var rig = SimRig.CreateUnconnected("horizon-host", configure: p => {
                p.AstrometrySettings.HorizonFilePath = path;
                p.AstrometrySettings.Horizon = CustomHorizon.FromFilePath(path);   // what upstream's deserialisation made of it
            });

            var horizon = rig.ProfileService.ActiveProfile.AstrometrySettings.Horizon;
            horizon!.GetAltitude(180).Should().Be(4, "the host replaced upstream's truncated reading");
            rig.Host.Horizon.Last!.Points.Should().HaveCount(9);
            foreach (var az in Azimuths) {
                horizon.GetAltitude(az).Should().Be(HorizonFile.Load(path).Horizon!.GetAltitude(az));
            }
        }
    }
}
