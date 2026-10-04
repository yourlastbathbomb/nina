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
using System.Text.RegularExpressions;

namespace NINA.Mac.Siril {

    /// <summary>One 80-character header card of a FITS primary HDU.</summary>
    public sealed class FitsCard {

        public FitsCard(string key, string rawValue, bool isString, string value, string comment, string image) {
            Key = key;
            RawValue = rawValue;
            IsString = isString;
            Value = value;
            Comment = comment;
            Image = image;
        }

        /// <summary>Keyword, upper case, without padding.</summary>
        public string Key { get; }

        /// <summary>Value field as written (strings still quoted), trimmed. Null for commentary cards.</summary>
        public string RawValue { get; }

        public bool IsString { get; }

        /// <summary>Value with string quotes removed and trailing blanks trimmed (FITS 4.0 section 4.2.1).</summary>
        public string Value { get; }

        public string Comment { get; }

        /// <summary>The full 80-character card image.</summary>
        public string Image { get; }
    }

    /// <summary>Header of a FITS image HDU. Keyword lookup returns the first card with that keyword, like cfitsio.</summary>
    public sealed class FitsHeader {
        private readonly List<FitsCard> cards;
        private readonly Dictionary<string, FitsCard> firstByKey = new(StringComparer.Ordinal);

        public FitsHeader(IEnumerable<FitsCard> cards, bool isTileCompressed = false) {
            this.cards = new List<FitsCard>(cards);
            IsTileCompressed = isTileCompressed;
            foreach (var card in this.cards) {
                if (!string.IsNullOrEmpty(card.Key) && card.RawValue != null) {
                    firstByKey.TryAdd(card.Key, card);
                }
            }
        }

        public IReadOnlyList<FitsCard> Cards => cards;

        /// <summary>
        /// The image header of a tile-compressed HDU (ZIMAGE = T): BITPIX/NAXIS/NAXISn are the image's (from
        /// ZBITPIX/ZNAXIS/ZNAXISn), the other keywords as written. <see cref="FitsFile"/> cannot read its pixels.
        /// </summary>
        public bool IsTileCompressed { get; }

        /// <summary>
        /// Value for exact comparisons: numbers in round-trip form (so 20 and 20.0 compare equal, 20.0 and 20.5 do not),
        /// strings as read; null when the keyword is missing.
        /// </summary>
        public string GetComparableValue(string key) {
            if (!firstByKey.TryGetValue(key, out var card)) {
                return null;
            }
            if (!card.IsString && TryParseNumber(card.Value, out var number)) {
                return number.ToString("R", CultureInfo.InvariantCulture);
            }
            return card.Value;
        }

        public bool Contains(string key) => firstByKey.ContainsKey(key);

        public bool TryGetCard(string key, out FitsCard card) => firstByKey.TryGetValue(key, out card);

        public string GetString(string key) => firstByKey.TryGetValue(key, out var card) ? card.Value : null;

        public bool TryGetDouble(string key, out double value) {
            value = double.NaN;
            if (!firstByKey.TryGetValue(key, out var card) || card.IsString) {
                return false;
            }
            return TryParseNumber(card.Value, out value);
        }

        public double? GetDouble(string key) => TryGetDouble(key, out var value) ? value : null;

        public int? GetInt(string key) {
            if (!TryGetDouble(key, out var value) || value != Math.Floor(value) || value < int.MinValue || value > int.MaxValue) {
                return null;
            }
            return (int)value;
        }

        internal static bool TryParseNumber(string text, out double value) {
            // FITS allows a 'D' exponent for double precision values
            var normalized = text.Trim().Replace('D', 'E').Replace('d', 'e');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>Pixel data of a FITS primary HDU, converted to physical values (BZERO/BSCALE applied).</summary>
    public sealed class FitsImage {

        public FitsImage(FitsHeader header, int width, int height, float[][] planes) {
            Header = header;
            Width = width;
            Height = height;
            Planes = planes;
        }

        public FitsHeader Header { get; }

        public int Width { get; }

        public int Height { get; }

        public int PlaneCount => Planes.Length;

        /// <summary>One array per NAXIS3 plane, rows in file order (FITS row 0 first), Width * Height values each.</summary>
        public float[][] Planes { get; }

        /// <summary>Value at column <paramref name="x"/> of stored row <paramref name="row"/> (no ROWORDER handling).</summary>
        public float this[int plane, int x, int row] => Planes[plane][(row * Width) + x];

        /// <summary>
        /// True when stored row 0 is the top of the picture: ROWORDER = 'TOP-DOWN' (what NINA writes). Siril's
        /// mirrorx -bottomup output says 'BOTTOM-UP'. Without the keyword the FITS default (bottom-up) applies.
        /// </summary>
        public bool IsTopDown => string.Equals(Header.GetString("ROWORDER"), "TOP-DOWN", StringComparison.OrdinalIgnoreCase);

        /// <summary>Value at picture coordinates (x, y) with y = 0 the top row, whatever the stored ROWORDER.</summary>
        public float AtTopDown(int plane, int x, int y) => this[plane, x, IsTopDown ? y : Height - 1 - y];
    }

    /// <summary>
    /// Minimal FITS reader for the first image HDU (BITPIX 8/16/32/-32/-64, NAXIS 2 or 3): the primary HDU, or, when
    /// the primary is empty (NAXIS = 0), the first extension if it is an IMAGE or a tile-compressed image. cfitsio
    /// writes compressed images that way (NINA's CFitsioFITS writer with a FITSCompressionType, saved as .fits unless
    /// FITSAddFzExtension is set, NINA.Image/ImageData/BaseImageData.cs:503-523; Siril's setcompress), and Siril
    /// reads the first image HDU. Headers of compressed images are read; their pixels are not.
    /// Enough to validate NINA frames and Siril outputs; not a general FITS library.
    /// </summary>
    public static class FitsFile {
        public const int BlockSize = 2880;
        public const int CardSize = 80;

        /// <summary>BINTABLE structure keywords of a compressed HDU that are not part of the image header (FITS 4.0 section 10.1).</summary>
        private static readonly Regex TableStructureKey =
            new(@"^(XTENSION|BITPIX|NAXIS\d*|PCOUNT|GCOUNT|TFIELDS|THEAP|T(TYPE|FORM|UNIT|DIM|SCAL|ZERO|NULL|DISP)\d+)$", RegexOptions.CultureInvariant);

        public static FitsHeader ReadHeader(string path) {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadHeader(stream, path);
        }

        public static FitsImage ReadImage(string path) {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var header = ReadHeader(stream, path);
            if (header.IsTileCompressed) {
                throw new InvalidDataException($"{path}: tile-compressed image ({header.GetString("ZCMPTYPE")}); only its header can be read here");
            }
            var bitpix = header.GetInt("BITPIX") ?? throw new InvalidDataException($"{path}: BITPIX missing");
            var naxis = header.GetInt("NAXIS") ?? 0;
            if (naxis < 2 || naxis > 3) {
                throw new InvalidDataException($"{path}: NAXIS = {naxis}, only 2-D and 3-D images are supported");
            }
            var width = header.GetInt("NAXIS1") ?? throw new InvalidDataException($"{path}: NAXIS1 missing");
            var height = header.GetInt("NAXIS2") ?? throw new InvalidDataException($"{path}: NAXIS2 missing");
            var planeCount = naxis == 3 ? header.GetInt("NAXIS3") ?? 1 : 1;
            var bzero = header.GetDouble("BZERO") ?? 0.0;
            var bscale = header.GetDouble("BSCALE") ?? 1.0;
            var bytesPerValue = Math.Abs(bitpix) / 8;
            if (bitpix != 8 && bitpix != 16 && bitpix != 32 && bitpix != -32 && bitpix != -64) {
                throw new InvalidDataException($"{path}: BITPIX = {bitpix} is not supported");
            }

            var count = width * height;
            var buffer = new byte[count * bytesPerValue];
            var planes = new float[planeCount][];
            for (var p = 0; p < planeCount; p++) {
                stream.ReadExactly(buffer);
                var plane = new float[count];
                for (var i = 0; i < count; i++) {
                    double raw = bitpix switch {
                        8 => buffer[i],
                        16 => BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(i * 2, 2)),
                        32 => BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(i * 4, 4)),
                        -32 => BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(i * 4, 4)),
                        _ => BinaryPrimitives.ReadDoubleBigEndian(buffer.AsSpan(i * 8, 8)),
                    };
                    plane[i] = (float)((raw * bscale) + bzero);
                }
                planes[p] = plane;
            }
            return new FitsImage(header, width, height, planes);
        }

        /// <summary>Header of the first image HDU; leaves the stream at that HDU's data.</summary>
        private static FitsHeader ReadHeader(Stream stream, string path) {
            var primary = new FitsHeader(ReadCards(stream, path, "SIMPLE  ="));
            if ((primary.GetInt("NAXIS") ?? 0) != 0 || stream.Position >= stream.Length) {
                return primary;
            }
            // Empty primary HDU (no data follows it): the image is in the first extension, if anywhere
            List<FitsCard> cards;
            try {
                cards = ReadCards(stream, path, "XTENSION=");
            } catch (InvalidDataException) {
                return primary;
            }
            var extension = new FitsHeader(cards);
            var type = extension.GetString("XTENSION");
            if (string.Equals(type, "IMAGE", StringComparison.Ordinal)) {
                return extension;
            }
            if (string.Equals(type, "BINTABLE", StringComparison.Ordinal) && string.Equals(extension.GetString("ZIMAGE"), "T", StringComparison.Ordinal)) {
                return CompressedImageHeader(extension);
            }
            return primary;
        }

        /// <summary>Image header of a tile-compressed HDU: ZBITPIX/ZNAXIS/ZNAXISn become BITPIX/NAXIS/NAXISn, table structure keywords are dropped.</summary>
        private static FitsHeader CompressedImageHeader(FitsHeader table) {
            var cards = new List<FitsCard>();
            void Map(string from, string to) {
                if (table.TryGetCard(from, out var c)) {
                    cards.Add(new FitsCard(to, c.RawValue, c.IsString, c.Value, c.Comment, to.PadRight(8) + c.Image.Substring(8)));
                }
            }
            Map("ZBITPIX", "BITPIX");
            Map("ZNAXIS", "NAXIS");
            for (var n = 1; n <= (table.GetInt("ZNAXIS") ?? 0); n++) {
                Map($"ZNAXIS{n.ToString(CultureInfo.InvariantCulture)}", $"NAXIS{n.ToString(CultureInfo.InvariantCulture)}");
            }
            foreach (var card in table.Cards) {
                if (!TableStructureKey.IsMatch(card.Key)) {
                    cards.Add(card);
                }
            }
            return new FitsHeader(cards, isTileCompressed: true);
        }

        private static List<FitsCard> ReadCards(Stream stream, string path, string firstCard) {
            var cards = new List<FitsCard>();
            var block = new byte[BlockSize];
            var first = true;
            while (true) {
                try {
                    stream.ReadExactly(block);
                } catch (EndOfStreamException) {
                    throw new InvalidDataException($"{path}: end of file before the END card");
                }
                var text = Encoding.ASCII.GetString(block);
                if (first && !text.StartsWith(firstCard, StringComparison.Ordinal)) {
                    throw new InvalidDataException(firstCard.StartsWith("SIMPLE", StringComparison.Ordinal)
                        ? $"{path}: not a FITS file (no SIMPLE card)"
                        : $"{path}: no {firstCard.TrimEnd('=', ' ')} card where an extension should start");
                }
                first = false;
                for (var offset = 0; offset < BlockSize; offset += CardSize) {
                    var image = text.Substring(offset, CardSize);
                    var key = image.Substring(0, 8).TrimEnd();
                    if (key == "END") {
                        return cards;
                    }
                    cards.Add(ParseCard(key, image));
                }
            }
        }

        internal static FitsCard ParseCard(string key, string image) {
            // Value indicator "= " in columns 9-10 (FITS 4.0 section 4.1.2.2); otherwise commentary
            if (image.Length < 10 || image[8] != '=' || image[9] != ' ') {
                return new FitsCard(key, null, false, null, image.Length > 8 ? image.Substring(8).TrimEnd() : string.Empty, image);
            }
            var field = image.Substring(10);
            var trimmed = field.TrimStart();
            if (trimmed.StartsWith('\'')) {
                // String: '' inside the quotes is an escaped quote; trailing blanks are not significant
                var sb = new StringBuilder();
                var i = 1;
                var closed = false;
                while (i < trimmed.Length) {
                    var c = trimmed[i];
                    if (c == '\'') {
                        if (i + 1 < trimmed.Length && trimmed[i + 1] == '\'') {
                            sb.Append('\'');
                            i += 2;
                            continue;
                        }
                        closed = true;
                        i++;
                        break;
                    }
                    sb.Append(c);
                    i++;
                }
                var raw = closed ? trimmed.Substring(0, i) : trimmed;
                var rest = closed ? trimmed.Substring(i) : string.Empty;
                var slash = rest.IndexOf('/');
                var comment = slash >= 0 ? rest.Substring(slash + 1).Trim() : string.Empty;
                return new FitsCard(key, raw.Trim(), true, sb.ToString().TrimEnd(), comment, image);
            }
            var commentStart = trimmed.IndexOf('/');
            var value = (commentStart >= 0 ? trimmed.Substring(0, commentStart) : trimmed).Trim();
            var valueComment = commentStart >= 0 ? trimmed.Substring(commentStart + 1).Trim() : string.Empty;
            return new FitsCard(key, value, false, value, valueComment, image);
        }
    }
}
