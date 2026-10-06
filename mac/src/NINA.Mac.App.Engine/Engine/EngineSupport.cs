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
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.ImageAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Mac.App.Engine {

    /// <summary>
    /// Raises the services' Changed events on the UI thread: the view-models update bound properties from them, which Avalonia
    /// allows only on the UI thread. Engine callbacks arrive on worker threads (the LX200 link, NINA's device update timers, the
    /// image save queue). Without a synchronization context (tests) the handler runs inline.
    /// </summary>
    internal sealed class UiPoster {
        private readonly SynchronizationContext context;

        public UiPoster(SynchronizationContext context) {
            this.context = context;
        }

        public void Post(Action action) {
            if (context == null || SynchronizationContext.Current == context) {
                action();
            } else {
                context.Post(_ => action(), null);
            }
        }
    }

    /// <summary>
    /// A device list that stays empty until the user connects. NINA's CameraVM rescans its chooser in its constructor (and on
    /// every profile change), and NINA.Equipment.Mac's CameraChooser opens each ZWO camera to read its name, so composing the
    /// engine would otherwise touch the USB camera at app start. The gate opens on Connect and closes on Disconnect. After a
    /// rescan the rig's one camera is preferred over the "No camera" entry and over an offline placeholder for a camera the
    /// profile remembered under another id.
    /// </summary>
    public sealed class GatedDeviceChooser : IDeviceChooserVM {
        private readonly IDeviceChooserVM inner;
        private volatile bool open;

        public GatedDeviceChooser(IDeviceChooserVM inner) {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public bool IsOpen => open;

        /// <summary>How many times the inner list was actually scanned (tests check that composing the engine never scans).</summary>
        public int Scans { get; private set; }

        public IDevice SelectedDevice {
            get => open ? inner.SelectedDevice : null;
            set {
                if (open) {
                    inner.SelectedDevice = value;
                }
            }
        }

        public bool SetupDialogOpen => false;

        public IList<IDevice> Devices => open ? inner.Devices : Array.Empty<IDevice>();

        public void Open() => open = true;

        public void Close() => open = false;

        public async Task GetEquipment() {
            if (!open) {
                return;
            }
            Scans++;
            await inner.GetEquipment();
            var selected = inner.SelectedDevice;
            if (selected == null || selected is DummyDevice || selected is OfflineDevice) {
                var device = inner.Devices?.FirstOrDefault(d => d is not DummyDevice && d is not OfflineDevice);
                if (device != null) {
                    inner.SelectedDevice = device;
                }
            }
        }
    }

    /// <summary>
    /// A behaviour selector with one fixed behaviour. NINA's Windows IoC binds <c>PluggableBehaviorSelector&lt;IStarDetection,
    /// StarDetection&gt;</c>; the headless host never runs upstream star detection (GDI+), it only needs the factories built.
    /// HFR and Bahtinov come from NINA.Mac.ImageAnalysis (<see cref="FrameAnalysisService"/>).
    /// </summary>
    internal sealed class FixedBehaviorSelector<T> : IPluggableBehaviorSelector<T> {

        public FixedBehaviorSelector(T behavior) {
            SelectedBehavior = behavior;
            Behaviors = new AsyncObservableCollection<T> { behavior };
        }

        public AsyncObservableCollection<T> Behaviors { get; set; }

        public T SelectedBehavior { get; set; }

        public event EventHandler SelectedBehaviorChanged {
            add { }
            remove { }
        }

        public T GetBehavior() => SelectedBehavior;

        public Type GetInterfaceType() => typeof(T);

        public void AddBehavior(object behavior) {
        }
    }

    internal static class EngineBehaviors {

        public static IPluggableBehaviorSelector<IStarDetection> StarDetectionSelector() => new FixedBehaviorSelector<IStarDetection>(new StarDetection());

        public static IPluggableBehaviorSelector<IStarAnnotator> StarAnnotatorSelector() => new FixedBehaviorSelector<IStarAnnotator>(new StarAnnotator());
    }
}
