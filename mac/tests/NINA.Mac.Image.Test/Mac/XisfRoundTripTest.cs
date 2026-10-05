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
using NINA.Image.FileFormat.XISF;
using System.Security.Cryptography;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// XISF is NINA's other managed format: writer and reader both run without native code. Each case saves a rig frame
    /// with SaveToDisk and loads it with NINA's own XISF.Load, through every compression codec (managed LZ4, zlib and Zstandard
    /// packages), with and without byte shuffling, and with each checksum.
    /// </summary>
    [TestFixture]
    public class XisfRoundTripTest {

        public static IEnumerable<TestCaseData> Codecs() {
            foreach (var compression in Enum.GetValues<XISFCompressionTypeEnum>()) {
                foreach (var shuffle in new[] { false, true }) {
                    if (compression == XISFCompressionTypeEnum.NONE && shuffle) {
                        continue;
                    }
                    yield return new TestCaseData(compression, shuffle);
                }
            }
        }

        [TestCaseSource(nameof(Codecs))]
        public async Task RigLight_RoundTripsThroughNinasXisfReader(XISFCompressionTypeEnum compression, bool shuffle) {
            var folder = TestHost.NewImageFolder($"xisf-{compression}-{shuffle}");
            var pixels = RigFrame.Ramp(RigFrame.Width, RigFrame.Height);
            var image = await RigFrame.Capture((ushort[])pixels.Clone(), RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());
            var saveInfo = RigFrame.SaveInfo(folder, FileTypeEnum.XISF);
            saveInfo.XISFCompressionType = compression;
            saveInfo.XISFByteShuffling = shuffle;
            saveInfo.XISFChecksumType = XISFChecksumTypeEnum.SHA256;

            var path = await image.SaveToDisk(saveInfo);
            var (imageDataFactory, _, _) = RigFrame.Factories();
            var loaded = await XISF.Load(new Uri(path), true, imageDataFactory, CancellationToken.None);

            path.Should().EndWith(".xisf");
            loaded.Properties.Width.Should().Be(RigFrame.Width);
            loaded.Properties.Height.Should().Be(RigFrame.Height);
            loaded.Data.FlatArray.Should().Equal(pixels);

            var metaData = loaded.MetaData;
            // Observation:Time:Start holds the UTC time without a zone designator, and XISFHeader.ExtractMetaData reads it with
            // DateTime.Parse, so the start comes back as the UTC clock time with an unspecified kind (identical on Windows)
            metaData.Image.ExposureStart.Ticks.Should().Be(RigFrame.ExposureStartUtc.Ticks);
            metaData.Image.ExposureStart.Kind.Should().Be(DateTimeKind.Unspecified);
            metaData.Image.ExposureTime.Should().Be(10);
            metaData.Camera.Name.Should().Be("ZWO ASI585MC Pro");
            metaData.Camera.Gain.Should().Be(200);
            metaData.Camera.Offset.Should().Be(3);
            metaData.Camera.BinX.Should().Be(2);
            metaData.Camera.BinY.Should().Be(2);
            metaData.Camera.PixelSize.Should().Be(2.9);
            metaData.Camera.Temperature.Should().Be(-9.8);
            metaData.Camera.SensorType.Should().Be(SensorType.RGGB);
            metaData.Camera.USBLimit.Should().Be(40);
            metaData.Observer.Latitude.Should().Be(22.3);
            metaData.Observer.Longitude.Should().Be(114.18);
            metaData.Telescope.Name.Should().Be("Meade LX200GPS 10in");
        }

        [TestCase(XISFChecksumTypeEnum.SHA1)]
        [TestCase(XISFChecksumTypeEnum.SHA256)]
        [TestCase(XISFChecksumTypeEnum.SHA512)]
        public async Task Checksums_AreWrittenAndVerifiedOnLoad(XISFChecksumTypeEnum checksum) {
            var folder = TestHost.NewImageFolder($"xisf-{checksum}");
            var pixels = RigFrame.Ramp(64, 48);
            var image = await RigFrame.Capture((ushort[])pixels.Clone(), 64, 48, RigFrame.LightFrameMetaData());
            var saveInfo = RigFrame.SaveInfo(folder, FileTypeEnum.XISF);
            saveInfo.XISFChecksumType = checksum;
            saveInfo.XISFCompressionType = XISFCompressionTypeEnum.ZSTD;

            var path = await image.SaveToDisk(saveInfo);
            var (imageDataFactory, _, _) = RigFrame.Factories();
            var loaded = await XISF.Load(new Uri(path), true, imageDataFactory, CancellationToken.None);

            loaded.Data.FlatArray.Should().Equal(pixels);
        }

        [TestCase(XISFChecksumTypeEnum.SHA3_256)]
        [TestCase(XISFChecksumTypeEnum.SHA3_512)]
        public async Task Sha3Checksums_DependOnThePlatformCryptoLibrary(XISFChecksumTypeEnum checksum) {
            // NINA calls SHA3_256.Create()/SHA3_512.Create(); .NET supports SHA-3 only where the OS crypto library provides it
            var supported = checksum == XISFChecksumTypeEnum.SHA3_256 ? SHA3_256.IsSupported : SHA3_512.IsSupported;
            TestContext.Out.WriteLine($"{checksum}: IsSupported = {supported} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            var folder = TestHost.NewImageFolder($"xisf-{checksum}");
            var pixels = RigFrame.Ramp(64, 48);
            var image = await RigFrame.Capture((ushort[])pixels.Clone(), 64, 48, RigFrame.LightFrameMetaData());
            var saveInfo = RigFrame.SaveInfo(folder, FileTypeEnum.XISF);
            saveInfo.XISFChecksumType = checksum;

            if (supported) {
                var path = await image.SaveToDisk(saveInfo);
                var (imageDataFactory, _, _) = RigFrame.Factories();
                var loaded = await XISF.Load(new Uri(path), true, imageDataFactory, CancellationToken.None);
                loaded.Data.FlatArray.Should().Equal(pixels);
            } else {
                await FluentActions.Awaiting(() => image.SaveToDisk(saveInfo)).Should().ThrowAsync<PlatformNotSupportedException>();
                Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Should().BeEmpty("the checksum is computed before the file is opened");
            }
        }
    }
}
