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
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Shim3D = System.Windows.Media.Media3D;
using ShimWindows = System.Windows;

namespace NINA.Mac.Engine.Test {

    /// <summary>
    /// Compat System.Windows.Point / Vector and System.Windows.Media.Media3D.Vector3D (used by NINA.Astrometry) against the
    /// real WPF structs: component round trip, setters, equality (operators and Equals, including NaN and -0.0), hash codes
    /// and every ToString form in cultures with '.' and ',' decimal separators.
    /// </summary>
    [TestFixture]
    public class GeometryOracleTest {
        private Type wpfPoint;
        private Type wpfVector;
        private Type wpfVector3D;

        private static readonly CultureInfo[] cultures = {
            CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("fr-FR")
        };

        private static readonly string[] formats = { null, "F3", "R", "E2" };

        [OneTimeSetUp]
        public void LoadWpf() {
            wpfPoint = WpfOracle.GetType("WindowsBase", "System.Windows.Point");
            wpfVector = WpfOracle.GetType("WindowsBase", "System.Windows.Vector");
            wpfVector3D = WpfOracle.GetType("PresentationCore", "System.Windows.Media.Media3D.Vector3D");
        }

        private static double[] Samples() {
            var values = new List<double> {
                0.0, -0.0, 1.0, -1.0, 0.5, 1e-300, -1e300, double.Epsilon, double.MaxValue, double.MinValue,
                double.NaN, double.PositiveInfinity, double.NegativeInfinity, 123.456, -7.25e-5, 1920.0, 1080.5, 89.711767695042226
            };
            var random = new Random(2026);
            for (var i = 0; i < 30; i++) {
                values.Add((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-8, 9)));
            }
            return values.ToArray();
        }

        private static string Describe(object value, Type type, string[] components) {
            var sb = new StringBuilder();
            foreach (var name in components) {
                sb.Append(BitConverter.DoubleToInt64Bits((double)type.GetProperty(name).GetValue(value))).Append(',');
            }
            sb.Append(value.GetHashCode()).Append('|').Append(value).Append('|');
            foreach (var culture in cultures) {
                sb.Append(type.GetMethod("ToString", new[] { typeof(IFormatProvider) }).Invoke(value, new object[] { culture })).Append('|');
                foreach (var format in formats) {
                    sb.Append(((IFormattable)value).ToString(format, culture)).Append('|');
                }
            }
            return sb.ToString();
        }

        private static IEnumerable<(object shim, object wpf)> Pairs(Type shimType, Type wpfType, int arity) {
            var samples = Samples();
            for (var i = 0; i < samples.Length; i++) {
                var args = Enumerable.Range(0, arity).Select(k => (object)samples[(i * (k * 7 + 1) + k) % samples.Length]).ToArray();
                yield return (Activator.CreateInstance(shimType, args), Activator.CreateInstance(wpfType, args));
            }
            yield return (Activator.CreateInstance(shimType), Activator.CreateInstance(wpfType));
        }

        private void ValuesMatchWpf(Type shimType, Type wpfType, string[] components) {
            foreach (var (shim, wpf) in Pairs(shimType, wpfType, components.Length)) {
                Describe(shim, shimType, components).Should().Be(Describe(wpf, wpfType, components));
            }
        }

        private void SettersMatchWpf(Type shimType, Type wpfType, string[] components) {
            var samples = Samples();
            foreach (var (shim, wpf) in Pairs(shimType, wpfType, components.Length).Take(12)) {
                foreach (var name in components) {
                    foreach (var value in samples.Take(14)) {
                        object s = shim;
                        object w = wpf;
                        shimType.GetProperty(name).SetValue(s, value);
                        wpfType.GetProperty(name).SetValue(w, value);
                        Describe(s, shimType, components).Should().Be(Describe(w, wpfType, components), $"{name} = {value:R}");
                    }
                }
            }
        }

        private void EqualityMatchesWpf(Type shimType, Type wpfType, int arity) {
            var pairs = Pairs(shimType, wpfType, arity).ToList();
            // Equal-but-distinct cases: -0.0 vs 0.0, NaN vs NaN
            pairs.Add((Activator.CreateInstance(shimType, Enumerable.Repeat((object)(-0.0), arity).ToArray()), Activator.CreateInstance(wpfType, Enumerable.Repeat((object)(-0.0), arity).ToArray())));
            pairs.Add((Activator.CreateInstance(shimType, Enumerable.Repeat((object)double.NaN, arity).ToArray()), Activator.CreateInstance(wpfType, Enumerable.Repeat((object)double.NaN, arity).ToArray())));

            var shimEquality = shimType.GetMethod("op_Equality");
            var shimInequality = shimType.GetMethod("op_Inequality");
            var shimStaticEquals = shimType.GetMethod("Equals", new[] { shimType, shimType });
            var shimTypedEquals = shimType.GetMethod("Equals", new[] { shimType });
            var wpfEquality = wpfType.GetMethod("op_Equality");
            var wpfInequality = wpfType.GetMethod("op_Inequality");
            var wpfStaticEquals = wpfType.GetMethod("Equals", new[] { wpfType, wpfType });
            var wpfTypedEquals = wpfType.GetMethod("Equals", new[] { wpfType });
            foreach (var (s1, w1) in pairs) {
                foreach (var (s2, w2) in pairs) {
                    var because = $"{s1} vs {s2}";
                    ((bool)shimEquality.Invoke(null, new[] { s1, s2 })).Should().Be((bool)wpfEquality.Invoke(null, new[] { w1, w2 }), because);
                    ((bool)shimInequality.Invoke(null, new[] { s1, s2 })).Should().Be((bool)wpfInequality.Invoke(null, new[] { w1, w2 }), because);
                    ((bool)shimStaticEquals.Invoke(null, new[] { s1, s2 })).Should().Be((bool)wpfStaticEquals.Invoke(null, new[] { w1, w2 }), because);
                    ((bool)shimTypedEquals.Invoke(s1, new[] { s2 })).Should().Be((bool)wpfTypedEquals.Invoke(w1, new[] { w2 }), because);
                    s1.Equals(s2).Should().Be(w1.Equals(w2), because);
                }
                s1.Equals(null).Should().Be(w1.Equals(null));
                s1.Equals(42.0).Should().Be(w1.Equals(42.0));
            }
        }

        private static readonly string[] xy = { "X", "Y" };
        private static readonly string[] xyz = { "X", "Y", "Z" };

        [Test]
        public void Point_MatchesWpf() {
            ValuesMatchWpf(typeof(ShimWindows.Point), wpfPoint, xy);
            SettersMatchWpf(typeof(ShimWindows.Point), wpfPoint, xy);
            EqualityMatchesWpf(typeof(ShimWindows.Point), wpfPoint, 2);
        }

        [Test]
        public void Vector_MatchesWpf() {
            ValuesMatchWpf(typeof(ShimWindows.Vector), wpfVector, xy);
            SettersMatchWpf(typeof(ShimWindows.Vector), wpfVector, xy);
            EqualityMatchesWpf(typeof(ShimWindows.Vector), wpfVector, 2);
        }

        [Test]
        public void Vector3D_MatchesWpf() {
            ValuesMatchWpf(typeof(Shim3D.Vector3D), wpfVector3D, xyz);
            SettersMatchWpf(typeof(Shim3D.Vector3D), wpfVector3D, xyz);
            EqualityMatchesWpf(typeof(Shim3D.Vector3D), wpfVector3D, 3);
        }

        [Test]
        public void ToString_UsesTheCurrentCulture_LikeWpf() {
            var previous = CultureInfo.CurrentCulture;
            try {
                foreach (var culture in cultures) {
                    CultureInfo.CurrentCulture = culture;
                    new ShimWindows.Point(1.5, -2.25).ToString().Should().Be(Activator.CreateInstance(wpfPoint, 1.5, -2.25).ToString());
                    new Shim3D.Vector3D(1.5, -2.25, 3e-7).ToString().Should().Be(Activator.CreateInstance(wpfVector3D, 1.5, -2.25, 3e-7).ToString());
                }
            } finally {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }
}
