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

[assembly: NINA.Mac.Sequencer.Test.MacPlatformSkips]

namespace NINA.Mac.Sequencer.Test {

    /// <summary>
    /// Linked upstream NINA.Test/Sequencer fixtures that cannot pass here for a reason outside the code under test. Each is listed by
    /// class and method with the reason and the mac test that covers the same behaviour, and is reported as Skipped instead of run
    /// (the mechanism NINA.Mac.Platesolving.Test and NINA.Mac.Image.Test use). NINA_MAC_RUN_SKIPPED=1 runs them anyway; each then
    /// fails for the stated reason (see mac/src/README-engine.md, NINA.Sequencer tests).
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class MacPlatformSkipsAttribute : Attribute, ITestAction {

        public const string WindowsProgram = "macOS: the test's command is Windows' %windir%\\System32\\mshta.exe (\"vbscript:close\"), which does not "
            + "exist on macOS (ExternalScript: \"Command not found\"). Mac/ExternalScriptMacTest runs the same ExternalScript paths with /bin/sh "
            + "scripts (exit code symbol, failure on a non-zero exit, symbols expanded before execution).";

        public const string WpfSequencerFactory = "macOS: the test builds upstream's SequencerFactory, whose constructor creates the editor's WPF "
            + "collection views (CollectionViewSource.GetDefaultView), which the headless engine does not have (WpfCompat throws "
            + "PlatformNotSupportedException). The headless engine uses NINA.Mac.Sequencing's HeadlessSequencerFactory, whose JSON round trips "
            + "Mac/SequenceJsonTest covers; linked templates are compiled but not catalogued.";

        public const string TimeZone = "Time zone: the test passes local DateTime values without a zone (2020-01-01 00:00 and 01:00) to an "
            + "altitude check at longitude 0, so its expected answer holds only where local time is close to UTC (verified on this Mac: "
            + "passes with TZ=UTC and TZ=Europe/Berlin, fails with TZ=Asia/Hong_Kong). Skipped only when the machine's UTC offset on "
            + "2020-01-01 is more than 1 h; the same expectation would fail on a Windows PC in Hong Kong.";

        private static readonly Dictionary<string, string> skips = new() {
            ["NINA.Test.Sequencer.SequenceItem.Utility.ExternalScriptTest.ExternalScript_Execute_HandlesNullProvider_Gracefully"] = WindowsProgram,
            ["NINA.Test.Sequencer.SequenceItem.Utility.ExternalScriptTest.ExternalScript_ProcessedScript_ReplacesSymbolsBeforeExecution"] = WindowsProgram,
            ["NINA.Test.Sequencer.SequenceItem.Utility.ExternalScriptTest.ExternalScript_Execute_SetsExitCodeSymbol_OnSuccess"] = WindowsProgram,
            ["NINA.Test.Sequencer.Container.LinkedTemplateContainerTest.SequenceJsonConverter_IgnoresLegacyLinkedTemplatePreviewContent"] = WpfSequencerFactory,
            ["NINA.Test.Sequencer.Container.LinkedTemplateContainerTest.SequenceJsonConverter_RoundTripsLinkedTemplateReferenceWithoutPreviewContent"] = WpfSequencerFactory,
            ["NINA.Test.Sequencer.Container.LinkedTemplateContainerTest.SequenceJsonConverter_RoundTripsTargetOverride"] = WpfSequencerFactory,
            ["NINA.Test.Sequencer.Conditions.AboveHorizonConditionTest.StandardHorizon_AboveHorizon_CheckFalse"] = TimeZone,
            ["NINA.Test.Sequencer.Conditions.AboveHorizonConditionTest.CustomHorizon_AboveHorizon_CheckFalse"] = TimeZone,
        };

        public ActionTargets Targets => ActionTargets.Test;

        /// <summary>Every listed test, by "Class.Method", with its reason (for the coverage check in MacPlatformSkipsTest).</summary>
        public static IReadOnlyDictionary<string, string> Listed => skips;

        /// <summary>Set NINA_MAC_RUN_SKIPPED=1 to run the listed tests anyway, e.g. to confirm they still fail for the stated reason.</summary>
        public static bool Disabled => Environment.GetEnvironmentVariable("NINA_MAC_RUN_SKIPPED") == "1";

        /// <summary>True where upstream's zone-less 2020-01-01 instants land near UTC, which the time-zone tests assume.</summary>
        public static bool LocalTimeIsNearUtc => Math.Abs(TimeZoneInfo.Local.GetUtcOffset(new DateTime(2020, 1, 1)).TotalHours) <= 1;

        public void BeforeTest(ITest test) {
            var reason = ReasonFor(test);
            if (reason == null || Disabled) {
                return;
            }
            if (reason == TimeZone && LocalTimeIsNearUtc) {
                return;
            }
            Assert.Ignore(reason);
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
