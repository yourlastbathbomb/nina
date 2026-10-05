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
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// Windows-1252 sources must compile to the literals Windows builds get (mac/src/Engine.props, legacy encoding).
    /// 40 of NINA.Image's 68 files and 149 of Accord.Imaging's 288 are not UTF-8. NINA.Image.Mac imports Engine.props, so its
    /// copies are transcoded. Accord.Imaging.Mac does not (it keeps Accord's own build settings); that is safe only while the
    /// non-ASCII bytes in those files stay in comments, which this test checks on the built assembly.
    /// </summary>
    [TestFixture]
    public class LegacyEncodingImageTest {

        private static List<string> Literals(string assemblyFile) {
            using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, assemblyFile));
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var literals = new List<string>();
            var handle = MetadataTokens.UserStringHandle(1);
            while (!handle.IsNil) {
                literals.Add(metadata.GetUserString(handle));
                handle = metadata.GetNextHandle(handle);
            }
            return literals;
        }

        [Test]
        public void NinaImage_HasNoMisdecodedLiterals() {
            var literals = Literals("NINA.Image.dll");

            literals.Should().NotBeEmpty();
            // The only U+FFFD is deliberate: XISFHeader.cs (UTF-8) replaces characters XML cannot carry with "�"
            literals.Where(s => s.Contains('�')).Should().Equal("�");
            // A non-ASCII literal from a UTF-8 file survives as written (StarDetection.cs log line)
            literals.Should().Contain(s => s.Contains("HFR σ"));
        }

        [Test]
        public void AccordImaging_HasNoMisdecodedLiterals() {
            var literals = Literals("Accord.Imaging.dll");

            literals.Should().NotBeEmpty();
            literals.Should().NotContain(s => s.Contains('�'));
        }
    }
}
