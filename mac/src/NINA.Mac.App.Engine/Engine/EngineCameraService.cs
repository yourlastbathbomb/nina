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
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Exceptions;
using NINA.Equipment.Model;
using NINA.Image.FileFormat;
using NINA.Mac.App.Services;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NinaCameraInfo = NINA.Equipment.Equipment.MyCamera.CameraInfo;

namespace NINA.Mac.App.Engine {

    /// <summary>
    /// The camera through NINA's own CameraVM (HeadlessHost): the ZWO ASI585MC Pro driven by NINA's ASICamera, wrapped in the
    /// profile decorator on connect; exposures through NINA's ImagingVM (metadata as Windows NINA writes it), saved through
    /// NINA's file patterns (the profile carries NINA.Mac.Siril's patterns), measured with NINA.Mac.ImageAnalysis.
    /// Readouts come from CameraVM's own 1 s device poll, never from the SDK on the UI thread. A temperature reading in the first
    /// seconds after a connect is ignored (M1 finding 2), a frozen reading while the cooler power climbs raises
    /// <see cref="HealthWarning"/> (finding 3), and a lost USB link turns the state to Lost.
    /// </summary>
    public sealed class EngineCameraService : ICameraService, IPolledService, IDisposable {

        /// <summary>The ASI585MC Pro's specified TEC delta (35 °C below ambient); the SDK does not report it.</summary>
        public const double Asi585MaxCoolingDelta = 35.0;

        /// <summary>After a cooler command, the device poll may still report the old state for this long.</summary>
        private static readonly TimeSpan CommandSettle = TimeSpan.FromSeconds(2.5);

        private readonly EngineDevices engine;
        private readonly StuckSensorGuard guard;
        private readonly TimeSpan firstReadingDelay;
        private readonly object lockobj = new();
        private DeviceConnectionState state = DeviceConnectionState.Disconnected;
        private string lastError;
        private CameraInfo info;
        private DateTimeOffset connectedAt;
        private bool coolerOn;
        private double targetTemperature;
        private DateTimeOffset coolerCommandedAt = DateTimeOffset.MinValue;
        private bool ownExposure;
        private DateTimeOffset exposureStart;
        private double exposureSeconds;
        private DateTimeOffset? engineExposureStart;
        private double engineExposureSeconds;
        private string healthWarning;
        private DateTimeOffset? disconnectedSince;
        private bool lostByPoll;

        internal EngineCameraService(EngineDevices engine, StuckSensorGuardOptions guardOptions, TimeSpan firstReadingDelay) {
            this.engine = engine;
            guard = new StuckSensorGuard(guardOptions);
            this.firstReadingDelay = firstReadingDelay;
            targetTemperature = engine.Settings.CoolingTargetCelsius;
            reconnectGrace = engine.Options.CameraReconnectGrace;
        }

        private readonly TimeSpan reconnectGrace;

        /// <summary>
        /// Set while a run is active and CameraVM reports the camera disconnected, before the grace window ends (NINA's
        /// ReconnectOnDownloadFailure is probably reconnecting it); null otherwise.
        /// </summary>
        public string ReconnectNote {
            get {
                lock (lockobj) {
                    return state == DeviceConnectionState.Connected && disconnectedSince != null
                        ? "The camera dropped out; NINA is reconnecting it before the next frame"
                        : null;
                }
            }
        }

        public string DisplayName => "Camera";

        public bool IsSimulated => false;

        public event EventHandler Changed;

        public DeviceConnectionState State {
            get {
                lock (lockobj) {
                    return state;
                }
            }
        }

        public string LastError {
            get {
                lock (lockobj) {
                    return lastError;
                }
            }
        }

        public CameraInfo Info {
            get {
                lock (lockobj) {
                    return state == DeviceConnectionState.Connected ? info : null;
                }
            }
        }

        public double? SensorTemperature {
            get {
                var i = Readout();
                if (i == null || engine.Clock.Now - connectedAt < firstReadingDelay || double.IsNaN(i.Temperature)) {
                    return null;
                }
                return Math.Round(i.Temperature, 1);
            }
        }

        public double? CoolerPowerPercent {
            get {
                var i = Readout();
                if (i == null || double.IsNaN(i.CoolerPower)) {
                    return null;
                }
                return CoolerOn ? Math.Round(i.CoolerPower) : 0;
            }
        }

        public bool CoolerOn {
            get {
                lock (lockobj) {
                    return state == DeviceConnectionState.Connected && coolerOn;
                }
            }
        }

        public double TargetTemperature {
            get {
                lock (lockobj) {
                    return targetTemperature;
                }
            }
        }

        public bool IsExposing {
            get {
                lock (lockobj) {
                    return ownExposure || engineExposureStart != null;
                }
            }
        }

        public double ExposureProgress {
            get {
                lock (lockobj) {
                    var now = engine.Clock.Now;
                    if (ownExposure) {
                        return exposureSeconds <= 0 ? 1 : Math.Clamp((now - exposureStart).TotalSeconds / exposureSeconds, 0, 1);
                    }
                    if (engineExposureStart is { } start) {
                        return engineExposureSeconds <= 0 ? 1 : Math.Clamp((now - start).TotalSeconds / engineExposureSeconds, 0, 1);
                    }
                    return 0;
                }
            }
        }

        public string HealthWarning {
            get {
                lock (lockobj) {
                    return state == DeviceConnectionState.Connected ? healthWarning : null;
                }
            }
        }

        public async Task ConnectAsync(CancellationToken ct = default) {
            lock (lockobj) {
                if (state == DeviceConnectionState.Connected || state == DeviceConnectionState.Connecting) {
                    return;
                }
                state = DeviceConnectionState.Connecting;
                lastError = null;
                healthWarning = null;
            }
            RaiseChanged();
            var since = DateTime.UtcNow;
            try {
                // Only now may the camera list touch USB (see GatedDeviceChooser)
                engine.CameraGate.Open();
                await engine.Host.Camera.Rescan();
                ct.ThrowIfCancellationRequested();
                var device = engine.CameraGate.SelectedDevice;
                if (device == null || device is DummyDevice || device is OfflineDevice) {
                    Fail("No ZWO camera found on USB. Check the cable (USB 3, directly on the Mac) and the 12 V supply, then connect again.");
                }
                // NINA's ASICamera.Connect switches mono-bin on when the profile says so; a focus frame cut short by a USB drop
                // can leave the profile saying so (the switch-off write failed). Lights must come back in colour.
                ClearMonoBinInProfile();
                var ok = await engine.Host.Camera.Connect();
                ct.ThrowIfCancellationRequested();
                if (!ok) {
                    Fail(EngineRuntime.LastProblemSince(since) ?? "The camera did not connect (see the log in the engine data folder).");
                }
                var nina = engine.Host.Camera.CameraInfo;
                lock (lockobj) {
                    info = ToInfo(nina);
                    connectedAt = engine.Clock.Now;
                    disconnectedSince = null;
                    lostByPoll = false;
                    coolerOn = nina.CoolerOn;
                    if (!double.IsNaN(nina.TemperatureSetPoint) && nina.CoolerOn) {
                        targetTemperature = nina.TemperatureSetPoint;
                    }
                    engineExposureStart = null;
                    guard.Reset();
                    healthWarning = null;
                    state = DeviceConnectionState.Connected;
                }
                Logger.Info($"Nightglass: camera connected ({info.Model}, {info.Width} x {info.Height}, {info.BayerPattern})");
            } catch (OperationCanceledException) {
                await DisconnectQuietly();
                SetState(DeviceConnectionState.Disconnected, null);
                throw;
            } catch (InvalidOperationException ex) {
                await DisconnectQuietly();
                SetState(DeviceConnectionState.Disconnected, ex.Message);
                throw;
            } catch (Exception ex) {
                Logger.Error("Nightglass: camera connect failed", ex);
                await DisconnectQuietly();
                SetState(DeviceConnectionState.Disconnected, ex.Message);
                throw new InvalidOperationException($"Camera: {ex.Message}", ex);
            }
            RaiseChanged();
        }

        public async Task DisconnectAsync() {
            lock (lockobj) {
                if (state == DeviceConnectionState.Disconnected) {
                    return;
                }
            }
            // NINA's ASICamera.Disconnect only closes the camera: switch the cooler off first (host contract item 7)
            SwitchCoolerOff();
            try {
                await engine.Host.Camera.Disconnect();
            } catch (Exception ex) {
                Logger.Error("Nightglass: camera disconnect failed", ex);
            }
            engine.CameraGate.Close();
            lock (lockobj) {
                coolerOn = false;
                engineExposureStart = null;
                guard.Reset();
                healthWarning = null;
                disconnectedSince = null;
                lostByPoll = false;
            }
            SetState(DeviceConnectionState.Disconnected, null);
        }

        public void SetCooler(bool on, double targetCelsius) {
            EnsureConnected();
            var vm = engine.Host.Camera;
            vm.SetTemperature(targetCelsius);
            vm.SetCooler(on);
            lock (lockobj) {
                coolerOn = on;
                targetTemperature = targetCelsius;
                coolerCommandedAt = engine.Clock.Now;
                guard.Reset();
                healthWarning = null;
            }
            RaiseChanged();
        }

        public async Task<FrameResult> ExposeAsync(ExposureRequest request, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Seconds < 0) {
                throw new ArgumentOutOfRangeException(nameof(request), "Exposure must be >= 0");
            }
            lock (lockobj) {
                EnsureConnectedLocked();
                // A run owns the camera from its start (RunAsync waits for a frame of ours that is still going)
                if (engine.Session.OwnsCamera) {
                    throw new InvalidOperationException("A run is using the camera; pause or stop it first");
                }
                if (ownExposure || engineExposureStart != null) {
                    throw new InvalidOperationException("Camera is already exposing");
                }
                ownExposure = true;
                exposureStart = engine.Clock.Now;
                exposureSeconds = request.Seconds;
            }
            RaiseChanged();
            var device = engine.Host.Camera.GetDevice();
            var monoBin = false;
            try {
                if (request.MonoBin && request.Bin > 1) {
                    monoBin = ZwoMonoBin.TrySet(device, true);
                }
                var sequence = new CaptureSequence(request.Seconds, ImageType(request.Type), null, new BinningMode((short)request.Bin, (short)request.Bin), 1) {
                    Gain = request.Gain,
                    Offset = request.Offset,
                };
                var data = await engine.Host.ImagingMediator.CaptureImage(sequence, ct, new Progress<ApplicationStatus>(), request.TargetName ?? string.Empty);
                if (monoBin) {
                    // CaptureImage has released NINA's imaging lock: mono-bin goes off before anyone else can take the camera
                    monoBin = false;
                    MonoBinOff(device);
                }
                if (data == null) {
                    throw new CameraDownloadFailedException(sequence);
                }
                var image = await data.ToImageData(null, ct);
                var props = image.Properties;
                var detect = request.Type is FrameType.Light or FrameType.Snapshot;
                var profile = engine.Profile.ActiveProfile;
                var pixelSize = profile.CameraSettings.PixelSize;
                var focalLength = profile.TelescopeSettings.FocalLength;
                var measurement = await Task.Run(() => FrameAnalysisService.Measure(image.Data.FlatArray, props.Width, props.Height, props.BitDepth, props.IsBayered,
                    detect, request.Type == FrameType.Snapshot, pixelSize, focalLength, ct), ct);
                string path = null;
                if (request.Keep && request.Type != FrameType.Snapshot) {
                    var files = profile.ImageFileSettings;
                    path = await image.SaveToDisk(new FileSaveInfo(engine.Profile) { FilePattern = files.GetFilePattern(sequence.ImageType) }, ct);
                }
                var temperature = image.MetaData.Camera.Temperature;
                return new FrameResult(request.Type, request.Seconds, request.Gain, request.Bin, double.IsNaN(temperature) ? null : Math.Round(temperature, 1),
                    measurement.Hfr, measurement.Stars, measurement.MeanFraction, measurement.BahtinovOffsetPixels, engine.Clock.Now) {
                    FilePath = path
                };
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) when (ex is CameraConnectionLostException || ex is CameraDownloadFailedException) {
                MarkLost($"Camera stopped delivering frames ({ex.Message}). Reconnect it.");
                throw new DeviceLostException(LastError);
            } catch (Exception ex) when (ex is not DeviceLostException && ex is not InvalidOperationException && ex is not ArgumentException) {
                Logger.Error("Nightglass: exposure failed", ex);
                throw new InvalidOperationException($"Exposure failed: {ex.Message}", ex);
            } finally {
                if (monoBin) {
                    // Lights must keep their Bayer pattern: mono-bin is only ever on for this one frame
                    MonoBinOff(device);
                }
                lock (lockobj) {
                    ownExposure = false;
                }
                RaiseChanged();
            }
        }

        /// <summary>
        /// The start of a run: waits for a frame of this service that is still going (a focus loop left running), then makes sure
        /// ZWO mono-bin is off on the camera and in the profile, so the run's lights keep their Bayer pattern.
        /// </summary>
        internal async Task PrepareForRunAsync(TimeSpan timeout, CancellationToken ct) {
            // A long calibration dark is waited for: its remaining time plus a download margin, at least the given timeout
            var left = OwnExposureRemaining ?? TimeSpan.Zero;
            var deadline = DateTime.UtcNow + (left + DownloadMargin > timeout ? left + DownloadMargin : timeout);
            while (true) {
                lock (lockobj) {
                    if (!ownExposure) {
                        break;
                    }
                }
                if (DateTime.UtcNow > deadline) {
                    throw new InvalidOperationException("The camera is still busy with a focus or calibration frame. Stop it (Focus or Calibrate screen) and start the run again.");
                }
                await Task.Delay(50, ct);
            }
            if (State == DeviceConnectionState.Connected) {
                MonoBinOff(engine.Host.Camera.GetDevice());
            } else {
                ClearMonoBinInProfile();
            }
        }

        /// <summary>How long a frame may take to download and save after its exposure ends (USB 3, bin 2: well under a second normally).</summary>
        internal static readonly TimeSpan DownloadMargin = TimeSpan.FromSeconds(30);

        /// <summary>Time left in a frame this service is taking (focus, snapshot, calibration); null when none is going.</summary>
        internal TimeSpan? OwnExposureRemaining {
            get {
                lock (lockobj) {
                    if (!ownExposure) {
                        return null;
                    }
                    var left = exposureSeconds - (engine.Clock.Now - exposureStart).TotalSeconds;
                    return TimeSpan.FromSeconds(Math.Max(0, left));
                }
            }
        }

        /// <summary>Mono-bin off on the device, and in the profile even when the device write failed (USB gone).</summary>
        private void MonoBinOff(object device) {
            ZwoMonoBin.TrySet(device, false);
            ClearMonoBinInProfile();
        }

        private void ClearMonoBinInProfile() {
            var settings = engine.Profile.ActiveProfile.CameraSettings;
            if (settings.ZwoAsiMonoBinMode == true) {
                settings.ZwoAsiMonoBinMode = false;
            }
        }

        /// <summary>
        /// The app's 1 Hz tick: picks up CameraVM's latest poll, runs the stuck-sensor guard, follows the sequencer's exposures.
        /// A camera CameraVM reports disconnected during a run is given <see cref="EngineDevicesOptions.CameraReconnectGrace"/>
        /// before it is marked Lost, because NINA's ReconnectOnDownloadFailure disconnects and reconnects it between lights; a
        /// camera that comes back after that (a slow reconnect) returns to Connected by itself.
        /// </summary>
        public void Tick() {
            var lost = false;
            var recovered = false;
            lock (lockobj) {
                if (state == DeviceConnectionState.Lost && lostByPoll && engine.CameraGate.IsOpen) {
                    var nina = engine.Host.Camera.CameraInfo;
                    if (nina.Connected) {
                        ResumeAfterEngineReconnectLocked(nina);
                        state = DeviceConnectionState.Connected;
                        lastError = null;
                        recovered = true;
                    }
                } else if (state == DeviceConnectionState.Connected) {
                    var nina = engine.Host.Camera.CameraInfo;
                    var now = engine.Clock.Now;
                    if (!nina.Connected) {
                        if (engine.Session.IsActive) {
                            disconnectedSince ??= now;
                            lost = now - disconnectedSince.Value >= reconnectGrace;
                        } else {
                            lost = true;
                        }
                    } else {
                        if (disconnectedSince != null) {
                            // NINA reconnected it (ReconnectOnDownloadFailure): a fresh SDK session, so the first-reading guard applies again
                            ResumeAfterEngineReconnectLocked(nina);
                            recovered = true;
                        }
                        if (now - coolerCommandedAt > CommandSettle) {
                            coolerOn = nina.CoolerOn;
                            if (nina.CoolerOn && !double.IsNaN(nina.TemperatureSetPoint)) {
                                targetTemperature = nina.TemperatureSetPoint;
                            }
                        }
                        var reading = now - connectedAt < firstReadingDelay ? (double?)null : nina.Temperature;
                        healthWarning = guard.Update(now, reading, coolerOn, nina.CoolerPower);
                        if (!ownExposure) {
                            if (nina.IsExposing && engineExposureStart == null) {
                                engineExposureStart = now;
                                engineExposureSeconds = Math.Max(0, (nina.ExposureEndTime - DateTime.Now).TotalSeconds);
                            } else if (!nina.IsExposing) {
                                engineExposureStart = null;
                            }
                        }
                    }
                }
            }
            if (recovered) {
                Logger.Info("Nightglass: the camera is connected again (reconnected by NINA)");
            }
            if (lost) {
                MarkLost("The camera stopped answering (USB unplugged, or the 12 V supply off?). Reconnect it.", byPoll: true);
                return;
            }
            RaiseChanged();
        }

        private void ResumeAfterEngineReconnectLocked(NinaCameraInfo nina) {
            disconnectedSince = null;
            lostByPoll = false;
            connectedAt = engine.Clock.Now;
            info = ToInfo(nina);
            engineExposureStart = null;
            guard.Reset();
            healthWarning = null;
        }

        public void Dispose() {
        }

        internal void MarkLost(string reason, bool byPoll = false) {
            lock (lockobj) {
                if (state != DeviceConnectionState.Connected) {
                    return;
                }
                state = DeviceConnectionState.Lost;
                lastError = reason;
                lostByPoll = byPoll;
                disconnectedSince = null;
                coolerOn = false;
                ownExposure = false;
                engineExposureStart = null;
                healthWarning = null;
            }
            Logger.Warning($"Nightglass: camera lost: {reason}");
            RaiseChanged();
        }

        private NinaCameraInfo Readout() {
            lock (lockobj) {
                return state == DeviceConnectionState.Connected ? engine.Host.Camera.CameraInfo : null;
            }
        }

        private void SwitchCoolerOff() {
            try {
                var vm = engine.Host.Camera;
                if (vm.CameraInfo.Connected && vm.CameraInfo.CanSetTemperature) {
                    vm.SetCooler(false);
                }
            } catch (Exception ex) {
                Logger.Error("Nightglass: could not switch the cooler off", ex);
            }
        }

        private async Task DisconnectQuietly() {
            SwitchCoolerOff();
            try {
                await engine.Host.Camera.Disconnect();
            } catch (Exception ex) {
                Logger.Error("Nightglass: camera disconnect after a failed connect", ex);
            }
            engine.CameraGate.Close();
        }

        private static void Fail(string message) => throw new InvalidOperationException(message);

        private static CameraInfo ToInfo(NinaCameraInfo nina) {
            var bins = nina.BinningModes?.Select(b => (int)b.X).Distinct().OrderBy(b => b).ToArray();
            var pattern = nina.SensorType switch {
                SensorType.Monochrome => "mono",
                SensorType.RGGB => "RGGB",
                _ => nina.SensorType.ToString(),
            };
            return new CameraInfo(string.IsNullOrWhiteSpace(nina.Name) ? "Camera" : nina.Name, nina.XSize, nina.YSize, nina.PixelSize, pattern,
                bins is { Length: > 0 } ? bins : new[] { 1 }, nina.CanSetTemperature, nina.CanSetTemperature ? Asi585MaxCoolingDelta : 0);
        }

        private static string ImageType(FrameType type) => type switch {
            FrameType.Light => CaptureSequence.ImageTypes.LIGHT,
            FrameType.Dark => CaptureSequence.ImageTypes.DARK,
            FrameType.Flat => CaptureSequence.ImageTypes.FLAT,
            FrameType.Bias => CaptureSequence.ImageTypes.BIAS,
            _ => CaptureSequence.ImageTypes.SNAPSHOT,
        };

        private void EnsureConnected() {
            lock (lockobj) {
                EnsureConnectedLocked();
            }
        }

        private void EnsureConnectedLocked() {
            if (state == DeviceConnectionState.Lost) {
                throw new DeviceLostException(lastError ?? "Camera connection lost");
            }
            if (state != DeviceConnectionState.Connected) {
                throw new InvalidOperationException("Camera is not connected");
            }
        }

        private void SetState(DeviceConnectionState newState, string error) {
            lock (lockobj) {
                state = newState;
                lastError = error;
            }
            RaiseChanged();
        }

        private void RaiseChanged() => engine.Ui.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// ZWO mono-bin through the driver's own properties (NINA's ASICamera exposes HasZwoAsiMonoBinMode and ZwoAsiMonoBinMode,
    /// ASI_MONO_BIN). Found by name, so a camera without them (any other driver, a test double without them) is left alone.
    /// </summary>
    internal static class ZwoMonoBin {

        public static bool TrySet(object device, bool on) {
            if (device == null) {
                return false;
            }
            var type = device.GetType();
            var has = type.GetProperty("HasZwoAsiMonoBinMode", BindingFlags.Public | BindingFlags.Instance);
            var mode = type.GetProperty("ZwoAsiMonoBinMode", BindingFlags.Public | BindingFlags.Instance);
            if (has?.PropertyType != typeof(bool) || mode?.PropertyType != typeof(bool) || !mode.CanWrite) {
                return false;
            }
            try {
                if (!(bool)has.GetValue(device)) {
                    return false;
                }
                mode.SetValue(device, on);
                return true;
            } catch (TargetInvocationException ex) {
                Logger.Warning($"Nightglass: setting ZWO mono-bin to {on} failed: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }
    }
}
