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
using NINA.Astrometry;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Mac;
using NINA.Mac.Lx200;
using NINA.Mac.Lx200.Sim;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.Equipment.Lx200.Test {

    /// <summary>
    /// The driver as the host uses it: through NINA.Equipment.Mac's choosers, over a real serial device node (a pseudo-terminal
    /// through System.IO.Ports, the code path of /dev/cu.usbserial-* minus the FTDI chip), and a check of the compiled driver
    /// that no string in it is the Autostar's park command.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Lx200IntegrationTest {

        [Test]
        public async Task Choosers_ListTheMountAndTheFocuser_AndSelectThemFromTheProfile() {
            using var rig = new Rig();
            rig.Profile.TelescopeSettings.Id = Lx200Telescope.DeviceId;
            rig.Profile.FocuserSettings.Id = Lx200Focuser.DeviceId;
            var telescopes = new TelescopeChooser(rig.ProfileService.Object, new IEquipmentProvider<ITelescope>[] { rig.Provider });
            var focusers = new FocuserChooser(rig.ProfileService.Object, new IEquipmentProvider<IFocuser>[] { rig.Provider });

            await telescopes.GetEquipment();
            await focusers.GetEquipment();
            await telescopes.GetEquipment();   // a refresh lists the same instances

            telescopes.Devices.Select(d => d.Id).Should().Equal("No_Device", Lx200Telescope.DeviceId);
            telescopes.SelectedDevice.Should().BeSameAs(rig.Telescope);
            focusers.Devices.Select(d => d.Id).Should().Equal("No_Device", Lx200Focuser.DeviceId);
            focusers.SelectedDevice.Should().BeSameAs(rig.Focuser);
            (await ((ITelescope)telescopes.SelectedDevice).Connect(CancellationToken.None)).Should().BeTrue();
            (await ((IFocuser)focusers.SelectedDevice).Connect(CancellationToken.None)).Should().BeTrue();
        }

        [Test]
        public async Task OverAPseudoTerminal_ThroughSystemIoPorts_GotoAndFocusWork_AndTheTraceIsWrittenToNinasLogFolder() {
            if (!PseudoTerminal.IsSupported) {
                Assert.Ignore("openpty needs macOS or Linux");
            }
            using var pty = PseudoTerminal.Open();
            using var sim = new AutostarSimulator(pty.Master, new SimOptions { SlewSeconds = 0.8, PlanetaryUpdateSeconds = 0.2 });
            var profile = new NINA.Profile.Profile("Pty");
            profile.AstrometrySettings.Latitude = 22.25;
            profile.AstrometrySettings.Longitude = 114.18;
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            _ = new Lx200Settings(profileService.Object) { PortPath = pty.SlavePath, PollIntervalMs = 300, MinimumSlewSeconds = 0.6 };
            var provider = new Lx200EquipmentProvider(profileService.Object, new Lx200LinkPool());   // default opener: Lx200Serial.Open
            var mount = provider.Telescope;
            var focuser = provider.Focuser;
            try {
                (await mount.Connect(CancellationToken.None)).Should().BeTrue();
                (await focuser.Connect(CancellationToken.None)).Should().BeTrue();
                mount.Link.Name.Should().Be(pty.SlavePath);
                var (ra, dec) = sim.BelievedRaDec;
                var target = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec + 2), Epoch.JNOW);
                (await mount.SlewToCoordinates(target, CancellationToken.None)).Should().BeTrue();
                var (ra2, dec2) = sim.BelievedRaDec;
                Lx200Astro.SeparationDeg(ra2, dec2, target.RA, target.Dec).Should().BeLessThan(10.0 / 3600);
                await focuser.Move(focuser.Position + 300, CancellationToken.None);
                focuser.Position.Should().Be(32800);
                var traceFile = mount.Link.TraceFile;
                traceFile.Should().StartWith(Path.Combine(TestHost.DataRoot, "Logs", "lx200"));
                mount.Disconnect();
                focuser.Disconnect();
                var trace = File.ReadAllText(traceFile);
                trace.Should().Contain(" TX ").And.Contain(" RX ").And.Contain(":MS#").And.Contain(":F-#");
                trace.Should().Contain("DF", "the degree byte is in the hex column");
            } finally {
                mount.Disconnect();
                focuser.Disconnect();
            }
            sim.ReceivedCommands.Should().NotContain(c => c.Contains("hP"));
        }

        [Test]
        public void TheCompiledDriver_HoldsNoStringWithTheAutostarParkCommand() {
            using var stream = File.OpenRead(typeof(Lx200Telescope).Assembly.Location);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var literals = new List<string>();
            for (var handle = MetadataTokens.UserStringHandle(1); !handle.IsNil; handle = metadata.GetNextHandle(handle)) {
                literals.Add(metadata.GetUserString(handle));
            }
            literals.Should().Contain(":Q#", "the scan sees the driver's command literals");
            // (method names reach the binary as CallerMemberName literals, e.g. "EnsureHighPrecisionFormat": only command spellings count)
            literals.Should().NotContain(s => s.Contains(":hP", StringComparison.Ordinal) || s.Contains("hP#", StringComparison.Ordinal) || s == "hP",
                "the driver has no way to spell the park command");
        }

        [Test]
        public void Settings_LiveInTheProfile_WithTheDocumentedDefaults() {
            var profile = new NINA.Profile.Profile("Defaults");
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            var settings = new Lx200Settings(profileService.Object);

            settings.PortPath.Should().BeEmpty();
            settings.PulseStrategy.Should().Be(Lx200PulseStrategy.Auto);
            settings.GuideRateArcsecPerSec.Should().Be(10.0);
            settings.SerializePulseAxes.Should().BeTrue();
            settings.MaxSyncOffsetDegrees.Should().Be(3.0);
            settings.MinimumSlewSeconds.Should().Be(1.5);
            settings.DateConvention.Should().Be(Lx200DateConvention.Unknown);
            settings.SiteToleranceArcmin.Should().Be(1.0);
            settings.SoftParkStopsTracking.Should().BeTrue();
            double.IsNaN(settings.SoftParkAltitude).Should().BeTrue();
            settings.FocuserSpeed.Should().Be(2);
            settings.FocuserMaxStep.Should().Be(65000);
            settings.FocusMethod.Should().Be(Lx200FocusMethod.HostTimed);
            settings.FocuserBacklashMs.Should().Be(0);

            settings.PulseStrategy = Lx200PulseStrategy.GotoOffset;
            settings.GuideRateArcsecPerSec = 99;
            profile.PluginSettings.TryGetValue(Lx200Settings.SettingsId, nameof(Lx200Settings.PulseStrategy), out string stored).Should().BeTrue();
            stored.Should().Be("GotoOffset");
            settings.GuideRateArcsecPerSec.Should().Be(15.0417, "':Rg' allows sidereal at most");
        }

        [Test]
        public void PortResolution_PicksTheOnlyUsbSerialAdapter_OrSaysWhatIsWrong() {
            Lx200Ports.Resolve("/dev/cu.usbserial-A1", () => Array.Empty<string>()).Should().Be("/dev/cu.usbserial-A1");
            Lx200Ports.Resolve("", () => new[] { "/dev/cu.Bluetooth-Incoming-Port", "/dev/cu.usbserial-A10KXYZ" }).Should().Be("/dev/cu.usbserial-A10KXYZ");
            Action none = () => Lx200Ports.Resolve(null, () => new[] { "/dev/cu.Bluetooth-Incoming-Port" });
            none.Should().Throw<InvalidOperationException>().WithMessage("*No USB serial adapter*");
            Action two = () => Lx200Ports.Resolve(" ", () => new[] { "/dev/cu.usbserial-A", "/dev/cu.usbserial-B" });
            two.Should().Throw<InvalidOperationException>().WithMessage("*Several USB serial ports*");
            Lx200Firmware.IsStarPatch("4.2G").Should().BeTrue();
            Lx200Firmware.IsStarPatch("4.2g").Should().BeFalse();
            Lx200Firmware.IsStarPatch("4.2k").Should().BeFalse();
            Lx200Firmware.ParseGw("AT2").Should().Be(('A', true, '2'));
            Lx200Firmware.ParseGw("LN0").Should().Be(('L', false, '0'));
            Lx200Firmware.ParseGw("AT").Should().BeNull();
        }
    }
}
