#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Input {

    /// <summary>
    /// WindowsBase's modifier-key flags, with WPF's values. NINA.Sequencer names them in its drag-and-drop contract
    /// (Interfaces/DragDrop/IDroppable.cs) and in TemplateController's drop handler; there is no keyboard input here.
    /// </summary>
    [Flags]
    public enum ModifierKeys {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Windows = 8
    }
}
