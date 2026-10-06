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
using NINA.Mac.App.Engine.Test.Fakes;
using NINA.Mac.App.Services;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine.Test {

    /// <summary>
    /// Slew and centre (Target screen): goto through the LX200 driver, then NINA's CenteringSolver with solve frames from the
    /// fake camera through NINA's ImagingVM and a fake solver that reports where the simulated mount really points.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class CentringServiceTests {

        [Test]
        public async Task SlewAndCentre_MeasuresTheGotoError_SyncsAndReslews() {
            await using var rig = new EngineRig("centre", pointingErrorRaArcmin: 12, pointingErrorDecArcmin: -7);
            await rig.ConnectAll();
            var target = Sky.At(45, 150);
            var reports = new List<string>();

            rig.Engine.Centring.CanPlateSolve.Should().BeTrue();
            var result = await rig.Engine.Centring.SlewAndCentreAsync(target.RA, target.Dec, new InlineReports(reports));

            TestContext.Out.WriteLine(string.Join(Environment.NewLine, reports));
            TestContext.Out.WriteLine(result);
            result.Centred.Should().BeTrue();
            result.Message.Should().StartWith("Centred");
            rig.Solver.Calls.Should().BeGreaterThanOrEqualTo(2, "one solve measures the 13.9' error, the next confirms the centre");
            rig.Received.Count(c => c.StartsWith(":CM", StringComparison.Ordinal)).Should().Be(1);
            rig.Received.Count(c => c.StartsWith(":MS", StringComparison.Ordinal)).Should().BeGreaterThanOrEqualTo(2, "the goto, then the re-slew after the sync");
            (rig.Truth() - target).Distance.ArcMinutes.Should().BeLessThan(1.0);
            rig.Camera.Exposures.Should().Contain(e => e.ImageType == "SNAPSHOT" && e.Gain == 450 && e.Binning.X == 2, "solve frames use the rig's plate-solve settings");
            reports.Should().Contain(r => r.StartsWith("Solved", StringComparison.Ordinal));
        }

        [Test]
        public async Task SlewAndCentre_WhenSolvingFails_LeavesTheMountOnTheGoto() {
            await using var rig = new EngineRig("centre fails");
            await rig.ConnectAll();
            rig.Solver.Fail = true;
            var target = Sky.At(45, 150);
            var result = await rig.Engine.Centring.SlewAndCentreAsync(target.RA, target.Dec);
            result.Centred.Should().BeFalse();
            result.Message.Should().Contain("goto");
            rig.Received.Should().NotContain(c => c.StartsWith(":CM", StringComparison.Ordinal));
        }

        [Test]
        public async Task SlewAndCentre_WithoutTheCamera_OnlySlews() {
            await using var rig = new EngineRig("centre no camera");
            await rig.Engine.Mount.ConnectAsync();
            var target = Sky.At(45, 150);
            var result = await rig.Engine.Centring.SlewAndCentreAsync(target.RA, target.Dec);
            result.Centred.Should().BeFalse();
            result.Message.Should().Contain("camera");
            rig.Solver.Calls.Should().Be(0);
            rig.Received.Should().Contain(c => c.StartsWith(":MS", StringComparison.Ordinal));
        }

        [Test]
        public void FrameAnalysis_MeasuresStars_AndCalibrationStatistics() {
            var frame = SyntheticSky.StarField(960, 540, 2, 0, 1);
            var lights = FrameAnalysisService.Measure(frame, 960, 540, 16, true, true, false, 2.9, 2500);
            lights.Stars.Should().BeGreaterThan(20);
            lights.Hfr.Should().BeInRange(1.0, 4.0);
            var calibration = FrameAnalysisService.Measure(frame, 960, 540, 16, true, false, false);
            calibration.Stars.Should().Be(0);
            double.IsNaN(calibration.Hfr).Should().BeTrue();
            calibration.MeanFraction.Should().BeApproximately(calibration.Mean / 65535, 1e-12);
            FluentActions.Invoking(() => FrameAnalysisService.Measure(frame, 100, 100, 16, true, true, false)).Should().Throw<ArgumentException>();
        }

        private sealed class InlineReports : IProgress<string> {
            private readonly List<string> reports;

            public InlineReports(List<string> reports) {
                this.reports = reports;
            }

            public void Report(string value) {
                lock (reports) {
                    reports.Add(value);
                }
            }
        }
    }
}
