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
using NINA.Mac.Native;
using System.Runtime.InteropServices;
using ZWOptical.ASISDK;

namespace NINA.Mac.Equipment.Test {

    /// <summary>
    /// NINA.Equipment's own ZWO binding (upstream ASICameraDll.cs compiled into NINA.Equipment.dll) reaches the staged arm64
    /// SDK through the resolver that NINA.Equipment registers for itself (Mac/NativeRegistration.cs): the test host makes no
    /// Register call. Needs mac/scripts/stage-zwo.sh to have run. No camera is opened: the SDK version is a library call and
    /// the camera count only enumerates USB devices.
    /// </summary>
    [TestFixture]
    public class AsiSdkTest {

        [Test]
        public void Binding_IsNinaEquipments_AndMapsToTheStagedDylib() {
            typeof(ASICameraDll).Assembly.GetName().Name.Should().Be("NINA.Equipment");
            NativeLibraries.DylibName("ASICamera2.dll").Should().Be("libASICamera2.dylib");
            var path = NativeLibraries.Locate("ASICamera2.dll");
            path.Should().NotBeNull("mac/scripts/stage-zwo.sh stages libASICamera2.dylib, and NINA.Mac.Native copies it next to the app");
            Path.GetDirectoryName(path).Should().Be(AppContext.BaseDirectory.TrimEnd('/'));
        }

        [Test]
        public void SdkVersion_ComesFromTheArm64Sdk_ThroughNinaEquipmentsResolver() {
            // The first P/Invoke into NINA.Equipment: it only resolves if the module initializer registered the resolver
            ASICameraDll.GetSDKVersion().Should().StartWith("1, 41");
            RuntimeInformation.ProcessArchitecture.Should().Be(Architecture.Arm64);
        }

        [Test]
        public void CameraCount_Enumerates_WithoutOpeningACamera() {
            // Plan m3b §10 item 4 expects 0 with no camera; with the ASI585MC plugged in it is 1. Either way the call must work.
            var count = ASICameras.Count;
            TestContext.Out.WriteLine($"ASICameras.Count = {count}");
            count.Should().BeGreaterThanOrEqualTo(0);
        }

        [Test]
        public void GetCamera_OutOfRange_Throws_AsUpstream() {
            var count = ASICameras.Count;
            FluentActions.Invoking(() => ASICameras.GetCamera(count, null!, null!)).Should().Throw<IndexOutOfRangeException>();
            FluentActions.Invoking(() => ASICameras.GetCamera(-1, null!, null!)).Should().Throw<IndexOutOfRangeException>();
        }
    }
}
