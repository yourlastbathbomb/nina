#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using System.Threading.Tasks;

namespace NINA.WPF.Base.Interfaces.ViewModel {

    /// <summary>
    /// macOS reduction of upstream NINA.WPF.Base/Interfaces/ViewModel/IFramingAssistantVM.cs. The full interface carries the
    /// framing assistant's sky-survey, cache and drawing members (SkySurvey, GDI+ image cache, WPF commands), none of which
    /// the headless engine compiles. The only member engine code calls is <see cref="SetCoordinates"/>
    /// (NINA.Sequencer/Container/DeepSkyObjectContainer.cs, its "open in framing assistant" command), kept with upstream's
    /// signature. Same name and namespace, so DeepSkyObjectContainer compiles unchanged.
    /// </summary>
    public interface IFramingAssistantVM {

        Task<bool> SetCoordinates(DeepSkyObject dso);
    }
}
