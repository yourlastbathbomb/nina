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
using NINA.Mac.RigTools.Rotation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NINA.Mac.RigTools.Planning {

    /// <summary>Plain-text rendering of a <see cref="NightPlan"/> and of the field-rotation table.</summary>
    public static class NightPlanReport {

        public static void Write(NightPlan plan, TextWriter w, bool includeSamples = true) {
            var o = plan.Options;
            var site = o.Site;
            w.WriteLine(Inv($"Night of {plan.EveningDate:ddd yyyy-MM-dd} at {site}"));
            if (plan.Dusk.HasValue) {
                var dark = plan.Dawn.Value - plan.Dusk.Value;
                w.WriteLine(Inv($"Darkness (Sun below {o.SunAltitudeDeg:0}°): {T(plan.Dusk.Value)} -> {T(plan.Dawn.Value)}  ({Dur(dark)})"));
            } else {
                w.WriteLine(Inv($"No darkness: the Sun stays above {o.SunAltitudeDeg:0}°."));
            }
            w.WriteLine(Inv($"Limits: altitude {o.MinAltitudeDeg:0}°-{o.MaxAltitudeDeg:0}° and above the horizon profile ({o.Horizon.Source})"));
            if (o.Horizon.IsPlaceholder) {
                w.WriteLine("        The horizon is a PLACEHOLDER (no northern sky), not a measurement. Measure yours and pass --horizon.");
            }
            w.WriteLine(Inv($"Rotation: {o.Train}; {o.AllowedBlurPx:0.##} px corner blur at r = {o.Train.HalfDiagonalPx:0.0} px (same limit with or without the reducer)"));
            if (o.MinSubSeconds.HasValue) {
                w.WriteLine(Inv($"          times allowing less than {o.MinSubSeconds.Value:0.#} s are excluded"));
            }
            w.WriteLine(Inv($"Sub steps: {string.Join("/", o.ExposureStepsSeconds.Select(s => s.ToString("0.#", CultureInfo.InvariantCulture)))} s. Times are local (UTC{Offset(site.UtcOffset)}); positions J2000 precessed to date, geometric altitude."));

            foreach (var t in plan.Targets) {
                w.WriteLine();
                w.WriteLine(Inv($"{t.Target.Name}  RA {AngleMath.FormatHours(t.Target.J2000.RaHours)} Dec {AngleMath.FormatDegrees(t.Target.J2000.DecDeg)} (J2000)"));
                w.WriteLine(Inv($"  transit {T(t.Transit)} at alt {t.TransitAltitudeDeg:0.0}° az {t.TransitAzimuthDeg:0}{(t.TransitInDarkness ? "" : " (not in darkness)")}; {Dur(t.TimeAboveMinAltitude)} per day above {o.MinAltitudeDeg:0}°"));
                if (t.Windows.Count == 0) {
                    w.WriteLine("  no imaging window tonight");
                }
                foreach (var win in t.Windows) {
                    var rec = win.RecommendedSubSeconds.HasValue ? Inv($"use {win.RecommendedSubSeconds.Value:0} s") : "no step fits";
                    w.WriteLine(Inv($"  {Side(win.Side)} {T(win.Start)}-{T(win.End)} {Dur(win.Duration),6}  alt {win.StartAltitudeDeg,4:0.0}->{win.EndAltitudeDeg,4:0.0} (peak {win.PeakAltitudeDeg:0.0})  max sub >= {Sub(win.ShortestMaxSubSeconds)} (worst {T(win.ShortestMaxSubAt)}) -> {rec}; frame turns {win.FrameRotationDeg:0}°"));
                    w.WriteLine(Inv($"        opens: {Limit(win.StartLimit)}; closes: {Limit(win.EndLimit)}"));
                }
                foreach (var note in t.Notes) {
                    w.WriteLine("  ! " + note);
                }
                if (includeSamples && t.Samples.Count > 0) {
                    w.WriteLine("    time   alt    az   horiz    HA     rot°/h   max sub  ok");
                    foreach (var s in t.Samples) {
                        w.WriteLine(Inv($"    {T(s.Time)} {s.AltitudeDeg,5:0.0} {s.AzimuthDeg,5:0} {s.HorizonAltitudeDeg,6:0.0} {s.HourAngleHours,6:+0.00;-0.00}h {s.RotationRateDegPerHour,8:0.0} {Sub(s.MaxSubSeconds),8}  {(s.Usable ? (s.Side == MeridianSide.East ? "E" : "W") : "-")}"));
                    }
                }
            }
        }

        /// <summary>
        /// The MAC_PORT_PLAN.md section 6 table: max sub at transit and +/-3 h for the given declinations, plus the
        /// frame rotation over +/-2 h, at the given latitude and train.
        /// </summary>
        public static void WriteRotationTable(TextWriter w, double latitudeDeg, ImagingTrain train, double allowedBlurPx, IEnumerable<double> declinations) {
            w.WriteLine(Inv($"Field-rotation max sub at {latitudeDeg:0.00}° latitude, {train}, {allowedBlurPx:0.##} px corner blur (r = {train.HalfDiagonalPx:0.0} px)"));
            w.WriteLine("  Dec    transit alt  t(HA 0)  t(HA +/-1h)  t(HA +/-2h)  t(HA +/-3h)  rotation over HA -2h..+2h");
            foreach (var dec in declinations) {
                var alt = SphericalAstronomy.TransitAltitudeDeg(dec, latitudeDeg);
                if (alt <= 0) {
                    w.WriteLine(Inv($"  {dec,4:+0;-0}   never rises"));
                    continue;
                }
                var cells = new[] { 0.0, 1.0, 2.0, 3.0 }.Select(h => Sub(FieldRotation.MaxSubSecondsAtHourAngle(latitudeDeg, h, dec, train, allowedBlurPx))).ToArray();
                var span = FieldRotation.OverHourAngles(latitudeDeg, dec, -2, 2).SpanDeg;
                w.WriteLine(Inv($"  {dec,4:+0;-0}   {alt,6:0.0}°    {cells[0],7}  {cells[1],11}  {cells[2],11}  {cells[3],11}  {span,6:0}°"));
            }
        }

        private static string Side(MeridianSide side) {
            return side == MeridianSide.East ? "EAST" : "WEST";
        }

        private static string Limit(PlanConstraint c) {
            if (c == PlanConstraint.None) { return "-"; }
            var parts = new List<string>();
            if (c.HasFlag(PlanConstraint.Darkness)) { parts.Add("twilight"); }
            if (c.HasFlag(PlanConstraint.Horizon)) { parts.Add("horizon profile"); }
            if (c.HasFlag(PlanConstraint.MinAltitude)) { parts.Add("min altitude"); }
            if (c.HasFlag(PlanConstraint.MaxAltitude)) { parts.Add("zenith keyhole"); }
            if (c.HasFlag(PlanConstraint.FieldRotation)) { parts.Add("field rotation"); }
            if (c.HasFlag(PlanConstraint.Meridian)) { parts.Add("meridian"); }
            return string.Join(" + ", parts);
        }

        internal static string Sub(double seconds) {
            if (double.IsPositiveInfinity(seconds)) { return "no limit"; }
            if (seconds >= 3600) { return ">1 h"; }
            return seconds.ToString(seconds < 100 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";
        }

        private static string T(DateTimeOffset t) {
            return t.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        private static string Dur(TimeSpan d) {
            var minutes = (int)Math.Round(d.TotalMinutes);
            return string.Format(CultureInfo.InvariantCulture, "{0}h{1:00}m", minutes / 60, minutes % 60);
        }

        private static string Offset(TimeSpan offset) {
            return (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hh\\:mm", CultureInfo.InvariantCulture);
        }

        private static string Inv(FormattableString s) {
            return FormattableString.Invariant(s);
        }
    }
}
