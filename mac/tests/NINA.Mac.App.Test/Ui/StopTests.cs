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
using NINA.Mac.App.Services;
using NINA.Mac.App.ViewModels;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Test.Ui {

    /// <summary>
    /// The ways to stop a moving mount from the app: the Target screen's Stop (cancels its slew and centring, then the mount's
    /// Abort), the status bar's Stop (shown while the mount slews, whichever screen started it), and the Focus loop giving the
    /// camera up when a run starts. Simulated devices on a clock whose waits last until they are cancelled, so a slew or an
    /// exposure stays under way until something stops it.
    /// </summary>
    [TestFixture]
    public class StopTests : HeadlessTestBase {

        private static AppServices CreateHeld() =>
            AppServices.Create(new AppServicesOptions {
                Clock = new HoldingClock(TestTimes.EveningOct10),
                Settings = new MemorySettingsStore(new AppSettings()),
                KeepAwake = new RecordingKeepAwake(),
                PowerSource = new FakePowerSource(),
                HomeDirectory = "/Users/test",
                SerialPortLister = () => new[] { new NINA.Mac.Platform.SerialPortInfo("/dev/cu.usbserial-A10KX5Z3", true) },
                FastSimulation = true,
            });

        private static async Task<bool> Eventually(Func<bool> condition) {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(5)) {
                if (condition()) {
                    return true;
                }
                await Task.Delay(20);
            }
            return condition();
        }

        [Test]
        public async Task Target_Stop_HaltsTheSlew_AndSaysSo() {
            using var services = CreateHeld();
            var vm = new MainWindowViewModel(services, null);
            await services.Mount.ConnectAsync();
            vm.Target.SearchText = "ngc 253";
            vm.Target.IsSlewing.Should().BeFalse();

            var slew = vm.Target.SlewAndCentreCommand.ExecuteAsync(null);
            (await Eventually(() => services.Mount.IsSlewing)).Should().BeTrue("the goto is under way");
            vm.Target.IsSlewing.Should().BeTrue("the Stop button shows");
            vm.StatusBar.IsMountSlewing.Should().BeTrue("the status bar offers its Stop too");

            vm.Target.StopSlewCommand.Execute(null);
            await slew;

            services.Mount.IsSlewing.Should().BeFalse();
            vm.Target.IsSlewing.Should().BeFalse();
            vm.Target.SlewStatus.Should().Contain("stopped");
            vm.Target.ErrorMessage.Should().BeNull("a stop is not an error");
            services.Mount.State.Should().Be(DeviceConnectionState.Connected);
            vm.StatusBar.IsMountSlewing.Should().BeFalse();
        }

        [Test]
        public async Task StatusBar_Stop_HaltsAGotoStartedElsewhere_AndTheRunMovingIt() {
            using var services = CreateHeld();
            var vm = new MainWindowViewModel(services, null);
            await services.Camera.ConnectAsync();
            await services.Mount.ConnectAsync();
            vm.Target.SearchText = "ngc 253";

            // A run starts with its goto
            var run = services.Session.RunAsync(new SessionPlan("NGC 253", vm.Target.SelectedTarget.RightAscensionHours, vm.Target.SelectedTarget.DeclinationDegrees,
                10, 5, 252, 8, 2, 0, 75, 20, true));
            (await Eventually(() => services.Mount.IsSlewing)).Should().BeTrue("the run's goto is under way");
            vm.StatusBar.IsMountSlewing.Should().BeTrue();

            vm.StatusBar.StopMountCommand.Execute(null);
            await run;

            services.Mount.IsSlewing.Should().BeFalse();
            services.Session.State.Should().Be(SessionState.Finished);
            services.Session.Progress.StopReason.Should().Be("Mount stopped by user");
            vm.StatusBar.IsMountSlewing.Should().BeFalse();
        }

        [Test]
        public async Task Focus_Loop_GivesTheCameraUp_WhenARunStarts() {
            using var services = CreateHeld();
            var vm = new MainWindowViewModel(services, null);
            await services.Camera.ConnectAsync();
            await services.Mount.ConnectAsync();
            vm.Target.SearchText = "ngc 253";

            var loop = vm.Focus.ToggleLoopCommand.ExecuteAsync(null);
            (await Eventually(() => services.Camera.IsExposing)).Should().BeTrue("the loop's first frame is under way");
            vm.Focus.IsLooping.Should().BeTrue();

            var run = services.Session.RunAsync(new SessionPlan("NGC 253", vm.Target.SelectedTarget.RightAscensionHours, vm.Target.SelectedTarget.DeclinationDegrees,
                10, 5, 252, 8, 2, 0, 75, 20, true));
            await loop;

            vm.Focus.IsLooping.Should().BeFalse("a run takes the camera");
            vm.Focus.ErrorMessage.Should().Contain("run started");
            services.Session.RequestStop("test over");
            await run;
        }

        /// <summary>Now stands still; every wait lasts until it is cancelled (a slew or an exposure stays under way).</summary>
        private sealed class HoldingClock : IClock {

            public HoldingClock(DateTimeOffset now) {
                Now = now;
            }

            public DateTimeOffset Now { get; }

            public Task Delay(TimeSpan delay, CancellationToken ct = default) =>
                delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(Timeout.Infinite, ct);
        }
    }
}
