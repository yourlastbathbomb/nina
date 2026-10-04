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
using System.Linq;

namespace NINA.Mac.Siril.Test {

    [TestFixture]
    public class SirilSessionValidatorTest {
        private string root;
        private SessionLayout layout;
        private SyntheticRig rig;
        private TargetFolders target;
        private int frame;

        [SetUp]
        public void SetUp() {
            root = TestEnv.NewTempDirectory("validator");
            layout = new SessionLayout(new SessionLayoutOptions { Root = root });
            rig = new SyntheticRig(64, 48); // header values only; frames below are 16x12
            target = layout.GetTargetFolders(new DateOnly(2026, 10, 3), "NGC 253");
            frame = 0;
        }

        [TearDown]
        public void TearDown() => TestEnv.Delete(root);

        private void Write(string type, double exposure, Action<FrameInfo> tweak = null, int width = 16, bool bayer = true, double ccdTemp = 0.1) {
            var info = NinaFrames.Info(type, new DateTime(2026, 10, 3, 13, 0, 0, DateTimeKind.Utc).AddMinutes(frame), ++frame, exposure, "NGC 253", rig);
            info.SensorTemperature = ccdTemp;
            tweak?.Invoke(info);
            var header = NinaFrames.Header(info, rig);
            var path = layout.GetFramePath(info);
            if (!bayer) {
                // Mono-bin: NINA writes no BAYERPAT; rebuild the header without it
                header = new FitsWriter().Add("IMAGETYP", "LIGHT").Add("EXPTIME", exposure).Add("GAIN", info.Gain).Add("OFFSET", info.Offset)
                    .Add("SET-TEMP", 0.0).Add("XBINNING", 2).Add("YBINNING", 2).Add("ROWORDER", "TOP-DOWN");
            }
            header.Write(path, new ushort[width * 12], width, 12);
        }

        private void WriteMaster(string name, int width = 16) {
            new FitsWriter().Add("IMAGETYP", "DARK").Write(Path.Combine(layout.Library.MastersDirectory, name), new ushort[width * 12], width, 12);
        }

        private SirilPreprocessingPlan Plan() => SirilPreprocessingPlan.ForTarget(target, layout.Library);

        [Test]
        public void CompleteSession_HasNoErrors() {
            for (var i = 0; i < 3; i++) {
                Write("LIGHT", 20);
                Write("FLAT", 1);
                Write("DARK", 1, f => f.IsDarkFlat = true);
            }
            WriteMaster("dark_20s_G252_O50_T0_B2.fit");

            SirilSessionValidator.Validate(Plan()).Should().BeEmpty();
        }

        [Test]
        public void MixedExposures_MissingMaster_AndStrayImages_AreErrors() {
            Write("LIGHT", 20);
            Write("LIGHT", 30);
            Write("LIGHT", 20, ccdTemp: 3.0);
            File.WriteAllText(Path.Combine(target.Lights, "preview.jpg"), "x");
            WriteMaster("dark_10s_G252_O50_T0_B2.fit");
            var plan = Plan();
            plan.Flat = FlatSource.None;

            var issues = SirilSessionValidator.Validate(plan);

            issues.Should().Contain(i => i.Severity == SirilIssueSeverity.Error && i.Message.StartsWith("Lights differ in EXPTIME"));
            issues.Should().Contain(i => i.Severity == SirilIssueSeverity.Error && i.Message.Contains("preview.jpg"));
            issues.Should().Contain(i => i.Severity == SirilIssueSeverity.Error && i.Message.Contains("No master dark dark_20s_G252_O50_T0_B2.fit")
                && i.Message.Contains("Available: dark_10s_G252_O50_T0_B2.fit"));
            issues.Should().Contain(i => i.Severity == SirilIssueSeverity.Warning && i.Message.Contains("CCD-TEMP 3.0"));
        }

        [Test]
        public void MonoBinLights_SizeMismatches_AndEmptyCalibrationFolders_AreErrors() {
            for (var i = 0; i < 3; i++) {
                Write("LIGHT", 20, bayer: false);
            }
            Write("FLAT", 1, width: 20);
            WriteMaster("dark_20s_G252_O50_T0_B2.fit", width: 20);

            var issues = SirilSessionValidator.Validate(Plan()).Select(i => i.Message).ToList();

            issues.Should().Contain(m => m.Contains("no BAYERPAT"));
            issues.Should().Contain(m => m.Contains("flats/") && m.Contains("is 20x12, lights are 16x12"));
            issues.Should().Contain(m => m.Contains("No biases in"));
            issues.Should().Contain(m => m.Contains("dark_20s_G252_O50_T0_B2.fit is 20x12"));
        }

        [Test]
        public void CleanProcessDirectory_OnlyDeletesTheWorkingDirectorysProcessFolder() {
            var plan = Plan();
            Directory.CreateDirectory(target.Process);
            File.WriteAllText(Path.Combine(target.Process, "light_00001.fit"), "x");
            Write("LIGHT", 20);

            SirilPreprocessor.CleanProcessDirectory(plan);

            Directory.Exists(target.Process).Should().BeFalse();
            SessionLayout.ListFitsFrames(target.Lights).Should().HaveCount(1);

            plan.ProcessDirectory = target.Lights;
            FluentActions.Invoking(() => SirilPreprocessor.CleanProcessDirectory(plan)).Should().Throw<InvalidOperationException>();
            SessionLayout.ListFitsFrames(target.Lights).Should().HaveCount(1);
        }
    }
}
