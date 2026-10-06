#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace NINA.Core.Utility.Http {

    /// <summary>
    /// Mac-only, compile-time stand-in. NINA.Core.Mac leaves out upstream's NINA.Core/Utility/Http/HttpDownloadImageRequest.cs
    /// (it decodes the download with WPF's BitmapImage). Its only user here is AstrometryPlateSolver.GetJobImage, a private
    /// method that nothing calls (the annotated nova.astrometry.net preview). This internal type keeps that dead code compiling
    /// and throws if it is ever reached. Delete it when upstream drops GetJobImage (plan P2) or NINA.Core.Mac compiles the
    /// upstream file again; PlatesolvingParityTest fails when either happens.
    /// </summary>
    internal sealed class HttpDownloadImageRequest : HttpRequest<BitmapSource> {

        public HttpDownloadImageRequest(string url, params object[] parameters) : base(url) {
            this.Parameters = parameters;
        }

        public object[] Parameters { get; }

        public override Task<BitmapSource> Request(CancellationToken ct, IProgress<int> progress = null) {
            throw new PlatformNotSupportedException("Downloading an image as a WPF BitmapSource is not available on macOS.");
        }
    }
}
