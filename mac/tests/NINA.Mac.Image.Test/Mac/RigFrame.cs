#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Image.FileFormat;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// Synthetic frames and metadata for this rig (ZWO ASI585MC Pro at bin 2 on a 2500 mm LX200GPS in Hong Kong), filled
    /// the way Windows NINA fills them for an ASI capture, plus NINA's image factories with a mocked profile.
    /// </summary>
    internal static class RigFrame {
        public const int Width = 1920;
        public const int Height = 1080;
        public const ushort Red = 40000;
        public const ushort Green = 10000;
        public const ushort Blue = 2000;

        /// <summary>
        /// NINA's default file pattern (NINA.Profile/ImageFileSettings.cs, filePattern), with Windows' backslash separators.
        /// </summary>
        public const string DefaultFilePattern = "$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$SENSORTEMP$$_$$EXPOSURETIME$$s_$$FRAMENR$$";

        /// <summary>Exposure start used by the header tests: 2026-10-04 13:45:30.1234567 UTC (21:45:30 in Hong Kong).</summary>
        public static readonly DateTime ExposureStartUtc = new DateTime(2026, 10, 4, 13, 45, 30, DateTimeKind.Utc).AddTicks(1234567);

        /// <summary>Exposure end for the 10 s light: the SDK reported the frame ready 10.25 s after the start.</summary>
        public static readonly DateTime ExposureEndUtc = ExposureStartUtc.AddMilliseconds(10250);

        /// <summary>The camera's electrons per ADU at gain 200, as the SDK's C float (ASI_CAMERA_INFO.ElecPerADU) carries it.</summary>
        public const float ElecPerAdu = 0.24f;

        /// <summary>An RGGB mosaic with constant channels: R 40000, G 10000, B 2000 (mean 15500, median 10000).</summary>
        public static ushort[] RggbMosaic(int width = Width, int height = Height) {
            var pixels = new ushort[width * height];
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    pixels[y * width + x] = (x % 2, y % 2) switch {
                        (0, 0) => Red,
                        (1, 1) => Blue,
                        _ => Green
                    };
                }
            }
            return pixels;
        }

        /// <summary>
        /// Every pixel different from its neighbours, rows distinguishable, and both ends of the unsigned 16-bit range present
        /// (0 and 65535 are the BZERO edge cases of the FITS encoding, 32767/32768 the sign boundary of the stored int16).
        /// </summary>
        public static ushort[] Ramp(int width, int height) {
            var pixels = new ushort[width * height];
            for (var i = 0; i < pixels.Length; i++) {
                pixels[i] = (ushort)((i * 7919L + (i / width) * 104729L) % 65536);
            }
            pixels[0] = 0;
            pixels[1] = ushort.MaxValue;
            pixels[^1] = 32768;
            pixels[^2] = 32767;
            return pixels;
        }

        /// <summary>
        /// The metadata Windows NINA attaches to a bin-2 light from this camera. Each block names the upstream code that sets
        /// those fields during a capture; NINA.Equipment is not ported yet, so the values are set here directly.
        /// </summary>
        public static ImageMetaData LightFrameMetaData() {
            var metaData = new ImageMetaData();

            // ASICamera.DownloadExposure (NINA.Equipment/Equipment/MyCamera/ASICamera.cs:415-427): metaData.FromCamera(this)
            // (NINA.Equipment/Utility/ImageMetaDataExtension.cs:34-70) on a fresh ImageMetaData, whose BayerPattern is Auto
            metaData.Camera.Id = "ZWOptical_ZWO ASI585MC Pro_";       // $"{Category}_{Name}_{CameraAlias}", no alias set
            metaData.Camera.Name = "ZWO ASI585MC Pro";
            metaData.Camera.Temperature = -98 / 10.0;                 // ASI_TEMPERATURE is in tenths of a degree
            metaData.Camera.Gain = 200;
            metaData.Camera.Offset = 3;
            metaData.Camera.SetPoint = -10;
            metaData.Camera.BinX = 2;
            metaData.Camera.BinY = 2;
            metaData.Camera.ElectronsPerADU = ElecPerAdu;            // float to double, as ASICamera.ElectronsPerADU => _info.ElecPerADU
            metaData.Camera.PixelSize = 2.9;                          // unbinned ASI_CAMERA_INFO.PixelSize
            metaData.Camera.USBLimit = 40;
            metaData.Camera.SensorType = SensorType.RGGB;             // Auto: the driver's pattern (ASI_BAYER_RG) ...
            metaData.Camera.BayerOffsetX = 0;                         // ... and ASICamera's fixed offsets
            metaData.Camera.BayerOffsetY = 0;
            // ReadoutModes is { "Default" } (one entry), so no ReadoutModeIndex/Name; times are DateTime.UtcNow values
            metaData.Image.SetExposureTimes(ExposureStartUtc, ExposureEndUtc);

            // ImagingVM.AddMetaData (NINA/ViewModel/ImagingVM.cs:162-195), then FromProfile (NINA.Image/ImageData/ImageMetaData.cs:40-58)
            metaData.Image.Id = 1;
            metaData.Image.ExposureTime = 10;
            metaData.Image.Binning = "2x2";
            metaData.Image.ExposureNumber = 1;
            metaData.Image.ImageType = "LIGHT";
            metaData.Target.Name = "M 31";
            metaData.Camera.PixelSize = 2.9;                          // profile CameraSettings.PixelSize, set from the camera on connect
            metaData.Telescope.Name = "Meade LX200GPS 10in";
            metaData.Telescope.FocalLength = 2500;
            metaData.Telescope.FocalRatio = 10;
            metaData.Observer.Latitude = 22.3;
            metaData.Observer.Longitude = 114.18;
            metaData.Observer.Elevation = 50;
            metaData.Target.Coordinates = new Coordinates(Angle.ByHours(0.7123), Angle.ByDegree(41.2689), Epoch.J2000);
            return metaData;
        }

        /// <summary>NINA's image factories with a mocked profile and the given detection/annotation behaviours.</summary>
        public static (ImageDataFactory ImageDataFactory, ExposureDataFactory ExposureDataFactory, Mock<IProfileService> Profile) Factories(
            IStarDetection? starDetection = null, IStarAnnotator? starAnnotator = null) {
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(x => x.ActiveProfile.ImageSettings.AnnotateUnlimitedStars).Returns(false);
            profileService.SetupGet(x => x.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio).Returns(1);
            profileService.SetupGet(x => x.ActiveProfile.FocuserSettings.AutoFocusOuterCropRatio).Returns(1);
            profileService.SetupGet(x => x.ActiveProfile.FocuserSettings.AutoFocusUseBrightestStars).Returns(0);
            profileService.SetupGet(x => x.ActiveProfile.CameraSettings.ASCOMCreate32BitData).Returns(false);
            var detectionSelector = new Mock<IPluggableBehaviorSelector<IStarDetection>>();
            detectionSelector.Setup(x => x.GetBehavior()).Returns(starDetection ?? new StarDetection());
            var annotatorSelector = new Mock<IPluggableBehaviorSelector<IStarAnnotator>>();
            annotatorSelector.Setup(x => x.GetBehavior()).Returns(starAnnotator ?? new StarAnnotator());
            var imageDataFactory = new ImageDataFactory(profileService.Object, detectionSelector.Object, annotatorSelector.Object);
            var exposureDataFactory = new ExposureDataFactory(imageDataFactory, profileService.Object, detectionSelector.Object, annotatorSelector.Object);
            return (imageDataFactory, exposureDataFactory, profileService);
        }

        /// <summary>
        /// The capture path of an ASI frame: ImageArrayExposureData, as ASICamera.DownloadExposure creates it (bit depth 16,
        /// bayered for a colour sensor), then ToImageData as ImagingVM does.
        /// </summary>
        public static async Task<IImageData> Capture(ushort[] pixels, int width, int height, ImageMetaData metaData, bool isBayered = true) {
            var (_, exposureDataFactory, _) = Factories();
            var exposure = exposureDataFactory.CreateImageArrayExposureData(pixels, width, height, 16, isBayered, metaData);
            return await exposure.ToImageData();
        }

        /// <summary>FileSaveInfo with the profile defaults (FITS, legacy managed writer) and NINA's default pattern, into <paramref name="folder"/>.</summary>
        public static FileSaveInfo SaveInfo(string folder, FileTypeEnum fileType = FileTypeEnum.FITS) {
            return new FileSaveInfo {
                FilePath = folder,
                FilePattern = DefaultFilePattern,
                FileType = fileType,
            };
        }
    }
}
