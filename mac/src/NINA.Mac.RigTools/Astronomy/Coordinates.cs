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
using System.Globalization;

namespace NINA.Mac.RigTools.Astronomy {

    /// <summary>Right ascension (hours) and declination (degrees).</summary>
    public readonly struct EquatorialCoordinates {

        public EquatorialCoordinates(double raHours, double decDeg) {
            if (double.IsNaN(raHours) || double.IsInfinity(raHours)) {
                throw new ArgumentOutOfRangeException(nameof(raHours));
            }
            if (!(decDeg >= -90.0 && decDeg <= 90.0)) {
                throw new ArgumentOutOfRangeException(nameof(decDeg), decDeg, "Declination must be within [-90, 90]");
            }
            RaHours = AngleMath.Normalize24(raHours);
            DecDeg = decDeg;
        }

        public double RaHours { get; }
        public double DecDeg { get; }

        public override string ToString() {
            return $"RA {AngleMath.FormatHours(RaHours)} Dec {AngleMath.FormatDegrees(DecDeg)}";
        }
    }

    /// <summary>Altitude above the geometric horizon and azimuth measured from north through east, both in degrees.</summary>
    public readonly struct HorizontalCoordinates {

        public HorizontalCoordinates(double altitudeDeg, double azimuthDeg) {
            AltitudeDeg = altitudeDeg;
            AzimuthDeg = azimuthDeg;
        }

        public double AltitudeDeg { get; }

        /// <summary>Azimuth in [0, 360): 0 = N, 90 = E, 180 = S, 270 = W (NINA and LX200 convention).</summary>
        public double AzimuthDeg { get; }

        public override string ToString() {
            return string.Format(CultureInfo.InvariantCulture, "Alt {0:0.000} Az {1:0.000}", AltitudeDeg, AzimuthDeg);
        }
    }
}
