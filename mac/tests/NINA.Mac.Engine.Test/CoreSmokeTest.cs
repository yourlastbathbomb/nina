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
using Google.Protobuf;
using NINA.Core.API.ASCOM.Camera;
using NINA.Core.Interfaces;
using NINA.Core.Locale;
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Core.Utility.SerialCommunication;
using NINA.Core.Utility.WindowService;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data.SQLite;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;

namespace NINA.Mac.Engine.Test {

    [TestFixture]
    [NonParallelizable]
    public class CoreSmokeTest {

        [Test]
        public void NinaCore_IsTheMacBuild_OfUpstreamVersion() {
            var assembly = typeof(CoreUtil).Assembly;
            assembly.GetName().Name.Should().Be("NINA.Core");
            assembly.GetName().Version.Should().Be(new Version(3, 3, 0, 1064));
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
            references.Should().Contain("NINA.Mac.WpfCompat");
            references.Should().NotContain(new[] { "WindowsBase", "PresentationCore", "PresentationFramework", "System.Xaml" });
            CoreUtil.Version.Should().Be("3.3.0.1064");
        }

        [Test]
        public void Logger_WritesHeaderAndMessages_ToTheConfiguredDataFolder() {
            var marker = Guid.NewGuid().ToString("N");
            Logger.SetLogLevel(Core.Enum.LogLevelEnum.DEBUG);
            Logger.Info($"mac smoke info {marker}");
            Logger.Debug($"mac smoke debug {marker}");
            Logger.Trace($"mac smoke trace {marker}");

            var logs = Directory.GetFiles(Path.Combine(EngineTestHost.DataRoot, "Logs"), "*.log");
            logs.Should().ContainSingle();
            string text;
            using (var stream = new FileStream(logs[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                text = new StreamReader(stream).ReadToEnd();
            }
            text.Should().Contain("Version 3.3.0.1064");
            text.Should().Contain("Process Architecture Arm64");
            // GetTotalPhysicalMemory uses WMI, which throws on macOS; upstream catches it
            text.Should().Contain("Unable to determine Physical Memory");
            text.Should().Contain($"|INFO|CoreSmokeTest.cs|Logger_WritesHeaderAndMessages_ToTheConfiguredDataFolder|");
            text.Should().Contain($"mac smoke info {marker}");
            text.Should().Contain($"mac smoke debug {marker}");
            text.Should().NotContain($"mac smoke trace {marker}");
            Logger.SetLogLevel(Core.Enum.LogLevelEnum.INFO);
        }

        [Test]
        public void Loc_ReadsNeutralAndSatelliteResources() {
            var neutral = ReadResx("Locale.resx");
            var german = ReadResx("Locale.de-DE.resx");
            var key = german.First(kv => !string.IsNullOrWhiteSpace(kv.Value) && neutral.TryGetValue(kv.Key, out var n) && n != kv.Value).Key;
            try {
                Loc.Instance.ReloadLocale("en-US");
                Loc.Instance[key].Should().Be(neutral[key]);
                Loc.Instance["LblError"].Should().Be("Error");
                Loc.Instance.ReloadLocale("de-DE");
                Loc.Instance[key].Should().Be(german[key]);
                Loc.Instance["LblError"].Should().Be("Fehler");
                Loc.Instance["NoSuchKeyAnywhere"].Should().Be("MISSING LABEL NoSuchKeyAnywhere");
            } finally {
                Loc.Instance.ReloadLocale("en-US");
            }
        }

        private static Dictionary<string, string> ReadResx(string file) {
            var root = typeof(CoreSmokeTest).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "NinaRoot").Value;
            return XDocument.Load(Path.Combine(root, "NINA.Core", "Locale", file)).Root.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name"), d => (string)d.Element("value"));
        }

        [Test]
        public void SerialPortProvider_ListsOnlyCalloutDevices_WithoutWmi() {
            var provider = new SerialPortProvider();
            var expected = SerialPort.GetPortNames().Where(p => p.StartsWith("/dev/cu.", StringComparison.Ordinal)).OrderBy(p => p).ToList();

            provider.GetPortNames().Should().Equal(new[] { "----" }.Concat(expected));
            provider.GetPortNames(addDivider: false).Should().Equal(expected);
            provider.GetPortNames("SELECT * FROM Win32_PnPEntity", addDivider: false, addGenericPorts: false).Should().BeEmpty();
            provider.GetPortNames().Skip(1).Should().OnlyContain(p => p.StartsWith("/dev/cu."));

            // Creating the wrapper does not open the port; without WMI there is no Arduino Leonardo DTR override
            var port = provider.GetSerialPort("/dev/cu.does-not-exist", 9600, Parity.None, 8, StopBits.One, Handshake.None, false, "#", 1000, 1000);
            port.PortName.Should().Be("/dev/cu.does-not-exist");
            port.DtrEnable.Should().BeFalse();
        }

        [Test]
        public void DllLoader_IsANoOp_OffWindows() {
            FluentActions.Invoking(() => DllLoader.LoadDll(Path.Combine("SOFA", "SOFAlib.dll"))).Should().NotThrow();
            FluentActions.Invoking(() => DllLoader.LoadDllFromAbsolutePath("/nowhere/kernel32.dll")).Should().NotThrow();
        }

        [Test]
        public void AsyncObservableCollection_AcceptsChangesFromWorkerThreads() {
            var collection = new AsyncObservableCollection<int>();
            var events = new List<NotifyCollectionChangedAction>();
            collection.CollectionChanged += (s, e) => events.Add(e.Action);

            Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => collection.Add(i)))).Wait();
            collection.Remove(3);

            collection.Should().HaveCount(19).And.NotContain(3);
            events.Should().HaveCount(21);
        }

        [Test]
        public void Notification_RaisesPosted_WithUpstreamHeadersAndLifetimes() {
            var posted = new List<NotificationPostedEventArgs>();
            EventHandler<NotificationPostedEventArgs> handler = (s, e) => posted.Add(e);
            Notification.ShowError("nobody listens");
            Notification.Posted += handler;
            try {
                Notification.ShowError("e");
                Notification.ShowWarning("w");
                Notification.ShowInformation("i", TimeSpan.FromSeconds(3));
                Notification.ShowExternalError("x", "Camera");
            } finally {
                Notification.Posted -= handler;
            }
            posted.Select(p => (p.Kind, p.Message, p.Lifetime)).Should().Equal(
                (NotificationKind.Error, "e", TimeSpan.FromHours(24)),
                (NotificationKind.Warning, "w", TimeSpan.FromSeconds(30)),
                (NotificationKind.Information, "i", TimeSpan.FromSeconds(3)),
                (NotificationKind.ExternalError, "x", TimeSpan.FromHours(24)));
            posted[0].Header.Should().Be(Loc.Instance["LblError"]);
            posted[3].Header.Should().Be("Camera");
            FluentActions.Invoking(() => Notification.ConfigurePosition((NotificationWorkArea)42, NotificationCorner.TopLeft)).Should().Throw<ArgumentOutOfRangeException>();
        }

        private sealed class AnswerYes : IMyMessageBoxVM {
            public string Asked;

            public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxResult defaultResult) {
                Asked = $"{caption}|{messageBoxText}|{button}|{defaultResult}";
                return MessageBoxResult.Yes;
            }
        }

        [Test]
        public void MyMessageBox_NeedsAHost() {
            FluentActions.Invoking(() => MyMessageBox.Show("text", "caption")).Should().Throw<PlatformNotSupportedException>();
            var host = new AnswerYes();
            try {
                MyMessageBox.Host = host;
                MyMessageBox.Show("reset?", "Focuser", MessageBoxButton.YesNo, MessageBoxResult.No).Should().Be(MessageBoxResult.Yes);
                host.Asked.Should().Be("Focuser|reset?|YesNo|No");
                new MyMessageBoxVM().Show("a", "b", MessageBoxButton.OK, MessageBoxResult.OK).Should().Be(MessageBoxResult.Yes);
                MyMessageBox.Host = new MyMessageBoxVM();
                FluentActions.Invoking(() => MyMessageBox.Show("loop")).Should().Throw<InvalidOperationException>();
            } finally {
                MyMessageBox.Host = null;
            }
        }

        [Test]
        public void WpfWindowsAndDialogs_AreUnsupported() {
            var service = new WindowServiceFactory().Create();
            FluentActions.Invoking(() => service.ShowDialog(new object(), "Setup")).Should().Throw<PlatformNotSupportedException>();
            FluentActions.Invoking(() => service.Show(new object(), "Setup")).Should().Throw<PlatformNotSupportedException>();
            service.Close().IsCompletedSuccessfully.Should().BeTrue();
            FluentActions.Invoking(() => CoreUtil.GetFilteredFileDialog("/tmp", "x.fits", "FITS|*.fits")).Should().Throw<PlatformNotSupportedException>();
            FluentActions.Invoking(() => new LocExtension("LblError")).Should().Throw<PlatformNotSupportedException>();
            FluentActions.Invoking(() => new Window()).Should().Throw<PlatformNotSupportedException>();
        }

        [Test]
        public void GrpcContract_IsGenerated_AndSerializes() {
            var reply = new GetIntPropertyReply { Value = 3840 };
            GetIntPropertyReply.Parser.ParseFrom(reply.ToByteArray()).Value.Should().Be(3840);
            typeof(CameraService.CameraServiceClient).Should().NotBeNull();
        }

        [Test]
        public void SqliteNativeLibrary_LoadsOnArm64() {
            using var connection = new SQLiteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "select sqlite_version()";
            ((string)command.ExecuteScalar()).Should().StartWith("3.");
        }
    }
}
