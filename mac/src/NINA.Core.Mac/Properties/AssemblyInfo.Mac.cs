#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Reflection;
using System.Runtime.InteropServices;

// Replaces upstream NINA.Core/Properties/AssemblyInfo.cs, whose only other content is the WPF ThemeInfo attribute.
[assembly: AssemblyTitle("N.I.N.A. Core Library")]
[assembly: AssemblyDescription("This assembly contains the core components of N.I.N.A.")]
[assembly: AssemblyConfiguration("")]
[assembly: ComVisible(false)]
