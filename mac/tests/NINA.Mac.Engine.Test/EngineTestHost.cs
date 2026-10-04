#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NUnit.Framework;
using System;
using System.IO;
using System.Windows;

namespace NINA.Mac.Engine.Test {

    /// <summary>
    /// Process-wide host setup, as an app head would do it: point NINA's data folder at a temp dir before Logger or
    /// ProfileService initialise, and create the (single) System.Windows.Application on the test thread.
    /// </summary>
    [SetUpFixture]
    public class EngineTestHost {

        public static string DataRoot { get; private set; }

        [OneTimeSetUp]
        public void Start() {
            DataRoot = Path.Combine(Path.GetTempPath(), "nina-mac-engine-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataRoot);
            CoreUtil.APPLICATIONTEMPPATH = DataRoot;
            if (Application.Current == null) {
                _ = new Application();
            }
        }

        [OneTimeTearDown]
        public void Stop() {
            Logger.CloseAndFlush();
            try {
                Directory.Delete(DataRoot, true);
            } catch (IOException) {
            }
        }
    }
}
