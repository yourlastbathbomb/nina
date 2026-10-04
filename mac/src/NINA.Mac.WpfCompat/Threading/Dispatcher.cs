#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Windows.Threading {

    /// <summary>
    /// Stand-in for WPF's Dispatcher on hosts without a WPF message loop.
    /// A dispatcher belongs to the thread that created it (<see cref="CurrentDispatcher"/>), as in WPF.
    /// If that thread had a SynchronizationContext at creation (a UI framework's main loop), work from other
    /// threads is marshalled through it: Invoke uses Send and blocks, BeginInvoke uses Post.
    /// Without one there is no loop to marshal to: Invoke and BeginInvoke run the callback inline on the calling thread,
    /// holding the dispatcher's lock, so work items still never overlap (as on WPF's single dispatcher thread).
    /// Priorities are validated and recorded but do not reorder work.
    /// </summary>
    public sealed class Dispatcher {

        [ThreadStatic]
        private static Dispatcher currentDispatcher;

        private readonly SynchronizationContext loop;
        private readonly object gate = new object();

        private Dispatcher() {
            Thread = Thread.CurrentThread;
            var context = SynchronizationContext.Current;
            loop = context is DispatcherSynchronizationContext ? null : context;
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

        internal object InvokeCore(DispatcherPriority priority, Delegate method, object[] args) {
            ArgumentNullException.ThrowIfNull(method);
            ValidatePriority(priority);
            if (loop == null) {
                lock (gate) {
                    return Execute(method, args);
                }
            }
            if (CheckAccess()) {
                return Execute(method, args);
            }

            object result = null;
            ExceptionDispatchInfo error = null;
            loop.Send(_ => {
                try {
                    result = Execute(method, args);
                } catch (Exception ex) {
                    error = ExceptionDispatchInfo.Capture(ex);
                }
            }, null);
            error?.Throw();
            return result;
        }

        internal DispatcherOperation BeginInvokeCore(DispatcherPriority priority, Delegate method, object[] args) {
            ArgumentNullException.ThrowIfNull(method);
            ValidatePriority(priority);
            var operation = new DispatcherOperation(this, priority, method, args);
            if (loop == null) {
                lock (gate) {
                    operation.Run();
                }
            } else {
                loop.Post(_ => operation.Run(), null);
            }
            return operation;
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

        internal DispatcherOperation(Dispatcher dispatcher, DispatcherPriority priority, Delegate method, object[] args) {
            Dispatcher = dispatcher;
            Priority = priority;
            this.method = method;
            this.args = args;
        }

        public Dispatcher Dispatcher { get; }

        public DispatcherPriority Priority { get; }

        public DispatcherOperationStatus Status { get; private set; } = DispatcherOperationStatus.Pending;

        public Task Task => completion.Task;

        public TaskAwaiter GetAwaiter() {
            return Task.GetAwaiter();
        }

        internal void Run() {
            Status = DispatcherOperationStatus.Executing;
            try {
                var result = Dispatcher.Execute(method, args);
                Status = DispatcherOperationStatus.Completed;
                completion.SetResult(result);
            } catch (Exception ex) {
                Status = DispatcherOperationStatus.Completed;
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
