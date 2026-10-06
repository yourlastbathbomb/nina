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
using System.Windows.Threading;

namespace System.Windows {

    /// <summary>WindowsBase's abstract base of the weak event managers. Only the type; see <see cref="WeakEventManager{TEventSource, TEventArgs}"/>.</summary>
    public abstract class WeakEventManager : DispatcherObject {

        protected WeakEventManager() {
        }
    }

    /// <summary>
    /// WindowsBase's generic weak event manager, with WPF's semantics for the two static members NINA uses:
    /// <list type="bullet">
    /// <item>The event is found by name on <typeparamref name="TEventSource"/> (<c>typeof(TEventSource).GetEvent(eventName)</c>);
    /// an unknown name throws <see cref="ArgumentException"/>, a null handler <see cref="ArgumentNullException"/>.</item>
    /// <item>The source holds one forwarding delegate per source and event, never the listeners. Each handler's target is held
    /// weakly (a static method is held as is), so subscribing does not keep the listener alive; handlers whose target has been
    /// collected are dropped, and the forwarder is detached from the source once no handler is left.</item>
    /// <item>The event is delivered synchronously on the raising thread, in subscription order. Adding the same handler twice
    /// delivers it twice; <see cref="RemoveHandler"/> removes one registration.</item>
    /// </list>
    /// A null source means a static event of <typeparamref name="TEventSource"/>. NINA.Sequencer relies on this at run time:
    /// <c>DeepSkyObjectContainer</c> follows its target's <c>InputTarget.CoordinatesChanged</c> to push new coordinates into
    /// its conditions and triggers, and the profile's location and horizon changes.
    /// </summary>
    public class WeakEventManager<TEventSource, TEventArgs> : WeakEventManager where TEventArgs : EventArgs {
        private static readonly object sync = new object();
        private static readonly ConditionalWeakTable<object, Dictionary<string, Forwarder>> instanceSubscriptions = new ConditionalWeakTable<object, Dictionary<string, Forwarder>>();
        private static readonly Dictionary<string, Forwarder> staticSubscriptions = new Dictionary<string, Forwarder>();

        private WeakEventManager() {
        }

        public static void AddHandler(TEventSource source, string eventName, EventHandler<TEventArgs> handler) {
            if (handler == null) {
                throw new ArgumentNullException(nameof(handler));
            }
            var eventInfo = FindEvent(eventName);
            lock (sync) {
                var map = SubscriptionsOf(source);
                if (!map.TryGetValue(eventName, out var forwarder)) {
                    forwarder = new Forwarder(source, eventInfo);
                    map[eventName] = forwarder;
                }
                forwarder.Add(handler);
            }
        }

        public static void RemoveHandler(TEventSource source, string eventName, EventHandler<TEventArgs> handler) {
            if (handler == null) {
                throw new ArgumentNullException(nameof(handler));
            }
            FindEvent(eventName);
            lock (sync) {
                var map = SubscriptionsOf(source);
                if (map.TryGetValue(eventName, out var forwarder) && forwarder.Remove(handler)) {
                    map.Remove(eventName);
                }
            }
        }

        private static EventInfo FindEvent(string eventName) {
            if (eventName == null) {
                throw new ArgumentNullException(nameof(eventName));
            }
            var eventInfo = typeof(TEventSource).GetEvent(eventName);
            if (eventInfo == null) {
                throw new ArgumentException($"The event '{eventName}' was not found on type '{typeof(TEventSource).FullName}'.", nameof(eventName));
            }
            return eventInfo;
        }

        private static Dictionary<string, Forwarder> SubscriptionsOf(TEventSource source) {
            object key = source;
            return key == null ? staticSubscriptions : instanceSubscriptions.GetValue(key, _ => new Dictionary<string, Forwarder>());
        }

        /// <summary>The one delegate attached to a source's event; it holds the listeners weakly and forwards to them.</summary>
        private sealed class Forwarder {
            private static readonly MethodInfo deliver = typeof(Forwarder).GetMethod(nameof(Deliver), BindingFlags.NonPublic | BindingFlags.Instance);
            private readonly WeakReference source;
            private readonly bool isStatic;
            private readonly EventInfo eventInfo;
            private readonly Delegate attached;
            private readonly List<Listener> listeners = new List<Listener>();

            public Forwarder(object source, EventInfo eventInfo) {
                this.source = new WeakReference(source);
                isStatic = source == null;
                this.eventInfo = eventInfo;
                // Bound to the event's own delegate type (EventHandler, EventHandler<T>, ...), as WPF does
                attached = Delegate.CreateDelegate(eventInfo.EventHandlerType, this, deliver);
                eventInfo.AddEventHandler(source, attached);
            }

            public void Add(EventHandler<TEventArgs> handler) {
                listeners.Add(new Listener(handler));
            }

            /// <summary>Removes one registration of the handler; true when no listener is left and the forwarder has been detached.</summary>
            public bool Remove(EventHandler<TEventArgs> handler) {
                for (var i = 0; i < listeners.Count; i++) {
                    if (listeners[i].Matches(handler)) {
                        listeners.RemoveAt(i);
                        break;
                    }
                }
                listeners.RemoveAll(l => !l.IsAlive);
                return DetachIfEmpty();
            }

            private bool DetachIfEmpty() {
                if (listeners.Count > 0) {
                    return false;
                }
                var target = source.Target;
                if (target != null || isStatic) {
                    eventInfo.RemoveEventHandler(target, attached);
                }
                return true;
            }

            private void Deliver(object sender, TEventArgs args) {
                List<Listener> snapshot;
                lock (sync) {
                    listeners.RemoveAll(l => !l.IsAlive);
                    snapshot = new List<Listener>(listeners);
                }
                foreach (var listener in snapshot) {
                    listener.Invoke(sender, args);
                }
            }
        }

        private sealed class Listener {
            private readonly WeakReference target;
            private readonly MethodInfo method;
            private readonly EventHandler<TEventArgs> staticHandler;

            public Listener(EventHandler<TEventArgs> handler) {
                if (handler.Target == null) {
                    staticHandler = handler;
                } else {
                    target = new WeakReference(handler.Target);
                }
                method = handler.Method;
            }

            public bool IsAlive => staticHandler != null || target.IsAlive;

            public bool Matches(EventHandler<TEventArgs> handler) {
                if (staticHandler != null) {
                    return handler.Target == null && handler.Method == method;
                }
                return ReferenceEquals(target.Target, handler.Target) && handler.Method == method;
            }

            public void Invoke(object sender, TEventArgs args) {
                if (staticHandler != null) {
                    staticHandler(sender, args);
                    return;
                }
                var instance = target.Target;
                if (instance != null) {
                    ((EventHandler<TEventArgs>)Delegate.CreateDelegate(typeof(EventHandler<TEventArgs>), instance, method))(sender, args);
                }
            }
        }
    }
}
