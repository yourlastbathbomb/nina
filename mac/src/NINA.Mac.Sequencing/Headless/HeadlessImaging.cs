#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace NINA.Mac.Sequencing.Headless {

    /// <summary>
    /// The image panel without a panel: the IImageControlVM that NINA's ImagingVM hands every frame to. It does what upstream
    /// NINA/ViewModel/ImageControlVM.PrepareImage does up to the display: <c>data.RenderImage()</c> (a Gray16 bitmap over the raw
    /// array, which NINA.Image renders on macOS), then raises <see cref="ImagePrepared"/> (NINA's SymbolBroker updates its Image_*
    /// symbols from it) and keeps the result in <see cref="RenderedImage"/> / <see cref="Image"/>. It does not debayer, stretch,
    /// detect stars or analyse Bahtinov images: upstream's implementations of those use GDI+, which does not exist on macOS
    /// (mac/src/README-engine.md, "What works on macOS"). Saved files are unaffected: NINA saves the raw data, not the rendering.
    /// Commands are null; settings are plain properties.
    /// </summary>
    public class HeadlessImageControl : DockableVM, IImageControlVM {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        public HeadlessImageControl(IProfileService profileService) : base(profileService) {
            Title = "Image";
        }

        public event EventHandler<ImagePreparedEventArgs> ImagePrepared;

        public bool AutoStretch { get; set; }
        public BahtinovImage BahtinovImage => null;
        public ObservableRectangle BahtinovRectangle { get; set; }
        public ICommand CancelPlateSolveImageCommand => null;
        public bool DetectStars { get; set; }
        public ICommand DragMoveCommand => null;
        public double DragResizeBoundary => 0;
        public ICommand DragStartCommand => null;
        public ICommand DragStopCommand => null;
        public BitmapSource Image { get; set; }
#pragma warning disable CS0618 // IImageControlVM types these commands with NINA.Core's obsolete AsyncCommand; they are always null here
        public IAsyncCommand InspectAberrationCommand => null;
        public bool IsLiveViewEnabled => false;
        public IAsyncCommand PlateSolveImageCommand => null;
        public AsyncCommand<bool> PrepareImageCommand => null;
#pragma warning restore CS0618
        public IRenderedImage RenderedImage { get; set; }
        public bool ShowBahtinovAnalyzer { get; set; }
        public bool ShowCrossHair { get; set; }
        public ApplicationStatus Status { get; set; }
        public IWindowServiceFactory WindowServiceFactory { get; set; }
        public int ImageRotation { get; set; }

        /// <summary>Number of frames prepared so far (for status displays and tests).</summary>
        public int PreparedCount { get; private set; }

        public void Dispose() {
        }

        public async Task<IRenderedImage> PrepareImage(IImageData data, PrepareImageParameters parameters, CancellationToken cancelToken) {
            await gate.WaitAsync(cancelToken);
            try {
                if (data == null) {
                    return null;
                }
                var renderedImage = data.RenderImage();
                ImagePrepared?.Invoke(this, new ImagePreparedEventArgs { RenderedImage = renderedImage, Parameters = parameters });
                RenderedImage = renderedImage;
                Image = renderedImage.Image;
                PreparedCount++;
                return renderedImage;
            } finally {
                gate.Release();
            }
        }

        public void UpdateDeviceInfo(CameraInfo cameraInfo) {
        }
    }

    /// <summary>
    /// The statistics panel without a panel, as upstream NINA/ViewModel/ImageStatisticsVM.UpdateStatistics: the frame's
    /// <see cref="AllImageStatistics"/> become <see cref="Statistics"/>, and for a real exposure the statistics task is awaited.
    /// </summary>
    public class HeadlessImageStatistics : DockableVM, IImageStatisticsVM {

        public HeadlessImageStatistics(IProfileService profileService) : base(profileService) {
            Title = "Statistics";
        }

        public AllImageStatistics Statistics { get; set; }

        public async Task UpdateStatistics(IImageData imageData) {
            var exposureTime = imageData.MetaData.Image.ExposureTime;
            Statistics = AllImageStatistics.Create(imageData);
            if (exposureTime >= 0) {
                await imageData.Statistics.Task;
            }
        }
    }
}
