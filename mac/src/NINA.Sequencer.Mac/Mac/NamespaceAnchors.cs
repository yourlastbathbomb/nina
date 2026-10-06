#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// Namespace anchors. Some upstream NINA.Sequencer files import namespaces that the headless build does not contain, either
// because their folders are left out on purpose (autofocus, focuser items, the meridian-flip triggers) or because the
// imported package or vendor assembly is not referenced (ASCOM, Nikon, NINA.Equipment's SkyGuard guider). The imports are
// unused: each anchor declares an empty internal class and no types, so any real use of those namespaces would still fail
// to compile. SequencerParityTest fails as soon as an anchor is no longer needed. Upstream patch P1 (deleting the unused
// using lines, mac/docs/m7-headless-sequencer-plan.md section 6.7) would make this file unnecessary.

// Sequencer.cs: using NINA.Sequencer.SequenceItem.Autofocus; .SequenceItem.Focuser; .Trigger.MeridianFlip
namespace NINA.Sequencer.SequenceItem.Autofocus {
    internal static class MacNamespaceAnchor { }
}

namespace NINA.Sequencer.SequenceItem.Focuser {
    internal static class MacNamespaceAnchor { }
}

namespace NINA.Sequencer.Trigger.MeridianFlip {
    internal static class MacNamespaceAnchor { }
}

// Trigger/Guider/DitherAfterExposures.cs, Trigger/Platesolving/CenterAfterDriftTrigger.cs: using ASCOM.Com.DriverAccess
namespace ASCOM.Com.DriverAccess {
    internal static class MacNamespaceAnchor { }
}

// SequenceItem/Utility/WaitForSunAltitude.cs: using Nikon (nikoncswrapper)
namespace Nikon {
    internal static class MacNamespaceAnchor { }
}

// Conditions/MoonIlluminationCondition.cs: using NINA.Equipment.Equipment.MyGuider.SkyGuard.SkyGuardMessages (the SkyGuard
// guider is not in NINA.Equipment.Mac)
namespace NINA.Equipment.Equipment.MyGuider.SkyGuard.SkyGuardMessages {
    internal static class MacNamespaceAnchor { }
}

// Logic/SymbolBroker.cs: using System.Windows.Documents (WPF flow documents)
namespace System.Windows.Documents {
    internal static class MacNamespaceAnchor { }
}
