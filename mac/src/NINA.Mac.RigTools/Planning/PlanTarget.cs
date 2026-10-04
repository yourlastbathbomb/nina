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
using System;
using System.Collections.Generic;

namespace NINA.Mac.RigTools.Planning {

    /// <summary>A named target with J2000 catalogue coordinates.</summary>
    public sealed class PlanTarget {

        public PlanTarget(string name, EquatorialCoordinates j2000) {
            Name = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name.Trim();
            J2000 = j2000;
        }

        public PlanTarget(string name, double raHours, double decDeg) : this(name, new EquatorialCoordinates(raHours, decDeg)) {
        }

        public string Name { get; }
        public EquatorialCoordinates J2000 { get; }

        /// <summary>
        /// Parses "Name,RA,Dec": RA in hours, Dec in degrees, each decimal or sexagesimal
        /// ("M42,05:35:17.3,-05:23:28" or "M42,5.588,-5.391"). The name may itself contain commas; the last two
        /// fields are the coordinates.
        /// </summary>
        public static PlanTarget Parse(string spec) {
            if (string.IsNullOrWhiteSpace(spec)) { throw new FormatException("Empty target"); }
            var parts = spec.Split(',');
            if (parts.Length < 3) {
                throw new FormatException($"Target '{spec}' must be Name,RA,Dec");
            }
            var name = string.Join(",", parts, 0, parts.Length - 2);
            var ra = AngleMath.ParseSexagesimal(parts[parts.Length - 2]);
            var dec = AngleMath.ParseSexagesimal(parts[parts.Length - 1]);
            if (ra < 0 || ra >= 24) { throw new FormatException($"RA {ra} h outside [0, 24) in '{spec}'"); }
            if (dec < -90 || dec > 90) { throw new FormatException($"Dec {dec} outside [-90, 90] in '{spec}'"); }
            return new PlanTarget(name, ra, dec);
        }

        public override string ToString() {
            return $"{Name} ({J2000})";
        }
    }

    /// <summary>
    /// Classic southern-sky targets for the rig, J2000 positions rounded to ~1" (planning only; the mount and
    /// the plate solver supply real pointing). Values as listed by SIMBAD / the NGC-IC catalogue.
    /// </summary>
    public static class ClassicTargets {
        public static PlanTarget M42 { get; } = new PlanTarget("M42 Orion Nebula", Hms(5, 35, 17.3), Dms(-1, 5, 23, 28));
        public static PlanTarget M83 { get; } = new PlanTarget("M83 Southern Pinwheel", Hms(13, 37, 0.9), Dms(-1, 29, 51, 57));
        public static PlanTarget Ngc253 { get; } = new PlanTarget("NGC 253 Sculptor Galaxy", Hms(0, 47, 33.1), Dms(-1, 25, 17, 18));
        public static PlanTarget M8 { get; } = new PlanTarget("M8 Lagoon Nebula", Hms(18, 3, 37.0), Dms(-1, 24, 23, 12));
        public static PlanTarget M20 { get; } = new PlanTarget("M20 Trifid Nebula", Hms(18, 2, 23.0), Dms(-1, 23, 1, 48));
        public static PlanTarget OmegaCentauri { get; } = new PlanTarget("Omega Centauri (NGC 5139)", Hms(13, 26, 47.3), Dms(-1, 47, 28, 46));

        /// <summary>The six targets rigplan prints by default.</summary>
        public static IReadOnlyList<PlanTarget> Default { get; } = new[] { M42, M83, Ngc253, M8, M20, OmegaCentauri };

        private static double Hms(int h, int m, double s) {
            return h + m / 60.0 + s / 3600.0;
        }

        private static double Dms(int sign, int d, int m, double s) {
            return Math.Sign(sign) * (d + m / 60.0 + s / 3600.0);
        }
    }
}
