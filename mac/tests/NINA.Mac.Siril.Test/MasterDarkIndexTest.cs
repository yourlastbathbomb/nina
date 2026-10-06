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

    /// <summary>
    /// The dark-library index without Siril: masters written with NINA-like headers (as Siril keeps the first dark's keys in
    /// the stack), lights from <see cref="NinaFrames"/>, and the lookup a night's lights need.
    /// </summary>
    [TestFixture]
    public class MasterDarkIndexTest {
        private string tmp;
        private SessionLayout layout;
        private SyntheticRig rig;

        [SetUp]
        public void SetUp() {
            tmp = TestEnv.NewTempDirectory("dark-index");
            layout = new SessionLayout(new SessionLayoutOptions { Root = Path.Combine(tmp, "Astro") });
            rig = new SyntheticRig(64, 48);
        }

        [TearDown]
        public void TearDown() => TestEnv.Delete(tmp);

        private string Master(string name, double exposure, int gain, int offset, int bin, double? setTemp, double? ccdTemp, string camera = "ZWO ASI585MC Pro",
                              int stack = 30, int width = 64, int height = 48, bool withKeys = true) {
            Directory.CreateDirectory(layout.Library.MastersDirectory);
            var w = new FitsWriter().Add("IMAGETYP", "DARK");
            if (withKeys) {
                w.Add("EXPTIME", exposure).Add("XBINNING", bin).Add("GAIN", gain).Add("OFFSET", offset);
                if (setTemp.HasValue) {
                    w.Add("SET-TEMP", setTemp.Value);
                }
                if (ccdTemp.HasValue) {
                    w.Add("CCD-TEMP", ccdTemp.Value);
                }
                if (camera != null) {
                    w.Add("INSTRUME", camera);
                }
            }
            w.Add("STACKCNT", stack);
            var path = Path.Combine(layout.Library.MastersDirectory, name);
            w.Write(path, new ushort[width * height], width, height);
            return path;
        }

        /// <summary>Lights of one target; returns their settings and the first light's header.</summary>
        private (FrameSettings Settings, FitsHeader First) Lights(double exposure = 20, double setPoint = 0, double sensor = 0.1, int count = 5, string target = "NGC 253") {
            var start = new DateTime(2026, 10, 3, 13, 0, 0, DateTimeKind.Utc);
            string first = null;
            for (var k = 0; k < count; k++) {
                var info = NinaFrames.Info("LIGHT", start.AddSeconds(30 * k), k + 1, exposure, target, rig);
                info.SetPoint = setPoint;
                info.SensorTemperature = sensor;
                var path = NinaFrames.Save(layout, info, rig, new ushort[rig.Width * rig.Height]);
                first ??= path;
            }
            var folders = layout.GetTargetFolders(new DateOnly(2026, 10, 3), target);
            return (MasterDarkIndex.ReadLights(folders.Lights), FitsFile.ReadHeader(first));
        }

        [Test]
        public void ExactMatch_IsTheMasterSirilsTemplateWouldPick() {
            Master("dark_20s_G252_O50_T0_B2.fit", 20, 252, 50, 2, 0, 0.2);
            Master("dark_10s_G252_O50_T0_B2.fit", 10, 252, 50, 2, 0, 0.1);
            var (lights, first) = Lights();

            var match = MasterDarkIndex.Scan(layout.Library).Find(lights, firstLight: first);

            match.Found.Should().BeTrue(match.Message);
            match.Master.FileName.Should().Be("dark_20s_G252_O50_T0_B2.fit");
            match.SirilTemplateName.Should().Be("dark_20s_G252_O50_T0_B2.fit");
            match.NeedsExplicitPath.Should().BeFalse();
            match.Message.Should().StartWith("Master dark dark_20s_G252_O50_T0_B2.fit").And.Contain("30 frames");
            match.Rejected.Should().ContainSingle(r => r.Master.FileName == "dark_10s_G252_O50_T0_B2.fit" && r.Why.Contains("exposure 10 s"));
        }

        [Test]
        public void TemperatureWithinTolerance_Matches_ButSirilsTemplateWouldNot() {
            Master("dark_20s_G252_O50_T-1_B2.fit", 20, 252, 50, 2, -1.5, -1.4);
            var (lights, first) = Lights(setPoint: 0, sensor: 0.1);

            var match = MasterDarkIndex.Scan(layout.Library).Find(lights, firstLight: first);

            match.Found.Should().BeTrue(match.Message);
            match.SirilTemplateName.Should().Be("dark_20s_G252_O50_T0_B2.fit", "Siril would look for T0 and abort");
            match.NeedsExplicitPath.Should().BeTrue();
        }

        [Test]
        public void CoolerThatDidNotHoldTheSetPoint_NoMatch_AndTheMessageSaysWhyAndWhatToShoot() {
            Master("dark_20s_G252_O50_T0_B2.fit", 20, 252, 50, 2, 0, 0.0);
            var (lights, first) = Lights(setPoint: 0, sensor: 24.3);

            var match = MasterDarkIndex.Scan(layout.Library).Find(lights, firstLight: first);

            match.Found.Should().BeFalse();
            TestContext.Out.WriteLine(match.Message);
            match.Message.Should().Contain("No master dark matches lights at 20 s, gain 252, offset 50, bin 2, sensor 24.3 °C (set point 0.0 °C)")
                .And.Contain("Nearest: dark_20s_G252_O50_T0_B2.fit (taken at 0.0 °C, the lights at 24.3 °C (tolerance 2 °C))")
                .And.Contain("Shoot 30-50 darks of 20 s at gain 252, offset 50, bin 2, within 2 °C of 24.3 °C")
                .And.Contain(layout.Library.DarksDirectory);
            match.Warnings.Should().Contain(w => w.Contains("did not hold it") && w.Contains("12 V"));
        }

        [Test]
        public void AnotherCamera_OrAnotherSize_IsNotUsed() {
            Master("dark_20s_G252_O50_T0_B2.fit", 20, 252, 50, 2, 0, 0.0, camera: "ZWO ASI533MC Pro");
            Master("dark_20s_G252_O50_T0_B2 copy.fit", 20, 252, 50, 2, 0, 0.0, width: 32);
            var (lights, _) = Lights();

            var match = MasterDarkIndex.Scan(layout.Library).Find(lights);

            match.Found.Should().BeFalse();
            match.Rejected.Select(r => r.Why).Should().Contain(w => w.Contains("camera ZWO ASI533MC Pro")).And.Contain(w => w.Contains("32x48 pixels"));
            MasterDarkIndex.Scan(layout.Library).Find(lights, new DarkMatchOptions { RequireSameCamera = false })
                .Master.FileName.Should().Be("dark_20s_G252_O50_T0_B2.fit", "the camera check can be relaxed");
        }

        [Test]
        public void TwoCandidates_TheCloserTemperatureWins_ThenMoreFrames() {
            Master("a_warm.fit", 20, 252, 50, 2, 0, 1.8, stack: 50);
            Master("b_close.fit", 20, 252, 50, 2, 0, 0.3, stack: 10);
            Master("c_close_more.fit", 20, 252, 50, 2, 0, 0.3, stack: 40);
            var (lights, _) = Lights(sensor: 0.1);

            var match = MasterDarkIndex.Scan(layout.Library).Find(lights);

            match.Master.FileName.Should().Be("c_close_more.fit");
            match.Rejected.Should().HaveCount(2).And.OnlyContain(r => r.Why.Contains("also fits"));
        }

        [Test]
        public void HeaderWithoutTheKeys_TheDefaultFileNameSupplies_Them() {
            Master("dark_20s_G252_O50_T0_B2.fit", 0, 0, 0, 0, 0, null, withKeys: false);
            var (lights, _) = Lights();

            var match = MasterDarkIndex.Scan(layout.Library).Find(lights);

            match.Found.Should().BeTrue(match.Message);
            match.Warnings.Should().Contain(w => w.Contains("EXPTIME taken from the file name")).And.Contain(w => w.Contains("no INSTRUME"));
        }

        [Test]
        public void MasterOfUnknownTemperature_IsNotUsedForLightsOfKnownTemperature() {
            // a master dropped into masters/ by hand: right exposure, gain, offset, binning and camera, but no SET-TEMP or
            // CCD-TEMP and a name the default template does not parse, so nothing says how cold it was
            Master("master_dark_20s.fit", 20, 252, 50, 2, setTemp: null, ccdTemp: null, stack: 40);
            var library = MasterDarkIndex.Scan(layout.Library);
            library.Masters.Single().Settings.EffectiveTemperature.Should().BeNull("the test needs a master of unknown temperature");

            // no 12 V: the lights are warm although the set point was 0 °C
            var (warm, _) = Lights(setPoint: 0, sensor: 24.0);
            var match = library.Find(warm);
            TestContext.Out.WriteLine(match.Message);
            match.Found.Should().BeFalse("a master that may be a 0 °C dark must not calibrate 24 °C lights");
            match.Rejected.Should().ContainSingle(r => r.Master.FileName == "master_dark_20s.fit" && r.Why.Contains("temperature unknown") && r.Why.Contains("24.0 °C"));
            match.Message.Should().Contain("No master dark matches").And.Contain("Shoot 30-50 darks of 20 s").And.Contain("within 2 °C of 24.0 °C");

            // even lights whose cooler held 0 °C cannot be checked against it, so it is not used for them either
            var cold = MasterDarkIndex.Scan(layout.Library).Find(Lights(setPoint: 0, sensor: 0.1, target: "M 42").Settings);
            cold.Found.Should().BeFalse(cold.Message);
            cold.Rejected.Single().Why.Should().Contain("SET-TEMP").And.Contain("dark_20s_G252_O50_T0_B2.fit", "the fix: a header temperature or the default file name");
        }

        [Test]
        public void LightsOfUnknownTemperature_StillUseTheMaster_WithAWarning() {
            Master("dark_20s_G252_O50_T0_B2.fit", 20, 252, 50, 2, 0, 0.1);
            var lights = new FrameSettings { ExposureSeconds = 20, Gain = 252, Offset = 50, BinX = 2, Width = 64, Height = 48, Camera = "ZWO ASI585MC Pro", FrameCount = 5 };

            var match = MasterDarkIndex.Scan(layout.Library).Find(lights);

            match.Found.Should().BeTrue(match.Message);
            match.Warnings.Should().Contain(w => w.Contains("lights' temperature is unknown"));
        }

        [Test]
        public void EmptyLibrary_SaysSo() {
            var (lights, _) = Lights();
            var match = MasterDarkIndex.Scan(layout.Library).Find(lights);
            match.Found.Should().BeFalse();
            match.Message.Should().Contain("The library has no masters").And.Contain("Shoot 30-50 darks of 20 s");
        }

        [Test]
        public void Prepare_ChoosesCalibrationPerTarget_AndSaysWhatIsMissing() {
            Master("dark_20s_G252_O50_T0_B2.fit", 20, 252, 50, 2, 0, 0.1);
            Lights(target: "NGC 253");
            Lights(target: "M 42", exposure: 10, count: 4);
            Lights(target: "Too few", count: 2);
            var night = new DateOnly(2026, 10, 3);
            var t = layout.GetTargetFolders(night, "NGC 253");
            for (var k = 0; k < 3; k++) {
                var flat = NinaFrames.Info("FLAT", new DateTime(2026, 10, 3, 20, 40, k, DateTimeKind.Utc), k + 1, 1, "NGC 253", rig);
                NinaFrames.Save(layout, flat, rig, new ushort[rig.Width * rig.Height]);
                var darkFlat = NinaFrames.Info("DARK", new DateTime(2026, 10, 3, 20, 45, k, DateTimeKind.Utc), k + 1, 1, "NGC 253", rig, darkFlat: true);
                NinaFrames.Save(layout, darkFlat, rig, new ushort[rig.Width * rig.Height]);
            }

            var report = SirilNightProcessor.Prepare(layout, night);
            foreach (var line in report.Lines) {
                TestContext.Out.WriteLine(line);
            }

            report.Targets.Select(x => x.TargetName).Should().Equal("M 42", "NGC 253", "Too few");
            var ngc = report.Targets.Single(x => x.TargetName == "NGC 253");
            ngc.SkipReason.Should().BeNull();
            ngc.Plan.Dark.Should().Be(DarkSource.Library);
            ngc.Plan.MasterDarkPath.Should().Be(Path.Combine(layout.Library.MastersDirectory, "dark_20s_G252_O50_T0_B2.fit"));
            ngc.Plan.Flat.Should().Be(FlatSource.Folder);
            ngc.Plan.FlatCalibration.Should().Be(FlatCalibration.BiasFrames);
            ngc.Plan.Registration.Should().Be(RegistrationReference.MiddleFrame, "field rotation: register on the mid-session frame");
            var script = SirilScriptGenerator.Generate(ngc.Plan).Text;
            script.Should().Contain("\"-dark=" + Path.Combine(layout.Library.MastersDirectory, "dark_20s_G252_O50_T0_B2.fit") + "\"", "the explicit master, quoted (the path has a space)")
                .And.Contain("setref pp_light 3").And.NotContain("$EXPTIME");

            var m42 = report.Targets.Single(x => x.TargetName == "M 42");
            m42.Plan.Dark.Should().Be(DarkSource.None);
            m42.Notes.Should().Contain(n => n.StartsWith("processing WITHOUT a dark: No master dark matches lights at 10 s"))
                .And.Contain(n => n.StartsWith("no flats")).And.Contain(n => n.StartsWith("no calibration at all"));

            report.Targets.Single(x => x.TargetName == "Too few").SkipReason.Should().Contain("only 2 light(s)");

            SirilNightProcessor.Prepare(layout, night, new SirilNightOptions { ProcessWithoutDark = false })
                .Targets.Single(x => x.TargetName == "M 42").SkipReason.Should().StartWith("No master dark matches");
        }
    }
}
