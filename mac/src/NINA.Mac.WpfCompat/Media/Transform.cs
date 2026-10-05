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
    /// Stand-in for PresentationCore's abstract Transform. Only <see cref="ScaleTransform"/> exists: NINA's
    /// RenderedImage.GetThumbnail scales a bitmap with it through TransformedBitmap. As in WPF the constructor is internal.
    /// </summary>
    public abstract class Transform : Freezable {

        internal Transform() {
        }
    }

    /// <summary>
    /// Stand-in for PresentationCore's ScaleTransform: the two scale factors, settable until frozen. The centre point,
    /// the matrix value and animation are absent.
    /// </summary>
    public sealed class ScaleTransform : Transform {
        private double scaleX = 1.0;
        private double scaleY = 1.0;

        public ScaleTransform() {
        }

        public ScaleTransform(double scaleX, double scaleY) {
            this.scaleX = scaleX;
            this.scaleY = scaleY;
        }

        public double ScaleX {
            get => scaleX;
            set {
                WritePreamble();
                scaleX = value;
            }
        }

        public double ScaleY {
            get => scaleY;
            set {
                WritePreamble();
                scaleY = value;
            }
        }
    }
}
