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
using NINA.Mac.Platform;
using NUnit.Framework;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace NINA.Mac.App.Test.Platform {

    /// <summary>
    /// Sleep and App Nap prevention, verified against <c>pmset -g assertions</c> (what macOS itself reports) and
    /// against IOPMCopyAssertionsByProcess. These take real, short-lived assertions on this Mac.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class PowerManagementTests {

        private static string Pmset() => Shell.Run("/usr/bin/pmset", "-g assertions");

        /// <summary>Lines like: pid 123(dotnet): [0x...] 00:00:00 PreventUserIdleSystemSleep named: "reason"</summary>
        private static bool PmsetLists(string output, string type, string name) =>
            output.Split('\n').Any(line =>
                line.Contains($"pid {Environment.ProcessId}(", StringComparison.Ordinal)
                && line.Contains($"{type} named: \"{name}\"", StringComparison.Ordinal));

        private static string UniqueName(string what) => $"NINA.Mac.App.Test {what} {Guid.NewGuid():N}";

        [TestCase(PowerAssertionType.PreventUserIdleSystemSleep, "PreventUserIdleSystemSleep")]
        [TestCase(PowerAssertionType.PreventUserIdleDisplaySleep, "PreventUserIdleDisplaySleep")]
        public void PowerAssertion_IsListedByPmsetWhileHeld_AndGoneAfterDispose(PowerAssertionType type, string pmsetType) {
            var name = UniqueName(pmsetType);
            var assertion = PowerAssertion.Create(type, name);
            try {
                assertion.IsHeld.Should().BeTrue();
                assertion.Id.Should().NotBe(0u);
                PmsetLists(Pmset(), pmsetType, name).Should().BeTrue("pmset -g assertions must list the held assertion under this pid");
                PowerAssertion.ListForProcess(Environment.ProcessId).Should().Contain((pmsetType, name));
            } finally {
                assertion.Dispose();
            }
            assertion.IsHeld.Should().BeFalse();
            PmsetLists(Pmset(), pmsetType, name).Should().BeFalse("the assertion must be gone after Dispose");
            PowerAssertion.ListForProcess(Environment.ProcessId).Should().NotContain((pmsetType, name));
            assertion.Dispose(); // idempotent
        }

        [Test]
        public void ProcessActivity_UserInitiated_ShowsAsIdleSleepAssertionInPmset() {
            var reason = UniqueName("activity");
            var activity = ProcessActivity.Begin(ActivityOptions.UserInitiated, reason);
            try {
                activity.IsActive.Should().BeTrue();
                var output = Pmset();
                PmsetLists(output, "PreventUserIdleSystemSleep", reason).Should().BeTrue(
                    "NSActivityUserInitiated includes NSActivityIdleSystemSleepDisabled, which macOS backs with an assertion named after the reason. pmset said:\n" + output);
            } finally {
                activity.Dispose();
            }
            activity.IsActive.Should().BeFalse();
            Pmset().Should().NotContain(reason);
        }

        [Test]
        public void ProcessActivity_AllowingIdleSleep_BeginsAndEnds_WithoutASleepAssertion() {
            // App Nap prevention alone: macOS exposes no CLI/API to read App Nap state for a process (taskinfo needs
            // root), so this checks the activity starts and ends and that it does not block idle sleep.
            var reason = UniqueName("napOnly");
            var activity = ProcessActivity.Begin(ActivityOptions.UserInitiatedAllowingIdleSystemSleep, reason);
            try {
                activity.IsActive.Should().BeTrue();
                PmsetLists(Pmset(), "PreventUserIdleSystemSleep", reason).Should().BeFalse();
            } finally {
                activity.Dispose();
            }
            activity.IsActive.Should().BeFalse();
        }

        [Test]
        public void KeepAwake_EngageAdjustRelease_MatchesPmset() {
            using var keepAwake = new MacKeepAwake();
            var reason = UniqueName("session");
            var changes = 0;
            keepAwake.StateChanged += (_, _) => changes++;

            var state = keepAwake.Engage(reason, KeepAwakeOptions.Default);
            state.SystemSleepPrevented.Should().BeTrue();
            state.DisplaySleepPrevented.Should().BeTrue();
            state.AppNapPrevented.Should().BeTrue();
            state.Error.Should().BeNull();
            state.Summary.Should().Be("Awake: no idle sleep, display on, no App Nap");
            var output = Pmset();
            PmsetLists(output, "PreventUserIdleSystemSleep", reason).Should().BeTrue(output);
            PmsetLists(output, "PreventUserIdleDisplaySleep", reason).Should().BeTrue(output);

            // Display may sleep now: only that assertion goes
            state = keepAwake.Engage(reason, KeepAwakeOptions.Default with { PreventDisplaySleep = false });
            state.DisplaySleepPrevented.Should().BeFalse();
            state.SystemSleepPrevented.Should().BeTrue();
            output = Pmset();
            PmsetLists(output, "PreventUserIdleDisplaySleep", reason).Should().BeFalse(output);
            PmsetLists(output, "PreventUserIdleSystemSleep", reason).Should().BeTrue(output);

            state = keepAwake.Release();
            state.IsEngaged.Should().BeFalse();
            state.Summary.Should().Be("Sleep allowed");
            Pmset().Should().NotContain(reason);
            changes.Should().Be(3);
            keepAwake.Release().IsEngaged.Should().BeFalse(); // idempotent
        }

        [Test]
        public void KeepAwake_NewReason_RenamesTheAssertions() {
            using var keepAwake = new MacKeepAwake();
            var first = UniqueName("first");
            var second = UniqueName("second");
            keepAwake.Engage(first, KeepAwakeOptions.Default);
            keepAwake.Engage(second, KeepAwakeOptions.Default);
            var output = Pmset();
            output.Should().NotContain(first);
            PmsetLists(output, "PreventUserIdleSystemSleep", second).Should().BeTrue(output);
            keepAwake.Release();
        }

        [Test]
        public void KeepAwake_Dispose_ReleasesEverything() {
            var reason = UniqueName("dispose");
            var keepAwake = new MacKeepAwake();
            keepAwake.Engage(reason, KeepAwakeOptions.Default);
            keepAwake.Dispose();
            Pmset().Should().NotContain(reason);
        }

        [Test]
        public void PowerSource_AgreesWithPmsetBatt() {
            var before = Shell.Run("/usr/bin/pmset", "-g batt");
            var info = new MacPowerSource().Read();
            var after = Shell.Run("/usr/bin/pmset", "-g batt");

            // "Now drawing from 'Battery Power'" / "'AC Power'"
            var source = Regex.Match(before, "Now drawing from '([^']+)'").Groups[1].Value;
            var expected = source switch {
                "AC Power" => PowerSourceKind.AC,
                "Battery Power" => PowerSourceKind.Battery,
                "UPS Power" => PowerSourceKind.UPS,
                _ => PowerSourceKind.Unknown,
            };
            info.Source.Should().Be(expected, before);

            var percent = Regex.Match(before, @"InternalBattery-\d+.*?\t(\d+)%;\s*([^;]+);");
            if (!percent.Success) {
                info.HasBattery.Should().BeFalse("pmset reports no internal battery: " + before);
                Assert.Ignore("No internal battery on this Mac; percentage not checked.");
            }
            info.HasBattery.Should().BeTrue();
            var p1 = int.Parse(percent.Groups[1].Value);
            var p2 = int.Parse(Regex.Match(after, @"InternalBattery-\d+.*?\t(\d+)%").Groups[1].Value);
            info.BatteryPercent.Should().BeInRange(Math.Min(p1, p2) - 1, Math.Max(p1, p2) + 1, $"pmset said {before}");
            var state = percent.Groups[2].Value.Trim();
            if (state == "charging") {
                info.IsCharging.Should().BeTrue(before);
            } else if (state == "discharging") {
                info.IsCharging.Should().BeFalse(before);
                info.OnBattery.Should().BeTrue(before);
            }
            info.Summary.Should().StartWith($"Battery {info.BatteryPercent}%");
        }
    }
}
