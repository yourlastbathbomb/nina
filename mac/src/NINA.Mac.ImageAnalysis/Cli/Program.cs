#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.ImageAnalysis.AccordPort;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace NINA.Mac.ImageAnalysis.Cli {

    internal static class Program {

        private const string Usage = @"nina-ia: N.I.N.A. star detection / HFR / Bahtinov on FITS files (macOS port, M5)

usage: nina-ia analyze <file.fits> [options]

options:
  --sensitivity Normal|High|Highest   star sensitivity (profile default High)
  --noise None|Median|Normal|High|Highest   noise reduction (default None)
  --pixel <um> --focal <mm>           optics for High sensitivity (default: FITS XPIXSZ/XBINNING, FOCALLEN)
  --no-optics                         ignore optics (High then resizes as if the scale were unknown)
  --no-debayer | --linked | --no-debayered-hfr   NINA image settings (defaults: debayer, unlinked, debayered HFR)
  --plane <0|1|2|lum>                 plane of a 3-plane file (default lum = mean of the planes)
  --crop x,y,w,h                      analyse a sub-frame
  --bin2                              2x2 software bin keeping the Bayer mosaic (ZWO colour bin)
  --monobin2                          2x2 average to mono (ZWO mono bin)
  --stars <n>                         list the n stars with the highest mean brightness
  --bahtinov x,y,w,h [--robust]       Bahtinov analysis of a crop of the stretched frame
  --repeat <n>                        time n runs
";

        private static int Main(string[] args) {
            try {
                if (args.Length < 2 || args[0] != "analyze") {
                    Console.Write(Usage);
                    return args.Length == 0 ? 0 : 2;
                }
                return Analyze(args[1], ParseOptions(args.Skip(2).ToArray()));
            } catch (Exception ex) {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        private static Dictionary<string, string> ParseOptions(string[] args) {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++) {
                if (!args[i].StartsWith("--")) {
                    throw new ArgumentException($"unexpected argument {args[i]}");
                }
                string key = args[i].Substring(2);
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
                options[key] = hasValue ? args[++i] : "true";
            }
            return options;
        }

        private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        private static PixelRect Rect(string s) {
            var p = s.Split(',').Select(int.Parse).ToArray();
            return new PixelRect(p[0], p[1], p[2], p[3]);
        }

        private static int Analyze(string path, Dictionary<string, string> o) {
            var sw = Stopwatch.StartNew();
            var fits = FitsFile.Read(path);
            double readMs = sw.Elapsed.TotalMilliseconds;

            ushort[] data;
            string plane = o.GetValueOrDefault("plane", "lum");
            if (fits.Planes == 1) {
                data = fits.Data[0];
            } else if (plane == "lum") {
                data = new ushort[fits.Width * fits.Height];
                for (int i = 0; i < data.Length; i++) {
                    int sum = 0;
                    for (int p = 0; p < fits.Planes; p++) {
                        sum += fits.Data[p][i];
                    }
                    data[i] = (ushort)(sum / fits.Planes);
                }
            } else {
                data = fits.Data[int.Parse(plane)];
            }

            var pattern = fits.Planes == 1 ? BayerPatternUtility.Parse(fits.GetString("BAYERPAT")) : BayerPattern.None;
            pattern = BayerPatternUtility.ApplyOffsets(pattern, (int)fits.GetDouble("XBAYROFF", 0), (int)fits.GetDouble("YBAYROFF", 0));
            var frame = new PixelBuffer(data, fits.Width, fits.Height, 16, pattern);
            if (string.Equals(fits.GetString("ROWORDER"), "BOTTOM-UP", StringComparison.OrdinalIgnoreCase)) {
                frame = frame.FlipVertical();
            }
            if (o.TryGetValue("crop", out var crop)) {
                var r = Rect(crop);
                frame = frame.Crop(r.X, r.Y, r.Width, r.Height);
            }
            double binning = Math.Max(1, fits.GetDouble("XBINNING", 1));
            if (o.ContainsKey("bin2")) {
                frame = Bin2(frame, mono: false);
                binning *= 2;
            } else if (o.ContainsKey("monobin2")) {
                frame = Bin2(frame, mono: true);
                binning *= 2;
            }

            var options = new FrameAnalysisOptions {
                StarSensitivity = Enum.Parse<StarSensitivity>(o.GetValueOrDefault("sensitivity", "High"), true),
                NoiseReduction = Enum.Parse<NoiseReduction>(o.GetValueOrDefault("noise", "None"), true),
                DebayerImage = !o.ContainsKey("no-debayer"),
                UnlinkedStretch = !o.ContainsKey("linked"),
                DebayeredHFR = !o.ContainsKey("no-debayered-hfr")
            };
            if (!o.ContainsKey("no-optics")) {
                // NINA's FITS reader stores the unbinned pixel size (XPIXSZ / XBINNING)
                options.PixelSizeMicrons = o.TryGetValue("pixel", out var px) ? D(px) : fits.GetDouble("XPIXSZ") / Math.Max(1, fits.GetDouble("XBINNING", 1));
                options.FocalLengthMm = o.TryGetValue("focal", out var fl) ? D(fl) : fits.GetDouble("FOCALLEN");
            }

            Console.WriteLine($"file      {path}");
            Console.WriteLine($"fits      {fits.Width}x{fits.Height}x{fits.Planes} BITPIX {fits.BitPix}, BAYERPAT {fits.GetString("BAYERPAT") ?? "-"}, ROWORDER {fits.GetString("ROWORDER") ?? "-"}, " +
                $"INSTRUME {fits.GetString("INSTRUME") ?? "-"}, EXPTIME {fits.GetString("EXPTIME") ?? "-"}, read {readMs:F0} ms");
            Console.WriteLine($"frame     {frame.Width}x{frame.Height}, pattern {frame.BayerPattern}, binning {binning}, pixel {options.PixelSizeMicrons} um, focal {options.FocalLengthMm} mm" +
                (double.IsNaN(options.PixelSizeMicrons) || double.IsNaN(options.FocalLengthMm) ? "" : $" -> {StarDetector.ArcsecPerPixel(options.PixelSizeMicrons, options.FocalLengthMm) * binning:F3}\"/px (NINA uses the unbinned {StarDetector.ArcsecPerPixel(options.PixelSizeMicrons, options.FocalLengthMm):F3}\"/px)"));

            int repeat = int.Parse(o.GetValueOrDefault("repeat", "1"));
            FrameAnalysis analysis = null;
            var times = new List<double>();
            for (int i = 0; i < repeat; i++) {
                analysis = FrameAnalyzer.Analyze(frame, options);
                times.Add(analysis.TotalTime.TotalMilliseconds);
            }

            var st = analysis.Statistics;
            Console.WriteLine($"stats     mean {st.Mean:F1}, median {st.Median:F1}, MAD {st.MedianAbsoluteDeviation:F1}, stdev {st.StDev:F1}, min {st.Min} (x{st.MinOccurrences}), max {st.Max} (x{st.MaxOccurrences})");
            var s = analysis.Stars;
            Console.WriteLine($"options   sensitivity {options.StarSensitivity}, noise {options.NoiseReduction}, debayer {options.DebayerImage && frame.IsBayered}, unlinked {options.UnlinkedStretch}, debayered HFR {options.DebayeredHFR}");
            Console.WriteLine($"stars     resize {s.ResizeFactor:F3}, blobs {s.BlobCount}, stars {s.DetectedStars}, HFR {s.AverageHFR:F3} px (sd {s.HFRStdDev:F3}), FWHM {s.AverageFWHM:F3} px, eccentricity {s.AverageEccentricity:F3}");
            Console.WriteLine($"time      total {analysis.TotalTime.TotalMilliseconds:F0} ms (statistics {analysis.StatisticsTime.TotalMilliseconds:F0}, render {analysis.RenderTime.TotalMilliseconds:F0}, detection {analysis.DetectionTime.TotalMilliseconds:F0})" +
                (repeat > 1 ? $"; {repeat} runs: median {times.OrderBy(t => t).ElementAt(times.Count / 2):F0} ms, min {times.Min():F0} ms" : ""));
            Console.WriteLine($"stages    {string.Join(", ", s.StageTimes.Select(t => $"{t.Stage} {t.Elapsed.TotalMilliseconds:F0}"))} ms");

            if (o.TryGetValue("stars", out var nStars)) {
                foreach (var star in s.StarList.OrderByDescending(x => x.AverageBrightness).Take(int.Parse(nStars))) {
                    Console.WriteLine($"  ({star.Position.X,8:F2}, {star.Position.Y,8:F2})  HFR {star.HFR,6:F3}  FWHM {star.FWHM,6:F3}  ecc {star.Eccentricity:F3}  peak {star.MaxBrightness,6:F0}  bg {star.Background,7:F1}  aperture {star.MeasurementRadius:F1}");
                }
            }

            if (o.TryGetValue("bahtinov", out var b)) {
                var r = Rect(b);
                bool robust = o.ContainsKey("robust");
                var result = FrameAnalyzer.AnalyzeBahtinov(analysis, r, robust: robust);
                Console.WriteLine($"bahtinov  {result.Algorithm}: success {result.Success}, distance {result.Distance:F2} px, signed {result.SignedOffset:F2} px, " +
                    $"spikes {string.Join(" / ", result.Lines.Select(l => l.AngleDegrees.ToString("F1", CultureInfo.InvariantCulture)))} deg");
            }
            return 0;
        }

        /// <summary>2x2 software binning: same-colour average (mosaic kept) or plain average (mono).</summary>
        private static PixelBuffer Bin2(PixelBuffer frame, bool mono) {
            if (mono || !frame.IsBayered) {
                int w = frame.Width / 2, h = frame.Height / 2;
                var data = new ushort[w * h];
                for (int y = 0; y < h; y++) {
                    for (int x = 0; x < w; x++) {
                        int sum = frame[2 * x, 2 * y] + frame[(2 * x) + 1, 2 * y] + frame[2 * x, (2 * y) + 1] + frame[(2 * x) + 1, (2 * y) + 1];
                        data[(y * w) + x] = (ushort)(sum / 4);
                    }
                }
                return new PixelBuffer(data, w, h, frame.BitDepth, BayerPattern.None);
            } else {
                int w = (frame.Width / 4) * 2, h = (frame.Height / 4) * 2;
                var data = new ushort[w * h];
                for (int y = 0; y < h; y++) {
                    for (int x = 0; x < w; x++) {
                        int bx = (x / 2) * 4, by = (y / 2) * 4, qx = x & 1, qy = y & 1;
                        int sum = frame[bx + qx, by + qy] + frame[bx + qx + 2, by + qy] + frame[bx + qx, by + qy + 2] + frame[bx + qx + 2, by + qy + 2];
                        data[(y * w) + x] = (ushort)(sum / 4);
                    }
                }
                return new PixelBuffer(data, w, h, frame.BitDepth, frame.BayerPattern);
            }
        }
    }
}
