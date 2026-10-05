#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NINA.Core.Utility;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace NINA.Mac.Engine.Test {

    /// <summary>Threading stand-ins: Dispatcher, DispatcherSynchronizationContext, DispatcherObject, Application, CommandManager.</summary>
    [TestFixture]
    [NonParallelizable]
    public class DispatcherTest {

        /// <summary>A UI-like loop: one thread with its own SynchronizationContext that runs posted work in order.</summary>
        private sealed class LoopThread : IDisposable {
            private readonly BlockingCollection<(SendOrPostCallback callback, object state, ManualResetEventSlim done)> queue = new();
            private readonly Thread thread;
            private readonly Action beforeContext;
            private readonly Action afterContext;

            /// <param name="beforeContext">Runs on the loop thread before its SynchronizationContext is installed</param>
            /// <param name="afterContext">Runs on the loop thread right after it is installed, before the loop starts</param>
            public LoopThread(Action beforeContext = null, Action afterContext = null) {
                this.beforeContext = beforeContext;
                this.afterContext = afterContext;
                thread = new Thread(Run) { IsBackground = true, Name = "loop" };
                thread.Start();
            }

            public Thread Thread => thread;

            private void Run() {
                beforeContext?.Invoke();
                SynchronizationContext.SetSynchronizationContext(new LoopContext(this));
                afterContext?.Invoke();
                foreach (var (callback, state, done) in queue.GetConsumingEnumerable()) {
                    try {
                        callback(state);
                    } finally {
                        done?.Set();
                    }
                }
            }

            public T Call<T>(Func<T> func) {
                T result = default;
                using var done = new ManualResetEventSlim();
                queue.Add((_ => result = func(), null, done));
                done.Wait();
                return result;
            }

            private sealed class LoopContext : SynchronizationContext {
                private readonly LoopThread loop;

                public LoopContext(LoopThread loop) {
                    this.loop = loop;
                }

                public override void Post(SendOrPostCallback d, object state) {
                    loop.queue.Add((d, state, null));
                }

                public override void Send(SendOrPostCallback d, object state) {
                    if (Thread.CurrentThread == loop.thread) {
                        d(state);
                        return;
                    }
                    using var done = new ManualResetEventSlim();
                    loop.queue.Add((d, state, done));
                    done.Wait();
                }
            }

            public void Dispose() {
                queue.CompleteAdding();
                thread.Join();
            }
        }

        [Test]
        public void CurrentDispatcher_IsPerThread_AndOwnsItsThread() {
            var mine = Dispatcher.CurrentDispatcher;
            Dispatcher.CurrentDispatcher.Should().BeSameAs(mine);
            mine.Thread.Should().BeSameAs(Thread.CurrentThread);
            mine.CheckAccess().Should().BeTrue();

            var other = Task.Run(() => (Dispatcher.CurrentDispatcher, mine.CheckAccess(), Record(() => mine.VerifyAccess()))).Result;
            other.Item1.Should().NotBeSameAs(mine);
            other.Item2.Should().BeFalse();
            other.Item3.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Be("The calling thread cannot access this object because a different thread owns it.");
        }

        private static Exception Record(Action action) {
            try {
                action();
                return null;
            } catch (Exception ex) {
                return ex;
            }
        }

        [Test]
        public void WithoutALoop_InvokeAndBeginInvoke_RunInlineOnTheCaller() {
            var dispatcher = Task.Run(() => Dispatcher.CurrentDispatcher).Result;
            var caller = Thread.CurrentThread;

            Thread ranOn = null;
            dispatcher.Invoke(() => { ranOn = Thread.CurrentThread; });
            ranOn.Should().BeSameAs(caller);
            dispatcher.Invoke(new Func<int, int, int>((a, b) => a + b), 2, 3).Should().Be(5);
            EventHandler handler = (s, e) => ranOn = (Thread)s;
            dispatcher.Invoke(handler, Thread.CurrentThread, null);
            FluentActions.Invoking(() => dispatcher.Invoke(new Action(() => throw new TimeoutException("x")))).Should().Throw<TimeoutException>();

            var log = new List<int>();
            var operation = dispatcher.BeginInvoke(new Action(() => log.Add(1)));
            log.Should().Equal(1);
            operation.Status.Should().Be(DispatcherOperationStatus.Completed);
            operation.Task.IsCompletedSuccessfully.Should().BeTrue();
            operation.Priority.Should().Be(DispatcherPriority.Normal);
            operation.Dispatcher.Should().BeSameAs(dispatcher);

            var failing = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => throw new TimeoutException("y")));
            failing.Task.IsFaulted.Should().BeTrue();
            failing.Task.Exception.InnerException.Should().BeOfType<TimeoutException>();

            FluentActions.Invoking(() => dispatcher.BeginInvoke(DispatcherPriority.Inactive, new Action(() => { }))).Should().Throw<NotSupportedException>();
            FluentActions.Invoking(() => dispatcher.BeginInvoke(DispatcherPriority.Invalid, new Action(() => { }))).Should().Throw<InvalidEnumArgumentException>();
            FluentActions.Invoking(() => dispatcher.BeginInvoke((DispatcherPriority)11, new Action(() => { }))).Should().Throw<InvalidEnumArgumentException>();
        }

        [Test]
        public void WithoutALoop_BeginInvoke_QueuesWhileAnotherThreadRunsWork_InsteadOfBlocking() {
            var dispatcher = Task.Run(() => Dispatcher.CurrentDispatcher).Result;
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var busy = Task.Run(() => dispatcher.Invoke(() => { started.Set(); release.Wait(); }));
            started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

            var order = new ConcurrentQueue<string>();
            DispatcherOperation queued;
            try {
                var clock = Stopwatch.StartNew();
                var begin = Task.Run(() => dispatcher.BeginInvoke(new Action(() => order.Enqueue("queued"))));
                begin.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("BeginInvoke must not wait for the running item");
                clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
                queued = begin.Result;
                queued.Status.Should().Be(DispatcherOperationStatus.Pending);
                order.Enqueue("released");
            } finally {
                release.Set();
            }
            busy.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            queued.Task.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            queued.Status.Should().Be(DispatcherOperationStatus.Completed);
            order.Should().Equal("released", "queued");
        }

        [Test]
        public void WithoutALoop_AWorkItemWaitingForAThreadThatCallsBeginInvoke_DoesNotDeadlock() {
            var dispatcher = Task.Run(() => Dispatcher.CurrentDispatcher).Result;
            DispatcherOperation fromHelper = null;
            var outer = Task.Run(() => dispatcher.Invoke(() => {
                var helper = new Thread(() => fromHelper = dispatcher.BeginInvoke(new Action(() => { }))) { IsBackground = true };
                helper.Start();
                helper.Join();
            }));
            outer.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("BeginInvoke from the helper queues instead of waiting for the work item");
            fromHelper.Task.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        }

        [Test]
        public void WithoutALoop_InsideAWorkItem_InvokeRunsInline_AndBeginInvokeRunsAfterIt() {
            var dispatcher = Task.Run(() => Dispatcher.CurrentDispatcher).Result;
            var log = new ConcurrentQueue<int>();
            DispatcherOperation inner = null;
            var innerStatusInside = DispatcherOperationStatus.Aborted;
            dispatcher.Invoke(() => {
                inner = dispatcher.BeginInvoke(new Action(() => log.Enqueue(3)));
                dispatcher.Invoke(() => log.Enqueue(1));
                log.Enqueue(2);
                innerStatusInside = inner.Status;
            });
            innerStatusInside.Should().Be(DispatcherOperationStatus.Pending, "as in WPF, BeginInvoke from a work item queues behind it");
            inner.Task.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            log.Should().Equal(1, 2, 3);
        }

        [Test]
        public void WithoutALoop_WorkItemsFromManyThreads_NeverOverlap() {
            var dispatcher = Task.Run(() => Dispatcher.CurrentDispatcher).Result;
            var running = 0;
            var maxRunning = 0;
            var ran = 0;
            var maxLock = new object();
            void Work() {
                var now = Interlocked.Increment(ref running);
                lock (maxLock) {
                    maxRunning = Math.Max(maxRunning, now);
                }
                Thread.SpinWait(200);
                Interlocked.Increment(ref ran);
                Interlocked.Decrement(ref running);
            }
            var operations = new ConcurrentBag<DispatcherOperation>();
            Task.WaitAll(Enumerable.Range(0, 8).Select(t => Task.Run(() => {
                for (var i = 0; i < 50; i++) {
                    if ((i + t) % 2 == 0) {
                        dispatcher.Invoke(Work);
                    } else {
                        operations.Add(dispatcher.BeginInvoke(new Action(Work)));
                    }
                }
            })).ToArray(), TimeSpan.FromSeconds(30)).Should().BeTrue();
            Task.WaitAll(operations.Select(o => o.Task).ToArray(), TimeSpan.FromSeconds(30)).Should().BeTrue();
            ran.Should().Be(400);
            maxRunning.Should().Be(1);
        }

        [Test]
        public void Application_AdoptsTheUiLoop_WhenItsThreadUsedTheDispatcherBeforeTheLoopExisted() {
            // Without an Application, a dispatcher first used before the UI context was installed has no loop
            using (var early = new LoopThread(beforeContext: CommandManager.InvalidateRequerySuggested)) {
                var dispatcher = early.Call(() => Dispatcher.CurrentDispatcher);
                Task.Run(() => dispatcher.Invoke(new Func<Thread>(() => Thread.CurrentThread))).Result.Should().NotBeSameAs(early.Thread);
            }

            // Creating the Application on that thread once the loop exists makes the dispatcher marshal through it
            var current = typeof(Application).GetField("current", BindingFlags.NonPublic | BindingFlags.Static);
            var hostApplication = current.GetValue(null);
            try {
                current.SetValue(null, null);
                Application created = null;
                using var loop = new LoopThread(beforeContext: CommandManager.InvalidateRequerySuggested, afterContext: () => created = new Application());
                var dispatcher = loop.Call(() => Dispatcher.CurrentDispatcher);
                created.Dispatcher.Should().BeSameAs(dispatcher);
                Task.Run(() => created.Dispatcher.Invoke(new Func<Thread>(() => Thread.CurrentThread))).Result.Should().BeSameAs(loop.Thread);
                var posted = created.Dispatcher.BeginInvoke(new Func<Thread>(() => Thread.CurrentThread));
                posted.Task.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                ((Task<object>)posted.Task).Result.Should().BeSameAs(loop.Thread);
            } finally {
                current.SetValue(null, hostApplication);
            }
        }

        [Test]
        public async Task WithALoop_WorkIsMarshalledToTheLoopThread() {
            using var loop = new LoopThread();
            var dispatcher = loop.Call(() => Dispatcher.CurrentDispatcher);
            dispatcher.Thread.Should().BeSameAs(loop.Thread);
            dispatcher.CheckAccess().Should().BeFalse();

            dispatcher.Invoke(new Func<Thread>(() => Thread.CurrentThread)).Should().BeSameAs(loop.Thread);
            FluentActions.Invoking(() => dispatcher.Invoke(new Action(() => throw new TimeoutException()))).Should().Throw<TimeoutException>();

            var order = new ConcurrentQueue<int>();
            var gate = new ManualResetEventSlim();
            var first = dispatcher.BeginInvoke(new Action(() => { gate.Wait(); order.Enqueue(1); }));
            var second = dispatcher.BeginInvoke(new Action(() => order.Enqueue(2)));
            first.Status.Should().Be(DispatcherOperationStatus.Pending, "BeginInvoke returns before the loop runs the work");
            gate.Set();
            await first;
            await second;
            order.Should().Equal(1, 2);

            // On its own thread Invoke runs inline, BeginInvoke still queues
            loop.Call(() => {
                var inline = false;
                dispatcher.Invoke(() => inline = true);
                var queued = dispatcher.BeginInvoke(new Action(() => { }));
                return (inline, queued.Status);
            }).Should().Be((true, DispatcherOperationStatus.Pending));
        }

        [Test]
        public void DispatcherSynchronizationContext_SendsAndPostsThroughTheDispatcher() {
            using var loop = new LoopThread();
            var dispatcher = loop.Call(() => Dispatcher.CurrentDispatcher);
            var context = new DispatcherSynchronizationContext(dispatcher);
            Thread sentOn = null;
            context.Send(_ => sentOn = Thread.CurrentThread, null);
            sentOn.Should().BeSameAs(loop.Thread);

            using var posted = new ManualResetEventSlim();
            Thread postedOn = null;
            context.Post(_ => { postedOn = Thread.CurrentThread; posted.Set(); }, null);
            posted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            postedOn.Should().BeSameAs(loop.Thread);

            context.CreateCopy().Should().BeOfType<DispatcherSynchronizationContext>().And.NotBeSameAs(context);
            FluentActions.Invoking(() => new DispatcherSynchronizationContext(null)).Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void Application_IsASingleton_WithAKeyedResourceStore() {
            Application.Current.Should().NotBeNull();
            FluentActions.Invoking(() => new Application()).Should().Throw<InvalidOperationException>();
            var resources = Application.Current.Resources;
            resources["mac-missing"].Should().BeNull();
            resources.Contains("mac-missing").Should().BeFalse();
            resources.Add("mac-key", 1);
            FluentActions.Invoking(() => resources.Add("mac-key", 2)).Should().Throw<ArgumentException>();
            resources["mac-key"] = 3;
            resources["mac-key"].Should().Be(3);
            resources.Remove("mac-key");
            resources.Contains("mac-key").Should().BeFalse();
            new ApplicationResourceDictionary()["ActiveProfile"].Should().BeSameAs(resources["ActiveProfile"]);
        }

        [Test]
        public void CommandManager_RaisesLiveHandlersOnThisThread_AndDropsCollectedOnes() {
            var raised = 0;
            EventHandler kept = (s, e) => raised++;
            CommandManager.RequerySuggested += kept;
            AddCollectableHandler(() => raised += 100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            CommandManager.InvalidateRequerySuggested();
            raised.Should().Be(1, "no loop on this thread, so the requery runs inline; the uncollected handler was weak");

            Task.Run(CommandManager.InvalidateRequerySuggested).Wait();
            raised.Should().Be(1, "handlers belong to the thread that subscribed");

            CommandManager.RequerySuggested -= kept;
            CommandManager.InvalidateRequerySuggested();
            raised.Should().Be(1);
            GC.KeepAlive(kept);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddCollectableHandler(Action onRaise) {
            CommandManager.RequerySuggested += (s, e) => onRaise();
        }

        [Test]
        public void CommandManager_OnALoop_CoalescesRequeriesAndRaisesLater() {
            using var loop = new LoopThread();
            var raised = 0;
            EventHandler handler = (s, e) => Interlocked.Increment(ref raised);
            loop.Call(() => {
                CommandManager.RequerySuggested += handler;
                CommandManager.InvalidateRequerySuggested();
                CommandManager.InvalidateRequerySuggested();
                return raised;
            }).Should().Be(0, "the requery is queued behind the current work item");
            loop.Call(() => raised).Should().Be(1, "two requests were coalesced into one");
            GC.KeepAlive(handler);
        }

        [Test]
        public void NinaAsyncCommand_RaisesCanExecuteChanged_ThroughCommandManager() {
#pragma warning disable CS0618 // upstream marks AsyncCommand obsolete; it is still used by Equipment and Sequencer
            var command = new AsyncCommand<bool>(() => Task.FromResult(true));
#pragma warning restore CS0618
            var changes = 0;
            EventHandler handler = (s, e) => changes++;
            command.CanExecuteChanged += handler;
            command.ExecuteAsync(null).Wait();
            changes.Should().Be(2);
            command.CanExecuteChanged -= handler;
        }
    }
}
