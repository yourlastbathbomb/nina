#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Reflection;
using ZWOptical.ASISDK;

namespace NINA.Mac.ZwoProbe {

    /// <summary>
    /// The private structs that upstream ASICameraDll.cs marshals the SDK's ASI_CAMERA_INFO / ASI_CONTROL_CAPS through.
    /// They declare the SDK's C "long" fields as CLong (8 bytes on arm64); the public structs keep upstream's int fields,
    /// so their own layout is the Windows one. The selftest checks the native layout against the arm64 header.
    /// </summary>
    public static class SdkNativeLayout {

        public static Type CameraInfo { get; } = Nested("ASI_CAMERA_INFO_NATIVE");

        public static Type ControlCaps { get; } = Nested("ASI_CONTROL_CAPS_NATIVE");

        private static Type Nested(string name) {
            return typeof(ASICameraDll).GetNestedType(name, BindingFlags.NonPublic)
                ?? throw new MissingMemberException(typeof(ASICameraDll).FullName, name);
        }
    }
}
