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
using System.IO;
using System.Linq;

namespace NINA.Mac.App.Test.Platform {

    [TestFixture]
    [NonParallelizable]
    public class ResourcePathsTests {
        private string root;

        [SetUp]
        public void SetUp() {
            root = Path.Combine(Path.GetTempPath(), "ninamac-paths-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        [TearDown]
        public void TearDown() {
            Directory.Delete(root, true);
        }

        private string MakeBundle(string name = "Night Glass (wip).app") {
            var contents = Path.Combine(root, name, "Contents");
            foreach (var d in new[] { "MacOS", "Frameworks", "Resources", Path.Combine("Resources", "licenses") }) {
                Directory.CreateDirectory(Path.Combine(contents, d));
            }
            File.WriteAllText(Path.Combine(contents, "Resources", "JPLEPH"), "eph");
            File.WriteAllText(Path.Combine(contents, "Resources", "licenses", "ZWO-LICENSE.txt"), "mit");
            File.WriteAllText(Path.Combine(contents, "MacOS", "JPLEPH"), "wrong copy in MacOS");
            File.WriteAllText(Path.Combine(contents, "Frameworks", "libASICamera2.dylib"), "fw");
            // AppleDouble file, as unzip or a copy to a non-APFS volume leaves next to each file with extended attributes
            File.WriteAllText(Path.Combine(contents, "Frameworks", "._libASICamera2.dylib"), "xattrs");
            File.WriteAllText(Path.Combine(contents, "MacOS", "libASICamera2.dylib"), "macos copy");
            File.WriteAllText(Path.Combine(contents, "MacOS", "libSkiaSharp.dylib"), "skia");
            return Path.Combine(contents, "MacOS") + "/";
        }

        [Test]
        public void Bundle_ResourcesComeFromContentsResources_AndDylibsFromFrameworksFirst() {
            var macOS = MakeBundle();
            var paths = new ResourcePaths(macOS);

            paths.IsAppBundle.Should().BeTrue();
            paths.BundlePath.Should().Be(Path.Combine(root, "Night Glass (wip).app"));
            paths.ResourcesDirectory.Should().EndWith("/Contents/Resources");
            paths.FrameworksDirectory.Should().EndWith("/Contents/Frameworks");
            paths.ExecutableDirectory.Should().EndWith("/Contents/MacOS");
            paths.FindResource("JPLEPH").Should().Be(Path.Combine(paths.ResourcesDirectory, "JPLEPH"));
            paths.FindResource("licenses/ZWO-LICENSE.txt").Should().Be(Path.Combine(paths.ResourcesDirectory, "licenses", "ZWO-LICENSE.txt"));
            paths.FindResourceDirectory("licenses").Should().Be(Path.Combine(paths.ResourcesDirectory, "licenses"));
            paths.FindNativeLibrary("libASICamera2.dylib").Should().Be(Path.Combine(paths.FrameworksDirectory, "libASICamera2.dylib"));
            paths.FindNativeLibrary("libSkiaSharp.dylib").Should().Be(Path.Combine(paths.ExecutableDirectory, "libSkiaSharp.dylib"));
            paths.VendorLibraries().Select(Path.GetFileName).Should().Equal("libASICamera2.dylib");
            paths.FindResource("missing.bin").Should().BeNull();
            paths.Invoking(p => p.GetResource("missing.bin")).Should().Throw<FileNotFoundException>().WithMessage("*Contents/Resources*");
        }

        [Test]
        public void VendorLibraries_SkipAppleDoubleFiles() {
            var paths = new ResourcePaths(MakeBundle());
            File.Exists(Path.Combine(paths.FrameworksDirectory, "._libASICamera2.dylib")).Should().BeTrue();
            paths.VendorLibraries().Should().OnlyContain(p => !Path.GetFileName(p).StartsWith("._", StringComparison.Ordinal))
                .And.ContainSingle();
            ResourcePaths.IsAppleDouble("/x/Contents/Frameworks/._libusb-1.0.0.dylib").Should().BeTrue();
            ResourcePaths.IsAppleDouble("/x/Contents/Frameworks/libusb-1.0.0.dylib").Should().BeFalse();
        }

        [Test]
        public void Bundle_ResolutionDoesNotDependOnTheCurrentDirectory() {
            var macOS = MakeBundle();
            var original = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = "/";  // what a Finder launch gives the process
                var fromRoot = new ResourcePaths(macOS);
                Environment.CurrentDirectory = Path.GetTempPath();
                var fromTemp = new ResourcePaths(macOS);
                fromRoot.FindResource("JPLEPH").Should().Be(fromTemp.FindResource("JPLEPH")).And.StartWith(root);
                fromRoot.FindNativeLibrary("libASICamera2.dylib").Should().Be(fromTemp.FindNativeLibrary("libASICamera2.dylib"));
            } finally {
                Environment.CurrentDirectory = original;
            }
        }

        [Test]
        public void BuildOutput_FindsFilesNextToAssemblies_AndInAResourcesSubfolder() {
            var dir = Path.Combine(root, "bin", "Debug", "net10.0", "osx-arm64");
            Directory.CreateDirectory(Path.Combine(dir, "Resources"));
            File.WriteAllText(Path.Combine(dir, "JPLEPH"), "eph");
            File.WriteAllText(Path.Combine(dir, "Resources", "LICENSE.txt"), "mpl");
            File.WriteAllText(Path.Combine(dir, "libASICamera2.dylib"), "zwo");

            var paths = new ResourcePaths(dir);
            paths.IsAppBundle.Should().BeFalse();
            paths.BundlePath.Should().BeNull();
            paths.FindResource("JPLEPH").Should().Be(Path.Combine(dir, "JPLEPH"));
            paths.FindResource("LICENSE.txt").Should().Be(Path.Combine(dir, "Resources", "LICENSE.txt"));
            paths.FindNativeLibrary("libASICamera2.dylib").Should().Be(Path.Combine(dir, "libASICamera2.dylib"));
        }

        [TestCase("Contents/MacOS")]          // not inside an .app
        [TestCase("X.app/MacOS")]             // missing Contents
        [TestCase("X.app/Contents/Resources")] // not the executable folder
        public void NotABundle(string relative) {
            var dir = Path.Combine(root, relative);
            Directory.CreateDirectory(dir);
            new ResourcePaths(dir).IsAppBundle.Should().BeFalse();
        }

        [Test]
        public void RelativeBaseDirectory_IsRejected() {
            Action act = () => new ResourcePaths("bin/Debug");
            act.Should().Throw<ArgumentException>().WithMessage("*absolute*");
        }

        [TestCase("../etc/passwd")]
        [TestCase("a/../../b")]
        [TestCase("/etc/hosts")]
        public void ResourcePathsMustStayInside(string bad) {
            var paths = new ResourcePaths(MakeBundle());
            paths.Invoking(p => p.FindResource(bad)).Should().Throw<ArgumentException>();
        }

        [Test]
        public void Current_IsTheTestHostBaseDirectory() {
            ResourcePaths.Current.BaseDirectory.Should().Be(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
            ResourcePaths.Current.IsAppBundle.Should().BeFalse();
        }

        [Test]
        public void MacBundle_ReportsTheHostProcessBundle() {
            // The test host is a plain executable (dotnet), so NSBundle.mainBundle is its folder and has no identifier
            var path = MacBundle.MainBundlePath();
            path.Should().NotBeNullOrEmpty();
            Directory.Exists(path).Should().BeTrue(path);
            MacBundle.MainBundleIdentifier().Should().BeNull();
            // ... and no Info.plist entries (the packaged app's smoke test reads CFBundleName etc. this way)
            MacBundle.MainBundleInfoString("CFBundleIdentifier").Should().BeNull();
            MacBundle.MainBundleInfoString("NoSuchKey").Should().BeNull();
        }
    }

    [TestFixture]
    public class UserDataPathsTests {
        private static readonly AppIdentity Identity = new("Nightglass", "Nightglass", "local.nightglass.mac");

        [Test]
        public void DefaultFolders() {
            var paths = new UserDataPaths(Identity, "/Users/astro");
            paths.ImagesRoot.Should().Be("/Users/astro/Astro/Nightglass");
            paths.DefaultImagesRoot.Should().Be(paths.ImagesRoot);
            paths.SettingsDirectory.Should().Be("/Users/astro/Library/Application Support/Nightglass");
            paths.SettingsFile.Should().Be("/Users/astro/Library/Application Support/Nightglass/settings.json");
            paths.LogsDirectory.Should().Be("/Users/astro/Library/Logs/Nightglass");
            paths.CachesDirectory.Should().Be("/Users/astro/Library/Caches/local.nightglass.mac");
            paths.CloudSyncWarning(paths.ImagesRoot).Should().BeNull();
        }

        [Test]
        public void RealHome_IsAbsolute_AndImagesStayOutOfDocuments() {
            var paths = new UserDataPaths(Identity);
            Path.IsPathFullyQualified(paths.Home).Should().BeTrue();
            paths.ImagesRoot.Should().Be(Path.Combine(paths.Home, "Astro", "Nightglass"));
            paths.CloudSyncWarning(paths.ImagesRoot).Should().BeNull();
        }

        [TestCase("~/Pictures/astro", "/Users/astro/Pictures/astro")]
        [TestCase("/Volumes/SSD/astro", "/Volumes/SSD/astro")]
        public void ImagesRootOverride(string setting, string expected) {
            new UserDataPaths(Identity, "/Users/astro", setting).ImagesRoot.Should().Be(expected);
        }

        [Test]
        public void RelativeImagesRoot_IsRejected() {
            Action act = () => new UserDataPaths(Identity, "/Users/astro", "Astro/frames");
            act.Should().Throw<ArgumentException>();
        }

        [TestCase("~/Documents/Astro", true)]
        [TestCase("~/Desktop", true)]
        [TestCase("~/Library/Mobile Documents/com~apple~CloudDocs/astro", true)]
        [TestCase("~/documents/Astro", true)]        // the home volume is case-insensitive APFS: same folder
        [TestCase("~/DESKTOP/frames", true)]
        [TestCase("/Users/astro/documents", true)]
        [TestCase("~/library/mobile documents/com~apple~CloudDocs", true)]
        [TestCase("~/Library/CloudStorage/OneDrive-Personal/astro", true)]  // File Provider roots (OneDrive, Dropbox, Google Drive)
        [TestCase("~/Library/CloudStorage/Dropbox", true)]
        [TestCase("~/DocumentsArchive", false)]
        [TestCase("~/documentsarchive", false)]
        [TestCase("~/Astro/Nightglass", false)]
        [TestCase("~/Library/Caches/local.nightglass.mac", false)]
        public void CloudSyncWarning(string path, bool warns) {
            var paths = new UserDataPaths(Identity, "/Users/astro");
            (paths.CloudSyncWarning(path) != null).Should().Be(warns);
        }

        [Test]
        public void CloudSyncWarning_NamesTheService() {
            var paths = new UserDataPaths(Identity, "/Users/astro");
            paths.CloudSyncWarning("~/documents/astro").Should().Contain("iCloud").And.Contain("/Users/astro/documents/astro");
            paths.CloudSyncWarning("~/Library/CloudStorage/OneDrive-Personal").Should().Contain("cloud storage provider").And.NotContain("iCloud");
        }

        [Test]
        public void EnsureAppDirectories_CreatesSettingsAndLogsOnly() {
            var home = Path.Combine(Path.GetTempPath(), "ninamac-home-" + Guid.NewGuid().ToString("N"));
            try {
                var paths = new UserDataPaths(Identity, home);
                paths.EnsureAppDirectories();
                Directory.Exists(paths.SettingsDirectory).Should().BeTrue();
                Directory.Exists(paths.LogsDirectory).Should().BeTrue();
                Directory.Exists(paths.ImagesRoot).Should().BeFalse();
            } finally {
                Directory.Delete(home, true);
            }
        }

        [TestCase("Night/Glass")]
        [TestCase("..")]
        [TestCase("a:b")]
        public void ShortNameMustBeOneFolder(string shortName) {
            Action act = () => new AppIdentity("X", shortName, "local.x");
            act.Should().Throw<ArgumentException>();
        }
    }

    [TestFixture]
    public class SerialPortsTests {

        [Test]
        public void ListsCallOutNodesOnly_UsbFirst() {
            var dev = Path.Combine(Path.GetTempPath(), "ninamac-dev-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dev);
            try {
                foreach (var n in new[] { "cu.Bluetooth-Incoming-Port", "cu.usbserial-A10KX5Z3", "tty.usbserial-A10KX5Z3", "cu.usbmodem1101", "null" }) {
                    File.WriteAllText(Path.Combine(dev, n), "");
                }
                var ports = SerialPorts.List(dev);
                ports.Select(p => p.Name).Should().Equal("cu.usbmodem1101", "cu.usbserial-A10KX5Z3", "cu.Bluetooth-Incoming-Port");
                ports.Select(p => p.IsUsbSerial).Should().Equal(true, true, false);
            } finally {
                Directory.Delete(dev, true);
            }
        }

        [Test]
        public void RealDev_ReturnsOnlyCuNodes() {
            SerialPorts.List().Should().OnlyContain(p => p.Path.StartsWith("/dev/cu.", StringComparison.Ordinal));
        }

        [Test]
        public void MissingDirectory_GivesEmptyList() {
            SerialPorts.List("/nonexistent-dev-dir").Should().BeEmpty();
        }
    }
}
