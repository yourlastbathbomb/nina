#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NINA.Mac.App.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;

namespace NINA.Mac.App.Diagnostics {

    /// <summary>What a rendered frame looks like, sampled on a grid.</summary>
    /// <param name="DistinctColors">Distinct colours among the samples; a blank frame has 1.</param>
    /// <param name="NonRedSamples">Samples whose green or blue channel is brighter than red (night vision must have none).</param>
    public sealed record FrameStats(int Width, int Height, int Samples, int DistinctColors, double MeanLuma, int NonRedSamples, byte MaxRed, byte MaxGreen, byte MaxBlue) {
        public bool LooksRendered => Width > 0 && Height > 0 && DistinctColors >= 4;
    }

    /// <summary>Renders the shell headlessly (Avalonia.Headless + Skia) for tests and <c>--smoke-test</c>.</summary>
    public static class ScreenRenderer {

        public static void Pump() => Dispatcher.UIThread.RunJobs();

        public static FrameStats Capture(TopLevel topLevel, string savePath = null) {
            Pump();
            using var frame = topLevel.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered (is the headless platform using Skia drawing?)");
            if (savePath != null) {
                Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                frame.Save(savePath, PngBitmapEncoderOptions.Default);
            }
            return Analyze(frame);
        }

        /// <summary>Visits every page and captures it. Returns stats per page.</summary>
        public static IReadOnlyList<(PageKind Page, FrameStats Stats)> RenderAllPages(Window window, MainWindowViewModel viewModel, string saveDirectory = null, string suffix = "") {
            var results = new List<(PageKind, FrameStats)>();
            foreach (var page in viewModel.Pages) {
                viewModel.NavigateTo(page.Kind);
                var path = saveDirectory == null ? null : Path.Combine(saveDirectory, $"{(int)page.Kind + 1}-{page.Kind}{suffix}.png");
                results.Add((page.Kind, Capture(window, path)));
            }
            return results;
        }

        public static unsafe FrameStats Analyze(WriteableBitmap bitmap) {
            using var fb = bitmap.Lock();
            var width = fb.Size.Width;
            var height = fb.Size.Height;
            var isBgra = fb.Format == PixelFormat.Bgra8888;
            var colors = new HashSet<uint>();
            var samples = 0;
            var nonRed = 0;
            double luma = 0;
            byte maxR = 0, maxG = 0, maxB = 0;
            var step = Math.Max(1, Math.Min(width, height) / 200);
            for (var y = 0; y < height; y += step) {
                var row = (byte*)fb.Address + ((long)y * fb.RowBytes);
                for (var x = 0; x < width; x += step) {
                    var p = row + (x * 4);
                    byte r = isBgra ? p[2] : p[0];
                    byte g = p[1];
                    byte b = isBgra ? p[0] : p[2];
                    colors.Add((uint)((r << 16) | (g << 8) | b));
                    luma += (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                    if (g > r + 8 || b > r + 8) {
                        nonRed++;
                    }
                    maxR = Math.Max(maxR, r);
                    maxG = Math.Max(maxG, g);
                    maxB = Math.Max(maxB, b);
                    samples++;
                }
            }
            return new FrameStats(width, height, samples, colors.Count, samples == 0 ? 0 : luma / samples, nonRed, maxR, maxG, maxB);
        }
    }
}
