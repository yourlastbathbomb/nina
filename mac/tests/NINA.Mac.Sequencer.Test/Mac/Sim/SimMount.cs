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
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using System.Collections.Concurrent;

namespace NINA.Mac.Sequencer.Test.Sim {

    /// <summary>
    /// A simulated alt-az mount behind NINA's ITelescope, behaving where it matters as the LX200GPS driver does: it reports JNow,
    /// cannot flip (TimeToMeridianFlip NaN, pier side unknown, MeridianFlip refused and counted), parks and unparks, and pulse
    /// guides at half sidereal. Its state is the reported position R and a pointing error e = R - T, where T is where it really
    /// points: a goto sets R to the target (so T = target - e); a sync sets R to the synced coordinates and e to R - T, leaving T
    /// where it is; a guide pulse moves R and T together. An optional tracking drift makes e grow with time, so T walks away
    /// from the target while R stays on it, as with an alt-az tracking error; NINA's drift centring has to find and remove it.
    /// Everything is recorded: slews, syncs, pulses, parks and flip requests.
    /// </summary>
    internal sealed class SimMount : BaseINPC, ITelescope {
        private readonly object sync = new object();
        private Coordinates reported;               // R, J2000
        private double errorRaDeg, errorDecDeg;     // e at errorSince, J2000 degrees (RA in RA degrees)
        private DateTime errorSince = DateTime.UtcNow;
        private DateTime pulseUntil = DateTime.MinValue;
        private DateTime slewUntil = DateTime.MinValue;

        /// <param name="initialTrue">Where the mount points at the start.</param>
        /// <param name="gotoErrorRaArcmin">Initial pointing error on the sky (reported minus true), RA direction.</param>
        public SimMount(Coordinates initialTrue, double gotoErrorRaArcmin = 0, double gotoErrorDecArcmin = 0) {
            var t = initialTrue.Transform(Epoch.J2000);
            GotoErrorRaArcmin = gotoErrorRaArcmin;
            GotoErrorDecArcmin = gotoErrorDecArcmin;
            errorRaDeg = gotoErrorRaArcmin / 60 / Math.Cos(AstroUtil.ToRadians(t.Dec));
            errorDecDeg = gotoErrorDecArcmin / 60;
            reported = Offset(t, errorRaDeg, errorDecDeg);
        }

        public const string DeviceId = "Sim_LX200GPS";

        public double GotoErrorRaArcmin { get; }
        public double GotoErrorDecArcmin { get; }

        /// <summary>Tracking drift of the true pointing, arcseconds per second on the sky (RA direction, and Dec). Set it at a sync or slew.</summary>
        public double DriftRaArcsecPerSec { get; set; }
        public double DriftDecArcsecPerSec { get; set; }

        public ConcurrentQueue<Coordinates> SlewTargets { get; } = new();
        public ConcurrentQueue<Coordinates> SyncTargets { get; } = new();
        public ConcurrentQueue<(GuideDirections Direction, int Milliseconds, DateTime At)> Pulses { get; } = new();
        public int Parks { get; private set; }
        public int Unparks { get; private set; }
        public int MeridianFlipRequests { get; private set; }

        /// <summary>Called after every slew, sync and pulse.</summary>
        public Action? PointingChanged { get; set; }

        private static Coordinates Offset(Coordinates c, double raDeg, double decDeg) {
            return new Coordinates(Angle.ByDegree(AstroUtil.EuclidianModulus(c.RADegrees + raDeg, 360)), Angle.ByDegree(c.Dec + decDeg), Epoch.J2000);
        }

        /// <summary>The pointing error e(t) = R - T now, J2000 degrees.</summary>
        private (double Ra, double Dec) Error() {
            var seconds = (DateTime.UtcNow - errorSince).TotalSeconds;
            return (errorRaDeg - DriftRaArcsecPerSec * seconds / 3600 / Math.Cos(AstroUtil.ToRadians(reported.Dec)),
                    errorDecDeg - DriftDecArcsecPerSec * seconds / 3600);
        }

        /// <summary>Where the mount really points now (J2000): T = R - e(t).</summary>
        public Coordinates True {
            get {
                lock (sync) {
                    var (ra, dec) = Error();
                    return Offset(reported, -ra, -dec);
                }
            }
        }

        /// <summary>What the mount reports (J2000): R.</summary>
        public Coordinates ReportedJ2000 {
            get {
                lock (sync) {
                    return reported;
                }
            }
        }

        // IDevice
        public bool HasSetupDialog => false;
        public string Id => DeviceId;
        public string Name => "Simulated LX200GPS (alt-az)";
        public string DisplayName => Name;
        public string Category => "Simulator";
        public bool Connected { get; private set; }
        public string Description => "Simulated alt-az LX200GPS for the headless sequencer tests";
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

        // Position
        public Coordinates Coordinates => ReportedJ2000.Transform(Epoch.JNOW);
        public double RightAscension => Coordinates.RA;
        public string RightAscensionString => Coordinates.RAString;
        public double Declination => Coordinates.Dec;
        public string DeclinationString => Coordinates.DecString;
        public double SiderealTime => AstroUtil.GetLocalSiderealTimeNow(SiteLongitude);
        public string SiderealTimeString => AstroUtil.HoursToHMS(SiderealTime);
        public double Altitude => Horizontal().Altitude.Degree;
        public string AltitudeString => AstroUtil.DegreesToDMS(Altitude);
        public double Azimuth => Horizontal().Azimuth.Degree;
        public string AzimuthString => AstroUtil.DegreesToDMS(Azimuth);
        public double HoursToMeridian => AstroUtil.EuclidianModulus(RightAscension - SiderealTime, 24);
        public string HoursToMeridianString => AstroUtil.HoursToHMS(HoursToMeridian);
        public double TimeToMeridianFlip => double.NaN;
        public string TimeToMeridianFlipString => string.Empty;
        public double PrimaryMovingRate { get; set; }
        public double SecondaryMovingRate { get; set; }
        public PierSide SideOfPier => PierSide.pierUnknown;
        public bool CanSetTrackingEnabled => true;
        public bool TrackingEnabled { get; set; } = true;
        public IList<TrackingMode> TrackingModes => new List<TrackingMode> { TrackingMode.Sidereal, TrackingMode.Stopped };
        public TrackingRate TrackingRate => TrackingEnabled ? new TrackingRate { TrackingMode = TrackingMode.Sidereal } : TrackingRate.STOPPED;
        public TrackingMode TrackingMode {
            get => TrackingEnabled ? TrackingMode.Sidereal : TrackingMode.Stopped;
            set => TrackingEnabled = value != TrackingMode.Stopped;
        }
        public double SiteLatitude { get; set; } = Sky.Latitude;
        public double SiteLongitude { get; set; } = Sky.Longitude;
        public double SiteElevation { get; set; } = Sky.Elevation;
        public bool AtHome => false;
        public bool CanFindHome => false;
        public bool AtPark { get; private set; } = true;
        public bool CanPark => true;
        public bool CanUnpark => true;
        public bool CanSetPark => false;
        public Epoch EquatorialSystem => Epoch.JNOW;
        public bool HasUnknownEpoch => false;
        public Coordinates TargetCoordinates { get; private set; } = new Coordinates(Angle.Zero, Angle.Zero, Epoch.JNOW);
        public PierSide? TargetSideOfPier => null;
        public bool Slewing => DateTime.UtcNow < slewUntil;
        public double GuideRateRightAscensionArcsecPerSec => 7.5205;
        public double GuideRateDeclinationArcsecPerSec => 7.5205;
        public bool CanMovePrimaryAxis => false;
        public bool CanMoveSecondaryAxis => false;
        public bool CanSetDeclinationRate => false;
        public bool CanSetRightAscensionRate => false;
        public AlignmentMode AlignmentMode => AlignmentMode.AltAz;
        public bool CanPulseGuide => true;
        public bool IsPulseGuiding => DateTime.UtcNow < pulseUntil;
        public bool CanSetPierSide => false;
        public bool CanSlew => true;
        public bool CanSlewAltAz => true;
        public DateTime UTCDate => DateTime.UtcNow;

        private TopocentricCoordinates Horizontal() {
            return ReportedJ2000.Transform(Angle.ByDegree(SiteLatitude), Angle.ByDegree(SiteLongitude), SiteElevation);
        }

        public IList<(double, double)> GetAxisRates(TelescopeAxes axis) => new List<(double, double)>();

        public Task<bool> MeridianFlip(Coordinates targetCoordinates, CancellationToken token) {
            MeridianFlipRequests++;
            return Task.FromResult(false);
        }

        public void MoveAxis(TelescopeAxes axis, double rate) {
        }

        public void PulseGuide(GuideDirections direction, int duration) {
            Pulses.Enqueue((direction, duration, DateTime.UtcNow));
            var arcsec = GuideRateDeclinationArcsecPerSec * duration / 1000.0;
            lock (sync) {
                var cosDec = Math.Cos(AstroUtil.ToRadians(reported.Dec));
                reported = direction switch {
                    GuideDirections.guideNorth => Offset(reported, 0, arcsec / 3600),
                    GuideDirections.guideSouth => Offset(reported, 0, -arcsec / 3600),
                    GuideDirections.guideEast => Offset(reported, arcsec / 3600 / cosDec, 0),
                    _ => Offset(reported, -arcsec / 3600 / cosDec, 0)
                };
            }
            pulseUntil = DateTime.UtcNow.AddMilliseconds(duration);
            PointingChanged?.Invoke();
        }

        public Task Park(CancellationToken token) {
            Parks++;
            AtPark = true;
            TrackingEnabled = false;
            return Task.CompletedTask;
        }

        public void Setpark() {
        }

        public async Task<bool> SlewToCoordinates(Coordinates coordinates, CancellationToken token) {
            if (AtPark) {
                return false;
            }
            var target = coordinates.Transform(Epoch.J2000);
            TargetCoordinates = coordinates;
            SlewTargets.Enqueue(target);
            slewUntil = DateTime.UtcNow.AddMilliseconds(300);
            await Task.Delay(300, token);
            lock (sync) {
                reported = target;
            }
            PointingChanged?.Invoke();
            return true;
        }

        public Task<bool> SlewToAltAz(TopocentricCoordinates coordinates, CancellationToken token) {
            return SlewToCoordinates(coordinates.Transform(Epoch.J2000), token);
        }

        public void StopSlew() {
            slewUntil = DateTime.MinValue;
        }

        public bool Sync(Coordinates coordinates) {
            var target = coordinates.Transform(Epoch.J2000);
            SyncTargets.Enqueue(target);
            lock (sync) {
                var t = True;
                reported = target;
                errorRaDeg = target.RADegrees - t.RADegrees;
                errorDecDeg = target.Dec - t.Dec;
                errorSince = DateTime.UtcNow;
            }
            PointingChanged?.Invoke();
            return true;
        }

        public Task Unpark(CancellationToken token) {
            Unparks++;
            AtPark = false;
            TrackingEnabled = true;
            return Task.CompletedTask;
        }

        public void SetCustomTrackingRate(double rightAscensionRate, double declinationRate) {
        }

        public Task FindHome(CancellationToken token) => throw new NotSupportedException();

        public PierSide DestinationSideOfPier(Coordinates coordinates) => PierSide.pierUnknown;

        /// <summary>
        /// Angular distance between where the mount points and <paramref name="target"/>, arcminutes. Haversine: NINA's
        /// acos-based Coordinates subtraction returns NaN for numerically identical coordinates (wave-7 note).
        /// </summary>
        public double TrueErrorArcmin(Coordinates target) {
            var a = True;
            var b = target.Transform(Epoch.J2000);
            double ra1 = a.RA * 15 * Math.PI / 180, dec1 = a.Dec * Math.PI / 180, ra2 = b.RA * 15 * Math.PI / 180, dec2 = b.Dec * Math.PI / 180;
            var h = Math.Pow(Math.Sin((dec2 - dec1) / 2), 2) + (Math.Cos(dec1) * Math.Cos(dec2) * Math.Pow(Math.Sin((ra2 - ra1) / 2), 2));
            return 2 * Math.Asin(Math.Min(1, Math.Sqrt(h))) * 180 / Math.PI * 60;
        }
    }
}
