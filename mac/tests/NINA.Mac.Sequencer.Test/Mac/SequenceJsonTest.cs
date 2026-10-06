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
using NINA.Astrometry;
using NINA.Mac.Sequencer.Test.Sim;
using NINA.Mac.Sequencing.Conditions;
using NINA.Mac.Sequencing.Planning;
using NINA.Mac.Sequencing.Runner;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility.DateTimeProvider;
using Newtonsoft.Json.Linq;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// NINA's sequence JSON on macOS (plan section 3): generated trees round-trip through NINA's own SequenceJsonConverter over
    /// the headless factory; existing sequences and templates load, with entities this build does not have becoming Unknown
    /// placeholders that validation reports and the run skips; MaxAltitudeCondition serialises under its fork assembly.
    /// </summary>
    [TestFixture]
    public class SequenceJsonTest {
        private SimRig rig = null!;

        [OneTimeSetUp]
        public void Create() {
            rig = SimRig.CreateUnconnected("json", nighttime: new FixedNighttimeCalculator(DateTime.Now.AddHours(3)));
        }

        [OneTimeTearDown]
        public void Dispose() {
            rig.Host.Dispose();
        }

        private static NightPlan EveryOption() {
            var m42 = new Coordinates(Angle.ByHours(5.588), Angle.ByDegree(-5.39), Epoch.J2000);
            var m83 = new Coordinates(Angle.ByHours(13.617), Angle.ByDegree(-29.866), Epoch.J2000);
            return new NightPlan {
                Name = "Every option",
                CoolToC = -5,
                CoolMinutes = 7,
                WarmMinutes = 8,
                Dawn = DawnStop.Nautical,
                DawnOffsetMinutes = -10,
                EndScript = "/opt/homebrew/bin/siril-cli -s \"/Users/me/Siril scripts/night.ssf\"",
                Targets = new[] {
                    new TargetPlan { Name = "M 42", Coordinates = m42, ExposureSeconds = 10, Gain = 252, Offset = 15, Count = 120, DitherEvery = 5,
                        RecenterArcmin = 1.5, RecenterEvery = 5, HorizonOffsetDeg = 2, MinAltitudeDeg = 25, MaxAltitudeDeg = 72,
                        Keyhole = KeyholePolicy.WaitUntilBelow, FieldRotation = FieldRotationPolicy.Stop },
                    new TargetPlan { Name = "M 83", Coordinates = m83, ExposureSeconds = 15, Binning = 1 }
                }
            };
        }

        [Test]
        public void GeneratedTree_RoundTrips_WithTheSameStructureAndSettings() {
            var generated = rig.Host.Generator.Generate(EveryOption());
            var json = rig.Host.Json.Serialize(generated);
            var loaded = (ISequenceRootContainer)rig.Host.Json.Deserialize(json);
            var json2 = rig.Host.Json.Serialize(loaded);
            var reloaded = (ISequenceRootContainer)rig.Host.Json.Deserialize(json2);

            TreeDump.Of(loaded).Should().Be(TreeDump.Of(generated));
            TreeDump.Of(reloaded).Should().Be(TreeDump.Of(generated));
            SequenceJson.WithoutInheritedCoordinates(json2).Should().Be(SequenceJson.WithoutInheritedCoordinates(json));
            new HeadlessSequenceRunner(loaded).UnknownEntities().Should().BeEmpty();
        }

        [Test]
        public void ReloadedTimeConditions_UseTheFactorysOwnDawnProvider() {
            var loaded = (ISequenceRootContainer)rig.Host.Json.Deserialize(rig.Host.Json.Serialize(rig.Host.Generator.Generate(EveryOption())));
            var conditions = new List<TimeCondition>();
            HeadlessSequenceRunner.Walk(loaded, e => { if (e is TimeCondition t) { conditions.Add(t); } });
            conditions.Should().HaveCount(4);
            var nautical = rig.Host.Factory.DateTimeProviders.OfType<NauticalDawnProvider>().Single();
            conditions.Should().OnlyContain(c => ReferenceEquals(c.SelectedProvider, nautical) && c.MinutesOffset == -10);
        }

        [Test]
        public void MaxAltitudeCondition_SerialisesUnderTheForkAssembly_AndLoadsBack() {
            var json = rig.Host.Json.Serialize(rig.Host.Generator.Generate(EveryOption()));
            json.Should().Contain("\"NINA.Mac.Sequencing.Conditions.MaxAltitudeCondition, NINA.Mac.Sequencing\"");
            var loaded = (ISequenceRootContainer)rig.Host.Json.Deserialize(json);
            var conditions = new List<MaxAltitudeCondition>();
            HeadlessSequenceRunner.Walk(loaded, e => { if (e is MaxAltitudeCondition m) { conditions.Add(m); } });
            // M 42 waits out the keyhole (WaitForAltitude items, no condition); M 83 skips it on both of its blocks
            conditions.Select(c => c.MaxAltitude).Should().Equal(75, 75);
            conditions.Should().OnlyContain(c => c.HasDsoParent);
        }

        [Test]
        public void ASequenceWithAnEntityThisBuildLacks_LoadsItAsUnknown_ValidationReportsIt() {
            var json = rig.Host.Json.Serialize(rig.Host.Generator.Generate(EveryOption()));
            // As Windows NINA would see a mac sequence, and the mac build a Windows one with a meridian flip
            var foreign = json.Replace("NINA.Mac.Sequencing.Conditions.MaxAltitudeCondition, NINA.Mac.Sequencing", "Some.Plugin.MaxAltitudeCondition, Some.Plugin")
                .Replace("NINA.Sequencer.Trigger.Guider.DitherAfterExposures, NINA.Sequencer", "NINA.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger, NINA.Sequencer");
            var loaded = (ISequenceRootContainer)rig.Host.Json.Deserialize(foreign);
            var runner = new HeadlessSequenceRunner(loaded);
            var unknown = runner.UnknownEntities();
            // M 83 (keyhole Skip) has the max-altitude stop on both of its blocks, M 42 (WaitUntilBelow) none; both dither
            unknown.OfType<UnknownSequenceCondition>().Should().HaveCount(2);
            unknown.OfType<UnknownSequenceTrigger>().Should().HaveCount(2);
            unknown.Should().HaveCount(4);
            runner.Validate().Should().Contain(issue => issue.Length > 0);
        }

        private static string Examples => Path.Combine(TestHost.NinaRoot, "NINA", "Sequencer", "Examples");

        [Test]
        public void BundledStartupAndEndTemplates_LoadCompletely() {
            foreach (var name in new[] { "Basic Sequence Startup.template.json", "Basic Sequence End.template.json" }) {
                var container = rig.Host.Json.Deserialize(File.ReadAllText(Path.Combine(Examples, name)));
                container.Should().NotBeNull(name);
                var unknown = new List<ISequenceEntity>();
                HeadlessSequenceRunner.Walk(container, e => { if (e.GetType().Name.StartsWith("Unknown", StringComparison.Ordinal)) { unknown.Add(e); } });
                unknown.Should().BeEmpty(name);
                TestContext.Out.WriteLine($"{name}:\n{TreeDump.Of(container)}");
            }
        }

        [Test]
        public void BundledTargetTemplate_Loads_WithAutofocusGuidingRotatorAndMeridianFlipAsUnknown() {
            var container = rig.Host.Json.Deserialize(File.ReadAllText(Path.Combine(Examples, "Basic Sequence Target.template.json")));
            TestContext.Out.WriteLine(TreeDump.Of(container));
            var unknown = new List<ISequenceEntity>();
            HeadlessSequenceRunner.Walk(container, e => { if (e.GetType().Name.StartsWith("Unknown", StringComparison.Ordinal)) { unknown.Add(e); } });
            // The template holds RunAutofocus three times, but two of them sit inside the autofocus triggers' own instruction lists,
            // which load as Unknown triggers as a whole: 3 Unknown instructions and 3 Unknown triggers remain
            unknown.OfType<UnknownSequenceItem>().Select(u => u.Name).Should().HaveCount(3)
                .And.Contain(n => n.Contains("RunAutofocus")).And.Contain(n => n.Contains("StartGuiding")).And.Contain(n => n.Contains("CenterAndRotate"));
            unknown.OfType<UnknownSequenceTrigger>().Select(u => u.Name).Should().HaveCount(3)
                .And.Contain(n => n.Contains("MeridianFlipTrigger")).And.Contain(n => n.Contains("AutofocusAfterFilterChange")).And.Contain(n => n.Contains("AutofocusAfterHFRIncreaseTrigger"));
            unknown.Should().HaveCount(6);
            container.Should().BeOfType<DeepSkyObjectContainer>();
        }

        [Test]
        public void Upstream32Corpus_LoadsEveryEntity_OnlyTheLeftOutOnesAsUnknown() {
            var folder = Path.Combine(TestHost.NinaRoot, "NINA.Test", "Sequencer", "Serialization", "LegacySequences", "v3.2");
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(folder, "master-3.2-all-sequence-entities.manifest.json")));
            var exported = new[] { "Items", "Conditions", "Triggers", "Containers" }.SelectMany(k => manifest[k]!.Values<string>()).Distinct().ToList();
            var container = rig.Host.Json.Deserialize(File.ReadAllText(Path.Combine(folder, "master-3.2-all-sequence-entities.sequence.json")));

            var loaded = new List<ISequenceEntity>();
            HeadlessSequenceRunner.Walk(container, loaded.Add);
            var byName = manifest["Entities"]!.ToDictionary(e => (string)e["Name"]!, e => (string)e["Type"]!);
            var unknownTypes = loaded.Where(e => e.GetType().Name.StartsWith("Unknown", StringComparison.Ordinal))
                .Select(e => UnknownType(e, byName)).Distinct().OrderBy(t => t).ToList();
            var expectedUnknown = exported.Where(t => !rig.Host.Catalogue.Types.Any(c => c.FullName == t)).OrderBy(t => t).ToList();
            TestContext.Out.WriteLine($"{exported.Count} exported 3.2 types; loaded {loaded.Count} entities; unknown types: {unknownTypes.Count}");
            foreach (var t in unknownTypes) {
                TestContext.Out.WriteLine("  unknown " + t);
            }
            unknownTypes.Should().Equal(expectedUnknown, "exactly the 3.2 entities the rig catalogue does not offer load as Unknown");
            exported.Except(expectedUnknown).Should().OnlyContain(t => loaded.Any(e => e.GetType().FullName == t), "every catalogued 3.2 entity loads as itself");
        }

        private static string UnknownType(ISequenceEntity unknown, IReadOnlyDictionary<string, string> manifestTypesByName) {
            // Unknown items, conditions and triggers keep the $type token in their name ("<Unknown Instruction - Type, Assembly> ");
            // an Unknown container's name is overwritten by the JSON's Name, which the corpus manifest maps back to the type
            var name = unknown.GetType().GetProperty("Name")!.GetValue(unknown) as string ?? string.Empty;
            var start = name.IndexOf("NINA.", StringComparison.Ordinal);
            if (start >= 0) {
                var text = name[start..];
                var comma = text.IndexOf(',');
                return comma >= 0 ? text[..comma].Trim() : text.Trim('<', '>', ' ');
            }
            var inner = name.Trim();
            var dash = inner.IndexOf(" - ", StringComparison.Ordinal);
            if (dash >= 0) {
                inner = inner[(dash + 3)..].TrimEnd('>', ' ');
            }
            return manifestTypesByName.TryGetValue(inner, out var type) ? type : "unresolved: " + name;
        }
    }
}
