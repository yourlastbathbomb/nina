#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Media {

    /// <summary>
    /// Stand-in for PresentationCore's abstract Geometry, so <see cref="GeometryGroup"/> sits where WPF puts it
    /// (Geometry : Animatable : Freezable; Animatable is left out). Signature-only. As in WPF the constructor is internal.
    /// </summary>
    public abstract class Geometry : Freezable {

        internal Geometry() {
        }
    }

    /// <summary>
    /// Stand-in for PresentationCore's GeometryGroup, signature-only. NINA.Equipment's IDockableVM.ImageGeometry (the dock
    /// panel icon, NINA.Equipment/Interfaces/ViewModel/IDockableVM.cs) is typed with it; on Windows the icons come from XAML
    /// resources. An empty group can be created, as in WPF; children, fill rule and bounds are absent.
    /// </summary>
    public sealed class GeometryGroup : Geometry {

        public GeometryGroup() {
        }
    }
}
