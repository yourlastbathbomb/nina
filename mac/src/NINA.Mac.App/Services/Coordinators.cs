#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.Platform;
using System;

namespace NINA.Mac.App.Services {

    /// <summary>
    /// Holds the keep-awake assertions while the night is in progress: from the first device connection until
    /// everything is disconnected and no session runs (i.e. the Teardown screen finished).
    /// </summary>
    public sealed class KeepAwakeCoordinator : IDisposable {
        private readonly IKeepAwake keepAwake;
        private readonly ICameraService camera;
        private readonly IMountService mount;
        private readonly ISessionService session;
        private readonly ISettingsStore settings;
        private readonly string reason;
        private KeepAwakeOptions applied;

        public KeepAwakeCoordinator(IKeepAwake keepAwake, ICameraService camera, IMountService mount, ISessionService session, ISettingsStore settings, string reason) {
            this.keepAwake = keepAwake ?? throw new ArgumentNullException(nameof(keepAwake));
            this.camera = camera ?? throw new ArgumentNullException(nameof(camera));
            this.mount = mount ?? throw new ArgumentNullException(nameof(mount));
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.reason = reason;
            camera.Changed += OnInputChanged;
            mount.Changed += OnInputChanged;
            session.Changed += OnInputChanged;
            settings.Changed += OnInputChanged;
            keepAwake.StateChanged += OnKeepAwakeChanged;
        }

        public event EventHandler Changed;

        public KeepAwakeState State => keepAwake.State;

        /// <summary>True while any device is connected (or connecting, or lost and awaiting reconnect) or a session runs.</summary>
        public bool NightInProgress =>
            IsActive(camera.State) || IsActive(mount.State) || session.State is SessionState.Running or SessionState.Paused or SessionState.Stopping;

        public void Evaluate() {
            var s = settings.Current;
            var wanted = NightInProgress && (s.KeepSystemAwake || s.KeepDisplayAwake || s.PreventAppNap)
                ? new KeepAwakeOptions(s.KeepSystemAwake, s.KeepDisplayAwake, s.PreventAppNap)
                : null;
            if (wanted == applied) {
                return;
            }
            applied = wanted;
            if (wanted == null) {
                keepAwake.Release();
            } else {
                keepAwake.Engage(reason, wanted);
            }
        }

        public void Dispose() {
            camera.Changed -= OnInputChanged;
            mount.Changed -= OnInputChanged;
            session.Changed -= OnInputChanged;
            settings.Changed -= OnInputChanged;
            keepAwake.StateChanged -= OnKeepAwakeChanged;
            keepAwake.Release();
        }

        private static bool IsActive(DeviceConnectionState s) => s != DeviceConnectionState.Disconnected;

        private void OnInputChanged(object sender, EventArgs e) => Evaluate();

        private void OnKeepAwakeChanged(object sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Polls the power source (IOKit) for the status bar; the app refreshes it every 30 s.</summary>
    public sealed class PowerMonitor {
        private readonly IPowerSource source;

        public PowerMonitor(IPowerSource source) {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public PowerSourceInfo Current { get; private set; } = PowerSourceInfo.Unavailable;

        /// <summary>The source it polls (preflight reads it once more).</summary>
        public IPowerSource Source => source;

        public string LastError { get; private set; }

        public event EventHandler Changed;

        public void Refresh() {
            PowerSourceInfo reading;
            try {
                reading = source.Read();
                LastError = null;
            } catch (Exception ex) when (ex is PlatformServiceException || ex is PlatformNotSupportedException || ex is DllNotFoundException || ex is EntryPointNotFoundException) {
                reading = PowerSourceInfo.Unavailable;
                LastError = ex.Message;
            }
            if (reading != Current) {
                Current = reading;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Keep-awake that does nothing (non-macOS hosts).</summary>
    public sealed class NullKeepAwake : IKeepAwake {

        public KeepAwakeState State { get; private set; } = KeepAwakeState.Released;

        public event EventHandler StateChanged;

        public KeepAwakeState Engage(string reason, KeepAwakeOptions options) {
            State = KeepAwakeState.Released with { Reason = reason, Error = "Keep-awake is only available on macOS" };
            StateChanged?.Invoke(this, EventArgs.Empty);
            return State;
        }

        public KeepAwakeState Release() {
            State = KeepAwakeState.Released;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return State;
        }

        public void Dispose() {
        }
    }
}
