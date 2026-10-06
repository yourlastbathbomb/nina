#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Mac.Lx200;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200 {

    /// <summary>One pulse as it went out: requested length, and for host-timed moves the measured time between start and halt.</summary>
    public sealed record Lx200PulseRecord(GuideDirections Direction, int RequestedMs, Lx200PulseStrategy Strategy, DateTime StartUtc, double ActualMs);

    /// <summary>
    /// <see cref="Lx200Telescope.PulseGuide"/> in the three strategies of <see cref="Lx200PulseStrategy"/>. A pulse runs on the
    /// link; <see cref="IsPulseGuiding"/> is true from the call until the mount has stopped (plus <see cref="MountLatencyMargin"/>),
    /// which is what NINA's DirectGuider waits for after a dither (DirectGuider.cs:245-256). With
    /// <see cref="Lx200Settings.SerializePulseAxes"/> the second axis starts when the first ends, and every pulse call returns
    /// only once its pulse has gone out to the mount (for a queued second axis: after the first has ended), as ASCOM specifies
    /// for a mount that cannot move both axes at once. DirectGuider's own wait (the longer pulse, then the settle time) therefore
    /// starts after the last axis has started and cannot end before that axis has stopped, whatever the settle time and however
    /// stale the polled TelescopeInfo is (wave-7 M8 note). A pulse that nothing is ahead of returns after one link transaction
    /// (tens of milliseconds).
    /// </summary>
    internal sealed class Lx200PulseGuider : IDisposable {

        /// <summary>DirectGuider sends its two pulses back to back; the goto-offset strategy turns everything in this window into one goto.</summary>
        internal static readonly TimeSpan OffsetCoalesceWindow = TimeSpan.FromMilliseconds(150);

        /// <summary>
        /// The mount starts and stops a pulse a little after the link sends the command: about 1 ms per byte at 9600 baud, the
        /// FTDI latency timer (up to 16 ms) and the Autostar's own reaction. <see cref="IsPulseGuiding"/> stays true this much
        /// longer than the link's send time + duration, so a light cannot start while the mount may still be moving.
        /// </summary>
        internal static readonly TimeSpan MountLatencyMargin = TimeSpan.FromMilliseconds(50);

        /// <summary>A queued pulse's caller waits at most for what is ahead of it plus this, then returns anyway (and logs).</summary>
        internal static readonly TimeSpan StartWaitMargin = TimeSpan.FromSeconds(5);

        private const int MaxNativePulseMs = 9999;   // ":MgnDDDD#", four digits (P07 l.544)

        private readonly Lx200Telescope telescope;
        private readonly Lx200Link link;
        private readonly Lx200Settings settings;
        private readonly object sync = new();
        private readonly CancellationTokenSource cts = new();
        private readonly SemaphoreSlim offsetGate = new(1, 1);
        private readonly List<Lx200PulseRecord> history = new();
        private Task<DateTime> chain = Task.FromResult(DateTime.MinValue);
        private int active;
        private DateTime busyUntil = DateTime.MinValue;
        private double pendingEastArcsec;
        private double pendingNorthArcsec;
        private int pendingCount;
        private bool batchScheduled;
        private TaskCompletionSource offsetBatchDone;
        private long queuedMs;

        public Lx200PulseGuider(Lx200Telescope telescope, Lx200Link link, Lx200Settings settings) {
            this.telescope = telescope;
            this.link = link;
            this.settings = settings;
        }

        /// <summary>Resolved strategy (never Auto).</summary>
        public Lx200PulseStrategy Strategy { get; set; } = Lx200PulseStrategy.HostTimedMove;

        public bool IsPulseGuiding {
            get {
                lock (sync) {
                    return active > 0 || link.UtcNow < busyUntil;
                }
            }
        }

        public IReadOnlyList<Lx200PulseRecord> History {
            get {
                lock (sync) {
                    return history.ToArray();
                }
            }
        }

        /// <summary>
        /// Queues a pulse. With serialised axes it returns once this pulse has gone out to the mount, i.e. after the pulses ahead
        /// of it have ended, waiting at most their remaining time plus <see cref="StartWaitMargin"/>. Without serialisation it
        /// returns at once. With <see cref="Lx200PulseStrategy.GotoOffset"/>, a pulse that joins an offset goto already being
        /// collected returns when that goto has ended; the first returns at once.
        /// </summary>
        public void Pulse(GuideDirections direction, int durationMs) {
            if (durationMs <= 0) {
                return;
            }
            var strategy = Strategy;
            Task waitFor = null;
            var waitLimit = TimeSpan.Zero;
            lock (sync) {
                active++;
                if (strategy == Lx200PulseStrategy.GotoOffset) {
                    var joins = batchScheduled;
                    QueueOffset(direction, durationMs);
                    if (joins && settings.SerializePulseAxes) {
                        waitFor = offsetBatchDone?.Task;
                        waitLimit = TimeSpan.FromSeconds(Math.Max(1, settings.SlewTimeoutSeconds)) + StartWaitMargin;
                    }
                } else {
                    var serialize = settings.SerializePulseAxes;
                    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (serialize) {
                        var runningMs = Math.Max(0, (busyUntil - link.UtcNow).TotalMilliseconds);
                        waitFor = started.Task;
                        waitLimit = TimeSpan.FromMilliseconds(runningMs + queuedMs) + StartWaitMargin;
                    }
                    queuedMs += durationMs;
                    var previous = serialize ? chain : Task.FromResult(DateTime.MinValue);
                    var run = Run(previous, direction, durationMs, strategy, started);
                    chain = run;
                }
            }
            if (waitFor != null && !waitFor.Wait(waitLimit)) {
                Logger.Warning(string.Create(CultureInfo.InvariantCulture,
                    $"LX200: pulse {direction} {durationMs} ms has not started after {waitLimit.TotalSeconds:0.0} s behind the pulses ahead of it; returning (IsPulseGuiding stays true until it has run)"));
            }
        }

        private async Task<DateTime> Run(Task<DateTime> previous, GuideDirections direction, int ms, Lx200PulseStrategy strategy, TaskCompletionSource started) {
            try {
                DateTime? due = null;
                try {
                    // the previous pulse ends on the mount a little after its send time + duration (the command's own
                    // transmission and latency), so the next one goes out MountLatencyMargin later: never two axes at once
                    var previousEnd = await previous.ConfigureAwait(false);
                    if (previousEnd != DateTime.MinValue && previousEnd + MountLatencyMargin > link.UtcNow) {
                        due = previousEnd + MountLatencyMargin;
                    }
                } catch (Exception) {
                    // the previous pulse failed; this one starts now
                }
                var d = Letter(direction);
                return strategy == Lx200PulseStrategy.NativePulse
                    ? await Native(direction, d, ms, due, started).ConfigureAwait(false)
                    : await HostTimed(direction, d, ms, due, started).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error($"LX200: pulse {direction} {ms} ms ({strategy}) failed: {ex.Message}");
                return DateTime.MinValue;
            } finally {
                lock (sync) {
                    active--;
                    queuedMs -= ms;
                }
                started.TrySetResult();
            }
        }

        /// <summary>":Mg{d}DDDD#": the mount times it. Longer than 9999 ms goes out in pieces, each when the previous one ends.</summary>
        private async Task<DateTime> Native(GuideDirections direction, char d, int ms, DateTime? due, TaskCompletionSource started) {
            var remaining = ms;
            var end = DateTime.MinValue;
            while (remaining > 0) {
                var chunk = Math.Min(remaining, MaxNativePulseMs);
                var reply = await link.SendAsync(Lx200Format.PulseGuideCommand(d, chunk), Lx200Lane.Timed, cts.Token, dueUtc: due).ConfigureAwait(false);
                if (!reply.IsOk) {
                    throw new Lx200ReplyException($"pulse refused: {reply.Describe()}", reply);
                }
                end = reply.SentUtc + TimeSpan.FromMilliseconds(chunk);
                lock (sync) {
                    if (end + MountLatencyMargin > busyUntil) {
                        busyUntil = end + MountLatencyMargin;
                    }
                    history.Add(new Lx200PulseRecord(direction, chunk, Lx200PulseStrategy.NativePulse, reply.SentUtc, chunk));
                }
                started.TrySetResult();
                remaining -= chunk;
                due = end + MountLatencyMargin;
            }
            return end;
        }

        /// <summary>":RG#", ":M{d}#", then ":Q{d}#" scheduled on the link for start + ms (Timed lane, so polling cannot delay it).</summary>
        private async Task<DateTime> HostTimed(GuideDirections direction, char d, int ms, DateTime? due, TaskCompletionSource started) {
            // the guide rate first, every time: the handbox (or a manual move) may have left another rate selected
            var rate = await link.SendAsync(":RG#", Lx200Lane.Timed, cts.Token, dueUtc: due).ConfigureAwait(false);
            if (!rate.IsOk) {
                throw new Lx200ReplyException($"':RG#' {rate.Describe()}", rate);
            }
            var start = await link.SendAsync($":M{d}#", Lx200Lane.Timed, cts.Token, dueUtc: due).ConfigureAwait(false);
            if (!start.IsOk) {
                throw new Lx200ReplyException($"':M{d}#' {start.Describe()}", start);
            }
            var stopDue = start.SentUtc + TimeSpan.FromMilliseconds(ms);
            lock (sync) {
                if (stopDue + MountLatencyMargin > busyUntil) {
                    busyUntil = stopDue + MountLatencyMargin;
                }
            }
            started.TrySetResult();
            // not cancellable: once the move runs, its halt must go out (the link owes it across a reconnect)
            var stop = await link.SendAsync($":Q{d}#", Lx200Lane.Timed, CancellationToken.None, dueUtc: stopDue).ConfigureAwait(false);
            if (!stop.IsOk) {
                Logger.Warning($"LX200: ':Q{d}#' {stop.Describe()}; sending it again through the Stop lane");
                stop = await link.SendAsync($":Q{d}#", Lx200Lane.Stop).ConfigureAwait(false);
            }
            var actual = (stop.SentUtc - start.SentUtc).TotalMilliseconds;
            lock (sync) {
                busyUntil = stop.SentUtc + MountLatencyMargin;
                history.Add(new Lx200PulseRecord(direction, ms, Lx200PulseStrategy.HostTimedMove, start.SentUtc, actual));
            }
            if (Math.Abs(actual - ms) > 30) {
                Logger.Debug(string.Create(CultureInfo.InvariantCulture, $"LX200: host-timed pulse {direction} ran {actual:0} ms for {ms} ms requested"));
            }
            return stop.SentUtc;
        }

        /// <summary>Under the lock: adds duration x guide rate to the offset of the next goto, and schedules it.</summary>
        private void QueueOffset(GuideDirections direction, int ms) {
            var arcsec = ms / 1000.0 * settings.GuideRateArcsecPerSec;
            switch (direction) {
                case GuideDirections.guideNorth: pendingNorthArcsec += arcsec; break;
                case GuideDirections.guideSouth: pendingNorthArcsec -= arcsec; break;
                case GuideDirections.guideEast: pendingEastArcsec += arcsec; break;
                default: pendingEastArcsec -= arcsec; break;
            }
            pendingCount++;
            if (!batchScheduled) {
                batchScheduled = true;
                offsetBatchDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = RunOffsetBatch(offsetBatchDone);
            }
        }

        private async Task RunOffsetBatch(TaskCompletionSource done) {
            var count = 0;
            try {
                await Task.Delay(OffsetCoalesceWindow, cts.Token).ConfigureAwait(false);
                double east, north;
                lock (sync) {
                    east = pendingEastArcsec;
                    north = pendingNorthArcsec;
                    count = pendingCount;
                    pendingEastArcsec = pendingNorthArcsec = 0;
                    pendingCount = 0;
                    batchScheduled = false;
                }
                await offsetGate.WaitAsync(cts.Token).ConfigureAwait(false);
                var started = link.UtcNow;
                try {
                    await OffsetGoto(east, north, cts.Token).ConfigureAwait(false);
                } finally {
                    offsetGate.Release();
                }
                lock (sync) {
                    history.Add(new Lx200PulseRecord(GuideDirections.guideEast, (int)Math.Round(Math.Abs(east) / settings.GuideRateArcsecPerSec * 1000), Lx200PulseStrategy.GotoOffset, started, (link.UtcNow - started).TotalMilliseconds));
                }
            } catch (OperationCanceledException) {
                // disconnected
            } catch (Exception ex) {
                Logger.Error($"LX200: dither by goto offset failed: {ex.Message}");
            } finally {
                lock (sync) {
                    if (count == 0) {
                        count = pendingCount;
                        pendingCount = 0;
                        batchScheduled = false;
                    }
                    active -= count;
                }
                done.TrySetResult();
            }
        }

        /// <summary>
        /// Reads the position, adds the offset (east = RA increasing, north = Dec increasing) and slews there with ":Sr"/":Sd"/":MS#".
        /// RA is set in whole seconds of time (15"·cos Dec), so a smaller RA offset rounds away (RVM MNT-M4); the achieved offset is
        /// logged from the position read back.
        /// </summary>
        private async Task OffsetGoto(double eastArcsec, double northArcsec, CancellationToken token) {
            var (ra, dec) = await telescope.ReadPositionAsync(token).ConfigureAwait(false);
            var cosDec = Math.Max(0.01, Math.Cos(dec * Math.PI / 180.0));
            var newDec = Math.Clamp(dec + (northArcsec / 3600.0), -90, 90);
            var newRa = Lx200Astro.Wrap(ra + (eastArcsec / 3600.0 / 15.0 / cosDec), 24.0);
            var raStepSeconds = Math.Abs(eastArcsec) / 15.0 / cosDec;
            Logger.Info(string.Create(CultureInfo.InvariantCulture,
                $"LX200 dither by goto offset: {eastArcsec:+0.0;-0.0}\" east, {northArcsec:+0.0;-0.0}\" north (RA {raStepSeconds:0.00} s of time{(raStepSeconds < 0.5 && telescope.Precision == CoordinatePrecision.High ? ", rounds to no RA move" : "")})"));
            if (!await telescope.GotoAsync(newRa, newDec, token, "dither by goto offset").ConfigureAwait(false)) {
                throw new Lx200Exception("the offset goto did not complete");
            }
            var (ra2, dec2) = await telescope.ReadPositionAsync(token).ConfigureAwait(false);
            var achievedEast = Lx200Astro.HourDifference(ra2, ra) * 15.0 * 3600.0 * cosDec;
            var achievedNorth = (dec2 - dec) * 3600.0;
            Logger.Info(string.Create(CultureInfo.InvariantCulture, $"LX200 dither by goto offset: achieved {achievedEast:+0.0;-0.0}\" east, {achievedNorth:+0.0;-0.0}\" north"));
        }

        private static char Letter(GuideDirections direction) => direction switch {
            GuideDirections.guideNorth => 'n',
            GuideDirections.guideSouth => 's',
            GuideDirections.guideEast => 'e',
            _ => 'w'
        };

        public void Dispose() {
            try {
                cts.Cancel();
            } catch (ObjectDisposedException) {
            }
            lock (sync) {
                offsetBatchDone?.TrySetResult();
            }
        }
    }
}
