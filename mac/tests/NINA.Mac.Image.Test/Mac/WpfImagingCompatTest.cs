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
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// Behaviour of NINA.Mac.WpfCompat's imaging stand-ins that NINA.Image relies on. That every public type and member exists
    /// in WPF with the same signature is checked separately against WPF's metadata (NINA.Mac.Engine.Test, WpfApiSurfaceTest);
    /// these tests pin the documented WPF semantics: copy-on-create, stride and rectangle handling, sizes, freezing.
    /// </summary>
    [TestFixture]
    public class WpfImagingCompatTest {

        [Test]
        public void PixelFormats_HaveWpfsBitDepthsAndNames() {
            var formats = new Dictionary<PixelFormat, (int Bits, string Name)> {
                [PixelFormats.Indexed8] = (8, "Indexed8"),
                [PixelFormats.Gray8] = (8, "Gray8"),
                [PixelFormats.Bgr565] = (16, "Bgr565"),
                [PixelFormats.Gray16] = (16, "Gray16"),
                [PixelFormats.Bgr24] = (24, "Bgr24"),
                [PixelFormats.Bgr32] = (32, "Bgr32"),
                [PixelFormats.Bgra32] = (32, "Bgra32"),
                [PixelFormats.Pbgra32] = (32, "Pbgra32"),
                [PixelFormats.Rgb48] = (48, "Rgb48"),
            };
            formats.Should().HaveCount(9, "every format is distinct");
            foreach (var (format, (bits, name)) in formats) {
                format.BitsPerPixel.Should().Be(bits, name);
                format.ToString().Should().Be(name);
                (format == formats.Keys.Single(f => f.ToString() == name)).Should().BeTrue();
            }
            (PixelFormats.Gray16 != PixelFormats.Gray8).Should().BeTrue();
            PixelFormats.Gray16.Equals((object)PixelFormats.Gray16).Should().BeTrue();
        }

        [Test]
        public void Create_CopiesThePixels() {
            var pixels = new ushort[] { 1, 2, 3, 4, 5, 6 };
            var bitmap = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Gray16, null, pixels, 6);
            pixels[0] = 999;

            var copy = new ushort[6];
            bitmap.CopyPixels(copy, 6, 0);
            copy.Should().Equal(1, 2, 3, 4, 5, 6);
        }

        [Test]
        public void Create_WithPaddedSourceRows_And_CopyPixels_WithPaddedDestinationRows() {
            // 3 x 2 Gray8, source stride 4 (one padding byte per row), destination stride 5
            var source = new byte[] { 1, 2, 3, 0xEE, 4, 5, 6, 0xEE };
            var bitmap = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Gray8, null, source, 4);

            var destination = Enumerable.Repeat((byte)0xAA, 10).ToArray();
            bitmap.CopyPixels(destination, 5, 0);

            destination.Should().Equal(1, 2, 3, 0xAA, 0xAA, 4, 5, 6, 0xAA, 0xAA);
        }

        [Test]
        public void CopyPixels_SourceRectangle_AndOffsetInElements() {
            var pixels = Enumerable.Range(0, 20).Select(i => (ushort)i).ToArray(); // 5 x 4 Gray16
            var bitmap = BitmapSource.Create(5, 4, 96, 96, PixelFormats.Gray16, null, pixels, 10);

            var destination = new ushort[2 + 2 * 3];
            bitmap.CopyPixels(new Int32Rect(1, 2, 3, 2), destination, 6, 2);

            destination.Should().Equal(0, 0, 11, 12, 13, 16, 17, 18);
        }

        [Test]
        public void CopyPixels_ToUnmanagedMemory() {
            var pixels = Enumerable.Range(0, 12).Select(i => (byte)(i * 3)).ToArray();
            var bitmap = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Gray8, null, pixels, 4);
            var buffer = Marshal.AllocHGlobal(12);
            try {
                bitmap.CopyPixels(Int32Rect.Empty, buffer, 12, 4);
                var copy = new byte[12];
                Marshal.Copy(buffer, copy, 0, 12);
                copy.Should().Equal(pixels);
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Test]
        public void CreateAndCopy_RejectStridesAndBuffersThatAreTooSmall() {
            var pixels = new ushort[6];
            FluentActions.Invoking(() => BitmapSource.Create(3, 2, 96, 96, PixelFormats.Gray16, null, pixels, 5)).Should().Throw<ArgumentException>("stride below 3 x 2 bytes");
            FluentActions.Invoking(() => BitmapSource.Create(3, 3, 96, 96, PixelFormats.Gray16, null, pixels, 6)).Should().Throw<ArgumentException>("buffer too small");
            FluentActions.Invoking(() => BitmapSource.Create(3, 2, 96, 96, PixelFormats.Indexed8, null, new byte[6], 3)).Should().Throw<InvalidOperationException>("indexed formats need a palette");

            var bitmap = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Gray16, null, pixels, 6);
            FluentActions.Invoking(() => bitmap.CopyPixels(new ushort[6], 4, 0)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => bitmap.CopyPixels(new ushort[5], 6, 0)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => bitmap.CopyPixels(new Int32Rect(2, 0, 2, 1), new ushort[6], 6, 0)).Should().Throw<ArgumentException>("the rectangle leaves the bitmap");
        }

        [Test]
        public void ImageSource_WidthAndHeight_AreDeviceIndependentUnits() {
            var bitmap = BitmapSource.Create(300, 200, 192, 48, PixelFormats.Gray8, null, new byte[300 * 200], 300);

            bitmap.Width.Should().Be(150, "300 px at 192 dpi");
            bitmap.Height.Should().Be(400, "200 px at 48 dpi");
            bitmap.Metadata.Should().BeNull();
            bitmap.Palette.Should().BeNull();
        }

        [Test]
        public void Int32Rect_HasWpfValueSemantics() {
            Int32Rect.Empty.IsEmpty.Should().BeTrue();
            Int32Rect.Empty.HasArea.Should().BeFalse();
            new Int32Rect(1, 2, 3, 4).Should().Be(new Int32Rect(1, 2, 3, 4));
            (new Int32Rect(1, 2, 3, 4) != new Int32Rect(1, 2, 3, 5)).Should().BeTrue();
            Int32Rect.Empty.GetHashCode().Should().Be(0);
            new Int32Rect(1, 2, 3, 4).GetHashCode().Should().Be(1 ^ 2 ^ 3 ^ 4);
            Int32Rect.Empty.ToString().Should().Be("Empty");
            new Int32Rect(1, 2, 3, 4).ToString(CultureInfo.InvariantCulture).Should().Be("1,2,3,4");
            new Int32Rect(1, 2, 3, 4).ToString(new CultureInfo("de-DE")).Should().Be("1;2;3;4", "WPF switches to ';' when the decimal separator is ','");
        }

        [Test]
        public void Freeze_MakesTransformsReadOnly() {
            var scale = new ScaleTransform(0.5, 0.25);
            scale.IsFrozen.Should().BeFalse();
            scale.ScaleX = 2;
            scale.Freeze();

            scale.IsFrozen.Should().BeTrue();
            scale.CanFreeze.Should().BeTrue();
            scale.ScaleX.Should().Be(2);
            FluentActions.Invoking(() => scale.ScaleY = 1).Should().Throw<InvalidOperationException>();
            scale.Freeze();
        }

        [TestCase(1920, 1080, 0.15625, 300, 169)]
        [TestCase(3840, 2160, 300.0 / 3840, 300, 169)]
        [TestCase(5, 3, 0.1, 1, 1)]
        [TestCase(100, 100, 0.005, 1, 1)]
        [TestCase(10, 10, 2.0, 20, 20)]
        public void TransformedBitmap_SizeFollowsWpf(int width, int height, double factor, int expectedWidth, int expectedHeight) {
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray16, null, new ushort[width * height], width * 2);

            var scaled = new TransformedBitmap(source, new ScaleTransform(factor, factor));

            scaled.PixelWidth.Should().Be(expectedWidth);
            scaled.PixelHeight.Should().Be(expectedHeight);
            scaled.Format.Should().Be(PixelFormats.Gray16);
        }

        [Test]
        public void TransformedBitmap_AveragesBoxes_AndRefusesMirroring() {
            // 4 x 2 Gray8 halved: each output pixel is the mean of a 2 x 2 box
            var source = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Gray8, null, new byte[] { 10, 20, 100, 200, 30, 40, 0, 100 }, 4);

            var half = new TransformedBitmap(source, new ScaleTransform(0.5, 0.5));
            var copy = new byte[2];
            half.CopyPixels(copy, 2, 0);

            copy.Should().Equal(25, 100);
            FluentActions.Invoking(() => new TransformedBitmap(source, new ScaleTransform(-1, 1))).Should().Throw<NotSupportedException>();
        }

        [Test]
        public void WriteableAndFormatConvertedBitmaps_CopyTheirSource() {
            var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray16, null, new ushort[] { 1, 2, 3, 4 }, 4);

            var writeable = new WriteableBitmap(source);
            var same = new FormatConvertedBitmap(source, PixelFormats.Gray16, null, 0);

            foreach (var bitmap in new BitmapSource[] { writeable, same }) {
                var copy = new ushort[4];
                bitmap.CopyPixels(copy, 4, 0);
                copy.Should().Equal(1, 2, 3, 4);
            }
            // Real conversions are WIC's job (gamma and colour rules); nothing on the macOS engine path converts
            FluentActions.Invoking(() => new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0)).Should().Throw<NotSupportedException>();
            var initialised = new FormatConvertedBitmap();
            FluentActions.Invoking(() => initialised.Source = source).Should().Throw<InvalidOperationException>("properties are set between BeginInit and EndInit");
        }

        [Test]
        public void Codecs_ThrowPlatformNotSupported() {
            var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray16, null, new ushort[4], 4);
            var metadata = new BitmapMetadata("tiff") { ApplicationName = "N.I.N.A.", Title = "SIMPLE  =                    T" };
            metadata.Title.Should().StartWith("SIMPLE");
            var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };

            FluentActions.Invoking(() => BitmapFrame.Create(source, null, metadata, null)).Should().Throw<PlatformNotSupportedException>();
            FluentActions.Invoking(() => encoder.Save(new MemoryStream())).Should().Throw<PlatformNotSupportedException>();
            var uri = new Uri("file:///tmp/none.png");
            FluentActions.Invoking(() => new PngBitmapDecoder(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)).Should().Throw<PlatformNotSupportedException>();
            FluentActions.Invoking(() => new TiffBitmapDecoder(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)).Should().Throw<PlatformNotSupportedException>();
            FluentActions.Invoking(() => new JpegBitmapDecoder(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)).Should().Throw<PlatformNotSupportedException>();
            FluentActions.Invoking(() => new GifBitmapDecoder(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)).Should().Throw<PlatformNotSupportedException>();
        }
    }
}
