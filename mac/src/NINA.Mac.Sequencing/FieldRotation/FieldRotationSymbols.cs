#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Mac.RigTools.Optics;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Logic;
using System;

namespace NINA.Mac.Sequencing.FieldRotation {

    /// <summary>
    /// Field rotation as NINA expression symbols, for this alt-az mount without a rotator (NINA has none). It registers the
    /// symbol provider "FieldRotation" through NINA's public <see cref="ISymbolBroker.RegisterSymbolProvider"/> and, on every
    /// TelescopeInfo update, publishes:
    /// <list type="bullet">
    /// <item><c>FieldRotation_RateDegPerMin</c>: the signed rate at which the frame turns on the sky, degrees per minute;</item>
    /// <item><c>FieldRotation_MaxSub</c>: the longest exposure, in seconds, that keeps the corner blur within 1 px (decision 4) at
    /// the camera's current binning (capped at <see cref="MaxSubCapSeconds"/>; due east or west the rate is zero and the limit
    /// infinite). The limit is proportional to the allowed blur, so for B px it is <c>FieldRotation_MaxSub * B</c>. This is the
    /// live value for a display: it follows whatever the camera last did, e.g. Center's solve frames at the plate-solve binning;</item>
    /// <item><c>FieldRotation_MaxSubBin1</c>: the same limit for 1 unbinned pixel (bin 1), which does not depend on the camera's
    /// state. The limit is proportional to the binning as well, so for lights at bin b and B binned pixels of blur it is
    /// <c>FieldRotation_MaxSubBin1 * B * b</c>. Saved expressions (the generator's Stop policy) use this one, so their meaning
    /// depends neither on the host's settings nor on the binning of the last frame;</item>
    /// <item><c>FieldRotation_Ok</c>: 1 when <see cref="PlannedExposureSeconds"/> is within the limit for
    /// <see cref="PlannedBlurTolerancePx"/> (or no exposure is planned), else 0.</item>
    /// </list>
    /// The maths is NINA.Mac.RigTools' (Rotation/FieldRotation: rate = -omega cos(lat) cos(Az) / cos(alt), max sub = blur /
    /// (|rate| r) with r the half diagonal of the binned frame), from the mount's reported altitude and azimuth, which on an alt-az
    /// mount is where the camera points, and the profile's latitude. The limit does not depend on focal length. While the mount is
    /// disconnected the values are NaN, so a <c>LoopWhile FieldRotation_MaxSub &gt;= T</c> stops rather than images blind; the
    /// symbols always exist, so such an expression never fails on an undefined symbol.
    /// </summary>
    public sealed class FieldRotationSymbols : ITelescopeConsumer, ICameraConsumer {
        public const string ProviderName = "FieldRotation";
        public const string MaxSubSymbol = ProviderName + "_MaxSub";
        public const string MaxSubBin1Symbol = ProviderName + "_MaxSubBin1";
        public const string RateSymbol = ProviderName + "_RateDegPerMin";
        public const string OkSymbol = ProviderName + "_Ok";

        /// <summary>Published instead of infinity (the rate is zero due east or west): one hour.</summary>
        public const double MaxSubCapSeconds = 3600;

        private readonly IProfileService profileService;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly ISymbolProvider provider;
        private readonly object sync = new object();
        private CameraInfo camera;
        private TelescopeInfo telescope;

        public FieldRotationSymbols(ISymbolBroker symbolBroker, IProfileService profileService, ITelescopeMediator telescopeMediator, ICameraMediator cameraMediator) {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;
            this.cameraMediator = cameraMediator;
            provider = symbolBroker.RegisterSymbolProvider(ProviderName);
            Publish(double.NaN, double.NaN, double.NaN);
            cameraMediator.RegisterConsumer(this);
            telescopeMediator.RegisterConsumer(this);
        }

        /// <summary>The sub length FieldRotation_Ok is judged against; NaN = none (Ok is then 1). Takes effect at the next mount update.</summary>
        public double PlannedExposureSeconds { get; set; } = double.NaN;

        /// <summary>Allowed corner blur in binned pixels for FieldRotation_Ok (decision 4: 1 px). Takes effect at the next mount update.</summary>
        public double PlannedBlurTolerancePx { get; set; } = 1.0;

        /// <summary>The last published values (NaN while unknown).</summary>
        public double MaxSubSeconds { get; private set; } = double.NaN;

        /// <summary>The last published <c>FieldRotation_MaxSubBin1</c> (NaN while unknown).</summary>
        public double MaxSubBin1Seconds { get; private set; } = double.NaN;

        public double RateDegPerMin { get; private set; } = double.NaN;

        public void UpdateDeviceInfo(CameraInfo deviceInfo) {
            lock (sync) {
                camera = deviceInfo;
            }
            Update();
        }

        public void UpdateDeviceInfo(TelescopeInfo deviceInfo) {
            lock (sync) {
                telescope = deviceInfo;
            }
            Update();
        }

        /// <summary>
        /// Max sub, in seconds, for a pointing: the binned frame of a <paramref name="sensorWidthPx"/> x <paramref name="sensorHeightPx"/>
        /// sensor at <paramref name="binning"/>, for <paramref name="blurTolerancePx"/> of corner blur. The published symbol uses the
        /// connected camera's sensor and binning, or the ASI585MC's 3840 x 2160 at bin 1 (the most conservative) while no camera is
        /// connected, and 1 px.
        /// </summary>
        public static double MaxSub(double latitudeDeg, double altitudeDeg, double azimuthDeg, int sensorWidthPx, int sensorHeightPx, int binning, double blurTolerancePx) {
            var train = new ImagingTrain("current", sensorWidthPx, sensorHeightPx, 2.9, ImagingTrain.NativeFocalLengthMm, Math.Max(1, binning));
            var rate = RigTools.Rotation.FieldRotation.RateRadPerSec(latitudeDeg, altitudeDeg, azimuthDeg);
            return Math.Min(MaxSubCapSeconds, RigTools.Rotation.FieldRotation.MaxSubSeconds(rate, train.HalfDiagonalPx, blurTolerancePx));
        }

        private void Update() {
            CameraInfo c;
            TelescopeInfo t;
            lock (sync) {
                c = camera;
                t = telescope;
            }
            if (t == null || !t.Connected || double.IsNaN(t.Altitude) || double.IsNaN(t.Azimuth)) {
                Publish(double.NaN, double.NaN, double.NaN);
                return;
            }
            var latitude = profileService.ActiveProfile.AstrometrySettings.Latitude;
            var width = c != null && c.Connected && c.XSize > 0 ? c.XSize : ImagingTrain.Asi585WidthPx;
            var height = c != null && c.Connected && c.YSize > 0 ? c.YSize : ImagingTrain.Asi585HeightPx;
            var binning = c != null && c.Connected && c.BinX > 0 ? c.BinX : 1;
            var maxSub = MaxSub(latitude, t.Altitude, t.Azimuth, width, height, binning, 1.0);
            var maxSubBin1 = MaxSub(latitude, t.Altitude, t.Azimuth, width, height, 1, 1.0);
            var rate = RigTools.Rotation.FieldRotation.RateDegPerHour(latitude, t.Altitude, t.Azimuth) / 60.0;
            Publish(maxSub, maxSubBin1, rate);
        }

        private void Publish(double maxSub, double maxSubBin1, double rate) {
            MaxSubSeconds = maxSub;
            MaxSubBin1Seconds = maxSubBin1;
            RateDegPerMin = rate;
            var planned = PlannedExposureSeconds;
            var ok = double.IsNaN(planned) || (!double.IsNaN(maxSub) && planned <= maxSub * PlannedBlurTolerancePx) ? 1 : 0;
            provider.AddOrUpdateSymbol("MaxSub", maxSub);
            provider.AddOrUpdateSymbol("MaxSubBin1", maxSubBin1);
            provider.AddOrUpdateSymbol("RateDegPerMin", rate);
            provider.AddOrUpdateSymbol("Ok", ok);
        }

        public void Dispose() {
            telescopeMediator.RemoveConsumer(this);
            cameraMediator.RemoveConsumer(this);
        }
    }
}
