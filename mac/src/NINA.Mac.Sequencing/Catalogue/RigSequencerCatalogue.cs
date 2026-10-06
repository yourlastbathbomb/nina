#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Locale;
using NINA.Core.Utility;
using NINA.Mac.Sequencing.Conditions;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Camera;
using NINA.Sequencer.SequenceItem.Expressions;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Connect;
using NINA.Sequencer.Trigger.Guider;
using NINA.Sequencer.Trigger.Platesolving;
using NINA.Sequencer.Utility.DateTimeProvider;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;

namespace NINA.Mac.Sequencing.Catalogue {

    /// <summary>
    /// The sequence entities this rig can use, built by direct constructor calls: the static replacement for the plugin loader's
    /// MEF composition (NINA.Plugin/PluginLoader.cs) and its WPF resource dictionaries. An upstream constructor change therefore
    /// breaks this build instead of failing at run time.
    /// <para>
    /// As PluginLoader does for each export: the entity goes into the list of every [Export(typeof(...))] it carries, one instance
    /// per export (containers such as SequentialContainer are both an item and a container); Name, Description and Category
    /// come from its [ExportMetadata] (Lbl_ keys translated through Loc, other text kept); the icon stays null (no XAML
    /// resources); the entity gets the symbol broker; each list is sorted by Category + Name. The ten date-time providers are
    /// PluginLoader's, in its order (TimeCondition, WaitForTime and ResetVariableToDate pick theirs from this list, and the JSON
    /// converter matches saved providers against it by type).
    /// </para>
    /// <para>
    /// Left out on purpose: the meridian-flip triggers (not even compiled: an alt-az mount never flips), and entities that are
    /// compiled but not offered (<see cref="CompiledButNotCatalogued"/>). A saved sequence that uses one loads it as an Unknown
    /// placeholder, which validation reports and the run skips.
    /// </para>
    /// </summary>
    public sealed class RigSequencerCatalogue {

        /// <summary>Exported NINA.Sequencer types that the build compiles but the catalogue does not offer, with the reason.</summary>
        public static readonly IReadOnlyDictionary<Type, string> CompiledButNotCatalogued = new Dictionary<Type, string> {
            [typeof(NINA.Sequencer.SequenceItem.FlatDevice.AutoExposureFlat)] = "calibration screen (M9): flats are taken with the LED panel's own workflow",
            [typeof(NINA.Sequencer.SequenceItem.FlatDevice.CloseCover)] = "no flat device (part of AutoExposureFlat)",
            [typeof(NINA.Sequencer.SequenceItem.FlatDevice.OpenCover)] = "no flat device (part of AutoExposureFlat)",
            [typeof(NINA.Sequencer.SequenceItem.FlatDevice.SetBrightness)] = "no flat device (part of AutoExposureFlat)",
            [typeof(NINA.Sequencer.SequenceItem.FlatDevice.ToggleLight)] = "no flat device (part of AutoExposureFlat)",
            [typeof(NINA.Sequencer.SequenceItem.Connect.ConnectAllEquipment)] = "equipment connection belongs to the app's connection service (M8)",
            [typeof(NINA.Sequencer.SequenceItem.Connect.ConnectEquipment)] = "equipment connection belongs to the app's connection service (M8)",
            [typeof(NINA.Sequencer.SequenceItem.Connect.DisconnectAllEquipment)] = "equipment connection belongs to the app's connection service (M8)",
            [typeof(NINA.Sequencer.SequenceItem.Connect.DisconnectEquipment)] = "equipment connection belongs to the app's connection service (M8)",
            [typeof(SetReadoutMode)] = "the ASI585MC has one readout mode",
            [typeof(LinkedTemplateContainer)] = "linked templates need a template link resolver and the editor's template controller",
        };

        private readonly SequencerServices services;

        public RigSequencerCatalogue(SequencerServices services) {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            var s = services;
            var nighttime = s.NighttimeCalculator;
            // PluginLoader.cs constructor, same order
            DateTimeProviders = new List<IDateTimeProvider> {
                new NINA.Sequencer.Utility.DateTimeProvider.TimeProvider(nighttime),
                new SunsetProvider(nighttime),
                new CivilDuskProvider(nighttime),
                new NauticalDuskProvider(nighttime),
                new DuskProvider(nighttime),
                new DawnProvider(nighttime),
                new NauticalDawnProvider(nighttime),
                new CivilDawnProvider(nighttime),
                new SunriseProvider(nighttime),
                new MeridianProvider(s.ProfileService)
            };

            // Containers
            Add(() => new SequenceRootContainer());
            Add(() => new StartAreaContainer());
            Add(() => new TargetAreaContainer());
            Add(() => new EndAreaContainer());
            Add(() => new SequentialContainer());
            Add(() => new ParallelContainer());
            Add(() => new ConditionalContainer());
            Add(() => new DeepSkyObjectContainer(s.ProfileService, s.NighttimeCalculator, s.FramingAssistant, s.Application, s.PlanetariumFactory, s.Camera, s.FilterWheel, s.SymbolBroker));

            // Mount and plate solving
            Add(() => new SlewScopeToRaDec(s.Telescope, s.Guider));
            Add(() => new SlewScopeToAltAz(s.ProfileService, s.Telescope, s.Guider));
            Add(() => new ParkScope(s.Telescope, s.Guider));
            Add(() => new UnparkScope(s.Telescope));
            Add(() => new SetTracking(s.Telescope));
            Add(() => new Center(s.ProfileService, s.Telescope, s.Imaging, s.FilterWheel, s.Guider, s.Dome, s.DomeFollower, s.PlateSolverFactory, s.WindowServiceFactory));
            Add(() => new SolveAndSync(s.ProfileService, s.Telescope, s.Rotator, s.Imaging, s.FilterWheel, s.PlateSolverFactory, s.WindowServiceFactory));

            // Imaging. SwitchFilter is offered although the rig has no filter wheel: SmartExposure contains one, and NINA's own
            // templates and saved sequences carry it; without a prototype it would load as Unknown. It does nothing while no
            // filter wheel is connected
            Add(() => new NINA.Sequencer.SequenceItem.FilterWheel.SwitchFilter(s.ProfileService, s.FilterWheel));
            Add(() => new TakeExposure(s.ProfileService, s.Camera, s.Imaging, s.ImageSave, s.ImageHistory));
            Add(() => new TakeManyExposures(s.ProfileService, s.Camera, s.Imaging, s.ImageSave, s.ImageHistory));
            Add(() => new SmartExposure(s.ProfileService, s.Camera, s.Imaging, s.ImageSave, s.ImageHistory, s.FilterWheel, s.Guider, s.SafetyMonitor));

            // Camera
            Add(() => new CoolCamera(s.Camera));
            Add(() => new WarmCamera(s.Camera));
            Add(() => new DewHeater(s.Camera));
            Add(() => new SetUSBLimit(s.Camera));

            // Guider: the mount dither (DirectGuider)
            Add(() => new Dither(s.Guider, s.ProfileService));

            // Utility
            Add(() => new WaitForTime(DateTimeProviders));
            Add(() => new WaitForTimeSpan());
            Add(() => new WaitForAltitude(s.ProfileService));
            Add(() => new WaitUntilAboveHorizon(s.ProfileService));
            Add(() => new WaitForSunAltitude(s.ProfileService));
            Add(() => new WaitForMoonAltitude(s.ProfileService));
            Add(() => new WaitUntil(s.SafetyMonitor, s.Sequence, s.ProfileService));
            Add(() => new Annotation());
            Add(() => new ExternalScript(s.SymbolBroker));

            // Expressions (upstream does not export Constant: its [Export] is commented out, so NINA never offers it either)
            Add(() => new Variable());
            Add(() => new GlobalConstant());
            Add(() => new GlobalVariable());
            Add(() => new ResetVariable());
            Add(() => new ResetVariableToDate(DateTimeProviders));

            // Conditions
            Add(() => new LoopCondition());
            Add(() => new TimeCondition(DateTimeProviders));
            Add(() => new TimeSpanCondition());
            Add(() => new AltitudeCondition(s.ProfileService));
            Add(() => new AboveHorizonCondition(s.ProfileService));
            Add(() => new SunAltitudeCondition(s.ProfileService));
            Add(() => new MoonAltitudeCondition(s.ProfileService));
            Add(() => new MoonIlluminationCondition());
            Add(() => new LoopWhile());
            Add(() => new MaxAltitudeCondition(s.ProfileService));

            // Triggers
            Add(() => new DitherAfterExposures(s.Guider, s.ImageHistory, s.ProfileService, s.SafetyMonitor));
            Add(() => new CenterAfterDriftTrigger(s.ProfileService, s.Telescope, s.FilterWheel, s.Guider, s.Imaging, s.Camera, s.Dome, s.DomeFollower, s.ImageSave, s.ApplicationStatus, s.SafetyMonitor) {
                // Fork seam (upstream edit in CenterAfterDriftTrigger.cs): its centring run uses the host's factories instead of
                // the static solver factory proxy and NINA.Core's WindowServiceFactory, whose Show throws on macOS
                CenteringPlateSolverFactory = s.PlateSolverFactory,
                CenteringWindowServiceFactory = s.WindowServiceFactory
            });
            Add(() => new ReconnectTrigger(s.ProfileService, s.Camera, s.FilterWheel, s.Focuser, s.Rotator, s.Telescope, s.Guider, s.Switch, s.FlatDevice, s.WeatherData, s.Dome, s.SafetyMonitor));
            Add(() => new ReconnectOnDownloadFailure(s.ProfileService, s.Camera, s.Sequence));

            Sort(Items);
            Sort(Conditions);
            Sort(Triggers);
            Sort(Containers);
        }

        public IList<ISequenceItem> Items { get; } = new List<ISequenceItem>();
        public IList<ISequenceCondition> Conditions { get; } = new List<ISequenceCondition>();
        public IList<ISequenceTrigger> Triggers { get; } = new List<ISequenceTrigger>();
        public IList<ISequenceContainer> Containers { get; } = new List<ISequenceContainer>();
        public IList<IDateTimeProvider> DateTimeProviders { get; }

        /// <summary>Every catalogued type, once.</summary>
        public IReadOnlyCollection<Type> Types => Items.Cast<ISequenceEntity>().Concat(Conditions).Concat(Triggers).Concat(Containers)
            .Select(e => e.GetType()).Distinct().ToList();

        private void Add(Func<ISequenceEntity> create) {
            var probe = create();
            var exports = probe.GetType().GetCustomAttributes(typeof(ExportAttribute), false).Cast<ExportAttribute>().Select(e => e.ContractType).ToList();
            if (exports.Count == 0) {
                throw new InvalidOperationException($"{probe.GetType().FullName} has no [Export] attribute, so it is not a sequence entity NINA would load");
            }
            var first = true;
            foreach (var contract in exports) {
                // One instance per export, as MEF creates one part per export
                var entity = first ? probe : create();
                first = false;
                AssignMetadata(entity);
                if (contract == typeof(ISequenceItem)) {
                    Items.Add((ISequenceItem)entity);
                } else if (contract == typeof(ISequenceCondition)) {
                    Conditions.Add((ISequenceCondition)entity);
                } else if (contract == typeof(ISequenceTrigger)) {
                    Triggers.Add((ISequenceTrigger)entity);
                } else if (contract == typeof(ISequenceContainer)) {
                    Containers.Add((ISequenceContainer)entity);
                } else {
                    throw new InvalidOperationException($"{entity.GetType().FullName} exports the unknown contract {contract}");
                }
            }
        }

        /// <summary>PluginLoader.AssignSequenceEntity for a core entity (no plugin name): metadata, no icon, the symbol broker.</summary>
        private void AssignMetadata(ISequenceEntity entity) {
            var metadata = entity.GetType().GetCustomAttributes(typeof(ExportMetadataAttribute), false).Cast<ExportMetadataAttribute>().ToList();
            string Value(string name) => metadata.FirstOrDefault(m => m.Name == name)?.Value?.ToString();
            var name = Value("Name");
            if (name != null) {
                entity.Name = GrabLabel(name);
            }
            var description = Value("Description");
            if (description != null) {
                entity.Description = GrabLabel(description);
            }
            var category = Value("Category");
            if (category != null) {
                entity.Category = GrabLabel(category);
            }
            entity.SymbolBroker = services.SymbolBroker;
        }

        private static string GrabLabel(string label) {
            return label.StartsWith("Lbl_", StringComparison.Ordinal) ? Loc.Instance[label] : label;
        }

        private static void Sort<T>(IList<T> list) where T : ISequenceEntity {
            var sorted = list.OrderBy(item => item.Category + item.Name).ToList();
            list.Clear();
            foreach (var item in sorted) {
                list.Add(item);
            }
        }
    }
}
