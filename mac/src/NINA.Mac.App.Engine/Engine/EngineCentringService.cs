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
using NINA.Core.Model.Equipment;
using NINA.Equipment.Model;
using NINA.Mac.App.Services;
using NINA.PlateSolving;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SirilLayout = NINA.Mac.Siril.SessionLayout;
using SirilLayoutOptions = NINA.Mac.Siril.SessionLayoutOptions;

namespace NINA.Mac.App.Engine {

    /// <summary>
    /// Goto and plate-solve centring with NINA's own CenteringSolver, built the way the sequencer's Center item builds it (the
    /// profile's PlateSolveSettings: ASTAP with D80 near, solve-field blind failover; the rig's 15 s, gain 450, bin 2 solve
    /// frames). The solve frames go through NINA's ImagingVM, the sync and re-slew through TelescopeVM to the LX200 driver.
    /// </summary>
    public sealed class EngineCentringService : ICentringService {
        private readonly EngineDevices engine;

        internal EngineCentringService(EngineDevices engine) {
            this.engine = engine;
        }

        /// <summary>True when ASTAP is configured, or the host was given its own solver factory (tests).</summary>
        public bool CanPlateSolve => engine.Options.PlateSolverFactory != null || engine.Solvers.Problem == null;

        /// <summary>Why centring is not available, or null.</summary>
        public string PlateSolveProblem => CanPlateSolve ? null : engine.Solvers.Problem;

        public async Task<CentringResult> SlewAndCentreAsync(double rightAscensionHours, double declinationDegrees, IProgress<string> progress = null, CancellationToken ct = default) {
            if (engine.Session.IsActive) {
                throw new InvalidOperationException("A run is using the mount and the camera; stop it first");
            }
            // A halt (the mount's Abort, a disconnect, quitting) ends the whole operation, not only the goto it interrupts
            using var halt = CancellationTokenSource.CreateLinkedTokenSource(ct, engine.Mount.HaltToken);
            ct = halt.Token;
            progress?.Report("Slewing…");
            await engine.Mount.SlewToAsync(rightAscensionHours, declinationDegrees, ct);
            if (!CanPlateSolve) {
                return new CentringResult(false, null, 0, $"On target by goto only: {PlateSolveProblem}");
            }
            if (engine.Camera.State != DeviceConnectionState.Connected) {
                return new CentringResult(false, null, 0, "On target by goto only: connect the camera to plate solve");
            }
            progress?.Report("Plate solving…");
            var profile = engine.Profile.ActiveProfile;
            var ps = profile.PlateSolveSettings;
            var factory = engine.Host.PlateSolverFactory;
            var plateSolver = factory.GetPlateSolver(ps);
            var blindSolver = factory.GetBlindSolver(ps);
            var solver = factory.GetCenteringSolver(plateSolver, blindSolver, engine.Host.ImagingMediator, engine.Host.TelescopeMediator,
                engine.Host.FilterWheelMediator, engine.Host.DomeMediator, engine.Host.Services.DomeFollower);
            var target = new Coordinates(Angle.ByHours(rightAscensionHours), Angle.ByDegree(declinationDegrees), Epoch.J2000);
            var parameter = new CenterSolveParameter {
                Attempts = ps.NumberOfAttempts,
                Binning = ps.Binning,
                Coordinates = target,
                DownSampleFactor = ps.DownSampleFactor,
                FocalLength = profile.TelescopeSettings.FocalLength,
                MaxObjects = ps.MaxObjects,
                PixelSize = profile.CameraSettings.PixelSize,
                ReattemptDelay = TimeSpan.FromMinutes(ps.ReattemptDelay),
                Regions = ps.Regions,
                SearchRadius = ps.SearchRadius,
                Threshold = ps.Threshold,
                NoSync = profile.TelescopeSettings.NoSync,
                BlindFailoverEnabled = ps.BlindFailoverEnabled,
                DisableNotifications = true,
            };
            var sequence = new CaptureSequence(ps.ExposureTime, CaptureSequence.ImageTypes.SNAPSHOT, ps.Filter, new BinningMode(ps.Binning, ps.Binning), 1) {
                Gain = ps.Gain,
            };
            var solves = 0;
            var solveProgress = new Progress<PlateSolveProgress>(p => {
                if (p?.PlateSolveResult is { } r) {
                    solves++;
                    progress?.Report(r.Success
                        ? string.Format(CultureInfo.InvariantCulture, "Solved: {0} {1}{2}", r.Coordinates?.RAString, r.Coordinates?.DecString,
                            r.Separation is { } s ? string.Format(CultureInfo.InvariantCulture, ", {0:0.00}′ off", s.Distance.ArcMinutes) : "")
                        : "Solve failed");
                }
            });
            var result = await solver.CenterWithMeasurements(sequence, parameter, solveProgress, new Progress<ApplicationStatus>(), ct);
            ct.ThrowIfCancellationRequested();
            var error = result?.Separation?.Distance.ArcMinutes;
            var attempts = result?.Attempts?.Count ?? solves;
            if (result?.Success == true) {
                return new CentringResult(true, error, attempts, string.Format(CultureInfo.InvariantCulture, "Centred{0} ({1} solve{2})",
                    error is double e ? $" within {e:0.00}′" : "", attempts, attempts == 1 ? "" : "s"));
            }
            return new CentringResult(false, error, attempts, "Plate solving failed; the mount is on target by goto only");
        }
    }

    /// <summary>
    /// <see cref="IImageFolders"/> over NINA.Mac.Siril's layout, the folders NINA's file patterns (NinaFilePatterns, in the rig
    /// profile) write to: &lt;root&gt;/&lt;night&gt;/&lt;target&gt;/lights, night-level flats/ and biases/, and library/darks/.
    /// The night is NINA's $$DATEMINUS12$$ in the Mac's time zone (NINA formats it from the local time).
    /// </summary>
    public sealed class SirilFolders : IImageFolders {
        private readonly SirilLayout layout;
        private readonly DateOnly night;

        public SirilFolders(string imagesRoot, DateTimeOffset now, TimeZoneInfo zone = null) {
            layout = new SirilLayout(new SirilLayoutOptions { Root = imagesRoot, SiteTimeZone = zone ?? TimeZoneInfo.Local });
            night = layout.NightOf(now.UtcDateTime);
        }

        public string NightDirectory => layout.NightDirectory(night);

        public string FlatsDirectory => Path.Combine(NightDirectory, SirilLayout.FlatsFolder);

        public string DarksDirectory => layout.Library.DarksDirectory;

        public string BiasesDirectory => Path.Combine(NightDirectory, SirilLayout.BiasesFolder);

        public string LightsDirectory(string target) => layout.GetTargetFolders(night, EngineSessionService.SequenceTargetName(target)).Lights;

        public string SirilCommand(string target) =>
            $"siril-cli -d \"{layout.GetTargetFolders(night, EngineSessionService.SequenceTargetName(target)).WorkingDirectory}\" -s OSC_Preprocessing.ssf";
    }
}
