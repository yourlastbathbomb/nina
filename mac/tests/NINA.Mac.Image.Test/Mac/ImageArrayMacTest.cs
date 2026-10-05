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
using NINA.Image.ImageData;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// The exposure-data containers that carry a frame from the camera driver into NINA.Image: ImageArrayExposureData (ZWO and
    /// the other native SDKs) and Flipped2DExposureData (ASCOM-style [x, y] arrays, transposed by unsafe pointer code).
    /// </summary>
    [TestFixture]
    public class ImageArrayMacTest {

        [Test]
        public async Task ImageArrayExposureData_HandsTheDriverBufferToTheImageUnchanged() {
            var pixels = RigFrame.Ramp(RigFrame.Width, RigFrame.Height);
            var metaData = RigFrame.LightFrameMetaData();
            var (_, exposureDataFactory, _) = RigFrame.Factories();

            var exposure = exposureDataFactory.CreateImageArrayExposureData(pixels, RigFrame.Width, RigFrame.Height, 16, true, metaData);
            var image = await exposure.ToImageData();

            exposure.Width.Should().Be(RigFrame.Width);
            exposure.Height.Should().Be(RigFrame.Height);
            exposure.BitDepth.Should().Be(16);
            exposure.IsBayered.Should().BeTrue();
            image.Data.FlatArray.Should().BeSameAs(pixels, "no copy is made on the way from the SDK buffer to the image");
            image.Data.FlatArrayInt.Should().BeNull();
            image.Data.RAWData.Should().BeNull();
            image.Data.RAWType.Should().BeNull();
            image.MetaData.Should().BeSameAs(metaData);
            image.Properties.Width.Should().Be(RigFrame.Width);
            image.Properties.Height.Should().Be(RigFrame.Height);
            image.Properties.BitDepth.Should().Be(16);
            image.Properties.IsBayered.Should().BeTrue();
            image.Properties.Gain.Should().Be(200, "ImageProperties takes gain and offset from the camera metadata");
            image.Properties.Offset.Should().Be(3);
            image.StarDetectionAnalysis.Should().NotBeNull("the selected IStarDetection creates it with the image");
        }

        [Test]
        public async Task Flipped2DExposureData_TransposesXyArraysIntoRows() {
            const int width = 7;
            const int height = 5;
            var (_, exposureDataFactory, _) = RigFrame.Factories();
            var ints = new int[width, height];
            var ushorts = new ushort[width, height];
            var shorts = new short[width, height];
            var uints = new uint[width, height];
            var bytes = new byte[width, height];
            var expected = new ushort[width * height];
            for (var x = 0; x < width; x++) {
                for (var y = 0; y < height; y++) {
                    var value = (ushort)(1000 * y + x);
                    ints[x, y] = value;
                    ushorts[x, y] = value;
                    shorts[x, y] = (short)value;
                    uints[x, y] = value;
                    bytes[x, y] = (byte)(10 * y + x);
                    expected[y * width + x] = value;
                }
            }

            foreach (Array array in new Array[] { ints, ushorts, shorts, uints }) {
                var image = await exposureDataFactory.CreateFlipped2DExposureData(array, 16, false, RigFrame.LightFrameMetaData()).ToImageData();
                image.Properties.Width.Should().Be(width, array.GetType().Name);
                image.Properties.Height.Should().Be(height, array.GetType().Name);
                image.Data.FlatArray.Should().Equal(expected, array.GetType().Name);
            }

            var byteImage = await exposureDataFactory.CreateFlipped2DExposureData(bytes, 8, false, RigFrame.LightFrameMetaData()).ToImageData();
            byteImage.Data.FlatArray.Should().Equal(Enumerable.Range(0, width * height).Select(i => (ushort)(10 * (i / width) + i % width)));
        }

        [Test]
        public void ImageArrayInt_ClampsOutOfRangeValuesInItsUshortView() {
            var array = new ImageArrayInt(new[] { -1, 0, 65535, 65536, 123 });

            array.FlatArray.Should().Equal(new ushort[] { 65535, 0, 65535, 65535, 123 });
            array.FlatArrayInt.Should().Equal(-1, 0, 65535, 65536, 123);
        }
    }
}
