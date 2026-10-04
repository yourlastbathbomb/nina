#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Globalization;

namespace System.Windows {

    /// <summary>
    /// WPF's numeric list separator (MS.Internal.TokenizerHelper.GetNumericListSeparator), used by the ToString of the
    /// geometry value types: ',' unless the culture's decimal separator starts with ',', then ';'.
    /// </summary>
    internal static class NumericList {

        public static char Separator(IFormatProvider provider) {
            var separator = ',';
            var numberFormat = NumberFormatInfo.GetInstance(provider);
            if (numberFormat.NumberDecimalSeparator.Length > 0 && separator == numberFormat.NumberDecimalSeparator[0]) {
                separator = ';';
            }
            return separator;
        }
    }
}
