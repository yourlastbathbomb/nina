#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace Microsoft.Win32 {

    /// <summary>
    /// Placeholder for WPF's OpenFileDialog, which NINA.Core's CoreUtil.GetFilteredFileDialog returns.
    /// File dialogs belong to the macOS UI layer: construction throws.
    /// </summary>
    public sealed class OpenFileDialog {

        public OpenFileDialog() {
            throw new System.PlatformNotSupportedException("Microsoft.Win32.OpenFileDialog is WPF user interface and does not exist in the macOS engine.");
        }

        public string InitialDirectory { get; set; }

        public string FileName { get; set; }

        public string Filter { get; set; }
    }
}
