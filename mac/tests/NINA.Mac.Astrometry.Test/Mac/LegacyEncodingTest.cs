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
using NINA.Astrometry;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace NINA.Mac.Astrometry.Test {

    /// <summary>
    /// Upstream's Windows-1252 source files must compile to the same string literals as on Windows (see the legacy-encoding
    /// step in mac/src/Engine.props). Without it the C# compiler on macOS decodes them as UTF-8 and turns every non-ASCII
    /// byte in a literal into U+FFFD.
    /// </summary>
    [TestFixture]
    public class LegacyEncodingTest {

        [Test]
        public void FocusTargetInformation_HasTheDegreeSign() {
            // NINA.Astrometry/FocusTarget.cs is Windows-1252; its degree signs are byte 0xB0
            var target = new FocusTarget("Vega") { Altitude = 45.0, Azimuth = 120.0 };

            target.Information.Should().Contain("°, Az: ").And.EndWith("°)").And.NotContain("�");
        }

        [TestCase("NINA.Core")]
        [TestCase("NINA.Profile")]
        [TestCase("NINA.Astrometry")]
        public void EngineAssembly_HasNoReplacementCharacterInItsStringLiterals(string assemblyName) {
            var path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();

            var literals = new List<string>();
            var handle = MetadataTokens.UserStringHandle(1);
            while (!handle.IsNil) {
                literals.Add(metadata.GetUserString(handle));
                handle = metadata.GetNextHandle(handle);
            }

            literals.Should().NotBeEmpty();
            literals.Where(s => s.Contains('�')).Should().BeEmpty($"{assemblyName}.dll must not contain mis-decoded Windows-1252 literals");
        }
    }
}
