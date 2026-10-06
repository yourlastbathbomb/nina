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
using NINA.Mac.Sequencer.Test.Sim;
using NINA.Mac.Sequencing.Catalogue;
using NINA.Mac.Sequencing.Conditions;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Platesolving;
using NINA.Sequencer.Utility.DateTimeProvider;
using System.ComponentModel.Composition;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// Merge guards for the static catalogue (plan section 2.2): the rig's list is exactly the expected one; every entity that
    /// NINA.Sequencer exports is either catalogued or listed as compiled-only with a reason, so a new upstream entity shows up at
    /// merge time; no meridian-flip trigger exists in the build; the prototypes carry PluginLoader's metadata; the factory hands out
    /// clones.
    /// </summary>
    [TestFixture]
    public class CatalogueTest {
        private SimRig rig = null!;

        [OneTimeSetUp]
        public void Create() {
            rig = SimRig.CreateUnconnected("catalogue");
        }

        [OneTimeTearDown]
        public void Dispose() {
            rig.Host.Dispose();
        }

        private RigSequencerCatalogue Catalogue => rig.Host.Catalogue;

        private static IEnumerable<string> Names(IEnumerable<object> entities) => entities.Select(e => e.GetType().Name).OrderBy(n => n, StringComparer.Ordinal);

        [Test]
        public void Items_AreTheRigsInstructions() {
            Names(Catalogue.Items).Should().Equal(new[] {
                "Annotation", "Center", "ConditionalContainer", "CoolCamera", "DeepSkyObjectContainer", "DewHeater", "Dither", "ExternalScript",
                "GlobalConstant", "GlobalVariable", "ParallelContainer", "ParkScope", "ResetVariable", "ResetVariableToDate", "SequentialContainer",
                "SetTracking", "SetUSBLimit", "SlewScopeToAltAz", "SlewScopeToRaDec", "SmartExposure", "SolveAndSync", "SwitchFilter", "TakeExposure",
                "TakeManyExposures", "UnparkScope", "Variable", "WaitForAltitude", "WaitForMoonAltitude", "WaitForSunAltitude", "WaitForTime",
                "WaitForTimeSpan", "WaitUntil", "WaitUntilAboveHorizon", "WarmCamera"
            }.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Test]
        public void Conditions_AreTheRigsLoopConditions_WithMaxAltitude() {
            Names(Catalogue.Conditions).Should().Equal(new[] {
                "AboveHorizonCondition", "AltitudeCondition", "LoopCondition", "LoopWhile", "MaxAltitudeCondition", "MoonAltitudeCondition",
                "MoonIlluminationCondition", "SunAltitudeCondition", "TimeCondition", "TimeSpanCondition"
            }.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Test]
        public void Triggers_HaveNoMeridianFlip() {
            Names(Catalogue.Triggers).Should().Equal(new[] {
                "CenterAfterDriftTrigger", "DitherAfterExposures", "KeyholeTrigger", "ReconnectOnDownloadFailure", "ReconnectTrigger"
            }.OrderBy(n => n, StringComparer.Ordinal));
            Catalogue.Triggers.Should().NotContain(t => t is IMeridianFlipTrigger);
        }

        [Test]
        public void Containers_AreTheAreasAndInstructionSets() {
            Names(Catalogue.Containers).Should().Equal(new[] {
                "ConditionalContainer", "DeepSkyObjectContainer", "EndAreaContainer", "ParallelContainer", "SequenceRootContainer",
                "SequentialContainer", "SmartExposure", "StartAreaContainer", "TakeManyExposures", "TargetAreaContainer"
            }.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Test]
        public void EveryExportedEntityOfNinaSequencer_IsCataloguedOrListedAsCompiledOnly() {
            var contracts = new[] { typeof(ISequenceItem), typeof(ISequenceCondition), typeof(ISequenceTrigger), typeof(ISequenceContainer) };
            var exported = typeof(ISequenceItem).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(ExportAttribute), false).Cast<ExportAttribute>().Any(e => contracts.Contains(e.ContractType)))
                .ToList();
            exported.Should().HaveCountGreaterThan(50);
            var catalogued = Catalogue.Types.ToHashSet();
            exported.Where(t => !catalogued.Contains(t) && !RigSequencerCatalogue.CompiledButNotCatalogued.ContainsKey(t))
                .Select(t => t.FullName).Should().BeEmpty("a new upstream entity must be catalogued or listed in CompiledButNotCatalogued with a reason");
            RigSequencerCatalogue.CompiledButNotCatalogued.Keys.Should().OnlyContain(t => exported.Contains(t));
            RigSequencerCatalogue.CompiledButNotCatalogued.Values.Should().OnlyContain(reason => reason.Length > 10);
            catalogued.Should().OnlyContain(t => exported.Contains(t) || t == typeof(MaxAltitudeCondition) || t == typeof(NINA.Mac.Sequencing.Triggers.KeyholeTrigger));
        }

        [Test]
        public void NoMeridianFlipTriggerIsCompiled_SoNothingCanScheduleAFlip() {
            var types = typeof(ISequenceItem).Assembly.GetTypes();
            types.Where(t => t.Name.Contains("MeridianFlip", StringComparison.Ordinal) && !t.IsInterface).Should().BeEmpty();
            types.Where(t => !t.IsInterface && typeof(IMeridianFlipTrigger).IsAssignableFrom(t)).Should().BeEmpty();
            // ItemUtility.GetMeridianFlipTime finds no IMeridianFlipTrigger, so dithers and drift centring are never held back
            var container = new SequentialContainer();
            NINA.Sequencer.Utility.ItemUtility.IsTooCloseToMeridianFlip(container, TimeSpan.FromHours(12)).Should().BeFalse();
        }

        [Test]
        public void EveryPrototype_HasNameCategoryAndTheSymbolBroker_AsPluginLoaderAssignsThem() {
            var all = Catalogue.Items.Cast<ISequenceEntity>().Concat(Catalogue.Conditions).Concat(Catalogue.Triggers).Concat(Catalogue.Containers).ToList();
            all.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.Name) && !e.Name.StartsWith("Lbl_", StringComparison.Ordinal), "Lbl_ keys are translated through Loc");
            all.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.Category));
            all.Should().OnlyContain(e => ReferenceEquals(e.SymbolBroker, rig.Host.SymbolBroker));
            all.Should().OnlyContain(e => e.Icon == null, "icons are XAML resources on Windows; there are none headless");
            Catalogue.Conditions.OfType<MaxAltitudeCondition>().Single().Name.Should().Be("Loop until above max altitude");
        }

        [Test]
        public void Lists_AreSortedByCategoryAndName_AsPluginLoaderSortsThem() {
            foreach (var list in new IEnumerable<ISequenceEntity>[] { Catalogue.Items, Catalogue.Conditions, Catalogue.Triggers, Catalogue.Containers }) {
                var keys = list.Select(e => e.Category + e.Name).ToList();
                keys.Should().Equal(keys.OrderBy(k => k));
            }
        }

        [Test]
        public void DualExportedContainers_AreSeparatePrototypes_AsMefCreatesThem() {
            var asItem = Catalogue.Items.OfType<SequentialContainer>().Single(c => c.GetType() == typeof(SequentialContainer));
            var asContainer = Catalogue.Containers.OfType<SequentialContainer>().Single(c => c.GetType() == typeof(SequentialContainer));
            asItem.Should().NotBeSameAs(asContainer);
        }

        [Test]
        public void Factory_ReturnsADistinctCloneOfThePrototype() {
            var factory = rig.Host.Factory;
            var a = factory.GetItem<NINA.Sequencer.SequenceItem.Imaging.TakeExposure>();
            var b = factory.GetItem<NINA.Sequencer.SequenceItem.Imaging.TakeExposure>();
            a.Should().NotBeNull();
            a.Should().NotBeSameAs(b);
            a.Should().NotBeSameAs(Catalogue.Items.OfType<NINA.Sequencer.SequenceItem.Imaging.TakeExposure>().Single());
            a.Name.Should().Be(Catalogue.Items.OfType<NINA.Sequencer.SequenceItem.Imaging.TakeExposure>().Single().Name);
            factory.GetCondition<MaxAltitudeCondition>().Should().NotBeNull();
            factory.GetContainer<SequenceRootContainer>().Should().NotBeNull();
            factory.GetTrigger<CenterAfterDriftTrigger>().Should().NotBeNull();
            factory.GetContainer<LinkedTemplateContainer>().Should().BeNull("compiled, not catalogued: a saved linked template loads as Unknown");
            factory.ItemsView.Should().BeNull();
        }

        [Test]
        public void FactoryMethods_ArePublicInstanceMethods_TheJsonConvertersFindByReflection() {
            foreach (var name in new[] { "GetItem", "GetCondition", "GetTrigger", "GetContainer" }) {
                rig.Host.Factory.GetType().GetMethod(name).Should().NotBeNull(name);
            }
        }

        [Test]
        public void DateTimeProviders_ArePluginLoadersTen_InItsOrder() {
            Catalogue.DateTimeProviders.Select(p => p.GetType()).Should().Equal(
                typeof(NINA.Sequencer.Utility.DateTimeProvider.TimeProvider), typeof(SunsetProvider), typeof(CivilDuskProvider), typeof(NauticalDuskProvider),
                typeof(DuskProvider), typeof(DawnProvider), typeof(NauticalDawnProvider), typeof(CivilDawnProvider), typeof(SunriseProvider),
                typeof(MeridianProvider));
            rig.Host.Factory.DateTimeProviders.Should().BeSameAs(Catalogue.DateTimeProviders);
        }

        [Test]
        public void CenterAfterDriftTrigger_ClonesCarryTheHostsCentringFactories() {
            var trigger = rig.Host.Factory.GetTrigger<CenterAfterDriftTrigger>();
            trigger.CenteringPlateSolverFactory.Should().BeSameAs(rig.Host.PlateSolverFactory);
            trigger.CenteringWindowServiceFactory.Should().BeSameAs(rig.Host.WindowServiceFactory);
            var clone = (CenterAfterDriftTrigger)trigger.Clone();
            clone.CenteringPlateSolverFactory.Should().BeSameAs(rig.Host.PlateSolverFactory);
            clone.CenteringWindowServiceFactory.Should().BeSameAs(rig.Host.WindowServiceFactory);
            // Without the seam (as on Windows) the properties stay null, and Execute creates the factories it always created
            new CenterAfterDriftTrigger(null, null, null, null, null, null, null, null, null, null, null).CenteringWindowServiceFactory.Should().BeNull();
        }

        [Test]
        public void WindowService_ShowIsANoOp_ShowDialogThrows() {
            var service = rig.Host.WindowServiceFactory.Create();
            service.Invoking(s => s.Show(new object(), "Plate solving")).Should().NotThrow();
            service.Invoking(s => s.ShowDialog(new object(), "Prompt")).Should().Throw<PlatformNotSupportedException>();
        }
    }
}
