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
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using System.Globalization;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// NINA's own metadata -> FITS header path (FITSHeader.PopulateFromMetaData through BaseImageData.SaveToDisk) for a
    /// bin-2 light from this rig, card by card.
    /// <para>
    /// What "like Windows NINA" means here. The header is built by managed code that is the same IL on both systems, and every
    /// value is formatted with the invariant culture ("0.0##############" for doubles, which .NET formats to 15 significant
    /// digits in managed code on every OS). The expected cards below are written out by hand from those formatting rules
    /// (FITSHeaderCard: value right-justified to column 30, strings quoted from column 11, " / " before the comment, 80 columns),
    /// not captured from a run. Four cards depend on the machine and are checked separately: DATE-LOC (the local time zone),
    /// MJD-OBS and MJD-AVG (computed by the native SOFA library, here libsofa.dylib for arm64), and SWCREATE, which says
    /// "(x64)" because DllLoader.IsX86 only tells 32-bit from 64-bit, exactly as Windows x64 NINA writes it.
    /// </para>
    /// </summary>
    [TestFixture]
    public class FitsHeaderTest {

        /// <summary>The cards Windows NINA writes for <see cref="RigFrame.LightFrameMetaData"/>, in file order, by keyword.</summary>
        private static readonly string[] expectedCards = {
            "SIMPLE  =                    T / C# FITS                                        ",
            "BITPIX  =                   16                                                  ",
            "NAXIS   =                    2 / Dimensionality                                 ",
            "NAXIS1  =                 1920                                                  ",
            "NAXIS2  =                 1080                                                  ",
            "BZERO   =                32768                                                  ",
            "EXTEND  =                    T / Extensions are permitted                       ",
            "IMAGETYP= 'LIGHT'              / Type of exposure                               ",
            "EXPOSURE=                 10.0 / [s] Exposure duration                          ",
            "EXPTIME =                 10.0 / [s] Exposure duration                          ",
            "DATE-LOC",
            "DATE-OBS= '2026-10-04T13:45:30.1234567' / Time of observation (UTC)             ",
            "MJD-OBS ",
            "DATE-AVG= '2026-10-04T13:45:35.2484567' / Averaged midpoint time (UTC)          ",
            "MJD-AVG ",
            "XBINNING=                    2 / X axis binning factor                          ",
            "YBINNING=                    2 / Y axis binning factor                          ",
            "GAIN    =                  200 / Sensor gain                                    ",
            "OFFSET  =                    3 / Sensor gain offset                             ",
            "EGAIN   =    0.239999994635582 / [e-/ADU] Electrons per A/D unit                ",
            "XPIXSZ  =                  5.8 / [um] Pixel X axis size                         ",
            "YPIXSZ  =                  5.8 / [um] Pixel Y axis size                         ",
            "INSTRUME= 'ZWO ASI585MC Pro'   / Imaging instrument name                        ",
            "CAMERAID= 'ZWOptical_ZWO ASI585MC Pro_' / Imaging instrument identifier         ",
            "SET-TEMP=                -10.0 / [degC] CCD temperature setpoint                ",
            "CCD-TEMP=                 -9.8 / [degC] CCD temperature                         ",
            "BAYERPAT= 'RGGB'               / Sensor Bayer pattern                           ",
            "XBAYROFF=                    0 / Bayer pattern X axis offset                    ",
            "YBAYROFF=                    0 / Bayer pattern Y axis offset                    ",
            "USBLIMIT=                   40 / Camera-specific USB setting                    ",
            "TELESCOP= 'Meade LX200GPS 10in' / Name of telescope                             ",
            "FOCALLEN=               2500.0 / [mm] Focal length                              ",
            "FOCRATIO=                 10.0 / Focal ratio                                    ",
            "SITEELEV=                 50.0 / [m] Observation site elevation                 ",
            "SITELAT =                 22.3 / [deg] Observation site latitude                ",
            "SITELONG=               114.18 / [deg] Observation site longitude               ",
            "OBJECT  = 'M 31'               / Name of the object of interest                 ",
            "OBJCTRA = '00 42 44'           / [H M S] RA of imaged object                    ",
            "OBJCTDEC= '+41 16 08'          / [D M S] Declination of imaged object           ",
            "ROWORDER= 'TOP-DOWN'           / FITS Image Orientation                         ",
            "EQUINOX =               2000.0 / Equinox of celestial coordinate system         ",
            "SWCREATE= 'N.I.N.A. 3.3.0.1064 (x64)' / Software that created this file         ",
        };

        /// <summary>Cards whose value depends on the machine; checked by the tests below instead of literally.</summary>
        private static readonly string[] machineDependent = { "DATE-LOC", "MJD-OBS", "MJD-AVG" };

        private static async Task<FitsFile> SaveRigLight(string name) {
            var folder = TestHost.NewImageFolder(name);
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(), RigFrame.Width, RigFrame.Height, RigFrame.LightFrameMetaData());
            return FitsFile.Read(await image.SaveToDisk(RigFrame.SaveInfo(folder)));
        }

        [Test]
        public async Task RigLight_HeaderCards_AreTheOnesWindowsNinaWrites() {
            var file = await SaveRigLight("header");

            file.Cards.Select(c => c.Substring(0, 8)).Should().Equal(expectedCards.Select(c => c.Substring(0, 8)), "the same keywords in the same order");
            for (var i = 0; i < expectedCards.Length; i++) {
                if (!machineDependent.Contains(expectedCards[i].Substring(0, 8).TrimEnd())) {
                    file.Cards[i].Should().Be(expectedCards[i]);
                }
            }
        }

        [Test]
        public async Task RigLight_TheCardsSirilAndThePlanNeed_HaveExactValues() {
            // The keywords the M3b plan (§10) and Siril's OSC pipeline rely on, as values; the literal cards are checked above
            var file = await SaveRigLight("values");

            file.Value("BAYERPAT").Should().Be("RGGB");
            file.Value("ROWORDER").Should().Be("TOP-DOWN");
            file.Double("XPIXSZ").Should().Be(5.8, "the unbinned 2.9 um pixel times XBINNING 2");
            file.Double("YPIXSZ").Should().Be(5.8);
            file.Integer("XBINNING").Should().Be(2);
            file.Integer("YBINNING").Should().Be(2);
            file.Integer("GAIN").Should().Be(200);
            file.Integer("OFFSET").Should().Be(3);
            file.Double("CCD-TEMP").Should().Be(-9.8);
            file.Double("EXPTIME").Should().Be(10.0);
            file.Value("DATE-OBS").Should().Be("2026-10-04T13:45:30.1234567");
        }

        [Test]
        public async Task DateLoc_IsTheExposureStartInTheMachinesTimeZone() {
            var file = await SaveRigLight("dateloc");

            var offset = TimeZoneInfo.Local.GetUtcOffset(RigFrame.ExposureStartUtc);
            var local = new DateTime(RigFrame.ExposureStartUtc.Ticks + offset.Ticks, DateTimeKind.Unspecified);
            file.Card("DATE-LOC").Should().Be(
                $"DATE-LOC= '{local.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)}' / Time of observation (local)           ");
            if (TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.FromHours(8) && !TimeZoneInfo.Local.SupportsDaylightSavingTime) {
                file.Value("DATE-LOC").Should().Be("2026-10-04T21:45:30.1234567", "Hong Kong is UTC+8 all year");
            }
        }

        [Test]
        public async Task MjdObsAndMjdAvg_MatchTheCalendarArithmetic() {
            // MJD-OBS goes through SOFA iauDtf2d (native). Without a leap second on the day it is plain arithmetic:
            // days since 1858-11-17T00:00 UTC. The FITS value has 15 significant digits (10 decimals here).
            var file = await SaveRigLight("mjd");
            var mjdEpoch = new DateTime(1858, 11, 17, 0, 0, 0, DateTimeKind.Utc);

            var expectedObs = (RigFrame.ExposureStartUtc - mjdEpoch).Ticks / (double)TimeSpan.TicksPerDay;
            var expectedAvg = (RigFrame.ExposureStartUtc.AddTicks((RigFrame.ExposureEndUtc - RigFrame.ExposureStartUtc).Ticks / 2) - mjdEpoch).Ticks / (double)TimeSpan.TicksPerDay;

            expectedObs.Should().BeApproximately(61317.5732653178, 1e-10);
            file.Double("MJD-OBS").Should().BeApproximately(expectedObs, 1e-9, "1e-9 d is 86 us; SOFA works on a two-part JD near 2.46e6 whose ulp is 4.7e-10 d");
            file.Double("MJD-AVG").Should().BeApproximately(expectedAvg, 1e-9);
            file.Card("MJD-OBS")!.Should().MatchRegex(@"^MJD-OBS =     61317\.573265317\d / Modified Julian Date of observation +$");
            file.Card("MJD-AVG")!.Should().MatchRegex(@"^MJD-AVG =     61317\.573324634\d / Modified Julian Date of averaged midpoint +$");
        }

        [TestCase("en-US")]
        [TestCase("de-DE")]
        [TestCase("fr-FR")]
        [TestCase("sv-SE")]
        [TestCase("tr-TR")]
        [TestCase("ar-SA")]
        [TestCase("zh-HK")]
        public async Task HeaderBytes_DoNotDependOnTheCurrentCulture(string culture) {
            // Formatting goes through the invariant culture; sv-SE (U+2212 minus sign in ICU) and tr-TR (dotted capital I)
            // are the usual traps on macOS, where .NET takes culture data from ICU rather than from Windows NLS
            var reference = await SaveRigLight("culture-invariant");
            var saved = CultureInfo.CurrentCulture;
            try {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                var file = await SaveRigLight("culture-" + culture);
                file.Cards.Should().Equal(reference.Cards);
                file.Pixels.Should().Equal(reference.Pixels);
            } finally {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Test]
        public async Task MonoBin_HasNoBayerCards_AsOnWindows() {
            // ASICamera.DownloadExposure sets BayerPattern None for ZWO mono bin at bin > 1; then FromCamera has already set
            // SensorType RGGB, and PopulateFromMetaData skips the Bayer cards because BayerPattern is None
            var folder = TestHost.NewImageFolder("monobin");
            var metaData = RigFrame.LightFrameMetaData();
            metaData.Camera.BayerPattern = BayerPatternEnum.None;
            var image = await RigFrame.Capture(RigFrame.RggbMosaic(), RigFrame.Width, RigFrame.Height, metaData, isBayered: false);

            var file = FitsFile.Read(await image.SaveToDisk(RigFrame.SaveInfo(folder)));

            file.Card("BAYERPAT").Should().BeNull();
            file.Card("XBAYROFF").Should().BeNull();
            file.Card("YBAYROFF").Should().BeNull();
            file.Value("ROWORDER").Should().Be("TOP-DOWN");
        }

        [Test]
        public async Task HeaderReadBack_ThroughNinasOwnHeaderParser_RestoresTheMetadata() {
            // FITS.Load hands every card CFITSIO reads to FITSHeader.Add with a type guess, then calls ExtractMetaData. CFITSIO
            // is not available to NINA on macOS yet (plan P5), so the cards come from FitsFile and the guess below is a copy of
            // FITS.cs:122-150. ExtractMetaData is the managed half of NINA's FITS reader.
            var file = await SaveRigLight("readback");
            var header = new FITSHeader(file.Width, file.Height);
            foreach (var card in file.Cards) {
                var keyName = card.Substring(0, 8).TrimEnd();
                var field = card.Substring(10);
                var slash = field.StartsWith("'", StringComparison.Ordinal) ? field.IndexOf(" / ", field.LastIndexOf('\''), StringComparison.Ordinal) : field.IndexOf(" / ", StringComparison.Ordinal);
                var keyValue = (slash >= 0 ? field.Substring(0, slash) : field).Trim();
                var keyComment = slash >= 0 ? field.Substring(slash + 3).Trim() : string.Empty;
                if (string.IsNullOrEmpty(keyValue)) {
                    continue;
                }
                if (keyValue.Equals("T")) {
                    header.Add(keyName, true, keyComment);
                } else if (keyValue.Equals("F")) {
                    header.Add(keyName, false, keyComment);
                } else if (keyValue.StartsWith("'")) {
                    header.Add(keyName, keyValue.TrimStart('\'').TrimEnd('\'', ' ').Replace("''", "'"), keyComment);
                } else if (keyValue.Contains('.')) {
                    if (double.TryParse(keyValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                        header.Add(keyName, value, keyComment);
                    }
                } else if (int.TryParse(keyValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) {
                    header.Add(keyName, integer, keyComment);
                } else {
                    header.Add(keyName, keyValue.TrimStart('\'').TrimEnd('\'', ' ').Replace("''", "'"), keyComment);
                }
            }

            var metaData = header.ExtractMetaData();
            var original = RigFrame.LightFrameMetaData();

            metaData.Image.ImageType.Should().Be("LIGHT");
            metaData.Image.ExposureTime.Should().Be(10);
            metaData.Camera.BinX.Should().Be(2);
            metaData.Camera.BinY.Should().Be(2);
            metaData.Camera.Gain.Should().Be(200);
            metaData.Camera.Offset.Should().Be(3);
            metaData.Camera.ElectronsPerADU.Should().Be(0.239999994635582);
            metaData.Camera.PixelSize.Should().Be(2.9, "XPIXSZ divided by XBINNING");
            metaData.Camera.Name.Should().Be(original.Camera.Name);
            metaData.Camera.Id.Should().Be(original.Camera.Id);
            metaData.Camera.SetPoint.Should().Be(-10);
            metaData.Camera.Temperature.Should().Be(-9.8);
            metaData.Camera.SensorType.Should().Be(SensorType.RGGB);
            metaData.Camera.BayerOffsetX.Should().Be(0);
            metaData.Camera.BayerOffsetY.Should().Be(0);
            metaData.Camera.USBLimit.Should().Be(40);
            metaData.Telescope.Name.Should().Be(original.Telescope.Name);
            metaData.Telescope.FocalLength.Should().Be(2500);
            metaData.Telescope.FocalRatio.Should().Be(10);
            metaData.Observer.Latitude.Should().Be(22.3);
            metaData.Observer.Longitude.Should().Be(114.18);
            metaData.Observer.Elevation.Should().Be(50);
            metaData.Target.Name.Should().Be("M 31");
            // OBJCTRA/OBJCTDEC carry whole seconds
            metaData.Target.Coordinates.RA.Should().BeApproximately(original.Target.Coordinates.RA, 0.5 / 3600.0);
            metaData.Target.Coordinates.Dec.Should().BeApproximately(original.Target.Coordinates.Dec, 0.5 / 3600.0);
            // Upstream ExtractMetaData does not parse DATE-OBS/DATE-LOC back (both lines are commented out in FITSHeader.cs)
            metaData.Image.ExposureStart.Should().Be(DateTime.MinValue);
        }
    }
}
