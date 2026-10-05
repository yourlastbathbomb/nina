#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows {

    /// <summary>
    /// Stand-in for WindowsBase's Freezable, the base of WPF's imaging types (ImageSource, BitmapSource, Transform).
    /// Upstream NINA freezes every bitmap it creates so other threads may read it. Here <see cref="Freeze"/> sets
    /// <see cref="IsFrozen"/>, and the derived compat types refuse property changes once frozen, as WPF does
    /// (InvalidOperationException). There is no DependencyObject, no thread affinity and no Changed event.
    /// </summary>
    public abstract class Freezable {
        private volatile bool isFrozen;

        protected Freezable() {
        }

        /// <summary>Always true: no compat type holds state that cannot be frozen.</summary>
        public bool CanFreeze => true;

        public bool IsFrozen => isFrozen;

        /// <summary>Makes the object read-only. Freezing a frozen object does nothing, as in WPF.</summary>
        public void Freeze() {
            isFrozen = true;
        }

        /// <summary>WPF's WritePreamble check: a frozen object cannot be modified.</summary>
        internal void WritePreamble() {
            if (isFrozen) {
                throw new InvalidOperationException($"Cannot set a property on object '{this}' because it is in a read-only state.");
            }
        }
    }
}
