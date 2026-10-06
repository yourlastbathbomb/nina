#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// Two names that the linked upstream NINA (app) view models use from the NINA app assembly, which the mac build does not have.

namespace NINA.Properties {

    /// <summary>
    /// Stand-in for the NINA app's user settings (NINA/Properties/Settings.settings), for the one value the linked
    /// ViewModel/ImageSaveController.cs reads: the length of its save queue. Upstream's default is 2.
    /// </summary>
    internal sealed class Settings {

        public static Settings Default { get; } = new Settings();

        public int SaveQueueSize { get; set; } = 2;
    }
}

// ImageSaveController.cs: using NINA.ViewModel.Interfaces (unused; the namespace lives in the NINA app)
namespace NINA.ViewModel.Interfaces {
    internal static class MacNamespaceAnchor { }
}
