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
using NINA.Core.Utility;
using NINA.Equipment.Exceptions;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Mac.App.Services;
using NINA.Mac.Sequencing.Planning;
using NINA.Mac.Sequencing.Runner;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SirilLayout = NINA.Mac.Siril.SessionLayout;

namespace NINA.Mac.App.Engine {

    /// <summary>
    /// A run through NINA's own sequencer: the Target form's plan becomes a NightPlan (NINA.Mac.Sequencing), the generator builds
    /// NINA's sequence tree (centre or slew, lights with mount dithers, drift recentring, stops at the 75° keyhole, the minimum
    /// altitude and dawn), and NINA's Sequencer runs it headless against the host's devices. Frames are saved by NINA's
    /// ImageSaveController through the profile's file patterns (NINA.Mac.Siril's layout). Pause works between frames: a gate item
    /// before every light holds the loop while paused, so NINA's conditions (keyhole, horizon, dawn) still end the target.
    /// Stop cancels the run, which aborts the current exposure. Cooling, warming and parking stay with the Cool and Teardown
    /// screens, so the generated night leaves the cooler and the mount alone.
    /// </summary>
    public sealed class EngineSessionService : ISessionService, IDisposable {
        private const int MaxLogLines = 200;

        /// <summary>How long a run waits for a focus or calibration frame of the camera service to end before it starts.</summary>
        private static readonly TimeSpan CameraReleaseWait = TimeSpan.FromSeconds(15);

        private readonly EngineDevices engine;
        private readonly object lockobj = new();
        private readonly List<string> log = new();
        private SessionState state = SessionState.Idle;
        private SessionPlan plan;
        private SessionProgress progress = SessionProgress.None;
        private IImageFolders layout;
        private string targetName;
        private string stopRequest;
        private CancellationTokenSource runCts;
        private TaskCompletionSource resume;
        private TaskCompletionSource idle;
        private int analysing;
        private int lightsPrepared;
        private int savesFailed;
        private int downloadsFailed;
        private string lastDownloadError;
        private bool lastLightFailed;
        private volatile bool ownsCamera;
        private DateTimeOffset start;

        internal EngineSessionService(EngineDevices engine) {
            this.engine = engine;
            engine.Host.ImageSaveMediator.ImageSaved += OnImageSaved;
            engine.Host.ImagingMediator.ImagePrepared += OnImagePrepared;
            engine.Host.ImageSaveMediator.ImageSaveFailed += OnImageSaveFailed;
            engine.Host.ApplicationStatus.StatusUpdated += OnStatus;
            EngineRuntime.NotificationPosted += OnNotification;
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

        /// <summary>A run is running, paused or stopping.</summary>
        internal bool IsActive => State is SessionState.Running or SessionState.Paused or SessionState.Stopping;

        /// <summary>From the start of <see cref="RunAsync"/> to its end: the camera service refuses frames of its own.</summary>
        internal bool OwnsCamera => ownsCamera;

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

        /// <summary>The sequence tree of the current or last run (for tests and diagnostics).</summary>
        public SequenceRootContainer LastSequence { get; private set; }

        /// <summary>The NightPlan the Target form became (for tests and diagnostics).</summary>
        public NightPlan LastNightPlan { get; private set; }

        /// <summary>Turns the Target form's plan into the generator's NightPlan.</summary>
        public NightPlan ToNightPlan(SessionPlan p) {
            var settings = engine.Settings;
            var name = SequenceTargetName(p.TargetName);
            var target = new TargetPlan {
                Name = name,
                Coordinates = new Coordinates(Angle.ByHours(p.RightAscensionHours), Angle.ByDegree(p.DeclinationDegrees), Epoch.J2000),
                ExposureSeconds = p.ExposureSeconds,
                Gain = p.Gain,
                Offset = p.Offset,
                Binning = (short)Math.Clamp(p.Bin, 1, 4),
                Count = p.FrameCount,
                DitherEvery = Math.Max(0, p.DitherEvery),
                CenterFirst = settings.CentreBeforeRun && CanCentre,
                RecenterArcmin = CanCentre ? Math.Max(0, settings.Solver?.RecenterArcmin ?? 0) : 0,
                MinAltitudeDeg = p.MinAltitude > 0 ? p.MinAltitude : null,
                MaxAltitudeDeg = p.MaxAltitude,
                BlurTolerancePx = settings.FieldRotationBlurPixels > 0 ? settings.FieldRotationBlurPixels : 1.0,
            };
            return new NightPlan {
                Name = name,
                Targets = new[] { target },
                // The Cool and Teardown screens own the cooler and the park
                CoolToC = null,
                WarmAtEnd = false,
                ParkAtEnd = false,
                // The ASI585's anti-dew heater write is unverified on this rig (M1 notes: NINA logs failed ASI_ANTI_DEW_HEATER writes)
                DewHeater = false,
                UnparkAtStart = true,
                // NINA always ends the night at a dawn: without "stop at dawn" the latest one (civil) is used
                Dawn = p.StopAtDawn ? DawnStop.Astronomical : DawnStop.Civil,
                ReconnectOnDownloadFailure = true,
            };
        }

        /// <summary>
        /// NINA.Mac.Sequencing's PlanValidator over the plan as it would run now: the target's window between dusk and the dawn
        /// stop, with the profile's horizon, the keyhole and the field-rotation limit; warnings such as "Holds the sequence until
        /// dawn" and "Never reached" come through unchanged.
        /// </summary>
        public PlanCheck CheckPlan(SessionPlan p) {
            ArgumentNullException.ThrowIfNull(p);
            try {
                var night = ToNightPlan(p);
                var report = engine.Host.Validator.Validate(night);
                var warnings = report.Issues.Where(i => i.Severity == PlanIssueSeverity.Warning).Select(i => i.Message).ToList();
                var errors = report.Issues.Where(i => i.Severity == PlanIssueSeverity.Error).Select(i => i.Message).ToList();
                var w = report.Windows.FirstOrDefault();
                string summary;
                if (w?.ExpectedStart is DateTime from && w.ExpectedEnd is DateTime to) {
                    summary = $"Tonight (dark {report.Dusk:HH:mm}-{report.Dawn:HH:mm}): expected to image {from:HH:mm}-{to:HH:mm}, ending at {Describe(w.ExpectedStop)}; peak altitude {w.PeakAltitudeDeg:0}° at {w.PeakTime:HH:mm}";
                } else if (w?.Start is DateTime start && w.End is DateTime end) {
                    summary = $"Tonight (dark {report.Dusk:HH:mm}-{report.Dawn:HH:mm}): observable {start:HH:mm}-{end:HH:mm}";
                } else {
                    summary = $"Tonight (dark {report.Dusk:HH:mm}-{report.Dawn:HH:mm}): not observable under the limits and the horizon";
                }
                return new PlanCheck(warnings, errors, summary);
            } catch (Exception ex) {
                // A check must never stop the operator from seeing the Run screen; the run itself validates again
                Logger.Error("Nightglass: plan check failed", ex);
                return new PlanCheck(Array.Empty<string>(), new[] { $"The plan check failed: {ex.Message}" }, null);
            }
        }

        private static string Describe(ExpectedStop stop) => stop switch {
            ExpectedStop.Count => "the frame count",
            ExpectedStop.Horizon => "the horizon",
            ExpectedStop.MinimumAltitude => "the minimum altitude",
            ExpectedStop.Keyhole => "the keyhole (maximum altitude)",
            ExpectedStop.FieldRotation => "the field-rotation limit",
            ExpectedStop.Dawn => "dawn",
            _ => "not imaged",
        };

        public async Task RunAsync(SessionPlan newPlan, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(newPlan);
            if (newPlan.FrameCount < 1 || newPlan.ExposureSeconds <= 0) {
                throw new ArgumentException("Plan needs at least one frame and a positive exposure", nameof(newPlan));
            }
            lock (lockobj) {
                if (ownsCamera || state is SessionState.Running or SessionState.Paused or SessionState.Stopping) {
                    throw new InvalidOperationException("A session is already running");
                }
                // From here the camera belongs to the run: a focus loop's next frame is refused, so none slips between lights
                ownsCamera = true;
            }
            try {
                await RunOwnedAsync(newPlan, ct);
            } finally {
                ownsCamera = false;
            }
        }

        private async Task RunOwnedAsync(SessionPlan newPlan, CancellationToken ct) {
            if (engine.Camera.State != DeviceConnectionState.Connected) {
                throw new InvalidOperationException("Connect the camera first");
            }
            if (engine.Mount.State != DeviceConnectionState.Connected) {
                throw new InvalidOperationException("Connect the mount first");
            }

            // NINA's pre-flight check wants the image folder to exist (the layout below it is created per frame)
            System.IO.Directory.CreateDirectory(engine.ImagesRoot);
            var night = ToNightPlan(newPlan);
            var root = engine.Host.Generator.Generate(night);
            var gates = InsertPauseGates(root);
            var runner = new HeadlessSequenceRunner(root);
            var issues = runner.Validate();
            var report = engine.Host.Validator.Validate(night);

            CancellationTokenSource cts;
            lock (lockobj) {
                plan = newPlan;
                targetName = night.Targets[0].Name;
                start = engine.Clock.Now;
                layout = engine.CurrentFolders();
                progress = new SessionProgress(0, newPlan.FrameCount, null, null, null, TimeSpan.Zero);
                stopRequest = null;
                resume = null;
                lightsPrepared = 0;
                savesFailed = 0;
                downloadsFailed = 0;
                lastDownloadError = null;
                lastLightFailed = false;
                idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                log.Clear();
                runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts = runCts;
                state = SessionState.Running;
                LastSequence = root;
                LastNightPlan = night;
            }
            Append($"Session start: {targetName}, {newPlan.FrameCount} x {newPlan.ExposureSeconds:0.#} s, gain {newPlan.Gain}, bin {newPlan.Bin}, dither every {newPlan.DitherEvery} (NINA sequencer)");
            Append(night.Targets[0].CenterFirst ? "Centre by plate solving first" : $"Slew only ({CentreNote()})");
            foreach (var issue in report.Issues) {
                Append($"Plan {issue}");
            }
            string stopReason = null;
            var failed = false;
            root.FailureEvent += OnSequenceFailure;
            try {
                if (issues.Count > 0 || report.HasErrors) {
                    foreach (var issue in issues) {
                        Append($"Check: {issue}");
                    }
                    failed = true;
                    stopReason = issues.Count > 0 ? $"Pre-flight check failed: {issues[0]}" : "The plan has errors";
                    return;
                }
                Append($"Sequence: {gates} light loop(s), frames to {Layout.LightsDirectory(targetName)}");
                // A focus or calibration frame still going ends first; the lights must not inherit its ZWO mono-bin
                if (engine.Camera.OwnExposureRemaining is { } busy) {
                    Append($"Waiting for the camera's current frame to finish ({busy.TotalSeconds:0} s left)");
                }
                await engine.Camera.PrepareForRunAsync(CameraReleaseWait, cts.Token);
                await runner.RunAsync(new Progress<ApplicationStatus>(), cts.Token);
                // NINA's ImageSaveController writes each light after its sequence item returns: let the last ones land
                await WaitForSavesAsync(TimeSpan.FromSeconds(60));
                stopReason = StopRequested() ?? (cts.IsCancellationRequested ? "Cancelled" : EndReason(newPlan));
            } catch (OperationCanceledException) {
                stopReason = StopRequested() ?? "Cancelled";
            } catch (Exception ex) {
                Logger.Error("Nightglass: the sequence failed", ex);
                failed = true;
                stopReason = ex.Message;
            } finally {
                root.FailureEvent -= OnSequenceFailure;
                TaskCompletionSource done;
                lock (lockobj) {
                    state = failed ? SessionState.Failed : SessionState.Finished;
                    progress = progress with { StopReason = stopReason, Elapsed = engine.Clock.Now - start };
                    runCts?.Dispose();
                    runCts = null;
                    resume?.TrySetResult();
                    resume = null;
                    done = idle;
                }
                Append($"Session end: {stopReason}");
                done?.TrySetResult();
                try {
                    engine.ApplyPendingSettings();
                } catch (Exception ex) {
                    Logger.Error("Nightglass: applying the settings saved during the run failed", ex);
                }
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
            Append("Paused (after the current frame; limits still apply)");
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

        /// <summary>Completes when no run is active (at once if none), or after <paramref name="timeout"/>.</summary>
        public async Task WaitForIdleAsync(TimeSpan timeout) {
            Task wait;
            lock (lockobj) {
                wait = state is SessionState.Running or SessionState.Paused or SessionState.Stopping ? idle?.Task : null;
            }
            if (wait != null) {
                await Task.WhenAny(wait, Task.Delay(timeout));
            }
        }

        public void Dispose() {
            engine.Host.ImageSaveMediator.ImageSaved -= OnImageSaved;
            engine.Host.ImagingMediator.ImagePrepared -= OnImagePrepared;
            engine.Host.ImageSaveMediator.ImageSaveFailed -= OnImageSaveFailed;
            engine.Host.ApplicationStatus.StatusUpdated -= OnStatus;
            EngineRuntime.NotificationPosted -= OnNotification;
        }

        private bool CanCentre => engine.Centring.CanPlateSolve;

        private string CentreNote() => !engine.Settings.CentreBeforeRun ? "centring is off in Settings" : engine.Centring.PlateSolveProblem ?? "no solver";

        /// <summary>
        /// NINA writes frames under its own sanitised target name; a name the Siril layout would sanitise differently ('$', a
        /// leading '.') is sanitised the layout's way first, so the frames land where the Run screen and Siril look.
        /// </summary>
        public static string SequenceTargetName(string name) {
            if (string.IsNullOrWhiteSpace(name)) {
                return SirilLayout.UntitledTarget;
            }
            return SirilLayout.KeepsNinaFolderName(name) ? name.Trim() : SirilLayout.SanitizeFolderName(name);
        }

        /// <summary>Puts a pause gate in front of every light exposure; returns how many.</summary>
        private int InsertPauseGates(SequenceRootContainer root) {
            var exposures = new List<TakeExposure>();
            HeadlessSequenceRunner.Walk(root, entity => {
                if (entity is TakeExposure exposure) {
                    exposures.Add(exposure);
                }
            });
            foreach (var exposure in exposures) {
                if (exposure.Parent is SequenceContainer parent) {
                    var index = parent.GetItemsSnapshot().ToList().IndexOf(exposure);
                    parent.InsertIntoSequenceBlocks(index, new PauseGate(WaitWhilePaused));
                }
            }
            return exposures.Count;
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

        private async Task WaitForSavesAsync(TimeSpan timeout) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                lock (lockobj) {
                    if (progress.FramesDone + savesFailed >= lightsPrepared) {
                        return;
                    }
                }
                await Task.Delay(100);
            }
            Append("Some frames were still being saved when the run ended");
        }

        /// <summary>
        /// A light whose download failed: NINA's ReconnectOnDownloadFailure reconnects the camera before the next item, and the
        /// light loop gets one more iteration so the frame is taken again. A retry that fails too is not retried again (the
        /// plan's "reconnect and retry once"), so a camera that keeps failing cannot extend the run forever.
        /// </summary>
        private Task OnSequenceFailure(object sender, SequenceEntityFailureEventArgs e) {
            if (e?.Entity is not TakeExposure exposure || e.Exception is not (CameraDownloadFailedException or CameraExposureFailedException)) {
                return Task.CompletedTask;
            }
            bool retry;
            lock (lockobj) {
                downloadsFailed++;
                lastDownloadError = e.Exception.Message;
                retry = !lastLightFailed;
                lastLightFailed = true;
            }
            var loop = (exposure.Parent as SequenceContainer)?.Conditions.OfType<LoopCondition>().FirstOrDefault();
            if (retry && loop != null) {
                loop.Iterations += 1;
                Append($"Download failed ({e.Exception.Message}): the camera is reconnected and the frame taken again");
            } else {
                Append($"Download failed again ({e.Exception.Message}): the frame is skipped");
            }
            return Task.CompletedTask;
        }

        private Task OnImageSaveFailed(object sender, ImageSaveFailedEventArgs e) {
            lock (lockobj) {
                savesFailed++;
            }
            Append($"Saving a frame failed: {e?.Exception?.Message}");
            return Task.CompletedTask;
        }

        private string StopRequested() {
            lock (lockobj) {
                return stopRequest;
            }
        }

        /// <summary>Why NINA's sequence ended before every frame was taken (its conditions do not report a reason).</summary>
        private string EndReason(SessionPlan p) {
            var done = Progress.FramesDone;
            if (done >= p.FrameCount) {
                return "All frames taken";
            }
            try {
                var a = engine.Profile.ActiveProfile.AstrometrySettings;
                var target = new Coordinates(Angle.ByHours(p.RightAscensionHours), Angle.ByDegree(p.DeclinationDegrees), Epoch.J2000);
                var alt = target.Transform(Angle.ByDegree(a.Latitude), Angle.ByDegree(a.Longitude), a.Elevation).Altitude.Degree;
                if (alt > p.MaxAltitude) {
                    return $"Altitude {alt:0.0}° passed the {p.MaxAltitude:0}° keyhole limit";
                }
                if (alt < Math.Max(0, p.MinAltitude)) {
                    return $"Altitude {alt:0.0}° is below the {p.MinAltitude:0}° minimum";
                }
                var night = engine.Host.NighttimeCalculator.Calculate();
                var dawn = (p.StopAtDawn ? night.TwilightRiseAndSet : night.CivilTwilightRiseAndSet)?.Rise;
                if (dawn is DateTime d && DateTime.Now >= d.AddMinutes(-1)) {
                    return p.StopAtDawn ? $"Astronomical dawn ({d:HH:mm})" : $"Civil dawn ({d:HH:mm})";
                }
            } catch (Exception ex) {
                Logger.Error("Nightglass: working out why the run ended", ex);
            }
            int failedDownloads;
            string downloadError;
            lock (lockobj) {
                failedDownloads = downloadsFailed;
                downloadError = lastDownloadError;
            }
            if (failedDownloads > 0) {
                return $"Sequence ended after {done} of {p.FrameCount} frames: {failedDownloads} download{(failedDownloads == 1 ? "" : "s")} failed ({downloadError})";
            }
            return $"Sequence ended after {done} of {p.FrameCount} frames (see the log)";
        }

        private void OnImageSaved(object sender, ImageSavedEventArgs e) {
            if (e?.MetaData?.Image?.ImageType != CaptureSequence.ImageTypes.LIGHT) {
                return;
            }
            lock (lockobj) {
                if (state is not (SessionState.Running or SessionState.Paused or SessionState.Stopping)) {
                    return;
                }
                var file = e.PathToImage?.IsFile == true ? e.PathToImage.LocalPath : e.PathToImage?.ToString();
                progress = progress with {
                    FramesDone = progress.FramesDone + 1,
                    LastFile = file,
                    Elapsed = engine.Clock.Now - start,
                };
            }
            Append($"Frame {Progress.FramesDone}/{Progress.FrameCount} -> {System.IO.Path.GetFileName(Progress.LastFile)}");
        }

        /// <summary>HFR of every light the sequence prepares, measured off the UI thread; a frame is skipped if the last is still being measured.</summary>
        private void OnImagePrepared(object sender, ImagePreparedEventArgs e) {
            var raw = e?.RenderedImage?.RawImageData;
            if (raw == null || raw.MetaData?.Image?.ImageType != CaptureSequence.ImageTypes.LIGHT) {
                return;
            }
            lock (lockobj) {
                if (state is SessionState.Running or SessionState.Paused or SessionState.Stopping) {
                    lightsPrepared++;
                    lastLightFailed = false;
                }
            }
            if (Interlocked.Exchange(ref analysing, 1) == 1) {
                return;
            }
            var profile = engine.Profile.ActiveProfile;
            var pixelSize = profile.CameraSettings.PixelSize;
            var focalLength = profile.TelescopeSettings.FocalLength;
            _ = Task.Run(() => {
                try {
                    var p = raw.Properties;
                    var m = FrameAnalysisService.Measure(raw.Data.FlatArray, p.Width, p.Height, p.BitDepth, p.IsBayered, true, false, pixelSize, focalLength);
                    if (!double.IsNaN(m.Hfr)) {
                        lock (lockobj) {
                            progress = progress with { LastHfr = Math.Round(m.Hfr, 2) };
                        }
                        RaiseChanged();
                    }
                } catch (Exception ex) {
                    Logger.Error("Nightglass: measuring a light failed", ex);
                } finally {
                    Interlocked.Exchange(ref analysing, 0);
                }
            });
        }

        private string lastStatus;

        private void OnStatus(object sender, ApplicationStatus status) {
            if (string.IsNullOrWhiteSpace(status?.Status) || State is not (SessionState.Running or SessionState.Paused or SessionState.Stopping)) {
                return;
            }
            var line = $"{status.Source}: {status.Status}";
            lock (lockobj) {
                if (line == lastStatus) {
                    return;
                }
                lastStatus = line;
            }
            // NINA's progress chatter (exposure countdowns and the like) only changes the numbers, not the text
            Append(line);
        }

        private void OnNotification(object sender, EngineNotification n) {
            if (State is not (SessionState.Running or SessionState.Paused or SessionState.Stopping)) {
                return;
            }
            if (n.Kind is NINA.Core.Utility.Notification.NotificationKind.Error or NINA.Core.Utility.Notification.NotificationKind.Warning
                or NINA.Core.Utility.Notification.NotificationKind.ExternalError or NINA.Core.Utility.Notification.NotificationKind.ExternalWarning) {
                Append($"{n.Kind}: {n.Message}");
            }
        }

        private void Append(string message) {
            lock (lockobj) {
                var offset = TimeSpan.FromHours(engine.Settings.Site.UtcOffsetHours);
                log.Add($"{engine.Clock.Now.ToOffset(offset):HH:mm:ss}  {message}");
                if (log.Count > MaxLogLines) {
                    log.RemoveAt(0);
                }
            }
            RaiseChanged();
        }

        private void RaiseChanged() => engine.Ui.Post(() => Changed?.Invoke(this, EventArgs.Empty));

        /// <summary>A sequence item that holds the loop while the session is paused (inserted before each light).</summary>
        private sealed class PauseGate : SequenceItem {
            private readonly Func<CancellationToken, Task> wait;

            public PauseGate(Func<CancellationToken, Task> wait) {
                this.wait = wait;
                Name = "Pause gate (Nightglass)";
                Category = "Nightglass";
                Description = "Holds the loop while the run is paused";
            }

            private PauseGate(PauseGate cloneMe) : base(cloneMe) {
                wait = cloneMe.wait;
            }

            public override object Clone() => new PauseGate(this);

            public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) => wait(token);
        }
    }
}
