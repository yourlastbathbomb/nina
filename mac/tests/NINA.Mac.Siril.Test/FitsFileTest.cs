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
using NINA.Mac.Siril.Test.Synthetic;
using NUnit.Framework;
using System;
using System.IO;

namespace NINA.Mac.Siril.Test {

    [TestFixture]
    public class FitsFileTest {
        private string dir;

        [SetUp]
        public void SetUp() => dir = TestEnv.NewTempDirectory("fits");

        [TearDown]
        public void TearDown() => TestEnv.Delete(dir);

        [Test]
        public void WriterAndReader_RoundTripPixelsAndHeader() {
            var path = Path.Combine(dir, "frame.fits");
            var data = new ushort[] { 0, 1, 32767, 32768, 65535, 1234 };
            new FitsWriter()
                .Add("IMAGETYP", "LIGHT", "Type of exposure")
                .Add("EXPTIME", 20.0, "[s] Exposure duration")
                .Add("GAIN", 252, "Sensor gain")
                .Add("OBJECT", "Thor's Helmet", "quote inside")
                .Add("DATE-OBS", new DateTime(2026, 10, 3, 13, 0, 0, DateTimeKind.Utc), "UTC")
                .Add("ROWORDER", "TOP-DOWN", "FITS Image Orientation")
                .Write(path, data, 3, 2);

            var image = FitsFile.ReadImage(path);

            image.Width.Should().Be(3);
            image.Height.Should().Be(2);
            image.PlaneCount.Should().Be(1);
            image.Planes[0].Should().Equal(0f, 1f, 32767f, 32768f, 65535f, 1234f);
            image.IsTopDown.Should().BeTrue();
            image.AtTopDown(0, 0, 1).Should().Be(32768f, "TOP-DOWN: stored row 1 is picture row 1");
            image.Header.GetString("IMAGETYP").Should().Be("LIGHT");
            image.Header.GetString("OBJECT").Should().Be("Thor's Helmet");
            image.Header.GetDouble("EXPTIME").Should().Be(20.0);
            image.Header.GetInt("GAIN").Should().Be(252);
            image.Header.GetString("DATE-OBS").Should().Be("2026-10-03T13:00:00.0000000");
            image.Header.Contains("SET-TEMP").Should().BeFalse();
        }

        [Test]
        public void ParseCard_HandlesDExponentsLogicalsAndCommentary() {
            FitsFile.ParseCard("EGAIN", "EGAIN   =              2.5D-01 / e-/ADU".PadRight(80)).Value.Should().Be("2.5D-01");
            FitsHeader.TryParseNumber("2.5D-01", out var v).Should().BeTrue();
            v.Should().Be(0.25);
            FitsFile.ParseCard("SIMPLE", "SIMPLE  =                    T".PadRight(80)).Value.Should().Be("T");
            FitsFile.ParseCard("HISTORY", "HISTORY mean stacking".PadRight(80)).RawValue.Should().BeNull();
            FitsFile.ParseCard("OBJECT", "OBJECT  = 'it''s   '           / c".PadRight(80)).Value.Should().Be("it's");
        }

        [Test]
        public void NotFits_Throws() {
            var path = Path.Combine(dir, "x.fits");
            File.WriteAllBytes(path, new byte[2880]);
            FluentActions.Invoking(() => FitsFile.ReadHeader(path)).Should().Throw<InvalidDataException>();
        }
    }
}
