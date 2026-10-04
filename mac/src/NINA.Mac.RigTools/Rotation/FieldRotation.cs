#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.RigTools.Astronomy;
using NINA.Mac.RigTools.Optics;
using System;
using System.Collections.Generic;

namespace NINA.Mac.RigTools.Rotation {

    /// <summary>
    /// Field rotation on an alt-az mount without a rotator (NINA has none of this: 0 matches upstream,
    /// research/rig_investigate_solve.md SLV-16).
    /// <para>
    /// Rate. The camera keeps its orientation to the local vertical, so the frame turns on the sky at the
    /// rate the parallactic angle q changes. Differentiating Meeus eq. (14.1) with respect to hour angle gives
    /// dq/dH = -cos(phi) cos(A) / cos(h) with A measured from north (= cos(phi) cos(A_S) / cos(h) with Meeus'
    /// south-based azimuth); H advances at the Earth's sidereal rotation rate omega, so
    /// <code>  rate = -omega * cos(latitude) * cos(azimuth) / cos(altitude)   [rad/s]</code>
    /// whose magnitude is the standard alt-az field-rotation formula (13.92 deg/h * |cos A| / cos h at
    /// 22.25 N). The tests check it against a numerical derivative of (14.1). The rate is zero due east/west
    /// (A = 90/270) and grows without bound towards the zenith.
    /// </para>
    /// <para>
    /// Max sub. A rotation by theta about the frame centre moves a pixel at radius r by r * theta pixels.
    /// The farthest pixel is the corner at r = half the binned frame diagonal (1101.45 px at bin 2), so
    /// <code>  t_max = (allowed blur px / r px) / |rate|</code>
    /// Focal length cancels: it changes how much sky a pixel covers, not the rotation angle nor the corner's
    /// distance in pixels. So the limit is the same with or without the f/6.3 reducer (MAC_PORT_PLAN.md
    /// section 6). It is in pixels, though: with the reducer each pixel is 1.59x larger on the sky, so the
    /// same 1 px of blur is a larger fraction of the star FWHM (~6.3 px native vs ~4.1 px reduced at 3"
    /// seeing, bin 2).
    /// </para>
    /// </summary>
    public static class FieldRotation {

        /// <summary>Earth's rotation rate relative to the stars, 7.292115e-5 rad/s (IERS Conventions 2010, table 1.1).</summary>
        public const double SiderealRateRadPerSec = 7.292115e-5;

        /// <summary>The rig's default tolerance (decision 4: warn at 1 px of corner blur).</summary>
        public const double DefaultAllowedBlurPx = 1.0;

        /// <summary>Signed field-rotation rate dq/dt in rad/s (positive = parallactic angle increasing). Infinite at the zenith.</summary>
        public static double RateRadPerSec(double latitudeDeg, double altitudeDeg, double azimuthDeg) {
            var cosAlt = Math.Cos(altitudeDeg * AngleMath.DegToRad);
            var numerator = -SiderealRateRadPerSec * Math.Cos(latitudeDeg * AngleMath.DegToRad) * Math.Cos(azimuthDeg * AngleMath.DegToRad);
            if (Math.Abs(cosAlt) < 1e-12) {
                return numerator == 0 ? 0 : Math.Sign(numerator) * double.PositiveInfinity;
            }
            return numerator / cosAlt;
        }

        /// <summary>Signed field-rotation rate in deg/hour.</summary>
        public static double RateDegPerHour(double latitudeDeg, double altitudeDeg, double azimuthDeg) {
            return RateRadPerSec(latitudeDeg, altitudeDeg, azimuthDeg) * AngleMath.RadToDeg * 3600.0;
        }

        /// <summary>Signed field-rotation rate in rad/s for a given hour angle and declination.</summary>
        public static double RateRadPerSecAtHourAngle(double latitudeDeg, double hourAngleHours, double decDeg) {
            var hz = SphericalAstronomy.EquatorialToHorizontal(hourAngleHours, decDeg, latitudeDeg);
            return RateRadPerSec(latitudeDeg, hz.AltitudeDeg, hz.AzimuthDeg);
        }

        /// <summary>
        /// Longest exposure that keeps the corner blur within <paramref name="allowedBlurPx"/>.
        /// Returns +infinity when the rotation rate is zero (due east/west); tracking error then sets the limit.
        /// </summary>
        public static double MaxSubSeconds(double rateRadPerSec, double halfDiagonalPx, double allowedBlurPx) {
            if (!(allowedBlurPx > 0)) { throw new ArgumentOutOfRangeException(nameof(allowedBlurPx)); }
            if (!(halfDiagonalPx > 0)) { throw new ArgumentOutOfRangeException(nameof(halfDiagonalPx)); }
            var rate = Math.Abs(rateRadPerSec);
            if (rate < 1e-15) {
                return double.PositiveInfinity;
            }
            return (allowedBlurPx / halfDiagonalPx) / rate;
        }

        /// <summary>Longest exposure for 1 px (or the given) corner blur at an altitude/azimuth.</summary>
        public static double MaxSubSeconds(double latitudeDeg, double altitudeDeg, double azimuthDeg, ImagingTrain train, double allowedBlurPx = DefaultAllowedBlurPx) {
            return MaxSubSeconds(RateRadPerSec(latitudeDeg, altitudeDeg, azimuthDeg), train.HalfDiagonalPx, allowedBlurPx);
        }

        /// <summary>Longest exposure for 1 px (or the given) corner blur at an hour angle and declination.</summary>
        public static double MaxSubSecondsAtHourAngle(double latitudeDeg, double hourAngleHours, double decDeg, ImagingTrain train, double allowedBlurPx = DefaultAllowedBlurPx) {
            return MaxSubSeconds(RateRadPerSecAtHourAngle(latitudeDeg, hourAngleHours, decDeg), train.HalfDiagonalPx, allowedBlurPx);
        }

        /// <summary>Corner blur in pixels that an exposure collects at a given rate.</summary>
        public static double CornerBlurPx(double rateRadPerSec, double exposureSeconds, double halfDiagonalPx) {
            return Math.Abs(rateRadPerSec) * exposureSeconds * halfDiagonalPx;
        }

        /// <summary>
        /// How far the frame turns on the sky between two hour angles: the net change and the full span
        /// (max - min) of the unwrapped parallactic angle, sampled every ~1 minute of hour angle. The span is what
        /// shrinks the full-depth area of a stack; it equals |net| unless the rate changes sign (objects that
        /// cross azimuth 90 or 270 inside the interval).
        /// </summary>
        public static FrameRotation OverHourAngles(double latitudeDeg, double decDeg, double startHourAngleHours, double endHourAngleHours) {
            var span = endHourAngleHours - startHourAngleHours;
            var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(span) * 60.0));
            var hourAngles = new List<double>(steps + 1);
            for (var i = 0; i <= steps; i++) {
                hourAngles.Add(startHourAngleHours + span * i / steps);
            }
            var angles = new List<double>(hourAngles.Count);
            foreach (var ha in hourAngles) {
                angles.Add(SphericalAstronomy.ParallacticAngleDeg(ha, decDeg, latitudeDeg));
            }
            return FrameRotation.FromParallacticAngles(angles);
        }
    }

    /// <summary>Net and span of frame rotation over an interval, degrees.</summary>
    public readonly struct FrameRotation {

        public FrameRotation(double netDeg, double spanDeg) {
            NetDeg = netDeg;
            SpanDeg = spanDeg;
        }

        public double NetDeg { get; }
        public double SpanDeg { get; }

        /// <summary>
        /// Unwraps a time-ordered series of parallactic angles (each step taken as the shortest turn) and
        /// returns the net change and the span. Steps must be small enough that the frame turns less than
        /// 180 deg between samples (true for 1-minute steps except within ~0.1 deg of the zenith).
        /// </summary>
        public static FrameRotation FromParallacticAngles(IReadOnlyList<double> parallacticAnglesDeg) {
            if (parallacticAnglesDeg == null || parallacticAnglesDeg.Count == 0) {
                return new FrameRotation(0, 0);
            }
            double unwrapped = 0, min = 0, max = 0;
            for (var i = 1; i < parallacticAnglesDeg.Count; i++) {
                unwrapped += AngleMath.NormalizeSigned180(parallacticAnglesDeg[i] - parallacticAnglesDeg[i - 1]);
                min = Math.Min(min, unwrapped);
                max = Math.Max(max, unwrapped);
            }
            return new FrameRotation(unwrapped, max - min);
        }
    }
}
