#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry.Interfaces;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.Logic;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace NINA.Mac.Sequencing.Catalogue {

    /// <summary>
    /// Every service a rig sequence entity's constructor asks for: the parameters of the [ImportingConstructor]s that
    /// NINA.Plugin's PluginLoader composes through MEF on Windows (PluginLoader.cs, ComposeExportedValue). The headless host
    /// fills one; tests may fill one with mocks.
    /// </summary>
    public sealed class SequencerServices {
        public IProfileService ProfileService { get; init; }
        public ICameraMediator Camera { get; init; }
        public ITelescopeMediator Telescope { get; init; }
        public IGuiderMediator Guider { get; init; }
        public IFocuserMediator Focuser { get; init; }
        public IFilterWheelMediator FilterWheel { get; init; }
        public IRotatorMediator Rotator { get; init; }
        public IDomeMediator Dome { get; init; }
        public IDomeFollower DomeFollower { get; init; }
        public IFlatDeviceMediator FlatDevice { get; init; }
        public ISafetyMonitorMediator SafetyMonitor { get; init; }
        public ISwitchMediator Switch { get; init; }
        public IWeatherDataMediator WeatherData { get; init; }
        public IImagingMediator Imaging { get; init; }
        public IImageSaveMediator ImageSave { get; init; }
        public IImageHistoryVM ImageHistory { get; init; }
        public IApplicationStatusMediator ApplicationStatus { get; init; }
        public IApplicationMediator Application { get; init; }
        public ISequenceMediator Sequence { get; init; }
        public ISymbolBroker SymbolBroker { get; init; }
        public INighttimeCalculator NighttimeCalculator { get; init; }
        public IFramingAssistantVM FramingAssistant { get; init; }
        public IPlanetariumFactory PlanetariumFactory { get; init; }
        public IPlateSolverFactory PlateSolverFactory { get; init; }
        public IWindowServiceFactory WindowServiceFactory { get; init; }
    }
}
