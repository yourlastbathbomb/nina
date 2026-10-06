#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Mac.Sequencing.Runner;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace NINA.Mac.Sequencer.Test.Sim {

    /// <summary>
    /// Runs a sequence through NINA's own Sequencer (HeadlessSequenceRunner) and records when each container started and
    /// finished (from its Status changes), so a test can compare a stop with the moment the sky crossed the limit.
    /// </summary>
    internal sealed class NightRun {
        private readonly ConcurrentDictionary<string, DateTime> started = new();
        private readonly ConcurrentDictionary<string, DateTime> finished = new();

        private NightRun(ISequenceRootContainer root) {
            Root = root;
            HeadlessSequenceRunner.Walk(root, entity => {
                if (entity is ISequenceContainer container && entity is INotifyPropertyChanged inpc) {
                    inpc.PropertyChanged += (sender, e) => {
                        if (e.PropertyName != nameof(ISequenceEntity.Status)) {
                            return;
                        }
                        var name = container.Name ?? container.GetType().Name;
                        if (container.Status == SequenceEntityStatus.RUNNING) {
                            started.TryAdd(name, DateTime.Now);
                        } else if (container.Status == SequenceEntityStatus.FINISHED) {
                            finished[name] = DateTime.Now;
                        }
                    };
                }
            });
        }

        public ISequenceRootContainer Root { get; }
        public DateTime Start { get; private set; }
        public DateTime End { get; private set; }
        public IReadOnlyList<string> Issues { get; private set; } = Array.Empty<string>();

        public DateTime? Started(string containerName) => started.TryGetValue(containerName, out var t) ? t : null;

        public DateTime? Finished(string containerName) => finished.TryGetValue(containerName, out var t) ? t : null;

        public static async Task<NightRun> Run(ISequenceRootContainer root, TimeSpan timeout) {
            var run = new NightRun(root);
            var runner = new HeadlessSequenceRunner(root);
            run.Issues = runner.Validate();
            using var cts = new CancellationTokenSource(timeout);
            run.Start = DateTime.Now;
            await runner.RunAsync(new Progress<ApplicationStatus>(), cts.Token);
            run.End = DateTime.Now;
            if (cts.IsCancellationRequested) {
                throw new TimeoutException($"The simulated night did not finish within {timeout}");
            }
            return run;
        }

        public static SequentialContainer Block(ISequenceRootContainer root, string name) {
            SequentialContainer found = null!;
            HeadlessSequenceRunner.Walk(root, entity => {
                if (entity is SequentialContainer s && s.Name == name) {
                    found = s;
                }
            });
            return found ?? throw new InvalidOperationException($"No block named {name}");
        }
    }
}
