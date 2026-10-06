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
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Services.Simulation {

    /// <summary>
    /// Stand-in for the headless sequencer: slew (and, later, centre), then expose with mount dithers, stopping at
    /// the max-altitude keyhole, the minimum altitude and astronomical dawn. Writes nothing to disk; the file names
    /// it reports follow <see cref="SessionLayout"/>, NINA.Mac.Siril's layout as the Real devices write it.
    /// </summary>
    public sealed class SimulatedSession : ISessionService {
        private const int MaxLogLines = 200;

        private readonly ICameraService camera;
        private readonly IMountService mount;
        private readonly IClock clock;
        private readonly Func<AppSettings> settings;
        private readonly Func<string> imagesRoot;
        private readonly object lockobj = new();
        private readonly List<string> log = new();
        private SessionState state = SessionState.Idle;
        private SessionPlan plan;
        private SessionProgress progress = SessionProgress.None;
        private SessionLayout layout;
        private TaskCompletionSource resume;
        private string stopRequest;
        private CancellationTokenSource runCts;

        public SimulatedSession(ICameraService camera, IMountService mount, IClock clock, Func<AppSettings> settings, Func<string> imagesRoot) {
            this.camera = camera ?? throw new ArgumentNullException(nameof(camera));
            this.mount = mount ?? throw new ArgumentNullException(nameof(mount));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.imagesRoot = imagesRoot ?? throw new ArgumentNullException(nameof(imagesRoot));
        }

        public event EventHandler Changed;

        public SessionState State {
            get {
                lock (lockobj) {
                    return state;
                }
            }
        }

        public SessionPlan Plan {
            get {
                lock (lockobj) {
                    return plan;
                }
            }
        }

        public SessionProgress Progress {
            get {
                lock (lockobj) {
                    return progress;
                }
            }
        }

        public IImageFolders Layout {
            get {
                lock (lockobj) {
                    return layout;
                }
            }
        }

        public IReadOnlyList<string> Log {
            get {
                lock (lockobj) {
                    return log.ToArray();
                }
            }
        }

        /// <summary>The simulators have no plan validator (that is NINA.Mac.Sequencing's, with Real devices); the Target screen's warnings apply.</summary>
        public PlanCheck CheckPlan(SessionPlan plan) => new(Array.Empty<string>(), Array.Empty<string>(),
            "Simulated devices: NINA's plan check (window, horizon, keyhole, dawn) runs with Real devices. The Target screen's warnings still apply.");

        public async Task RunAsync(SessionPlan newPlan, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(newPlan);
            if (newPlan.FrameCount < 1 || newPlan.ExposureSeconds <= 0) {
                throw new ArgumentException("Plan needs at least one frame and a positive exposure", nameof(newPlan));
            }
            lock (lockobj) {
                if (state == SessionState.Running || state == SessionState.Paused || state == SessionState.Stopping) {
                    throw new InvalidOperationException("A session is already running");
                }
            }
            if (camera.State != DeviceConnectionState.Connected) {
                throw new InvalidOperationException("Connect the camera first");
            }
            if (mount.State != DeviceConnectionState.Connected) {
                throw new InvalidOperationException("Connect the mount first");
            }
            var start = clock.Now;
            lock (lockobj) {
                plan = newPlan;
                layout = new SessionLayout(imagesRoot(), start, settings().Site.UtcOffsetHours);
                progress = new SessionProgress(0, newPlan.FrameCount, null, null, null, TimeSpan.Zero);
                state = SessionState.Running;
                stopRequest = null;
                resume = null;
                log.Clear();
                runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }
            var token = runCts.Token;
            Append($"Session start: {newPlan.TargetName}, {newPlan.FrameCount} x {newPlan.ExposureSeconds:0.#} s, gain {newPlan.Gain}, bin {newPlan.Bin}");
            string stopReason = null;
            var failed = false;
            try {
                Append("Slewing to target");
                await mount.SlewToAsync(newPlan.RightAscensionHours, newPlan.DeclinationDegrees, token);
                Append("Centre: plate solving arrives with the engine (M6); assuming centred");
                for (var i = 1; i <= newPlan.FrameCount; i++) {
                    await WaitWhilePaused(token);
                    stopReason = StopRequested() ?? CheckLimits(newPlan);
                    if (stopReason != null) {
                        break;
                    }
                    var frame = await camera.ExposeAsync(new ExposureRequest(FrameType.Light, newPlan.ExposureSeconds, newPlan.Gain, newPlan.Offset, newPlan.Bin), token);
                    var file = layout.FramePath(FrameType.Light, newPlan.TargetName, newPlan.ExposureSeconds, newPlan.Gain, newPlan.Bin, frame.SensorTemperature, i,
                        frame.Completed - TimeSpan.FromSeconds(newPlan.ExposureSeconds), newPlan.Offset, camera.CoolerOn ? camera.TargetTemperature : null);
                    lock (lockobj) {
                        progress = new SessionProgress(i, newPlan.FrameCount, frame.Hfr, file, null, clock.Now - start);
                    }
                    Append($"Frame {i}/{newPlan.FrameCount}: HFR {frame.Hfr:0.00}, {frame.Stars} stars -> {System.IO.Path.GetFileName(file)}");
                    if (newPlan.DitherEvery > 0 && i % newPlan.DitherEvery == 0 && i < newPlan.FrameCount) {
                        await mount.DitherAsync(5, token);
                        Append("Dithered (mount pulse) and settled");
                    }
                }
                stopReason ??= "All frames taken";
            } catch (OperationCanceledException) {
                stopReason = StopRequested() ?? "Cancelled";
            } catch (DeviceLostException ex) {
                failed = true;
                stopReason = ex.Message;
            } catch (InvalidOperationException ex) {
                failed = true;
                stopReason = ex.Message;
            } finally {
                lock (lockobj) {
                    state = failed ? SessionState.Failed : SessionState.Finished;
                    progress = progress with { StopReason = stopReason, Elapsed = clock.Now - start };
                    runCts.Dispose();
                    runCts = null;
                    resume?.TrySetResult();
                    resume = null;
                }
                Append($"Session end: {stopReason}");
            }
        }

        public void Pause() {
            lock (lockobj) {
                if (state != SessionState.Running) {
                    return;
                }
                state = SessionState.Paused;
                resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            Append("Paused (after the current frame)");
        }

        public void Resume() {
            TaskCompletionSource toResume;
            lock (lockobj) {
                if (state != SessionState.Paused) {
                    return;
                }
                state = SessionState.Running;
                toResume = resume;
                resume = null;
            }
            toResume?.TrySetResult();
            Append("Resumed");
        }

        public void RequestStop(string reason) {
            TaskCompletionSource toResume;
            lock (lockobj) {
                if (state != SessionState.Running && state != SessionState.Paused) {
                    return;
                }
                stopRequest = string.IsNullOrWhiteSpace(reason) ? "Stopped by user" : reason;
                state = SessionState.Stopping;
                toResume = resume;
                resume = null;
                runCts?.Cancel();
            }
            toResume?.TrySetResult();
            Append($"Stop requested: {stopRequest}");
        }

        private async Task WaitWhilePaused(CancellationToken token) {
            Task wait;
            lock (lockobj) {
                wait = resume?.Task;
            }
            if (wait != null) {
                await wait.WaitAsync(token);
            }
        }

        private string StopRequested() {
            lock (lockobj) {
                return stopRequest;
            }
        }

        private string CheckLimits(SessionPlan p) {
            var alt = mount.Altitude;
            if (alt is { } a && a > p.MaxAltitude) {
                return $"Altitude {a:0.0}° passed the {p.MaxAltitude:0}° keyhole limit";
            }
            if (alt is { } b && b < p.MinAltitude) {
                return $"Altitude {b:0.0}° is below the {p.MinAltitude:0}° minimum";
            }
            if (p.StopAtDawn) {
                var site = settings().Site;
                var sun = SkyMath.SunAltitude(clock.Now, new GeoSite(site.LatitudeDegrees, site.LongitudeDegrees));
                if (sun > -18) {
                    return $"Astronomical twilight (Sun at {sun:0.0}°)";
                }
            }
            return null;
        }

        private void Append(string message) {
            lock (lockobj) {
                log.Add($"{clock.Now.ToOffset(TimeSpan.FromHours(settings().Site.UtcOffsetHours)):HH:mm:ss}  {message}");
                if (log.Count > MaxLogLines) {
                    log.RemoveAt(0);
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
