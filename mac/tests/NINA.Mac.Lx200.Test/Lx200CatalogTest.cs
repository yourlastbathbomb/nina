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
using NINA.Mac.Lx200;
using NINA.Mac.Lx200.Sim;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NINA.Mac.Lx200.Test {

    [TestFixture]
    public class Lx200CatalogTest {

        [Test]
        public void Catalog_EveryEntryHasShapeSourceAndUniqueCode() {
            Lx200Catalog.All.Select(c => c.Code).Should().OnlyHaveUniqueItems();
            foreach (var c in Lx200Catalog.All) {
                c.Reply.Should().NotBeNull(c.Code);
                c.Source.Should().NotBeNullOrWhiteSpace(c.Code);
                c.Purpose.Should().NotBeNullOrWhiteSpace(c.Code);
                c.Timeout.Should().BePositive(c.Code);
            }
            Lx200Catalog.All.Count(c => !c.IsBlocked).Should().BeGreaterThanOrEqualTo(35, "the rig needs about 35 commands");
        }

        [Test]
        public void Catalog_ReplyShapes_FromTheSpecAndResearch() {
            Lx200Catalog.Ack.Reply.Kind.Should().Be(ReplyKind.Char);
            Lx200Catalog.ByCode("GR").Reply.Kind.Should().Be(ReplyKind.Terminated);
            Lx200Catalog.ByCode("D").Reply.Kind.Should().Be(ReplyKind.Terminated);
            Lx200Catalog.ByCode("CM").Reply.Kind.Should().Be(ReplyKind.Terminated);
            Lx200Catalog.ByCode("St").Reply.Should().BeSameAs(ReplyShape.Bool);
            Lx200Catalog.ByCode("MS").Reply.Kind.Should().Be(ReplyKind.CharThenTerminatedIfNotZero);
            Lx200Catalog.ByCode("SC").Reply.Kind.Should().Be(ReplyKind.CharThenTwoTerminatedIfOne);
            Lx200Catalog.ByCode("P").Reply.Kind.Should().Be(ReplyKind.OneOf);
            Lx200Catalog.ByCode("P").Reply.Alternatives.Should().BeEquivalentTo("HIGH PRECISION", "LOW PRECISION");
            Lx200Catalog.ByCode("GW").Reply.Kind.Should().Be(ReplyKind.FixedThenOptionalHash);
            Lx200Catalog.ByCode("GW").Reply.Length.Should().Be(3);
            Lx200Catalog.ByCode("Gh").Reply.Kind.Should().Be(ReplyKind.UntilDegreeThenOptionalHash);
            foreach (var none in new[] { "Q", "Qn", "Mn", "Mgn", "F+", "F-", "FQ", "FP", "F1", "U", "AA", "AL", "RG", "Rg" }) {
                Lx200Catalog.ByCode(none).Reply.Kind.Should().Be(ReplyKind.None, none);
            }
            Lx200Catalog.ByCode("SC").Timeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10), "the planetary update follows :SC");
        }

        [Test]
        public void Catalog_Effects() {
            Lx200Catalog.ByCode("GR").Effect.Should().Be(CommandEffect.ReadOnly);
            Lx200Catalog.ByCode("MS").Effect.Should().Be(CommandEffect.MovesHardware);
            Lx200Catalog.ByCode("Mgn").Effect.Should().Be(CommandEffect.MovesHardware);
            Lx200Catalog.ByCode("F+").Effect.Should().Be(CommandEffect.MovesHardware);
            Lx200Catalog.ByCode("FP").Effect.Should().Be(CommandEffect.MovesHardware);
            Lx200Catalog.ByCode("SC").Effect.Should().Be(CommandEffect.WritesConfig);
            Lx200Catalog.ByCode("Q").Effect.Should().Be(CommandEffect.Halt);
            Lx200Catalog.ByCode("FQ").Effect.Should().Be(CommandEffect.Halt);
            Lx200Catalog.ByCode("SC").NeedsConfirmation.Should().BeTrue();
            Lx200Catalog.ByCode("Sr").NeedsConfirmation.Should().BeFalse("setting a target is volatile");
            Lx200Catalog.ByCode("U").NeedsConfirmation.Should().BeFalse();
            Lx200Catalog.ByCode("Q").NeedsConfirmation.Should().BeFalse();
            Lx200Catalog.ByCode("Mn").NeedsConfirmation.Should().BeTrue();
        }

        [Test]
        public void Blocklist_HasParkAndAReasonForEach() {
            var blocked = Lx200Catalog.BlockedCommands.Select(c => c.Code).ToList();
            blocked.Should().Contain(new[] { "hP", "hN", "hF", "hC", "I", "Aa", "AP", "SB", "SS", "gT", "f-", "$Q", "$B" });
            foreach (var c in Lx200Catalog.BlockedCommands) {
                c.BlockedReason.Should().NotBeNullOrWhiteSpace(c.Code);
                c.Source.Should().StartWith("P07", c.Code);
            }
        }

        [TestCase("GR", "GR")]
        [TestCase("Gr", "Gr")]
        [TestCase("Ms", "Ms")]
        [TestCase("MS", "MS")]
        [TestCase("Mgn2000", "Mgn")]
        [TestCase("Sr05:35:17", "Sr")]
        [TestCase("SL01:00:00", "SL")]
        [TestCase("FP+1000", "FP")]
        [TestCase("F1", "F1")]
        [TestCase("Q", "Q")]
        [TestCase("Qn", "Qn")]
        [TestCase("Gg", "Gg")]
        [TestCase("GG", "GG")]
        [TestCase("hP", "hP")]
        [TestCase("hPx", "hP")]
        [TestCase("SB6", "SB")]
        [TestCase("$QZ+", "$Q")]
        [TestCase("I", "I")]
        [TestCase("AP", "AP")]
        public void Match_LongestCaseSensitiveCode(string body, string code) {
            Lx200Catalog.Match(body).Code.Should().Be(code);
        }

        [TestCase("Mg")]
        [TestCase("Sr")]
        [TestCase("GRX")]
        [TestCase("XYZ")]
        [TestCase("gr")]
        public void Match_Unknown_IsNull(string body) {
            Lx200Catalog.Match(body).Should().BeNull();
        }

        [Test]
        public void Command_Parse_AddsFramingAndRefusesPackedCommands() {
            Lx200Command.Parse("GR").Text.Should().Be(":GR#");
            Lx200Command.Parse(" :GR# ").Spec.Code.Should().Be("GR");
            Lx200Command.Parse("ack").IsAck.Should().BeTrue();
            Lx200Command.Parse("\u0006").IsAck.Should().BeTrue();
            Lx200Command.Parse(":SL01:00:00#").Spec.Code.Should().Be("SL");
            Lx200Command.Parse(":XY#").IsKnown.Should().BeFalse();
            Lx200Command.Parse(":XY#").NeedsConfirmation.Should().BeTrue("unknown commands are treated as motion");
            FluentActions.Invoking(() => Lx200Command.Parse(":Q#:hP#")).Should().Throw<Lx200MalformedCommandException>();
            FluentActions.Invoking(() => Lx200Command.Parse(":G\u0001R#")).Should().Throw<Lx200MalformedCommandException>();
            FluentActions.Invoking(() => Lx200Command.FromBytes(new byte[] { 0x47, 0x52 })).Should().Throw<Lx200MalformedCommandException>();
            FluentActions.Invoking(() => Lx200Command.Parse(":" + new string('G', 80) + "#")).Should().Throw<Lx200MalformedCommandException>();
        }

        [Test]
        public void Command_InnerColonSegmentThatIsBlocked_IsBlocked() {
            Lx200Command.Parse(":Q:hP#").Spec.IsBlocked.Should().BeTrue();
            Lx200Command.Parse(":Sr12:hP#").Spec.Code.Should().Be("hP");
        }
    }

    /// <summary>
    /// Every allowed catalog command against the simulator: the connection must read exactly the bytes the
    /// simulator sends (nothing left over, nothing missing), and the link must still be in step afterwards.
    /// </summary>
    [TestFixture]
    public class ReplyShapeFramingTest {

        public static IEnumerable<TestCaseData> AllowedCommands() =>
            Lx200Catalog.All.Where(c => !c.IsBlocked).Select(c => new TestCaseData(c.Code).SetName($"Framing_{Sanitize(c.Code)}"));

        private static string Sanitize(string code) => code == Lx200CommandSpec.AckCode ? "ACK" : code.Replace("+", "Plus").Replace("-", "Minus").Replace("$", "Dollar");

        [TestCaseSource(nameof(AllowedCommands))]
        public void CatalogShape_MatchesSimulatorBytes(string code) {
            var spec = Lx200Catalog.ByCode(code);
            using var h = new SimHarness(linkOptions: new Lx200ConnectionOptions {
                TrailingWindow = TimeSpan.FromMilliseconds(150),   // anything after the expected shape shows up here
                ResyncOnFailure = false,
                MinimumGap = TimeSpan.FromMilliseconds(5)
            });
            var command = spec.Code == Lx200CommandSpec.AckCode ? Lx200Command.FromBytes(new[] { ReplyShape.Ack }) : Lx200Command.Parse(spec.Example);

            var reply = h.Link.Send(command);

            reply.Status.Should().BeOneOf(new[] { ReplyStatus.Ok, ReplyStatus.NoReplyExpected }, reply.Describe());
            reply.Trailing.Should().BeEmpty(reply.Describe());
            reply.Stale.Should().BeEmpty(reply.Describe());
            reply.Error.Should().BeNull(reply.Describe());
            if (spec.Reply.ExpectsReply) {
                reply.Raw.Should().NotBeEmpty();
            } else {
                reply.Raw.Should().BeEmpty();
            }
            // still in step: an ACK gets exactly one mode character and nothing else
            var ack = h.Link.Ack();
            ack.Status.Should().Be(ReplyStatus.Ok, ack.Describe());
            ack.Stale.Should().BeEmpty();
            ack.Trailing.Should().BeEmpty();
            "AL".Should().Contain(ack.Value);
            h.Sim.ReceivedCommands.Should().Contain(command.IsAck ? "ACK" : command.Text);
            h.Link.StopAll("test cleanup");
        }

        [Test]
        public void Parts_OfMultiPartReplies() {
            using var h = new SimHarness();
            var sc = h.Link.Send(":SC10/04/26#");
            sc.Parts.Should().HaveCount(3);
            sc.Parts[0].Should().Be("1");
            sc.Parts[1].Should().Be("Updating Planetary Data");
            sc.Parts[2].Trim().Should().BeEmpty();

            var ms = h.Link.Send(":MS#");
            ms.Parts.Should().Equal("0");

            h.Link.Send(":Sa-20*00:00#").Value.Should().Be("1");
            h.Link.Send(":Sz180*00:00#").Value.Should().Be("1");
            h.Link.Send(":Sr" + Lx200Format.FormatRa(h.Sim.BelievedRaDec.RaHours + 12, CoordinatePrecision.Low) + "#");
            h.Link.Send(":Sd-80*00#");
            var below = h.Link.Send(":MS#");
            below.Status.Should().Be(ReplyStatus.Ok);
            below.Parts[0].Should().Be("1");
            below.Parts[1].Should().Contain("Horizon");

            var cm = h.Link.Send(":CM#");
            cm.Value.Should().Be(" M31 EX GAL MAG 3.5 SZ178.0'");

            var p = h.Link.Send(":P#");
            p.Value.Should().Be("HIGH PRECISION");
            p.TerminatorSeen.Should().BeFalse();
            h.Link.Send(":P#").Value.Should().Be("LOW PRECISION");

            var gw = h.Link.Send(":GW#");
            gw.Value.Should().Be("AT2");
            gw.TerminatorSeen.Should().BeFalse();

            var gh = h.Link.Send(":Gh#");
            gh.Value.Should().Be("+90\u00DF");
            gh.TerminatorSeen.Should().BeFalse();
            var go = h.Link.Send(":Go#");
            go.Value.Should().Be("00\u00DF");
            go.TerminatorSeen.Should().BeTrue();
        }

        [Test]
        public void GwWithHash_IsSwallowed_AndGwUnsupported_IsSilent() {
            using (var h = new SimHarness(new SimOptions { GwHasHash = true }, new Lx200ConnectionOptions { TrailingWindow = TimeSpan.FromMilliseconds(150) })) {
                var gw = h.Link.Send(":GW#");
                gw.Status.Should().Be(ReplyStatus.Ok);
                gw.Value.Should().Be("AT2");
                gw.TerminatorSeen.Should().BeTrue();
                gw.Trailing.Should().BeEmpty();
            }
            using (var h = new SimHarness(new SimOptions { GwSupported = false })) {
                var gw = h.Link.Send(":GW#", timeout: TimeSpan.FromMilliseconds(300));
                gw.Status.Should().Be(ReplyStatus.Silent);
                gw.Resynced.Should().BeTrue("a missing reply triggers ACK resync");
                h.Link.Send(":GVP#").Value.Should().Be("LX2001");
            }
        }

        [Test]
        public void DegreeStar_And_ColonSeparator_Parse() {
            using var h = new SimHarness(new SimOptions { DegreeByte = (byte)'*', SecondsSeparator = ':', StartInLongFormat = true });
            var gd = h.Link.Send(":GD#");
            gd.Raw.Should().NotContain(Lx200Format.DegreeByte);
            gd.Value.Should().MatchRegex(@"^[+-]\d\d\*\d\d:\d\d$");
            Lx200Format.ParseDegrees(gd.Value).Should().BeApproximately(h.Sim.BelievedRaDec.DecDeg, 1.0 / 3600);
        }

        [Test]
        public void HighPrecisionPointing_GotoNeverFinishes_UntilQ() {
            using var h = new SimHarness(new SimOptions { HighPrecisionPointing = true, SlewSeconds = 0.2 });
            h.Link.Send(":MS#").Value.Should().Be("0");
            Thread.Sleep(500);
            h.Link.Send(":D#").Value.Trim().Should().NotBeEmpty("with High Precision ON the Autostar waits for ENTER");
            h.Link.Send(":Q#");
            h.Link.Send(":D#").Value.Should().BeEmpty();
        }
    }
}
