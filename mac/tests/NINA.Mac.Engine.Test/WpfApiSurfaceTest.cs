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
using Mono.Cecil;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NINA.Mac.Engine.Test {

    /// <summary>
    /// Every public type in NINA.Mac.WpfCompat must exist in WPF under the same name, and every public member it declares
    /// must exist on the WPF type with the same signature: the compat layer may leave WPF API out, never invent any.
    /// Enum members must carry WPF's values. Checked against the WPF metadata in the WindowsDesktop runtime pack.
    /// </summary>
    [TestFixture]
    public class WpfApiSurfaceTest {
        private static readonly string[] wpfAssemblies = { "WindowsBase.dll", "PresentationCore.dll", "PresentationFramework.dll", "System.Xaml.dll" };
        private Dictionary<string, TypeDefinition> wpfTypes;

        [OneTimeSetUp]
        public void ReadWpfMetadata() {
            wpfTypes = new Dictionary<string, TypeDefinition>();
            foreach (var file in wpfAssemblies) {
                var module = WpfOracle.ReadModule(file);
                foreach (var type in module.Types.Where(t => t.IsPublic)) {
                    wpfTypes[type.FullName] = type;
                }
            }
        }

        private static IEnumerable<Type> CompatTypes() {
            return typeof(System.Windows.Application).Assembly.GetExportedTypes();
        }

        [Test]
        public void EveryCompatTypeExistsInWpf_WithTheSameKind() {
            foreach (var type in CompatTypes()) {
                wpfTypes.Should().ContainKey(type.FullName, "the compat layer must not invent types");
                var wpf = wpfTypes[type.FullName];
                type.IsValueType.Should().Be(wpf.IsValueType, type.FullName);
                type.IsEnum.Should().Be(wpf.IsEnum, type.FullName);
                type.IsInterface.Should().Be(wpf.IsInterface, type.FullName);
                if (wpf.IsSealed && !wpf.IsEnum && !wpf.IsValueType) {
                    type.IsSealed.Should().BeTrue($"{type.FullName} is sealed in WPF");
                }
                if (wpf.IsAbstract && !wpf.IsSealed) {
                    type.IsAbstract.Should().BeTrue($"{type.FullName} is abstract in WPF");
                }
            }
        }

        [Test]
        public void EveryCompatMemberExistsInWpf_WithTheSameSignature() {
            const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var missing = new List<string>();
            foreach (var type in CompatTypes().Where(t => !t.IsEnum)) {
                var wpf = wpfTypes[type.FullName];
                foreach (var member in type.GetMembers(declared)) {
                    if (!ExistsInWpf(member, wpf)) {
                        missing.Add($"{type.FullName}.{member}");
                    }
                }
            }
            missing.Should().BeEmpty("the compat layer must only contain WPF API");
        }

        [Test]
        public void EnumValuesMatchWpf() {
            foreach (var type in CompatTypes().Where(t => t.IsEnum)) {
                var wpf = wpfTypes[type.FullName].Fields.Where(f => f.IsStatic && f.HasConstant)
                    .ToDictionary(f => f.Name, f => Convert.ToInt64(f.Constant));
                var compat = Enum.GetNames(type).ToDictionary(n => n, n => Convert.ToInt64(Enum.Parse(type, n)));
                compat.Should().BeEquivalentTo(wpf, type.FullName);
            }
        }

        private static bool ExistsInWpf(MemberInfo member, TypeDefinition wpf) {
            switch (member) {
                case ConstructorInfo ctor:
                    return wpf.Methods.Any(m => m.IsConstructor && m.IsStatic == ctor.IsStatic && (m.IsPublic || m.IsFamily) && SameParameters(m, ctor.GetParameters()));
                case MethodInfo method when method.IsSpecialName && (method.Name.StartsWith("get_") || method.Name.StartsWith("set_") || method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")):
                    return true; // covered by the property/event checks
                case MethodInfo method:
                    return FindInHierarchy(wpf, t => t.Methods.Any(m => m.Name == method.Name && m.IsStatic == method.IsStatic && (m.IsPublic || m.IsFamily || IsExplicitImplementation(m, method))
                        && Same(m.ReturnType, method.ReturnType) && SameParameters(m, method.GetParameters())))
                        || method.IsVirtual && method.GetBaseDefinition().DeclaringType != method.DeclaringType;
                case PropertyInfo property:
                    return FindInHierarchy(wpf, t => t.Properties.Any(p => p.Name == property.Name && Same(p.PropertyType, property.PropertyType)
                        && (property.GetGetMethod() == null || p.GetMethod?.IsPublic == true)
                        && (property.GetSetMethod() == null || p.SetMethod?.IsPublic == true)
                        && SameParameters(p, property.GetIndexParameters())));
                case EventInfo evt:
                    return FindInHierarchy(wpf, t => t.Events.Any(e => e.Name == evt.Name && Same(e.EventType, evt.EventHandlerType)));
                case FieldInfo field:
                    return FindInHierarchy(wpf, t => t.Fields.Any(f => f.Name == field.Name && f.IsPublic && Same(f.FieldType, field.FieldType)));
                case Type nested:
                    return wpf.NestedTypes.Any(n => n.Name == nested.Name && n.IsNestedPublic);
                default:
                    return false;
            }
        }

        private static bool IsExplicitImplementation(MethodDefinition wpf, MethodInfo compat) {
            return wpf.IsPrivate && wpf.Name.EndsWith("." + compat.Name, StringComparison.Ordinal);
        }

        private static bool FindInHierarchy(TypeDefinition type, Func<TypeDefinition, bool> predicate) {
            for (var current = type; current != null; current = current.BaseType?.Resolve()) {
                if (predicate(current)) {
                    return true;
                }
            }
            return false;
        }

        private static bool SameParameters(MethodReference method, ParameterInfo[] parameters) {
            return method.Parameters.Count == parameters.Length
                && method.Parameters.Select((p, i) => Same(p.ParameterType, parameters[i].ParameterType)).All(x => x);
        }

        private static bool SameParameters(PropertyDefinition property, ParameterInfo[] parameters) {
            return property.Parameters.Count == parameters.Length
                && property.Parameters.Select((p, i) => Same(p.ParameterType, parameters[i].ParameterType)).All(x => x);
        }

        private static bool Same(TypeReference wpf, Type compat) {
            return Normalize(wpf.FullName) == Normalize(compat.FullName ?? compat.Name);
        }

        private static string Normalize(string name) {
            // Cecil writes nested types and generic instances differently from reflection
            name = name.Replace('/', '+');
            var tick = name.IndexOf('[');
            return tick > 0 && name.Contains('`') ? name.Substring(0, tick) : name;
        }
    }
}
