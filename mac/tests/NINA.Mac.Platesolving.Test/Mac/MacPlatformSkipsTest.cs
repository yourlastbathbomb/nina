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
using System.Reflection;

namespace NINA.Mac.Platesolving.Test {

    /// <summary>Keeps the skip list honest: every entry must name an upstream test that is linked into this assembly.</summary>
    [TestFixture]
    public class MacPlatformSkipsTest {

        [Test]
        public void EverySkippedTest_ExistsInALinkedUpstreamFixture() {
            var tests = typeof(MacPlatformSkipsTest).Assembly.GetTypes()
                .Where(t => t.Namespace != null && t.Namespace.StartsWith("NINA.Test", StringComparison.Ordinal))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.GetCustomAttributes().Any(a => a is TestAttribute || a is TestCaseAttribute || a is TestCaseSourceAttribute))
                    .Select(m => t.FullName + "." + m.Name))
                .ToHashSet();

            MacPlatformSkipsAttribute.Listed.Keys.Should().OnlyContain(name => tests.Contains(name));
            MacPlatformSkipsAttribute.Listed.Should().HaveCount(5);
            MacPlatformSkipsAttribute.Listed.Values.Should().OnlyContain(reason => reason.StartsWith("macOS: ", StringComparison.Ordinal));
        }
    }
}
