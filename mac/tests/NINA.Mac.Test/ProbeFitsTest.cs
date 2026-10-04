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
using NINA.Mac.ZwoProbe;
using NUnit.Framework;
using System.IO;
using System.Text;

namespace NINA.Mac.Test {

    [TestFixture]
    public class ProbeFitsTest {
        private string path;

        [SetUp]
        public void SetUp() {
            path = Path.Combine(Path.GetTempPath(), $"probefits_{TestContext.CurrentContext.Test.ID}.fits");
        }

        [TearDown]
        public void TearDown() {
            File.Delete(path);
        }

        [Test]
        public void Write_ProducesBlockAlignedFileWithCardsAndBigEndianOffsetData() {
            var fits = new ProbeFits();
            fits.Add("BAYERPAT", "RGGB", "Sensor Bayer pattern");
            fits.Add("ROWORDER", "TOP-DOWN", "FITS Image Orientation");
            fits.Add("XPIXSZ", 5.8, "[um] Pixel X axis size");

            fits.Write(path, new ushort[] { 0, 32768, 65535, 1 }, 2, 2);

            var bytes = File.ReadAllBytes(path);
            (bytes.Length % 2880).Should().Be(0);
            bytes.Length.Should().Be(2 * 2880);

            var header = Encoding.ASCII.GetString(bytes, 0, 2880);
            header.Should().StartWith("SIMPLE  =                    T");
            header.Substring(80, 30).Should().Be("BITPIX  =                   16");
            header.Should().Contain("NAXIS1  =                    2");
            header.Should().Contain("BZERO   =                32768");
            header.Should().Contain("BAYERPAT= 'RGGB    '");
            header.Should().Contain("ROWORDER= 'TOP-DOWN'");
            header.Should().Contain("XPIXSZ  =                  5.8");
            header.Should().Contain("END" + new string(' ', 77));

            // stored value = physical - BZERO, big-endian int16
            bytes[2880].Should().Be(0x80); bytes[2881].Should().Be(0x00);   // 0     -> -32768
            bytes[2882].Should().Be(0x00); bytes[2883].Should().Be(0x00);   // 32768 -> 0
            bytes[2884].Should().Be(0x7F); bytes[2885].Should().Be(0xFF);   // 65535 -> 32767
            bytes[2886].Should().Be(0x80); bytes[2887].Should().Be(0x01);   // 1     -> -32767
        }
    }
}
