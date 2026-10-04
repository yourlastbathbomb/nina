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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NINA.Mac.ZwoProbe {

    /// <summary>
    /// Minimal 16-bit FITS writer for the camera probe. Uses the same keywords and conventions as
    /// NINA's FITSHeader (BZERO 32768, XPIXSZ = pixel size x bin, ROWORDER TOP-DOWN) so Siril
    /// treats probe frames exactly like NINA frames. Replaced by NINA.Image's writer in M3.
    /// </summary>
    public sealed class ProbeFits {
        private const int BlockSize = 2880;
        private readonly List<string> cards = new();

        public void Add(string key, string value, string comment) {
            var quoted = "'" + value.Replace("'", "''").PadRight(8) + "'";
            AddRaw(key, quoted.PadRight(20), comment);
        }

        public void Add(string key, bool value, string comment) => AddRaw(key, (value ? "T" : "F").PadLeft(20), comment);

        public void Add(string key, int value, string comment) => AddRaw(key, value.ToString(CultureInfo.InvariantCulture).PadLeft(20), comment);

        public void Add(string key, double value, string comment) => AddRaw(key, value.ToString("0.0##########", CultureInfo.InvariantCulture).PadLeft(20), comment);

        public void Add(string key, DateTime utc, string comment) => Add(key, utc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), comment);

        private void AddRaw(string key, string value, string comment) {
            var card = $"{key,-8}= {value} / {comment}";
            cards.Add(card.Length > 80 ? card[..80] : card.PadRight(80));
        }

        public void Write(string path, ushort[] data, int width, int height) {
            if (data.Length < width * height) {
                throw new ArgumentException($"Buffer has {data.Length} pixels, expected {width * height}");
            }
            var header = new ProbeFits();
            header.Add("SIMPLE", true, "C# FITS");
            header.Add("BITPIX", 16, "");
            header.Add("NAXIS", 2, "Dimensionality");
            header.Add("NAXIS1", width, "");
            header.Add("NAXIS2", height, "");
            header.Add("BZERO", 32768, "");
            header.Add("EXTEND", true, "Extensions are permitted");
            header.cards.AddRange(cards);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

            var headerText = new StringBuilder();
            foreach (var card in header.cards) {
                headerText.Append(card);
            }
            headerText.Append("END".PadRight(80));
            var headerBytes = Encoding.ASCII.GetBytes(headerText.ToString());
            fs.Write(headerBytes);
            WritePadding(fs, headerBytes.Length, (byte)' ');

            // Rows in sensor readout order (top row first), matching ROWORDER = TOP-DOWN
            var bytes = new byte[width * height * 2];
            for (int i = 0, n = width * height; i < n; i++) {
                var signed = (short)(data[i] - 32768);
                bytes[2 * i] = (byte)(signed >> 8);
                bytes[(2 * i) + 1] = (byte)signed;
            }
            fs.Write(bytes);
            WritePadding(fs, bytes.Length, 0);
        }

        private static void WritePadding(Stream stream, long written, byte fill) {
            var remainder = (int)(written % BlockSize);
            if (remainder == 0) {
                return;
            }
            var pad = new byte[BlockSize - remainder];
            Array.Fill(pad, fill);
            stream.Write(pad);
        }
    }
}
