#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Data {

    /// <summary>
    /// Placeholder so NINA.Core's LocExtension (a XAML markup extension that lives in Loc.cs) compiles.
    /// XAML bindings do not exist in the macOS engine: construction throws.
    /// </summary>
    public class Binding {

        public Binding(string path) {
            throw UiOnly.NotSupported("System.Windows.Data.Binding");
        }

        public BindingMode Mode { get; set; }

        public object Source { get; set; }
    }

    public enum BindingMode {
        TwoWay = 0,
        OneWay = 1,
        OneTime = 2,
        OneWayToSource = 3,
        Default = 4
    }
}
