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
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace NINA.Mac.App.Test.Packaging {

    /// <summary>
    /// mac/packaging/bundle_tools.py, the helper package-app.sh uses for licence notices and signing, run on small
    /// inputs (the full packaging run takes about 40 s and is checked by the script itself).
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class PackagingToolsTests {
        private string temp;

        [SetUp]
        public void SetUp() {
            temp = Path.Combine(Path.GetTempPath(), "ninamac-packaging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
        }

        [TearDown]
        public void TearDown() {
            Directory.Delete(temp, true);
        }

        private static ProcessResult Tools(params string[] args) =>
            ChildProcess.Run("/usr/bin/python3", new[] { MacRepo.BundleTools }.Concat(args));

        private static ProcessResult Run(string file, params string[] args) => ChildProcess.Run(file, args);

        private string Folder(string name) {
            var dir = Path.Combine(temp, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Test]
        public void NativeLicenses_ADylibWithoutALicenceEntry_StopsPackaging() {
            var frameworks = Folder("Frameworks");
            File.WriteAllText(Path.Combine(frameworks, "libmystery.dylib"), "");
            var result = Tools("native-licenses", frameworks, MacRepo.NativeStage, MacRepo.NinaRoot, Folder("Resources"));
            result.ExitCode.Should().NotBe(0);
            result.Output.Should().Contain("no licence entry for libmystery.dylib");
        }

        [Test]
        public void NativeLicenses_IgnoreAppleDoubleFiles() {
            var frameworks = Folder("Frameworks");
            File.WriteAllText(Path.Combine(frameworks, "._libmystery.dylib"), "");
            var result = Tools("native-licenses", frameworks, MacRepo.NativeStage, MacRepo.NinaRoot, Folder("Resources"));
            result.ExitCode.Should().Be(0, result.Output);
            result.StdOut.Should().BeEmpty();
        }

        /// <summary>Every dylib staged for the bundle gets its licence file and notice, with the statements the licences ask for.</summary>
        [Test]
        public void NativeLicenses_CoverEveryStagedLibrary() {
            var staged = Directory.Exists(MacRepo.NativeStage) ? Directory.GetFiles(MacRepo.NativeStage, "*.dylib").Select(Path.GetFileName).ToArray() : Array.Empty<string>();
            if (staged.Length == 0) {
                Assert.Ignore($"No staged dylibs in {MacRepo.NativeStage} (run mac/scripts/stage-zwo.sh and build-astrometry-natives.sh)");
            }
            var resources = Folder("Resources");
            var ephemeris = Path.Combine(MacRepo.Root, "native", "ephemeris", "JPLEPH");
            if (File.Exists(ephemeris)) {
                File.Copy(ephemeris, Path.Combine(resources, "JPLEPH"));
            }

            var result = Tools("native-licenses", MacRepo.NativeStage, MacRepo.NativeStage, MacRepo.NinaRoot, resources);
            TestContext.Out.WriteLine(result.Output);
            result.ExitCode.Should().Be(0, result.Output);
            var notices = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => File.ReadAllText(p[1]));
            foreach (var name in staged) {
                notices.Keys.Should().Contain(k => k.Contains($"({name})"), $"{name} needs a notice");
            }
            string Notice(string name) => notices.Single(n => n.Key.Contains($"({name})")).Value;
            var licenses = Path.Combine(resources, "licenses");
            if (staged.Contains("libsofa.dylib")) {
                // SOFA licence clause 3(a)
                Notice("libsofa.dylib").Should().Contain("uses routines and computations derived by its developers from software provided by SOFA under")
                    .And.Contain("does not itself constitute software provided by and/or endorsed by SOFA")
                    .And.Contain("SOFA Software License");
                File.Exists(Path.Combine(licenses, "SOFA-LICENSE.txt")).Should().BeTrue();
            }
            if (staged.Contains("libnovas31.dylib")) {
                Notice("libnovas31.dylib").Should().Contain("Astronomical Applications Department of the U.S. Naval Observatory");
                File.Exists(Path.Combine(licenses, "NOVAS-C3.1-README.txt")).Should().BeTrue();
            }
            if (staged.Contains("libusb-1.0.0.dylib")) {
                Notice("libusb-1.0.0.dylib").Should().MatchRegex(@"is libusb \d+\.\d+\.\d+ as built by Homebrew")
                    .And.Contain("GNU Lesser General Public").And.Contain("Copyright");
                File.Exists(Path.Combine(licenses, "libusb-LGPL-2.1.txt")).Should().BeTrue();
            }
            if (staged.Contains("libASICamera2.dylib")) {
                File.Exists(Path.Combine(licenses, "ZWO-ASI-SDK-LICENSE.txt")).Should().BeTrue();
            }
            if (File.Exists(ephemeris)) {
                Notice("Resources/JPLEPH").Should().Contain("JPL Planetary Ephemeris DE").And.Contain("Jet Propulsion Laboratory");
            }
        }

        /// <summary>
        /// The runtime's createdump needs com.apple.security.cs.debugger to dump a crashed process. Re-signing it ad hoc
        /// must keep that entitlement, and check-entitlements must notice when it is lost.
        /// </summary>
        [Test]
        public void Sign_KeepsCreatedumpEntitlements_AndCheckEntitlementsCatchesLosingThem() {
            var runtime = RuntimeEnvironment.GetRuntimeDirectory();
            var original = Path.Combine(runtime, "createdump");
            if (!File.Exists(original)) {
                Assert.Ignore($"No createdump in {runtime}");
            }
            Run("/usr/bin/codesign", "-d", "--entitlements", "-", "--xml", original).StdOut
                .Should().Contain("com.apple.security.cs.debugger", "precondition: the runtime ships createdump entitled");
            var bundle = Folder("MacOS");
            var copy = Path.Combine(bundle, "createdump");
            File.Copy(original, copy);

            var signed = Tools("sign", bundle);
            signed.ExitCode.Should().Be(0, signed.Output);
            signed.StdOut.Should().Contain("1 Mach-O (1 with entitlements kept)");
            Run("/usr/bin/codesign", "-dv", copy).StdErr.Should().Contain("Signature=adhoc");
            Run("/usr/bin/codesign", "-d", "--entitlements", "-", "--xml", copy).StdOut.Should().Contain("com.apple.security.cs.debugger");
            Run("/usr/bin/codesign", "--verify", "--strict", copy).ExitCode.Should().Be(0);

            // check-entitlements compares only Mach-O files that exist in both trees; create a reference tree with createdump alone
            var reference = Folder("publish");
            File.Copy(original, Path.Combine(reference, "createdump"));
            Tools("check-entitlements", reference, bundle).ExitCode.Should().Be(0);
            Run("/usr/bin/codesign", "--force", "--sign", "-", "--timestamp=none", copy).ExitCode.Should().Be(0); // the old, plain re-sign
            var lost = Tools("check-entitlements", reference, bundle);
            lost.ExitCode.Should().NotBe(0);
            lost.StdOut.Should().Contain("entitlements lost: createdump");
        }

        /// <summary>Fonts embedded in NuGet assemblies carry their own licence (Inter: SIL OFL 1.1), which the package's MIT entry hides.</summary>
        [Test]
        public void Notices_ListFontsEmbeddedInPackages() {
            var deps = Path.Combine(Path.GetDirectoryName(MacRepo.AppBinary), "NINA.Mac.App.deps.json");
            var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            var output = Path.Combine(temp, "THIRD-PARTY-NOTICES.txt");
            var result = Tools("notices", deps, nuget, output);
            result.ExitCode.Should().Be(0, result.Output);
            var text = File.ReadAllText(output);
            text.Should().Contain("--- Fonts embedded in Avalonia.Fonts.Inter (their own licence, not the package's) ---")
                .And.Contain("The Inter Project Authors")
                .And.Contain("SIL Open Font License, Version 1.1");
        }
    }
}
