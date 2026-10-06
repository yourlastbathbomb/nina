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
using System.IO;
using System.Text;

namespace NINA.PlateSolving.Mac {

    /// <summary>
    /// Mac-only: the value for IPlateSolveSettings.ASTAPLocation when ASTAP's star database is not where ASTAP looks for it.
    /// <para>
    /// ASTAP's macOS command-line solver (astap_cli) only looks for its star databases in /usr/local/opt/astap/ unless it is
    /// given -d &lt;folder&gt;, and NINA's ASTAP solver has no setting for -d: it passes -f, -fov, -z, -s and -r/-ra/-spd only.
    /// <see cref="Resolve"/> therefore returns the executable itself when the database is in that default folder, and otherwise
    /// writes a small Nightglass-owned launcher script that execs the executable with -d and NINA's arguments unchanged
    /// ("$@", so every argument keeps its boundaries). exec keeps the process id, so NINA's timeout still kills ASTAP itself.
    /// </para>
    /// </summary>
    public static class AstapSetup {

        /// <summary>Where ASTAP's macOS builds look for the star databases by themselves.</summary>
        public const string DefaultDatabaseDirectory = "/usr/local/opt/astap";

        /// <summary>The launcher's file name inside <see cref="LauncherDirectory"/>.</summary>
        public const string LauncherFileName = "astap";

        private static readonly object launcherLock = new object();
        private static string launcherDirectory;

        /// <summary>Folder for the launcher. Defaults to &lt;CoreUtil.APPLICATIONTEMPPATH&gt;/Solvers, next to astrometry.cfg.</summary>
        public static string LauncherDirectory {
            get => launcherDirectory ?? Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Solvers");
            set => launcherDirectory = value;
        }

        public static string LauncherPath => Path.Combine(LauncherDirectory, LauncherFileName);

        /// <summary>
        /// The ASTAPLocation for an ASTAP executable (astap_cli, or ASTAP.app/Contents/MacOS/astap) and the folder holding its
        /// star database files (for example d80_*.1476). Both must exist. Returns <paramref name="astapExecutable"/> when the
        /// database folder is ASTAP's default (or not given), else the launcher path, written or refreshed as needed.
        /// </summary>
        public static string Resolve(string astapExecutable, string databaseDirectory) {
            if (string.IsNullOrWhiteSpace(astapExecutable) || !File.Exists(astapExecutable)) {
                throw new FileNotFoundException($"ASTAP executable not found at {astapExecutable}", astapExecutable);
            }
            astapExecutable = Path.GetFullPath(astapExecutable);
            if (string.IsNullOrWhiteSpace(databaseDirectory)
                || string.Equals(Path.GetFullPath(databaseDirectory).TrimEnd('/'), DefaultDatabaseDirectory, StringComparison.Ordinal)) {
                return astapExecutable;
            }
            databaseDirectory = Path.GetFullPath(databaseDirectory).TrimEnd('/');
            if (!Directory.Exists(databaseDirectory)) {
                throw new DirectoryNotFoundException($"ASTAP star database folder not found: {databaseDirectory}");
            }

            var path = LauncherPath;
            var text = BuildLauncher(astapExecutable, databaseDirectory);
            lock (launcherLock) {
                if (!File.Exists(path) || File.ReadAllText(path) != text) {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temp, text, new UTF8Encoding(false));
                    if (!OperatingSystem.IsWindows()) {
                        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    File.Move(temp, path, overwrite: true);
                    Logger.Debug($"ASTAP launcher written to {path} ({astapExecutable} -d {databaseDirectory})");
                }
            }
            return path;
        }

        /// <summary>The launcher script: sh, with both paths single-quoted for the shell.</summary>
        public static string BuildLauncher(string astapExecutable, string databaseDirectory) {
            return "#!/bin/sh\n"
                + "# ASTAP launcher written by Nightglass (NINA.Platesolving on macOS). NINA's ASTAP solver cannot pass the star\n"
                + "# database folder (-d), and ASTAP's macOS builds only look in " + DefaultDatabaseDirectory + "/ by themselves.\n"
                + "exec " + ShellQuote(astapExecutable) + " -d " + ShellQuote(databaseDirectory) + " \"$@\"\n";
        }

        /// <summary>POSIX shell single quoting: the text verbatim, each ' written as '\''.</summary>
        public static string ShellQuote(string text) {
            return "'" + text.Replace("'", "'\\''") + "'";
        }
    }
}
