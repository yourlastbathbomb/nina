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
using System.IO;
using System.Linq;

namespace NINA.Mac.Platform {

    /// <summary>
    /// Finds read-only app files the same way from a build output folder and from inside an .app bundle, starting
    /// from the managed app's base directory and never from the current directory (a Finder launch starts with
    /// CWD "/"; research/broad_verify_native.md NAT-M2).
    /// <list type="bullet">
    /// <item>Bundle (<c>X.app/Contents/MacOS</c> = base): resources in <c>Contents/Resources</c>, vendor dylibs in
    /// <c>Contents/Frameworks</c>, then <c>Contents/MacOS</c> (where the .NET host keeps NuGet natives).</item>
    /// <item>Build output: everything next to the assemblies, with a <c>Resources/</c> subfolder also searched so a
    /// dev layout can mirror the bundle.</item>
    /// </list>
    /// </summary>
    public sealed class ResourcePaths {
        private static readonly Lazy<ResourcePaths> current = new(() => new ResourcePaths(AppContext.BaseDirectory));

        /// <param name="baseDirectory">Absolute directory holding the app's managed assemblies (AppContext.BaseDirectory).</param>
        public ResourcePaths(string baseDirectory) {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
            if (!Path.IsPathFullyQualified(baseDirectory)) {
                throw new ArgumentException($"Base directory must be absolute (a relative one would depend on the current directory): '{baseDirectory}'", nameof(baseDirectory));
            }
            BaseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));

            var macOS = new DirectoryInfo(BaseDirectory);
            var contents = macOS.Parent;
            var bundle = contents?.Parent;
            IsAppBundle = macOS.Name == "MacOS"
                && contents?.Name == "Contents"
                && bundle != null
                && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase);

            if (IsAppBundle) {
                BundlePath = bundle.FullName;
                ContentsDirectory = contents.FullName;
                ExecutableDirectory = BaseDirectory;
                FrameworksDirectory = Path.Combine(ContentsDirectory, "Frameworks");
                ResourcesDirectory = Path.Combine(ContentsDirectory, "Resources");
                ResourceSearchDirectories = new[] { ResourcesDirectory };
                NativeLibrarySearchDirectories = new[] { FrameworksDirectory, ExecutableDirectory };
            } else {
                ExecutableDirectory = BaseDirectory;
                FrameworksDirectory = BaseDirectory;
                ResourcesDirectory = BaseDirectory;
                ResourceSearchDirectories = new[] { BaseDirectory, Path.Combine(BaseDirectory, "Resources") };
                NativeLibrarySearchDirectories = new[] { BaseDirectory, Path.Combine(BaseDirectory, "runtimes", "osx-arm64", "native") };
            }
        }

        /// <summary>Resolved from AppContext.BaseDirectory once per process.</summary>
        public static ResourcePaths Current => current.Value;

        public string BaseDirectory { get; }

        public bool IsAppBundle { get; }

        /// <summary>The <c>.app</c> directory, or null outside a bundle.</summary>
        public string BundlePath { get; }

        public string ContentsDirectory { get; }

        public string ExecutableDirectory { get; }

        /// <summary>Where vendor dylibs (ZWO, libusb, SOFA, NOVAS) live: <c>Contents/Frameworks</c> or the build output.</summary>
        public string FrameworksDirectory { get; }

        /// <summary>Where read-only data (JPLEPH, licences, templates) lives: <c>Contents/Resources</c> or the build output.</summary>
        public string ResourcesDirectory { get; }

        public IReadOnlyList<string> ResourceSearchDirectories { get; }

        public IReadOnlyList<string> NativeLibrarySearchDirectories { get; }

        /// <summary>Full path of a read-only resource such as "JPLEPH" or "licenses/ZWO-LICENSE.txt", or null.</summary>
        public string FindResource(string relativePath) => Find(ResourceSearchDirectories, relativePath, File.Exists);

        /// <summary>Like <see cref="FindResource"/> but for a directory.</summary>
        public string FindResourceDirectory(string relativePath) => Find(ResourceSearchDirectories, relativePath, Directory.Exists);

        /// <summary>Full path of a resource; throws <see cref="FileNotFoundException"/> naming every place searched.</summary>
        public string GetResource(string relativePath) {
            return FindResource(relativePath)
                ?? throw new FileNotFoundException($"Resource '{relativePath}' not found in: {string.Join(", ", ResourceSearchDirectories)}", relativePath);
        }

        /// <summary>Full path of a native library file such as "libASICamera2.dylib", or null.</summary>
        public string FindNativeLibrary(string fileName) {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            if (fileName.Contains('/')) {
                throw new ArgumentException("Pass a file name, not a path", nameof(fileName));
            }
            return Find(NativeLibrarySearchDirectories, fileName, File.Exists);
        }

        /// <summary>Every *.dylib in the vendor library directory (Contents/Frameworks in a bundle).</summary>
        public IReadOnlyList<string> VendorLibraries() {
            if (!Directory.Exists(FrameworksDirectory)) {
                return Array.Empty<string>();
            }
            return Directory.GetFiles(FrameworksDirectory, "*.dylib").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        }

        private static string Find(IEnumerable<string> directories, string relativePath, Func<string, bool> exists) {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            if (Path.IsPathRooted(relativePath)) {
                throw new ArgumentException($"Expected a relative path, got '{relativePath}'", nameof(relativePath));
            }
            var normalized = relativePath.Replace('\\', '/');
            if (normalized.Split('/').Contains("..")) {
                throw new ArgumentException($"'..' is not allowed in resource paths: '{relativePath}'", nameof(relativePath));
            }
            foreach (var dir in directories) {
                var candidate = Path.Combine(dir, normalized);
                if (exists(candidate)) {
                    return candidate;
                }
            }
            return null;
        }
    }
}
