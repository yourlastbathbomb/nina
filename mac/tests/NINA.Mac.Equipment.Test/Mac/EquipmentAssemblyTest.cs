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
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace NINA.Mac.Equipment.Test {

    /// <summary>
    /// The built NINA.Equipment.dll, read as metadata: what it references, what it imports natively, which device classes it
    /// carries and whether its legacy-encoded sources compiled to Windows' literals. These are the checks the M3b plan (§11) asks
    /// to repeat after every upstream merge; a vendor driver or a WPF dependency that slips into the kept files shows up here.
    /// </summary>
    [TestFixture]
    public class EquipmentAssemblyTest {

        private static readonly Assembly equipment = typeof(ICamera).Assembly;

        /// <summary>Assembly names (prefixes) that only excluded vendor code or WPF would pull in.</summary>
        private static readonly string[] forbiddenReferences = {
            "ASCOM", "Castle", "GrpcDotNetNamedPipes", "NJsonSchema", "NmeaParser", "NINA.MGEN", "nikoncswrapper",
            "PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml", "System.Windows.Forms", "System.Drawing.Common",
            "System.Management", "Microsoft.Win32.Registry",
        };

        private static MetadataReader Metadata(PEReader pe) => pe.GetMetadataReader();

        private static PEReader OpenEquipment() {
            return new PEReader(File.OpenRead(equipment.Location));
        }

        [Test]
        public void IsUpstreamsNinaEquipment_WithItsAssemblyInfo() {
            equipment.GetName().Name.Should().Be("NINA.Equipment");
            equipment.GetName().Version.Should().Be(typeof(NINA.Core.Utility.CoreUtil).Assembly.GetName().Version, "Engine.props stamps CommonAssemblyInfo's version");
            equipment.GetCustomAttribute<AssemblyTitleAttribute>()!.Title.Should().Be("NINA.Equipment");
            equipment.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description.Should().StartWith("This assembly contains the Equipment components of N.I.N.A.");
        }

        [Test]
        public void References_NoVendorOrWpfAssembly() {
            using var pe = OpenEquipment();
            var md = Metadata(pe);
            var references = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            TestContext.Out.WriteLine("References: " + string.Join(", ", references));

            references.Should().NotContain(r => forbiddenReferences.Any(f => r.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
            references.Should().Contain(new[] { "NINA.Core", "NINA.Profile", "NINA.Astrometry", "NINA.Image", "NINA.Mac.WpfCompat", "NINA.Mac.Native" });
        }

        [Test]
        public void NativeImports_AreOnlyTheZwoCameraSdk() {
            using var pe = OpenEquipment();
            var md = Metadata(pe);
            var modules = md.MethodDefinitions.Select(h => md.GetMethodDefinition(h).GetImport()).Where(i => !i.Module.IsNil)
                .Select(i => md.GetString(md.GetModuleReference(i.Module).Name)).ToList();

            modules.Distinct().Should().Equal("ASICamera2.dll");
            modules.Should().HaveCount(29, "upstream ASICameraDll.cs declares 29 imports (the plan counted 30 while the fork binding still carried the probe-only ASIGetLMHGainOffset, now in NINA.Mac.ZwoProbe/GainPresets.cs)");
        }

        [Test]
        public void DeviceClasses_AreTheRigsAndTheNoDevicePlaceholders() {
            var devices = equipment.GetTypes().Where(t => typeof(IDevice).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
                .Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();

            devices.Should().Equal(
                "NINA.Equipment.Equipment.DummyDevice",
                "NINA.Equipment.Equipment.MyCamera.ASICamera",
                "NINA.Equipment.Equipment.MyCamera.PersistSettingsCameraDecorator",
                "NINA.Equipment.Equipment.MyGuider.DirectGuider",
                "NINA.Equipment.Equipment.MyGuider.DummyGuider",
                "NINA.Equipment.Equipment.OfflineDevice");
        }

        [Test]
        public void DeviceContracts_AreCompiledUnchanged() {
            // The interfaces the mac drivers (LX200 mount and focuser, M4) implement and the engine consumes
            new[] {
                typeof(IDevice), typeof(ICamera), typeof(ITelescope), typeof(IFocuser), typeof(IGuider), typeof(IFilterWheel), typeof(IRotator),
                typeof(ICameraMediator), typeof(ITelescopeMediator), typeof(IFocuserMediator), typeof(IGuiderMediator), typeof(IImagingMediator),
                typeof(ICameraConsumer), typeof(ITelescopeConsumer), typeof(IFocuserConsumer), typeof(IGuiderConsumer),
                typeof(IDeviceChooserVM), typeof(IEquipmentProvider<>), typeof(IDockableVM),
            }.Should().OnlyContain(t => t.Assembly == equipment);
            typeof(ICamera).GetProperties().Select(p => p.Name).Should().Contain(new[] { "CameraXSize", "PixelSizeX", "SensorType", "CoolerOn", "Gain" });
            typeof(ASICamera).GetInterfaces().Should().Contain(typeof(ICamera));
            typeof(DirectGuider).GetInterfaces().Should().Contain(new[] { typeof(IGuider), typeof(ITelescopeConsumer) });
            // The [ObservableProperty] source generator ran for the partial DeviceInfo (CommunityToolkit.Mvvm reaches it through NINA.Core)
            typeof(CameraInfo).GetProperty(nameof(DeviceInfo.Connected))!.DeclaringType.Should().Be(typeof(DeviceInfo));
        }

        [Test]
        public void DockableIcon_IsWpfCompatsGeometryGroup() {
            var property = typeof(IDockableVM).GetProperty(nameof(IDockableVM.ImageGeometry))!;
            property.PropertyType.Should().Be(typeof(System.Windows.Media.GeometryGroup));
            property.PropertyType.Assembly.GetName().Name.Should().Be("NINA.Mac.WpfCompat");
            new System.Windows.Media.GeometryGroup().Should().BeAssignableTo<System.Windows.Media.Geometry>().And.BeAssignableTo<System.Windows.Freezable>();
        }

        [Test]
        public void LegacyEncodedSources_CompileToWindowsLiterals() {
            // 75 of the 125 kept files are Windows-1252 (Engine.props transcodes them). None has a non-ASCII literal today, so the
            // assembly must not contain a single replacement character, and no other non-ASCII character either
            using var pe = OpenEquipment();
            var md = Metadata(pe);
            var literals = new List<string>();
            var handle = MetadataTokens.UserStringHandle(1);
            while (!handle.IsNil) {
                literals.Add(md.GetUserString(handle));
                handle = md.GetNextHandle(handle);
            }

            literals.Should().NotBeEmpty();
            literals.Should().NotContain(s => s.Contains('�'));
            literals.Should().NotContain(s => s.Any(c => c > 0x7E));
        }
    }
}
