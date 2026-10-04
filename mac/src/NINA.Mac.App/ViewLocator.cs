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
using Avalonia.Controls.Templates;
using NINA.Mac.App.ViewModels;
using NINA.Mac.App.Views;
using System;
using System.Collections.Generic;

namespace NINA.Mac.App {

    /// <summary>Maps page view-models to views with an explicit table (no reflection, trim-safe).</summary>
    public sealed class ViewLocator : IDataTemplate {

        private static readonly Dictionary<Type, Func<Control>> factories = new() {
            [typeof(ConnectViewModel)] = () => new ConnectView(),
            [typeof(CoolViewModel)] = () => new CoolView(),
            [typeof(FocusViewModel)] = () => new FocusView(),
            [typeof(TargetViewModel)] = () => new TargetView(),
            [typeof(RunViewModel)] = () => new RunView(),
            [typeof(CalibrateViewModel)] = () => new CalibrateView(),
            [typeof(TeardownViewModel)] = () => new TeardownView(),
            [typeof(SettingsViewModel)] = () => new SettingsView(),
            [typeof(AboutViewModel)] = () => new AboutView(),
        };

        public static IReadOnlyCollection<Type> KnownViewModels => factories.Keys;

        public Control Build(object param) {
            if (param != null && factories.TryGetValue(param.GetType(), out var factory)) {
                var view = factory();
                view.DataContext = param;
                return view;
            }
            return new TextBlock { Text = $"No view for {param?.GetType().Name ?? "null"}" };
        }

        public bool Match(object data) => data != null && factories.ContainsKey(data.GetType());
    }
}
