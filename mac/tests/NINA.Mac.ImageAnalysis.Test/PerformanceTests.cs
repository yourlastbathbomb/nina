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
using NINA.Mac.ImageAnalysis.AccordPort;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Linq;

namespace NINA.Mac.ImageAnalysis.Test {

    /// <summary>
    /// M5 budget: full analysis (statistics, debayer, stretch, star detection and measurement) of a 1920 x 1080 bin-2
    /// frame in under 1 s on the M1 Air. Median of 5 runs after one warm-up run. Meaningful in Release only.
    /// </summary>
    [TestFixture, NonParallelizable, Category("Performance")]
    public class PerformanceTests {

        private static PixelBuffer Frame(int width, int height, BayerPattern pattern, int stars) {
            var sky = new SkyFrame(width, height) { Background = 1500, GradientX = 300, ReadNoise = 8, Gain = 1, Seed = 99, BayerPattern = pattern, Supersample = 1 };
            sky.AddStarField(stars, i => Psf.Gaussian(4 + (i % 5)), 2e3, 2e6, 25, 30, 98);
            return sky.Render();
        }

        private static (double MedianMs, FrameAnalysis Last) Time(PixelBuffer frame, FrameAnalysisOptions options) {
            FrameAnalyzer.Analyze(frame, options); // warm-up (JIT, thread pool)
            var times = new double[5];
            FrameAnalysis last = null;
            for (int i = 0; i < times.Length; i++) {
                var sw = Stopwatch.StartNew();
                last = FrameAnalyzer.Analyze(frame, options);
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            return (times[2], last);
        }

        private static string Configuration() {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }

        [TestCase(BayerPattern.RGGB, false, TestName = "Bin2Osc_1920x1080_DefaultProfile")]
        [TestCase(BayerPattern.RGGB, true, TestName = "Bin2Osc_1920x1080_RigOptics")]
        [TestCase(BayerPattern.None, false, TestName = "Bin2MonoBin_1920x1080_DefaultProfile")]
        public void FullAnalysis_Bin2Frame_UnderOneSecond(BayerPattern pattern, bool rigOptics) {
            var frame = Frame(1920, 1080, pattern, 300);
            var options = rigOptics ? FrameAnalysisOptions.ForRig() : new FrameAnalysisOptions();
            var (median, last) = Time(frame, options);
            TestContext.Out.WriteLine($"[{Configuration()}] {TestContext.CurrentContext.Test.Name}: median {median:F0} ms " +
                $"(last run: stats {last.StatisticsTime.TotalMilliseconds:F0}, render {last.RenderTime.TotalMilliseconds:F0}, detect {last.DetectionTime.TotalMilliseconds:F0} ms; {last.Stars.StarList.Count} stars, resize {last.Stars.ResizeFactor:F3})");
            median.Should().BeLessThan(1000);
        }

        [Test]
        public void FullAnalysis_Bin1Osc_3840x2160_Informational() {
            var frame = Frame(3840, 2160, BayerPattern.RGGB, 600);
            var (median, last) = Time(frame, new FrameAnalysisOptions());
            TestContext.Out.WriteLine($"[{Configuration()}] bin-1 OSC 3840x2160: median {median:F0} ms (stats {last.StatisticsTime.TotalMilliseconds:F0}, render {last.RenderTime.TotalMilliseconds:F0}, detect {last.DetectionTime.TotalMilliseconds:F0} ms; {last.Stars.StarList.Count} stars)");
            median.Should().BeLessThan(4000);
        }

        [Test]
        public void Bahtinov_200x200Crop_IsFast() {
            var img = BahtinovPattern.Render(200, 200, 100.4, 99.6, 62, 17, 4, seed: 1);
            BahtinovAnalyzer.Analyze(img);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) {
                BahtinovAnalyzer.Analyze(img);
            }
            double ms = sw.Elapsed.TotalMilliseconds / 10;
            TestContext.Out.WriteLine($"[{Configuration()}] Bahtinov 200x200: {ms:F1} ms per analysis");
            ms.Should().BeLessThan(100);
        }
    }
}
