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
using NINA.Core.Enum;
using System.Globalization;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// NINA's managed FITS writer (the default, FITSUseLegacyWriter) on macOS, through the capture path a frame takes:
    /// ImageArrayExposureData -> ToImageData -> SaveToDisk with NINA's default file pattern. The files are read back by
    /// <see cref="FitsFile"/>, a reader written from the FITS standard that shares no code with NINA, and by CFITSIO's
    /// fitsverify/listhead and Siril when they are installed.
    /// </summary>
    [TestFixture]
    public class FitsWriterTest {

        [Test]
        public async Task DefaultPattern_SavesIntoDateAndImageTypeFolders() {
            var folder = TestHost.NewImageFolder("pattern");
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(), RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());

            var path = await image.SaveToDisk(RigFrame.SaveInfo(folder));

            // $$DATEMINUS12$$\$$IMAGETYPE$$\$$DATETIME$$_$$FILTER$$_$$SENSORTEMP$$_$$EXPOSURETIME$$s_$$FRAMENR$$, local time,
            // empty filter, temperature and exposure with two decimals: the backslashes are folders on macOS as on Windows
            var local = RigFrame.ExposureStartUtc.ToLocalTime();
            var expected = Path.Combine(folder,
                local.AddHours(-12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "LIGHT",
                local.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + "__-9.80_10.00s_0001.fits");
            path.Should().Be(expected);
            File.Exists(expected).Should().BeTrue();
        }

        [Test]
        public async Task RigLight_EveryPixelRoundTrips() {
            var folder = TestHost.NewImageFolder("rig");
            var pixels = RigFrame.RggbMosaic();
            var image = await RigFrame.Capture((ushort[])pixels.Clone(), RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());

            var file = FitsFile.Read(await image.SaveToDisk(RigFrame.SaveInfo(folder)));

            file.Width.Should().Be(RigFrame.Width);
            file.Height.Should().Be(RigFrame.Height);
            file.Integer("BITPIX").Should().Be(16);
            file.Integer("BZERO").Should().Be(32768);
            file.Pixels.Should().Equal(pixels, "rows are stored top-down in array order (ROWORDER TOP-DOWN)");
            // 1920 x 1080 x 2 bytes after a two-block header, padded to whole 2880-byte blocks
            file.DataStart.Should().Be(2 * FitsFile.BlockSize);
            file.FileLength.Should().Be(2 * FitsFile.BlockSize + (long)Math.Ceiling(RigFrame.Width * RigFrame.Height * 2 / (double)FitsFile.BlockSize) * FitsFile.BlockSize);
        }

        [TestCase(1, 1)]
        [TestCase(2, 1)]
        [TestCase(37, 23)]
        [TestCase(1440, 1)]
        [TestCase(3840, 2160)]
        public async Task Ramp_FullUnsignedRange_RoundTrips(int width, int height) {
            var folder = TestHost.NewImageFolder("ramp");
            var pixels = width * height >= 4 ? RigFrame.Ramp(width, height) : Enumerable.Range(0, width * height).Select(i => (ushort)(i == 0 ? 65535 : 0)).ToArray();
            var image = await RigFrame.Capture((ushort[])pixels.Clone(), width, height, RigFrame.LightFrameMetaData(), isBayered: false);

            var file = FitsFile.Read(await image.SaveToDisk(RigFrame.SaveInfo(folder)));

            file.Width.Should().Be(width);
            file.Height.Should().Be(height);
            file.Pixels.Should().Equal(pixels);
        }

        [Test]
        public async Task Fitsverify_FindsNoErrorsOrWarnings() {
            ExternalTools.Require(ExternalTools.Fitsverify);
            var folder = TestHost.NewImageFolder("fitsverify");
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(), RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());
            var path = await image.SaveToDisk(RigFrame.SaveInfo(folder));

            var (exitCode, output) = ExternalTools.Run(ExternalTools.Fitsverify, folder, TimeSpan.FromMinutes(1), path);

            TestContext.Out.WriteLine(output);
            exitCode.Should().Be(0, output);
            output.Should().Contain("**** Verification found 0 warning(s) and 0 error(s). ****");
        }

        [Test]
        public async Task CfitsioListhead_ReadsTheSameCards() {
            ExternalTools.Require(ExternalTools.Listhead);
            var folder = TestHost.NewImageFolder("listhead");
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(), RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());
            var path = await image.SaveToDisk(RigFrame.SaveInfo(folder));

            var (exitCode, output) = ExternalTools.Run(ExternalTools.Listhead, folder, TimeSpan.FromMinutes(1), path);

            exitCode.Should().Be(0, output);
            // listhead prints a header line, then each card (trailing blanks trimmed) up to END
            var cfitsioCards = output.Replace("\r", string.Empty).Split('\n')
                .SkipWhile(l => !l.StartsWith("SIMPLE", StringComparison.Ordinal))
                .TakeWhile(l => l.TrimEnd() != "END")
                .Select(l => l.TrimEnd())
                .ToList();
            cfitsioCards.Should().Equal(FitsFile.Read(path).Cards.Select(c => c.TrimEnd()));
        }

        [Test]
        public async Task Siril_DebayersTheRigFrame_FromItsHeader() {
            ExternalTools.Require(ExternalTools.SirilCli);
            var folder = TestHost.NewImageFolder("siril");
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(), RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());
            var path = await image.SaveToDisk(RigFrame.SaveInfo(folder));

            var (red, green, blue, log) = ExternalTools.SirilDebayerMeans(path, folder);

            TestContext.Out.WriteLine(log);
            // Siril takes the CFA pattern (BAYERPAT) and the row order (ROWORDER) from the header; a wrong pattern or a
            // bottom-up reading would swap the channels
            log.Should().Contain("RGGB from header").And.Contain("top-down from header");
            red.Should().Be(RigFrame.Red);
            green.Should().Be(RigFrame.Green);
            blue.Should().Be(RigFrame.Blue);
        }
    }
}
