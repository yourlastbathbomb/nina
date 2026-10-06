#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Mac.App.Engine;
using NINA.Mac.App.Services;
using NINA.Mac.Equipment.Lx200;
using NINA.Mac.Native;
using NINA.Mac.Platform;
using NINA.Mac.Sequencing.Runner;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Mac.App.Diagnostics {

    /// <summary>
    /// The smoke test's engine check: composes the Real device services (<see cref="EngineDevices"/>) in a temporary data folder
    /// exactly as the app does, without opening anything: the camera list is an empty test list (the real one would scan USB)
    /// and the serial opener refuses. It proves the engine assemblies, NINA's data (ephemeris, catalogue scripts, through the
    /// bundle's symlinks) and the native libraries are in place, and that NINA's sequence generator builds the rig's tree.
    /// </summary>
    internal static class EngineSmoke {

        public static string Run() {
            var folder = Path.Combine(Path.GetTempPath(), $"{AppInfo.Current.ShortName}-smoke-engine-{Guid.NewGuid():N}");
            var images = Path.Combine(folder, "images");
            var settings = new AppSettings { DeviceSource = DeviceSource.Real, ImagesRoot = images };
            var cameras = new NoCameras();
            try {
                using var engine = EngineDevices.Create(new EngineDevicesOptions {
                    Settings = () => settings,
                    ImagesRoot = () => images,
                    DataDirectory = Path.Combine(folder, "Engine"),
                    CameraChooser = (_, _) => cameras,
                    LinkPool = new Lx200LinkPool(_ => _ => throw new IOException("smoke test: no serial port is opened")),
                    SerialPortLister = () => Array.Empty<SerialPortInfo>(),
                    CheckEngineData = true,
                });
                var natives = new[] { "ASICamera2.dll", "SOFA_2023_10_11.dll", "NOVAS31lib.dll" }
                    .Select(n => NativeLibraries.Locate(n) ?? throw new DllNotFoundException($"{NativeLibraries.DylibName(n)} not found in {string.Join(", ", NativeLibraries.SearchDirectories())}"))
                    .ToList();
                var night = engine.Session.ToNightPlan(new SessionPlan("NGC 253", 0.7928, -25.288, 30, 10, 252, 8, 2, 5, 75, 20, true));
                var root = engine.Host.Generator.Generate(night);
                var entities = 0;
                HeadlessSequenceRunner.Walk(root, _ => entities++);
                var unknown = new HeadlessSequenceRunner(root).UnknownEntities();
                if (unknown.Count > 0) {
                    throw new InvalidOperationException($"{unknown.Count} sequence entities did not resolve");
                }
                if (cameras.Scans != 0) {
                    throw new InvalidOperationException("composing the engine scanned the camera list");
                }
                if (engine.Mount.State != DeviceConnectionState.Disconnected || engine.Camera.State != DeviceConnectionState.Disconnected) {
                    throw new InvalidOperationException("a device connected on its own");
                }
                var dataFolder = EngineRuntime.DataDirectory;
                return $"NINA data and ephemeris OK, rig profile written, generated sequence of {entities} entities, " +
                    $"natives {string.Join(", ", natives.Select(Path.GetFileName))} in {Path.GetDirectoryName(natives[0])}; nothing opened (data folder {dataFolder})";
            } finally {
                try {
                    Directory.Delete(folder, recursive: true);
                } catch (IOException) {
                    // NINA's log file may still be open; it is in the temporary folder
                } catch (UnauthorizedAccessException) {
                }
            }
        }

        private sealed class NoCameras : IDeviceChooserVM {
            public int Scans { get; private set; }

            public IDevice SelectedDevice { get; set; }

            public bool SetupDialogOpen => false;

            public IList<IDevice> Devices { get; } = new List<IDevice>();

            public Task GetEquipment() {
                Scans++;
                return Task.CompletedTask;
            }
        }
    }
}
