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
using System.Linq;

namespace NINA.Mac.App.Test.Ui {

    /// <summary>The fork must not be called NINA in any user-visible name (MPL-2.0 grants no trademark rights).</summary>
    [TestFixture]
    public class AppIdentityTests {

        [TestCase("NINA", true)]
        [TestCase("N.I.N.A.", true)]
        [TestCase("N I N A Imager", true)]
        [TestCase("N-I-N-A", true)]
        [TestCase("Nina Imager", true)]
        [TestCase("local.nina.mac", true)]
        [TestCase("MyNINAMac", true)]
        [TestCase("Nightglass (working name)", false)]
        [TestCase("Nightglass", false)]
        [TestCase("local.nightglass.mac", false)]
        [TestCase("Night Glass", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void ContainsNina_IgnoresCaseSpacesAndPunctuation(string name, bool expected) {
            AppInfo.ContainsNina(name).Should().Be(expected);
        }

        [Test]
        public void DefaultIdentity_IsNightglass_AndFreeOfNina() {
            var info = AppInfo.Current;
            // The app's name is Nightglass, with no "(working name)" suffix: Finder, the Dock, the menu bar and the About box show it
            info.DisplayName.Should().Be("Nightglass");
            info.ShortName.Should().Be("Nightglass");
            info.BundleId.Should().Be("local.nightglass.mac");
            new[] { info.DisplayName, info.ShortName, info.BundleId }.Should().OnlyContain(n => !AppInfo.ContainsNina(n));
        }

        private static ProcessResult CheckAppIdentity(params string[] properties) {
            var args = new List<string> { "msbuild", MacRepo.AppProject, "-nologo", "-nodeReuse:false", "-v:quiet", "-t:CheckAppIdentity" };
            args.AddRange(properties.Select(p => "-p:" + p));
            // A nested build must not inherit the test runner's MSBuild state
            var env = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase) || k.StartsWith("MSBuild", StringComparison.Ordinal) || k == "DOTNET_HOST_PATH")
                .ToDictionary(k => k, _ => (string)null);
            return ChildProcess.Run(MacRepo.Dotnet, args, MacRepo.Root, env);
        }

        /// <summary>The build-time guard in NINA.Mac.App.csproj covers every derived name, not just AppDisplayName.</summary>
        [TestCase("AppShortName=NINA", "AppShortName 'NINA'")]
        [TestCase("AppDisplayName=N I N A Imager", "AppDisplayName 'N I N A Imager'")]
        [TestCase("AppExecutableName=N.I.N.A", "AppExecutableName 'N.I.N.A'")]
        [TestCase("AppBundleId=local.nina.mac", "AppBundleId 'local.nina.mac'")]
        public void Build_RejectsNinaInAnyAppName(string property, string message) {
            var result = CheckAppIdentity(property);
            result.ExitCode.Should().NotBe(0, result.Output);
            result.Output.Should().Contain(message).And.Contain("contains 'NINA'");
        }

        [Test]
        public void Build_AcceptsTheDefaultAndAnotherName() {
            var byDefault = CheckAppIdentity();
            byDefault.ExitCode.Should().Be(0, byDefault.Output);
            var renamed = CheckAppIdentity("AppDisplayName=Starlight Imager");
            renamed.ExitCode.Should().Be(0, renamed.Output);
        }
    }
}
