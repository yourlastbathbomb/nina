#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using System;

namespace NINA.PlateSolving.Mac {

    /// <summary>
    /// Mac-only: plate-solve settings for this rig (ASI585MC Pro bin 2 behind the LX200GPS 10" at f/10, 2500 mm, through the
    /// ALP-T dual-band filter), from MAC_PORT_PLAN.md M6 and risk 4. The native field is 15.3' x 8.6' (0.479"/px at bin 2,
    /// ASTAP -fov 0.1436 deg), just under the 0.15 deg that ASTAP documents as D80's smallest field, so the near solver is ASTAP
    /// with D80 on longer, high-gain solve frames, downsampled by 2 (which also merges the Bayer mosaic of a bin-2 frame), and
    /// the blind solver is the local solve-field with its scale hints, never ASTAP's all-sky -r 180 search.
    /// The optical train itself (focal length 2500 mm native, about 1575-1640 mm with the reducer) lives in the telescope
    /// settings, one profile per train.
    /// </summary>
    public static class RigPlateSolveDefaults {

        /// <summary>Solve-frame exposure in seconds (risk 4: 10-20 s at high gain).</summary>
        public const double ExposureTime = 15d;

        /// <summary>The SDK's lowest-read-noise preset for the ASI585MC (ASIGetGainOffset, M1 results: gain 450).</summary>
        public const int Gain = 450;

        /// <summary>Solve frames at bin 2, as the lights.</summary>
        public const short Binning = 2;

        /// <summary>ASTAP -z 2 and solve-field --downsample 2 (merges the RGGB mosaic of a bin-2 OSC frame).</summary>
        public const int DownSampleFactor = 2;

        /// <summary>Near-solve search radius in degrees after a handset alignment (research SLV-08: 2-5 deg, not NINA's 30).</summary>
        public const double SearchRadius = 5d;

        /// <summary>
        /// Applies the rig's plate-solve settings. <paramref name="astapLocation"/> is the value from
        /// <see cref="AstapSetup.Resolve"/>; <paramref name="astrometryBinDirectory"/> the folder with solve-field (Homebrew's
        /// by default). Every other setting (threshold, attempts, filter, max objects) keeps its value.
        /// </summary>
        public static void Apply(IPlateSolveSettings settings, string astapLocation, string astrometryBinDirectory = AstrometryNetSetup.HomebrewBinDirectory) {
            ArgumentNullException.ThrowIfNull(settings);
            settings.PlateSolverType = PlateSolverEnum.ASTAP;
            settings.ASTAPLocation = astapLocation ?? string.Empty;
            settings.BlindSolverType = BlindSolverEnum.LOCAL;
            settings.CygwinLocation = astrometryBinDirectory ?? string.Empty;
            settings.BlindFailoverEnabled = true;
            settings.DownSampleFactor = DownSampleFactor;
            settings.SearchRadius = SearchRadius;
            settings.ExposureTime = ExposureTime;
            settings.Gain = Gain;
            settings.Binning = Binning;
        }
    }
}
