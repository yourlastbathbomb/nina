#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Exceptions;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Equipment.Utility;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine.Test.Fakes {

    /// <summary>
    /// A stand-in for the ZWO ASI585MC Pro behind NINA's ICamera (the real one is never touched by these tests): an RGGB sensor
    /// (smaller than the real 3840 x 2160 so analysis stays quick), bins 1-4, gain 0-600, offset 0-200, a cooler, ZWO mono-bin
    /// under the driver's property names, synthetic star fields whose star size follows <see cref="FocusErrorPixels"/>, and the
    /// failure modes the app must handle: USB gone (<see cref="Unplug"/>), a failing download, and a frozen temperature reading.
    /// </summary>
    internal sealed class FakeCamera : BaseINPC, ICamera {
        public const string DeviceId = "Fake_ZWO_ASI585MC_Pro";

        private readonly IExposureDataFactory exposureDataFactory;
        private readonly IProfileService profileService;
        private readonly object sync = new();
        private DateTime exposureStart;
        private DateTime exposureEnd;
        private CancellationTokenSource exposureCts;
        private double setPoint = 20;
        private bool coolerOn;
        private bool connected;
        private bool monoBin;

        /// <param name="profileService">When given, mono-bin follows NINA's ASICamera: a successful write is stored in the profile's
        /// CameraSettings.ZwoAsiMonoBinMode, and Connect switches mono-bin on when the profile says so.</param>
        public FakeCamera(IExposureDataFactory exposureDataFactory, int sensorWidth = 1920, int sensorHeight = 1080, IProfileService profileService = null) {
            this.exposureDataFactory = exposureDataFactory;
            this.profileService = profileService;
            CameraXSize = sensorWidth;
            CameraYSize = sensorHeight;
            BinningModes = new AsyncObservableCollection<BinningMode>();
            for (short i = 1; i <= 4; i++) {
                BinningModes.Add(new BinningMode(i, i));
            }
        }

        /// <summary>Gaussian sigma added to the stars (pixels at bin 1): 0 is in focus.</summary>
        public double FocusErrorPixels { get; set; }

        /// <summary>When set, frames show a Bahtinov-mask pattern at the centre whose middle spike is off by this many pixels.</summary>
        public double? BahtinovOffsetPixels { get; set; }

        /// <summary>Ambient the sensor sits at with the cooler off.</summary>
        public double AmbientCelsius { get; set; } = 26;

        /// <summary>When set, the temperature reading is stuck at this value (M1 finding 3).</summary>
        public double? FrozenTemperature { get; set; }

        /// <summary>Cooler power reported while the reading is frozen (the test ramps it).</summary>
        public double FrozenCoolerPower { get; set; }

        /// <summary>The next download throws CameraDownloadFailedException.</summary>
        public bool FailNextDownload {
            get => FailNextDownloads > 0;
            set => FailNextDownloads = value ? 1 : 0;
        }

        /// <summary>This many of the next downloads throw CameraDownloadFailedException.</summary>
        public int FailNextDownloads { get; set; }

        public ConcurrentQueue<CaptureSequence> Exposures { get; } = new();

        public ConcurrentQueue<bool> CoolerWrites { get; } = new();

        /// <summary>Mono-bin state at each exposure start, in order.</summary>
        public ConcurrentQueue<bool> MonoBinAtExposure { get; } = new();

        public ConcurrentQueue<bool> MonoBinWrites { get; } = new();

        public int Connects { get; private set; }

        public bool HasSetupDialog => false;
        public string Id => DeviceId;
        public string Name => "ZWO ASI585MC Pro (fake)";
        public string DisplayName => Name;
        public string Category => "Test";

        public bool Connected {
            get {
                lock (sync) {
                    return connected;
                }
            }
        }

        public string Description => "Fake ASI585MC Pro for the Nightglass engine tests";
        public string DriverInfo => "NINA.Mac.App.Engine.Test";
        public string DriverVersion => "1.0";
        public IList<string> SupportedActions => new List<string>();

        public Task<bool> Connect(CancellationToken token) {
            lock (sync) {
                connected = true;
                Connects++;
                // As ASICamera.Connect: the camera comes up in mono-bin when the profile says so (and the SDK keeps no state across a replug)
                monoBin = profileService?.ActiveProfile.CameraSettings.ZwoAsiMonoBinMode == true;
            }
            RaisePropertyChanged(nameof(Connected));
            return Task.FromResult(true);
        }

        public void Disconnect() {
            lock (sync) {
                connected = false;
            }
            RaisePropertyChanged(nameof(Connected));
        }

        /// <summary>USB gone: the driver reports itself disconnected and every exposure fails.</summary>
        public void Unplug() => Disconnect();

        public void SetupDialog() {
        }

        public string Action(string actionName, string actionParameters) => throw new NotSupportedException();
        public string SendCommandString(string command, bool raw = true) => throw new NotSupportedException();
        public bool SendCommandBool(string command, bool raw = true) => throw new NotSupportedException();
        public void SendCommandBlind(string command, bool raw = true) => throw new NotSupportedException();

        public bool HasShutter => false;

        public double Temperature {
            get {
                lock (sync) {
                    if (FrozenTemperature is double frozen) {
                        return frozen;
                    }
                    return coolerOn ? setPoint : AmbientCelsius;
                }
            }
        }

        public double TemperatureSetPoint {
            get {
                lock (sync) {
                    return setPoint;
                }
            }
            set {
                lock (sync) {
                    setPoint = Math.Round(value);
                }
            }
        }

        public short BinX { get; set; } = 1;
        public short BinY { get; set; } = 1;
        public string SensorName => "IMX585";
        public SensorType SensorType => SensorType.RGGB;
        public short BayerOffsetX => 0;
        public short BayerOffsetY => 0;
        public int CameraXSize { get; }
        public int CameraYSize { get; }
        public double ExposureMin => 0.000032;
        public double ExposureMax => 2000;
        public short MaxBinX => 4;
        public short MaxBinY => 4;
        public double PixelSizeX => 2.9;
        public double PixelSizeY => 2.9;
        public bool CanSetTemperature => true;

        public bool CoolerOn {
            get {
                lock (sync) {
                    return coolerOn;
                }
            }
            set {
                lock (sync) {
                    coolerOn = value;
                }
                CoolerWrites.Enqueue(value);
            }
        }

        public double CoolerPower {
            get {
                lock (sync) {
                    if (FrozenTemperature != null) {
                        return FrozenCoolerPower;
                    }
                    return coolerOn ? 41 : 0;
                }
            }
        }

        public bool HasDewHeater => true;
        public bool DewHeaterOn { get; set; }
        public CameraStates CameraState { get; private set; } = CameraStates.Idle;
        public bool CanSubSample => false;
        public bool EnableSubSample { get; set; }
        public int SubSampleX { get; set; }
        public int SubSampleY { get; set; }
        public int SubSampleWidth { get; set; }
        public int SubSampleHeight { get; set; }
        public bool CanShowLiveView => false;
        public bool LiveViewEnabled => false;
        public bool HasBattery => false;
        public int BatteryLevel => -1;
        public int BitDepth => 16;
        public bool CanSetOffset => true;
        public int Offset { get; set; } = 3;
        public int OffsetMin => 0;
        public int OffsetMax => 200;
        public bool CanSetUSBLimit => true;
        public int USBLimit { get; set; } = 40;
        public int USBLimitMin => 40;
        public int USBLimitMax => 100;
        public int USBLimitStep => 1;
        public bool CanGetGain => true;
        public bool CanSetGain => true;
        public int GainMax => 600;
        public int GainMin => 0;
        public int Gain { get; set; } = 200;
        public double ElectronsPerADU => 0.24;
        public IList<string> ReadoutModes => new List<string> { "Default" };
        public short ReadoutMode { get; set; }
        public short ReadoutModeForSnapImages { get; set; }
        public short ReadoutModeForNormalImages { get; set; }
        public IList<int> Gains => new List<int>();
        public AsyncObservableCollection<BinningMode> BinningModes { get; }

        // ZWO mono-bin, under the property names of NINA's ASICamera, with its setter's behaviour: the SDK write fails while the
        // camera is gone (nothing changes), and a successful one is stored in the profile
        public bool HasZwoAsiMonoBinMode => true;

        public bool ZwoAsiMonoBinMode {
            get {
                lock (sync) {
                    return monoBin;
                }
            }
            set {
                lock (sync) {
                    if (!connected) {
                        return;
                    }
                    monoBin = value;
                }
                MonoBinWrites.Enqueue(value);
                if (profileService != null) {
                    profileService.ActiveProfile.CameraSettings.ZwoAsiMonoBinMode = value;
                }
            }
        }

        public void SetBinning(short x, short y) {
            BinX = x;
            BinY = y;
        }

        public void StartExposure(CaptureSequence sequence) {
            if (!Connected) {
                throw new CameraConnectionLostException();
            }
            lock (sync) {
                exposureStart = DateTime.UtcNow;
                exposureEnd = exposureStart.AddSeconds(sequence.ExposureTime);
                exposureCts?.Dispose();
                exposureCts = new CancellationTokenSource();
                CameraState = CameraStates.Exposing;
            }
            Exposures.Enqueue(sequence);
            MonoBinAtExposure.Enqueue(ZwoAsiMonoBinMode);
        }

        public async Task WaitUntilExposureIsReady(CancellationToken token) {
            TimeSpan remaining;
            CancellationToken abort;
            lock (sync) {
                remaining = exposureEnd - DateTime.UtcNow;
                abort = exposureCts?.Token ?? CancellationToken.None;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, abort);
            if (remaining > TimeSpan.Zero) {
                await Task.Delay(remaining, linked.Token);
            }
        }

        public void StopExposure() => AbortExposure();

        public void AbortExposure() {
            lock (sync) {
                exposureCts?.Cancel();
                CameraState = CameraStates.Idle;
            }
        }

        public Task<IExposureData> DownloadExposure(CancellationToken token) {
            return Task.Run<IExposureData>(() => {
                if (!Connected) {
                    throw new CameraConnectionLostException();
                }
                if (FailNextDownloads > 0) {
                    FailNextDownloads--;
                    throw new CameraDownloadFailedException("Fake download failure");
                }
                DateTime start, end;
                bool mono;
                lock (sync) {
                    start = exposureStart;
                    end = exposureEnd;
                    mono = monoBin && BinX > 1;
                    CameraState = CameraStates.Download;
                }
                var width = CameraXSize / BinX;
                var height = CameraYSize / BinY;
                var frame = BahtinovOffsetPixels is double offset
                    ? SyntheticSky.Bahtinov(width, height, 62, 17, offset, Exposures.Count)
                    : SyntheticSky.StarField(width, height, BinX, FocusErrorPixels, Exposures.Count);
                var metaData = new ImageMetaData();
                metaData.FromCamera(this);
                metaData.Image.SetExposureTimes(start, end);
                if (mono) {
                    // As ASICamera.DownloadExposure does in mono-bin mode
                    metaData.Camera.BayerPattern = BayerPatternEnum.None;
                }
                lock (sync) {
                    CameraState = CameraStates.Idle;
                }
                return exposureDataFactory.CreateImageArrayExposureData(frame, width, height, BitDepth, !mono, metaData);
            }, token);
        }

        public void StartLiveView(CaptureSequence sequence) => throw new NotSupportedException();
        public Task<IExposureData> DownloadLiveView(CancellationToken token) => throw new NotSupportedException();

        public void StopLiveView() {
        }

        public void UpdateSubSampleArea() {
        }
    }

    /// <summary>Synthetic frames: a noisy background with Gaussian stars at fixed positions.</summary>
    internal static class SyntheticSky {

        public const int StarCount = 40;

        public static ushort[] StarField(int width, int height, int bin, double focusErrorPixels, int seed) {
            var frame = new ushort[width * height];
            var noise = new Random(seed);
            for (var i = 0; i < frame.Length; i++) {
                frame[i] = (ushort)(1000 + noise.Next(0, 40));
            }
            var positions = new Random(585);
            var sigma = (1.6 + Math.Max(0, focusErrorPixels)) / Math.Max(1, bin);
            sigma = Math.Max(sigma, 1.2);
            var radius = (int)Math.Ceiling(sigma * 5);
            for (var s = 0; s < StarCount; s++) {
                var cx = 30 + (positions.NextDouble() * (width - 60));
                var cy = 30 + (positions.NextDouble() * (height - 60));
                var peak = 12000 + (positions.NextDouble() * 30000);
                for (var y = (int)cy - radius; y <= (int)cy + radius; y++) {
                    for (var x = (int)cx - radius; x <= (int)cx + radius; x++) {
                        if (x < 0 || y < 0 || x >= width || y >= height) {
                            continue;
                        }
                        var d2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                        var value = frame[(y * width) + x] + (peak * Math.Exp(-d2 / (2 * sigma * sigma)));
                        frame[(y * width) + x] = (ushort)Math.Min(65535, value);
                    }
                }
            }
            return frame;
        }

        /// <summary>
        /// A Bahtinov-mask pattern at the frame centre (the model of NINA.Mac.ImageAnalysis.Test's BahtinovPattern, scaled to
        /// 16 bits): a star core, two outer spikes at the centre angle ± <paramref name="outerHalfAngleDeg"/> through the star and
        /// a central spike displaced by -offset along its normal, so the analyser's signed offset is +<paramref name="offsetPx"/>.
        /// </summary>
        public static ushort[] Bahtinov(int width, int height, double centerAngleDeg, double outerHalfAngleDeg, double offsetPx, int seed) {
            var frame = new ushort[width * height];
            var rng = new Random(seed);
            double cx = (width / 2.0) + 0.4, cy = (height / 2.0) - 0.4;
            double a0 = centerAngleDeg * Math.PI / 180;
            double nx = -Math.Sin(a0), ny = Math.Cos(a0);
            var spikes = new[] {
                (angle: a0, px: cx - (offsetPx * nx), py: cy - (offsetPx * ny)),
                (angle: a0 - (outerHalfAngleDeg * Math.PI / 180), px: cx, py: cy),
                (angle: a0 + (outerHalfAngleDeg * Math.PI / 180), px: cx, py: cy),
            };
            const double sigma = 2.2, peak = 170, background = 28, noise = 6, core = 4;
            var length = 200 * 0.9;
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    double v = background;
                    if (Math.Abs(x - cx) < 150 && Math.Abs(y - cy) < 150) {
                        foreach (var sp in spikes) {
                            double dx = x - sp.px, dy = y - sp.py;
                            var along = (dx * Math.Cos(sp.angle)) + (dy * Math.Sin(sp.angle));
                            var across = (-dx * Math.Sin(sp.angle)) + (dy * Math.Cos(sp.angle));
                            v += peak * Math.Exp(-(across * across) / (2 * sigma * sigma)) / (1.0 + (Math.Abs(along) / length));
                        }
                        double rx = x - cx, ry = y - cy;
                        v += 400 * Math.Exp(-((rx * rx) + (ry * ry)) / (2 * core * core));
                    }
                    var u1 = 1.0 - rng.NextDouble();
                    v += noise * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * rng.NextDouble());
                    frame[(y * width) + x] = (ushort)Math.Clamp(Math.Round(v * 150), 0, 65535);
                }
            }
            return frame;
        }
    }
}
