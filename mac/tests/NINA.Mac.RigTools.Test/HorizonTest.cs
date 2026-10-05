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
using NINA.Mac.RigTools.Horizon;
using NINA.Mac.RigTools.Test.Upstream;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace NINA.Mac.RigTools.Test {

    [TestFixture]
    public class HorizonTest {

        /// <summary>research/rig_investigate_solve.md Table 7: the template with INLINE comments that upstream mangles.</summary>
        private const string InlineCommentTemplate =
            "# Deep Water Bay backyard - azimuth(deg, N=0, E=90)  altitude(deg)\n" +
            "0     60\n" +
            "60    60\n" +
            "60.1  25      # steep edge: two points 0.1 deg apart\n" +
            "90    15\n" +
            "135   6\n" +
            "180   4       # over water\n" +
            "225   6\n" +
            "270   15\n" +
            "299.9 25\n" +
            "300   60\n" +
            "360   60\n";

        private static HorizonParseResult Parse(string text, bool strict = false) {
            return HorizonFile.ParseStandard(new StringReader(text), "test", strict);
        }

        private static UpstreamCustomHorizonSnapshot Upstream(string text) {
            return UpstreamCustomHorizonSnapshot.FromReader_Standard(new StringReader(text));
        }

        [Test]
        public void InlineComments_KeepTheirPoints() {
            var result = Parse(InlineCommentTemplate);

            result.RejectedLines.Should().BeEmpty();
            result.PointCount.Should().Be(11);
            result.Profile.GetAltitude(180).Should().Be(4);
            result.Profile.GetAltitude(60.05).Should().BeApproximately(42.5, 1e-9);
            result.Profile.GetAltitude(75).Should().BeApproximately(25 + (15 - 25) * (75 - 60.1) / (90 - 60.1), 1e-9);
        }

        [Test]
        public void Upstream_DropsInlineCommentLines_SilentlyChangingTheProfile() {
            // Characterises the bug the proposed upstream patch fixes (CustomHorizon.cs:97-113).
            var upstream = Upstream(InlineCommentTemplate);

            upstream.Azimuths.Should().NotContain(60.1).And.NotContain(180.0);
            upstream.Warnings.Should().HaveCount(2).And.OnlyContain(w => w.StartsWith("Invalid line for horizon values"));
            upstream.GetAltitude(180).Should().Be(6, "the 4 deg point over water is lost");
            upstream.GetAltitude(61).Should().BeApproximately(58.5, 1e-9, "the steep edge at 60.1 is lost, so the wall leans to 90 deg");
            Parse(InlineCommentTemplate).Profile.GetAltitude(61).Should().BeApproximately(24.6990, 1e-4);
        }

        [Test]
        public void Upstream_AltitudeWarningNamesTheAzimuthToken() {
            // Second upstream defect: CustomHorizon.cs:106 logs columns[0] for a bad altitude.
            var upstream = Upstream("0 10\n100 abc\n200 20\n");
            upstream.Warnings.Should().ContainSingle().Which.Should().Be("Invalid value for altitude 100");
            Parse("0 10\n100 abc\n200 20\n").RejectedLines.Single().Reason.Should().Be("invalid altitude 'abc'");
        }

        [Test]
        public void ProposedUpstreamPatch_KeepsInlineCommentPointsAndMatchesThisParser() {
            var patched = UpstreamCustomHorizonSnapshot.FromReader_Standard_ProposedPatch(new StringReader(InlineCommentTemplate));
            var ours = Parse(InlineCommentTemplate).Profile;

            patched.Warnings.Should().BeEmpty();
            patched.Azimuths.Should().Equal(ours.Points.Select(p => p.AzimuthDeg));
            for (var az = 0.0; az < 360.0; az += 0.25) {
                patched.GetAltitude(az).Should().Be(ours.GetAltitude(az), $"azimuth {az}");
            }
            UpstreamCustomHorizonSnapshot.FromReader_Standard_ProposedPatch(new StringReader("0 10\n100 abc\n200 20\n"))
                .Warnings.Should().ContainSingle().Which.Should().Be("Invalid value for altitude abc");
            // Comment-free files behave exactly as before the patch.
            var plain = "0 60\n60 60\n60.1 25\n90 15\n180 4\n300 60\n";
            var before = Upstream(plain);
            var after = UpstreamCustomHorizonSnapshot.FromReader_Standard_ProposedPatch(new StringReader(plain));
            after.Azimuths.Should().Equal(before.Azimuths);
            for (var az = 0.0; az < 360.0; az += 0.25) {
                after.GetAltitude(az).Should().Be(before.GetAltitude(az));
            }
        }

        [TestCase("0 60\n60 60\n60.1 25\n90 15\n135 6\n180 4\n225 6\n270 15\n299.9 25\n300 60\n360 60\n", TestName = "Parity: corrected research template")]
        [TestCase("10 5\n200 20\n350 8\n", TestName = "Parity: neither 0 nor 360 (tie, nearest to 0 wins)")]
        [TestCase("30 5\n200 20\n350 8\n", TestName = "Parity: neither 0 nor 360 (nearest to 360 wins)")]
        [TestCase("30 5\n200 20\n300 8\n", TestName = "Parity: neither 0 nor 360 (nearest to 0 wins)")]
        [TestCase("15 10\n30 10\n60 15\n90 15\n180 4\n270 15\n300 60\n330 60\n345 60\n", TestName = "Parity: neither 0 nor 360, wall across north (review F1)")]
        [TestCase("0 12\n180 3\n", TestName = "Parity: only 0")]
        [TestCase("90 15\n360 40\n", TestName = "Parity: only 360")]
        [TestCase("0\t10\n45,20\n90;30\n  135   25  \n180 10\n", TestName = "Parity: all four separators")]
        [TestCase("0 10\n90 20\n90 30\n180 5\n", TestName = "Parity: duplicate azimuth, last wins")]
        [TestCase("# header\n\n0 1\n\n   \n120 30\n240 2\n", TestName = "Parity: comments and blank lines")]
        public void CommentFreeFiles_GiveTheSameAltitudesAsUpstream(string text) {
            var ours = Parse(text).Profile;
            var upstream = Upstream(text);

            ours.Points.Select(p => p.AzimuthDeg).Should().Equal(upstream.Azimuths);
            for (var az = -30.0; az <= 400.0; az += 0.25) {
                ours.GetAltitude(az).Should().Be(upstream.GetAltitude(az), $"azimuth {az}");
                upstream.GetAltitude_ProposedPatch(az).Should().Be(upstream.GetAltitude(az), $"the proposed guard changes nothing at azimuth {az}");
            }
            // Where upstream reads 0 by accident (see TinyNegativeAndNonFiniteAzimuths_...), we match the patched upstream.
            foreach (var az in new[] { -1e-14, -2.842170943040401e-14, -1e-300, double.NaN, double.PositiveInfinity, double.NegativeInfinity }) {
                ours.GetAltitude(az).Should().Be(upstream.GetAltitude_ProposedPatch(az), $"azimuth {az:R}");
            }
        }

        /// <summary>Measured every 15 deg but starting at 15: a 60 deg wall from 300 to 345, 10 deg from 15.</summary>
        private const string NoNorthPointFile = "15 10\n30 10\n60 15\n90 15\n180 4\n270 15\n300 60\n330 60\n345 60\n";

        [Test]
        public void MissingZeroAnd360_KeepsUpstreamAltitudesButWarns() {
            // Review F1: upstream grooming copies the altitude at 15 (10 deg) to both 0 and 360 instead of joining
            // 345 (60 deg) to 15 (10 deg) across north, so the wall reads up to 25 deg too low just west of north.
            var result = Parse(NoNorthPointFile);
            var upstream = Upstream(NoNorthPointFile);

            // Parity is kept (the parity test above covers every 0.25 deg), including the under-reported values.
            result.Profile.Points.Select(p => p.AzimuthDeg).Should().Equal(upstream.Azimuths);
            result.Profile.GetAltitude(355).Should().BeApproximately(60 - 50 * 10 / 15.0, 1e-9).And.Be(upstream.GetAltitude(355));
            result.Profile.GetAltitude(0).Should().Be(10);

            // ...but it is no longer silent.
            result.RejectedLines.Should().BeEmpty();
            var warning = result.Warnings.Should().ContainSingle().Subject;
            warning.LineNumber.Should().Be(0);
            warning.Line.Should().BeEmpty();
            warning.Reason.Should().Be("file has no point at azimuth 0 or 360: both get 10°, copied from azimuth 15 as upstream NINA does, " +
                                       "so the horizon between azimuth 345 and 15 is not interpolated across north " +
                                       "(a straight line would give 35° at azimuth 0); add a point at 0 or 360");
            warning.ToString().Should().Be(warning.Reason, "a whole-file warning has no line to quote");
        }

        [TestCase("15 50\n180 4\n345 30\n", "both get 50°, copied from azimuth 15 ", "would give 40° at azimuth 0", TestName = "Wrap warning: over-blocks (tie goes to the 0 side)")]
        [TestCase("30 5\n200 20\n350 8\n", "both get 8°, copied from azimuth 350 ", "would give 7.25° at azimuth 0", TestName = "Wrap warning: nearest to 360 wins")]
        public void MissingZeroAnd360_WarningNamesTheCopiedAltitudeAndTheStraightLine(string text, string copied, string straight) {
            var result = Parse(text);

            result.Warnings.Should().ContainSingle().Which.Reason.Should().Contain(copied).And.Contain(straight);
            // The copied altitude is what upstream reads at 0 and 360.
            var upstream = Upstream(text);
            result.Profile.GetAltitude(0).Should().Be(upstream.GetAltitude(0));
            result.Profile.GetAltitude(359.999).Should().Be(upstream.GetAltitude(359.999));
        }

        [TestCase("0 12\n180 3\n", TestName = "No wrap warning: only 0")]
        [TestCase("90 15\n360 40\n", TestName = "No wrap warning: only 360")]
        [TestCase("-0 12\n180 3\n", TestName = "No wrap warning: -0 counts as 0")]
        [TestCase("0 60\n180 4\n360 60\n", TestName = "No wrap warning: both")]
        public void ZeroOr360Present_GivesNoWrapWarning(string text) {
            Parse(text).Warnings.Should().BeEmpty();
        }

        [Test]
        public void Mw4_MissingZeroAnd360_WarnsToo() {
            var result = HorizonFile.ParseMw4(new StringReader("[[10, 15], [4, 180], [60, 345]]"), "site.hpts");

            result.Warnings.Should().ContainSingle().Which.Reason.Should().StartWith("file has no point at azimuth 0 or 360: both get 10°, copied from azimuth 15 ");
            HorizonFile.ParseMw4(new StringReader("[[10, 0], [4, 180]]"), "site.hpts").Warnings.Should().BeEmpty();
        }

        [Test]
        public void TinyNegativeAndNonFiniteAzimuths_ReadTheProfileNotAnOpenHorizon() {
            // Review F1: EuclidianModulus(-1e-14, 360) = -1e-14 + 360, which rounds to exactly 360.0; Interpolate1D
            // has no knot above 360 and returns its fallback 0. Upstream therefore reads the 60 deg northern wall
            // as open sky at those azimuths and at NaN. The planner normalises azimuths first, but a slew guard fed
            // an atan2 azimuth in (-180, 180] or a mount reporting -0.00000x would not.
            var text = HorizonFile.ReadPlaceholderText();
            var placeholder = SiteHorizons.DeepWaterBayPlaceholder;
            var upstream = Upstream(text);

            foreach (var az in new[] { -1e-14, -2.842170943040401e-14, -1e-300, -0.0 }) {
                placeholder.GetAltitude(az).Should().Be(60, $"azimuth {az:R} is due north");
                placeholder.GetAltitude(az).Should().Be(placeholder.GetAltitude(0));
                upstream.GetAltitude_ProposedPatch(az).Should().Be(60, $"patched upstream at azimuth {az:R}");
            }
            // One ulp further from 0 the sum no longer rounds to 360, so upstream was already right there.
            placeholder.GetAltitude(-5.684341886080802e-14).Should().Be(60);
            upstream.GetAltitude(-5.684341886080802e-14).Should().Be(60);

            // Non-finite azimuths fail closed: the highest point of the profile.
            foreach (var az in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }) {
                placeholder.GetAltitude(az).Should().Be(placeholder.MaxAltitude, $"azimuth {az}");
            }
            HorizonProfile.FromPoints(new[] { new HorizonPoint(0, 10), new HorizonPoint(180, 35) }).GetAltitude(double.NaN).Should().Be(35);

            // Characterisation of unpatched upstream (the defect the proposed GetAltitude patch fixes).
            upstream.GetAltitude(-1e-14).Should().Be(0, "unpatched upstream falls off the end of the knots");
            upstream.GetAltitude(-2.842170943040401e-14).Should().Be(0);
            upstream.GetAltitude(double.NaN).Should().Be(0);
            upstream.GetAltitude(double.PositiveInfinity).Should().Be(0);
        }

        [Test]
        public void Interpolate1D_MatchesAccordOnEdgeCases() {
            double[] x = { 0, 10, 10.5, 360 };
            double[] y = { 1, 2, 7, 1 };
            foreach (var v in new[] { -1.0, 0, 5, 10, 10.25, 10.5, 200, 359.999, 360, 361 }) {
                HorizonProfile.Interpolate1D(v, x, y, -5, -6).Should().Be(Accord.Math.Tools.Interpolate1D(v, x, y, -5, -6), $"value {v}");
            }
        }

        [Test]
        public void BlankAndWhitespaceLines_AreIgnoredWithoutIssues() {
            var result = Parse("\n\n0 10\n   \n\t\n180 20   # trailing comment\n# only comment\n#\n");

            result.RejectedLines.Should().BeEmpty();
            result.Warnings.Should().BeEmpty();
            result.PointCount.Should().Be(2);
            result.Profile.GetAltitude(90).Should().Be(15);
            result.Profile.GetAltitude(360).Should().Be(10);
        }

        [Test]
        public void BadLines_AreReportedWithLineNumbersNotSilentlyDropped() {
            var text = "0 10\nabc 10\n100\n100 20 30\nNaN 10\n400 10\n100 95\n180 20\n";

            var result = Parse(text);

            result.PointCount.Should().Be(2);
            result.RejectedLines.Select(r => r.LineNumber).Should().Equal(2, 3, 4, 5, 6, 7);
            result.RejectedLines[0].Reason.Should().Be("invalid azimuth 'abc'");
            result.RejectedLines[1].Reason.Should().Be("expected 2 values (azimuth altitude), found 1");
            result.RejectedLines[2].Reason.Should().Be("expected 2 values (azimuth altitude), found 3");
            result.RejectedLines[3].Reason.Should().Be("invalid azimuth 'NaN'");
            result.RejectedLines[4].Reason.Should().Be("azimuth 400 outside [0, 360]");
            result.RejectedLines[5].Reason.Should().Be("altitude 95 outside [-90, 90]");
        }

        [Test]
        public void Strict_ThrowsListingEveryBadLine() {
            FluentActions.Invoking(() => Parse("0 10\n90 x\n180 20 # ok\n270 5 6\n", strict: true))
                .Should().Throw<FormatException>()
                .Which.Message.Should().Contain("2 invalid line(s)").And.Contain("line 2").And.Contain("line 4");
        }

        [Test]
        public void DuplicateAzimuth_LastWinsAndIsReported() {
            var result = Parse("0 10\n90 20\n90 30\n180 5\n");

            result.Profile.GetAltitude(90).Should().Be(30);
            result.Warnings.Should().ContainSingle().Which.LineNumber.Should().Be(3);
        }

        [Test]
        public void TooFewPoints_ThrowsLikeUpstream() {
            FluentActions.Invoking(() => Parse("# nothing\n90 10 # one point\n"))
                .Should().Throw<ArgumentException>().WithMessage("Horizon file does not contain enough entries or is invalid");
        }

        [Test]
        public void Load_ReadsStandardAndMountWizzardFiles() {
            var dir = Path.Combine(Path.GetTempPath(), "rigtools-horizon-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var hrz = Path.Combine(dir, "site.hrz");
                File.WriteAllText(hrz, InlineCommentTemplate);
                HorizonFile.Load(hrz).Profile.GetAltitude(180).Should().Be(4);

                var hpts = Path.Combine(dir, "site.hpts");
                File.WriteAllText(hpts, "[[10, 0], [20, 180], [5, 270]]");
                var mw4 = HorizonFile.Load(hpts).Profile;
                mw4.GetAltitude(0).Should().Be(10);
                mw4.GetAltitude(90).Should().Be(15);
                mw4.GetAltitude(359.99).Should().BeApproximately(10, 0.01);

                File.WriteAllText(hpts, "[[95, 0], [20, 180]]");
                FluentActions.Invoking(() => HorizonFile.Load(hpts)).Should().Throw<ArgumentException>().WithMessage("Invalid altitude 95*");

                FluentActions.Invoking(() => HorizonFile.Load(Path.Combine(dir, "missing.hrz"))).Should().Throw<FileNotFoundException>();
            } finally {
                Directory.Delete(dir, true);
            }
        }

        private static HorizonProfile Mw4(string json) {
            return HorizonFile.ParseMw4(new StringReader(json), "site.hpts").Profile;
        }

        // Review F2: upstream reads coordinates with Newtonsoft JToken.Value<double>(), which also converts numeric
        // strings, and its JsonTextReader skips comments and trailing commas.
        [TestCase("[[10, 0], [20, 180], [5, 270]]", TestName = "MW4 parity: numbers")]
        [TestCase("[[\"10\",\"0\"],[\"20\",\"180\"]]", TestName = "MW4 parity: numeric strings")]
        [TestCase("[[\" 1e1 \",0],[20.5,\"180\"],[\"5\",\"359.5\"]]", TestName = "MW4 parity: exponent, padding and mixed kinds")]
        [TestCase("/* MW4 */ [[10, 0], [20, 180],] // trailing comment", TestName = "MW4 parity: comments and a trailing comma")]
        [TestCase("[[10,30],[20,180],[12,30]]", TestName = "MW4 parity: duplicate azimuth, last wins")]
        public void Mw4_AcceptsWhatUpstreamAccepts_WithTheSameAltitudes(string json) {
            var ours = Mw4(json);
            var upstream = UpstreamCustomHorizonSnapshot.FromReader_MW4(new StringReader(json));

            ours.Points.Select(p => p.AzimuthDeg).Should().Equal(upstream.Azimuths);
            for (var az = 0.0; az <= 360.0; az += 0.25) {
                ours.GetAltitude(az).Should().Be(upstream.GetAltitude(az), $"azimuth {az}");
            }
        }

        [TestCase("[[\"1,000\",0],[20,180]]", "Invalid altitude 1000 found in MW4-formatted horizon file", TestName = "MW4 same error as upstream: thousands separator, out of range")]
        [TestCase("[[95, 0], [20, 180]]", "Invalid altitude 95 found in MW4-formatted horizon file", TestName = "MW4 same error as upstream: altitude above 90")]
        [TestCase("[[10, 0], [20, \"400\"]]", "Invalid azimuth 400 found in MW4-formatted horizon file", TestName = "MW4 same error as upstream: azimuth above 360")]
        [TestCase("[[10, 0, 1], [20, 180]]", "Expected JSON 2-element array for each point in MW4-formatted horizon file", TestName = "MW4 same error as upstream: three values")]
        [TestCase("[[10, 0], 5]", "Expected JSON array for each point in MW4-formatted horizon file", TestName = "MW4 same error as upstream: bare number")]
        [TestCase("{\"points\": []}", "Expected JSON array in MW4-formatted horizon file", TestName = "MW4 same error as upstream: object")]
        [TestCase("[[10, 0]]", "Horizon file does not contain enough entries or is invalid", TestName = "MW4 same error as upstream: one point")]
        public void Mw4_RejectsWhatUpstreamRejects_WithTheSameMessage(string json, string message) {
            FluentActions.Invoking(() => Mw4(json)).Should().Throw<ArgumentException>().WithMessage(message);
            FluentActions.Invoking(() => UpstreamCustomHorizonSnapshot.FromReader_MW4(new StringReader(json))).Should().Throw<ArgumentException>().WithMessage(message);
        }

        [TestCase("[[10, 0], [20, 180", "MW4-formatted horizon file site.hpts is not valid JSON: *", TestName = "MW4 rejects (upstream loads it): truncated file")]
        [TestCase("[[10,0],[20,180]] [[1,2]]", "MW4-formatted horizon file site.hpts is not valid JSON: *", TestName = "MW4 rejects (upstream ignores it): text after the array")]
        [TestCase("", "MW4-formatted horizon file site.hpts is not valid JSON: *", TestName = "MW4 rejects (upstream: NullReferenceException): empty file")]
        [TestCase("[[true,0],[20,180]]", "Invalid altitude true in point 1 of MW4-formatted horizon file site.hpts: expected a number", TestName = "MW4 rejects (upstream reads 1): boolean")]
        [TestCase("[[\"NaN\",0],[20,180]]", "Invalid altitude NaN found in MW4-formatted horizon file", TestName = "MW4 rejects (upstream loads it): NaN")]
        [TestCase("[[null,0],[20,180]]", "Invalid altitude null in point 1 of MW4-formatted horizon file site.hpts: expected a number", TestName = "MW4 rejects (upstream: InvalidCastException): null")]
        [TestCase("[[10,0],[[1],180]]", "Invalid altitude [1] in point 2 of MW4-formatted horizon file site.hpts: expected a number", TestName = "MW4 rejects (upstream: InvalidCastException): nested array")]
        [TestCase("[[10,{}],[20,180]]", "Invalid azimuth {} in point 1 of MW4-formatted horizon file site.hpts: expected a number", TestName = "MW4 rejects (upstream: InvalidCastException): object")]
        [TestCase("[[\"abc\",0],[20,180]]", "Invalid altitude \"abc\" in point 1 of MW4-formatted horizon file site.hpts: expected a number", TestName = "MW4 rejects (upstream: FormatException): non-numeric string")]
        public void Mw4_DeliberateDifferences_AreAlwaysArgumentExceptions(string json, string message) {
            FluentActions.Invoking(() => Mw4(json)).Should().Throw<ArgumentException>().WithMessage(message);
        }

        [Test]
        public void Mw4_UpstreamLeniency_ThatTheDeliberateDifferencesRefuse() {
            // Characterises the claims in the HorizonFile remarks against the upstream snapshot.
            UpstreamCustomHorizonSnapshot.FromReader_MW4(new StringReader("[[10, 0], [20, 180")).Azimuths.Should().Equal(0.0, 180.0, 360.0);
            UpstreamCustomHorizonSnapshot.FromReader_MW4(new StringReader("[[10,0],[20,180]] [[1,2]]")).Azimuths.Should().Equal(0.0, 180.0, 360.0);
            UpstreamCustomHorizonSnapshot.FromReader_MW4(new StringReader("[[true,0],[20,180]]")).GetAltitude(0).Should().Be(1);
            double.IsNaN(UpstreamCustomHorizonSnapshot.FromReader_MW4(new StringReader("[[\"NaN\",0],[20,180]]")).GetAltitude(0)).Should().BeTrue();
            FluentActions.Invoking(() => UpstreamCustomHorizonSnapshot.FromReader_MW4(new StringReader(""))).Should().Throw<NullReferenceException>();
            FluentActions.Invoking(() => UpstreamCustomHorizonSnapshot.FromReader_MW4(new StringReader("[[null,0],[20,180]]"))).Should().Throw<InvalidCastException>();
            FluentActions.Invoking(() => UpstreamCustomHorizonSnapshot.FromReader_MW4(new StringReader("[[\"abc\",0],[20,180]]"))).Should().Throw<FormatException>();
        }

        [Test]
        public void Placeholder_BlocksTheNorthAndLoadsUnchangedInUpstream() {
            var text = HorizonFile.ReadPlaceholderText();
            var placeholder = SiteHorizons.DeepWaterBayPlaceholder;

            placeholder.IsPlaceholder.Should().BeTrue();
            text.Should().Contain("PLACEHOLDER");
            Parse(text).RejectedLines.Should().BeEmpty();
            Parse(text).Warnings.Should().BeEmpty();
            placeholder.GetAltitude(0).Should().Be(60);
            placeholder.GetAltitude(330).Should().Be(60);
            placeholder.GetAltitude(30).Should().Be(60);
            placeholder.GetAltitude(180).Should().Be(4);
            placeholder.MaxAltitude.Should().Be(60);
            placeholder.MinAltitude.Should().Be(4);

            var upstream = Upstream(text);
            upstream.Warnings.Should().BeEmpty("comments sit on their own lines so unpatched NINA reads every point");
            for (var az = 0.0; az < 360.0; az += 0.5) {
                placeholder.GetAltitude(az).Should().Be(upstream.GetAltitude(az));
            }
        }

        [Test]
        public void Flat_IsConstant() {
            var flat = HorizonProfile.Flat(12.5);
            foreach (var az in new[] { -10.0, 0, 90, 359.5, 360, 725 }) {
                flat.GetAltitude(az).Should().Be(12.5);
            }
            flat.IsPlaceholder.Should().BeFalse();
        }

        [Test]
        public void StripComment_KeepsTextBeforeHash() {
            HorizonFile.StripComment("  180 4   # over water ").Should().Be("180 4");
            HorizonFile.StripComment("# all comment").Should().BeEmpty();
            HorizonFile.StripComment(null).Should().BeEmpty();
        }
    }
}
