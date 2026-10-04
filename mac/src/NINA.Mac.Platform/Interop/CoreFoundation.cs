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
using System.Text;

namespace NINA.Mac.Platform.Interop {

    /// <summary>
    /// The handful of CoreFoundation calls the platform services need. CF types are passed as raw IntPtr;
    /// "Create"/"Copy" results are owned (+1) and must be released with <see cref="CFRelease"/>.
    /// </summary>
    internal static unsafe partial class CoreFoundation {
        private const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        public const uint kCFStringEncodingUTF8 = 0x08000100;
        public const nint kCFNumberSInt64Type = 4;
        public const nint kCFNumberFloat64Type = 6;

        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);

        [LibraryImport(Lib)]
        public static partial void CFRelease(IntPtr cf);

        [LibraryImport(Lib)]
        public static partial nuint CFGetTypeID(IntPtr cf);

        [LibraryImport(Lib)]
        public static partial nuint CFStringGetTypeID();

        [LibraryImport(Lib)]
        public static partial nuint CFNumberGetTypeID();

        [LibraryImport(Lib)]
        public static partial nuint CFBooleanGetTypeID();

        [LibraryImport(Lib)]
        public static partial nuint CFArrayGetTypeID();

        [LibraryImport(Lib)]
        public static partial nuint CFDictionaryGetTypeID();

        [LibraryImport(Lib)]
        public static partial nint CFArrayGetCount(IntPtr array);

        [LibraryImport(Lib)]
        public static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

        [LibraryImport(Lib)]
        public static partial IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

        [LibraryImport(Lib)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool CFNumberGetValue(IntPtr number, nint theType, out long value);

        [LibraryImport(Lib)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool CFNumberGetValue(IntPtr number, nint theType, out double value);

        [LibraryImport(Lib)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool CFBooleanGetValue(IntPtr boolean);

        [LibraryImport(Lib)]
        public static partial nint CFStringGetLength(IntPtr str);

        [LibraryImport(Lib)]
        public static partial nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

        [LibraryImport(Lib)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool CFStringGetCString(IntPtr str, byte* buffer, nint bufferSize, uint encoding);

        /// <summary>Creates an owned CFString; dispose the returned holder to release it.</summary>
        public static CFStringHolder CreateString(string value) {
            var handle = CFStringCreateWithCString(IntPtr.Zero, value, kCFStringEncodingUTF8);
            if (handle == IntPtr.Zero) {
                throw new InvalidOperationException($"CFStringCreateWithCString failed for '{value}'");
            }
            return new CFStringHolder(handle);
        }

        public static bool IsString(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == CFStringGetTypeID();

        public static bool IsNumber(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == CFNumberGetTypeID();

        public static bool IsBoolean(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == CFBooleanGetTypeID();

        public static bool IsArray(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == CFArrayGetTypeID();

        public static bool IsDictionary(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == CFDictionaryGetTypeID();

        /// <summary>Converts a (borrowed) CFString to a managed string; null when <paramref name="cf"/> is not a CFString.</summary>
        public static string ToManagedString(IntPtr cf) {
            if (!IsString(cf)) {
                return null;
            }
            var length = CFStringGetLength(cf);
            var size = CFStringGetMaximumSizeForEncoding(length, kCFStringEncodingUTF8) + 1;
            var buffer = new byte[size];
            fixed (byte* p = buffer) {
                if (!CFStringGetCString(cf, p, size, kCFStringEncodingUTF8)) {
                    return null;
                }
            }
            var end = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        }

        /// <summary>Looks up <paramref name="key"/> in a (borrowed) CFDictionary. The result is borrowed, not owned.</summary>
        public static IntPtr GetValue(IntPtr dictionary, string key) {
            if (!IsDictionary(dictionary)) {
                return IntPtr.Zero;
            }
            using var cfKey = CreateString(key);
            return CFDictionaryGetValue(dictionary, cfKey.Handle);
        }

        public static string GetString(IntPtr dictionary, string key) => ToManagedString(GetValue(dictionary, key));

        public static long? GetInt64(IntPtr dictionary, string key) {
            var value = GetValue(dictionary, key);
            if (!IsNumber(value)) {
                return null;
            }
            return CFNumberGetValue(value, kCFNumberSInt64Type, out long result) ? result : null;
        }

        public static bool? GetBoolean(IntPtr dictionary, string key) {
            var value = GetValue(dictionary, key);
            if (IsBoolean(value)) {
                return CFBooleanGetValue(value);
            }
            if (IsNumber(value) && CFNumberGetValue(value, kCFNumberSInt64Type, out long number)) {
                return number != 0;
            }
            return null;
        }
    }

    /// <summary>Owns one CFString reference.</summary>
    internal readonly struct CFStringHolder : IDisposable {

        public CFStringHolder(IntPtr handle) {
            Handle = handle;
        }

        public IntPtr Handle { get; }

        public void Dispose() {
            if (Handle != IntPtr.Zero) {
                CoreFoundation.CFRelease(Handle);
            }
        }
    }
}
