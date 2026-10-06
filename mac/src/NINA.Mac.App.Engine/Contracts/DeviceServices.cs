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
    /// Common shape of the device services. The view-models only talk to these interfaces. They are backed either by the
    /// simulators (NINA.Mac.App, Services/Simulation) or by the headless engine (NINA.Mac.App.Engine: NINA's ASICamera, the
    /// native LX200 driver, NINA's sequencer); Settings › Devices picks one at start-up.
    /// Events are raised on the UI thread in the app: the simulators raise them on the thread that caused the change (the UI
    /// thread), the engine services post them to the synchronization context they were created on.
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

    public sealed record ExposureRequest(FrameType Type, double Seconds, int Gain, int Offset, int Bin) {

        /// <summary>
        /// Save the frame (Real devices: through NINA's file patterns into the Siril layout). Lights, darks, flats and biases
        /// are saved unless this is false (e.g. the trial frames of the flat auto-exposure); snapshots are never saved.
        /// </summary>
        public bool Keep { get; init; } = true;

        /// <summary>ZWO mono-bin (the colour camera sums its Bayer cells into one grey pixel at bin 2+): brighter, faster focus frames.</summary>
        public bool MonoBin { get; init; }

        /// <summary>Target name for the FITS OBJECT card and the lights folder; null for calibration frames and snapshots.</summary>
        public string TargetName { get; init; }
    }

    /// <summary>Result of one exposure, with the statistics the screens show. No pixels yet (engine M3/M5).</summary>
    public sealed record FrameResult(FrameType Type, double Seconds, int Gain, int Bin, double? SensorTemperature, double Hfr, int Stars, double MeanAduFraction, double? BahtinovOffsetPixels, DateTimeOffset Completed) {

        /// <summary>Where the frame was saved; null when it was not saved (snapshots, simulators).</summary>
        public string FilePath { get; init; }
    }

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

        /// <summary>
        /// A warning about the camera's health while it is connected, or null. The engine camera sets it when the sensor
        /// temperature stays frozen while the cooler power climbs (mac/docs/m1-camera-results.md finding 3: after a USB glitch
        /// the SDK can report a constant temperature); the banner then offers a reconnect.
        /// </summary>
        string HealthWarning { get; }

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

        /// <summary>
        /// A goto, soft park or slew-and-centre started through the app has not returned yet. The status bar shows its Stop from
        /// this as well as from the polled <see cref="IsSlewing"/>, which a short goto can finish between two polls.
        /// </summary>
        bool IsMotionCommandActive { get; }

        /// <summary>Slews to J2000 coordinates. Refuses targets below the horizon (and the local horizon) or above the keyhole limit (slew guard).</summary>
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

    /// <summary>
    /// What the plan check found before a run (Real devices: NINA.Mac.Sequencing's PlanValidator over tonight's sky with the
    /// profile's horizon): warnings such as "Holds the sequence until dawn" or "Never reached", errors that stop the run, and
    /// a one-line summary of the target's window tonight.
    /// </summary>
    public sealed record PlanCheck(IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, string Summary) {
        public static PlanCheck Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>(), null);

        public bool HasErrors => Errors.Count > 0;

        public bool HasWarnings => Warnings.Count > 0;
    }

    /// <summary>The imaging run: expose, dither, stop at limits (simulated, or NINA's sequencer through the headless engine).</summary>
    public interface ISessionService {

        SessionState State { get; }

        SessionPlan Plan { get; }

        SessionProgress Progress { get; }

        /// <summary>Folders of the running (or last) session; null before the first run.</summary>
        IImageFolders Layout { get; }

        IReadOnlyList<string> Log { get; }

        event EventHandler Changed;

        /// <summary>Checks a plan against tonight's sky without running it (shown on the Run screen before Start).</summary>
        PlanCheck CheckPlan(SessionPlan plan);

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

    /// <summary>
    /// Where a night's frames go. The simulators use <see cref="SessionLayout"/>; the engine uses NINA.Mac.Siril's layout,
    /// which is what NINA's file patterns write with Real devices.
    /// </summary>
    public interface IImageFolders {

        string NightDirectory { get; }

        string FlatsDirectory { get; }

        string DarksDirectory { get; }

        string BiasesDirectory { get; }

        string LightsDirectory(string target);

        /// <summary>Siril command line that would stack a target from this night (shown, not run).</summary>
        string SirilCommand(string target);
    }

    public sealed record CentringResult(bool Centred, double? ErrorArcmin, int Solves, string Message);

    /// <summary>Goto plus plate-solve centring (Target screen). The simulators only slew.</summary>
    public interface ICentringService {

        /// <summary>True when a solver is configured (Real devices); false means "slew only".</summary>
        bool CanPlateSolve { get; }

        /// <summary>Slews to J2000 coordinates and, when <see cref="CanPlateSolve"/>, centres with solve, sync and re-slew.</summary>
        Task<CentringResult> SlewAndCentreAsync(double rightAscensionHours, double declinationDegrees, IProgress<string> progress = null, CancellationToken ct = default);
    }

    /// <summary>A service whose readouts the app refreshes once a second (AppServices.Tick).</summary>
    public interface IPolledService {

        void Tick();
    }
}
