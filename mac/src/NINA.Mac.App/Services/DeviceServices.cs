#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.Platform;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Services {

    public enum DeviceConnectionState {
        Disconnected,
        Connecting,
        Connected,

        /// <summary>The device dropped out (USB unplugged, serial timeout). The UI shows a banner and offers reconnect.</summary>
        Lost,
    }

    /// <summary>Thrown by device calls when the connection is lost mid-operation.</summary>
    public sealed class DeviceLostException : Exception {

        public DeviceLostException(string message) : base(message) {
        }
    }

    /// <summary>
    /// Common shape of the device services. The view-models only talk to these interfaces; today they are backed by
    /// simulators, later by the headless engine (NINA's ASICamera + the native LX200 driver, M3/M4).
    /// Events are raised on the thread that caused the change (the UI thread in the app).
    /// </summary>
    public interface IDeviceService {

        string DisplayName { get; }

        DeviceConnectionState State { get; }

        /// <summary>Why the last connect failed or the connection was lost; null when fine.</summary>
        string LastError { get; }

        bool IsSimulated { get; }

        event EventHandler Changed;

        Task ConnectAsync(CancellationToken ct = default);

        Task DisconnectAsync();
    }

    public sealed record CameraInfo(string Model, int Width, int Height, double PixelSizeMicrons, string BayerPattern, IReadOnlyList<int> Bins, bool HasCooler, double MaxCoolingDelta);

    public sealed record ExposureRequest(FrameType Type, double Seconds, int Gain, int Offset, int Bin);

    /// <summary>Result of one exposure, with the statistics the screens show. No pixels yet (engine M3/M5).</summary>
    public sealed record FrameResult(FrameType Type, double Seconds, int Gain, int Bin, double? SensorTemperature, double Hfr, int Stars, double MeanAduFraction, double? BahtinovOffsetPixels, DateTimeOffset Completed);

    public interface ICameraService : IDeviceService {

        /// <summary>Null until connected.</summary>
        CameraInfo Info { get; }

        double? SensorTemperature { get; }

        double? CoolerPowerPercent { get; }

        bool CoolerOn { get; }

        double TargetTemperature { get; }

        bool IsExposing { get; }

        /// <summary>0..1 through the current exposure.</summary>
        double ExposureProgress { get; }

        void SetCooler(bool on, double targetCelsius);

        Task<FrameResult> ExposeAsync(ExposureRequest request, CancellationToken ct = default);
    }

    /// <summary>The LX200GPS in alt-az: no meridian flips and no hard park (soft park only).</summary>
    public interface IMountService : IDeviceService {

        string PortName { get; }

        IReadOnlyList<SerialPortInfo> AvailablePorts { get; }

        void RefreshPorts();

        void SelectPort(string portPath);

        double? RightAscensionHours { get; }

        double? DeclinationDegrees { get; }

        double? Altitude { get; }

        double? Azimuth { get; }

        bool IsSlewing { get; }

        bool IsTracking { get; }

        /// <summary>Slews to J2000 coordinates. Refuses targets below the horizon or above the keyhole limit (slew guard).</summary>
        Task SlewToAsync(double rightAscensionHours, double declinationDegrees, CancellationToken ct = default);

        Task SyncAsync(double rightAscensionHours, double declinationDegrees);

        /// <summary>Mount-only dither (MAC_PORT_PLAN.md: DirectGuider pulses), by up to <paramref name="pixels"/> pixels.</summary>
        Task DitherAsync(double pixels, CancellationToken ct = default);

        void SetTracking(bool on);

        /// <summary>Slew to a safe low southern position and stop tracking. Never a hard park (that would point at Dec +89, the blocked north).</summary>
        Task SoftParkAsync(CancellationToken ct = default);

        void Abort();
    }

    /// <summary>
    /// The #1209 microfocuser driven through the mount: no position readout, only timed moves at speed 1-4.
    /// <see cref="Position"/> is virtual: milliseconds of motor time at the locked speed, starting at MaxStep/2
    /// (MAC_PORT_PLAN.md focuser model).
    /// </summary>
    public interface IFocuserService {

        DeviceConnectionState State { get; }

        int Speed { get; }

        int Position { get; }

        int MaxStep { get; }

        bool IsMoving { get; }

        event EventHandler Changed;

        /// <summary>1 (slowest) to 4. Changing speed recentres the virtual position, because positions are only meaningful at one speed.</summary>
        void SetSpeed(int speed);

        /// <summary>Positive moves out, negative moves in.</summary>
        Task MoveAsync(int milliseconds, CancellationToken ct = default);

        void RecenterVirtualPosition();
    }

    public sealed record SessionPlan(
        string TargetName,
        double RightAscensionHours,
        double DeclinationDegrees,
        double ExposureSeconds,
        int FrameCount,
        int Gain,
        int Offset,
        int Bin,
        int DitherEvery,
        double MaxAltitude,
        double MinAltitude,
        bool StopAtDawn);

    public enum SessionState {
        Idle,
        Running,
        Paused,
        Stopping,
        Finished,
        Failed,
    }

    public sealed record SessionProgress(int FramesDone, int FrameCount, double? LastHfr, string LastFile, string StopReason, TimeSpan Elapsed) {
        public static SessionProgress None { get; } = new(0, 0, null, null, null, TimeSpan.Zero);
    }

    /// <summary>Stand-in for the headless sequencer (M7): expose, dither, stop at limits.</summary>
    public interface ISessionService {

        SessionState State { get; }

        SessionPlan Plan { get; }

        SessionProgress Progress { get; }

        SessionLayout Layout { get; }

        IReadOnlyList<string> Log { get; }

        event EventHandler Changed;

        Task RunAsync(SessionPlan plan, CancellationToken ct = default);

        void Pause();

        void Resume();

        void RequestStop(string reason);
    }

    public sealed record CalibrationPlan(int FlatCount, double FlatTargetFraction, int DarkCount, IReadOnlyList<double> DarkExposures, int BiasCount, int Gain, int Offset, int Bin);

    public interface ICalibrationService {

        bool IsRunning { get; }

        string Status { get; }

        /// <summary>0..1</summary>
        double Progress { get; }

        double? LastFlatExposure { get; }

        event EventHandler Changed;

        Task RunFlatsAsync(CalibrationPlan plan, CancellationToken ct = default);

        Task RunDarksAsync(CalibrationPlan plan, CancellationToken ct = default);

        Task RunBiasesAsync(CalibrationPlan plan, CancellationToken ct = default);
    }
}
