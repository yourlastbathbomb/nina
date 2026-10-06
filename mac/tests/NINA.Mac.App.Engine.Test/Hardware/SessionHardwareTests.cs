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
using NINA.Mac.App.Engine.Test.Fakes;
using NINA.Mac.App.Services;
using NINA.Mac.Siril;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ctl = ZWOptical.ASISDK.ASICameraDll.ASI_CONTROL_TYPE;

namespace NINA.Mac.App.Engine.Test.Hardware {

    /// <summary>
    /// A short real run: the app's EngineSessionService turns a Target-form plan into NINA's sequence and NINA's Sequencer runs
    /// it with the REAL camera and the simulated LX200 (DirectGuider dithers as pulses on the simulator; NINA's Center with the
    /// fake solver, which solves the real solve frame at where the simulated mount points). The lights must land in
    /// NINA.Mac.Siril's layout with the right headers, and siril-cli 1.4.4 must convert (debayer from the header) and stack them.
    /// The cooler is never switched on; it is switched off and read back on exit anyway.
    /// </summary>
    [TestFixture]
    [Category("Hardware")]
    [NonParallelizable]
    public class SessionHardwareTests {
        private const string SirilCli = "/Applications/Siril.app/Contents/MacOS/siril-cli";

        private static void Log(string line) => HardwareRig.Log(line);

        [Test]
        [Explicit("Needs the ZWO ASI585MC Pro on USB and siril-cli 1.4.4; run by hand (about 1 minute)")]
        public async Task Run_SixBin2Lights_DitherEvery2_LandInTheSirilLayout_AndSirilStacksThem() {
            var id = Asi.RequireCamera();
            if (!File.Exists(SirilCli)) {
                Assert.Fail($"{SirilCli} is not installed; the Siril check is part of this test");
            }
            await using var rig = new HardwareRig("hw session");
            rig.StartTicker();
            await rig.Engine.Camera.ConnectAsync();
            await rig.Engine.Mount.ConnectAsync();
            rig.Engine.Camera.State.Should().Be(DeviceConnectionState.Connected);
            rig.Engine.Mount.State.Should().Be(DeviceConnectionState.Connected);
            await Task.Delay(3500);

            const string name = "HW run NGC 253";
            var target = Sky.At(45, 160);
            var s = rig.Settings;
            var plan = new SessionPlan(name, target.RA, target.Dec, 2, 6, s.Gain, s.Offset, 2, 2, s.MaxAltitudeDegrees, s.MinAltitudeDegrees, true);
            var session = rig.Engine.Session;
            var sw = Stopwatch.StartNew();
            await session.RunAsync(plan);
            var runSeconds = sw.Elapsed.TotalSeconds;

            Log("session log:\n  " + string.Join("\n  ", session.Log));
            Log($"run {runSeconds:0.0} s: state {session.State}, stop reason '{session.Progress.StopReason}', frames {session.Progress.FramesDone}/{session.Progress.FrameCount}, last HFR {session.Progress.LastHfr}, solver calls {rig.Solver.Calls}");
            session.State.Should().Be(SessionState.Finished);
            session.Progress.StopReason.Should().Be("All frames taken");
            session.Progress.FramesDone.Should().Be(6);

            var lightsDir = session.Layout.LightsDirectory(name);
            var lights = Directory.GetFiles(lightsDir, "*.fits").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Log($"lights in {lightsDir}:\n  {string.Join("\n  ", lights.Select(Path.GetFileName))}");
            lights.Should().HaveCount(6);
            Path.GetRelativePath(rig.ImagesRoot, lightsDir).Split('/').Should().HaveCount(3).And.EndWith("lights");
            foreach (var light in lights) {
                CameraServiceHardwareTests.CheckColourLight(light, 2, s.Gain, s.Offset, name);
            }
            Directory.GetFiles(rig.ImagesRoot, "*.fits", SearchOption.AllDirectories).Should().HaveCount(6, "solve frames are not saved; only the lights");

            // Mount side: NINA's Center synced once, DirectGuider dithered (pulse-guide commands on the simulator)
            var pulses = rig.Received.Where(c => c.StartsWith(":Mg", StringComparison.Ordinal)).ToList();
            var syncs = rig.Received.Count(c => c.StartsWith(":CM", StringComparison.Ordinal));
            Log($"simulator: {pulses.Count} pulse commands ({string.Join(" ", pulses)}), {syncs} sync(s)");
            pulses.Should().NotBeEmpty("dither every 2 of 6 lights");
            syncs.Should().Be(1);
            Asi.Read(id, Ctl.ASI_MONO_BIN).Should().Be(0);
            Asi.Read(id, Ctl.ASI_COOLER_ON).Should().Be(0, "the generated night never touches the cooler");

            // Siril consumes the layout: convert (debayer from the header) and stack the six lights into one master
            var targetDir = Path.GetDirectoryName(lightsDir)!;
            var ini = Path.Combine(rig.Folder, "siril-cli.ini");
            File.WriteAllText(ini, string.Empty);
            var script = Path.Combine(rig.Folder, "hwcheck.ssf");
            File.WriteAllText(script, string.Join("\n",
                "requires 1.4.0",
                "setext fit",
                "set32bits",
                "setcompress 0",
                "cd lights",
                "convert light -debayer -out=../hwcheck",
                "cd ../hwcheck",
                "stack light rej 3 3 -nonorm -out=../hw_master",
                ""));
            var (exit, output) = RunSiril(rig.Folder, "-o", "-i", ini, "-d", targetDir, "-s", script);
            var interesting = output.Split('\n').Where(l => l.Contains("siril 1.") || l.Contains("Pattern") || l.Contains("Reading FITS") || l.Contains("stack", StringComparison.OrdinalIgnoreCase)
                || l.Contains("images", StringComparison.OrdinalIgnoreCase) || l.Contains("Saving FITS") || l.Contains("rror") || l.Contains("Script")).ToList();
            Log($"siril-cli exit {exit}:\n  {string.Join("\n  ", interesting)}");
            exit.Should().Be(0, output);
            output.Should().Contain("Script execution finished successfully");
            output.Should().Contain("Welcome to siril 1.4.4");
            output.Should().Contain("RGGB from header");
            var master = Path.Combine(targetDir, "hw_master.fit");
            File.Exists(master).Should().BeTrue(output);
            var h = FitsFile.ReadHeader(master);
            Log($"master: NAXIS {h.GetInt("NAXIS")} {h.GetInt("NAXIS1")}x{h.GetInt("NAXIS2")}x{h.GetInt("NAXIS3")}, BITPIX {h.GetInt("BITPIX")}, STACKCNT {h.GetString("STACKCNT")}, LIVETIME {h.GetString("LIVETIME")}, BAYERPAT {h.GetString("BAYERPAT") ?? "-"}");
            h.GetInt("NAXIS1").Should().Be(1920);
            h.GetInt("NAXIS2").Should().Be(1080);
            h.GetInt("NAXIS3").Should().Be(3, "debayered to RGB");
            h.GetInt("STACKCNT").Should().Be(6);
            rig.HealthWarnings.Should().BeEmpty();
        }

        private static (int ExitCode, string Output) RunSiril(string workingDirectory, params string[] arguments) {
            var start = new ProcessStartInfo(SirilCli) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            };
            foreach (var a in arguments) {
                start.ArgumentList.Add(a);
            }
            start.Environment["LC_ALL"] = "C";
            start.Environment["LANG"] = "C";
            start.Environment["LANGUAGE"] = "C";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180_000)) {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("siril-cli did not finish within 3 minutes");
            }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
    }
}
