#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections;
using System.Windows.Threading;

namespace System.Windows {

    /// <summary>
    /// Stand-in for WPF's Application object. As in WPF, <see cref="Current"/> is null until the host constructs one,
    /// and the instance belongs to the constructing thread's <see cref="Dispatcher"/>. Engine code that dispatches
    /// through <c>Application.Current.Dispatcher</c> (e.g. ProfileService events) needs the host to create it once at
    /// startup, on its UI thread if it has one. Windows, styles and the WPF lifetime (Run/Shutdown) do not exist.
    /// </summary>
    public class Application : DispatcherObject {
        private static readonly object instanceLock = new object();
        private static Application current;

        public Application() {
            lock (instanceLock) {
                if (current != null) {
                    throw new InvalidOperationException("Cannot create more than one System.Windows.Application instance in the same AppDomain.");
                }
                current = this;
            }
        }

        public static Application Current => current;

        /// <summary>Application-scope resource dictionary; engine code uses it as a keyed object store.</summary>
        public ResourceDictionary Resources { get; set; } = new ResourceDictionary();
    }

    /// <summary>
    /// Keyed object store with WPF ResourceDictionary lookup semantics for the members engine code uses:
    /// the indexer returns null for a missing key, Add throws on a duplicate key. No XAML, merged or theme dictionaries.
    /// </summary>
    public class ResourceDictionary : IDictionary {
        private readonly Hashtable entries = new Hashtable();

        public object this[object key] {
            get => entries[key];
            set => entries[key] = value;
        }

        public bool Contains(object key) {
            return entries.Contains(key);
        }

        public void Add(object key, object value) {
            entries.Add(key, value);
        }

        public void Remove(object key) {
            entries.Remove(key);
        }

        public void Clear() {
            entries.Clear();
        }

        public int Count => entries.Count;

        public ICollection Keys => entries.Keys;

        public ICollection Values => entries.Values;

        public bool IsFixedSize => false;

        public bool IsReadOnly => false;

        bool ICollection.IsSynchronized => entries.IsSynchronized;

        object ICollection.SyncRoot => entries.SyncRoot;

        public IDictionaryEnumerator GetEnumerator() {
            return entries.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return entries.GetEnumerator();
        }

        void ICollection.CopyTo(Array array, int index) {
            entries.CopyTo(array, index);
        }
    }

    /// <summary>
    /// Placeholder for WPF's Window, which only appears in engine signatures (e.g. ProfileService.ActivateInstanceWatcher).
    /// There are no WPF windows on macOS: construction throws.
    /// </summary>
    public class Window : DispatcherObject {

        public Window() {
            throw UiOnly.NotSupported("System.Windows.Window");
        }

        public bool Activate() {
            throw UiOnly.NotSupported("Window.Activate");
        }
    }

    internal static class UiOnly {

        public static PlatformNotSupportedException NotSupported(string member) {
            return new PlatformNotSupportedException($"{member} is WPF user interface and does not exist in the macOS engine.");
        }
    }
}
