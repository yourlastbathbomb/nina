#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Reflection;
using System.Runtime.InteropServices;
using ZWOptical.ASISDK;
using static ZWOptical.ASISDK.ASICameraDll;

namespace NINA.Mac.ZwoProbe {

    /// <summary>
    /// ZWO gain/offset presets for <c>zwoprobe info</c>. Declared here rather than in upstream ASICameraDll.cs, which only wraps
    /// what NINA itself uses. Same library name as upstream, so <c>NativeLibraries.Register(typeof(ASICameraDll).Assembly)</c>
    /// (this assembly) resolves it. Both functions take <c>int*</c> in ASICamera2.h (SDK 1.41), so no C long is involved.
    /// The probe calls them from its single command thread, so upstream's SDK lock is not needed.
    /// </summary>
    public static class GainPresets {
        private const string DLLNAME = "ASICamera2.dll";

        [DllImport(DLLNAME, EntryPoint = "ASIGetGainOffset", CallingConvention = CallingConvention.Cdecl)]
        private static extern ASI_ERROR_CODE ASIGetGainOffset(int iCameraID, out int pOffset_HighestDR, out int pOffset_UnityGain, out int pGain_LowestRN, out int pOffset_LowestRN);

        [DllImport(DLLNAME, EntryPoint = "ASIGetLMHGainOffset", CallingConvention = CallingConvention.Cdecl)]
        private static extern ASI_ERROR_CODE ASIGetLMHGainOffset(int iCameraID, out int pLGain, out int pMGain, out int pHGain, out int pHOffset);

        public static void GetGainOffset(int cameraId, out int offsetHighestDR, out int offsetUnityGain, out int gainLowestRN, out int offsetLowestRN) {
            Check(ASIGetGainOffset(cameraId, out offsetHighestDR, out offsetUnityGain, out gainLowestRN, out offsetLowestRN), MethodBase.GetCurrentMethod(), cameraId);
        }

        public static void GetLMHGainOffset(int cameraId, out int lowGain, out int mediumGain, out int highGain, out int highOffset) {
            Check(ASIGetLMHGainOffset(cameraId, out lowGain, out mediumGain, out highGain, out highOffset), MethodBase.GetCurrentMethod(), cameraId);
        }

        private static void Check(ASI_ERROR_CODE errorCode, MethodBase callingMethod, int cameraId) {
            if (errorCode != ASI_ERROR_CODE.ASI_SUCCESS) {
                throw new ASICameraException(errorCode, callingMethod, new object[] { cameraId });
            }
        }
    }
}
