#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NINA.Mac.ImageAnalysis.Cli {

    /// <summary>
    /// Minimal read-only FITS reader for the command line tool: primary HDU, BITPIX 8/16/32/-32/-64, 2 or 3 axes,
    /// BZERO/BSCALE. Not a general FITS library (no compression, no extensions).
    /// </summary>
    internal sealed class FitsFile {

        public Dictionary<string, string> Header { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Planes { get; private set; } = 1;
        public int BitPix { get; private set; }

        /// <summary>Samples as stored (first stored row first), one array per plane, scaled to 0..65535.</summary>
        public ushort[][] Data { get; private set; }

        public string GetString(string key) {
            if (!Header.TryGetValue(key, out var v)) {
                return null;
            }
            v = v.Trim();
            if (v.StartsWith("'")) {
                int end = v.IndexOf('\'', 1);
                return end > 0 ? v.Substring(1, end - 1).Trim() : v.Trim('\'').Trim();
            }
            return v;
        }

        public double GetDouble(string key, double fallback = double.NaN) {
            var s = GetString(key);
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        }

        public static FitsFile Read(string path) {
            using var stream = File.OpenRead(path);
            var fits = new FitsFile();
            var block = new byte[2880];
            bool end = false;
            while (!end) {
                if (stream.Read(block, 0, block.Length) != block.Length) {
                    throw new InvalidDataException("Truncated FITS header");
                }
                for (int c = 0; c < 36; c++) {
                    var card = Encoding.ASCII.GetString(block, c * 80, 80);
                    var key = card.Substring(0, 8).Trim();
                    if (key == "END") {
                        end = true;
                        break;
                    }
                    if (card.Length > 9 && card[8] == '=') {
                        var value = card.Substring(10);
                        // strip comment outside quotes
                        int slash = -1;
                        bool inQuote = false;
                        for (int i = 0; i < value.Length; i++) {
                            if (value[i] == '\'') { inQuote = !inQuote; }
                            if (value[i] == '/' && !inQuote) { slash = i; break; }
                        }
                        fits.Header[key] = (slash >= 0 ? value.Substring(0, slash) : value).Trim();
                    }
                }
            }

            fits.BitPix = (int)fits.GetDouble("BITPIX");
            int naxis = (int)fits.GetDouble("NAXIS");
            if (naxis < 2 || naxis > 3) {
                throw new InvalidDataException($"NAXIS = {naxis} not supported");
            }
            fits.Width = (int)fits.GetDouble("NAXIS1");
            fits.Height = (int)fits.GetDouble("NAXIS2");
            fits.Planes = naxis == 3 ? (int)fits.GetDouble("NAXIS3") : 1;
            double bzero = fits.GetDouble("BZERO", 0);
            double bscale = fits.GetDouble("BSCALE", 1);

            int bytesPerSample = Math.Abs(fits.BitPix) / 8;
            int count = fits.Width * fits.Height;
            var raw = new byte[count * bytesPerSample];
            fits.Data = new ushort[fits.Planes][];
            var physical = new double[count];
            for (int p = 0; p < fits.Planes; p++) {
                int read = 0;
                while (read < raw.Length) {
                    int n = stream.Read(raw, read, raw.Length - read);
                    if (n <= 0) {
                        throw new InvalidDataException("Truncated FITS data");
                    }
                    read += n;
                }
                double max = double.MinValue;
                for (int i = 0; i < count; i++) {
                    double v = fits.BitPix switch {
                        8 => raw[i],
                        16 => BinaryPrimitives.ReadInt16BigEndian(raw.AsSpan(i * 2)),
                        32 => BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(i * 4)),
                        -32 => BinaryPrimitives.ReadSingleBigEndian(raw.AsSpan(i * 4)),
                        -64 => BinaryPrimitives.ReadDoubleBigEndian(raw.AsSpan(i * 8)),
                        _ => throw new InvalidDataException($"BITPIX {fits.BitPix} not supported")
                    };
                    v = (v * bscale) + bzero;
                    physical[i] = v;
                    if (v > max) { max = v; }
                }
                // floating point data normalised to [0, 1] (Siril) is scaled to 16 bit
                double scale = (fits.BitPix < 0 && max <= 1.0) ? 65535.0 : 1.0;
                var plane = new ushort[count];
                for (int i = 0; i < count; i++) {
                    plane[i] = (ushort)Math.Max(0, Math.Min(65535, Math.Round(physical[i] * scale)));
                }
                fits.Data[p] = plane;
            }
            return fits;
        }
    }
}
