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
using System.Linq;
using System.Text.Json;

namespace NINA.Mac.App.Test.Platform {

    /// <summary>What CoreGraphics says about the displays, read by a separate python3 process (ctypes), as an oracle.</summary>
    public sealed record DisplayOracle(uint[] Active, DisplayInfo[] Online) {
        private const string Script = @"
import ctypes, json
cg = ctypes.CDLL('/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics')
ids = (ctypes.c_uint32 * 32)(); n = ctypes.c_uint32()
assert cg.CGGetActiveDisplayList(32, ids, ctypes.byref(n)) == 0
active = list(ids)[:n.value]
assert cg.CGGetOnlineDisplayList(32, ids, ctypes.byref(n)) == 0
online = [[d, cg.CGDisplayIsMain(d), cg.CGDisplayIsBuiltin(d), cg.CGDisplayIsActive(d), cg.CGDisplayIsAsleep(d)] for d in list(ids)[:n.value]]
print(json.dumps({'active': active, 'online': online}))
";

        public static DisplayOracle Read() {
            var result = ChildProcess.Run("/usr/bin/python3", new[] { "-c", Script });
            result.ExitCode.Should().Be(0, result.Output);
            using var json = JsonDocument.Parse(result.StdOut);
            var active = json.RootElement.GetProperty("active").EnumerateArray().Select(e => e.GetUInt32()).ToArray();
            var online = json.RootElement.GetProperty("online").EnumerateArray().Select(e => e.EnumerateArray().Select(v => v.GetUInt32()).ToArray())
                .Select(v => new DisplayInfo(v[0], v[1] != 0, v[2] != 0, v[3] != 0, v[4] != 0)).ToArray();
            return new DisplayOracle(active, online);
        }

        public bool AnyActive => Active.Length > 0;
    }

    [TestFixture]
    public class DisplaysTests {

        /// <summary>
        /// MacDisplays (which the startup checks use to explain a GUI that cannot start) agrees with CoreGraphics read
        /// independently. On this M1 Air: one built-in display, active while awake, asleep and inactive otherwise.
        /// </summary>
        [Test]
        public void MacDisplays_AgreeWithCoreGraphicsReadFromPython() {
            var oracle = DisplayOracle.Read();
            TestContext.Out.WriteLine($"active [{string.Join(", ", oracle.Active)}]; online {string.Join("; ", oracle.Online.Select(d => d.ToString()))}");
            MacDisplays.ActiveCount().Should().Be(oracle.Active.Length);
            MacDisplays.Online().Should().BeEquivalentTo(oracle.Online, o => o.WithStrictOrdering());
            var reason = MacDisplays.NoActiveDisplayReason();
            if (oracle.AnyActive) {
                reason.Should().BeNull();
            } else {
                reason.Should().StartWith("no active display (").And.Contain("Wake the display");
                if (oracle.Online.Any(d => d.IsAsleep)) {
                    reason.Should().Contain("asleep");
                }
            }
        }
    }
}
