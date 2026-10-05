#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NINA.Mac.App.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NINA.Mac.App.Diagnostics {

    /// <summary>What a rendered frame (or a region of it) looks like, sampled on a grid.</summary>
    /// <param name="DistinctColors">Distinct colours among the samples; a blank frame has 1.</param>
    /// <param name="NonRedSamples">Samples that are not a shade of red (<see cref="ScreenRenderer.IsNonRed"/>): white, grey, green, blue... Night vision must have none.</param>
    /// <param name="MaxGreenBlueToRed">Largest max(G,B)/R among samples brighter than near-black (night palette: at most about 0.35).</param>
    public sealed record FrameStats(int Width, int Height, int Samples, int DistinctColors, double MeanLuma, int NonRedSamples, byte MaxRed, byte MaxGreen, byte MaxBlue, double MaxGreenBlueToRed) {
        public bool LooksRendered => Width > 0 && Height > 0 && DistinctColors >= 4;
    }

    /// <summary>One page of the shell, captured.</summary>
    /// <param name="Stats">The whole window.</param>
    /// <param name="Content">Only the page host (right of the sidebar, above the status bar), so the shell chrome alone cannot pass.</param>
    /// <param name="View">Type name of the view presented for the page's view-model, or null if none is visible.</param>
    public sealed record PageFrame(PageKind Page, FrameStats Stats, FrameStats Content, string View) {
        public bool LooksRendered => Stats.LooksRendered && Content.LooksRendered && View != null;
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

        /// <summary>
        /// Visits every page and captures it. Besides the whole-window stats, each result has the stats of the page host
        /// alone and the name of the view shown there, so a page whose view failed to build or render does not pass on
        /// the strength of the sidebar and status bar.
        /// </summary>
        public static IReadOnlyList<PageFrame> RenderAllPages(Window window, MainWindowViewModel viewModel, string saveDirectory = null, string suffix = "") {
            var results = new List<PageFrame>();
            foreach (var page in viewModel.Pages) {
                viewModel.NavigateTo(page.Kind);
                var path = saveDirectory == null ? null : Path.Combine(saveDirectory, $"{(int)page.Kind + 1}-{page.Kind}{suffix}.png");
                results.Add(CapturePage(window, page, path));
            }
            return results;
        }

        private static PageFrame CapturePage(Window window, PageViewModel page, string savePath) {
            Pump();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered (is the headless platform using Skia drawing?)");
            if (savePath != null) {
                Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                frame.Save(savePath, PngBitmapEncoderOptions.Default);
            }
            return AnalyzePage(window, page, frame);
        }

        /// <summary>
        /// Stats of <paramref name="frame"/> (a capture of the whole <paramref name="window"/>, at any scale) and of its
        /// page host region, plus the view shown for <paramref name="page"/>. Used headlessly and by <c>--gui-smoke</c>.
        /// </summary>
        public static PageFrame AnalyzePage(Window window, PageViewModel page, Bitmap frame) {
            var host = window.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault(c => c.Name == "PageHost")
                ?? throw new InvalidOperationException("MainWindow has no PageHost");
            // The view the ViewLocator built for this page's view-model (its fallback is a TextBlock, not a UserControl)
            var view = host.GetVisualDescendants().OfType<UserControl>()
                .FirstOrDefault(v => ReferenceEquals(v.DataContext, page) && v.IsEffectivelyVisible && v.Bounds.Width > 0 && v.Bounds.Height > 0);
            var origin = host.TranslatePoint(new Point(0, 0), window) ?? throw new InvalidOperationException("PageHost is not in the window");
            var scale = window.Bounds.Width > 0 ? frame.PixelSize.Width / window.Bounds.Width : 1;
            var region = new PixelRect((int)(origin.X * scale), (int)(origin.Y * scale), (int)(host.Bounds.Width * scale), (int)(host.Bounds.Height * scale));
            return new PageFrame(page.Kind, Analyze(frame), Analyze(frame, region), view?.GetType().Name);
        }

        /// <summary>
        /// Not a shade of red: green or blue is more than 45 % of red, plus 8 levels of slack for anti-aliasing near black.
        /// White and grey count, as do green and blue. The night palette's colours are at most 35 % (#FF5A4E).
        /// </summary>
        public static bool IsNonRed(byte r, byte g, byte b) => Math.Max(g, b) > (0.45 * r) + 8;

        /// <summary>
        /// <see cref="Analyze(WriteableBitmap, PixelRect?)"/> for any bitmap, e.g. a <see cref="RenderTargetBitmap"/>
        /// drawn by the real platform's renderer: its pixels are copied into a BGRA buffer first.
        /// </summary>
        public static FrameStats Analyze(Bitmap bitmap, PixelRect? region = null) {
            if (bitmap is WriteableBitmap writeable) {
                return Analyze(writeable, region);
            }
            using var copy = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = copy.Lock()) {
                bitmap.CopyPixels(fb); // converts to the buffer's format
            }
            return Analyze(copy, region);
        }

        /// <summary>Samples the bitmap, or only <paramref name="region"/> of it (clipped to the bitmap), on a grid of about 200 steps.</summary>
        public static unsafe FrameStats Analyze(WriteableBitmap bitmap, PixelRect? region = null) {
            using var fb = bitmap.Lock();
            var bounds = new PixelRect(0, 0, fb.Size.Width, fb.Size.Height);
            var area = region is { } r0 ? bounds.Intersect(r0) : bounds;
            var width = area.Width;
            var height = area.Height;
            var isBgra = fb.Format == PixelFormat.Bgra8888;
            var colors = new HashSet<uint>();
            var samples = 0;
            var nonRed = 0;
            double luma = 0;
            double maxRatio = 0;
            byte maxR = 0, maxG = 0, maxB = 0;
            var step = Math.Max(1, Math.Min(width, height) / 200);
            for (var y = area.Y; y < area.Bottom; y += step) {
                var row = (byte*)fb.Address + ((long)y * fb.RowBytes);
                for (var x = area.X; x < area.Right; x += step) {
                    var p = row + (x * 4);
                    byte r = isBgra ? p[2] : p[0];
                    byte g = p[1];
                    byte b = isBgra ? p[0] : p[2];
                    colors.Add((uint)((r << 16) | (g << 8) | b));
                    luma += (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                    if (IsNonRed(r, g, b)) {
                        nonRed++;
                    }
                    if (Math.Max(r, Math.Max(g, b)) > 24) {
                        maxRatio = Math.Max(maxRatio, r == 0 ? double.PositiveInfinity : Math.Max(g, b) / (double)r);
                    }
                    maxR = Math.Max(maxR, r);
                    maxG = Math.Max(maxG, g);
                    maxB = Math.Max(maxB, b);
                    samples++;
                }
            }
            return new FrameStats(width, height, samples, colors.Count, samples == 0 ? 0 : luma / samples, nonRed, maxR, maxG, maxB, maxRatio);
        }
    }
}
