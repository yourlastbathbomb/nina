#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Avalonia.Controls;
using NINA.Mac.App.ViewModels;
using System;

namespace NINA.Mac.App.Views {

    public partial class MainWindow : Window {
        private MainWindowViewModel subscribed;

        public MainWindow() {
            InitializeComponent();
        }

        /// <summary>The About box most recently opened (tests inspect it).</summary>
        public AboutWindow LastAboutWindow { get; private set; }

        protected override void OnDataContextChanged(EventArgs e) {
            base.OnDataContextChanged(e);
            if (subscribed != null) {
                subscribed.AboutRequested -= OnAboutRequested;
            }
            subscribed = DataContext as MainWindowViewModel;
            if (subscribed != null) {
                subscribed.AboutRequested += OnAboutRequested;
            }
        }

        private void OnAboutRequested(object sender, EventArgs e) {
            LastAboutWindow = new AboutWindow { DataContext = subscribed.About };
            LastAboutWindow.Show(this);
        }
    }
}
