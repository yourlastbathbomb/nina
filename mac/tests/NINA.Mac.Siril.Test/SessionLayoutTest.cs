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
using System.IO;

namespace NINA.Mac.Siril.Test {

    [TestFixture]
    public class SessionLayoutTest {
        private const string Root = "/Volumes/Astro SSD/NINA";
        private SessionLayout layout;
        private SyntheticRig rig;

        [SetUp]
        public void SetUp() {
            layout = new SessionLayout(new SessionLayoutOptions { Root = Root });
            rig = new SyntheticRig(64, 48);
        }

        private static DateTime Hkt(int y, int mo, int d, int h, int mi, int s = 0) =>
            TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified), SessionLayout.HongKong);

        [TestCase(2026, 10, 3, 19, 30, 0, "2026-10-03")]   // dusk; Siril's UTC-based $DATE-OBS:dm12$ would say 10-02
        [TestCase(2026, 10, 3, 23, 59, 59, "2026-10-03")]
        [TestCase(2026, 10, 4, 0, 0, 0, "2026-10-03")]     // midnight does not start a new night
        [TestCase(2026, 10, 4, 5, 30, 0, "2026-10-03")]    // dawn flats
        [TestCase(2026, 10, 4, 11, 59, 59, "2026-10-03")]
        [TestCase(2026, 10, 4, 12, 0, 0, "2026-10-04")]    // rolls over at local noon
        public void NightOf_RollsOverAtLocalNoonHongKongTime(int y, int mo, int d, int h, int mi, int s, string expected) {
            var utc = Hkt(y, mo, d, h, mi, s);
            utc.Kind.Should().Be(DateTimeKind.Utc);

            SessionLayout.NightLabel(layout.NightOf(utc)).Should().Be(expected);
            SessionLayout.NightLabel(layout.NightOf(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified))).Should().Be(expected, "unspecified kind is UTC");
            SessionLayout.NightLabel(layout.NightOf(utc.ToLocalTime())).Should().Be(expected, "local kind is converted");
        }

        [Test]
        public void FramePaths_FollowTheSirilLayout() {
            var start = Hkt(2026, 10, 3, 21, 0, 5);
            var light = NinaFrames.Info("LIGHT", start, 1, 20, "NGC 253", rig);
            var snapshot = NinaFrames.Info("SNAPSHOT", start, 2, 2, "NGC 253", rig);
            var flat = NinaFrames.Info("FLAT", Hkt(2026, 10, 4, 4, 40), 3, 1.5, "NGC 253", rig);
            var darkFlat = NinaFrames.Info("DARK", Hkt(2026, 10, 4, 4, 45), 4, 1.5, "NGC 253", rig, darkFlat: true);
            var bias = NinaFrames.Info("BIAS", Hkt(2026, 10, 4, 4, 50), 5, 0.0001, "NGC 253", rig);
            var dark = NinaFrames.Info("DARK", Hkt(2026, 9, 28, 22, 10), 6, 20, null, rig);
            var nightFlat = NinaFrames.Info("FLAT", Hkt(2026, 10, 4, 5, 0), 7, 1.5, "", rig);
            var untitled = NinaFrames.Info("LIGHT", start, 8, 20, null, rig);

            layout.GetFramePath(light).Should().Be($"{Root}/2026-10-03/NGC 253/lights/2026-10-03_21-00-05_20.00s_2x2_g252_0.10C_0001.fits");
            layout.GetFramePath(snapshot).Should().Be($"{Root}/2026-10-03/NGC 253/snapshots/2026-10-03_21-00-05_2.00s_2x2_g252_0.10C_0002.fits");
            layout.GetFramePath(flat).Should().Be($"{Root}/2026-10-03/NGC 253/flats/2026-10-04_04-40-00_1.50s_2x2_g252_0.10C_0003.fits");
            layout.GetFramePath(darkFlat).Should().Be($"{Root}/2026-10-03/NGC 253/biases/2026-10-04_04-45-00_1.50s_2x2_g252_0.10C_0004.fits");
            layout.GetFramePath(bias).Should().Be($"{Root}/2026-10-03/NGC 253/biases/2026-10-04_04-50-00_0.00s_2x2_g252_0.10C_0005.fits");
            layout.GetFramePath(dark).Should().Be($"{Root}/library/darks/20.00s_g252_o50_0.00C_2x2/2026-09-28_22-10-00_20.00s_2x2_g252_0.10C_0006.fits");
            layout.GetFramePath(nightFlat).Should().Be($"{Root}/2026-10-03/flats/2026-10-04_05-00-00_1.50s_2x2_g252_0.10C_0007.fits");
            layout.GetFramePath(untitled).Should().Be($"{Root}/2026-10-03/untitled/lights/2026-10-03_21-00-05_20.00s_2x2_g252_0.10C_0008.fits");

            SessionLayout.Classify(darkFlat).Should().Be(FrameKind.DarkFlat);
            SessionLayout.Classify(dark).Should().Be(FrameKind.Dark);
            layout.Library.MastersDirectory.Should().Be($"{Root}/library/masters");
            layout.Library.MasterDarkPathTemplate.Should().Be($"{Root}/library/masters/dark_$EXPTIME:%d$s_G$GAIN:%d$_O$OFFSET:%d$_T$SET-TEMP:%d$_B$XBINNING:%d$.fit");
        }

        [Test]
        public void Snapshots_NeverLandInLights() {
            var snapshot = NinaFrames.Info("SNAPSHOT", Hkt(2026, 10, 3, 21, 0), 1, 2, "M 42", rig);
            var target = layout.GetTargetFolders(snapshot);

            layout.GetFrameDirectory(snapshot).Should().Be(target.Snapshots).And.NotBe(target.Lights);
        }

        [TestCase("NGC 253", "NGC 253")]
        [TestCase("Thor's Helmet", "Thor's Helmet")]
        [TestCase("C/2020 F3 NEOWISE", "C-2020 F3 NEOWISE")]
        [TestCase("M42: \"Orion\" $5", "M42_ _Orion_ _5")]
        [TestCase("  .hidden  ", "_hidden")]
        [TestCase("   ", "untitled")]
        public void SanitizeFolderName_KeepsSpacesAndApostrophes_RemovesWhatBreaksSiril(string name, string expected) {
            SessionLayout.SanitizeFolderName(name).Should().Be(expected);
        }

        [Test]
        public void UnknownImageType_Throws() {
            var frame = NinaFrames.Info("FOCUS", DateTime.UtcNow, 1, 1, "x", rig);
            FluentActions.Invoking(() => layout.GetFramePath(frame)).Should().Throw<ArgumentException>();
        }

        [Test]
        public void TargetFolders_FallBackToNightLevelFlatsAndBiases() {
            var root = TestEnv.NewTempDirectory("layout");
            try {
                var local = new SessionLayout(new SessionLayoutOptions { Root = root });
                var night = new DateOnly(2026, 10, 3);
                var target = local.GetTargetFolders(night, "NGC 253");

                target.ResolveFlatsDirectory().Should().Be(target.Flats, "nothing anywhere: the target's own folder");

                Directory.CreateDirectory(target.NightFlats);
                File.WriteAllText(Path.Combine(target.NightFlats, "a.fits"), "x");
                File.WriteAllText(Path.Combine(target.NightFlats, ".DS_Store"), "x");
                target.ResolveFlatsDirectory().Should().Be(target.NightFlats, "shared flats of the night");

                Directory.CreateDirectory(target.Flats);
                File.WriteAllText(Path.Combine(target.Flats, "b.fit"), "x");
                target.ResolveFlatsDirectory().Should().Be(target.Flats, "the target's own flats win");
                target.ResolveBiasesDirectory().Should().Be(target.Biases);
            } finally {
                TestEnv.Delete(root);
            }
        }

        [Test]
        public void NinaPatterns_WithSlashSeparators_ProduceTheLayoutPaths() {
            var start = Hkt(2026, 10, 3, 22, 15, 30);
            foreach (var (type, exposure) in new[] { ("LIGHT", 20.0), ("FLAT", 1.5), ("BIAS", 0.0001), ("DARK", 30.0) }) {
                var frame = NinaFrames.Info(type, start, 12, exposure, "NGC 253", rig);
                var expanded = Path.Combine(Root, NinaFilePatterns.Expand(NinaFilePatterns.PatternFor(type), frame, SessionLayout.HongKong)) + ".fits";

                expanded.Should().Be(layout.GetFramePath(frame), type);
            }
        }

        [TestCase("NGC 253", true)]
        [TestCase("Thor's Helmet", true)]
        [TestCase("C/2020 F3 NEOWISE", true)]       // both map '/' to '-'
        [TestCase("  M 42 ", true)]                 // both trim
        [TestCase("M42: Orion", true)]              // both write '_' for Windows' invalid characters (fork CoreUtil patch)
        [TestCase("Sh2-155 \"Cave\"", true)]
        [TestCase("M31*Core?<|>", true)]
        [TestCase("Price $5", false)]               // NINA keeps '$', the layout writes '_' (Siril token delimiter)
        [TestCase(".hidden", false)]
        [TestCase("tab\there", true)]               // control characters are in Windows' invalid set
        public void NinaPatterns_GiveTheLayoutPaths_ExactlyWhenNinaKeepsTheFolderName(string name, bool same) {
            var start = Hkt(2026, 10, 3, 22, 15, 30);

            SessionLayout.KeepsNinaFolderName(name).Should().Be(same);
            foreach (var (type, exposure) in new[] { ("LIGHT", 20.0), ("FLAT", 1.5), ("BIAS", 0.0001) }) {
                var frame = NinaFrames.Info(type, start, 12, exposure, name, rig);
                var nina = Path.Combine(Root, NinaFilePatterns.Expand(NinaFilePatterns.PatternFor(type), frame, SessionLayout.HongKong)) + ".fits";
                var ours = layout.GetFramePath(frame);

                (nina == ours).Should().Be(same, $"{type} of '{name}': NINA pattern {nina}, layout {ours}");
            }
        }

        [Test]
        public void NinaPatterns_PutUntitledLightsInTheNightFolder_TheLayoutInUntitled() {
            var frame = NinaFrames.Info("LIGHT", Hkt(2026, 10, 3, 22, 15, 30), 1, 20, null, rig);

            SessionLayout.KeepsNinaFolderName(null).Should().BeFalse();
            NinaFilePatterns.Expand(NinaFilePatterns.Light, frame, SessionLayout.HongKong).Should().StartWith("2026-10-03/lights/");
            layout.GetFramePath(frame).Should().StartWith($"{Root}/2026-10-03/untitled/lights/");
        }

        [Test]
        public void ImageTypeDirToken_RoutesSnapshotsAndDarkFlats_WhereStockPatternsCannot() {
            var start = Hkt(2026, 10, 3, 22, 15, 30);
            var snapshot = NinaFrames.Info("SNAPSHOT", start, 1, 2, "NGC 253", rig);
            var darkFlat = NinaFrames.Info("DARK", start, 2, 1.5, "NGC 253", rig, darkFlat: true);

            // Stock: snapshots go through FilePattern into lights/, dark flats (IMAGETYP DARK) into the dark library
            NinaFilePatterns.Expand(NinaFilePatterns.PatternFor("SNAPSHOT"), snapshot, SessionLayout.HongKong).Should().Contain("/lights/");
            NinaFilePatterns.Expand(NinaFilePatterns.PatternFor("DARK"), darkFlat, SessionLayout.HongKong).Should().StartWith("library/darks/1.50s_");

            // Proposed $$IMAGETYPEDIR$$ (+ the engine's dark-flat flag): the layout's folders
            (Path.Combine(Root, NinaFilePatterns.Expand(NinaFilePatterns.WithImageTypeDir, snapshot, SessionLayout.HongKong)) + ".fits")
                .Should().Be(layout.GetFramePath(snapshot));
            (Path.Combine(Root, NinaFilePatterns.Expand(NinaFilePatterns.WithImageTypeDir, darkFlat, SessionLayout.HongKong)) + ".fits")
                .Should().Be(layout.GetFramePath(darkFlat));
        }

        [Test]
        public void UpstreamDefaultPattern_IsOneFileNameOnMacOS_ButFoldersWithTheProposedSplit() {
            var frame = NinaFrames.Info("LIGHT", Hkt(2026, 10, 3, 22, 15, 30), 3, 20, "NGC 253", rig);

            // Upstream splits on CoreUtil.PATHSEPARATORS = { DirectorySeparatorChar, AltDirectorySeparatorChar } (CoreUtil.cs:31)
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }.Should().AllBeEquivalentTo('/', "on macOS both separators are '/'");
            Path.GetInvalidFileNameChars().Should().NotContain('\\', "so '\\' survives as a file-name character");
            NinaFilePatterns.UpstreamDefault.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .Should().HaveCount(1, "upstream writes one file whose name contains backslashes (research SIR-01)");

            NinaFilePatterns.Expand(NinaFilePatterns.UpstreamDefault, frame, SessionLayout.HongKong)
                .Should().Be("2026-10-03/LIGHT/2026-10-03_22-15-30__0.10_20.00s_0003", "the proposed split on both separators gives folders");
        }
    }
}
