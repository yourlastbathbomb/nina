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

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// A strict, independent reader for the primary HDU of a 16-bit FITS image, written from the FITS 4.0 standard
    /// (sections 3.3, 4.1, 4.2, 5.1) and sharing no code with NINA: 2880-byte blocks, 80-character ASCII cards, END,
    /// big-endian two's-complement data scaled by BZERO/BSCALE. It checks what the standard requires of a file it
    /// reads and throws <see cref="InvalidDataException"/> otherwise.
    /// </summary>
    internal sealed class FitsFile {
        public const int BlockSize = 2880;
        public const int CardSize = 80;

        private FitsFile(List<string> cards, ushort[] pixels, int width, int height, long fileLength, int dataStart) {
            Cards = cards;
            Pixels = pixels;
            Width = width;
            Height = height;
            FileLength = fileLength;
            DataStart = dataStart;
        }

        /// <summary>The header cards in file order, up to but excluding END, each exactly 80 characters.</summary>
        public IReadOnlyList<string> Cards { get; }

        /// <summary>Pixel values (BZERO applied) in file order: row by row, as stored.</summary>
        public ushort[] Pixels { get; }

        public int Width { get; }

        public int Height { get; }

        public long FileLength { get; }

        public int DataStart { get; }

        /// <summary>The card for <paramref name="keyword"/>, or null.</summary>
        public string? Card(string keyword) {
            return Cards.FirstOrDefault(c => c.Substring(0, 8).TrimEnd() == keyword);
        }

        /// <summary>The value field of a card (columns 11-80 before any comment), trimmed; strings without quotes.</summary>
        public string? Value(string keyword) {
            var card = Card(keyword);
            if (card == null) {
                return null;
            }
            var field = card.Substring(10);
            if (field.TrimStart().StartsWith('\'')) {
                var start = field.IndexOf('\'');
                var text = new StringBuilder();
                for (var i = start + 1; i < field.Length; i++) {
                    if (field[i] == '\'') {
                        if (i + 1 < field.Length && field[i + 1] == '\'') {
                            text.Append('\'');
                            i++;
                            continue;
                        }
                        break;
                    }
                    text.Append(field[i]);
                }
                return text.ToString().TrimEnd();
            }
            var slash = field.IndexOf('/');
            return (slash >= 0 ? field.Substring(0, slash) : field).Trim();
        }

        public double Double(string keyword) {
            return double.Parse(Value(keyword) ?? throw new KeyNotFoundException(keyword), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public long Integer(string keyword) {
            return long.Parse(Value(keyword) ?? throw new KeyNotFoundException(keyword), NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        public static FitsFile Read(string path) {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length % BlockSize != 0) {
                throw new InvalidDataException($"File length {bytes.Length} is not a positive multiple of {BlockSize} bytes");
            }

            var cards = new List<string>();
            var offset = 0;
            var ended = false;
            while (!ended) {
                if (offset + CardSize > bytes.Length) {
                    throw new InvalidDataException("No END card");
                }
                for (var i = offset; i < offset + CardSize; i++) {
                    if (bytes[i] < 0x20 || bytes[i] > 0x7E) {
                        throw new InvalidDataException($"Header byte 0x{bytes[i]:X2} at {i} is not printable ASCII");
                    }
                }
                var card = Encoding.ASCII.GetString(bytes, offset, CardSize);
                offset += CardSize;
                if (card.Substring(0, 8) == "END     ") {
                    if (card.Substring(8).Trim().Length != 0) {
                        throw new InvalidDataException("END card is not blank after the keyword");
                    }
                    ended = true;
                } else {
                    cards.Add(card);
                }
            }
            // The rest of the last header block must be blank cards
            var dataStart = (offset + BlockSize - 1) / BlockSize * BlockSize;
            for (var i = offset; i < dataStart; i++) {
                if (bytes[i] != (byte)' ') {
                    throw new InvalidDataException($"Header padding byte at {i} is not a space");
                }
            }

            var file = new FitsFile(cards, Array.Empty<ushort>(), 0, 0, bytes.Length, dataStart);
            RequireCard(file, 0, "SIMPLE", "T");
            if (file.Integer("BITPIX") != 16) {
                throw new InvalidDataException("Only BITPIX 16 is read here");
            }
            if (file.Integer("NAXIS") != 2) {
                throw new InvalidDataException("Only two-dimensional images are read here");
            }
            var width = checked((int)file.Integer("NAXIS1"));
            var height = checked((int)file.Integer("NAXIS2"));
            var bzero = file.Value("BZERO") != null ? file.Double("BZERO") : 0.0;
            var bscale = file.Value("BSCALE") != null ? file.Double("BSCALE") : 1.0;

            var dataLength = checked(width * height * 2);
            if (dataStart + dataLength > bytes.Length) {
                throw new InvalidDataException("Data array is truncated");
            }
            var pixels = new ushort[width * height];
            for (var i = 0; i < pixels.Length; i++) {
                var stored = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(dataStart + 2 * i, 2));
                var physical = bzero + bscale * stored;
                if (physical < 0 || physical > ushort.MaxValue || physical != Math.Floor(physical)) {
                    throw new InvalidDataException($"Pixel {i} scales to {physical}, which is not an unsigned 16-bit value");
                }
                pixels[i] = (ushort)physical;
            }
            // The data unit is padded to a whole block with zero bytes
            for (var i = dataStart + dataLength; i < bytes.Length; i++) {
                if (bytes[i] != 0) {
                    throw new InvalidDataException($"Data padding byte at {i} is not zero");
                }
            }
            if ((dataStart + dataLength + BlockSize - 1) / BlockSize * BlockSize != bytes.Length) {
                throw new InvalidDataException("Extra blocks after the primary HDU (not expected from NINA's writer)");
            }
            return new FitsFile(cards, pixels, width, height, bytes.Length, dataStart);
        }

        private static void RequireCard(FitsFile file, int index, string keyword, string value) {
            if (file.Cards.Count <= index || file.Cards[index].Substring(0, 8).TrimEnd() != keyword || file.Value(keyword) != value) {
                throw new InvalidDataException($"Card {index + 1} must be {keyword} = {value}");
            }
        }
    }
}
