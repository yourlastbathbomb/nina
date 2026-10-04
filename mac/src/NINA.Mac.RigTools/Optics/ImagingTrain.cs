#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.RigTools.Astronomy;
using System;
using System.Globalization;

namespace NINA.Mac.RigTools.Optics {

    /// <summary>
    /// Sensor + telescope + binning: pixel scale, field of view, and the pixel radius that field rotation
    /// acts on. Defaults are the rig: ZWO ASI585MC Pro (IMX585, 3840 x 2160, 2.9 um) on a Meade 10" LX200R
    /// at f/10 (2500 mm) or with the f/6.3 reducer (nominal 0.63 x 2500 = 1575 mm; the user's quoted
    /// 0.37"/px implies ~1595-1639 mm, so confirm with a solve - research/rig_verify_solve.md SLV-01).
    /// </summary>
    public sealed class ImagingTrain {
        public const int Asi585WidthPx = 3840;
        public const int Asi585HeightPx = 2160;
        public const double Asi585PixelSizeUm = 2.9;
        public const double NativeFocalLengthMm = 2500.0;
        public const double ReducerNominalFocalLengthMm = 1575.0;

        public ImagingTrain(string name, int sensorWidthPx, int sensorHeightPx, double pixelSizeUm, double focalLengthMm, int binning) {
            if (sensorWidthPx <= 0) { throw new ArgumentOutOfRangeException(nameof(sensorWidthPx)); }
            if (sensorHeightPx <= 0) { throw new ArgumentOutOfRangeException(nameof(sensorHeightPx)); }
            if (!(pixelSizeUm > 0)) { throw new ArgumentOutOfRangeException(nameof(pixelSizeUm)); }
            if (!(focalLengthMm > 0)) { throw new ArgumentOutOfRangeException(nameof(focalLengthMm)); }
            if (binning < 1 || binning > sensorWidthPx || binning > sensorHeightPx) { throw new ArgumentOutOfRangeException(nameof(binning)); }
            Name = name ?? string.Empty;
            SensorWidthPx = sensorWidthPx;
            SensorHeightPx = sensorHeightPx;
            PixelSizeUm = pixelSizeUm;
            FocalLengthMm = focalLengthMm;
            Binning = binning;
        }

        /// <summary>ASI585MC at native f/10, 2500 mm. Bin 2x2 is the rig's usual mode.</summary>
        public static ImagingTrain Asi585Native(int binning = 2) {
            return new ImagingTrain("ASI585MC @ 2500 mm (f/10)", Asi585WidthPx, Asi585HeightPx, Asi585PixelSizeUm, NativeFocalLengthMm, binning);
        }

        /// <summary>ASI585MC with the f/6.3 reducer; nominal 1575 mm unless a solved focal length is given.</summary>
        public static ImagingTrain Asi585Reducer(int binning = 2, double focalLengthMm = ReducerNominalFocalLengthMm) {
            return new ImagingTrain(string.Format(CultureInfo.InvariantCulture, "ASI585MC @ {0:0} mm (f/6.3 reducer)", focalLengthMm),
                Asi585WidthPx, Asi585HeightPx, Asi585PixelSizeUm, focalLengthMm, binning);
        }

        public string Name { get; }
        public int SensorWidthPx { get; }
        public int SensorHeightPx { get; }
        public double PixelSizeUm { get; }
        public double FocalLengthMm { get; }
        public int Binning { get; }

        /// <summary>Binned frame width; ZWO drops the remainder (3840 / 2 = 1920).</summary>
        public int BinnedWidthPx => SensorWidthPx / Binning;

        public int BinnedHeightPx => SensorHeightPx / Binning;

        public double BinnedPixelSizeUm => PixelSizeUm * Binning;

        /// <summary>Image scale in arcsec per (binned) pixel: 206.265 * pixel(um) * bin / FL(mm).</summary>
        public double PixelScaleArcsec => AngleMath.ArcsecPerRadian * BinnedPixelSizeUm * 1e-3 / FocalLengthMm;

        public double FieldWidthArcmin => BinnedWidthPx * PixelScaleArcsec / 60.0;

        public double FieldHeightArcmin => BinnedHeightPx * PixelScaleArcsec / 60.0;

        public double FieldDiagonalArcmin => Math.Sqrt(FieldWidthArcmin * FieldWidthArcmin + FieldHeightArcmin * FieldHeightArcmin);

        /// <summary>
        /// Distance in binned pixels from the frame centre (the rotation centre on an alt-az mount pointed at
        /// the target) to the farthest point of the frame, the corner: half the frame diagonal,
        /// 0.5 * hypot(1920, 1080) = 1101.45 px at bin 2. It depends on pixel count and binning only, not on
        /// focal length.
        /// </summary>
        public double HalfDiagonalPx => 0.5 * Math.Sqrt((double)BinnedWidthPx * BinnedWidthPx + (double)BinnedHeightPx * BinnedHeightPx);

        public ImagingTrain WithBinning(int binning) {
            return new ImagingTrain(Name, SensorWidthPx, SensorHeightPx, PixelSizeUm, FocalLengthMm, binning);
        }

        public override string ToString() {
            return string.Format(CultureInfo.InvariantCulture, "{0}, bin {1}x{1}: {2}x{3} px, {4:0.000}\"/px, {5:0.00}' x {6:0.00}'",
                Name, Binning, BinnedWidthPx, BinnedHeightPx, PixelScaleArcsec, FieldWidthArcmin, FieldHeightArcmin);
        }
    }
}
