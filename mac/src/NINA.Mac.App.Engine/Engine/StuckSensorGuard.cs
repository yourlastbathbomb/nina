#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NINA.Mac.App.Engine {

    public sealed record StuckSensorGuardOptions {

        /// <summary>How long the reading must stay unchanged while the power climbs.</summary>
        public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(90);

        /// <summary>Readings closer than this count as unchanged (the SDK reports 0.1 °C steps).</summary>
        public double FrozenToleranceCelsius { get; init; } = 0.05;

        /// <summary>Cooler power rise (percentage points) within the window that makes a frozen reading suspicious.</summary>
        public double PowerRisePercent { get; init; } = 15;
    }

    /// <summary>
    /// Stuck-sensor guard (mac/docs/m1-camera-results.md finding 3). After the USB cable came loose once while the camera stayed
    /// on 12 V, the next SDK session reported a constant 20.0 °C while the cooler power climbed from 0 to 54 % in 90 s, far
    /// faster than the normal ramp. A reading that does not move at all for <see cref="StuckSensorGuardOptions.Window"/> while the
    /// cooler power rises by <see cref="StuckSensorGuardOptions.PowerRisePercent"/> within that same window is reported; a
    /// camera holding its set point (reading steady, power steady or drifting slowly with the night) is not. Readings with the
    /// cooler off, or missing, clear the history.
    /// </summary>
    public sealed class StuckSensorGuard {
        private readonly StuckSensorGuardOptions options;
        private readonly LinkedList<(DateTimeOffset Time, double Temperature, double Power)> samples = new();

        public StuckSensorGuard(StuckSensorGuardOptions options = null) {
            this.options = options ?? new StuckSensorGuardOptions();
        }

        /// <summary>The current warning, or null.</summary>
        public string Warning { get; private set; }

        public void Reset() {
            samples.Clear();
            Warning = null;
        }

        /// <summary>Adds one reading and returns the warning (null when the sensor looks fine).</summary>
        public string Update(DateTimeOffset now, double? temperature, bool coolerOn, double? powerPercent) {
            if (!coolerOn || temperature is not double t || powerPercent is not double power || double.IsNaN(t) || double.IsNaN(power)) {
                Reset();
                return null;
            }
            if (samples.Count > 0 && Math.Abs(samples.Last.Value.Temperature - t) > options.FrozenToleranceCelsius) {
                // The reading moved: the sensor is alive, start over from this sample
                samples.Clear();
                Warning = null;
            }
            samples.AddLast((now, t, power));
            // Keep one sample at or before the window start, so "frozen for the whole window" can be checked
            while (samples.Count > 2 && samples.First.Next.Value.Time <= now - options.Window) {
                samples.RemoveFirst();
            }
            var oldest = samples.First.Value;
            if (now - oldest.Time < options.Window) {
                return Warning;
            }
            var lowest = samples.Where(s => s.Time >= now - options.Window).Min(s => s.Power);
            lowest = Math.Min(lowest, oldest.Power);
            if (power - lowest >= options.PowerRisePercent) {
                Warning = string.Format(CultureInfo.InvariantCulture,
                    "The sensor has read {0:0.0} °C for {1:0} s while the cooler power rose from {2:0} % to {3:0} %. The temperature reading may be frozen (a USB glitch can do this); reconnect the camera.",
                    t, (now - oldest.Time).TotalSeconds, lowest, power);
            }
            return Warning;
        }
    }
}
