#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Mac.App.Services;
using NINA.Mac.Equipment.Lx200;
using NINA.Mac.Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine {

    /// <summary>
    /// The LX200GPS in alt-az through NINA's TelescopeVM over the native driver (NINA.Mac.Equipment.Lx200): port picker over
    /// /dev/cu.*, gotos and syncs in J2000 (the driver converts to JNow with NINA's transform), soft park (the driver's; never
    /// :hP#), mount-only dithers through NINA's DirectGuider. The #1209 focuser and DirectGuider connect with the mount.
    /// While the driver's serial link reconnects on its own (cable pulled), the state is Lost and the banner says so; the
    /// devices stay connected in NINA, as the driver intends, and the state returns to Connected when the link does. When the
    /// link gives up, NINA disconnects the mount and Reconnect starts afresh.
    /// </summary>
    public sealed class EngineMountService : IMountService, IPolledService, IDisposable {

        /// <summary>Reconnect waits this long for a link that is already reconnecting on its own.</summary>
        public static readonly TimeSpan LinkRecoveryWait = TimeSpan.FromSeconds(10);

        private readonly EngineDevices engine;
        private readonly Func<IReadOnlyList<SerialPortInfo>> portLister;
        private readonly object lockobj = new();
        private DeviceConnectionState state = DeviceConnectionState.Disconnected;
        private string lastError;
        private IReadOnlyList<SerialPortInfo> ports = Array.Empty<SerialPortInfo>();
        private Lx200Link link;
        private bool linkDown;
        private bool focuserConnected;
        private string focuserError;
        private CancellationTokenSource haltCts = new();

        internal EngineMountService(EngineDevices engine, Func<IReadOnlyList<SerialPortInfo>> portLister) {
            this.engine = engine;
            this.portLister = portLister ?? (() => SerialPorts.List());
        }

        public string DisplayName => "Mount";

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

        /// <summary>The configured port (Lx200Settings.PortPath in the rig profile); null = the only USB serial adapter at connect.</summary>
        public string PortName {
            get {
                var path = Telescope.Settings.PortPath;
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
        }

        public IReadOnlyList<SerialPortInfo> AvailablePorts {
            get {
                lock (lockobj) {
                    return ports;
                }
            }
        }

        /// <summary>Whether the #1209 focuser connected with the mount (it shares the serial link).</summary>
        public bool FocuserConnected {
            get {
                lock (lockobj) {
                    return focuserConnected;
                }
            }
        }

        public string FocuserError {
            get {
                lock (lockobj) {
                    return focuserError;
                }
            }
        }

        public Lx200Telescope Telescope => engine.Lx200.Telescope;

        public double? RightAscensionHours => WithInfo(i => J2000(i)?.RA);

        public double? DeclinationDegrees => WithInfo(i => J2000(i)?.Dec);

        public double? Altitude => WithInfo<double>(i => double.IsNaN(i.Altitude) ? null : i.Altitude);

        public double? Azimuth => WithInfo<double>(i => double.IsNaN(i.Azimuth) ? null : i.Azimuth);

        public bool IsSlewing => WithInfo(i => (bool?)i.Slewing) ?? false;

        public bool IsTracking => WithInfo(i => (bool?)i.TrackingEnabled) ?? false;

        public void RefreshPorts() {
            IReadOnlyList<SerialPortInfo> list;
            try {
                list = portLister() ?? Array.Empty<SerialPortInfo>();
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                list = Array.Empty<SerialPortInfo>();
            }
            lock (lockobj) {
                ports = list;
            }
            // Fill an empty choice with the first USB serial adapter; a saved port is never replaced (its adapter may be unplugged)
            if (PortName == null && list.FirstOrDefault(p => p.IsUsbSerial) is { } usb && State == DeviceConnectionState.Disconnected) {
                Telescope.Settings.PortPath = usb.Path;
                SaveProfile();
            }
            RaiseChanged();
        }

        public void SelectPort(string portPath) {
            lock (lockobj) {
                if (state == DeviceConnectionState.Connected || state == DeviceConnectionState.Connecting) {
                    throw new InvalidOperationException("Disconnect before changing the port");
                }
            }
            Telescope.Settings.PortPath = portPath ?? string.Empty;
            SaveProfile();
            RaiseChanged();
        }

        public async Task ConnectAsync(CancellationToken ct = default) {
            Lx200Link reconnecting = null;
            lock (lockobj) {
                if (state == DeviceConnectionState.Connected || state == DeviceConnectionState.Connecting) {
                    return;
                }
                if (state == DeviceConnectionState.Lost && linkDown && link != null && link.State == Lx200LinkState.Reconnecting) {
                    reconnecting = link;
                }
                state = DeviceConnectionState.Connecting;
                lastError = null;
            }
            RaiseChanged();
            if (reconnecting != null) {
                // The driver reopens the port every second by itself; give it a moment before starting over
                if (await reconnecting.WaitUntilConnectedAsync(LinkRecoveryWait, ct)) {
                    lock (lockobj) {
                        linkDown = false;
                        state = DeviceConnectionState.Connected;
                    }
                    RaiseChanged();
                    return;
                }
            }
            var since = DateTime.UtcNow;
            try {
                await DisconnectEngineDevices();
                await engine.Host.Telescope.Rescan();
                ct.ThrowIfCancellationRequested();
                var ok = await engine.Host.Telescope.Connect();
                ct.ThrowIfCancellationRequested();
                if (!ok || !Telescope.Connected) {
                    throw new InvalidOperationException(EngineRuntime.LastProblemSince(since) ?? $"The mount did not connect on {PortName ?? "the USB serial adapter"}.");
                }
                var l = Telescope.Link;
                lock (lockobj) {
                    link = l;
                    linkDown = false;
                }
                if (l != null) {
                    l.StateChanged += OnLinkStateChanged;
                }

                // The #1209 focuser shares the mount's link; a focuser that does not answer leaves the mount usable
                bool focuserOk;
                string focuserProblem = null;
                var focuserSince = DateTime.UtcNow;
                try {
                    focuserOk = await engine.Lx200.Focuser.Connect(ct);
                    if (!focuserOk) {
                        focuserProblem = EngineRuntime.LastProblemSince(focuserSince) ?? "The focuser did not answer";
                    }
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    focuserOk = false;
                    focuserProblem = ex.Message;
                }
                lock (lockobj) {
                    focuserConnected = focuserOk;
                    focuserError = focuserProblem;
                }

                // NINA's DirectGuider ("Mount Dither") dithers by pulse guiding through the telescope mediator
                await engine.Host.Guider.Rescan();
                if (!await engine.Host.Guider.Connect()) {
                    Logger.Warning("Nightglass: the mount dither (DirectGuider) did not connect; runs will not dither");
                }
                lock (lockobj) {
                    state = DeviceConnectionState.Connected;
                }
                Logger.Info($"Nightglass: mount connected on {Telescope.Link?.Name}, firmware {Telescope.Firmware}, focuser {(focuserOk ? "connected" : "not connected: " + focuserProblem)}");
            } catch (OperationCanceledException) {
                await DisconnectEngineDevices();
                SetState(DeviceConnectionState.Disconnected, null);
                throw;
            } catch (InvalidOperationException ex) {
                await DisconnectEngineDevices();
                SetState(DeviceConnectionState.Disconnected, ex.Message);
                throw;
            } catch (Exception ex) {
                Logger.Error("Nightglass: mount connect failed", ex);
                await DisconnectEngineDevices();
                SetState(DeviceConnectionState.Disconnected, ex.Message);
                throw new InvalidOperationException($"Mount: {ex.Message}", ex);
            }
            RaiseChanged();
        }

        public async Task DisconnectAsync() {
            lock (lockobj) {
                if (state == DeviceConnectionState.Disconnected) {
                    return;
                }
            }
            await DisconnectEngineDevices();
            SetState(DeviceConnectionState.Disconnected, null);
        }

        public async Task SlewToAsync(double rightAscensionHours, double declinationDegrees, CancellationToken ct = default) {
            EnsureConnected();
            var target = new Coordinates(Angle.ByHours(rightAscensionHours), Angle.ByDegree(declinationDegrees), Epoch.J2000);
            var (alt, _) = AltAz(target);
            var limits = engine.Settings;
            if (alt < 0) {
                throw new InvalidOperationException($"Slew refused: target is below the horizon (altitude {alt:0.0}°)");
            }
            if (alt > limits.MaxAltitudeDegrees) {
                throw new InvalidOperationException($"Slew refused: altitude {alt:0.0}° is above the {limits.MaxAltitudeDegrees:0}° keyhole limit");
            }
            var since = DateTime.UtcNow;
            // NINA's TelescopeVM reports a goto that ':Q#' halted as a success; a halt (Abort, Disconnect) cancels this token instead
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, HaltToken);
            var ok = await engine.Host.TelescopeMediator.SlewToCoordinatesAsync(target, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            ThrowIfLost();
            if (!ok) {
                throw new InvalidOperationException("Slew failed: " + (EngineRuntime.LastProblemSince(since) ?? "the mount refused or did not arrive"));
            }
        }

        public async Task SyncAsync(double rightAscensionHours, double declinationDegrees) {
            EnsureConnected();
            var since = DateTime.UtcNow;
            var ok = await engine.Host.TelescopeMediator.Sync(new Coordinates(Angle.ByHours(rightAscensionHours), Angle.ByDegree(declinationDegrees), Epoch.J2000));
            if (!ok) {
                throw new InvalidOperationException("Sync refused: " + (EngineRuntime.LastProblemSince(since) ?? "the mount did not accept it"));
            }
            RaiseChanged();
        }

        public async Task DitherAsync(double pixels, CancellationToken ct = default) {
            EnsureConnected();
            if (pixels > 0) {
                engine.Profile.ActiveProfile.GuiderSettings.DitherPixels = pixels;
            }
            var ok = await engine.Host.GuiderMediator.Dither(ct);
            ct.ThrowIfCancellationRequested();
            if (!ok) {
                throw new InvalidOperationException("Dither failed: the mount dither (DirectGuider) is not connected");
            }
            // DirectGuider waits for the longer pulse plus the settle time and then for TelescopeInfo.IsPulseGuiding, which
            // TelescopeVM polls. The driver serialises the two axes and owns the live flag (true from PulseGuide until the
            // mount has stopped), so wait on that too before the next frame starts.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (Telescope.IsPulseGuiding && DateTime.UtcNow < deadline) {
                await Task.Delay(50, ct);
            }
        }

        public void SetTracking(bool on) {
            EnsureConnected();
            // NINA's TelescopeVM returns the tracking state after the write, not whether the write worked
            if (engine.Host.TelescopeMediator.SetTrackingEnabled(on) != on) {
                throw new InvalidOperationException($"The mount did not {(on ? "start" : "stop")} tracking");
            }
            RaiseChanged();
        }

        /// <summary>The driver's soft park (Lx200Settings: park in place, or at the stored alt/az; tracking off). Never :hP#.</summary>
        public async Task SoftParkAsync(CancellationToken ct = default) {
            EnsureConnected();
            var since = DateTime.UtcNow;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, HaltToken);
            var ok = await engine.Host.TelescopeMediator.ParkTelescope(null, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!ok) {
                throw new InvalidOperationException("Park failed: " + (EngineRuntime.LastProblemSince(since) ?? "the mount did not park"));
            }
            RaiseChanged();
        }

        /// <summary>
        /// Cancelled when the mount is halted (<see cref="Abort"/>, a disconnect): gotos, parks and centring link it, so a halted
        /// operation ends instead of carrying on (TelescopeVM reports a halted goto as arrived).
        /// </summary>
        internal CancellationToken HaltToken {
            get {
                lock (lockobj) {
                    return haltCts.Token;
                }
            }
        }

        /// <summary>Halts the mount (':Q#' through NINA's StopSlew) and ends every goto, park and centring that is waiting on it.</summary>
        public void Abort() {
            try {
                engine.Host.TelescopeMediator.StopSlew();
            } catch (Exception ex) {
                Logger.Error("Nightglass: stop slew failed", ex);
            }
            SignalHalt();
        }

        private void SignalHalt() {
            CancellationTokenSource halted;
            lock (lockobj) {
                halted = haltCts;
                haltCts = new CancellationTokenSource();
            }
            try {
                halted.Cancel();
            } catch (AggregateException ex) {
                Logger.Error("Nightglass: ending the halted mount operations", ex);
            }
            halted.Dispose();
        }

        /// <summary>The app's 1 Hz tick: notices a mount NINA disconnected (the link gave up) and refreshes the readouts.</summary>
        public void Tick() {
            var lost = false;
            lock (lockobj) {
                if (state == DeviceConnectionState.Connected && !engine.Host.Telescope.TelescopeInfo.Connected) {
                    lost = true;
                }
            }
            if (lost) {
                MarkLost("The mount stopped answering and the serial link gave up. Check the cable and the mount's power, then reconnect.");
                return;
            }
            RaiseChanged();
        }

        public void Dispose() {
            Lx200Link l;
            lock (lockobj) {
                l = link;
                link = null;
            }
            if (l != null) {
                l.StateChanged -= OnLinkStateChanged;
            }
        }

        private void OnLinkStateChanged(Lx200LinkState linkState) {
            switch (linkState) {
                case Lx200LinkState.Reconnecting:
                    lock (lockobj) {
                        if (state != DeviceConnectionState.Connected) {
                            return;
                        }
                        linkDown = true;
                        state = DeviceConnectionState.Lost;
                        lastError = "Serial link to the mount lost; reconnecting on its own (gotos and focusing wait). Check the USB serial cable.";
                    }
                    Logger.Warning("Nightglass: mount link lost, the driver is reconnecting");
                    RaiseChanged();
                    break;

                case Lx200LinkState.Connected:
                    lock (lockobj) {
                        if (state != DeviceConnectionState.Lost || !linkDown) {
                            return;
                        }
                        linkDown = false;
                        state = DeviceConnectionState.Connected;
                        lastError = null;
                    }
                    Logger.Info("Nightglass: mount link restored");
                    RaiseChanged();
                    break;

                case Lx200LinkState.Failed:
                    lock (lockobj) {
                        if (state == DeviceConnectionState.Disconnected) {
                            return;
                        }
                        linkDown = false;
                        state = DeviceConnectionState.Lost;
                        lastError = "The serial link to the mount gave up reconnecting. Check the cable and the mount's power, then reconnect.";
                    }
                    RaiseChanged();
                    break;
            }
        }

        private void MarkLost(string reason) {
            lock (lockobj) {
                if (state != DeviceConnectionState.Connected) {
                    return;
                }
                state = DeviceConnectionState.Lost;
                lastError = reason;
            }
            Logger.Warning($"Nightglass: mount lost: {reason}");
            RaiseChanged();
        }

        private async Task DisconnectEngineDevices() {
            Lx200Link l;
            lock (lockobj) {
                l = link;
                link = null;
                linkDown = false;
                focuserConnected = false;
            }
            if (l != null) {
                l.StateChanged -= OnLinkStateChanged;
            }
            // The driver's teardown halts only MoveAxis motions; a goto would carry on with the link closed. Send ':Q#' first
            // (StopSlew returns once it is on the wire, or owes it while the link reconnects) and end the operations waiting on it.
            if (Telescope.Connected) {
                try {
                    engine.Host.TelescopeMediator.StopSlew();
                } catch (Exception ex) {
                    Logger.Error("Nightglass: stop slew before disconnect failed", ex);
                }
            }
            SignalHalt();
            try {
                await engine.Host.Guider.Disconnect();
            } catch (Exception ex) {
                Logger.Error("Nightglass: guider disconnect failed", ex);
            }
            try {
                engine.Lx200.Focuser.Disconnect();
            } catch (Exception ex) {
                Logger.Error("Nightglass: focuser disconnect failed", ex);
            }
            try {
                await engine.Host.Telescope.Disconnect();
            } catch (Exception ex) {
                Logger.Error("Nightglass: mount disconnect failed", ex);
            }
        }

        private (double Altitude, double Azimuth) AltAz(Coordinates coordinates) {
            var a = engine.Profile.ActiveProfile.AstrometrySettings;
            var topo = coordinates.Transform(Angle.ByDegree(a.Latitude), Angle.ByDegree(a.Longitude), a.Elevation);
            return (topo.Altitude.Degree, topo.Azimuth.Degree);
        }

        private static Coordinates J2000(NINA.Equipment.Equipment.MyTelescope.TelescopeInfo info) {
            var c = info.Coordinates;
            if (c == null || double.IsNaN(c.RA) || double.IsNaN(c.Dec)) {
                return null;
            }
            return c.Transform(Epoch.J2000);
        }

        private T? WithInfo<T>(Func<NINA.Equipment.Equipment.MyTelescope.TelescopeInfo, T?> select) where T : struct {
            lock (lockobj) {
                if (state != DeviceConnectionState.Connected) {
                    return null;
                }
            }
            var info = engine.Host.Telescope.TelescopeInfo;
            return info.Connected ? select(info) : null;
        }

        private void ThrowIfLost() {
            lock (lockobj) {
                if (state == DeviceConnectionState.Lost) {
                    throw new DeviceLostException(lastError ?? "Mount connection lost");
                }
            }
        }

        private void EnsureConnected() {
            lock (lockobj) {
                if (state == DeviceConnectionState.Lost) {
                    throw new DeviceLostException(lastError ?? "Mount connection lost");
                }
                if (state != DeviceConnectionState.Connected) {
                    throw new InvalidOperationException("Mount is not connected");
                }
            }
        }

        private void SaveProfile() {
            try {
                engine.Profile.Save();
            } catch (Exception ex) {
                Logger.Error("Nightglass: saving the rig profile failed", ex);
            }
        }

        private void SetState(DeviceConnectionState newState, string error) {
            lock (lockobj) {
                state = newState;
                lastError = error;
            }
            RaiseChanged();
        }

        internal void RaiseChanged() => engine.Ui.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// The #1209 focuser through the mount (Lx200Focuser): timed moves in milliseconds at speed 1-4 (Lx200Settings.FocuserSpeed,
    /// sent before every move), a virtual position that a speed change recentres. Its connection follows the mount's.
    /// </summary>
    public sealed class EngineFocuserService : IFocuserService {

        /// <summary>Same cap as the simulator: one nudge is at most 30 s of motor time.</summary>
        public const int MaxSingleMoveMs = 30000;

        private readonly EngineDevices engine;
        private int moving;

        internal EngineFocuserService(EngineDevices engine) {
            this.engine = engine;
            engine.Mount.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler Changed;

        private Lx200Focuser Focuser => engine.Lx200.Focuser;

        public DeviceConnectionState State {
            get {
                var mount = engine.Mount.State;
                if (mount == DeviceConnectionState.Connected) {
                    return Focuser.Connected ? DeviceConnectionState.Connected : DeviceConnectionState.Disconnected;
                }
                return mount;
            }
        }

        public int Speed => Math.Clamp(Focuser.Settings.FocuserSpeed, 1, 4);

        public int Position => Focuser.Position;

        public int MaxStep => Focuser.MaxStep;

        public bool IsMoving => Volatile.Read(ref moving) > 0 || Focuser.IsMoving;

        public void SetSpeed(int speed) {
            if (speed < 1 || speed > 4) {
                throw new ArgumentOutOfRangeException(nameof(speed), "Speed is 1 (slowest) to 4");
            }
            if (Focuser.Settings.FocuserSpeed == speed) {
                return;
            }
            Focuser.Settings.FocuserSpeed = speed;
            // Positions only compare at one speed
            Focuser.RecenterPosition();
            Save();
            RaiseChanged();
        }

        public async Task MoveAsync(int milliseconds, CancellationToken ct = default) {
            if (milliseconds == 0) {
                return;
            }
            if (Math.Abs(milliseconds) > MaxSingleMoveMs) {
                throw new ArgumentOutOfRangeException(nameof(milliseconds), $"A single move is at most {MaxSingleMoveMs} ms");
            }
            var mount = engine.Mount.State;
            if (mount == DeviceConnectionState.Lost) {
                throw new DeviceLostException(engine.Mount.LastError ?? "Mount connection lost; the focuser is driven through it");
            }
            if (mount != DeviceConnectionState.Connected) {
                throw new InvalidOperationException("Focuser is driven through the mount; connect the mount first");
            }
            if (!Focuser.Connected) {
                throw new InvalidOperationException("The focuser did not connect with the mount" + (engine.Mount.FocuserError is { } e ? $": {e}" : ""));
            }
            if (Interlocked.Increment(ref moving) > 1) {
                Interlocked.Decrement(ref moving);
                throw new InvalidOperationException("Focuser is already moving");
            }
            RaiseChanged();
            try {
                await Focuser.Move(Focuser.Position + milliseconds, ct);
            } finally {
                Interlocked.Decrement(ref moving);
                RaiseChanged();
            }
        }

        public void RecenterVirtualPosition() {
            Focuser.RecenterPosition();
            RaiseChanged();
        }

        private void Save() {
            try {
                engine.Profile.Save();
            } catch (Exception ex) {
                Logger.Error("Nightglass: saving the rig profile failed", ex);
            }
        }

        internal void RaiseChanged() => engine.Ui.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }
}
