#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace ASCOM {

    /// <summary>
    /// Mac-only, compile-time only. Upstream Solvers/TheSkyXImageLinkSolver.cs starts with an unused <c>using ASCOM;</c>, which
    /// resolves on Windows only because NINA.Equipment's ASCOM packages flow in transitively. NINA.Equipment.Mac drops those
    /// packages, so this empty internal type declares the namespace. Nothing can use it, so any real ASCOM type use in
    /// upstream code would still fail to compile. Delete this file when upstream drops the using (plan P3);
    /// PlatesolvingParityTest fails when that happens.
    /// </summary>
    internal static class MacNamespaceAnchor {
    }
}
