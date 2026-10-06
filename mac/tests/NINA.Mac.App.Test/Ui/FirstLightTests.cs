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
using NINA.Mac.App.Astro;
using NINA.Mac.App.Diagnostics;
using NINA.Mac.App.Services;
using NINA.Mac.App.Services.Simulation;
using NINA.Mac.App.ViewModels;
using NINA.Mac.Platform;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Test.Ui {

    /// <summary>The local horizon: NINA's .hrz format both ways, interpolation through north, the site estimate.</summary>
    [TestFixture]
    public class HorizonProfileTests {

        [Test]
        public void SiteEstimate_OpensTheSouth_BlocksTheNorth_AndIsMarked() {
            var h = HorizonProfile.SiteEstimate();
            h.IsEstimate.Should().BeTrue();
            h.GetAltitude(180).Should().Be(15);
            h.GetAltitude(0).Should().Be(80);
            h.GetAltitude(350).Should().Be(80);
            h.GetAltitude(30).Should().Be(80);
            h.GetAltitude(85).Should().BeApproximately(47.5, 1e-9, "linear between 80° at az 80 and 15° at az 90");
            h.GetAltitude(-10).Should().Be(80, "azimuths wrap");
        }

        [Test]
        public void Interpolation_WrapsThroughNorth_WithoutPointsAt0Or360() {
            var h = new HorizonProfile(new[] { new HorizonPoint(90, 10), new HorizonPoint(270, 30) });
            h.GetAltitude(180).Should().Be(20);
            h.GetAltitude(0).Should().Be(20, "halfway from 270 through 360 to 90");
            h.GetAltitude(315).Should().Be(25);
        }

        [Test]
        public void Parse_AcceptsInlineComments_TabsAndCommas_AndReportsBadLines() {
            var text = "# my horizon\n0 20\n90\t15  # tree line\n180,10\nnot a line\n270;25\n400 10\n";
            var h = HorizonProfile.Parse(text, "test", out var warnings);
            h.Points.Select(p => (p.Azimuth, p.Altitude)).Should().Equal(new (double, double)[] { (0, 20), (90, 15), (180, 10), (270, 25), (360, 20) },
                "the engine's reader keeps the inline-commented line and adds 360° as NINA's grooming does");
            h.IsEstimate.Should().BeFalse();
            warnings.Should().HaveCount(2);
            warnings[0].Should().Contain("line 5");
            warnings[1].Should().Contain("line 7");
            FluentActions.Invoking(() => HorizonProfile.Parse("# nothing\n10 10\n", "x", out _)).Should().Throw<FormatException>();
        }

        [Test]
        public void FileText_IsNinasPlainFormat_AndUpstreamReadsTheSameHorizon() {
            var h = HorizonProfile.SiteEstimate();
            var text = h.ToFileText("Deep Water Bay", new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(8)));
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
                if (!line.StartsWith('#')) {
                    line.Should().NotContain("#", "upstream's parser drops lines with inline comments");
                    line.Split(' ').Should().HaveCount(2);
                }
            }
            text.Should().Contain(HorizonProfile.EstimateMarker);
            HorizonProfile.Parse(text, "round trip", out _).IsEstimate.Should().BeTrue();

            // NINA's own parser (NINA.Core CustomHorizon) reads the file as the app does, also without explicit 0/360 points
            var sparse = new HorizonProfile(new[] { new HorizonPoint(90, 10), new HorizonPoint(270, 30) });
            foreach (var profile in new[] { h, sparse }) {
                var upstream = NINA.Core.Model.CustomHorizon.FromReader_Standard(new StringReader(profile.ToFileText()));
                foreach (var az in new[] { 0.0, 10, 45, 85, 90, 135, 180, 225, 275, 300, 359 }) {
                    upstream.GetAltitude(az).Should().BeApproximately(profile.GetAltitude(az), 0.01, "azimuth {0}", az);
                }
            }
        }

        [Test]
        public void SaveAndLoad_RoundTrip() {
            var path = Path.Combine(Path.GetTempPath(), $"horizon-{Guid.NewGuid():N}.hrz");
            try {
                var saved = new HorizonProfile(new[] { new HorizonPoint(0, 60), new HorizonPoint(180, 12.5) }).Save(path, "site");
                saved.Source.Should().Be(path);
                var loaded = HorizonProfile.Load(path, out var warnings);
                warnings.Should().BeEmpty();
                loaded.IsEstimate.Should().BeFalse();
                loaded.GetAltitude(180).Should().Be(12.5);
                loaded.GetAltitude(90).Should().BeApproximately(36.25, 1e-9);
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void SlewGuard_RefusesBelowTheLocalHorizon_AndNamesIt() {
            var settings = new AppSettings();
            var h = HorizonProfile.SiteEstimate();
            FluentActions.Invoking(() => SlewGuard.Check(40, 10, settings, h)).Should().Throw<InvalidOperationException>().WithMessage("*local horizon*80°*estimated*");
            FluentActions.Invoking(() => SlewGuard.Check(12, 180, settings, h)).Should().Throw<InvalidOperationException>().WithMessage("*local horizon*15°*");
            FluentActions.Invoking(() => SlewGuard.Check(30, 180, settings, h)).Should().NotThrow();
            FluentActions.Invoking(() => SlewGuard.Check(12, 180, settings, null)).Should().NotThrow("without a horizon only 0° and the keyhole apply");
            FluentActions.Invoking(() => SlewGuard.Check(80, 180, settings, h)).Should().Throw<InvalidOperationException>().WithMessage("*keyhole*");
        }
    }

    /// <summary>Preflight against fakes: every line has a status and, when it is not a pass, a fix.</summary>
    [TestFixture]
    public class PreflightTests {
        private string home;

        [SetUp]
        public void SetUp() {
            home = Path.Combine(Path.GetTempPath(), "nightglass-preflight-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
        }

        [TearDown]
        public void TearDown() {
            try {
                Directory.Delete(home, true);
            } catch (IOException) {
            }
        }

        private PreflightContext Context(Action<AppSettings> configure = null, IReadOnlyList<SerialPortInfo> ports = null, PowerSourceInfo power = null,
                long? freeBytes = 500_000_000_000, HorizonProfile horizon = null, string expectedPort = null, Func<string, string> probe = null) {
            var settings = new AppSettings { DeviceSource = DeviceSource.Real };
            configure?.Invoke(settings);
            var paths = new UserDataPaths(AppInfo.Current.Identity, home, settings.ImagesRoot);
            return new PreflightContext {
                Settings = settings,
                DataPaths = paths,
                Clock = new ManualClock(new DateTimeOffset(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(8))),
                Power = new FakePowerSource { Next = power ?? new PowerSourceInfo(PowerSourceKind.AC, true, 100, false, true, null) },
                SerialPorts = () => ports ?? Array.Empty<SerialPortInfo>(),
                ExpectedPort = expectedPort,
                Horizon = horizon ?? HorizonProfile.SiteEstimate(),
                ActiveDeviceSource = settings.DeviceSource,
                IndexDirectories = new[] { Path.Combine(home, "Astrometry") },
                SirilCli = Path.Combine(home, "no-siril", "siril-cli"),
                SirilConfigDirectory = Path.Combine(home, "siril"),
                FreeSpace = _ => freeBytes,
                ProbeAstap = probe ?? Preflight.ProbeAstap,
                // Never scans USB in tests: only the hardware agent may touch the camera
                WithDevices = false,
            };
        }

        private static PreflightItem Item(PreflightReport report, string name) => report.Items.Single(i => i.Name == name);

        [Test]
        public void EveryLine_HasAStatus_AndEveryWarnOrFail_HasAFix() {
            var report = Preflight.Run(Context());
            report.Items.Should().HaveCountGreaterThan(18);
            report.Items.Where(i => i.Status is PreflightStatus.Warn or PreflightStatus.Fail).Should().OnlyContain(i => !string.IsNullOrWhiteSpace(i.Fix));
            report.Items.Should().NotContain(i => i.Detail.StartsWith("the check itself failed"));
            report.Format().Should().Contain("PASS  Engine assemblies").And.Contain("fix: ");
            Item(report, "ZWO SDK").Detail.Should().Contain("USB not scanned");
            report.Duration.Should().BeLessThan(TimeSpan.FromSeconds(10), "a preflight finishes in seconds");
        }

        [Test]
        public void BundleChecks_PassInTheBuildOutput() {
            var report = Preflight.Run(Context());
            report.Items.Where(i => i.IsBundleCheck).Should().OnlyContain(i => i.Status == PreflightStatus.Pass);
        }

        [Test]
        public void MissingAstap_Fails_AndADownloadedZipIsNamedInTheFix() {
            var dl = Path.Combine(home, "Astro", "astap", "dl");
            Directory.CreateDirectory(dl);
            File.WriteAllText(Path.Combine(dl, "astap_cli_M1.zip"), "");
            var report = Preflight.Run(Context());
            Item(report, "ASTAP").Status.Should().Be(PreflightStatus.Fail);
            Item(report, "ASTAP").Fix.Should().Contain("astap_cli_M1.zip");
            Item(report, "ASTAP D80").Status.Should().Be(PreflightStatus.Fail);
            report.ExitCode.Should().Be(1);
        }

        [Test]
        public void InstalledAstap_AndD80_Pass() {
            var cli = Path.Combine(home, "Astro", "astap", "cli");
            Directory.CreateDirectory(cli);
            File.WriteAllText(Path.Combine(cli, "astap_cli"), "#!/bin/sh\n");
            File.SetUnixFileMode(Path.Combine(cli, "astap_cli"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var d80 = Path.Combine(home, "Astro", "astap", "d80");
            Directory.CreateDirectory(d80);
            File.WriteAllText(Path.Combine(d80, "d80_0101.1476"), "");
            var report = Preflight.Run(Context());
            Item(report, "ASTAP").Status.Should().Be(PreflightStatus.Pass);
            Item(report, "ASTAP D80").Status.Should().Be(PreflightStatus.Pass);
        }

        private string InstallAstap(string script) {
            var cli = Path.Combine(home, "Astro", "astap", "cli");
            Directory.CreateDirectory(cli);
            var exe = Path.Combine(cli, "astap_cli");
            File.WriteAllText(exe, script);
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return exe;
        }

        private static void Run(string file, params string[] args) {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args) { UseShellExecute = false });
            p.WaitForExit(10_000).Should().BeTrue();
            p.ExitCode.Should().Be(0, "{0} {1}", file, string.Join(" ", args));
        }

        [Test]
        public void QuarantinedAstap_Fails_WithTheXattrFix_AndIsNotStarted() {
            // Archive Utility carries the download quarantine onto astap_cli; macOS then kills it when a solve starts
            var exe = InstallAstap("#!/bin/sh\necho 'ASTAP astrometric solver version CLI-test'\n");
            FileQuarantine.IsQuarantined(exe).Should().BeFalse();
            Item(Preflight.Run(Context()), "ASTAP").Status.Should().Be(PreflightStatus.Pass, "an unquarantined solver that prints its banner runs");

            Run("/usr/bin/xattr", "-w", FileQuarantine.AttributeName, "0081;00000000;Safari;", exe);
            FileQuarantine.IsQuarantined(exe).Should().BeTrue();
            var started = 0;
            var item = Item(Preflight.Run(Context(probe: path => { started++; return null; })), "ASTAP");
            item.Status.Should().Be(PreflightStatus.Fail);
            item.Detail.Should().Contain("quarantine");
            item.Fix.Should().Contain($"xattr -d com.apple.quarantine \"{exe}\"");
            started.Should().Be(0, "a quarantined binary is not started (macOS would kill it or show a Gatekeeper dialog)");
            Preflight.Run(Context()).ExitCode.Should().Be(1);
        }

        [Test]
        public void AstapThatMacOsKills_Fails_AndOneThatRuns_Passes() {
            // What macOS does to a quarantined or badly signed astap_cli: SIGKILL at exec, no output, exit 137
            var exe = InstallAstap("#!/bin/sh\nkill -9 $$\n");
            var killed = Item(Preflight.Run(Context()), "ASTAP");
            killed.Status.Should().Be(PreflightStatus.Fail);
            killed.Detail.Should().Contain("does not run").And.Contain("137");
            killed.Fix.Should().Contain("-h").And.Contain("xattr -d com.apple.quarantine");

            File.WriteAllText(exe, "#!/bin/sh\necho 'not the solver'\nexit 3\n");
            Item(Preflight.Run(Context()), "ASTAP").Detail.Should().Contain("exited with 3");

            File.WriteAllText(exe, "#!/bin/sh\necho 'ASTAP astrometric solver version CLI-test'\n");
            Item(Preflight.Run(Context()), "ASTAP").Status.Should().Be(PreflightStatus.Pass);
        }

        [Test]
        public void CorruptSettings_AreTheirOwnFailLine_NotAHorizonProblem_AndPreflightWritesNothing() {
            var paths = new UserDataPaths(AppInfo.Current.Identity, home);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile));
            File.WriteAllText(paths.SettingsFile, "{ \"deviceSource\": \"Real\", \"site\": { \"latitudeDegrees\": 22.2");
            var before = Directory.GetFiles(Path.GetDirectoryName(paths.SettingsFile));

            var context = PreflightRunner.ForUser(withDevices: false, liveEphemeris: false, homeDirectory: home);
            context.SettingsWarning.Should().Contain("could not be read");
            context.HorizonError.Should().BeNull("a bad settings file is not a horizon problem");
            Directory.GetFiles(Path.GetDirectoryName(paths.SettingsFile)).Should().BeEquivalentTo(before, "a preflight leaves nothing behind (no settings.json.bad)");

            var report = Preflight.Run(context);
            var settings = Item(report, "Settings");
            settings.Status.Should().Be(PreflightStatus.Fail);
            settings.Fix.Should().Contain(paths.SettingsFile).And.Contain("settings.json.bad");
            Item(report, "Horizon").Detail.Should().Contain("estimate").And.NotContain("Settings file");
            Item(report, "Device source").Fix.Should().Contain("settings file", "the cause is the unreadable file, not the Devices setting");

            File.WriteAllText(paths.SettingsFile, "{ \"deviceSource\": \"Real\" }");
            var fine = Preflight.Run(PreflightRunner.ForUser(withDevices: false, liveEphemeris: false, homeDirectory: home));
            Item(fine, "Settings").Status.Should().Be(PreflightStatus.Pass);
            Item(fine, "Device source").Status.Should().NotBe(PreflightStatus.Fail);
        }

        [Test]
        public void StackingSpace_BudgetsSirilsThirtyTwoBitRgbIntermediates() {
            var optics = new OpticsSettings();
            Preflight.StackingBytesPerLight(optics).Should().BeInRange(49_700_000, 49_900_000, "pp_ and r_ 1920 x 1080 x 3 x 4 B each");
            ((double)Preflight.StackingBytesPerLight(optics) / Preflight.FrameBytes(optics)).Should().BeApproximately(12, 0.1);

            // The night's lights at 10 s subs take 1.5 GB/h; stacking them takes ~18 GB per hour of lights
            var roomy = Preflight.Run(Context());
            Item(roomy, "Free disk").Status.Should().Be(PreflightStatus.Pass);
            Item(roomy, "Stacking space").Status.Should().Be(PreflightStatus.Pass);

            var half = Preflight.Run(Context(freeBytes: 50_000_000_000));
            Item(half, "Free disk").Status.Should().Be(PreflightStatus.Pass);
            var stacking = Item(half, "Stacking space");
            stacking.Status.Should().Be(PreflightStatus.Info);
            stacking.Detail.Should().Contain("room to stack about ").And.Contain("~12x");
            stacking.Fix.Should().Contain("process/");

            var tight = Preflight.Run(Context(freeBytes: 20_000_000_000));
            Item(tight, "Free disk").Status.Should().Be(PreflightStatus.Warn);
            Item(tight, "Free disk").Fix.Should().NotContain("double");
            Item(tight, "Stacking space").Status.Should().Be(PreflightStatus.Warn);
        }

        [Test]
        public void IndexTiles_ForTheSelectedTrain_NameTheMissingTiles() {
            Preflight.IndexScalesFor(15.3).Should().Equal(2, 3, 4, 5);
            Preflight.IndexScalesFor(24.3).Should().Equal(3, 4, 5, 6, 7);
            var dir = Path.Combine(home, "Astrometry");
            Directory.CreateDirectory(dir);
            foreach (var scale in new[] { 2, 3, 4 }) {
                for (var tile = 0; tile < 48; tile++) {
                    if (!(scale == 2 && tile == 5) && !(scale == 4 && tile == 4)) {
                        File.WriteAllText(Path.Combine(dir, $"index-42{scale:00}-{tile:00}.fits"), "");
                    }
                }
            }
            for (var tile = 0; tile < 12; tile++) {
                File.WriteAllText(Path.Combine(dir, $"index-4205-{tile:00}.fits"), "");
            }
            var native = Item(Preflight.Run(Context()), "Index tiles");
            native.Status.Should().Be(PreflightStatus.Warn);
            native.Detail.Should().Contain("index-4202-05").And.Contain("index-4204-04").And.Contain("native f/10");
            var reducer = Item(Preflight.Run(Context(s => s.Optics.UseReducer = true)), "Index tiles");
            reducer.Detail.Should().Contain("scales 4206, 4207 missing").And.NotContain("index-4202-05", "the reducer's field does not use 4202");
        }

        [Test]
        public void SerialPort_NamesTheExpectedAdapter() {
            Item(Preflight.Run(Context(ports: new[] { new SerialPortInfo(PreflightContext.RigSerialPort, true) })), "Serial port").Status.Should().Be(PreflightStatus.Pass);
            var other = Item(Preflight.Run(Context(ports: new[] { new SerialPortInfo("/dev/cu.usbserial-OTHER", true), new SerialPortInfo("/dev/cu.Bluetooth-Incoming-Port", false) })), "Serial port");
            other.Status.Should().Be(PreflightStatus.Warn);
            other.Detail.Should().Contain("expected /dev/cu.usbserial-DU0D8VUG").And.Contain("/dev/cu.usbserial-OTHER").And.NotContain("Bluetooth");
            Item(Preflight.Run(Context(expectedPort: "/dev/cu.usbserial-OTHER", ports: new[] { new SerialPortInfo("/dev/cu.usbserial-OTHER", true) })), "Serial port")
                .Status.Should().Be(PreflightStatus.Pass, "the profile's port wins over the rig default");
        }

        [Test]
        public void PortFromProfile_ReadsTheRigProfilesPortPath() {
            var engine = Path.Combine(home, "Engine");
            Directory.CreateDirectory(Path.Combine(engine, "Profiles"));
            File.WriteAllText(Path.Combine(engine, "Profiles", $"{NINA.Mac.App.Engine.EngineProfileService.RigProfileId}.profile"),
                "<Profile><PluginSettings><Value>/dev/cu.usbserial-DU0D8VUG</Value></PluginSettings></Profile>");
            Preflight.PortFromProfile(engine).Should().Be("/dev/cu.usbserial-DU0D8VUG");
            Preflight.PortFromProfile(Path.Combine(home, "nothing")).Should().BeNull();
        }

        [Test]
        public void Site_MoreThanOneArcminuteOff_Fails() {
            Item(Preflight.Run(Context()), "Site").Status.Should().Be(PreflightStatus.Pass);
            Item(Preflight.Run(Context(s => s.Site.LatitudeDegrees = 22.25 + (0.9 / 60))), "Site").Status.Should().Be(PreflightStatus.Pass);
            var off = Item(Preflight.Run(Context(s => s.Site.LongitudeDegrees = 114.18 + (1.5 / 60))), "Site");
            off.Status.Should().Be(PreflightStatus.Fail);
            off.Fix.Should().Contain("114.18");
        }

        [Test]
        public void Horizon_Estimate_SaysMeasureIt_AMeasuredOnePasses() {
            Item(Preflight.Run(Context()), "Horizon").Detail.Should().Contain("estimate - measure it");
            Item(Preflight.Run(Context()), "Horizon").Status.Should().Be(PreflightStatus.Warn);
            var measured = new HorizonProfile(new[] { new HorizonPoint(0, 70), new HorizonPoint(180, 18) }, source: "/x/horizon.hrz");
            Item(Preflight.Run(Context(horizon: measured)), "Horizon").Status.Should().Be(PreflightStatus.Pass);
        }

        [Test]
        public void OpticalTrain_NativeIsBelowAstapsMinimum_TheReducerIsNot() {
            var native = Item(Preflight.Run(Context()), "Optical train");
            native.Status.Should().Be(PreflightStatus.Warn);
            native.Detail.Should().Contain("0.1436").And.Contain("0.15");
            native.Fix.Should().Contain("reducer");
            var reducer = Item(Preflight.Run(Context(s => s.Optics.UseReducer = true)), "Optical train");
            reducer.Status.Should().Be(PreflightStatus.Pass);
            reducer.Detail.Should().Contain("f/6.3 reducer 1575 mm").And.Contain("0.38″/px bin 1").And.Contain("0.76″/px bin 2");
        }

        [Test]
        public void DiskBudget_OneAndAHalfGigabytesAnHour() {
            Preflight.FrameBytes(new OpticsSettings()).Should().BeInRange(4_100_000, 4_200_000, "a bin-2 16-bit 1920 x 1080 frame");
            Item(Preflight.Run(Context()), "Free disk").Detail.Should().Contain("1.5 GB/h");
            Item(Preflight.Run(Context(freeBytes: 5_000_000_000)), "Free disk").Status.Should().Be(PreflightStatus.Fail);
            Item(Preflight.Run(Context(freeBytes: 20_000_000_000)), "Free disk").Status.Should().Be(PreflightStatus.Warn);
            Item(Preflight.Run(Context(freeBytes: null)), "Free disk").Status.Should().Be(PreflightStatus.Info);
        }

        [Test]
        public void ImagesUnderDocuments_Fail() {
            var item = Item(Preflight.Run(Context(s => s.ImagesRoot = "~/Documents/Astro")), "Images folder");
            item.Status.Should().Be(PreflightStatus.Fail);
            item.Detail.Should().Contain("iCloud");
        }

        [Test]
        public void Battery_LowFails_HalfWarns_FullOrAcPasses() {
            Item(Preflight.Run(Context(power: new PowerSourceInfo(PowerSourceKind.Battery, true, 25, false, false, null))), "Battery").Status.Should().Be(PreflightStatus.Fail);
            Item(Preflight.Run(Context(power: new PowerSourceInfo(PowerSourceKind.Battery, true, 60, false, false, null))), "Battery").Status.Should().Be(PreflightStatus.Warn);
            Item(Preflight.Run(Context(power: new PowerSourceInfo(PowerSourceKind.Battery, true, 98, false, false, null))), "Battery").Status.Should().Be(PreflightStatus.Pass);
        }

        [Test]
        public void Siril_BinnedPixelSizeTicked_Warns() {
            Directory.CreateDirectory(Path.Combine(home, "siril"));
            File.WriteAllText(Path.Combine(home, "siril", "config.1.4.ini"), "[core]\nbinning_update=true\n");
            var item = Item(Preflight.Run(Context()), "Siril binned pixel size");
            item.Status.Should().Be(PreflightStatus.Warn);
            item.Fix.Should().Contain("Update pixel size of binned images");
            File.WriteAllText(Path.Combine(home, "siril", "config.1.4.ini"), "[core]\nbinning_update=false\n");
            Item(Preflight.Run(Context()), "Siril binned pixel size").Status.Should().Be(PreflightStatus.Pass);
        }

        [Test]
        public void Tonight_GivesDuskDawnAndTheMoon() {
            var report = Preflight.Run(Context());
            Item(report, "Tonight").Detail.Should().Contain("astronomical dusk Sat 19:").And.Contain("dawn Sun 05:");
            Item(report, "Moon").Detail.Should().Contain("% lit");
        }

        [Test]
        public void SimulatedDevices_Warn() {
            var item = Item(Preflight.Run(Context(s => s.DeviceSource = DeviceSource.Simulated)), "Device source");
            item.Status.Should().Be(PreflightStatus.Warn);
            item.Fix.Should().Contain("Real");
        }
    }

    [TestFixture]
    public class MoonTests {

        [Test]
        public void Illumination_FullAndNewMoon() {
            // Full Moon 2024-04-23 23:49 UTC; new Moon (total solar eclipse) 2024-04-08 18:21 UTC
            SkyMath.MoonIllumination(new DateTimeOffset(2024, 4, 23, 23, 49, 0, TimeSpan.Zero)).Should().BeGreaterThan(0.99);
            SkyMath.MoonIllumination(new DateTimeOffset(2024, 4, 8, 18, 21, 0, TimeSpan.Zero)).Should().BeLessThan(0.01);
        }

        [Test]
        public void Position_AtTheEclipse_IsTheSuns() {
            var t = new DateTimeOffset(2024, 4, 8, 18, 18, 0, TimeSpan.Zero);
            var (mr, md, parallax) = SkyMath.MoonPosition(t);
            var (sr, sd) = SkyMath.SunPosition(t);
            SkyMath.Separation(mr, md, sr, sd).Should().BeLessThan(1.5, "geocentric: within the parallax of the topocentric alignment");
            parallax.Should().BeInRange(0.9, 1.02);
        }

        [Test]
        public void AstronomicalNight_InHongKongInOctober() {
            var site = new GeoSite(22.25, 114.18);
            var n = SkyMath.AstronomicalNight(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(8)), site).Value;
            n.DarkNow.Should().BeFalse();
            n.Dusk.ToOffset(TimeSpan.FromHours(8)).Hour.Should().Be(19);
            n.Dawn.ToOffset(TimeSpan.FromHours(8)).Hour.Should().Be(5);
            SkyMath.AstronomicalNight(new DateTimeOffset(2026, 10, 10, 23, 0, 0, TimeSpan.FromHours(8)), site).Value.DarkNow.Should().BeTrue();
        }
    }

    /// <summary>The horizon in the app: estimate by default, saved from the Target screen, used by the slew guard and the planner.</summary>
    [TestFixture]
    public class HorizonInTheAppTests : HeadlessTestBase {
        private string home;

        [SetUp]
        public void SetUp() {
            home = Path.Combine(Path.GetTempPath(), "nightglass-horizon-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
        }

        [TearDown]
        public void TearDown() {
            try {
                Directory.Delete(home, true);
            } catch (IOException) {
            }
        }

        private AppServices Services(ManualClock clock = null) => AppServices.Create(new AppServicesOptions {
            Clock = clock ?? new ManualClock(TestTimes.EveningOct10),
            Settings = new MemorySettingsStore(),
            KeepAwake = new RecordingKeepAwake(),
            PowerSource = new FakePowerSource(),
            HomeDirectory = home,
            SerialPortLister = () => new[] { new SerialPortInfo("/dev/cu.usbserial-DU0D8VUG", true) },
            FastSimulation = true,
        });

        [Test]
        public void NoFile_UsesTheEstimate_WithoutWritingAnything() {
            using var services = Services();
            services.Horizon.IsEstimate.Should().BeTrue();
            services.Horizon.Source.Should().Be(HorizonProfile.BuiltInSource);
            File.Exists(services.DataPaths.HorizonFile).Should().BeFalse("starting the app writes nothing");
        }

        [Test]
        public async Task SlewGuard_UsesTheHorizon_AndASavedOneReplacesIt() {
            using var services = Services();
            await services.Mount.ConnectAsync();
            var site = services.Site;
            var (ra, dec) = SkyMath.FromAltAz(services.Clock.Now, 12, 180, site);
            await FluentActions.Awaiting(() => services.Mount.SlewToAsync(ra, dec)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*local horizon*");

            services.SaveHorizon(new HorizonProfile(new[] { new HorizonPoint(0, 60), new HorizonPoint(180, 8) }));
            File.Exists(services.DataPaths.HorizonFile).Should().BeTrue();
            services.Horizon.IsEstimate.Should().BeFalse();
            await services.Mount.SlewToAsync(ra, dec);
            services.Mount.Altitude.Should().BeApproximately(12, 0.1);

            using var again = Services();
            again.Horizon.IsEstimate.Should().BeFalse("the saved horizon is read at the next start");
            again.Horizon.GetAltitude(180).Should().Be(8);
        }

        [Test]
        public void UnreadableFile_FallsBackToTheEstimate_AndSaysWhy() {
            var path = new UserDataPaths(AppInfo.Current.Identity, home).HorizonFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "garbage\n");
            using var services = Services();
            services.Horizon.IsEstimate.Should().BeTrue();
            services.HorizonError.Should().Contain("could not be read");
        }

        [Test]
        public void TargetPlanner_FlagsATargetBehindTheLocalHorizon() {
            var settings = new AppSettings { MinAltitudeDegrees = 0 };
            var target = new CatalogTarget("low south", "", 0, 0, "test");
            var clock = TestTimes.EveningOct10;
            var (ra, dec) = SkyMath.FromAltAz(clock, 10, 175, new GeoSite(22.25, 114.18));
            target = target with { RightAscensionHours = ra, DeclinationDegrees = dec };
            var v = TargetPlanner.Evaluate(target, clock, settings, 10, HorizonProfile.SiteEstimate());
            v.BelowLocalHorizon.Should().BeTrue();
            v.Observable.Should().BeFalse();
            v.Warnings.Should().Contain(w => w.Contains("Behind the local horizon") && w.Contains("estimated"));
            TargetPlanner.Evaluate(target, clock, settings, 10).Observable.Should().BeTrue("without a horizon only the altitude limits apply");
        }

        [Test]
        public void Editor_RecordsFromTheMount_Saves_Imports_AndExports() {
            OnUiAsync(async () => {
                using var services = Services();
                var vm = new MainWindowViewModel(services, null);
                var editor = vm.Target.Horizon;
                editor.IsEstimate.Should().BeTrue();
                editor.Rows.Should().HaveCount(6);
                vm.Target.ChartHorizon.IsEstimate.Should().BeTrue();
                editor.CanRecord.Should().BeFalse();

                await services.Mount.ConnectAsync();
                editor.CanRecord.Should().BeTrue();
                editor.RecordFromMountCommand.Execute(null);
                editor.Rows.Should().HaveCount(7);
                editor.Rows.Select(r => r.Azimuth).Should().BeInAscendingOrder();
                var recorded = editor.Rows.Single(r => Math.Abs(r.Azimuth - SimulatedMount.SoftParkAzimuth) < 0.1);
                recorded.Altitude.Should().BeApproximately(SimulatedMount.SoftParkAltitude, 0.1);
                editor.IsEstimate.Should().BeFalse("a recorded point is a measurement");
                editor.Status.Should().Contain("Recorded azimuth 180.0°");
                vm.Target.ChartHorizon.GetAltitude(180).Should().BeApproximately(30, 0.1, "the sky view draws the table being edited");

                editor.SaveCommand.Execute(null);
                editor.Error.Should().BeNull();
                services.Horizon.IsEstimate.Should().BeFalse();
                services.Horizon.GetAltitude(180).Should().BeApproximately(30, 0.1);
                File.ReadAllText(services.DataPaths.HorizonFile).Should().NotContain(HorizonProfile.EstimateMarker);

                var export = Path.Combine(home, "export.hrz");
                editor.ExportTo(export);
                File.ReadAllText(export).Should().Contain("180 30");

                var import = Path.Combine(home, "import.hrz");
                File.WriteAllText(import, "0 50\n120 20  # roof\n240 25\n");
                editor.ImportFrom(import);
                editor.Rows.Select(r => (r.Azimuth, r.Altitude)).Should().Equal(new (double, double)[] { (0, 50), (120, 20), (240, 25), (360, 50) },
                    "the inline comment is accepted and 360° is added as NINA reads the file");
                editor.IsDirty.Should().BeTrue();
                editor.Status.Should().Contain("Imported 4 points");
                editor.RevertCommand.Execute(null);
                editor.Rows.Should().HaveCount(7);

                editor.Rows[0].Altitude = 95;
                editor.Draft.Should().BeNull();
                editor.Error.Should().Contain("outside -90° to 90°");
                editor.SaveCommand.Execute(null);
                services.Horizon.GetAltitude(180).Should().BeApproximately(30, 0.1, "an invalid table is not saved");
            });
        }
    }

    /// <summary>The Connect screen's preflight and optical train, the Run screen's plan check and calibration hand-over.</summary>
    [TestFixture]
    public class FirstLightScreenTests : HeadlessTestBase {

        [Test]
        public void Connect_Preflight_ListsFailuresFirst_WithoutScanningUsb() {
            OnUiAsync(async () => {
                var services = CreateServices();
                var vm = new MainWindowViewModel(services, null);
                vm.Connect.PreflightWithDevices = true;
                await vm.Connect.RunPreflightCommand.ExecuteAsync(null);
                var report = vm.Connect.LastPreflight;
                report.Should().NotBeNull();
                vm.Connect.PreflightLines.Should().HaveCount(report.Items.Count);
                vm.Connect.PreflightSummary.Should().NotBeNullOrEmpty();
                var statuses = vm.Connect.PreflightLines.Select(l => l.Item.Status).ToList();
                statuses.TakeWhile(s => s == PreflightStatus.Fail).Count().Should().Be(report.Failures, "FAIL lines come first");
                report.Items.Single(i => i.Name == "ZWO SDK").Detail.Should().Contain("USB not scanned", "simulated devices never scan USB, even when asked");
                report.Items.Single(i => i.Name == "Serial port").Status.Should().Be(PreflightStatus.Warn, "the test's adapter is not the rig's");
            });
        }

        [Test]
        public void Connect_Preflight_ShowsAnUnreadableSettingsFile_UntilTheSettingsAreSaved() {
            var folder = Path.Combine(Path.GetTempPath(), "nightglass-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "settings.json");
            File.WriteAllText(file, "{ \"deviceSource\": \"Re");
            try {
                OnUiAsync(async () => {
                    using var services = AppServices.Create(new AppServicesOptions {
                        Clock = new ManualClock(TestTimes.EveningOct10),
                        Settings = new JsonSettingsStore(file),
                        KeepAwake = new RecordingKeepAwake(),
                        PowerSource = new FakePowerSource(),
                        HomeDirectory = "/Users/test",
                        SerialPortLister = () => Array.Empty<SerialPortInfo>(),
                        FastSimulation = true,
                    });
                    services.SettingsLoadWarning.Should().Contain("could not be read");
                    var vm = new MainWindowViewModel(services, null);
                    vm.Connect.SimulationNote.Should().Contain("could not be read");
                    await vm.Connect.RunPreflightCommand.ExecuteAsync(null);
                    var settings = vm.Connect.LastPreflight.Items.Single(i => i.Name == "Settings");
                    settings.Status.Should().Be(PreflightStatus.Fail);
                    settings.Fix.Should().Contain(file);
                    vm.Connect.PreflightLines.First().Item.Status.Should().Be(PreflightStatus.Fail);

                    vm.NavigateTo(PageKind.Settings);
                    vm.Settings.SaveCommand.Execute(null);
                    services.SettingsLoadWarning.Should().BeNull("the file is good again once saved");
                    await vm.Connect.RunPreflightCommand.ExecuteAsync(null);
                    vm.Connect.LastPreflight.Items.Single(i => i.Name == "Settings").Status.Should().Be(PreflightStatus.Pass);
                });
            } finally {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Test]
        public void Connect_OpticalTrain_SwitchSavesTheSettings() {
            var services = CreateServices();
            var vm = new MainWindowViewModel(services, null);
            vm.Connect.UseNative.Should().BeTrue();
            vm.Connect.NativeTrainText.Should().Contain("2500 mm").And.Contain("below ASTAP D80's 0.15°");
            vm.Connect.ReducerTrainText.Should().Contain("1575 mm").And.NotContain("below");
            vm.Connect.UseReducer = true;
            services.Settings.Current.Optics.UseReducer.Should().BeTrue();
            services.Settings.Current.Optics.EffectiveFocalLengthMm.Should().Be(1575);
            vm.Connect.UseNative.Should().BeFalse();
            vm.Target.OpticsText.Should().StartWith("1575 mm");

            // Settings › Save afterwards must not put the old train back
            vm.NavigateTo(PageKind.Settings);
            vm.Settings.Draft.Gain = 200;
            vm.Settings.SaveCommand.Execute(null);
            services.Settings.Current.Optics.UseReducer.Should().BeTrue();
            services.Settings.Current.Gain.Should().Be(200);
        }

        [Test]
        public void Teardown_ShowsTheOneCommandThatStacksTheNight() {
            var services = CreateServices();
            var vm = new MainWindowViewModel(services, null);
            vm.Teardown.Refresh();
            vm.Teardown.StackNightCommand.Should().EndWith("--stack-night 2026-10-10");
            vm.Focus.BahtinovNote.Should().Be("simulated");
        }

        [Test]
        public void Run_ShowsThePlanCheck_ForTheLoadedPlan() {
            var services = CreateServices();
            var vm = new MainWindowViewModel(services, null);
            vm.Target.SearchText = "NGC 253";
            vm.Target.UseForRunCommand.Execute(null);
            vm.Run.PlanCheckSummary.Should().Contain("Real devices", "the simulators have no plan validator and say so");
            vm.Run.PlanErrors.Should().BeEmpty();
        }

        [Test]
        public void Run_DuringCalibration_AsksFirst_ThenCancelsIt_AndStarts() {
            OnUiAsync(async () => {
                var clock = new HoldingClock(TestTimes.EveningOct10);
                var services = AppServices.Create(new AppServicesOptions {
                    Clock = clock,
                    Settings = new MemorySettingsStore(),
                    KeepAwake = new RecordingKeepAwake(),
                    PowerSource = new FakePowerSource(),
                    HomeDirectory = "/Users/test",
                    SerialPortLister = () => Array.Empty<SerialPortInfo>(),
                    FastSimulation = true,
                });
                var vm = new MainWindowViewModel(services, null);
                await vm.Connect.StartNightCommand.ExecuteAsync(null);
                vm.Target.SearchText = "NGC 253";
                vm.Target.FrameCount = 2;
                vm.Target.UseForRunCommand.Execute(null);

                // A long dark that does not end by itself (the clock holds every delay until released or cancelled)
                services.SimCamera.SetCooler(true, 0);
                clock.Advance(TimeSpan.FromMinutes(30));
                clock.Hold = true;
                vm.Calibrate.DarkExposuresText = "300";
                var darks = vm.Calibrate.RunDarksCommand.ExecuteAsync(null);
                services.Calibration.IsRunning.Should().BeTrue();

                await vm.Run.StartCommand.ExecuteAsync(null);
                vm.Run.CalibrationConflict.Should().Contain("Calibration is running").And.Contain("Press Start again");
                services.Calibration.IsRunning.Should().BeTrue("the first Start only asks");
                services.Session.State.Should().Be(SessionState.Idle);

                clock.Hold = false;
                await vm.Run.StartCommand.ExecuteAsync(null);
                await darks;
                services.Calibration.IsRunning.Should().BeFalse("the second Start cancelled the calibration");
                vm.Run.CalibrationConflict.Should().BeNull();
                vm.Run.ErrorMessage.Should().BeNull();
                services.Session.State.Should().Be(SessionState.Finished);
                services.Session.Progress.FramesDone.Should().Be(2);
            });
        }

        /// <summary>A manual clock whose delays wait (until cancelled) while <see cref="Hold"/> is set, and complete at once otherwise.</summary>
        private sealed class HoldingClock : IClock {
            private readonly object lockobj = new();
            private DateTimeOffset now;

            public HoldingClock(DateTimeOffset start) {
                now = start;
            }

            public bool Hold { get; set; }

            public void Advance(TimeSpan by) {
                lock (lockobj) {
                    now += by;
                }
            }

            public DateTimeOffset Now {
                get {
                    lock (lockobj) {
                        return now;
                    }
                }
            }

            public Task Delay(TimeSpan delay, CancellationToken ct = default) {
                if (Hold) {
                    return Task.Delay(Timeout.Infinite, ct);
                }
                lock (lockobj) {
                    now += delay;
                }
                return Task.CompletedTask;
            }
        }
    }

    /// <summary>--stack-night picks the night and refuses nonsense before anything runs Siril.</summary>
    [TestFixture]
    public class StackNightTests {

        [Test]
        public void PicksTheNewestNightWithLights_AndRefusesBadInput() {
            var home = Path.Combine(Path.GetTempPath(), "nightglass-stack-" + Guid.NewGuid().ToString("N"));
            try {
                var paths = new UserDataPaths(AppInfo.Current.Identity, home);
                var output = new StringWriter();
                StackNight.Run(new[] { StackNight.Flag, StackNight.DryRunFlag }, output, paths).Should().Be(1);
                output.ToString().Should().Contain("No night with lights");

                foreach (var night in new[] { "2026-10-09", "2026-10-10" }) {
                    var lights = Path.Combine(paths.ImagesRoot, night, "NGC 253", "lights");
                    Directory.CreateDirectory(lights);
                    File.WriteAllText(Path.Combine(lights, "frame_0001.fits"), "");
                }
                Directory.CreateDirectory(Path.Combine(paths.ImagesRoot, "2026-10-11", "M42", "snapshots"));
                var layout = new NINA.Mac.Siril.SessionLayout(new NINA.Mac.Siril.SessionLayoutOptions { Root = paths.ImagesRoot });
                StackNight.LatestNightWithLights(layout).Should().Be(new DateOnly(2026, 10, 10), "the newest night that has lights, not the newest folder");

                output = new StringWriter();
                StackNight.Run(new[] { StackNight.Flag, "2026-13-40" }, output, paths).Should().Be(2);
                output.ToString().Should().Contain("not a night");
                output = new StringWriter();
                StackNight.Run(new[] { StackNight.Flag, "2026-10-11", StackNight.DryRunFlag }, output, paths).Should().Be(1);
                output.ToString().Should().Contain("No target with lights");
            } finally {
                if (Directory.Exists(home)) {
                    Directory.Delete(home, true);
                }
            }
        }
    }
}
