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
using Moq;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Mac.Sequencer.Test.Sim;
using NINA.Mac.Sequencing.Headless;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Mediator;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace NINA.Mac.Sequencer.Test {

    /// <summary>The headless stand-ins: the image history's counting (dithering depends on it) and the absent devices' handlers.</summary>
    [TestFixture]
    public class HeadlessServicesTest {

        private static IProfileService Profile() {
            var profile = new NINA.Profile.Profile("headless");
            profile.CameraSettings.PixelSize = 2.9;
            profile.TelescopeSettings.FocalLength = 2500;
            var service = new Mock<IProfileService>();
            service.SetupGet(x => x.ActiveProfile).Returns(profile);
            return service.Object;
        }

        [Test]
        public void ImageHistory_CountsWhatTakeExposureAdds_AndNumbersFramesFromOneCounter() {
            var history = new HeadlessImageHistory(Profile(), null!);
            history.PlotClear();
            var first = history.GetNextImageId();
            var second = history.GetNextImageId();
            second.Should().Be(first + 1);
            history.Add(first, "LIGHT");
            history.Add(second, "LIGHT");
            history.ImageHistory.Should().HaveCount(2);
            history.ImageHistory.Select(p => p.Id).Should().Equal(first, second);
            history.PlotClear();
            history.ImageHistory.Should().BeEmpty();
            history.GetNextImageId().Should().Be(1, "PlotClear resets the id counter, as upstream");
        }

        [Test]
        public void DisconnectedDevice_AnswersAsADisconnectedDevice_AndNeverThrows() {
            var wheel = DisconnectedDevice.Create<IFilterWheelVM, FilterWheelInfo>();
            wheel.GetDeviceInfo().Connected.Should().BeFalse();
            wheel.GetDeviceInfo().Should().NotBeSameAs(wheel.GetDeviceInfo());
            wheel.Connect().Result.Should().BeFalse();
            wheel.Rescan().Result.Should().BeEmpty();
            wheel.Disconnect().IsCompletedSuccessfully.Should().BeTrue();
            wheel.GetDevice().Should().BeNull();
            wheel.Invoking(w => w.Connected += (_, _) => Task.CompletedTask).Should().NotThrow();

            // Behind NINA's own mediator, as the host registers it
            var mediator = new FocuserMediator();
            mediator.RegisterHandler(DisconnectedDevice.Create<IFocuserVM, FocuserInfo>());
            mediator.GetInfo().Should().NotBeNull().And.Match<FocuserInfo>(i => !i.Connected);
        }

        [Test]
        public void HeadlessApplicationStatus_RaisesEveryStatusUpdate() {
            var status = new HeadlessApplicationStatus();
            var mediator = new ApplicationStatusMediator();
            mediator.RegisterHandler(status);
            var seen = new List<string>();
            status.StatusUpdated += (_, s) => seen.Add(s.Status);
            mediator.StatusUpdate(new NINA.Core.Model.ApplicationStatus { Source = "Camera", Status = "Exposing" });
            seen.Should().Equal("Exposing");
        }
    }

    /// <summary>The host's disconnect never leaves the cooler running (NINA's ASICamera.Disconnect would only close the camera).</summary>
    [TestFixture]
    [NonParallelizable]
    public class HeadlessHostDisconnectTest {

        [Test]
        public async Task Disconnect_SwitchesTheCoolerOff_ThenDisconnectsEveryDevice() {
            var now = DateTime.Now;
            var rig = await SimRig.Create("disconnect", Sky.TargetAt(40, rising: true, decDeg: 0, now), 0, 0, new FixedNighttimeCalculator(now.AddHours(1)));
            try {
                // Through NINA's CameraVM, as a CoolCamera item would leave it
                var camera = (ICamera)rig.Host.CameraMediator.GetDevice();
                camera.CoolerOn = true;
                rig.Camera.CoolerOn.Should().BeTrue();

                await rig.Host.DisconnectAsync();

                rig.Camera.CoolerWrites.Should().Equal(true, false);
                rig.Camera.CoolerOn.Should().BeFalse();
                rig.Camera.Connected.Should().BeFalse();
                rig.Mount.Connected.Should().BeFalse();
                rig.Host.CameraMediator.GetInfo().Connected.Should().BeFalse();
                rig.Host.TelescopeMediator.GetInfo().Connected.Should().BeFalse();
                rig.Host.GuiderMediator.GetInfo().Connected.Should().BeFalse();
            } finally {
                rig.Host.Dispose();
            }
        }
    }

    /// <summary>
    /// The WpfCompat stand-ins the sequencer needs at run time (plan section 1.4): WeakEventManager must work (DeepSkyObjectContainer
    /// pushes target changes to its children through it), Dispatcher.InvokeAsync, and the UI placeholders throw. WpfApiSurfaceTest
    /// (NINA.Mac.Engine.Test) separately checks every compat type and member against WPF's metadata.
    /// </summary>
    [TestFixture]
    public class WpfCompatSequencerTest {

        private sealed class Source {
            public event EventHandler? Changed;
            public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
            public int Subscribers => Changed?.GetInvocationList().Length ?? 0;
        }

        private sealed class Listener {
            public int Calls;
            public void OnChanged(object? sender, EventArgs e) => Calls++;
        }

        [Test]
        public void WeakEventManager_DeliversToTheListener_AndRemoveStopsIt() {
            var source = new Source();
            var listener = new Listener();
            WeakEventManager<Source, EventArgs>.AddHandler(source, nameof(Source.Changed), listener.OnChanged);
            source.Raise();
            listener.Calls.Should().Be(1);
            source.Subscribers.Should().Be(1, "one forwarder per source and event");
            WeakEventManager<Source, EventArgs>.RemoveHandler(source, nameof(Source.Changed), listener.OnChanged);
            source.Raise();
            listener.Calls.Should().Be(1);
            source.Subscribers.Should().Be(0, "the forwarder is detached once no handler is left");
        }

        [Test]
        public void WeakEventManager_DoesNotKeepTheListenerAlive() {
            var source = new Source();
            var reference = Subscribe(source);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            reference.IsAlive.Should().BeFalse("the source holds the forwarder, never the listener");
            source.Invoking(s => s.Raise()).Should().NotThrow();
            source.Subscribers.Should().Be(1);
        }

        private static WeakReference Subscribe(Source source) {
            var listener = new Listener();
            WeakEventManager<Source, EventArgs>.AddHandler(source, nameof(Source.Changed), listener.OnChanged);
            return new WeakReference(listener);
        }

        [Test]
        public void WeakEventManager_RejectsUnknownEventsAndNullHandlers_AsWpf() {
            var source = new Source();
            FluentActions.Invoking(() => WeakEventManager<Source, EventArgs>.AddHandler(source, "Nope", (_, _) => { })).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => WeakEventManager<Source, EventArgs>.AddHandler(source, nameof(Source.Changed), null!)).Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void WeakEventManager_WorksOnInterfaceEvents_AsDeepSkyObjectContainerUsesItForTheProfile() {
            var listener = new Listener();
            var profile = new Mock<IProfileService>();
            WeakEventManager<IProfileService, EventArgs>.AddHandler(profile.Object, nameof(IProfileService.LocationChanged), listener.OnChanged);
            profile.Raise(p => p.LocationChanged += null, EventArgs.Empty);
            listener.Calls.Should().Be(1);
        }

        [Test]
        public async Task DispatcherInvokeAsync_Runs_AndACancelledTokenAbortsIt() {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var ran = false;
            await dispatcher.InvokeAsync(() => ran = true).Task;
            ran.Should().BeTrue();
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var operation = dispatcher.InvokeAsync(() => ran = false, DispatcherPriority.DataBind, cancelled.Token);
            await FluentActions.Awaiting(() => operation.Task).Should().ThrowAsync<TaskCanceledException>();
            operation.Status.Should().Be(DispatcherOperationStatus.Aborted);
            ran.Should().BeTrue();
        }

        [Test]
        public void UiPlaceholders_Throw() {
            FluentActions.Invoking(() => new System.Windows.Data.CollectionViewSource()).Should().Throw<PlatformNotSupportedException>();
            FluentActions.Invoking(() => System.Windows.Data.CollectionViewSource.GetDefaultView(new List<int>())).Should().Throw<PlatformNotSupportedException>();
            FluentActions.Invoking(() => new System.Windows.Controls.TextBox()).Should().Throw<PlatformNotSupportedException>();
            FluentActions.Invoking(() => new Microsoft.Win32.OpenFileDialog()).Should().Throw<PlatformNotSupportedException>();
        }

        [Test]
        public void SolidColorBrush_HoldsItsColour_AndAFrozenBrushRefusesANewOne() {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);
            brush.Color.Should().Be(System.Windows.Media.Colors.Orange);
            brush.Freeze();
            brush.Invoking(b => b.Color = System.Windows.Media.Colors.Red).Should().Throw<InvalidOperationException>();
            var sort = new SortDescription("Entity.Name", ListSortDirection.Ascending);
            sort.PropertyName.Should().Be("Entity.Name");
            FluentActions.Invoking(() => new SortDescription("x", (ListSortDirection)7)).Should().Throw<InvalidEnumArgumentException>();
        }
    }
}
