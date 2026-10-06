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
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Mac.App.Services;
using NINA.Mac.Equipment.Lx200;
using NINA.Mac.Siril;
using NINA.PlateSolving;
using NINA.PlateSolving.Mac;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine.Test {

    /// <summary>Composing the engine: nothing opened, the rig profile written and kept, NINA's data folder moved.</summary>
    [TestFixture]
    [NonParallelizable]
    public class CompositionTests {

        [Test]
        public async Task Create_OpensNothing_TheCameraListIsScannedOnlyByConnect() {
            await using var rig = new EngineRig("compose");
            // NINA's CameraVM rescans its chooser in its constructor; the gate must have kept that away from USB
            rig.Chooser.Scans.Should().Be(0, "composing the engine must not enumerate cameras");
            rig.Engine.CameraGate.IsOpen.Should().BeFalse();
            rig.Engine.CameraGate.Devices.Should().BeEmpty();
            rig.Cable.Opens.Should().Be(0, "the serial port is opened only by the mount's Connect");
            rig.Engine.Camera.State.Should().Be(DeviceConnectionState.Disconnected);
            rig.Engine.Mount.State.Should().Be(DeviceConnectionState.Disconnected);
            rig.Engine.Focuser.State.Should().Be(DeviceConnectionState.Disconnected);

            await rig.Engine.Camera.ConnectAsync();
            rig.Chooser.Scans.Should().Be(1);
            rig.Engine.CameraGate.SelectedDevice.Should().BeSameAs(rig.Camera, "the gate prefers the camera over the 'No Camera' entry");
            await rig.Engine.Camera.DisconnectAsync();
            rig.Engine.CameraGate.IsOpen.Should().BeFalse("disconnecting closes the gate again");
        }

        [Test]
        public async Task RigProfile_CarriesTheAppSettings_SirilPatterns_AndTheDriverIds() {
            await using var rig = new EngineRig("profile", s => {
                s.Gain = 200;
                s.Offset = 15;
                s.Optics.Bin = 2;
                s.Optics.UseReducer = true;
                s.FocuserSpeed = 3;
            });
            var p = rig.Engine.Profile.ActiveProfile;
            CoreUtil.APPLICATIONTEMPPATH.Should().Be(EngineRuntime.DataDirectory, "NINA's data folder is the engine's");
            p.AstrometrySettings.Latitude.Should().Be(22.25);
            p.AstrometrySettings.Longitude.Should().Be(114.18);
            p.TelescopeSettings.Id.Should().Be(Lx200Telescope.DeviceId);
            p.TelescopeSettings.FocalLength.Should().Be(1575, "the reducer is fitted");
            p.TelescopeSettings.TelescopeLocationSyncDirection.Should().Be(TelescopeLocationSyncDirection.NOSYNC, "the lat/long prompt is a dialog");
            p.FocuserSettings.Id.Should().Be(Lx200Focuser.DeviceId);
            p.GuiderSettings.GuiderName.Should().Be("Direct_Guider", "mount-only dithers through NINA's DirectGuider");
            p.CameraSettings.Gain.Should().Be(200);
            p.CameraSettings.Offset.Should().Be(15);
            p.CameraSettings.BinningX.Should().Be(2);
            p.CameraSettings.ZwoAsiMonoBinMode.Should().BeFalse();
            p.ImageFileSettings.FilePath.Should().Be(rig.ImagesRoot);
            p.ImageFileSettings.FilePattern.Should().Be(NinaFilePatterns.Light);
            p.ImageFileSettings.FilePatternFLAT.Should().Be(NinaFilePatterns.Flat);
            p.ImageFileSettings.FilePatternBIAS.Should().Be(NinaFilePatterns.Bias);
            p.ImageFileSettings.FilePatternDARK.Should().Be(NinaFilePatterns.Dark);
            p.ImageFileSettings.FileType.Should().Be(FileTypeEnum.FITS);
            p.PlateSolveSettings.PlateSolverType.Should().Be(PlateSolverEnum.ASTAP);
            p.PlateSolveSettings.BlindSolverType.Should().Be(BlindSolverEnum.LOCAL, "solve-field is the blind fallback, never ASTAP -r 180");
            p.PlateSolveSettings.Threshold.Should().Be(1.0);
            new Lx200Settings(rig.Engine.Profile).FocuserSpeed.Should().Be(3);
            rig.Engine.Solvers.Problem.Should().Contain("ASTAP not found", "the test has no ASTAP; the app shows why");
            File.Exists(EngineProfileService.ProfilePath).Should().BeTrue("the rig profile is a NINA profile file");
        }

        [Test]
        public async Task RigProfile_KeepsTheDriverSettings_BetweenStarts() {
            await using (var first = new EngineRig("profile keep")) {
                first.Engine.Mount.SelectPort("/dev/cu.usbserial-KEEP");
                new Lx200Settings(first.Engine.Profile) { DateConvention = Lx200DateConvention.Utc };
                first.Engine.Profile.Save();
            }
            // A new start opens the same NINA profile file and finds the driver's plugin-store values
            var service = new EngineProfileService();
            try {
                service.LoadedFromFile.Should().BeTrue();
                service.LoadWarning.Should().BeNull();
                service.ActiveProfile.Id.Should().Be(EngineProfileService.RigProfileId);
                var lx = new Lx200Settings(service);
                lx.PortPath.Should().Be("/dev/cu.usbserial-KEEP");
                lx.DateConvention.Should().Be(Lx200DateConvention.Utc);
            } finally {
                service.Release();
            }
        }

        [Test]
        public void PlateSolving_IsAstapWithItsDatabase_ThenSolveFieldBlind() {
            var folder = TestHost.NewFolder("solvers");
            var astap = Path.Combine(folder, "astap cli", "astap_cli");
            Directory.CreateDirectory(Path.GetDirectoryName(astap));
            File.WriteAllText(astap, "#!/bin/sh\nexit 1\n");
            var database = Path.Combine(folder, "d80");
            Directory.CreateDirectory(database);
            var settings = new AppSettings();
            settings.Solver.AstapExecutable = astap;
            settings.Solver.AstapDatabase = database;
            var service = new EngineProfileService();
            try {
                var setup = RigProfile.Apply(service, settings, Path.Combine(folder, "images"));
                setup.Problem.Should().BeNull();
                setup.AstapLocation.Should().Be(AstapSetup.LauncherPath, "NINA's ASTAP solver cannot pass -d; the launcher does");
                File.ReadAllText(setup.AstapLocation).Should().Contain(" -d ").And.Contain(database);
                var ps = service.ActiveProfile.PlateSolveSettings;
                ps.ASTAPLocation.Should().Be(setup.AstapLocation);
                ps.CygwinLocation.Should().Be("/opt/homebrew/bin");
                ps.BlindFailoverEnabled.Should().BeTrue();
                ps.DownSampleFactor.Should().Be(2);
                ps.SearchRadius.Should().Be(5);
                ps.ExposureTime.Should().Be(15);
                ps.Gain.Should().Be(450);
                ps.Binning.Should().Be(2);
                // NINA's own factory (what the app uses without a test solver): ASTAP near, the local solve-field blind
                var factory = new PlateSolverFactoryProxy();
                factory.GetPlateSolver(ps).GetType().Name.Should().Be("ASTAPSolver");
                factory.GetBlindSolver(ps).GetType().Name.Should().Be("LocalPlateSolver");
            } finally {
                service.Release();
            }
        }

        [Test]
        public void SolverPaths_ExpandHome() {
            RigProfile.ExpandHome("~/Astro/astap/cli/astap_cli", "/Users/test").Should().Be("/Users/test/Astro/astap/cli/astap_cli");
            RigProfile.ExpandHome("/opt/homebrew/bin", "/Users/test").Should().Be("/opt/homebrew/bin");
            RigProfile.ExpandHome("~", "/Users/test").Should().Be("/Users/test");
        }

        [Test]
        public void EngineRuntime_RefusesToMoveTheDataFolder() {
            var current = EngineRuntime.DataDirectory;
            current.Should().NotBeNull();
            FluentActions.Invoking(() => EngineRuntime.Initialize(current)).Should().NotThrow("the same folder again is fine");
            FluentActions.Invoking(() => EngineRuntime.Initialize(Path.Combine(Path.GetTempPath(), "elsewhere"))).Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void SirilFolders_FollowNinasPatterns() {
            var now = new DateTimeOffset(2026, 10, 10, 21, 0, 0, TimeSpan.FromHours(8));
            var hk = TimeZoneInfo.FindSystemTimeZoneById("Asia/Hong_Kong");
            var folders = new SirilFolders("/r", now, hk);
            folders.NightDirectory.Should().Be("/r/2026-10-10");
            folders.LightsDirectory("NGC 253").Should().Be("/r/2026-10-10/NGC 253/lights");
            folders.FlatsDirectory.Should().Be("/r/2026-10-10/flats");
            folders.BiasesDirectory.Should().Be("/r/2026-10-10/biases");
            folders.DarksDirectory.Should().Be("/r/library/darks");
            folders.LightsDirectory("M31:Core").Should().Be("/r/2026-10-10/M31_Core/lights", "Windows' invalid characters become '_' as NINA writes them");
            folders.LightsDirectory("$weird").Should().Be("/r/2026-10-10/_weird/lights");
            EngineSessionService.SequenceTargetName("$weird").Should().Be("_weird", "a name NINA would keep but Siril cannot is sanitised before NINA sees it");
            EngineSessionService.SequenceTargetName("NGC 253").Should().Be("NGC 253");
            // After midnight the night is still the evening's
            new SirilFolders("/r", now.AddHours(6), hk).NightDirectory.Should().Be("/r/2026-10-10");
        }
    }
}
