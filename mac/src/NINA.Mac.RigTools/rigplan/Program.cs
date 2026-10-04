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
using NINA.Mac.RigTools.Horizon;
using NINA.Mac.RigTools.Optics;
using NINA.Mac.RigTools.Planning;
using NINA.Mac.RigTools.Rotation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NINA.Mac.RigTools.Cli {

    public static class Program {

        private const string Usage = @"rigplan - imaging windows and field-rotation limits for the alt-az LX200 + ASI585MC rig

usage:
  rigplan [tonight] [options]   windows for tonight (default targets: M42 M83 NGC253 M8 M20 OmegaCen)
  rigplan table [options]       max-sub table (MAC_PORT_PLAN.md section 6) for Dec -45..+20
  rigplan horizon [--horizon F] print the horizon profile in use (default: built-in placeholder)

options:
  --date YYYY-MM-DD    evening date (default: tonight in site time; before noon = last night)
  --target N,RA,DEC    add a target (repeatable; replaces the defaults). RA hours, Dec degrees,
                       decimal or sexagesimal, e.g. --target ""M1,05:34:31.9,+22:00:52""
  --twilight T         astro (-18, default) | nautical (-12) | civil (-6) | a Sun altitude in degrees
  --min-alt D          minimum altitude, default 15
  --max-alt D          zenith keyhole, default 75
  --horizon F          horizon file (NINA format, inline # comments allowed; .hpts = MountWizzard4)
                       or 'flat' for none; default: the built-in Deep Water Bay PLACEHOLDER
  --bin N              binning for the rotation limit, default 2
  --reducer [MM]       f/6.3 reducer train (default 1575 mm); the rotation limit does not change
  --blur PX            allowed corner blur in binned px, default 1
  --min-sub S          treat times allowing less than S seconds as unusable (default: warn only)
  --steps a/b/c        dark-library exposure steps in s, default 5/10/20/30
  --every MIN          report sample spacing in minutes, default 30
  --no-samples         windows only
  --lat D --lon D --utc H   other site (east-positive longitude, UTC offset in hours)

exit status: 0 on success, 2 for a bad argument or an unreadable or invalid horizon file
";

        public static int Main(string[] args) {
            return Execute(args, Console.Out, Console.Error, DateTimeOffset.Now);
        }

        /// <summary>
        /// <see cref="Run"/> with the CLI's error handling: bad arguments or input files (format, overflow, argument
        /// and I/O errors) print "rigplan: message" to <paramref name="error"/> and return exit code 2.
        /// </summary>
        public static int Execute(string[] args, TextWriter output, TextWriter error, DateTimeOffset now) {
            try {
                return Run(args, output, now);
            } catch (Exception e) when (e is FormatException || e is OverflowException || e is ArgumentException || e is IOException || e is UnauthorizedAccessException) {
                error.WriteLine("rigplan: " + e.Message);
                return 2;
            }
        }

        public static int Run(string[] args, TextWriter output, DateTimeOffset now) {
            var command = "tonight";
            DateOnly? date = null;
            var targets = new List<PlanTarget>();
            double sunAlt = LowPrecisionSun.AstronomicalTwilightDeg;
            double minAlt = 15, maxAlt = 75, blur = FieldRotation.DefaultAllowedBlurPx;
            double? minSub = null;
            int bin = 2, every = 30;
            double? reducerMm = null;
            string horizonArg = null;
            var samples = true;
            IReadOnlyList<double> steps = new[] { 5.0, 10.0, 20.0, 30.0 };
            double? lat = null, lon = null, utc = null;

            for (var i = 0; i < args.Length; i++) {
                var a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
                switch (a) {
                    case "-h":
                    case "--help":
                        output.Write(Usage);
                        return 0;
                    case "tonight":
                    case "table":
                    case "horizon":
                        command = a;
                        break;
                    case "--date":
                        date = DateOnly.ParseExact(Next(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                        break;
                    case "--target":
                        targets.Add(PlanTarget.Parse(Next()));
                        break;
                    case "--twilight":
                        sunAlt = ParseTwilight(Next());
                        break;
                    case "--min-alt":
                        minAlt = Num(Next());
                        break;
                    case "--max-alt":
                        maxAlt = Num(Next());
                        break;
                    case "--horizon":
                        horizonArg = Next();
                        break;
                    case "--bin":
                        bin = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--reducer":
                        reducerMm = ImagingTrain.ReducerNominalFocalLengthMm;
                        if (i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mm)) {
                            reducerMm = mm;
                            i++;
                        }
                        break;
                    case "--blur":
                        blur = Num(Next());
                        break;
                    case "--min-sub":
                        minSub = Num(Next());
                        break;
                    case "--steps":
                        steps = Array.ConvertAll(Next().Split('/', StringSplitOptions.RemoveEmptyEntries), Num);
                        break;
                    case "--every":
                        every = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--no-samples":
                        samples = false;
                        break;
                    case "--lat":
                        lat = Num(Next());
                        break;
                    case "--lon":
                        lon = Num(Next());
                        break;
                    case "--utc":
                        utc = Num(Next());
                        break;
                    default:
                        throw new ArgumentException($"unknown argument '{a}' (try --help)");
                }
            }

            var site = Site.DeepWaterBay;
            if (lat.HasValue || lon.HasValue || utc.HasValue) {
                site = new Site("custom site", lat ?? site.LatitudeDeg, lon ?? site.LongitudeDeg,
                    utc.HasValue ? TimeSpan.FromHours(utc.Value) : site.UtcOffset);
            }
            var train = reducerMm.HasValue ? ImagingTrain.Asi585Reducer(bin, reducerMm.Value) : ImagingTrain.Asi585Native(bin);
            var horizon = LoadHorizon(horizonArg, output);

            switch (command) {
                case "table":
                    NightPlanReport.WriteRotationTable(output, site.LatitudeDeg, train, blur, new[] { -45.0, -30.0, -5.0, 0.0, 10.0, 20.0 });
                    return 0;
                case "horizon":
                    output.WriteLine($"# {horizon.Source}{(horizon.IsPlaceholder ? " - PLACEHOLDER, NOT MEASURED" : "")}");
                    output.WriteLine("# azimuth altitude (groomed: 0 and 360 present)");
                    foreach (var p in horizon.Points) {
                        output.WriteLine(FormattableString.Invariant($"{p.AzimuthDeg,-7} {p.AltitudeDeg}"));
                    }
                    return 0;
            }

            var options = new NightPlanOptions {
                Site = site,
                Horizon = horizon,
                MinAltitudeDeg = minAlt,
                MaxAltitudeDeg = maxAlt,
                SunAltitudeDeg = sunAlt,
                Train = train,
                AllowedBlurPx = blur,
                MinSubSeconds = minSub,
                ExposureStepsSeconds = steps,
                ReportIntervalMinutes = every
            };
            var evening = date ?? NightPlanner.EveningDateFor(now, site);
            var plan = NightPlanner.Plan(evening, targets.Count > 0 ? targets : ClassicTargets.Default, options);
            NightPlanReport.Write(plan, output, samples);
            return 0;
        }

        private static HorizonProfile LoadHorizon(string arg, TextWriter output) {
            if (string.IsNullOrEmpty(arg)) {
                return SiteHorizons.DeepWaterBayPlaceholder;
            }
            if (string.Equals(arg, "flat", StringComparison.OrdinalIgnoreCase)) {
                return HorizonProfile.Flat(0);
            }
            var result = HorizonFile.Load(arg);
            foreach (var issue in result.RejectedLines) {
                output.WriteLine($"warning: horizon {issue}");
            }
            foreach (var issue in result.Warnings) {
                output.WriteLine($"warning: horizon {issue}");
            }
            return result.Profile;
        }

        private static double ParseTwilight(string value) {
            switch (value.ToLowerInvariant()) {
                case "astro":
                case "astronomical":
                    return LowPrecisionSun.AstronomicalTwilightDeg;
                case "nautical":
                    return LowPrecisionSun.NauticalTwilightDeg;
                case "civil":
                    return LowPrecisionSun.CivilTwilightDeg;
                default:
                    return Num(value);
            }
        }

        private static double Num(string value) {
            return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
