#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NINA.Astrometry.Mac {

    /// <summary>
    /// Mac-only startup check for the data files that upstream code reads from AppDomain.CurrentDomain.BaseDirectory
    /// (Contents/MacOS in an .app bundle, where they may be relative symlinks into Contents/Resources):
    /// <list type="bullet">
    /// <item>External/JPLEPH, the JPL DE421 ephemeris that NOVAS.cs opens in its static constructor. Without it NOVAS does not
    /// fail: Moon and planet places come back as NaN or as a believable but wrong position with error code 0, and the only
    /// trace is one Logger.Error from the static constructor.</item>
    /// <item>Database/Initial and Database/Migration, the SQL scripts NINADbContext builds the catalogue database from.</item>
    /// </list>
    /// A host calls <see cref="EnsureAvailable"/> once at startup so a broken install fails loudly instead.
    /// </summary>
    public static class EngineData {

        /// <summary>Julian date (TT) of the live NOVAS check: J2000.0, inside the DE421 file's range (JD 2414992.5 to 2469808.5, 1899 to 2050).</summary>
        private const double CheckJulianDate = 2451545.0;

        /// <summary>
        /// Required files under <paramref name="baseDirectory"/> that cannot be opened for reading. Symlinks are followed; a
        /// dangling one counts as missing (File.Exists alone reports a dangling symlink as present on Unix).
        /// </summary>
        public static IReadOnlyList<string> FindMissingFiles(string baseDirectory) {
            var missing = new List<string>();
            var ephemeris = Path.Combine(baseDirectory, "External", "JPLEPH");
            if (!IsReadable(ephemeris)) {
                missing.Add($"JPL ephemeris not found at {ephemeris}: NOVAS Moon and planet positions would be NaN or wrong");
            }
            foreach (var script in new[] { "initial_schema.sql", "initial_data.sql" }) {
                var path = Path.Combine(baseDirectory, "Database", "Initial", script);
                if (!IsReadable(path)) {
                    missing.Add($"Catalogue database script not found at {path}");
                }
            }
            var migration = Path.Combine(baseDirectory, "Database", "Migration");
            bool hasMigrations;
            try {
                hasMigrations = Directory.EnumerateFiles(migration, "*.sql").Any(IsReadable);
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                hasMigrations = false;
            }
            if (!hasMigrations) {
                missing.Add($"Catalogue database migrations not found in {migration}");
            }
            return missing;
        }

        private static bool IsReadable(string path) {
            try {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return true;
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                return false;
            }
        }

        /// <summary>
        /// Missing files under the app's base directory, plus a live check that NOVAS has its ephemeris open
        /// (a Moon apparent place must be finite). An empty list means the engine data is usable.
        /// </summary>
        public static IReadOnlyList<string> Check() {
            var problems = FindMissingFiles(AppDomain.CurrentDomain.BaseDirectory).ToList();
            try {
                var moon = NOVAS.PlanetApparentCoordinates(CheckJulianDate, NOVAS.Body.Moon);
                if (!double.IsFinite(moon.RA) || !double.IsFinite(moon.Dec)) {
                    problems.Add($"NOVAS returned a non-finite Moon position (RA {moon.RA}, Dec {moon.Dec}): the ephemeris {NOVAS.EphemerisLocation} is not open");
                }
            } catch (Exception ex) {
                problems.Add($"NOVAS Moon position failed: {ex.Message}");
            }
            return problems;
        }

        /// <summary>Runs <see cref="Check"/>; logs and throws <see cref="InvalidOperationException"/> listing every problem.</summary>
        public static void EnsureAvailable() {
            var problems = Check();
            if (problems.Count == 0) {
                return;
            }
            foreach (var problem in problems) {
                Logger.Error(problem);
            }
            throw new InvalidOperationException("NINA engine data is missing or unusable:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
    }
}
