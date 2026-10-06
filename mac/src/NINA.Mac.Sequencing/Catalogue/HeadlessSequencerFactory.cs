#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility.DateTimeProvider;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace NINA.Mac.Sequencing.Catalogue {

    /// <summary>
    /// The sequencer factory of the headless engine: the prototype lists of a <see cref="RigSequencerCatalogue"/> without the
    /// editor's sidebar views. Upstream's SequencerFactory builds four WPF collection views in its constructor; nothing headless
    /// shows them, so the view properties are null, as in upstream's own test factory (CorpusSequencerFactory in
    /// NINA.Test/Sequencer/Serialization/LegacySequenceMigrationCorpusTest.cs).
    /// <para>
    /// The GetX&lt;T&gt; methods are copied from upstream SequencerFactory.cs: a clone of the first prototype of exactly that type,
    /// or null. They must stay public instance methods: NINA's JSON creation converters find them by reflection on the concrete
    /// factory type (Factory.GetType().GetMethod(nameof(Factory.GetItem))), and an entity whose method is not found loads as an
    /// Unknown placeholder.
    /// </para>
    /// </summary>
    public class HeadlessSequencerFactory : ISequencerFactory {

        public HeadlessSequencerFactory(RigSequencerCatalogue catalogue) {
            Items = catalogue.Items;
            Conditions = catalogue.Conditions;
            Triggers = catalogue.Triggers;
            Container = catalogue.Containers;
            DateTimeProviders = catalogue.DateTimeProviders;
            Upgraders = new List<ISequenceEntityUpgrader>();
        }

        public IList<ISequenceItem> Items { get; }
        public IList<ISequenceCondition> Conditions { get; }
        public IList<ISequenceTrigger> Triggers { get; }
        public IList<ISequenceContainer> Container { get; }
        public IList<IDateTimeProvider> DateTimeProviders { get; }
        public IList<ISequenceEntityUpgrader> Upgraders { get; }

        public ICollectionView ItemsView => null;
        public ICollectionView InstructionsView => null;
        public ICollectionView ConditionsView => null;
        public ICollectionView TriggersView => null;
        public string ViewFilter { get; set; } = string.Empty;

        public T GetContainer<T>() where T : ISequenceContainer {
            return (T)(Container.FirstOrDefault(x => x.GetType() == typeof(T))?.Clone() ?? default(T));
        }

        public T GetItem<T>() where T : ISequenceItem {
            return (T)(Items.FirstOrDefault(x => x.GetType() == typeof(T))?.Clone() ?? default(T));
        }

        public T GetCondition<T>() where T : ISequenceCondition {
            return (T)(Conditions.FirstOrDefault(x => x.GetType() == typeof(T))?.Clone() ?? default(T));
        }

        public T GetTrigger<T>() where T : ISequenceTrigger {
            return (T)(Triggers.FirstOrDefault(x => x.GetType() == typeof(T))?.Clone() ?? default(T));
        }
    }
}
