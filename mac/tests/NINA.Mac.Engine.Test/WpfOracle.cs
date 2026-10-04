#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Mono.Cecil;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace NINA.Mac.Engine.Test {

    /// <summary>
    /// The real WPF assemblies, as a test oracle for the WpfCompat stand-ins.
    /// The WindowsDesktop runtime pack ships ReadyToRun images for win-x64, which CoreCLR on macOS arm64 refuses to load.
    /// Mono.Cecil rewrites WindowsBase, PresentationCore and System.Xaml as IL-only images (the IL is all there) and drops
    /// PresentationCore's module initializer, which loads the C++/CLI DirectWriteForwarder. Pure managed code such as
    /// Color arithmetic, parsing and formatting then runs unchanged. Anything touching Win32 or the WPF render thread
    /// does not, so only value semantics are compared. The rewritten copies live in the system temp folder.
    /// </summary>
    internal static class WpfOracle {
        private static readonly string[] executable = { "WindowsBase.dll", "PresentationCore.dll", "System.Xaml.dll" };
        private static readonly Lazy<(AssemblyLoadContext context, string error)> loaded = new Lazy<(AssemblyLoadContext, string)>(Load);

        /// <summary>Folder holding the original (unmodified) WPF assemblies, for metadata-only reads.</summary>
        public static string PackDir => typeof(WpfOracle).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "WpfRuntimePackDir").Value;

        public static Type GetType(string assembly, string fullName) {
            var (context, error) = loaded.Value;
            if (context == null) {
                Assert.Ignore($"WPF oracle unavailable: {error}");
            }
            var asm = context.Assemblies.Single(a => a.GetName().Name == assembly);
            return asm.GetType(fullName, throwOnError: true);
        }

        /// <summary>Metadata of an original WPF assembly (no execution).</summary>
        public static ModuleDefinition ReadModule(string assemblyFile) {
            var path = Path.Combine(PackDir, assemblyFile);
            if (!File.Exists(path)) {
                Assert.Ignore($"WPF runtime pack not found at {PackDir}");
            }
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(PackDir);
            return ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver });
        }

        private static (AssemblyLoadContext, string) Load() {
            try {
                var packDir = PackDir;
                if (!File.Exists(Path.Combine(packDir, "PresentationCore.dll"))) {
                    return (null, $"runtime pack not found at {packDir}");
                }
                var segments = Path.GetFullPath(packDir).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                var version = segments[Array.IndexOf(segments, "microsoft.windowsdesktop.app.runtime.win-x64") + 1];
                var target = Path.Combine(Path.GetTempPath(), "nina-mac-wpf-oracle", version);
                Directory.CreateDirectory(target);
                var resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(packDir);
                foreach (var file in executable) {
                    var destination = Path.Combine(target, file);
                    if (File.Exists(destination)) {
                        continue;
                    }
                    using var module = ModuleDefinition.ReadModule(Path.Combine(packDir, file), new ReaderParameters { AssemblyResolver = resolver });
                    module.Attributes = ModuleAttributes.ILOnly;
                    module.Architecture = TargetArchitecture.I386;
                    var moduleType = module.Types.Single(t => t.Name == "<Module>");
                    var initializer = moduleType.Methods.SingleOrDefault(m => m.IsConstructor && m.IsStatic);
                    if (initializer != null) {
                        moduleType.Methods.Remove(initializer);
                    }
                    var temp = destination + ".tmp";
                    module.Write(temp);
                    File.Move(temp, destination, true);
                }
                var context = new OracleLoadContext(target);
                foreach (var file in executable) {
                    context.LoadFromAssemblyPath(Path.Combine(target, file));
                }
                return (context, null);
            } catch (Exception ex) {
                return (null, ex.ToString());
            }
        }

        private sealed class OracleLoadContext : AssemblyLoadContext {
            private readonly string directory;

            public OracleLoadContext(string directory) : base("wpf-oracle") {
                this.directory = directory;
            }

            protected override Assembly Load(AssemblyName assemblyName) {
                var path = Path.Combine(directory, assemblyName.Name + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }

        public static object Invoke(Type type, string method, params object[] args) {
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == method && m.GetParameters().Length == args.Length).ToList();
            var match = candidates.Single(m => m.GetParameters().Select(p => p.ParameterType).SequenceEqual(args.Select(a => a.GetType())));
            return Unwrap(() => match.Invoke(null, args));
        }

        public static object Unwrap(Func<object> call) {
            try {
                return call();
            } catch (TargetInvocationException ex) when (ex.InnerException != null) {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        public static IEnumerable<FieldDefinition> EnumMembers(ModuleDefinition module, string fullName) {
            return module.GetType(fullName).Fields.Where(f => f.IsStatic && f.HasConstant);
        }
    }
}
