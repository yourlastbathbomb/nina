#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework.Interfaces;

[assembly: NINA.Mac.Platesolving.Test.MacPlatformSkips]

namespace NINA.Mac.Platesolving.Test {

    /// <summary>
    /// Upstream NINA.Test/PlateSolving fixtures are linked unchanged. Five of their tests exercise code or programs that do not
    /// exist on macOS by design: Windows' cmd.exe, the Cygwin solver that MacReplacements/LocalPlateSolver.cs replaces, and the
    /// Windows version-resource check that upstream edit P1 limits to Windows. Each is listed here by class and method, with
    /// the reason and the mac test that covers the same behaviour, and is reported as Skipped instead of run, as NUnit's
    /// [Platform(Exclude = "MacOsX")] would mark it (the same mechanism as NINA.Mac.Image.Test's GDI+ list).
    /// NINA_MAC_RUN_SKIPPED=1 runs them anyway; then all five fail for the stated reason (see mac/src/README-engine.md).
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class MacPlatformSkipsAttribute : Attribute, ITestAction {

        public const string CmdExe = "macOS: the test's stand-in solver runs Windows' cmd.exe (\"/C exit 0\", \"/C ping ...\"), which does not "
            + "exist on macOS (Win32Exception: No such file or directory). Mac/CliSolverMacTest checks the same CLISolver behaviour "
            + "(clean-up, failed-solve archive, solver-owned timeout) with /bin/sh stand-ins.";

        public const string CygwinLocalSolver = "macOS: upstream's Cygwin LocalPlateSolver (cmd.exe, bash.exe, /usr/bin/solve-field) is not "
            + "compiled; MacReplacements/LocalPlateSolver.cs replaces it by name and runs solve-field directly. "
            + "Mac/LocalPlateSolverMacTest checks its arguments.";

        public const string WindowsVersionResource = "macOS: the legacy-ASTAP check reads the executable's Windows version resource "
            + "(FileVersionInfo), which never exists for a Mach-O or script ASTAP, so every solve with the default auto downsample "
            + "(0) would be rejected; upstream edit P1 runs the check on Windows only. Mac/AstapSolverMacTest pins the macOS "
            + "behaviour (missing executable still rejected, auto downsample accepted).";

        private static readonly Dictionary<string, string> skips = new() {
            ["NINA.Test.PlateSolving.CliSolverBehaviorTest.SolveAsync_SuccessCleansTemporaryImageOutputAndSidecarFiles"] = CmdExe,
            ["NINA.Test.PlateSolving.CliSolverBehaviorTest.SolveAsync_FailureMovesTemporaryFilesToFailedArchive"] = CmdExe,
            ["NINA.Test.PlateSolving.CliSolverBehaviorTest.SolveAsync_TimeoutUsesLinkedSolverTimeoutToken"] = CmdExe,
            ["NINA.Test.PlateSolving.SolverTranslationBehaviorTest.LocalPlateSolver_TranslatesHintedAndBlindArguments"] = CygwinLocalSolver,
            ["NINA.Test.PlateSolving.SolverTranslationBehaviorTest.ASTAPSolver_ValidationRejectsMissingOrLegacyAutoDownsampleConfiguration"] = WindowsVersionResource,
        };

        public ActionTargets Targets => ActionTargets.Test;

        /// <summary>Every listed test, by "Class.Method", with its reason (for the coverage check in MacPlatformSkipsTest).</summary>
        public static IReadOnlyDictionary<string, string> Listed => skips;

        /// <summary>Set NINA_MAC_RUN_SKIPPED=1 to run the listed tests anyway, e.g. to confirm they still fail for the stated reason.</summary>
        public static bool Disabled => Environment.GetEnvironmentVariable("NINA_MAC_RUN_SKIPPED") == "1";

        public void BeforeTest(ITest test) {
            var reason = ReasonFor(test);
            if (reason != null && !Disabled) {
                Assert.Ignore(reason);
            }
        }

        public void AfterTest(ITest test) {
        }

        public static string? ReasonFor(ITest test) {
            if (test.ClassName == null || test.MethodName == null) {
                return null;
            }
            return skips.TryGetValue(test.ClassName + "." + test.MethodName, out var reason) ? reason : null;
        }
    }
}
