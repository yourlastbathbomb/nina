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
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using ZWOptical.ASISDK;
using static ZWOptical.ASISDK.ASICameraDll;

namespace NINA.Mac.ZwoProbe {

    public sealed record CaptureSettings(double ExposureSeconds, int Bin, int? Gain, int? Offset, int UsbLimit, bool MonoBin, bool Dark);

    public sealed record CaptureResult(ushort[] Data, int Width, int Height, DateTime StartUtc, TimeSpan ExposureWait, TimeSpan Download, ASI_EXPOSURE_STATUS FinalStatus, bool Stalled);

    /// <summary>Drives one ZWO camera the same way NINA's ASICamera does (open, init, RAW16 ROI, poll, download).</summary>
    public sealed class ProbeCamera : IDisposable {
        public ASI_CAMERA_INFO Info { get; }
        public int Id => Info.CameraID;
        public Dictionary<ASI_CONTROL_TYPE, ASI_CONTROL_CAPS> Controls { get; } = new();
        public bool CoolerEnabledByProbe { get; private set; }
        public bool LeaveCoolerOn { get; set; }

        private ProbeCamera(ASI_CAMERA_INFO info) {
            Info = info;
        }

        public static ProbeCamera Open(int index) {
            var count = GetNumOfConnectedCameras();
            if (count == 0) {
                throw new InvalidOperationException("No ZWO camera connected (check USB and 12 V power for cooling).");
            }
            if (index >= count) {
                throw new ArgumentOutOfRangeException(nameof(index), $"Camera index {index} requested but only {count} connected");
            }
            var cam = new ProbeCamera(GetCameraProperties(index));
            OpenCamera(cam.Id);
            InitCamera(cam.Id);
            for (int i = 0, n = GetNumOfControls(cam.Id); i < n; i++) {
                var caps = GetControlCaps(cam.Id, i);
                cam.Controls[caps.ControlType] = caps;
            }
            cam.ApplyNinaDefaults();
            return cam;
        }

        // Same fixed settings as ASICamera.Initialize()
        private void ApplyNinaDefaults() {
            TrySet(ASI_CONTROL_TYPE.ASI_FLIP, (int)ASI_FLIP_STATUS.ASI_FLIP_NONE);
            TrySet(ASI_CONTROL_TYPE.ASI_BANDWIDTHOVERLOAD, 40);
            TrySet(ASI_CONTROL_TYPE.ASI_WB_B, 50);
            TrySet(ASI_CONTROL_TYPE.ASI_WB_R, 50);
            TrySet(ASI_CONTROL_TYPE.ASI_GAMMA, 50);
            TrySet(ASI_CONTROL_TYPE.ASI_HIGH_SPEED_MODE, 0);
            TrySet(ASI_CONTROL_TYPE.ASI_HARDWARE_BIN, 0);
            TrySet(ASI_CONTROL_TYPE.ASI_OVERCLOCK, 0);
            TrySet(ASI_CONTROL_TYPE.ASI_PATTERN_ADJUST, 0);
        }

        public bool Has(ASI_CONTROL_TYPE type) => Controls.ContainsKey(type);

        public int Get(ASI_CONTROL_TYPE type) => GetControlValue(Id, type, out _);

        public bool TrySet(ASI_CONTROL_TYPE type, int value) {
            if (!Controls.TryGetValue(type, out var caps) || !caps.IsWritable.Equals(ASI_BOOL.ASI_TRUE) || value < caps.MinValue || value > caps.MaxValue) {
                return false;
            }
            SetControlValue(Id, type, value, false);
            return true;
        }

        public void Set(ASI_CONTROL_TYPE type, int value) {
            if (!TrySet(type, value)) {
                var range = Controls.TryGetValue(type, out var caps) ? $"{caps.MinValue}..{caps.MaxValue}, writable={caps.IsWritable}" : "not supported";
                throw new ArgumentOutOfRangeException(nameof(value), $"{type}={value} rejected ({range})");
            }
        }

        public double TemperatureC => Get(ASI_CONTROL_TYPE.ASI_TEMPERATURE) / 10.0;

        public bool IsColor => Info.IsColorCam == ASI_BOOL.ASI_TRUE;

        public IReadOnlyList<int> SupportedBins => Info.SupportedBins.TakeWhile(b => b != 0).ToList();

        public string BayerPatternName => Info.BayerPattern switch {
            ASI_BAYER_PATTERN.ASI_BAYER_RG => "RGGB",
            ASI_BAYER_PATTERN.ASI_BAYER_BG => "BGGR",
            ASI_BAYER_PATTERN.ASI_BAYER_GR => "GRBG",
            ASI_BAYER_PATTERN.ASI_BAYER_GB => "GBRG",
            _ => "UNKNOWN"
        };

        public void SetCooler(bool on, int targetC) {
            Set(ASI_CONTROL_TYPE.ASI_TARGET_TEMP, targetC);
            Set(ASI_CONTROL_TYPE.ASI_COOLER_ON, on ? 1 : 0);
            CoolerEnabledByProbe = on;
        }

        public CaptureResult Capture(CaptureSettings s, CancellationToken token) {
            if (!SupportedBins.Contains(s.Bin)) {
                throw new ArgumentOutOfRangeException(nameof(s.Bin), $"Bin {s.Bin} not supported; camera offers {string.Join(",", SupportedBins)}");
            }
            if (s.Gain.HasValue) { Set(ASI_CONTROL_TYPE.ASI_GAIN, s.Gain.Value); }
            if (s.Offset.HasValue) { Set(ASI_CONTROL_TYPE.ASI_OFFSET, s.Offset.Value); }
            TrySet(ASI_CONTROL_TYPE.ASI_BANDWIDTHOVERLOAD, s.UsbLimit);
            if (IsColor) { TrySet(ASI_CONTROL_TYPE.ASI_MONO_BIN, s.MonoBin ? 1 : 0); }

            // Same ROI rule as ASICamera.StartExposure: width multiple of 8, height multiple of 2
            var width = (Info.MaxWidth / s.Bin) - (Info.MaxWidth / s.Bin % 8);
            var height = (Info.MaxHeight / s.Bin) - (Info.MaxHeight / s.Bin % 2);
            SetROIFormat(Id, new Size(width, height), s.Bin, ASI_IMG_TYPE.ASI_IMG_RAW16);
            SetStartPos(Id, new Point(0, 0));

            Set(ASI_CONTROL_TYPE.ASI_EXPOSURE, (int)Math.Round(s.ExposureSeconds * 1_000_000));

            var start = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();
            StartExposure(Id, s.Dark);

            // Stall = still WORKING well past the requested exposure plus generous readout slack
            var stallAfter = TimeSpan.FromSeconds(s.ExposureSeconds + 30);
            var status = ASI_EXPOSURE_STATUS.ASI_EXP_WORKING;
            var stalled = false;
            try {
                while ((status = GetExposureStatus(Id)) == ASI_EXPOSURE_STATUS.ASI_EXP_WORKING) {
                    if (sw.Elapsed > stallAfter) {
                        stalled = true;
                        break;
                    }
                    token.WaitHandle.WaitOne(10);
                    token.ThrowIfCancellationRequested();
                }
            } catch (OperationCanceledException) {
                StopExposure(Id);
                throw;
            }
            var exposureWait = sw.Elapsed;

            if (stalled) {
                StopExposure(Id);
                return new CaptureResult(null, width, height, start, exposureWait, TimeSpan.Zero, status, true);
            }
            if (status != ASI_EXPOSURE_STATUS.ASI_EXP_SUCCESS) {
                return new CaptureResult(null, width, height, start, exposureWait, TimeSpan.Zero, status, false);
            }

            sw.Restart();
            var data = new ushort[width * height];
            var ok = GetDataAfterExp(Id, data, width * height * 2);
            var download = sw.Elapsed;
            return new CaptureResult(ok ? data : null, width, height, start, exposureWait, download, status, false);
        }

        public void Dispose() {
            try {
                if (CoolerEnabledByProbe && !LeaveCoolerOn) {
                    TrySet(ASI_CONTROL_TYPE.ASI_COOLER_ON, 0);
                }
            } finally {
                CloseCamera(Id);
            }
        }
    }
}
