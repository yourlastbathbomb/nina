#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.ComponentModel;

namespace System.Windows.Data {

    /// <summary>
    /// Placeholder for PresentationFramework's CollectionViewSource, which NINA.Sequencer's editor controllers
    /// (SymbolController, SymbolFunctionController, TargetController, TemplateController) and the WPF SequencerFactory use to
    /// build the editor's sidebar views. Only the WPF sequencer editor creates those; the headless sequencer does not, so
    /// construction and <see cref="GetDefaultView"/> throw.
    /// </summary>
    public class CollectionViewSource {

        public CollectionViewSource() {
            throw UiOnly.NotSupported("System.Windows.Data.CollectionViewSource");
        }

        public static ICollectionView GetDefaultView(object source) {
            throw UiOnly.NotSupported("CollectionViewSource.GetDefaultView");
        }

        public object Source { get; set; }

        public ICollectionView View => null;

        public System.Collections.ObjectModel.ObservableCollection<GroupDescription> GroupDescriptions => null;

        public SortDescriptionCollection SortDescriptions => null;
    }

    /// <summary>PresentationFramework's group-by-property rule; holds its property name.</summary>
    public class PropertyGroupDescription : GroupDescription {

        public PropertyGroupDescription(string propertyName) {
            PropertyName = propertyName;
        }

        public string PropertyName { get; set; }
    }

    /// <summary>
    /// Placeholder for PresentationFramework's BindingExpression, which only NINA.Sequencer's tooltip handler
    /// UserSymbol.ShowSymbols (a XAML event handler) reads. There are no bindings here: no instance can exist.
    /// </summary>
    public sealed class BindingExpression {

        private BindingExpression() {
        }

        public object ResolvedSource => throw UiOnly.NotSupported("BindingExpression.ResolvedSource");
    }
}
