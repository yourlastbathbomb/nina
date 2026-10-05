#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows {

    /// <summary>
    /// WPF's assembly-level ThemeInfo attribute (PresentationFramework), so upstream Properties/AssemblyInfo.cs files compile
    /// unchanged. Only metadata: nothing looks up theme dictionaries on macOS.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class ThemeInfoAttribute : Attribute {

        public ThemeInfoAttribute(ResourceDictionaryLocation themeDictionaryLocation, ResourceDictionaryLocation genericDictionaryLocation) {
            ThemeDictionaryLocation = themeDictionaryLocation;
            GenericDictionaryLocation = genericDictionaryLocation;
        }

        public ResourceDictionaryLocation ThemeDictionaryLocation { get; }

        public ResourceDictionaryLocation GenericDictionaryLocation { get; }
    }

    /// <summary>Where WPF looks for theme and generic resource dictionaries (WPF values).</summary>
    public enum ResourceDictionaryLocation {
        None = 0,
        SourceAssembly = 1,
        ExternalAssembly = 2
    }
}
