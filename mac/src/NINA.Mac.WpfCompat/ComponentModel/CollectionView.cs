#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace System.ComponentModel {

    /// <summary>
    /// WindowsBase's collection-view contract, reduced to the members NINA.Sequencer uses: the sequencer factory's and the
    /// editor controllers' sidebar views (ISequencerFactory.ItemsView and friends) group, sort, filter and refresh through it.
    /// The headless sequencer never creates a view (its factory returns null for the four view properties), so nothing here
    /// implements it.
    /// </summary>
    public interface ICollectionView : IEnumerable, INotifyCollectionChanged {

        IEnumerable SourceCollection { get; }

        Predicate<object> Filter { get; set; }

        bool CanFilter { get; }

        SortDescriptionCollection SortDescriptions { get; }

        bool CanSort { get; }

        bool CanGroup { get; }

        ObservableCollection<GroupDescription> GroupDescriptions { get; }

        void Refresh();
    }

    /// <summary>WindowsBase's abstract grouping rule. Only the type: the headless engine never groups a view.</summary>
    public abstract class GroupDescription {

        protected GroupDescription() {
        }
    }

    /// <summary>WindowsBase's sort rule (property name and direction), a plain value as in WPF.</summary>
    public struct SortDescription {
        private string propertyName;
        private ListSortDirection direction;

        public SortDescription(string propertyName, ListSortDirection direction) {
            if (direction != ListSortDirection.Ascending && direction != ListSortDirection.Descending) {
                throw new InvalidEnumArgumentException(nameof(direction), (int)direction, typeof(ListSortDirection));
            }
            this.propertyName = propertyName;
            this.direction = direction;
        }

        public string PropertyName {
            get => propertyName;
            set => propertyName = value;
        }

        public ListSortDirection Direction {
            get => direction;
            set => direction = value;
        }
    }

    /// <summary>WindowsBase's list of sort rules. A plain collection; no view listens to it here.</summary>
    public class SortDescriptionCollection : Collection<SortDescription> {

        public SortDescriptionCollection() {
        }
    }
}
