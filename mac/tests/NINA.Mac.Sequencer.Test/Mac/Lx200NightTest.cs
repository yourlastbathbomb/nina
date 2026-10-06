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
using NINA.Astrometry;
using NINA.Equipment.Interfaces;
using NINA.Mac.Equipment.Lx200;
using NINA.Mac.Equipment.Lx200.Test;
using NINA.Mac.Lx200.Sim;
using NINA.Mac.Sequencer.Test.Sim;
using NINA.Mac.Sequencing.Planning;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// A generated night through the real M4 driver: NINA's TelescopeVM drives Lx200Telescope (NINA.Mac.Equipment.Lx200, from
    /// Lx200EquipmentProvider through NINA.Equipment.Mac's TelescopeChooser) over Lx200Link to the Autostar II simulator on an
    /// in-memory cable (NINA.Mac.Equipment.Lx200.Test's SimCable). NINA's Center goes to the target, measures a 12' x -7' pointing
    /// error with the simulated solver, syncs (:CM#) and re-slews; DirectGuider dithers through Lx200Telescope.PulseGuide; the end
    /// area soft-parks. This is plan M4's "centring converges against simulated solves", through the sequencer.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Lx200NightTest {
        private const string Port = "/dev/cu.usbserial-SIM";

        [Test]
        public async Task GeneratedNight_CentresDithersAndSoftParks_ThroughTheLx200Driver() {
            var now = DateTime.Now;
            var coordinates = Sky.TargetAt(40, rising: true, decDeg: 0, now);
            var options = new SimOptions { PlanetaryUpdateSeconds = 0.2, SlewSeconds = 0.8 };
            using var cable = new SimCable(options);
            var errorRa = 12.0 / 60 / Math.Cos(AstroUtil.ToRadians(coordinates.Dec));
            var errorDec = -7.0 / 60;
            var synced = false;
            cable.Sim.CommandReceived += command => {
                if (command.StartsWith(":CM", StringComparison.Ordinal)) {
                    // NINA syncs to the solved position, so from here on the mount's belief is the sky
                    synced = true;
                }
            };
            Coordinates Truth() {
                var (ra, dec) = cable.Sim.BelievedRaDec;
                var believed = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), Epoch.JNOW).Transform(Epoch.J2000);
                return synced ? believed : new Coordinates(Angle.ByDegree(believed.RADegrees - errorRa), Angle.ByDegree(believed.Dec - errorDec), Epoch.J2000);
            }

            await using var rig = await SimRig.Create("lx200 night", coordinates, nighttime: new FixedNighttimeCalculator(now.AddHours(1)),
                configure: p => p.AstrometrySettings.Elevation = 10,
                mount: profile => {
                    var settings = new Lx200Settings(profile) { PortPath = Port, PollIntervalMs = 200, MinimumSlewSeconds = 0.6 };
                    var pool = new Lx200LinkPool(_ => cable.Open, () => new Lx200LinkOptions {
                        ReconnectInterval = TimeSpan.FromMilliseconds(200), WaitForReconnect = TimeSpan.FromSeconds(3), ReconnectGiveUp = null
                    });
                    var provider = new Lx200EquipmentProvider(profile, pool, new Lx200Clock(null, _ => TimeSpan.FromHours(8)));
                    return new MountUnderTest(provider, Lx200Telescope.DeviceId, Truth);
                });
            rig.Host.TelescopeMediator.GetDevice().Should().BeOfType<Lx200Telescope>();

            var plan = new NightPlan {
                Name = "LX200 night", CoolToC = null, WarmAtEnd = false, DewHeater = false,
                Targets = new[] { new TargetPlan { Name = "LX200 test", Coordinates = coordinates, ExposureSeconds = 2, Count = 4, DitherEvery = 2, RecenterArcmin = 0 } }
            };
            var run = await NightRun.Run(rig.Host.Generator.Generate(plan), TimeSpan.FromMinutes(3));
            var files = await rig.WaitForFiles(4, TimeSpan.FromSeconds(20));
            var commands = cable.Sim.ReceivedCommands;
            TestContext.Out.WriteLine($"run {run.Start:HH:mm:ss} - {run.End:HH:mm:ss}; issues: {string.Join(" | ", run.Issues)}");
            TestContext.Out.WriteLine($"solver calls {rig.Solver.Solver.Calls}; true error after centring {(Truth() - coordinates).Distance.ArcMinutes:0.00}'");
            TestContext.Out.WriteLine("commands (excluding polls): " + string.Join(" ", commands.Where(c => !c.StartsWith(":G", StringComparison.Ordinal) && !c.StartsWith(":D", StringComparison.Ordinal))));

            run.Issues.Should().BeEmpty();
            files.Should().HaveCount(4);
            commands.Should().Contain(c => c.StartsWith(":MS", StringComparison.Ordinal), "NINA's slews reached the mount as gotos");
            commands.Count(c => c.StartsWith(":CM", StringComparison.Ordinal)).Should().Be(1, "Center measured the pointing error and synced once");
            rig.Solver.Solver.Calls.Should().Be(2);
            (Truth() - coordinates).Distance.ArcMinutes.Should().BeLessThan(1, "after the sync and re-slew the mount points at the target (one dither has moved it since)");
            commands.Should().Contain(c => c.StartsWith(":Mg", StringComparison.Ordinal) || c.StartsWith(":Mn", StringComparison.Ordinal) || c.StartsWith(":Ms", StringComparison.Ordinal)
                || c.StartsWith(":Me", StringComparison.Ordinal) || c.StartsWith(":Mw", StringComparison.Ordinal), "DirectGuider's dither pulses reached the mount");
            commands.Should().NotContain(c => c.StartsWith(":hP", StringComparison.Ordinal), "park is the driver's soft park, never the Autostar's :hP#");
            cable.Sim.Tracking.Should().BeFalse("the soft park stopped tracking");
        }
    }
}
