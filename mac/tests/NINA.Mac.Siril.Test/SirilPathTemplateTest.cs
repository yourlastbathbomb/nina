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
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace NINA.Mac.Siril.Test {

    [TestFixture]
    public class SirilPathTemplateTest {

        private static Dictionary<string, object> Header(double exptime, int gain, int offset, double setTemp, int bin) => new() {
            ["EXPTIME"] = exptime,
            ["GAIN"] = gain,
            ["OFFSET"] = offset,
            ["SET-TEMP"] = setTemp,
            ["XBINNING"] = bin,
        };

        [TestCase(20.0, 252, 50, 0.0, 2, "dark_20s_G252_O50_T0_B2.fit")]
        [TestCase(19.99, 252, 50, -10.0, 2, "dark_19s_G252_O50_T-10_B2.fit")]   // (int) truncates, as siril-cli 1.4.4 parse printed
        [TestCase(0.5, 100, 8, -0.6, 1, "dark_0s_G100_O8_T0_B1.fit")]           // toward zero: -0.6 -> 0
        [TestCase(30.0, 0, 0, -15.5, 4, "dark_30s_G0_O0_T-15_B4.fit")]
        public void MasterDarkName_FollowsCIntCast(double exptime, int gain, int offset, double setTemp, int bin, string expected) {
            SirilPathTemplate.Resolve(SirilPathTemplate.MasterDarkFileName, Header(exptime, gain, offset, setTemp, bin)).Should().Be(expected);
        }

        [TestCase("%03d", 19.99, "019")]
        [TestCase("%+d", 7.9, "+7")]
        [TestCase("%5d", 42.0, "42")]          // padding blanks are removed by Siril
        [TestCase("%-5d", -3.2, "-3")]
        [TestCase("%.1f", 19.99, "20.0")]
        [TestCase("%.1f", 0.5, "0.5")]
        [TestCase("%.2f", 0.125, "0.12")]      // exact binary tie: half to even, like libc
        [TestCase("%.2f", 0.375, "0.38")]
        [TestCase("%.2f", 2.675, "2.67")]      // 2.675 is 2.67499999... in binary
        [TestCase("%f", -1.5, "-1.500000")]
        [TestCase("%08.3f", -1.5, "-001.500")]
        public void NumericFormats_MatchCPrintf(string format, double value, string expected) {
            SirilPathTemplate.Resolve($"$EXPTIME:{format}$", new Dictionary<string, object> { ["EXPTIME"] = value }).Should().Be(expected);
        }

        [Test]
        public void DateMinus12_UsesTheHeaderDateAsWritten() {
            var header = new Dictionary<string, object> {
                ["DATE-OBS"] = new DateTime(2026, 10, 3, 11, 30, 0),   // UTC of 19:30 HKT
                ["DATE-LOC"] = new DateTime(2026, 10, 3, 19, 30, 0),
            };

            // DATE-OBS is UTC, so dm12 rolls over at 20:00 HKT: frames before 20:00 get the previous night's label
            SirilPathTemplate.Resolve("$DATE-OBS:dm12$", header).Should().Be("2026-10-02");
            SirilPathTemplate.Resolve("$DATE-LOC:dm12$", header).Should().Be("2026-10-03");
            SirilPathTemplate.Resolve("$DATE-LOC:dm0$", header).Should().Be("2026-10-03");
        }

        [TestCase("NGC 253", "NGC_253")]
        [TestCase("  M42: Orion?  ", "M42__Orion_")]
        [TestCase("a   b", "a_b")]
        [TestCase("Thor's Helmet", "Thors_Helmet")]   // FITS '' then g_shell_unquote: siril-cli 1.4.4 printed this
        public void StringFormat_SanitisesLikeSiril(string value, string expected) {
            SirilPathTemplate.Resolve("$OBJECT:%s$", new Dictionary<string, object> { ["OBJECT"] = value }).Should().Be(expected);
        }

        [Test]
        public void MissingKey_Throws_LikeSirilAborts() {
            var header = Header(20, 252, 50, 0, 2);
            header.Remove("SET-TEMP");

            FluentActions.Invoking(() => SirilPathTemplate.Resolve(SirilPathTemplate.MasterDarkFileName, header))
                .Should().Throw<SirilPathTemplateException>().Where(e => e.Key == "SET-TEMP");
        }

        [Test]
        public void GetKeys_ListsTheHeaderKeywords() {
            SirilPathTemplate.GetKeys(SirilPathTemplate.MasterDarkFileName).Should().Equal("EXPTIME", "GAIN", "OFFSET", "SET-TEMP", "XBINNING");
        }

        [Test]
        public void ExpressionsWithoutTokens_AreReturnedUnchanged() {
            SirilPathTemplate.Resolve("../masters/pp_flat_stacked", new Dictionary<string, object>()).Should().Be("../masters/pp_flat_stacked");
        }

        [TestCase("$*EXPTIME:%d$")]
        [TestCase("$defdark")]
        [TestCase("$seqname$")]
        [TestCase("$EXPTIME:%x$")]
        [TestCase("$RA:ra$")]
        public void UnsupportedFeatures_Throw(string expression) {
            FluentActions.Invoking(() => SirilPathTemplate.Resolve(expression, new Dictionary<string, object> { ["EXPTIME"] = 20.0, ["RA"] = 10.0 }))
                .Should().Throw<SirilPathTemplateException>();
        }
    }
}
