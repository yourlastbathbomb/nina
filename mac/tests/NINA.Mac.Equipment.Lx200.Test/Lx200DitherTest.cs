#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using Moq;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Mac.Lx200;
using NINA.Mac.Lx200.Sim;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200.Test {

    /// <summary>
    /// NINA's own mount dither (upstream <see cref="DirectGuider"/>, "Mount Dither", compiled unchanged in NINA.Equipment.Mac)
    /// moving the simulated LX200GPS through <see cref="Lx200Telescope.PulseGuide"/>, once per pulse strategy. The guider talks
    /// to the mount only through <see cref="ITelescopeMediator.PulseGuide"/> and the <see cref="TelescopeInfo"/> the host
    /// broadcasts (here every 100 ms, as TelescopeVM's update timer does every 2 s), exactly as in NINA. Each case checks that
    /// the mount moved by what the pulses that went out asked for, using the simulator's ground truth.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Lx200DitherTest {

        /// <summary>DirectGuider wired to the telescope through a mediator, plus the info pump.</summary>
        private sealed class MountDither : IDisposable {
            private readonly CancellationTokenSource stop = new();
            private readonly Task pump;
            private readonly List<(GuideDirections Direction, int Ms)> pulses = new();

            public MountDither(Rig rig) {
                var mediator = new Mock<ITelescopeMediator>();
                mediator.Setup(m => m.PulseGuide(It.IsAny<GuideDirections>(), It.IsAny<int>()))
                    .Callback<GuideDirections, int>((direction, ms) => {
                        lock (pulses) {
                            pulses.Add((direction, ms));
                        }
                        rig.Telescope.PulseGuide(direction, ms);
                    });
                Guider = new DirectGuider(rig.ProfileService.Object, mediator.Object);
                Guider.UpdateDeviceInfo(Info(rig.Telescope));
                pump = Task.Run(async () => {
                    while (!stop.IsCancellationRequested) {
                        var info = Info(rig.Telescope);
                        if (info.IsPulseGuiding) {
                            SawPulseGuiding = true;
                        }
                        Guider.UpdateDeviceInfo(info);
                        try {
                            await Task.Delay(100, stop.Token);
                        } catch (OperationCanceledException) {
                            break;
                        }
                    }
                });
            }

            public DirectGuider Guider { get; }

            public bool SawPulseGuiding { get; private set; }

            public IReadOnlyList<(GuideDirections Direction, int Ms)> Pulses {
                get {
                    lock (pulses) {
                        return pulses.ToArray();
                    }
                }
            }

            private static TelescopeInfo Info(Lx200Telescope t) => new TelescopeInfo {
                Connected = t.Connected,
                IsPulseGuiding = t.IsPulseGuiding,
                GuideRateRightAscensionArcsecPerSec = t.GuideRateRightAscensionArcsecPerSec,
                GuideRateDeclinationArcsecPerSec = t.GuideRateDeclinationArcsecPerSec,
                CanPulseGuide = t.CanPulseGuide
            };

            public void Dispose() {
                stop.Cancel();
                pump.Wait(TimeSpan.FromSeconds(2));
                Guider.Dispose();
            }
        }

        private static async Task<MountDither> ConnectedDither(Rig rig) {
            await rig.ConnectTelescope();
            var dither = new MountDither(rig);
            (await dither.Guider.Connect(CancellationToken.None)).Should().BeTrue("the mount is connected");
            dither.Guider.WestEastGuideRate.Should().Be(10.0, "the driver reports the ':Rg' rate it set");
            return dither;
        }

        [Test]
        public async Task NativePulse_DirectGuiderDither_MovesTheMountByThePulses() {
            // pulse=eq: the simulated firmware moves RA/Dec for ':Mg', so the expected shift is exact
            using var rig = new Rig(new SimOptions { PulseGuide = PulseGuideBehaviour.EquatorialAxes, PlanetaryUpdateSeconds = 0.2 },
                                    configure: s => s.PulseStrategy = Lx200PulseStrategy.NativePulse);
            rig.Profile.GuiderSettings.DitherPixels = 20;
            rig.Profile.GuiderSettings.MountDitherMinimumPixels = 10;
            using var dither = await ConnectedDither(rig);

            for (var i = 0; i < 2; i++) {
                var (ra0, dec0) = rig.Sim.BelievedRaDec;
                var sent = rig.Received.Count;
                (await dither.Guider.Dither(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20))).Should().BeTrue();
                rig.Telescope.IsPulseGuiding.Should().BeFalse();

                var mg = rig.Received.Skip(sent).Where(c => c.StartsWith(":Mg", StringComparison.Ordinal)).ToList();
                mg.Should().HaveCount(dither.Pulses.Skip(2 * i).Count(p => p.Ms > 0), "one ':Mg' per non-zero DirectGuider pulse");
                double north = 0, east = 0;
                foreach (var c in mg) {
                    var ms = int.Parse(c[4..^1], CultureInfo.InvariantCulture);
                    var arcsec = ms / 1000.0 * 10.0;
                    switch (c[3]) {
                        case 'n': north += arcsec; break;
                        case 's': north -= arcsec; break;
                        case 'e': east += arcsec; break;
                        case 'w': east -= arcsec; break;
                    }
                }
                (Math.Abs(north) + Math.Abs(east)).Should().BeGreaterThan(0);
                var (ra1, dec1) = rig.Sim.BelievedRaDec;
                ((dec1 - dec0) * 3600).Should().BeApproximately(north, 0.1, "the mount moved north/south by the pulses");
                (Lx200Astro.HourDifference(ra1, ra0) * 15 * 3600).Should().BeApproximately(east, 0.1, "and east/west (RA degrees)");
            }
            dither.SawPulseGuiding.Should().BeTrue("IsPulseGuiding is true while a pulse runs");
            rig.Received.Should().NotContain(c => c.StartsWith(":Mn", StringComparison.Ordinal) || c == ":Me#" || c == ":Mw#" || c == ":Ms#" || c == ":MS#",
                "the native strategy uses ':Mg' only");
        }

        [Test]
        public async Task HostTimedMove_DirectGuiderDither_MovesTheMountByTheTimedMoves() {
            using var rig = new Rig(configure: s => s.PulseStrategy = Lx200PulseStrategy.HostTimedMove);
            rig.Profile.GuiderSettings.DitherPixels = 20;
            rig.Profile.GuiderSettings.MountDitherMinimumPixels = 10;
            using var dither = await ConnectedDither(rig);

            for (var i = 0; i < 2; i++) {
                var (ra0, dec0) = rig.Sim.BelievedRaDec;
                var logStart = rig.SimLog.Count;
                (await dither.Guider.Dither(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20))).Should().BeTrue();
                rig.Telescope.IsPulseGuiding.Should().BeFalse();

                // the simulator moves an axis from the ':Mx#' it receives until the ':Qx#'
                var log = rig.SimLog.Skip(logStart).ToList();
                double dAlt = 0, dAz = 0;
                foreach (var dir in "nsew") {
                    var starts = log.Where(e => e.Command == $":M{dir}#").ToList();
                    var stops = log.Where(e => e.Command == $":Q{dir}#").ToList();
                    starts.Count.Should().Be(stops.Count, $"every ':M{dir}#' has its ':Q{dir}#'");
                    for (var k = 0; k < starts.Count; k++) {
                        var seconds = (stops[k].Utc - starts[k].Utc).TotalSeconds;
                        var deg = seconds * 10.0 / 3600.0;
                        switch (dir) {
                            case 'n': dAlt += deg; break;
                            case 's': dAlt -= deg; break;
                            case 'e': dAz += deg; break;
                            case 'w': dAz -= deg; break;
                        }
                    }
                }
                log.Should().Contain(e => e.Command == ":RG#", "the guide rate is selected before each move");
                var requested = dither.Pulses.Skip(2 * i).Sum(p => p.Ms);
                var ran = log.Where(e => e.Command.StartsWith(":Q", StringComparison.Ordinal)).Count();
                ran.Should().BeGreaterThan(0);

                // expected axes: the start position carried forward by tracking, plus the timed moves
                var lst = Lx200Astro.LstHours(rig.Sim.MountUtc, rig.Sim.LongitudeEast);
                var (alt0, az0) = Lx200Astro.ToAltAz(ra0, dec0, lst, 22.25);
                var axes = rig.Sim.Axes;
                ((axes.Alt - alt0) * 3600).Should().BeApproximately(dAlt * 3600, 0.5, "altitude moved by the north/south moves");
                (Lx200Astro.DegreeDifference(axes.Az, az0) * 3600).Should().BeApproximately(dAz * 3600, 0.5, "azimuth by the east/west moves");
                (Math.Abs(dAlt) + Math.Abs(dAz)).Should().BeGreaterThan(0);
                TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"dither {i}: requested {requested} ms of pulses, moved alt {dAlt * 3600:+0.0;-0.0}\" az {dAz * 3600:+0.0;-0.0}\""));
            }
            dither.SawPulseGuiding.Should().BeTrue();
            rig.Received.Should().NotContain(c => c.StartsWith(":Mg", StringComparison.Ordinal) || c == ":MS#", "host-timed moves only");
        }

        [Test]
        public async Task GotoOffset_DirectGuiderDither_MovesTheMountByTheOffset() {
            using var rig = new Rig(configure: s => s.PulseStrategy = Lx200PulseStrategy.GotoOffset);
            // large dithers: RA goes in whole seconds of time (about 14" here), RVM MNT-M4
            rig.Profile.GuiderSettings.DitherPixels = 150;
            rig.Profile.GuiderSettings.MountDitherMinimumPixels = 100;
            using var dither = await ConnectedDither(rig);

            for (var i = 0; i < 2; i++) {
                var (ra0, dec0) = rig.Sim.BelievedRaDec;
                var sent = rig.Received.Count;
                (await dither.Guider.Dither(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30))).Should().BeTrue();
                rig.Telescope.IsPulseGuiding.Should().BeFalse("IsPulseGuiding lasts until the offset goto has ended");

                var pulses = dither.Pulses.Skip(2 * i).ToList();
                double north = 0, east = 0;
                foreach (var p in pulses) {
                    var arcsec = p.Ms / 1000.0 * 10.0;
                    switch (p.Direction) {
                        case GuideDirections.guideNorth: north += arcsec; break;
                        case GuideDirections.guideSouth: north -= arcsec; break;
                        case GuideDirections.guideEast: east += arcsec; break;
                        case GuideDirections.guideWest: east -= arcsec; break;
                    }
                }
                var commands = rig.Received.Skip(sent).ToList();
                commands.Count(c => c == ":MS#").Should().Be(1, "both DirectGuider pulses become one goto");
                var sr = Lx200Format.ParseRaHours(commands.Last(c => c.StartsWith(":Sr", StringComparison.Ordinal))[3..^1]);
                var sd = Lx200Format.ParseDegrees(commands.Last(c => c.StartsWith(":Sd", StringComparison.Ordinal))[3..^1]);
                var (ra1, dec1) = rig.Sim.BelievedRaDec;
                Lx200Astro.SeparationDeg(ra1, dec1, sr, sd).Should().BeLessThan(1.0 / 3600, "the mount went to the offset target");
                var cosDec = Math.Cos(dec0 * Math.PI / 180);
                ((dec1 - dec0) * 3600).Should().BeApproximately(north, 1.5, "Dec offset (1\" steps, plus the 0.5\" rounding of the start)");
                (Lx200Astro.HourDifference(ra1, ra0) * 15 * 3600 * cosDec).Should().BeApproximately(east, (15 * cosDec) + 1, "RA offset (1 s of time steps)");
                TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"dither {i}: asked {east:+0.0;-0.0}\" E {north:+0.0;-0.0}\" N"));
            }
            rig.Received.Should().NotContain(c => c.StartsWith(":Mg", StringComparison.Ordinal) || c == ":Mn#" || c == ":Me#", "goto offsets only");
        }

        [Test]
        public async Task Auto_OnStockFirmware_DithersWithHostTimedMoves() {
            using var rig = new Rig();
            rig.Profile.GuiderSettings.DitherPixels = 10;
            rig.Profile.GuiderSettings.MountDitherMinimumPixels = 5;
            using var dither = await ConnectedDither(rig);
            (await dither.Guider.Dither(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20))).Should().BeTrue();
            rig.Telescope.EffectivePulseStrategy.Should().Be(Lx200PulseStrategy.HostTimedMove);
            rig.Received.Should().Contain(c => c == ":Mn#" || c == ":Ms#" || c == ":Me#" || c == ":Mw#");
        }

        [Test]
        public async Task HostTimedPulses_AreTimedToTheMillisecond_AndTheSecondAxisWaitsForTheFirst_EvenUnderPolling() {
            // a 9600-baud cable with 15 ms turnaround and the mount polled every 100 ms: the moves still start and stop on time
            using var rig = new Rig(configure: s => {
                s.PulseStrategy = Lx200PulseStrategy.HostTimedMove;
                s.PollIntervalMs = 100;
            }, baudRate: 9600, turnaround: TimeSpan.FromMilliseconds(15));
            var mount = await rig.ConnectTelescope();
            await Task.Delay(300);

            var asked = rig.Link.UtcNow;
            mount.PulseGuide(GuideDirections.guideNorth, 300);
            mount.PulseGuide(GuideDirections.guideEast, 200);
            mount.IsPulseGuiding.Should().BeTrue("from the call on");
            (await Rig.Eventually(() => !mount.IsPulseGuiding, TimeSpan.FromSeconds(5))).Should().BeTrue();

            var tx = rig.Transmitted();
            var rg = tx.First(t => t.Utc >= asked && t.Text == ":RG#");
            var mn = tx.First(t => t.Utc >= asked && t.Text == ":Mn#");
            var qn = tx.First(t => t.Utc >= asked && t.Text == ":Qn#");
            var me = tx.First(t => t.Utc >= asked && t.Text == ":Me#");
            var qe = tx.First(t => t.Utc >= asked && t.Text == ":Qe#");
            var latency = (rg.Utc - asked).TotalMilliseconds;
            var north = (qn.Utc - mn.Utc).TotalMilliseconds;
            var east = (qe.Utc - me.Utc).TotalMilliseconds;
            TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"start latency {latency:0} ms; north ran {north:0.0} ms of 300, east {east:0.0} ms of 200"));
            latency.Should().BeLessThan(100, "the pulse jumps ahead of the polls");
            north.Should().BeInRange(299, 315);
            east.Should().BeInRange(199, 215);
            me.Utc.Should().BeOnOrAfter(qn.Utc, "the east move starts when the north move has stopped");
        }
    }
}
