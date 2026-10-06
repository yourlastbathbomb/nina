#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Model;
using NINA.WPF.Base.Utility.AutoFocus;
using NINA.WPF.Base.ViewModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Input;

namespace NINA.Mac.Sequencing.Headless {

    /// <summary>
    /// The image history without its charts: a port of the counting core of upstream NINA/ViewModel/ImageHistory/ImageHistoryVM.cs
    /// (ImageHistory, GetNextImageId, Add, PopulateStatistics, AppendImageProperties, AppendAutoFocusPoint, PlotClear). It is not
    /// a null object: NINA's DitherAfterExposures counts dithers off <see cref="ImageHistory"/> (TakeExposure adds each LIGHT and
    /// SNAPSHOT frame), and ImagingVM numbers every frame with <see cref="GetNextImageId"/>, which the $$IMAGEID$$ pattern and the
    /// FITS header use. As upstream, ids come from one process-wide counter that only <see cref="PlotClear"/> resets. The CSV
    /// export, filter lists and plot views are left out (the mac UI draws its own).
    /// </summary>
    public class HeadlessImageHistory : DockableVM, IImageHistoryVM {
        private static int exposureId = 0;
        private readonly object lockObj = new object();

        public HeadlessImageHistory(IProfileService profileService, IImageSaveMediator imageSaveMediator) : base(profileService) {
            Title = "Image history";
            ObservableImageHistory = new AsyncObservableCollection<ImageHistoryPoint>();
            AutoFocusPoints = new AsyncObservableCollection<ImageHistoryPoint>();
            if (imageSaveMediator != null) {
                imageSaveMediator.ImageSaved += (sender, e) => AppendImageProperties(e);
            }
        }

        public AsyncObservableCollection<ImageHistoryPoint> AutoFocusPoints { get; set; }

        public List<ImageHistoryPoint> ImageHistory { get; private set; } = new List<ImageHistoryPoint>();

        public AsyncObservableCollection<ImageHistoryPoint> ObservableImageHistory { get; set; }

        public ICommand PlotClearCommand => null;

        public int GetNextImageId() {
            return Interlocked.Increment(ref exposureId);
        }

        public void Add(int id, IImageStatistics statistics, string imageType) {
            lock (lockObj) {
                var point = new ImageHistoryPoint(id, statistics, imageType);
                point.SetArcsecPerPixel(GetArcsecPerPixel());
                ImageHistory.Add(point);
            }
        }

        public void Add(int id, string imageType) {
            lock (lockObj) {
                var point = new ImageHistoryPoint(id, imageType);
                point.SetArcsecPerPixel(GetArcsecPerPixel());
                ImageHistory.Add(point);
            }
        }

        public void PopulateStatistics(int id, IImageStatistics statistics) {
            lock (lockObj) {
                ImageHistory.FirstOrDefault(item => item.Id == id)?.PopulateStatistics(statistics);
            }
        }

        public void AppendImageProperties(ImageSavedEventArgs imageSavedEventArgs) {
            if (imageSavedEventArgs == null) {
                return;
            }
            ImageHistoryPoint point;
            lock (lockObj) {
                point = ImageHistory.FirstOrDefault(item => item.Id == imageSavedEventArgs.MetaData.Image.Id);
            }
            if (point != null) {
                point.PopulateProperties(imageSavedEventArgs);
                point.SetArcsecPerPixel(GetArcsecPerPixel());
                ObservableImageHistory.Add(point);
            }
        }

        public void AppendAutoFocusPoint(AutoFocusReport report) {
            if (report == null) {
                return;
            }
            ImageHistoryPoint last;
            lock (lockObj) {
                last = ImageHistory.LastOrDefault();
            }
            last ??= new ImageHistoryPoint(0, "NONE");
            last.PopulateAFPoint(report);
            AutoFocusPoints.Add(last);
        }

        public void PlotClear() {
            ObservableImageHistory.Clear();
            AutoFocusPoints.Clear();
            lock (lockObj) {
                ImageHistory.Clear();
            }
            exposureId = 0;
            Logger.Info("Image history has been cleared");
        }

        // As upstream ImageHistoryVM.GetArcsecPerPixel
        private double GetArcsecPerPixel() {
            var pixelSize = profileService.ActiveProfile?.CameraSettings?.PixelSize ?? double.NaN;
            var focalLength = profileService.ActiveProfile?.TelescopeSettings?.FocalLength ?? double.NaN;

            if (double.IsNaN(pixelSize) || double.IsNaN(focalLength) || pixelSize <= 0 || focalLength <= 0) {
                return double.NaN;
            }

            return NINA.Astrometry.AstroUtil.ArcsecPerPixel(pixelSize, focalLength);
        }
    }
}
