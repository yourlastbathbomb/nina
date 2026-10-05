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
using NINA.Core.Enum;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;

namespace NINA.Mac.Equipment.Test {

    /// <summary>
    /// Upstream DirectGuider ("Mount Dither"), the rig's only dithering, with this rig's numbers: ASI585MC 2.9 µm pixels on the
    /// 2500 mm LX200GPS. It dithers by calling <see cref="ITelescopeMediator.PulseGuide"/>, which the M4 LX200 driver serves.
    /// The profile is a real NINA Profile; the random offsets come from upstream's test seam (DitherOffsetSelector's
    /// candidate factory), so the pulses are exact.
    /// </summary>
    [TestFixture]
    public class DirectGuiderRigTest {
        private const double PixelSize = 2.9;
        private const double FocalLength = 2500;

        private NINA.Profile.Profile profile = null!;
        private Mock<IProfileService> profileService = null!;
        private Mock<ITelescopeMediator> telescopeMediator = null!;
        private List<(GuideDirections Direction, int Milliseconds)> pulses = null!;

        [SetUp]
        public void SetUp() {
            profile = new NINA.Profile.Profile("Rig");
            profile.CameraSettings.PixelSize = PixelSize;
            profile.TelescopeSettings.FocalLength = FocalLength;
            profile.GuiderSettings.SettleTime = 0;
            profileService = new Mock<IProfileService>();
            profileService.SetupGet(x => x.ActiveProfile).Returns(profile);
            telescopeMediator = new Mock<ITelescopeMediator>();
            pulses = new List<(GuideDirections, int)>();
            telescopeMediator.Setup(x => x.PulseGuide(It.IsAny<GuideDirections>(), It.IsAny<int>()))
                .Callback<GuideDirections, int>((direction, ms) => pulses.Add((direction, ms)));
        }

        private DirectGuider Guider(params (double WestEast, double NorthSouth)[] offsets) {
            var queue = new Queue<DitherOffset>(offsets.Select(o => new DitherOffset(o.WestEast, o.NorthSouth)));
            return new DirectGuider(profileService.Object, telescopeMediator.Object, new DitherOffsetSelector(_ => queue.Dequeue()));
        }

        private static TelescopeInfo Mount(bool connected = true, double guideRate = double.NaN) {
            return new TelescopeInfo {
                Connected = connected,
                GuideRateRightAscensionArcsecPerSec = guideRate,
                GuideRateDeclinationArcsecPerSec = guideRate,
            };
        }

        private static int Milliseconds(double pixels, double pixelScale, double rate) {
            return (int)Math.Round(TimeSpan.FromSeconds(Math.Abs(pixels) * pixelScale / rate).TotalMilliseconds);
        }

        [Test]
        public void MountInfo_SetsTheRigsPixelScale_AndHalfSiderealWhenTheMountReportsNoGuideRate() {
            var guider = Guider();

            guider.UpdateDeviceInfo(Mount());

            var pixelScale = AstroUtil.ArcsecPerPixel(PixelSize, FocalLength);
            pixelScale.Should().BeApproximately(0.2393, 0.0001, "206.265 x 2.9 µm / 2500 mm");
            guider.PixelScale.Should().Be(pixelScale);
            guider.WestEastGuideRate.Should().Be(AstroUtil.SIDEREAL_RATE_ARCSECONDS_PER_SECOND / 2);
            guider.NorthSouthGuideRate.Should().Be(AstroUtil.SIDEREAL_RATE_ARCSECONDS_PER_SECOND / 2);
            guider.DirectGuideDuration.Should().BeApproximately(profile.GuiderSettings.DitherPixels * pixelScale / (15.041 / 2), 1e-12);
            telescopeMediator.Verify(x => x.RegisterConsumer(guider), Times.Once);
        }

        [Test]
        public async Task Connect_FollowsTheMount() {
            var guider = Guider();

            (await guider.Connect(CancellationToken.None)).Should().BeFalse("the mount is not connected");

            guider.UpdateDeviceInfo(Mount());
            (await guider.Connect(CancellationToken.None)).Should().BeTrue();
            guider.Connected.Should().BeTrue();

            var posted = new List<NotificationPostedEventArgs>();
            EventHandler<NotificationPostedEventArgs> handler = (_, e) => posted.Add(e);
            Notification.Posted += handler;
            try {
                guider.UpdateDeviceInfo(Mount(connected: false));
            } finally {
                Notification.Posted -= handler;
            }
            guider.Connected.Should().BeFalse("DirectGuider disconnects when the mount goes away");
            posted.Should().ContainSingle(p => p.Kind == NotificationKind.Warning);
        }

        [Test]
        public async Task Dither_PulsesEachAxisFromThePreviousOffset_AtTheMountsGuideRate() {
            var guider = Guider((3, -4), (-2, 1));
            guider.UpdateDeviceInfo(Mount(guideRate: 7.5));
            await guider.Connect(CancellationToken.None);
            var scale = guider.PixelScale;

            (await guider.Dither(null!, CancellationToken.None)).Should().BeTrue();
            (await guider.Dither(null!, CancellationToken.None)).Should().BeTrue();

            pulses.Should().Equal(
                (GuideDirections.guideEast, Milliseconds(3, scale, 7.5)),
                (GuideDirections.guideSouth, Milliseconds(4, scale, 7.5)),
                (GuideDirections.guideWest, Milliseconds(5, scale, 7.5)),
                (GuideDirections.guideNorth, Milliseconds(5, scale, 7.5)));
            pulses[0].Milliseconds.Should().Be(96, "3 px x 0.2393\"/px at 7.5\"/s");
            guider.State.Should().Be("Idle");
        }

        [Test]
        public async Task Dither_RaOnly_PulsesOnlyEastWest() {
            profile.GuiderSettings.DitherRAOnly = true;
            var guider = Guider((-3, 0));
            guider.UpdateDeviceInfo(Mount());

            (await guider.Dither(null!, CancellationToken.None)).Should().BeTrue();

            pulses.Should().ContainSingle().Which.Direction.Should().Be(GuideDirections.guideWest);
        }

        [Test]
        public async Task Dither_WaitsWhileTheMountIsStillPulseGuiding() {
            var guider = Guider((1, 1));
            var busy = Mount(guideRate: 7.5);
            busy.IsPulseGuiding = true;
            guider.UpdateDeviceInfo(busy);

            var dither = guider.Dither(null!, CancellationToken.None);
            await Task.Delay(300);
            dither.IsCompleted.Should().BeFalse("the mount still reports IsPulseGuiding");
            guider.UpdateDeviceInfo(Mount(guideRate: 7.5));

            (await dither.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        }

        [Test]
        public async Task Dither_WithoutAMount_DoesNotPulse() {
            var guider = Guider((1, 1));
            guider.UpdateDeviceInfo(Mount(connected: false));

            (await guider.Dither(null!, CancellationToken.None)).Should().BeFalse();

            pulses.Should().BeEmpty();
        }
    }
}
