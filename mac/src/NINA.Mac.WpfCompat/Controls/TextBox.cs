#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Windows.Data;

namespace System.Windows {

    /// <summary>
    /// Placeholder for WindowsBase's DependencyProperty identifier. It only appears as the type of a WPF control's static
    /// property field (<see cref="Controls.TextBox.TextProperty"/>); there is no property system here.
    /// </summary>
    public sealed class DependencyProperty {

        internal DependencyProperty(string name) {
            Name = name;
        }

        public string Name { get; }
    }

    /// <summary>
    /// Placeholder for PresentationFramework's FrameworkElement, for the members NINA.Sequencer's XAML tooltip handler
    /// (UserSymbol.ShowSymbols) calls on the TextBox that raised it. There are no WPF elements on macOS: every member throws.
    /// </summary>
    public class FrameworkElement {

        internal FrameworkElement() {
        }

        public object ToolTip {
            get => throw UiOnly.NotSupported("FrameworkElement.ToolTip");
            set => throw UiOnly.NotSupported("FrameworkElement.ToolTip");
        }

        public BindingExpression GetBindingExpression(DependencyProperty dp) {
            throw UiOnly.NotSupported("FrameworkElement.GetBindingExpression");
        }
    }
}

namespace System.Windows.Controls {

    /// <summary>
    /// Placeholder for PresentationFramework's TextBox (WPF: TextBox : TextBoxBase : Control : FrameworkElement), named only by
    /// NINA.Sequencer's XAML tooltip handler UserSymbol.ShowSymbols. There are no WPF controls on macOS: construction throws.
    /// </summary>
    public class TextBox : FrameworkElement {

        public static readonly DependencyProperty TextProperty = new DependencyProperty("Text");

        public TextBox() {
            throw UiOnly.NotSupported("System.Windows.Controls.TextBox");
        }
    }
}
