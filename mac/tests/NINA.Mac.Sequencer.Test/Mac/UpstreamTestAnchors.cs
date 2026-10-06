#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// Namespace anchors for unused using lines in linked upstream NINA.Test fixtures: the namespaces live in assemblies the mac
// build does not have (the NINA app, MdXaml, the Windows planetarium drivers). Each anchor declares no types, so a real use of
// those namespaces would still fail to compile.

// Trigger/Guider/DitherAfterExposuresTest.cs, SequenceItem/Imaging/TakeExposureTest.cs, TakeManyExposuresTest.cs,
// Serialization/SequenceJsonConverterTest.cs: using NINA.ViewModel.ImageHistory (the NINA app's image history panel)
namespace NINA.ViewModel.ImageHistory {
    internal static class MacNamespaceAnchor { }
}

// Serialization/SequenceJsonConverterTest.cs: using NINA.ViewModel.FramingAssistant (NINA app)
namespace NINA.ViewModel.FramingAssistant {
    internal static class MacNamespaceAnchor { }
}

// Serialization/SequenceJsonConverterTest.cs: using NINA.Equipment.Equipment.MyPlanetarium (not in NINA.Equipment.Mac)
namespace NINA.Equipment.Equipment.MyPlanetarium {
    internal static class MacNamespaceAnchor { }
}

// Logic/ExpressionTest.cs: using MdXaml.Plugins (the WPF markdown viewer)
namespace MdXaml.Plugins {
    internal static class MacNamespaceAnchor { }
}
