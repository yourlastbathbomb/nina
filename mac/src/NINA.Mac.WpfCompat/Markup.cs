#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Markup {

    /// <summary>
    /// WPF/System.Xaml's MarkupExtension: an abstract base with one abstract method, reproduced as is so NINA.Core's
    /// EnumBindingSourceExtension.cs (which also holds the non-UI EnumDescriptionTypeConverter) compiles unchanged.
    /// Nothing evaluates markup extensions on macOS.
    /// </summary>
    public abstract class MarkupExtension {

        public abstract object ProvideValue(IServiceProvider serviceProvider);
    }
}
