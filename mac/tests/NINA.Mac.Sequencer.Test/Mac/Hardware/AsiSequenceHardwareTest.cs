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
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Mac;
using NINA.Mac.Image.Test;
using NINA.Mac.Sequencer.Test.Sim;
using NINA.Mac.Sequencing.Planning;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// A generated sequence on the real ZWO ASI585MC Pro (USB) with the simulated mount: NINA.Equipment.Mac's CameraChooser lists the
    /// camera, NINA's CameraVM connects it through PersistSettingsCameraDecorator, and three 1 s bin-2 lights go through NINA's
    /// ImagingVM and ImageSaveController into FITS files. No cooling is requested; the host switches the cooler off before it
    /// disconnects in any case (finally block). Run by hand:
    /// <c>mac/dotnet test mac/tests/NINA.Mac.Sequencer.Test/NINA.Mac.Sequencer.Test.csproj --filter "FullyQualifiedName~AsiSequenceHardwareTest"</c>.
    /// Inconclusive when no ZWO camera is on USB.
    /// </summary>
    [TestFixture]
    [Explicit("Needs the ZWO ASI585MC Pro on USB")]
    [Category("Hardware")]
    public class AsiSequenceHardwareTest {

        [Test]
        public async Task GeneratedSequence_TakesThreeBinTwoLights_OnTheRealCamera() {
            if (ASICameras.Count == 0) {
                Assert.Inconclusive("No ZWO camera connected (ASICameras.Count = 0)");
            }
            var now = DateTime.Now;
            var coordinates = Sky.TargetAt(45, rising: true, decDeg: 0, now);
            SimRig? rig = null;
            try {
                rig = await SimRig.Create("asi hardware", coordinates, 0, 0, new FixedNighttimeCalculator(now.AddHours(1)),
                    configure: p => p.CameraSettings.Id = "",
                    cameraChooser: (profile, factory) => new FirstZwoCamera(profile, factory));
                var camera = (ICamera)rig.Host.CameraMediator.GetDevice();
                TestContext.Out.WriteLine($"camera: {camera.Name} ({camera.Id}), {camera.CameraXSize} x {camera.CameraYSize}, cooler {(camera.CoolerOn ? "on" : "off")}");
                var root = rig.Host.Generator.Generate(new NightPlan {
                    Name = "Hardware", CoolToC = null, WarmAtEnd = false, DewHeater = false,
                    Targets = new[] { new TargetPlan { Name = "ASI test", Coordinates = coordinates, ExposureSeconds = 1, Count = 3, Gain = 252, Offset = 15, DitherEvery = 0, RecenterArcmin = 0, CenterFirst = false } }
                });
                var run = await NightRun.Run(root, TimeSpan.FromMinutes(2));
                var files = await rig.WaitForFiles(3, TimeSpan.FromSeconds(30));
                TestContext.Out.WriteLine(string.Join("\n", files));
                files.Should().HaveCount(3);
                foreach (var file in files) {
                    var fits = FitsFile.Read(Path.Combine(rig.ImageFolder, file));
                    fits.Width.Should().Be(1920);
                    fits.Height.Should().Be(1080);
                    fits.Value("BAYERPAT").Should().Be("RGGB");
                    fits.Integer("XBINNING").Should().Be(2);
                    fits.Integer("GAIN").Should().Be(252);
                    fits.Value("OBJECT").Should().Be("ASI test");
                }
            } finally {
                if (rig != null) {
                    // HeadlessHost.DisconnectAsync switches the cooler off before closing the camera
                    await rig.DisposeAsync();
                }
            }
        }

        /// <summary>NINA.Equipment.Mac's CameraChooser, with the first ZWO camera selected whatever the profile says.</summary>
        private sealed class FirstZwoCamera : CameraChooser {

            public FirstZwoCamera(NINA.Profile.Interfaces.IProfileService profile, NINA.Image.Interfaces.IExposureDataFactory factory) : base(profile, factory) {
            }

            public override async Task GetEquipment() {
                await base.GetEquipment();
                SelectedDevice = Devices.First(d => d is ASICamera || d.Category == "ZWOptical");
            }
        }
    }
}
