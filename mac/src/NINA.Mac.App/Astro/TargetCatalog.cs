#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.App.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Mac.App.Astro {

    /// <summary>A catalogue object, J2000 coordinates.</summary>
    public sealed record CatalogTarget(string Name, string CommonName, double RightAscensionHours, double DeclinationDegrees, string Kind) {
        public string Display => string.IsNullOrEmpty(CommonName) ? Name : $"{Name}  {CommonName}";
    }

    public interface ITargetCatalog {

        IReadOnlyList<CatalogTarget> Search(string text, int limit = 50);
    }

    /// <summary>
    /// A small built-in list of targets that suit the site (good southern view, no northern sky) plus a few that
    /// demonstrate the warnings. Stand-in until the engine's NINA.sqlite search works on macOS (M3; research MVP-M1).
    /// </summary>
    public sealed class BuiltInTargetCatalog : ITargetCatalog {

        public static IReadOnlyList<CatalogTarget> Targets { get; } = new[] {
            new CatalogTarget("M42", "Orion Nebula", 5.5881, -5.3911, "Nebula"),
            new CatalogTarget("M78", "", 5.7796, 0.0139, "Nebula"),
            new CatalogTarget("IC 434", "Horsehead Nebula", 5.6831, -2.4583, "Nebula"),
            new CatalogTarget("NGC 2237", "Rosette Nebula", 6.5625, 4.9983, "Nebula"),
            new CatalogTarget("M1", "Crab Nebula", 5.5755, 22.0144, "Supernova remnant"),
            new CatalogTarget("M104", "Sombrero Galaxy", 12.6665, -11.6231, "Galaxy"),
            new CatalogTarget("M83", "Southern Pinwheel", 13.6169, -29.8658, "Galaxy"),
            new CatalogTarget("NGC 5128", "Centaurus A", 13.4243, -43.0192, "Galaxy"),
            new CatalogTarget("NGC 5139", "Omega Centauri", 13.4465, -47.4794, "Globular cluster"),
            new CatalogTarget("NGC 6302", "Bug Nebula", 17.2289, -37.1044, "Planetary nebula"),
            new CatalogTarget("M8", "Lagoon Nebula", 18.0603, -24.3867, "Nebula"),
            new CatalogTarget("M20", "Trifid Nebula", 18.0397, -23.0300, "Nebula"),
            new CatalogTarget("M16", "Eagle Nebula", 18.3133, -13.8167, "Nebula"),
            new CatalogTarget("M17", "Omega Nebula", 18.3406, -16.1767, "Nebula"),
            new CatalogTarget("M57", "Ring Nebula", 18.8931, 33.0292, "Planetary nebula"),
            new CatalogTarget("M27", "Dumbbell Nebula", 19.9934, 22.7211, "Planetary nebula"),
            new CatalogTarget("NGC 7293", "Helix Nebula", 22.4940, -20.8372, "Planetary nebula"),
            new CatalogTarget("NGC 253", "Sculptor Galaxy", 0.7925, -25.2883, "Galaxy"),
            new CatalogTarget("M31", "Andromeda Galaxy", 0.7123, 41.2692, "Galaxy"),
            new CatalogTarget("NGC 2070", "Tarantula Nebula", 5.6450, -69.1008, "Nebula"),
        };

        public IReadOnlyList<CatalogTarget> Search(string text, int limit = 50) {
            if (string.IsNullOrWhiteSpace(text)) {
                return Targets.Take(limit).ToArray();
            }
            var needle = Normalize(text);
            return Targets
                .Where(t => Normalize(t.Name).Contains(needle, StringComparison.Ordinal) || Normalize(t.CommonName).Contains(needle, StringComparison.Ordinal))
                .OrderBy(t => Normalize(t.Name) == needle ? 0 : 1)
                .Take(limit)
                .ToArray();
        }

        private static string Normalize(string s) => (s ?? "").Replace(" ", "").ToUpperInvariant();
    }

    /// <summary>Everything the Target screen shows about one target at one time.</summary>
    public sealed record TargetVisibility(
        double Altitude,
        double Azimuth,
        double HourAngleHours,
        double TransitAltitude,
        DateTimeOffset NextTransit,
        double MaxSubSecondsNow,
        bool NeverRises,
        bool InBlockedNorth,
        bool AboveMaxAltitude,
        bool BelowMinAltitude,
        bool TransitsAboveMaxAltitude,
        IReadOnlyList<string> Warnings) {

        /// <summary>Below the site's local horizon (Target › Horizon) at its azimuth now.</summary>
        public bool BelowLocalHorizon { get; init; }

        /// <summary>The local horizon's altitude at the target's azimuth now; NaN without a horizon.</summary>
        public double LocalHorizonAltitude { get; init; } = double.NaN;

        public bool Observable => !NeverRises && !InBlockedNorth && !AboveMaxAltitude && !BelowMinAltitude && !BelowLocalHorizon;
    }

    public static class TargetPlanner {

        public static TargetVisibility Evaluate(CatalogTarget target, DateTimeOffset now, AppSettings settings, double exposureSeconds, HorizonProfile horizon = null) {
            var site = new GeoSite(settings.Site.LatitudeDegrees, settings.Site.LongitudeDegrees);
            var (alt, az) = SkyMath.ToAltAz(now, target.RightAscensionHours, target.DeclinationDegrees, site);
            var ha = SkyMath.HourAngleHours(SkyMath.LocalSiderealTimeHours(now, site.LongitudeDegrees), target.RightAscensionHours);
            var transitAlt = SkyMath.TransitAltitude(target.DeclinationDegrees, site.LatitudeDegrees);
            var radius = SkyMath.CornerRadiusPixels(settings.Optics.SensorWidth, settings.Optics.SensorHeight, settings.Optics.Bin);
            var maxSub = alt > 0 ? SkyMath.MaxSubSeconds(site.LatitudeDegrees, alt, az, radius, settings.FieldRotationBlurPixels) : double.NaN;

            var neverRises = transitAlt <= 0;
            var blocked = alt > 0 && settings.IsAzimuthBlocked(az);
            var aboveMax = alt > settings.MaxAltitudeDegrees;
            var belowMin = alt < settings.MinAltitudeDegrees;
            var transitsHigh = transitAlt > settings.MaxAltitudeDegrees;
            var localHorizon = horizon?.GetAltitude(az) ?? double.NaN;
            var belowLocal = horizon != null && alt > 0 && alt < localHorizon;

            var warnings = new List<string>();
            if (neverRises) {
                warnings.Add($"Never rises at latitude {site.LatitudeDegrees:0.##}° (transit altitude {transitAlt:0.0}°).");
            } else {
                if (blocked) {
                    warnings.Add($"Azimuth {az:0}° is in the blocked northern sky ({settings.NorthBlockedFromAzimuth:0}°–{settings.NorthBlockedToAzimuth:0}°).");
                }
                if (aboveMax) {
                    warnings.Add($"Altitude {alt:0.0}° is above the {settings.MaxAltitudeDegrees:0}° zenith keyhole limit; the alt-az fork cannot track through it.");
                } else if (transitsHigh) {
                    warnings.Add($"Transits at {transitAlt:0.0}°, above the {settings.MaxAltitudeDegrees:0}° limit: image well east or west of the meridian.");
                }
                if (belowLocal && !blocked) {
                    warnings.Add($"Behind the local horizon: altitude {alt:0.0}° at azimuth {az:0}°, where the horizon is {localHorizon:0}°{(horizon.IsEstimate ? " (estimated)" : "")}.");
                }
                if (belowMin && !blocked) {
                    warnings.Add($"Altitude {alt:0.0}° is below the {settings.MinAltitudeDegrees:0}° minimum.");
                }
                if (!double.IsNaN(maxSub) && exposureSeconds > maxSub) {
                    warnings.Add($"{exposureSeconds:0.#} s subs exceed the field-rotation limit of {FormatSeconds(maxSub)} here ({settings.FieldRotationBlurPixels:0.#} px corner blur at bin {settings.Optics.Bin}).");
                }
            }
            return new TargetVisibility(alt, az, ha, transitAlt, now + SkyMath.TimeToTransit(now, target.RightAscensionHours, site.LongitudeDegrees),
                maxSub, neverRises, blocked, aboveMax, belowMin, transitsHigh, warnings) {
                BelowLocalHorizon = belowLocal,
                LocalHorizonAltitude = localHorizon,
            };
        }

        public static string FormatSeconds(double seconds) {
            if (double.IsNaN(seconds)) {
                return "n/a";
            }
            if (double.IsPositiveInfinity(seconds) || seconds > 600) {
                return "> 10 min";
            }
            return seconds < 10 ? $"{seconds:0.0} s" : $"{seconds:0} s";
        }
    }
}
