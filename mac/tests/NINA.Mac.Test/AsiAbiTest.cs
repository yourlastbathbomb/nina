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
using NINA.Mac.Native;
using NUnit.Framework;
using System.Runtime.InteropServices;
using ZWOptical.ASISDK;
using static ZWOptical.ASISDK.ASICameraDll;

namespace NINA.Mac.Test {

    /// <summary>
    /// ZWO ASICamera2.h declares several fields as C "long" (8 bytes on LP64 macOS/Linux, 4 on Windows).
    /// Expected values come from compiling the SDK 1.41 header with clang -arch arm64 (offsetof/sizeof).
    /// Offsets are asserted, not only sizes, so a wrongly ordered fix is caught too.
    /// </summary>
    [TestFixture]
    public class AsiAbiTest {

        [TestCase("CameraID", 64)]
        [TestCase("maxHeight", 72)]
        [TestCase("maxWidth", 80)]
        [TestCase(nameof(ASI_CAMERA_INFO.IsColorCam), 88)]
        [TestCase(nameof(ASI_CAMERA_INFO.BayerPattern), 92)]
        [TestCase(nameof(ASI_CAMERA_INFO.SupportedBins), 96)]
        [TestCase(nameof(ASI_CAMERA_INFO.SupportedVideoFormat), 160)]
        [TestCase(nameof(ASI_CAMERA_INFO.PixelSize), 192)]
        [TestCase(nameof(ASI_CAMERA_INFO.IsCoolerCam), 208)]
        [TestCase(nameof(ASI_CAMERA_INFO.ElecPerADU), 220)]
        [TestCase(nameof(ASI_CAMERA_INFO.BitDepth), 224)]
        [TestCase(nameof(ASI_CAMERA_INFO.IsTriggerCam), 228)]
        [TestCase(nameof(ASI_CAMERA_INFO.Unused), 232)]
        public void CameraInfo_FieldOffsets_MatchArm64Header(string field, int expected) {
            Marshal.OffsetOf<ASI_CAMERA_INFO>(field).ToInt32().Should().Be(expected);
        }

        [Test]
        public void CameraInfo_Size_MatchesArm64Header() {
            Marshal.SizeOf<ASI_CAMERA_INFO>().Should().Be(248);
        }

        [TestCase("maxValue", 192)]
        [TestCase("minValue", 200)]
        [TestCase("defaultValue", 208)]
        [TestCase(nameof(ASI_CONTROL_CAPS.IsAutoSupported), 216)]
        [TestCase(nameof(ASI_CONTROL_CAPS.IsWritable), 220)]
        [TestCase(nameof(ASI_CONTROL_CAPS.ControlType), 224)]
        [TestCase(nameof(ASI_CONTROL_CAPS.Unused), 228)]
        public void ControlCaps_FieldOffsets_MatchArm64Header(string field, int expected) {
            Marshal.OffsetOf<ASI_CONTROL_CAPS>(field).ToInt32().Should().Be(expected);
        }

        [Test]
        public void ControlCaps_Size_MatchesArm64Header() {
            Marshal.SizeOf<ASI_CONTROL_CAPS>().Should().Be(264);
        }

        [Test]
        public void CameraInfo_LongFields_RoundTripThroughIntProperties() {
            // Simulate what the SDK writes on arm64: 8-byte longs at 72/80, int fields after
            var buffer = Marshal.AllocHGlobal(248);
            try {
                for (var i = 0; i < 248; i++) { Marshal.WriteByte(buffer, i, 0); }
                Marshal.WriteInt64(buffer, 72, 2160);
                Marshal.WriteInt64(buffer, 80, 3840);
                Marshal.WriteInt32(buffer, 88, 1);
                Marshal.WriteInt32(buffer, 96, 1);
                Marshal.WriteInt32(buffer, 100, 2);
                Marshal.WriteInt64(buffer, 192, System.BitConverter.DoubleToInt64Bits(2.9));

                var info = Marshal.PtrToStructure<ASI_CAMERA_INFO>(buffer);

                info.MaxWidth.Should().Be(3840);
                info.MaxHeight.Should().Be(2160);
                info.IsColorCam.Should().Be(ASI_BOOL.ASI_TRUE);
                info.SupportedBins[0].Should().Be(1);
                info.SupportedBins[1].Should().Be(2);
                info.PixelSize.Should().Be(2.9);
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Test]
        public void SdkDylib_IsStagedAndLoads() {
            NativeLibraries.Register(typeof(ASICameraDll).Assembly);
            NativeLibraries.Locate("ASICamera2.dll").Should().NotBeNull("run mac/scripts/stage-zwo.sh first");

            GetSDKVersion().Should().StartWith("1, 4");
        }
    }
}
