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
using NINA.Core.Utility;
using NINA.Image.FileFormat.XISF;
using NINA.Image.Mac;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// SWCREATE on macOS: the header writers read the upstream hook CoreUtil.ImageFileCreator, whose default is upstream's own
    /// value (FitsHeaderTest and the linked upstream FITSTest/XISFTest pin it), and the fork's host points it at
    /// <see cref="MacImageFileIdentity"/>, which names Nightglass, the N.I.N.A. version it is based on and the real architecture.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class SoftwareCreatorTest {

        private Func<string> saved = null!;

        [SetUp]
        public void Remember() => saved = CoreUtil.ImageFileCreator;

        [TearDown]
        public void Restore() => CoreUtil.ImageFileCreator = saved;

        [Test]
        public void Default_IsUpstreamsValue_SoWindowsWritesWhatItAlwaysWrote() {
            CoreUtil.ImageFileCreator().Should().Be(string.Format("N.I.N.A. {0} ({1})", CoreUtil.Version, DllLoader.IsX86() ? "x86" : "x64"));
        }

        [Test]
        public void Describe_NamesNightglass_TheUpstreamVersion_AndTheProcessArchitecture() {
            MacImageFileIdentity.Describe("0.1.0", "3.3.0.1064", Architecture.Arm64).Should().Be("Nightglass 0.1.0 (based on N.I.N.A. 3.3.0.1064) (arm64)");
            MacImageFileIdentity.Describe("0.1.0").Should().EndWith($"(based on N.I.N.A. {CoreUtil.Version}) ({RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})");
            RuntimeInformation.ProcessArchitecture.Should().Be(Architecture.Arm64, "this Mac is Apple silicon");
            MacImageFileIdentity.Describe(null, "3.3.0.1064", Architecture.Arm64).Should().Be("Nightglass dev (based on N.I.N.A. 3.3.0.1064) (arm64)");
            MacImageFileIdentity.ProductName.Should().NotContainEquivalentOf("NINA");

            var longVersion = MacImageFileIdentity.Describe("0.1.0-beta.12.long.prerelease+abcdef0123456789", "3.3.0.1064", Architecture.Arm64);
            longVersion.Should().Be("Nightglass 0.1.0 (based on N.I.N.A. 3.3.0.1064) (arm64)", "the suffix goes first when the value would not fit");
            MacImageFileIdentity.Describe(new string('9', 80), "3.3.0.1064", Architecture.Arm64).Length.Should().BeLessThanOrEqualTo(MacImageFileIdentity.MaxLength);
            MacImageFileIdentity.Describe("1.0 'quoted'", "3.3.0.1064", Architecture.Arm64).Should().NotContain("'", "a FITS string cannot hold a bare quote");
        }

        [Test]
        public async Task Applied_FitsAndXisfFiles_SayNightglass() {
            MacImageFileIdentity.Apply("0.1.0");
            var expected = MacImageFileIdentity.Describe("0.1.0");

            var image = await RigFrame.Capture(RigFrame.RggbMosaic(), RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());
            var fits = FitsFile.Read(await image.SaveToDisk(RigFrame.SaveInfo(TestHost.NewImageFolder("swcreate-fits"))));
            fits.Value("SWCREATE").Should().Be(expected);
            fits.Card("SWCREATE").Should().StartWith($"SWCREATE= '{expected}'");
            fits.Cards.Should().ContainSingle(c => c.StartsWith("SWCREATE", StringComparison.Ordinal));

            var xisfPath = await image.SaveToDisk(RigFrame.SaveInfo(TestHost.NewImageFolder("swcreate-xisf"), FileTypeEnum.XISF));
            var bytes = await File.ReadAllBytesAsync(xisfPath);
            var headerLength = BitConverter.ToInt32(bytes, 8);
            var header = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes, 16, headerLength));
            header.Descendants().Where(e => e.Name.LocalName == "FITSKeyword" && (string?)e.Attribute("name") == "SWCREATE")
                .Select(e => (string?)e.Attribute("value")).Should().Equal($"'{expected}'");
        }
    }
}
