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
using NINA.Mac.Siril.Test.Synthetic;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NINA.Mac.Siril.Test {

    /// <summary>The C# path-template resolver against siril-cli's own "parse -r" on the same FITS headers.</summary>
    [TestFixture]
    public class SirilParityTest {
        private string dir;

        [SetUp]
        public void SetUp() {
            TestEnv.RequireSiril();
            dir = TestEnv.NewTempDirectory("parity");
        }

        [TearDown]
        public void TearDown() => TestEnv.Delete(dir);

        private static readonly string[] Templates = {
            SirilPathTemplate.MasterDarkFileName,
            "obs_$DATE-OBS:dm12$_loc_$DATE-LOC:dm12$",
            "e_$EXPTIME:%.1f$_$EXPTIME:%03d$_$EXPTIME:%+d$_$EXPTIME:%.2f$",
            "o_$OBJECT:%s$",
        };

        [Test]
        public async Task Resolve_MatchesSirilParse_ForEveryHeader() {
            var headers = new (string Name, FitsWriter Header)[] {
                ("nina", Header(20.0, 252, 50, 0.0, 2, "2026-10-03T13:00:00.0000000", "2026-10-03T21:00:00.0000000", "NGC 253")),
                ("cold", Header(19.99, 252, 50, -10.0, 2, "2026-10-03T11:30:00.0000000", "2026-10-03T19:30:00.0000000", "Thor's Helmet")),
                ("short", Header(0.125, 100, 8, -0.6, 1, "2026-10-03T16:00:00.0000000", "2026-10-04T00:00:00.0000000", "M42: Orion?")),
                ("tie", Header(0.375, 0, 0, -15.5, 4, "2026-10-04T03:59:59.0000000", "2026-10-04T11:59:59.0000000", "C/2020 F3")),
            };
            var script = new StringBuilder("requires 1.3.4\n");
            var expected = new List<string>();
            foreach (var (name, header) in headers) {
                var path = Path.Combine(dir, name + ".fit");
                header.Write(path, new ushort[16], 4, 4);
                var parsed = FitsFile.ReadHeader(path);
                script.Append("load ").Append(SirilQuote.Word(path)).Append('\n');
                foreach (var template in Templates) {
                    script.Append("parse ").Append(SirilQuote.Word(template)).Append(" -r\n");
                    expected.Add(SirilPathTemplate.Resolve(template, parsed));
                }
            }
            script.Append("close\n");
            var scriptPath = Path.Combine(dir, "parity.ssf");
            File.WriteAllText(scriptPath, script.ToString());

            var run = await TestEnv.Runner(dir).RunAsync(scriptPath, dir);
            TestEnv.Print(run);

            run.Succeeded.Should().BeTrue();
            var sirilOut = run.LogLines.Where(l => l.StartsWith("log: String out: ", StringComparison.Ordinal))
                .Select(l => l.Substring("log: String out: ".Length)).ToList();
            for (var i = 0; i < expected.Count; i++) {
                TestContext.Progress.WriteLine($"siril {sirilOut.ElementAtOrDefault(i)}  |  c# {expected[i]}");
            }
            sirilOut.Should().Equal(expected);
        }

        [Test]
        public async Task MissingKey_FailsInBoth() {
            var path = Path.Combine(dir, "nokey.fit");
            new FitsWriter().Add("EXPTIME", 20.0).Write(path, new ushort[16], 4, 4);
            var scriptPath = Path.Combine(dir, "nokey.ssf");
            File.WriteAllText(scriptPath, $"requires 1.3.4\nload {SirilQuote.Word(path)}\nparse {SirilQuote.Word(SirilPathTemplate.MasterDarkFileName)} -r\nclose\n");

            var run = await TestEnv.Runner(dir).RunAsync(scriptPath, dir);

            run.LogLines.Should().Contain(l => l.Contains("Key not found: GAIN"));
            run.LogLines.Should().Contain("log: String out: (null)");
            FluentActions.Invoking(() => SirilPathTemplate.Resolve(SirilPathTemplate.MasterDarkFileName, FitsFile.ReadHeader(path)))
                .Should().Throw<SirilPathTemplateException>().Where(e => e.Key == "GAIN");
        }

        private static FitsWriter Header(double exptime, int gain, int offset, double setTemp, int bin, string dateObs, string dateLoc, string target) {
            return new FitsWriter()
                .Add("IMAGETYP", "LIGHT")
                .Add("EXPTIME", exptime)
                .Add("DATE-LOC", dateLoc)
                .Add("DATE-OBS", dateObs)
                .Add("XBINNING", bin)
                .Add("GAIN", gain)
                .Add("OFFSET", offset)
                .Add("SET-TEMP", setTemp)
                .Add("OBJECT", target)
                .Add("ROWORDER", "TOP-DOWN");
        }
    }
}
