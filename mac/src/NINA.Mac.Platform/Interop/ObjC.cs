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
using System.Runtime.InteropServices;
using System.Threading;

namespace NINA.Mac.Platform.Interop {

    /// <summary>
    /// Minimal Objective-C runtime access. On arm64 objc_msgSend must be called through a prototype that matches
    /// the method exactly (it is not variadic there); each overload below is one such prototype.
    /// </summary>
    internal static partial class ObjC {
        private const string Lib = "/usr/lib/libobjc.A.dylib";
        private const string FoundationPath = "/System/Library/Frameworks/Foundation.framework/Foundation";

        private static int foundationLoaded;

        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr objc_getClass(string name);

        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr sel_registerName(string name);

        [LibraryImport(Lib)]
        public static partial IntPtr objc_autoreleasePoolPush();

        [LibraryImport(Lib)]
        public static partial void objc_autoreleasePoolPop(IntPtr pool);

        /// <summary>id objc_msgSend(id, SEL)</summary>
        [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
        public static partial IntPtr MsgSend(IntPtr receiver, IntPtr selector);

        /// <summary>id objc_msgSend(id, SEL, id)</summary>
        [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
        public static partial IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

        /// <summary>id objc_msgSend(id, SEL, uint64_t, id): -[NSProcessInfo beginActivityWithOptions:reason:]</summary>
        [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
        public static partial IntPtr MsgSend(IntPtr receiver, IntPtr selector, ulong arg1, IntPtr arg2);

        /// <summary>const char* objc_msgSend(id, SEL): -[NSString UTF8String]</summary>
        [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
        public static partial IntPtr MsgSendPtr(IntPtr receiver, IntPtr selector);

        /// <summary>
        /// Foundation classes (NSProcessInfo, NSBundle) are only registered once Foundation is loaded. A GUI process
        /// already has it through AppKit; a console or test host may not.
        /// </summary>
        public static void EnsureFoundation() {
            if (Interlocked.Exchange(ref foundationLoaded, 1) == 0) {
                NativeLibrary.Load(FoundationPath);
            }
        }

        public static IntPtr Class(string name) {
            EnsureFoundation();
            var cls = objc_getClass(name);
            if (cls == IntPtr.Zero) {
                throw new InvalidOperationException($"Objective-C class {name} not found");
            }
            return cls;
        }

        /// <summary>Converts a (borrowed) NSString to a managed string.</summary>
        public static string ToManagedString(IntPtr nsString) {
            if (nsString == IntPtr.Zero) {
                return null;
            }
            var utf8 = MsgSendPtr(nsString, sel_registerName("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        /// <summary>Runs <paramref name="body"/> inside an autorelease pool, so autoreleased results do not leak on pool-less threads.</summary>
        public static T WithAutoreleasePool<T>(Func<T> body) {
            var pool = objc_autoreleasePoolPush();
            try {
                return body();
            } finally {
                objc_autoreleasePoolPop(pool);
            }
        }
    }
}
