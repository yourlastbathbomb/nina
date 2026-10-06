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
using NINA.Astrometry.Body;
using NINA.Astrometry.Interfaces;
using NINA.Astrometry.RiseAndSet;
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine.Test.Fakes {

    /// <summary>Sky positions for the test site (Deep Water Bay, as the Autostar simulator's default site).</summary>
    internal static class Sky {
        public const double Latitude = 22.25;
        public const double Longitude = 114.18;
        public const double Elevation = 10;

        /// <summary>J2000 coordinates that stand at <paramref name="altitude"/>, <paramref name="azimuth"/> right now.</summary>
        public static Coordinates At(double altitude, double azimuth) {
            var topo = new TopocentricCoordinates(Angle.ByDegree(azimuth), Angle.ByDegree(altitude), Angle.ByDegree(Latitude), Angle.ByDegree(Longitude), Elevation);
            return topo.Transform(Epoch.J2000);
        }

        public static double AltitudeNow(Coordinates c) =>
            c.Transform(Angle.ByDegree(Latitude), Angle.ByDegree(Longitude), Elevation).Altitude.Degree;
    }

    /// <summary>A fixed device list (the first real device is picked by the gate), counting its scans.</summary>
    internal sealed class FixedDeviceChooser : IDeviceChooserVM {

        public FixedDeviceChooser(params IDevice[] devices) {
            Devices = devices.ToList();
        }

        public int Scans { get; private set; }

        public IDevice SelectedDevice { get; set; }

        public bool SetupDialogOpen => false;

        public IList<IDevice> Devices { get; }

        public Task GetEquipment() {
            Scans++;
            SelectedDevice ??= Devices.FirstOrDefault();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A plate solver that "solves" every frame at where the mount really points (<see cref="Truth"/>), as the sequencer tests'
    /// simulated solver does, so NINA's CenteringSolver measures and removes a pointing error.
    /// </summary>
    internal sealed class FakeSolver : IPlateSolver {
        private readonly Func<Coordinates> truth;

        public FakeSolver(Func<Coordinates> truth) {
            this.truth = truth;
        }

        public int Calls { get; private set; }

        public bool Fail { get; set; }

        public Task<PlateSolveResult> SolveAsync(IImageData source, PlateSolveParameter parameter, IProgress<ApplicationStatus> progress, CancellationToken canceltoken) {
            Calls++;
            if (Fail) {
                return Task.FromResult(new PlateSolveResult { Success = false });
            }
            return Task.FromResult(new PlateSolveResult {
                Success = true,
                Coordinates = truth().Transform(Epoch.J2000),
                Pixscale = 0.4785,
                PositionAngle = 0,
                Radius = 0.15
            });
        }
    }

    /// <summary>NINA's own image, capture and centring solvers around <see cref="FakeSolver"/>.</summary>
    internal sealed class FakeSolverFactory : IPlateSolverFactory {

        public FakeSolverFactory(FakeSolver solver) {
            Solver = solver;
        }

        public FakeSolver Solver { get; }

        public IPlateSolver GetPlateSolver(IPlateSolveSettings plateSolveSettings) => Solver;

        public IPlateSolver GetBlindSolver(IPlateSolveSettings plateSolveSettings) => Solver;

        public IImageSolver GetImageSolver(IPlateSolver plateSolver, IPlateSolver blindSolver) => new ImageSolver(plateSolver, blindSolver);

        public ICaptureSolver GetCaptureSolver(IPlateSolver plateSolver, IPlateSolver blindSolver, IImagingMediator imagingMediator, IFilterWheelMediator filterWheelMediator) =>
            new CaptureSolver(plateSolver, blindSolver, imagingMediator, filterWheelMediator);

        public ICenteringSolver GetCenteringSolver(IPlateSolver plateSolver, IPlateSolver blindSolver, IImagingMediator imagingMediator, ITelescopeMediator telescopeMediator,
                IFilterWheelMediator filterWheelMediator, IDomeMediator domeMediator, IDomeFollower domeFollower) =>
            new CenteringSolver(plateSolver, blindSolver, imagingMediator, telescopeMediator, filterWheelMediator, domeMediator, domeFollower);
    }

    /// <summary>Twilight times the test chooses (astronomical dawn an hour away by default), as the sequencer tests do.</summary>
    internal sealed class FixedNighttimeCalculator : INighttimeCalculator {
        private readonly NighttimeData data;

        public FixedNighttimeCalculator(DateTime astronomicalDawn) {
            var sunset = DateTime.Now.AddHours(-1);
            var evening = sunset.AddMinutes(30);
            data = new NighttimeData(DateTime.Now, DateTime.Today, AstroUtil.MoonPhase.Unknown, null,
                new Fixed(evening, astronomicalDawn),
                new Fixed(evening, astronomicalDawn.AddMinutes(25)),
                new Fixed(sunset, astronomicalDawn.AddMinutes(75)),
                new Fixed(null, null),
                new Fixed(sunset.AddMinutes(10), astronomicalDawn.AddMinutes(50)));
        }

        public event EventHandler OnReferenceDayChanged {
            add { }
            remove { }
        }

        public NighttimeData Calculate(DateTime? selectedDate = null) => data;

        private sealed class Fixed : RiseAndSetEvent {
            private readonly DateTime? set;
            private readonly DateTime? rise;

            public Fixed(DateTime? set, DateTime? rise) : base(DateTime.Today, Sky.Latitude, Sky.Longitude, Sky.Elevation) {
                this.set = set;
                this.rise = rise;
            }

            public override DateTime? Rise => rise;

            public override DateTime? Set => set;

            protected override double AdjustAltitude(BasicBody body) => throw new NotSupportedException();

            protected override BasicBody GetBody(DateTime date) => throw new NotSupportedException();
        }
    }
}
