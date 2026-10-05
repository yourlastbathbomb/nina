#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NINA.Astrometry;
using NINA.Core.Utility;
using System.Data.SQLite;

namespace NINA.Mac.Astrometry.Test {

    /// <summary>
    /// NINA's catalogue database on macOS: EF6 + System.Data.SQLite over SourceGear's osx-arm64 e_sqlite3. NINADbContext
    /// (NINA.Core) creates a new database from Database/Initial/*.sql and then applies Database/Migration/N.sql in numeric
    /// order; NINA.Astrometry.Mac copies the upstream scripts (NINA/Database) into the output folder. Every database here
    /// lives in the test's temp data folder (TestHost.DataRoot), never in the user's Application Support folder.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class DeepSkyDatabaseTest {
        private string folder = string.Empty;

        [SetUp]
        public void SetUp() {
            folder = Path.Combine(TestHost.DataRoot, "db-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        [TearDown]
        public void TearDown() {
            SQLiteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try {
                Directory.Delete(folder, true);
            } catch (IOException) {
            }
        }

        private static string ScriptFolder(string name) {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Database", name);
        }

        private static string ConnectionString(string path) {
            return $"Data Source={path};Pooling=False;";
        }

        /// <summary>
        /// The mac data folder is chosen, not inherited by accident: CoreUtil.APPLICATIONTEMPPATH is
        /// LocalApplicationData/NINA, and .NET 10 on macOS resolves LocalApplicationData to ~/Library/Application Support,
        /// where macOS apps keep their data. Logs, profiles and the default NINA.sqlite live there. If a runtime ever maps
        /// LocalApplicationData elsewhere, this test says so. The test host moves the folder to a temp path before any test runs.
        /// </summary>
        [Test]
        public void DefaultDataFolder_IsNinaInApplicationSupport() {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            TestHost.DefaultApplicationTempPath.Should().Be(Path.Combine(home, "Library", "Application Support", "NINA"));
            CoreUtil.APPLICATIONTEMPPATH.Should().Be(TestHost.DataRoot).And.StartWith(Path.GetTempPath());
        }

        /// <summary>
        /// Upstream's default is "%localappdata%\NINA\NINA.sqlite" through Environment.ExpandEnvironmentVariables, which on
        /// macOS stays a literal relative file name in the working directory. Off Windows DatabaseInteraction now uses NINA's
        /// data folder, CoreUtil.APPLICATIONTEMPPATH (~/Library/Application Support/NINA unless the host moves it).
        /// </summary>
        [Test]
        public async Task DefaultDatabase_LivesInNinasDataFolder_AndIsBuiltOnFirstUse() {
            var expected = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NINA.sqlite");
            CoreUtil.APPLICATIONTEMPPATH.Should().Be(TestHost.DataRoot);

            var db = new DatabaseInteraction();
            using (var context = db.GetContext()) {
                new SQLiteConnectionStringBuilder(context.Database.Connection.ConnectionString).DataSource.Should().Be(expected);
            }

            // IERS UT1-UTC rows come from the migrations; |UT1-UTC| stays below 0.9 s by definition
            var ut1MinusUtc = await db.GetUT1_UTC(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

            double.IsNaN(ut1MinusUtc).Should().BeFalse();
            Math.Abs(ut1MinusUtc).Should().BeLessThan(0.9);
            File.Exists(expected).Should().BeTrue();
            File.Exists(Path.Combine(Environment.CurrentDirectory, @"%localappdata%\NINA\NINA.sqlite")).Should().BeFalse();
        }

        /// <summary>
        /// The M3 check: create the catalogue from the upstream scripts in a temp folder, then search "M42" the way the
        /// sky atlas does (DatabaseInteraction.GetDeepSkyObjects with an object name).
        /// </summary>
        [Test]
        public async Task SearchM42_FindsTheOrionNebula_InADatabaseBuiltFromTheUpstreamScripts() {
            var path = Path.Combine(folder, "NINA.sqlite");
            var db = new DatabaseInteraction(ConnectionString(path));
            using (var context = db.GetContext()) {
                // Runs NINADbContext's initializer (create + migrate) directly, so a failure throws here instead of being logged
                context.Database.Initialize(force: false);
            }

            var results = await db.GetDeepSkyObjects(null as string, null, new DatabaseInteraction.DeepSkyObjectSearchParams { ObjectName = "M42" }, CancellationToken.None);

            var m42 = results.Should().ContainSingle(dso => dso.AlsoKnownAs.Contains("M 42")).Subject;
            m42.Id.Should().Be("NGC1976");
            m42.Name.Should().Be("M 42");
            m42.AlsoKnownAs.Should().Contain(new[] { "NGC 1976", "Orion Nebula", "Great Orion Nebula" });
            // Added by Migration/7.sql, so the migrations ran
            m42.AlsoKnownAs.Should().Contain("LBN 974");
            m42.Constellation.Should().Be("ORI");
            m42.DSOType.Should().Be("CL+NB");
            m42.Magnitude.Should().Be(4.0);
            m42.Size.Should().Be(5400.0);

            // Catalogue position, J2000: 05h 35m 17.3s, -05 deg 23' 28" (SIMBAD: 05 35 17.3, -05 23 28)
            m42.Coordinates.Epoch.Should().Be(Epoch.J2000);
            m42.Coordinates.RADegrees.Should().BeApproximately(83.82208333, 1e-8);
            m42.Coordinates.Dec.Should().BeApproximately(-5.39111111, 1e-8);
            m42.Coordinates.RA.Should().BeApproximately(5.0 + 35.0 / 60.0 + 17.3 / 3600.0, 0.1 / 3600.0);
            m42.Coordinates.Dec.Should().BeApproximately(-(5.0 + 23.0 / 60.0 + 28.0 / 3600.0), 1.0 / 3600.0);
        }

        /// <summary>
        /// NINADbContext logs and skips a script that fails, so a script that does not apply on this SQLite would go unnoticed
        /// at run time. Apply every upstream script directly (each in a transaction, as NINA does) to a second database, failing
        /// on the first error, and check that NINA's build of the same scripts ends with the same tables, row counts and version.
        /// </summary>
        [Test]
        public void UpstreamScripts_ApplyCleanlyOnArm64Sqlite_AndNinaBuildsTheSameDatabase() {
            var ninaPath = Path.Combine(folder, "nina.sqlite");
            using (var context = new DatabaseInteraction(ConnectionString(ninaPath)).GetContext()) {
                context.Database.Initialize(force: false);
            }

            var referencePath = Path.Combine(folder, "reference.sqlite");
            var migrations = Directory.GetFiles(ScriptFolder("Migration"), "*.sql")
                .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)))
                .ToList();
            migrations.Select(f => Path.GetFileName(f)).Should().Equal(
                "1.sql", "2.sql", "3.sql", "5.sql", "6.sql", "7.sql", "8.sql", "9.sql", "10.sql", "11.sql", "12.sql", "13.sql", "14.sql", "15.sql", "16.sql");

            using (var connection = new SQLiteConnection(ConnectionString(referencePath))) {
                connection.Open();
                Execute(connection, "PRAGMA foreign_keys = OFF;");
                ExecuteInTransaction(connection, File.ReadAllText(Path.Combine(ScriptFolder("Initial"), "initial_schema.sql")) + File.ReadAllText(Path.Combine(ScriptFolder("Initial"), "initial_data.sql")));
                foreach (var migration in migrations) {
                    try {
                        ExecuteInTransaction(connection, File.ReadAllText(migration));
                    } catch (SQLiteException ex) {
                        Assert.Fail($"{Path.GetFileName(migration)} does not apply: {ex.Message}");
                    }
                }
            }

            var nina = Describe(ninaPath);
            var reference = Describe(referencePath);
            TestContext.Out.WriteLine(string.Join(Environment.NewLine, nina.Select(kv => $"{kv.Key}: {kv.Value}")));
            nina.Should().Equal(reference);
            nina["user_version"].Should().Be(16);
            nina["dsodetail"].Should().BeGreaterThan(10_000);
            nina["hipsskymaps"].Should().BeGreaterThan(0);
        }

        private static void Execute(SQLiteConnection connection, string sql) {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void ExecuteInTransaction(SQLiteConnection connection, string sql) {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand()) {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        /// <summary>Row count of every table, plus PRAGMA user_version.</summary>
        private static SortedDictionary<string, long> Describe(string path) {
            var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
            using var connection = new SQLiteConnection(ConnectionString(path));
            connection.Open();
            var tables = new List<string>();
            using (var command = connection.CreateCommand()) {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                using var reader = command.ExecuteReader();
                while (reader.Read()) {
                    tables.Add(reader.GetString(0));
                }
            }
            foreach (var table in tables) {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM `{table}`";
                result[table] = Convert.ToInt64(command.ExecuteScalar());
            }
            using (var command = connection.CreateCommand()) {
                command.CommandText = "PRAGMA user_version";
                result["user_version"] = Convert.ToInt64(command.ExecuteScalar());
            }
            return result;
        }
    }
}
