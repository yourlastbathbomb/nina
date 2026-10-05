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
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// The rendering glue that the save, plate-solve and star-detection paths go through, running on NINA.Mac.WpfCompat's
    /// managed BitmapSource: BaseImageData.RenderBitmapSource/RenderImage (a Gray16 bitmap over the raw array),
    /// RenderedImage.GetThumbnail (the plate-solve progress thumbnail, TransformedBitmap + WriteableBitmap) and
    /// ImageArrayExposureData.FromBitmapSource (pixels back out with CopyPixels).
    /// </summary>
    [TestFixture]
    public class RenderingGlueTest {

        [Test]
        public async Task RenderBitmapSource_IsAFrozenGray16CopyOfTheRawArray() {
            var pixels = RigFrame.Ramp(RigFrame.Width, RigFrame.Height);
            var image = await RigFrame.Capture(pixels, RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());

            var bitmap = image.RenderBitmapSource();

            bitmap.Format.Should().Be(PixelFormats.Gray16);
            bitmap.PixelWidth.Should().Be(RigFrame.Width);
            bitmap.PixelHeight.Should().Be(RigFrame.Height);
            bitmap.DpiX.Should().Be(96);
            bitmap.Width.Should().Be(RigFrame.Width, "1 DIP per pixel at 96 dpi");
            bitmap.IsFrozen.Should().BeTrue("ImageUtility.CreateSourceFromArray freezes it");
            var copy = new ushort[pixels.Length];
            bitmap.CopyPixels(copy, RigFrame.Width * 2, 0);
            copy.Should().Equal(pixels);
        }

        [Test]
        public async Task RenderImage_WrapsTheRawImageData() {
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(), RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());

            var rendered = image.RenderImage();

            rendered.RawImageData.Should().BeSameAs(image);
            rendered.Image.Should().BeSameAs(rendered.OriginalImage);
            rendered.Image.Format.Should().Be(PixelFormats.Gray16);
            var reRendered = rendered.ReRender();
            reRendered.RawImageData.Should().BeSameAs(image);
            reRendered.Image.Should().NotBeSameAs(rendered.Image);
        }

        [Test]
        public async Task GetThumbnail_Is300PixelsWide_WithWpfsSizeRounding() {
            var pixels = Enumerable.Repeat((ushort)12345, RigFrame.Width * RigFrame.Height).ToArray();
            var image = await RigFrame.Capture(pixels, RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());

            var thumbnail = await image.RenderImage().GetThumbnail();

            // factor 300 / 1920 = 0.15625; WPF sizes a scaled bitmap as (uint)(factor * pixels + 0.5): 300 x 169
            thumbnail.Should().NotBeNull();
            thumbnail.PixelWidth.Should().Be(300);
            thumbnail.PixelHeight.Should().Be(169);
            thumbnail.Format.Should().Be(PixelFormats.Gray16);
            thumbnail.IsFrozen.Should().BeTrue();
            var copy = new ushort[300 * 169];
            thumbnail.CopyPixels(copy, 300 * 2, 0);
            copy.Should().OnlyContain(v => v == 12345, "averaging a uniform frame gives the same value");
        }

        [Test]
        public async Task GetThumbnail_OfTheRigMosaic_AveragesTheBayerPattern() {
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(), RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());

            var thumbnail = await image.RenderImage().GetThumbnail();

            var copy = new ushort[thumbnail.PixelWidth * thumbnail.PixelHeight];
            thumbnail.CopyPixels(copy, thumbnail.PixelWidth * 2, 0);
            // Each thumbnail pixel covers 6.4 x 6.4 mosaic pixels, so it is close to the frame mean of 15500
            copy.Average(v => (double)v).Should().BeApproximately(15500, 15500 * 0.01);
            copy.Should().OnlyContain(v => v > 10000 && v < 22000);
        }

        [Test]
        public async Task FromBitmapSource_Gray16_KeepsThePixels_InUpstreamsDoubleLengthArray() {
            // Upstream ImageArrayExposureData.ArrayFrom16BitSource (ImageArrayExposureData.cs:113-119) sizes its ushort[] as
            // stride * height with the stride in bytes, so the array has 2 x width x height elements: the pixels, then zeros.
            // That is managed code plus WPF's CopyPixels contract, so Windows NINA builds the same array (an upstream bug,
            // reported in mac/src/README-engine.md). Only Gray16 BitmapSources (simulator and file cameras) take this path.
            var pixels = RigFrame.Ramp(101, 67);
            var source = BitmapSource.Create(101, 67, 96, 96, PixelFormats.Gray16, null, pixels, 101 * 2);
            var (imageDataFactory, _, _) = RigFrame.Factories();

            var exposure = await ImageArrayExposureData.FromBitmapSource(source, imageDataFactory);
            var image = await exposure.ToImageData();

            image.Properties.Width.Should().Be(101);
            image.Properties.Height.Should().Be(67);
            image.Properties.BitDepth.Should().Be(16);
            image.Properties.IsBayered.Should().BeFalse();
            image.Data.FlatArray.Should().HaveCount(2 * pixels.Length);
            image.Data.FlatArray.Take(pixels.Length).Should().Equal(pixels);
            image.Data.FlatArray.Skip(pixels.Length).Should().OnlyContain(v => v == 0);
        }

        [Test]
        public async Task FromBitmapSource_Gray8_ScalesTo16Bit() {
            var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            var source = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Gray8, null, bytes, 16);
            var (imageDataFactory, _, _) = RigFrame.Factories();

            var image = await (await ImageArrayExposureData.FromBitmapSource(source, imageDataFactory)).ToImageData();

            // ArrayFrom8BitSource: (ushort)(value * (65535 / 255.0)), i.e. value * 257
            image.Properties.BitDepth.Should().Be(8);
            image.Data.FlatArray.Should().Equal(bytes.Select(b => (ushort)(b * 257)));
        }

        [Test]
        public async Task FromBitmapSource_ColourBitmaps_NeedWicConversion_AndThrow() {
            // Bgr24/Bgr32/Pbgra32 go through a FormatConvertedBitmap to Gray8 (WIC). Only WPF's simulator camera feeds those.
            var source = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgr24, null, new byte[4 * 4 * 3], 12);
            var (imageDataFactory, _, _) = RigFrame.Factories();

            await FluentActions.Awaiting(() => ImageArrayExposureData.FromBitmapSource(source, imageDataFactory)).Should().ThrowAsync<NotSupportedException>();
        }
    }
}
