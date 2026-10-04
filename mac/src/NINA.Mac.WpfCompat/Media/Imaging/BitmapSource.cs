#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Media.Imaging {

    /// <summary>
    /// Placeholder for PresentationCore's abstract BitmapSource. In NINA.Astrometry it only types the optional sky-survey
    /// preview of a deep-sky object (SkyObjectBase.Image and its image factory, which DatabaseInteraction passes as null).
    /// There is no WPF imaging on macOS and no subclass exists, so no instance can be created. Freeze (from WPF's
    /// Freezable) does nothing because there is no state to freeze. Pixel access arrives when NINA.Image is ported.
    /// </summary>
    public abstract class BitmapSource {

        protected BitmapSource() {
        }

        public void Freeze() {
        }
    }
}
