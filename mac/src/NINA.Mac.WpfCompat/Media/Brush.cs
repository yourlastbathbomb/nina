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
    /// Stand-in for PresentationCore's abstract Brush (Brush : Animatable : Freezable; Animatable is left out, as for
    /// <see cref="Geometry"/>). As in WPF the constructor is not public.
    /// </summary>
    public abstract class Brush : Freezable {

        internal Brush() {
        }
    }

    /// <summary>
    /// Stand-in for PresentationCore's SolidColorBrush: a colour holder. NINA.Sequencer's Expression.InfoButtonColor (the
    /// editor's error-badge colour: White, Orange or Red) creates one per call. It is never drawn here. As in WPF, a frozen
    /// brush refuses a new colour.
    /// </summary>
    public sealed class SolidColorBrush : Brush {
        private Color color;

        public SolidColorBrush() {
        }

        public SolidColorBrush(Color color) {
            this.color = color;
        }

        public Color Color {
            get => color;
            set {
                WritePreamble();
                color = value;
            }
        }
    }
}
