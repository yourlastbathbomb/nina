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
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using ZWOptical.ASISDK;
using static ZWOptical.ASISDK.ASICameraDll;

namespace NINA.Mac.Test {

    /// <summary>
    /// ZWO ASICamera2.h declares several fields as C "long" (8 bytes on LP64 macOS/Linux, 4 on Windows).
    /// ASICameraDll marshals the SDK structs through private mirrors (ASI_CAMERA_INFO_NATIVE, ASI_CONTROL_CAPS_NATIVE) that use
    /// CLong for those fields; the public structs keep upstream's int fields, so plugins built against NINA.Equipment keep working.
    /// Expected values come from compiling the SDK 1.41 header with clang -arch arm64 (offsetof/sizeof).
    /// Offsets are asserted, not only sizes, so a wrongly ordered fix is caught too.
    /// </summary>
    [TestFixture]
    public class AsiAbiTest {
        private static readonly Type cameraInfoNative = Native("ASI_CAMERA_INFO_NATIVE");
        private static readonly Type controlCapsNative = Native("ASI_CONTROL_CAPS_NATIVE");
        private static readonly string[] cameraInfoLongs = { nameof(ASI_CAMERA_INFO.MaxHeight), nameof(ASI_CAMERA_INFO.MaxWidth) };
        private static readonly string[] controlCapsLongs = { nameof(ASI_CONTROL_CAPS.MaxValue), nameof(ASI_CONTROL_CAPS.MinValue), nameof(ASI_CONTROL_CAPS.DefaultValue) };

        private static Type Native(string name) {
            return typeof(ASICameraDll).GetNestedType(name, BindingFlags.NonPublic);
        }

        [TestCase(nameof(ASI_CAMERA_INFO.CameraID), 64)]
        [TestCase(nameof(ASI_CAMERA_INFO.MaxHeight), 72)]
        [TestCase(nameof(ASI_CAMERA_INFO.MaxWidth), 80)]
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
            Marshal.OffsetOf(cameraInfoNative, field).ToInt32().Should().Be(expected);
        }

        [Test]
        public void CameraInfo_Size_MatchesArm64Header() {
            Marshal.SizeOf(cameraInfoNative).Should().Be(248);
        }

        [TestCase(nameof(ASI_CONTROL_CAPS.MaxValue), 192)]
        [TestCase(nameof(ASI_CONTROL_CAPS.MinValue), 200)]
        [TestCase(nameof(ASI_CONTROL_CAPS.DefaultValue), 208)]
        [TestCase(nameof(ASI_CONTROL_CAPS.IsAutoSupported), 216)]
        [TestCase(nameof(ASI_CONTROL_CAPS.IsWritable), 220)]
        [TestCase(nameof(ASI_CONTROL_CAPS.ControlType), 224)]
        [TestCase(nameof(ASI_CONTROL_CAPS.Unused), 228)]
        public void ControlCaps_FieldOffsets_MatchArm64Header(string field, int expected) {
            Marshal.OffsetOf(controlCapsNative, field).ToInt32().Should().Be(expected);
        }

        [Test]
        public void ControlCaps_Size_MatchesArm64Header() {
            Marshal.SizeOf(controlCapsNative).Should().Be(264);
        }

        [Test]
        public void CameraInfo_LongFields_RoundTripThroughIntFields() {
            // Simulate what the SDK writes on arm64: 8-byte longs at 72/80, int fields after
            var buffer = Marshal.AllocHGlobal(248);
            try {
                for (var i = 0; i < 248; i++) { Marshal.WriteByte(buffer, i, 0); }
                Marshal.WriteInt64(buffer, 72, 2160);
                Marshal.WriteInt64(buffer, 80, 3840);
                Marshal.WriteInt32(buffer, 88, 1);
                Marshal.WriteInt32(buffer, 96, 1);
                Marshal.WriteInt32(buffer, 100, 2);
                Marshal.WriteInt64(buffer, 192, BitConverter.DoubleToInt64Bits(2.9));

                var info = (ASI_CAMERA_INFO)ToManaged(Marshal.PtrToStructure(buffer, cameraInfoNative));

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
        public void ControlCaps_LongFields_RoundTripThroughIntFields() {
            var buffer = Marshal.AllocHGlobal(264);
            try {
                for (var i = 0; i < 264; i++) { Marshal.WriteByte(buffer, i, 0); }
                Marshal.WriteByte(buffer, 0, (byte)'G');
                Marshal.WriteInt64(buffer, 192, 2_000_000_000);
                Marshal.WriteInt64(buffer, 200, -40);
                Marshal.WriteInt64(buffer, 208, 252);
                Marshal.WriteInt32(buffer, 216, 1);
                Marshal.WriteInt32(buffer, 220, 1);
                Marshal.WriteInt32(buffer, 224, (int)ASI_CONTROL_TYPE.ASI_TARGET_TEMP);

                var caps = (ASI_CONTROL_CAPS)ToManaged(Marshal.PtrToStructure(buffer, controlCapsNative));

                caps.Name.Should().Be("G");
                caps.MaxValue.Should().Be(2_000_000_000);
                caps.MinValue.Should().Be(-40);
                caps.DefaultValue.Should().Be(252);
                caps.IsAutoSupported.Should().Be(ASI_BOOL.ASI_TRUE);
                caps.IsWritable.Should().Be(ASI_BOOL.ASI_TRUE);
                caps.ControlType.Should().Be(ASI_CONTROL_TYPE.ASI_TARGET_TEMP);
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Test]
        public void PublicStructs_KeepUpstreamIntFields() {
            // NINA.Equipment is a NuGet package for plugins: turning these fields into properties would break compiled plugins
            foreach (var (type, names) in new[] { (typeof(ASI_CAMERA_INFO), cameraInfoLongs), (typeof(ASI_CONTROL_CAPS), controlCapsLongs) }) {
                foreach (var name in names) {
                    var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                    field.Should().NotBeNull($"{type.Name}.{name} must stay a public field");
                    field.FieldType.Should().Be(typeof(int), $"{type.Name}.{name}");
                }
            }
        }

        [TestCase(typeof(ASI_CAMERA_INFO), "ASI_CAMERA_INFO_NATIVE")]
        [TestCase(typeof(ASI_CONTROL_CAPS), "ASI_CONTROL_CAPS_NATIVE")]
        public void NativeMirror_MatchesThePublicStruct_FieldForField(Type publicType, string nativeName) {
            // An upstream change to the public struct (a new SDK field) must be mirrored, or ToManaged would drop it
            var longs = publicType == typeof(ASI_CAMERA_INFO) ? cameraInfoLongs : controlCapsLongs;
            var native = Native(nativeName);
            native.Should().NotBeNull();
            var publicFields = Fields(publicType);
            var nativeFields = Fields(native);
            nativeFields.Select(f => f.Name).Should().Equal(publicFields.Select(f => f.Name));
            foreach (var (pub, nat) in publicFields.Zip(nativeFields)) {
                nat.FieldType.Should().Be(longs.Contains(pub.Name) ? typeof(CLong) : pub.FieldType, pub.Name);
                SizeConst(nat).Should().Be(SizeConst(pub), pub.Name);
            }

            // ToManaged copies every field: give each a distinct value and read it back
            var source = Activator.CreateInstance(native);
            var seed = 1;
            foreach (var field in nativeFields) {
                field.SetValue(source, SampleValue(field.FieldType, seed++));
            }
            var converted = ToManaged(source);
            seed = 1;
            foreach (var field in publicFields) {
                var expected = SampleValue(field.FieldType, seed++);
                field.GetValue(converted).Should().BeEquivalentTo(expected, options => options.WithStrictOrdering(), field.Name);
            }
        }

        private static FieldInfo[] Fields(Type type) {
            return type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderBy(f => f.MetadataToken).ToArray();
        }

        private static int? SizeConst(FieldInfo field) {
            return field.GetCustomAttribute<MarshalAsAttribute>()?.SizeConst;
        }

        private static object SampleValue(Type type, int seed) {
            if (type == typeof(CLong) || type == typeof(int)) {
                return type == typeof(CLong) ? new CLong(seed * 1000) : seed * 1000;
            }
            if (type == typeof(double)) { return seed + 0.25; }
            if (type == typeof(float)) { return seed + 0.5f; }
            if (type.IsEnum) { return Enum.ToObject(type, seed % 2); }
            if (type.IsArray) {
                var array = Array.CreateInstance(type.GetElementType(), 2);
                array.SetValue(SampleValue(type.GetElementType(), seed), 0);
                return array;
            }
            if (type == typeof(byte)) { return (byte)seed; }
            throw new NotSupportedException(type.FullName);
        }

        private static object ToManaged(object native) {
            return native.GetType().GetMethod("ToManaged").Invoke(native, null);
        }

        [Test]
        public void SdkDylib_IsStagedAndLoads() {
            NativeLibraries.Register(typeof(ASICameraDll).Assembly);
            NativeLibraries.Locate("ASICamera2.dll").Should().NotBeNull("run mac/scripts/stage-zwo.sh first");

            GetSDKVersion().Should().StartWith("1, 4");
        }

        [TestCase("ASIGetGainOffset")]
        [TestCase("ASIGetLMHGainOffset")]
        public void GainPresetEntryPoints_AreExportedByTheStagedSdk(string entryPoint) {
            // NINA.Mac.ZwoProbe.GainPresets declares these itself; upstream ASICameraDll.cs does not wrap them
            var path = NativeLibraries.Locate("ASICamera2.dll");
            path.Should().NotBeNull("run mac/scripts/stage-zwo.sh first");
            var handle = NativeLibrary.Load(path);
            NativeLibrary.TryGetExport(handle, entryPoint, out _).Should().BeTrue();
        }
    }
}
