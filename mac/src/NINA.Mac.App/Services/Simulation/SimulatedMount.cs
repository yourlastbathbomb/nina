#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.App.Astro;
using NINA.Mac.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Services.Simulation {

    /// <summary>
    /// LX200GPS alt-az stand-in. The port list is real (/dev/cu.*), the mount is not: nothing is opened. Pointing
    /// is kept as RA/Dec while tracking (so alt/az drift with the sky) and as alt/az when tracking is off.
    /// </summary>
    public sealed class SimulatedMount : IMountService {
        public const double SlewRateDegreesPerSecond = 4.0;
        public const double SoftParkAltitude = 30.0;
        public const double SoftParkAzimuth = 180.0;

        private readonly IClock clock;
        private readonly Func<AppSettings> settings;
        private readonly Func<IReadOnlyList<SerialPortInfo>> portLister;
        private readonly Func<HorizonProfile> horizon;
        private readonly Random random = new(1209);
        private readonly object lockobj = new();
        private DeviceConnectionState state = DeviceConnectionState.Disconnected;
        private string lastError;
        private IReadOnlyList<SerialPortInfo> ports = Array.Empty<SerialPortInfo>();
        private string portName;
        private bool tracking;
        private bool slewing;
        private double ra;
        private double dec;
        private double fixedAlt;
        private double fixedAz;
        private CancellationTokenSource slewCts;

        /// <param name="horizon">The local horizon for the slew guard; null (or a null result) means the mathematical horizon only.</param>
        public SimulatedMount(IClock clock, Func<AppSettings> settings, Func<IReadOnlyList<SerialPortInfo>> portLister = null, Func<HorizonProfile> horizon = null) {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.portLister = portLister ?? (() => SerialPorts.List());
            this.horizon = horizon ?? (() => null);
        }

        public TimeSpan ConnectDelay { get; set; } = TimeSpan.FromSeconds(2);

        public string DisplayName => "Mount";

        public bool IsSimulated => true;

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

        public string PortName {
            get {
                lock (lockobj) {
                    return portName;
                }
            }
        }

        public IReadOnlyList<SerialPortInfo> AvailablePorts {
            get {
                lock (lockobj) {
                    return ports;
                }
            }
        }

        public double? RightAscensionHours => WithPointing(p => p.Ra);

        public double? DeclinationDegrees => WithPointing(p => p.Dec);

        public double? Altitude => WithPointing(p => p.Alt);

        public double? Azimuth => WithPointing(p => p.Az);

        public bool IsSlewing {
            get {
                lock (lockobj) {
                    return slewing;
                }
            }
        }

        public bool IsTracking {
            get {
                lock (lockobj) {
                    return tracking && state == DeviceConnectionState.Connected;
                }
            }
        }

        /// <summary>The simulator sets <see cref="IsSlewing"/> before a goto's first await, so the command and the readout agree.</summary>
        public bool IsMotionCommandActive => IsSlewing;

        private GeoSite Site => new(settings().Site.LatitudeDegrees, settings().Site.LongitudeDegrees);

        public void RefreshPorts() {
            IReadOnlyList<SerialPortInfo> list;
            try {
                list = portLister();
            } catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) {
                list = Array.Empty<SerialPortInfo>();
            }
            lock (lockobj) {
                ports = list;
                if (portName == null || !list.Any(p => p.Path == portName)) {
                    portName = list.FirstOrDefault(p => p.IsUsbSerial)?.Path ?? portName;
                }
            }
            RaiseChanged();
        }

        public void SelectPort(string portPath) {
            lock (lockobj) {
                if (state == DeviceConnectionState.Connected || state == DeviceConnectionState.Connecting) {
                    throw new InvalidOperationException("Disconnect before changing the port");
                }
                portName = portPath;
            }
            RaiseChanged();
        }

        public async Task ConnectAsync(CancellationToken ct = default) {
            lock (lockobj) {
                if (state == DeviceConnectionState.Connected || state == DeviceConnectionState.Connecting) {
                    return;
                }
                state = DeviceConnectionState.Connecting;
                lastError = null;
            }
            RaiseChanged();
            try {
                await clock.Delay(ConnectDelay, ct);
            } catch (OperationCanceledException) {
                lock (lockobj) {
                    state = DeviceConnectionState.Disconnected;
                }
                RaiseChanged();
                throw;
            }
            lock (lockobj) {
                state = DeviceConnectionState.Connected;
                // Power-on pointing for the simulation: low in the south, tracking
                (ra, dec) = SkyMath.FromAltAz(clock.Now, SoftParkAltitude, SoftParkAzimuth, Site);
                tracking = true;
            }
            RaiseChanged();
        }

        public Task DisconnectAsync() {
            lock (lockobj) {
                slewCts?.Cancel();
                state = DeviceConnectionState.Disconnected;
                lastError = null;
                tracking = false;
                slewing = false;
            }
            RaiseChanged();
            return Task.CompletedTask;
        }

        public async Task SlewToAsync(double rightAscensionHours, double declinationDegrees, CancellationToken ct = default) {
            var (alt, az) = SkyMath.ToAltAz(clock.Now, rightAscensionHours, declinationDegrees, Site);
            SlewGuard.Check(alt, az, settings(), horizon());
            await MoveAsync(alt, az, ct, () => {
                ra = rightAscensionHours;
                dec = declinationDegrees;
                tracking = true;
            });
        }

        public Task SyncAsync(double rightAscensionHours, double declinationDegrees) {
            lock (lockobj) {
                EnsureConnected();
                ra = rightAscensionHours;
                dec = declinationDegrees;
            }
            RaiseChanged();
            return Task.CompletedTask;
        }

        public async Task DitherAsync(double pixels, CancellationToken ct = default) {
            lock (lockobj) {
                EnsureConnected();
                var scaleDeg = settings().Optics.PixelScaleArcsec / 3600.0;
                var angle = random.NextDouble() * 2 * Math.PI;
                var distance = pixels * scaleDeg * Math.Sqrt(random.NextDouble());
                dec += distance * Math.Sin(angle);
                ra += distance * Math.Cos(angle) / 15.0 / Math.Max(0.05, Math.Cos(dec * Math.PI / 180));
                ra = SkyMath.NormalizeHours(ra);
            }
            RaiseChanged();
            // Pulse time plus settle
            await clock.Delay(TimeSpan.FromSeconds(2), ct);
        }

        public void SetTracking(bool on) {
            lock (lockobj) {
                EnsureConnected();
                if (tracking == on) {
                    return;
                }
                if (!on) {
                    (fixedAlt, fixedAz) = SkyMath.ToAltAz(clock.Now, ra, dec, Site);
                } else {
                    (ra, dec) = SkyMath.FromAltAz(clock.Now, fixedAlt, fixedAz, Site);
                }
                tracking = on;
            }
            RaiseChanged();
        }

        public async Task SoftParkAsync(CancellationToken ct = default) {
            await MoveAsync(SoftParkAltitude, SoftParkAzimuth, ct, () => {
                tracking = false;
                fixedAlt = SoftParkAltitude;
                fixedAz = SoftParkAzimuth;
            });
        }

        public void Abort() {
            lock (lockobj) {
                slewCts?.Cancel();
            }
        }

        public void Tick() => RaiseChanged();

        /// <summary>Simulates the FTDI cable dropping out.</summary>
        public void SimulateConnectionLoss(string reason = "Serial port stopped responding (simulated cable unplug)") {
            lock (lockobj) {
                if (state != DeviceConnectionState.Connected) {
                    return;
                }
                state = DeviceConnectionState.Lost;
                lastError = reason;
                slewCts?.Cancel();
            }
            RaiseChanged();
        }

        private async Task MoveAsync(double alt, double az, CancellationToken ct, Action arrive) {
            CancellationTokenSource linked;
            double distance;
            lock (lockobj) {
                EnsureConnected();
                if (slewing) {
                    throw new InvalidOperationException("Mount is already slewing");
                }
                var current = CurrentPointing();
                distance = AngularDistance(current.Alt, current.Az, alt, az);
                slewing = true;
                slewCts = new CancellationTokenSource();
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct, slewCts.Token);
            }
            RaiseChanged();
            try {
                await clock.Delay(TimeSpan.FromSeconds(Math.Max(1.0, distance / SlewRateDegreesPerSecond)), linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                lock (lockobj) {
                    arrive();
                }
            } catch (OperationCanceledException) when (State == DeviceConnectionState.Lost) {
                throw new DeviceLostException(LastError ?? "Mount connection lost");
            } finally {
                lock (lockobj) {
                    slewing = false;
                    slewCts?.Dispose();
                    slewCts = null;
                }
                linked.Dispose();
                RaiseChanged();
            }
        }

        private T? WithPointing<T>(Func<(double Ra, double Dec, double Alt, double Az), T> select) where T : struct {
            lock (lockobj) {
                if (state != DeviceConnectionState.Connected) {
                    return null;
                }
                return select(CurrentPointing());
            }
        }

        private (double Ra, double Dec, double Alt, double Az) CurrentPointing() {
            if (tracking) {
                var (alt, az) = SkyMath.ToAltAz(clock.Now, ra, dec, Site);
                return (ra, dec, alt, az);
            }
            var (r, d) = SkyMath.FromAltAz(clock.Now, fixedAlt, fixedAz, Site);
            return (r, d, fixedAlt, fixedAz);
        }

        private static double AngularDistance(double alt1, double az1, double alt2, double az2) {
            const double deg = Math.PI / 180;
            var cos = (Math.Sin(alt1 * deg) * Math.Sin(alt2 * deg)) + (Math.Cos(alt1 * deg) * Math.Cos(alt2 * deg) * Math.Cos((az1 - az2) * deg));
            return Math.Acos(Math.Clamp(cos, -1, 1)) / deg;
        }

        private void EnsureConnected() {
            if (state == DeviceConnectionState.Lost) {
                throw new DeviceLostException(lastError ?? "Mount connection lost");
            }
            if (state != DeviceConnectionState.Connected) {
                throw new InvalidOperationException("Mount is not connected");
            }
        }

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
