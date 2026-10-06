#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Windows.Threading {

    /// <summary>
    /// Stand-in for WPF's Dispatcher on hosts without a WPF message loop.
    /// A dispatcher belongs to the thread that created it (<see cref="CurrentDispatcher"/>), as in WPF.
    /// If that thread has a SynchronizationContext (a UI framework's main loop), work from other threads is marshalled
    /// through it: Invoke uses Send and blocks, BeginInvoke uses Post. The loop is taken from the thread when the dispatcher
    /// is created, or when an <see cref="Application"/> is created on that thread later (UI frameworks often install their
    /// context after the first dispatcher use).
    /// Without a loop, work items run one at a time and never overlap, as on WPF's single dispatcher thread:
    /// <list type="bullet">
    /// <item>When the dispatcher is idle, Invoke and BeginInvoke run the callback inline on the calling thread.</item>
    /// <item>While a work item runs, Invoke from another thread waits for it and then runs (Send priority goes first);
    /// Invoke from inside a work item runs inline, as WPF's Invoke does on its own thread.</item>
    /// <item>While a work item runs, or other items are queued, BeginInvoke queues and returns a Pending operation. Queued
    /// items run in order on a thread-pool thread once the current item finishes, so BeginInvoke never blocks.</item>
    /// </list>
    /// Priorities are validated and recorded but do not reorder queued work.
    /// </summary>
    public sealed class Dispatcher {

        [ThreadStatic]
        private static Dispatcher currentDispatcher;

        private readonly object gate = new object();
        private readonly Queue<DispatcherOperation> pending = new Queue<DispatcherOperation>();
        private volatile SynchronizationContext loop;
        // Without a loop: the thread running a work item now, and whether a thread-pool drain of 'pending' is scheduled
        private Thread executing;
        private bool draining;

        private Dispatcher() {
            Thread = Thread.CurrentThread;
            loop = LoopOf(SynchronizationContext.Current);
        }

        /// <summary>The dispatcher of the calling thread, created on first use (WPF semantics).</summary>
        public static Dispatcher CurrentDispatcher => currentDispatcher ??= new Dispatcher();

        public Thread Thread { get; }

        public bool CheckAccess() {
            return Thread == Thread.CurrentThread;
        }

        public void VerifyAccess() {
            if (!CheckAccess()) {
                throw new InvalidOperationException("The calling thread cannot access this object because a different thread owns it.");
            }
        }

        public void Invoke(Action callback) {
            InvokeCore(DispatcherPriority.Send, callback, null);
        }

        public object Invoke(Delegate method, params object[] args) {
            return InvokeCore(DispatcherPriority.Send, method, args);
        }

        public DispatcherOperation BeginInvoke(Delegate method, params object[] args) {
            return BeginInvokeCore(DispatcherPriority.Normal, method, args);
        }

        public DispatcherOperation BeginInvoke(DispatcherPriority priority, Delegate method) {
            return BeginInvokeCore(priority, method, null);
        }

        /// <summary>WPF's InvokeAsync at Normal priority: queued like <see cref="BeginInvoke(Delegate, object[])"/>.</summary>
        public DispatcherOperation InvokeAsync(Action callback) {
            return InvokeAsync(callback, DispatcherPriority.Normal, CancellationToken.None);
        }

        /// <summary>
        /// WPF's InvokeAsync: queued like <see cref="BeginInvoke(Delegate, object[])"/>. If <paramref name="cancellationToken"/>
        /// is cancelled before the callback starts, the operation is aborted and its <see cref="DispatcherOperation.Task"/> is
        /// cancelled, as in WPF; once it has started it runs to completion.
        /// </summary>
        public DispatcherOperation InvokeAsync(Action callback, DispatcherPriority priority, CancellationToken cancellationToken) {
            ArgumentNullException.ThrowIfNull(callback);
            return BeginInvokeCore(priority, callback, null, cancellationToken);
        }

        private static SynchronizationContext LoopOf(SynchronizationContext context) {
            return context is DispatcherSynchronizationContext ? null : context;
        }

        /// <summary>
        /// Called on the owning thread by <see cref="Application"/>'s constructor: if this dispatcher was created before the UI
        /// framework installed its SynchronizationContext, it marshals through that loop from now on.
        /// </summary>
        internal void AdoptLoop(SynchronizationContext context) {
            var candidate = LoopOf(context);
            if (candidate != null && CheckAccess()) {
                lock (gate) {
                    loop ??= candidate;
                }
            }
        }

        internal object InvokeCore(DispatcherPriority priority, Delegate method, object[] args) {
            ArgumentNullException.ThrowIfNull(method);
            ValidatePriority(priority);
            var context = loop;
            if (context == null) {
                if (!Enter()) {
                    return Execute(method, args);
                }
                try {
                    return Execute(method, args);
                } finally {
                    Exit();
                }
            }
            if (CheckAccess()) {
                return Execute(method, args);
            }

            object result = null;
            ExceptionDispatchInfo error = null;
            context.Send(_ => {
                try {
                    result = Execute(method, args);
                } catch (Exception ex) {
                    error = ExceptionDispatchInfo.Capture(ex);
                }
            }, null);
            error?.Throw();
            return result;
        }

        internal DispatcherOperation BeginInvokeCore(DispatcherPriority priority, Delegate method, object[] args, CancellationToken cancellationToken = default) {
            ArgumentNullException.ThrowIfNull(method);
            ValidatePriority(priority);
            var operation = new DispatcherOperation(this, priority, method, args, cancellationToken);
            var context = loop;
            if (context != null) {
                context.Post(_ => operation.Run(), null);
                return operation;
            }
            lock (gate) {
                if (executing != null || draining || pending.Count > 0) {
                    pending.Enqueue(operation);
                    return operation;
                }
                executing = Thread.CurrentThread;
            }
            try {
                operation.Run();
            } finally {
                Exit();
            }
            return operation;
        }

        /// <summary>Takes the right to run a work item; false if the calling thread already holds it (a nested call).</summary>
        private bool Enter() {
            var me = Thread.CurrentThread;
            lock (gate) {
                if (executing == me) {
                    return false;
                }
                while (executing != null) {
                    Monitor.Wait(gate);
                }
                executing = me;
                return true;
            }
        }

        private void Exit() {
            lock (gate) {
                executing = null;
                if (pending.Count > 0 && !draining) {
                    draining = true;
                    ThreadPool.UnsafeQueueUserWorkItem(_ => Drain(), null);
                }
                Monitor.PulseAll(gate);
            }
        }

        private void Drain() {
            while (true) {
                DispatcherOperation next;
                lock (gate) {
                    while (executing != null) {
                        Monitor.Wait(gate);
                    }
                    if (pending.Count > 0 && loop != null) {
                        // A loop was adopted meanwhile: it runs the rest, in order
                        var context = loop;
                        foreach (var operation in pending) {
                            context.Post(_ => operation.Run(), null);
                        }
                        pending.Clear();
                    }
                    if (pending.Count == 0) {
                        draining = false;
                        return;
                    }
                    next = pending.Dequeue();
                    executing = Thread.CurrentThread;
                }
                next.Run();
                lock (gate) {
                    executing = null;
                    Monitor.PulseAll(gate);
                }
            }
        }

        internal static object Execute(Delegate method, object[] args) {
            if (method is Action action && (args == null || args.Length == 0)) {
                action();
                return null;
            }
            if (method is SendOrPostCallback callback && args?.Length == 1) {
                callback(args[0]);
                return null;
            }
            try {
                return method.DynamicInvoke(args);
            } catch (TargetInvocationException ex) when (ex.InnerException != null) {
                // Surface the callback's own exception, as a direct call would
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static void ValidatePriority(DispatcherPriority priority) {
            if (priority < DispatcherPriority.Inactive || priority > DispatcherPriority.Send) {
                throw new ComponentModel.InvalidEnumArgumentException(nameof(priority), (int)priority, typeof(DispatcherPriority));
            }
            if (priority == DispatcherPriority.Inactive) {
                // WPF parks Inactive operations until they are re-prioritized; that needs a WPF message loop
                throw new NotSupportedException("DispatcherPriority.Inactive needs a WPF message loop.");
            }
        }
    }

    /// <summary>
    /// The result of <see cref="Dispatcher.BeginInvoke(Delegate, object[])"/>. Awaitable.
    /// If the callback throws, the exception faults <see cref="Task"/> (WPF instead raises Dispatcher.UnhandledException).
    /// </summary>
    public sealed class DispatcherOperation {
        private readonly Delegate method;
        private readonly object[] args;
        private readonly TaskCompletionSource<object> completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly CancellationToken cancellationToken;

        internal DispatcherOperation(Dispatcher dispatcher, DispatcherPriority priority, Delegate method, object[] args, CancellationToken cancellationToken = default) {
            Dispatcher = dispatcher;
            Priority = priority;
            this.method = method;
            this.args = args;
            this.cancellationToken = cancellationToken;
        }

        public Dispatcher Dispatcher { get; }

        public DispatcherPriority Priority { get; }

        // Read from other threads (e.g. CommandManager coalescing) while a queued operation completes elsewhere
        private volatile DispatcherOperationStatus status = DispatcherOperationStatus.Pending;

        public DispatcherOperationStatus Status => status;

        public Task Task => completion.Task;

        public TaskAwaiter GetAwaiter() {
            return Task.GetAwaiter();
        }

        internal void Run() {
            if (cancellationToken.IsCancellationRequested) {
                // InvokeAsync with a cancelled token: WPF aborts the operation before it runs
                status = DispatcherOperationStatus.Aborted;
                completion.SetCanceled(cancellationToken);
                return;
            }
            status = DispatcherOperationStatus.Executing;
            try {
                var result = Dispatcher.Execute(method, args);
                status = DispatcherOperationStatus.Completed;
                completion.SetResult(result);
            } catch (Exception ex) {
                status = DispatcherOperationStatus.Completed;
                completion.SetException(ex);
            }
        }
    }

    public enum DispatcherPriority {
        Invalid = -1,
        Inactive = 0,
        SystemIdle = 1,
        ApplicationIdle = 2,
        ContextIdle = 3,
        Background = 4,
        Input = 5,
        Loaded = 6,
        Render = 7,
        DataBind = 8,
        Normal = 9,
        Send = 10
    }

    public enum DispatcherOperationStatus {
        Pending = 0,
        Aborted = 1,
        Completed = 2,
        Executing = 3
    }

    /// <summary>SynchronizationContext over a <see cref="Dispatcher"/>: Send = Invoke at Send priority, Post = BeginInvoke (WPF semantics).</summary>
    public sealed class DispatcherSynchronizationContext : SynchronizationContext {
        private readonly Dispatcher dispatcher;
        private readonly DispatcherPriority priority;

        public DispatcherSynchronizationContext() : this(Dispatcher.CurrentDispatcher, DispatcherPriority.Normal) {
        }

        public DispatcherSynchronizationContext(Dispatcher dispatcher) : this(dispatcher, DispatcherPriority.Normal) {
        }

        public DispatcherSynchronizationContext(Dispatcher dispatcher, DispatcherPriority priority) {
            ArgumentNullException.ThrowIfNull(dispatcher);
            this.dispatcher = dispatcher;
            this.priority = priority;
        }

        public override void Send(SendOrPostCallback d, object state) {
            dispatcher.InvokeCore(DispatcherPriority.Send, d, new[] { state });
        }

        public override void Post(SendOrPostCallback d, object state) {
            dispatcher.BeginInvokeCore(priority, d, new[] { state });
        }

        public override SynchronizationContext CreateCopy() {
            return new DispatcherSynchronizationContext(dispatcher, priority);
        }
    }

    /// <summary>Base for objects owned by a <see cref="Dispatcher"/>: captures the creating thread's dispatcher (WPF semantics).</summary>
    public abstract class DispatcherObject {

        protected DispatcherObject() {
            Dispatcher = Dispatcher.CurrentDispatcher;
        }

        public Dispatcher Dispatcher { get; }

        public bool CheckAccess() {
            return Dispatcher.CheckAccess();
        }

        public void VerifyAccess() {
            Dispatcher.VerifyAccess();
        }
    }
}
