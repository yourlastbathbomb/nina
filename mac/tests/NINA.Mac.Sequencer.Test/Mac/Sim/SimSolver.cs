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
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace NINA.Mac.Sequencer.Test.Sim {

    /// <summary>
    /// Plate solving for the simulated night. Two paths, because NINA has two:
    /// <list type="bullet">
    /// <item>Center (and CenterAfterDriftTrigger's centring run, through the fork seam) take an injected IPlateSolverFactory:
    /// <see cref="Factory"/> hands out <see cref="Solver"/>, which "solves" every frame to where the mount really points, and NINA's own ImageSolver, CaptureSolver and CenteringSolver around it, as PlateSolverFactoryProxy does.</item>
    /// <item>CenterAfterDriftTrigger's drift check (PlatesolvingImageFollower) calls the static PlateSolverFactory with the
    /// profile's settings, so it runs the profile's ASTAP executable: <see cref="WriteFakeAstap"/> writes a /bin/sh stand-in that
    /// answers like ASTAP (an .ini next to the image) with the truth file's solution, which <see cref="WriteTruth"/> keeps at the
    /// true pointing for each exposure. NINA's real ASTAPSolver and CLISolver run around it.</item>
    /// </list>
    /// </summary>
    internal sealed class SimSolver {
        public const double PixelScaleArcsec = 0.4785;

        /// <param name="truth">Where the telescope really points (J2000): what a solve finds.</param>
        public SimSolver(Func<Coordinates> truth, string folder) {
            Truth = truth;
            Solver = new TruthSolver(truth);
            Factory = new SimPlateSolverFactory(Solver);
            TruthFile = Path.Combine(folder, "astap truth.ini");
            ArgumentLog = Path.Combine(folder, "astap calls.log");
            AstapPath = Path.Combine(folder, "fake astap");
            WriteTruth(truth());
        }

        public Func<Coordinates> Truth { get; }
        public TruthSolver Solver { get; }
        public SimPlateSolverFactory Factory { get; }
        public string TruthFile { get; }
        public string ArgumentLog { get; }
        public string AstapPath { get; }

        /// <summary>Number of times NINA ran the fake ASTAP (drift checks).</summary>
        public int AstapCalls => File.Exists(ArgumentLog) ? File.ReadAllLines(ArgumentLog).Length : 0;

        public void WriteTruth(Coordinates j2000) {
            var s = PixelScaleArcsec / 3600.0;
            var ini = new StringBuilder();
            ini.Append("PLTSOLVD=T\n");
            ini.Append(string.Format(CultureInfo.InvariantCulture, "CRVAL1={0:R}\n", j2000.RADegrees));
            ini.Append(string.Format(CultureInfo.InvariantCulture, "CRVAL2={0:R}\n", j2000.Dec));
            ini.Append("CRPIX1=960.5\nCRPIX2=540.5\n");
            ini.Append(string.Format(CultureInfo.InvariantCulture, "CD1_1={0:R}\nCD1_2=0\nCD2_1=0\nCD2_2={1:R}\n", -s, s));
            var temp = TruthFile + ".tmp";
            File.WriteAllText(temp, ini.ToString());
            File.Move(temp, TruthFile, overwrite: true);
        }

        public void WriteFakeAstap() {
            var script = new StringBuilder();
            script.Append("#!/bin/sh\n");
            script.Append("# Stand-in for astap_cli: answers like ASTAP (an .ini next to the image) with the test's truth file\n");
            script.Append("printf '%s\\n' \"$*\" >> \"$(dirname \"$0\")/astap calls.log\"\n");
            script.Append("f=''\n");
            script.Append("while [ $# -gt 0 ]; do\n  if [ \"$1\" = \"-f\" ]; then f=\"$2\"; shift; fi\n  shift\ndone\n");
            script.Append("[ -n \"$f\" ] || exit 2\n");
            script.Append("cp \"$(dirname \"$0\")/astap truth.ini\" \"${f%.*}.ini\"\n");
            File.WriteAllText(AstapPath, script.ToString());
            File.SetUnixFileMode(AstapPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        /// <summary>"Solves" to where the mount really points, or fails on request.</summary>
        public sealed class TruthSolver : IPlateSolver {
            private readonly Func<Coordinates> truth;

            public TruthSolver(Func<Coordinates> truth) {
                this.truth = truth;
            }

            public int Calls => calls;
            private int calls;
            public int FailNextCalls { get; set; }
            public ConcurrentQueue<PlateSolveParameter> Parameters { get; } = new();

            public Task<PlateSolveResult> SolveAsync(IImageData source, PlateSolveParameter parameter, IProgress<ApplicationStatus> progress, CancellationToken canceltoken) {
                Interlocked.Increment(ref calls);
                Parameters.Enqueue(parameter);
                if (FailNextCalls > 0) {
                    FailNextCalls--;
                    return Task.FromResult(new PlateSolveResult { Success = false });
                }
                return Task.FromResult(new PlateSolveResult {
                    Success = true,
                    Coordinates = truth(),
                    Pixscale = PixelScaleArcsec,
                    PositionAngle = 0,
                    Radius = 0.146
                });
            }
        }

        /// <summary>NINA's PlateSolverFactoryProxy with the simulated solver in place of the profile's solvers.</summary>
        public sealed class SimPlateSolverFactory : IPlateSolverFactory {
            private readonly IPlateSolver solver;

            public SimPlateSolverFactory(IPlateSolver solver) {
                this.solver = solver;
            }

            public IPlateSolver GetPlateSolver(IPlateSolveSettings plateSolveSettings) => solver;

            public IPlateSolver GetBlindSolver(IPlateSolveSettings plateSolveSettings) => solver;

            public IImageSolver GetImageSolver(IPlateSolver plateSolver, IPlateSolver blindSolver) => new ImageSolver(plateSolver, blindSolver);

            public ICaptureSolver GetCaptureSolver(IPlateSolver plateSolver, IPlateSolver blindSolver, IImagingMediator imagingMediator, IFilterWheelMediator filterWheelMediator) {
                return new CaptureSolver(plateSolver, blindSolver, imagingMediator, filterWheelMediator);
            }

            public ICenteringSolver GetCenteringSolver(IPlateSolver plateSolver, IPlateSolver blindSolver, IImagingMediator imagingMediator, ITelescopeMediator telescopeMediator, IFilterWheelMediator filterWheelMediator, IDomeMediator domeMediator, IDomeFollower domeFollower) {
                return new CenteringSolver(plateSolver, blindSolver, imagingMediator, telescopeMediator, filterWheelMediator, domeMediator, domeFollower);
            }
        }
    }
}
