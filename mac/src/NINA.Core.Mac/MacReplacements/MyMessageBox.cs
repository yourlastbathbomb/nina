#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Interfaces;
using System;
using System.Windows;

namespace NINA.Core.MyMessageBox {

    /// <summary>
    /// macOS replacement for upstream MyMessageBox/MyMessageBox.cs (a modal WPF dialog). Same static Show API.
    /// The answer comes from <see cref="Host"/>, which a macOS UI (or a headless runner with a policy) installs.
    /// Without a host Show throws PlatformNotSupportedException: there is nobody to answer, and guessing an answer
    /// would silently change what the calling code does.
    /// </summary>
    public class MyMessageBox {

        /// <summary>mac only: answers message boxes. Must not be a <see cref="MyMessageBoxVM"/>, which calls back into Show.</summary>
        public static IMyMessageBoxVM Host { get; set; }

        public static MessageBoxResult Show(string messageBoxText) {
            return Show(messageBoxText, "", MessageBoxButton.OK, MessageBoxResult.OK);
        }

        public static MessageBoxResult Show(string messageBoxText, string caption) {
            return Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxResult.OK);
        }

        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxResult defaultresult) {
            var host = Host;
            if (host == null) {
                throw new PlatformNotSupportedException($"No message box host is installed to answer \"{caption}\": {messageBoxText}");
            }
            if (host is MyMessageBoxVM) {
                throw new InvalidOperationException("MyMessageBox.Host must not be a MyMessageBoxVM; it forwards back to MyMessageBox.Show.");
            }
            return host.Show(messageBoxText, caption, button, defaultresult);
        }
    }
}
