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
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Equipment.Utility;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using System.Collections.Concurrent;

namespace NINA.Mac.Sequencer.Test.Sim {

    /// <summary>
    /// A simulated ZWO ASI585MC Pro behind NINA's ICamera: 3840 x 2160 RGGB, 2.9 um pixels, bins 1-4 (the frame shrinks with the
    /// bin, as ZWO's does), gain 0-600, offset 0-200, a cooler whose sensor reaches the set point at once (so NINA's
    /// RegulateTemperature finishes after one 5 s check), a dew heater and a USB limit. Exposures take their exposure time in real
    /// time and download a synthetic 16-bit frame through NINA's ExposureDataFactory with the metadata ASICamera.DownloadExposure
    /// fills (FromCamera, exposure times). Every exposure, abort and cooler change is recorded.
    /// </summary>
    internal sealed class SimCamera : BaseINPC, ICamera {
        private readonly IExposureDataFactory exposureDataFactory;
        private readonly object sync = new object();
        private CaptureSequence? current;
        private DateTime exposureStart;
        private DateTime exposureEnd;
        private CancellationTokenSource? exposureCts;
        private double setPoint = 20;
        private bool coolerOn;

        public SimCamera(IExposureDataFactory exposureDataFactory, int sensorWidth = 3840, int sensorHeight = 2160) {
            this.exposureDataFactory = exposureDataFactory;
            CameraXSize = sensorWidth;
            CameraYSize = sensorHeight;
            BinningModes = new AsyncObservableCollection<BinningMode>();
            for (short i = 1; i <= 4; i++) {
                BinningModes.Add(new BinningMode(i, i));
            }
        }

        public const string DeviceId = "Sim_ZWO_ASI585MC_Pro";

        /// <summary>Exposures started, in order.</summary>
        public ConcurrentQueue<CaptureSequence> Exposures { get; } = new();

        /// <summary>Every exposure: its image type, exposure time and local start time, in order.</summary>
        public ConcurrentQueue<(string ImageType, double Seconds, DateTime Start)> ExposureLog { get; } = new();

        public int Aborts { get; private set; }

        /// <summary>Every cooler on/off write, in order (true = on).</summary>
        public ConcurrentQueue<bool> CoolerWrites { get; } = new();

        /// <summary>Called with each started exposure and its start time (e.g. to record where the mount pointed).</summary>
        public Action<CaptureSequence, DateTime>? ExposureStarted { get; set; }

        public bool HasSetupDialog => false;
        public string Id => DeviceId;
        public string Name => "Simulated ZWO ASI585MC Pro";
        public string DisplayName => Name;
        public string Category => "Simulator";
        public bool Connected { get; private set; }
        public string Description => "Simulated ASI585MC Pro for the headless sequencer tests";
        public string DriverInfo => "NINA.Mac.Sequencer.Test";
        public string DriverVersion => "1.0";
        public IList<string> SupportedActions => new List<string>();

        public Task<bool> Connect(CancellationToken token) {
            Connected = true;
            RaisePropertyChanged(nameof(Connected));
            return Task.FromResult(true);
        }

        public void Disconnect() {
            Connected = false;
            RaisePropertyChanged(nameof(Connected));
        }

        public void SetupDialog() {
        }

        public string Action(string actionName, string actionParameters) => throw new NotSupportedException();
        public string SendCommandString(string command, bool raw = true) => throw new NotSupportedException();
        public bool SendCommandBool(string command, bool raw = true) => throw new NotSupportedException();
        public void SendCommandBlind(string command, bool raw = true) => throw new NotSupportedException();

        public bool HasShutter => false;
        public double Temperature => coolerOn ? setPoint : 20;

        public double TemperatureSetPoint {
            get => setPoint;
            set => setPoint = value;
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
            get => coolerOn;
            set {
                coolerOn = value;
                CoolerWrites.Enqueue(value);
            }
        }

        public double CoolerPower => coolerOn ? 50 : 0;
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

        public void SetBinning(short x, short y) {
            BinX = x;
            BinY = y;
        }

        public void StartExposure(CaptureSequence sequence) {
            lock (sync) {
                current = sequence;
                exposureStart = DateTime.UtcNow;
                exposureEnd = exposureStart.AddSeconds(sequence.ExposureTime);
                exposureCts?.Dispose();
                exposureCts = new CancellationTokenSource();
                CameraState = CameraStates.Exposing;
            }
            Exposures.Enqueue(sequence);
            ExposureLog.Enqueue((sequence.ImageType, sequence.ExposureTime, DateTime.Now));
            ExposureStarted?.Invoke(sequence, exposureStart);
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

        public void StopExposure() {
            AbortExposure();
        }

        public void AbortExposure() {
            lock (sync) {
                exposureCts?.Cancel();
                CameraState = CameraStates.Idle;
            }
            Aborts++;
        }

        public Task<IExposureData> DownloadExposure(CancellationToken token) {
            return Task.Run<IExposureData>(() => {
                DateTime start, end;
                lock (sync) {
                    start = exposureStart;
                    end = exposureEnd;
                    CameraState = CameraStates.Download;
                }
                var width = CameraXSize / BinX;
                var height = CameraYSize / BinY;
                var frame = new ushort[width * height];
                var random = new Random(Exposures.Count);
                for (var i = 0; i < frame.Length; i++) {
                    // Bayer-ish background with a little noise
                    var x = i % width;
                    var y = i / width;
                    frame[i] = (ushort)(1000 + ((x & 1) == 0 && (y & 1) == 0 ? 200 : 0) + random.Next(0, 64));
                }
                var metaData = new ImageMetaData();
                metaData.FromCamera(this);
                metaData.Image.SetExposureTimes(start, end);
                lock (sync) {
                    CameraState = CameraStates.Idle;
                }
                return exposureDataFactory.CreateImageArrayExposureData(frame, width, height, BitDepth, true, metaData);
            }, token);
        }

        public void StartLiveView(CaptureSequence sequence) => throw new NotSupportedException();
        public Task<IExposureData> DownloadLiveView(CancellationToken token) => throw new NotSupportedException();
        public void StopLiveView() {
        }

        public void UpdateSubSampleArea() {
        }
    }
}
