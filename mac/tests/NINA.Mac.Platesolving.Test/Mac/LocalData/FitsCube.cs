#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NINA.Mac.Platesolving.Test.LocalData {

    /// <summary>
    /// A read-only reader for the primary HDU of a FITS file (FITS 4.0, written from the standard, no NINA code): the header
    /// cards and the image as float planes, for BITPIX 16 (with BZERO/BSCALE) and -32, NAXIS 2 or 3 (colour stacks are 3 planes).
    /// NINA's own FITS reader needs CFITSIO, which is not available on macOS yet (README-engine.md), and the files read here are
    /// the user's own stacks, opened read-only and never copied.
    /// </summary>
    internal sealed class FitsCube {

        private const int BlockSize = 2880;
        private const int CardSize = 80;

        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public string Path { get; }
        public int Width { get; }
        public int Height { get; }
        public float[][] Planes { get; }

        private FitsCube(string path, int width, int height, float[][] planes) {
            Path = path;
            Width = width;
            Height = height;
            Planes = planes;
        }

        public bool Has(string keyword) => values.ContainsKey(keyword);

        public string? String(string keyword) {
            if (!values.TryGetValue(keyword, out var raw)) {
                return null;
            }
            return raw.StartsWith('\'') ? raw.Trim('\'').TrimEnd() : raw;
        }

        public double Double(string keyword) => double.Parse(values[keyword], NumberStyles.Float, CultureInfo.InvariantCulture);

        public double? OptionalDouble(string keyword) =>
            values.TryGetValue(keyword, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        public static FitsCube Read(string path) {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cards = new List<string>();
            var block = new byte[BlockSize];
            var ended = false;
            while (!ended) {
                stream.ReadExactly(block);
                for (var i = 0; i < BlockSize; i += CardSize) {
                    var card = Encoding.ASCII.GetString(block, i, CardSize);
                    if (card.StartsWith("END     ", StringComparison.Ordinal) || card.TrimEnd() == "END") {
                        ended = true;
                        break;
                    }
                    cards.Add(card);
                }
            }

            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var card in cards) {
                if (card.Length < 10 || card[8] != '=' || card[9] != ' ') {
                    continue;
                }
                var key = card[..8].TrimEnd();
                var rest = card[10..];
                string value;
                if (rest.TrimStart().StartsWith('\'')) {
                    var start = rest.IndexOf('\'');
                    var end = start + 1;
                    while (end < rest.Length) {
                        if (rest[end] == '\'') {
                            if (end + 1 < rest.Length && rest[end + 1] == '\'') {
                                end += 2;
                                continue;
                            }
                            break;
                        }
                        end++;
                    }
                    value = rest[start..Math.Min(end + 1, rest.Length)];
                } else {
                    var slash = rest.IndexOf('/');
                    value = (slash >= 0 ? rest[..slash] : rest).Trim();
                }
                parsed.TryAdd(key, value);
            }

            int Int(string key) => int.Parse(parsed[key], CultureInfo.InvariantCulture);
            var bitpix = Int("BITPIX");
            var naxis = Int("NAXIS");
            if (naxis != 2 && naxis != 3) {
                throw new InvalidDataException($"{path}: NAXIS {naxis} is not an image");
            }
            var width = Int("NAXIS1");
            var height = Int("NAXIS2");
            var planesCount = naxis == 3 ? Int("NAXIS3") : 1;
            var bzero = parsed.TryGetValue("BZERO", out var z) ? double.Parse(z, CultureInfo.InvariantCulture) : 0d;
            var bscale = parsed.TryGetValue("BSCALE", out var s) ? double.Parse(s, CultureInfo.InvariantCulture) : 1d;
            var bytesPerValue = bitpix switch {
                16 => 2,
                -32 => 4,
                _ => throw new InvalidDataException($"{path}: BITPIX {bitpix} is not read here")
            };

            var planes = new float[planesCount][];
            var row = new byte[width * bytesPerValue];
            for (var p = 0; p < planesCount; p++) {
                var plane = planes[p] = new float[width * height];
                for (var y = 0; y < height; y++) {
                    stream.ReadExactly(row);
                    for (var x = 0; x < width; x++) {
                        var raw = bitpix == 16
                            ? BinaryPrimitives.ReadInt16BigEndian(row.AsSpan(x * 2))
                            : (double)BinaryPrimitives.ReadSingleBigEndian(row.AsSpan(x * 4));
                        plane[y * width + x] = (float)(bzero + bscale * raw);
                    }
                }
            }

            var cube = new FitsCube(path, width, height, planes);
            foreach (var pair in parsed) {
                cube.values[pair.Key] = pair.Value;
            }
            return cube;
        }

        /// <summary>The mean of the planes (a luminance for an RGB stack), as NINA's 16-bit pixels; float data in 0..1 is scaled to 0..65535.</summary>
        public float[] Mono() {
            if (Planes.Length == 1) {
                return Planes[0];
            }
            var mono = new float[Width * Height];
            for (var i = 0; i < mono.Length; i++) {
                var sum = 0f;
                foreach (var plane in Planes) {
                    sum += plane[i];
                }
                mono[i] = sum / Planes.Length;
            }
            return mono;
        }

        public static ushort[] ToUShort(float[] values, double scale = 1) {
            var result = new ushort[values.Length];
            for (var i = 0; i < values.Length; i++) {
                result[i] = (ushort)Math.Clamp(Math.Round(values[i] * scale), 0, 65535);
            }
            return result;
        }
    }
}
