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
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using Shim = System.Windows.Media;

namespace NINA.Mac.Engine.Test {

    /// <summary>Compat System.Windows.Media.Color / Colors / ColorConverter against the real WPF implementation.</summary>
    [TestFixture]
    public class ColorOracleTest {
        private Type wpfColor;
        private Type wpfColors;
        private Type wpfConverter;

        private static readonly CultureInfo[] cultures = { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("en-US") };

        [OneTimeSetUp]
        public void LoadWpf() {
            wpfColor = WpfOracle.GetType("PresentationCore", "System.Windows.Media.Color");
            wpfColors = WpfOracle.GetType("PresentationCore", "System.Windows.Media.Colors");
            wpfConverter = WpfOracle.GetType("PresentationCore", "System.Windows.Media.ColorConverter");
        }

        private static IEnumerable<byte[]> ArgbSamples() {
            var random = new Random(4711);
            for (var v = 0; v < 256; v++) {
                yield return new[] { (byte)v, (byte)v, (byte)(255 - v), (byte)(v * 7) };
                yield return new[] { (byte)255, (byte)v, (byte)0, (byte)255 };
            }
            for (var i = 0; i < 2000; i++) {
                var bytes = new byte[4];
                random.NextBytes(bytes);
                yield return bytes;
            }
        }

        private static IEnumerable<float> ScSamples() {
            var specials = new[] { -1f, -0.0f, 0f, 1e-8f, 0.001f, 0.0031308f, 0.0031309f, 0.04045f, 0.2f, 0.5f, 0.999999f, 1f, 1.0000001f, 2f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
            foreach (var s in specials) {
                yield return s;
            }
            var random = new Random(1234);
            for (var i = 0; i < 300; i++) {
                yield return (float)((random.NextDouble() * 2.0) - 0.5);
            }
        }

        private string DescribeWpf(object color) {
            var sb = new StringBuilder();
            foreach (var name in new[] { "A", "R", "G", "B" }) {
                sb.Append(wpfColor.GetProperty(name).GetValue(color)).Append(',');
            }
            foreach (var name in new[] { "ScA", "ScR", "ScG", "ScB" }) {
                sb.Append(BitConverter.SingleToInt32Bits((float)wpfColor.GetProperty(name).GetValue(color))).Append(',');
            }
            sb.Append(color.ToString()).Append('|');
            foreach (var culture in cultures) {
                sb.Append(wpfColor.GetMethod("ToString", new[] { typeof(IFormatProvider) }).Invoke(color, new object[] { culture })).Append('|');
                sb.Append(((IFormattable)color).ToString(null, culture)).Append('|');
                sb.Append(((IFormattable)color).ToString("F3", culture)).Append('|');
            }
            return sb.ToString();
        }

        private static string DescribeShim(Shim.Color color) {
            var sb = new StringBuilder();
            sb.Append(color.A).Append(',').Append(color.R).Append(',').Append(color.G).Append(',').Append(color.B).Append(',');
            foreach (var value in new[] { color.ScA, color.ScR, color.ScG, color.ScB }) {
                sb.Append(BitConverter.SingleToInt32Bits(value)).Append(',');
            }
            sb.Append(color.ToString()).Append('|');
            foreach (var culture in cultures) {
                sb.Append(color.ToString(culture)).Append('|');
                sb.Append(((IFormattable)color).ToString(null, culture)).Append('|');
                sb.Append(((IFormattable)color).ToString("F3", culture)).Append('|');
            }
            return sb.ToString();
        }

        private object WpfFromArgb(byte[] v) {
            return WpfOracle.Invoke(wpfColor, "FromArgb", v[0], v[1], v[2], v[3]);
        }

        [Test]
        public void FromArgb_FromRgb_MatchWpf() {
            foreach (var v in ArgbSamples()) {
                DescribeShim(Shim.Color.FromArgb(v[0], v[1], v[2], v[3])).Should().Be(DescribeWpf(WpfFromArgb(v)), $"FromArgb({string.Join(",", v)})");
                DescribeShim(Shim.Color.FromRgb(v[1], v[2], v[3])).Should().Be(DescribeWpf(WpfOracle.Invoke(wpfColor, "FromRgb", v[1], v[2], v[3])));
            }
        }

        [Test]
        public void FromScRgb_MatchesWpf() {
            var samples = ScSamples().ToArray();
            for (var i = 0; i < samples.Length; i++) {
                var a = samples[i];
                var r = samples[(i * 7 + 1) % samples.Length];
                var g = samples[(i * 13 + 2) % samples.Length];
                var b = samples[(i * 31 + 3) % samples.Length];
                DescribeShim(Shim.Color.FromScRgb(a, r, g, b)).Should().Be(DescribeWpf(WpfOracle.Invoke(wpfColor, "FromScRgb", a, r, g, b)), $"FromScRgb({a:R},{r:R},{g:R},{b:R})");
                DescribeShim(Shim.Color.FromScRgb(r, r, r, r)).Should().Be(DescribeWpf(WpfOracle.Invoke(wpfColor, "FromScRgb", r, r, r, r)), $"FromScRgb({r:R} x4)");
            }
        }

        [Test]
        public void PropertySetters_MatchWpf() {
            var bytes = ArgbSamples().Take(600).ToArray();
            var floats = ScSamples().ToArray();
            for (var i = 0; i < bytes.Length; i++) {
                var start = bytes[i];
                var value = bytes[(i * 17 + 5) % bytes.Length][1];
                var f = floats[i % floats.Length];
                foreach (var name in new[] { "A", "R", "G", "B" }) {
                    object wpf = WpfFromArgb(start);
                    wpfColor.GetProperty(name).SetValue(wpf, value);
                    object shim = Shim.Color.FromArgb(start[0], start[1], start[2], start[3]);
                    typeof(Shim.Color).GetProperty(name).SetValue(shim, value);
                    DescribeShim((Shim.Color)shim).Should().Be(DescribeWpf(wpf), $"{name} = {value} on {string.Join(",", start)}");
                }
                foreach (var name in new[] { "ScA", "ScR", "ScG", "ScB" }) {
                    object wpf = WpfFromArgb(start);
                    wpfColor.GetProperty(name).SetValue(wpf, f);
                    object shim = Shim.Color.FromArgb(start[0], start[1], start[2], start[3]);
                    typeof(Shim.Color).GetProperty(name).SetValue(shim, f);
                    DescribeShim((Shim.Color)shim).Should().Be(DescribeWpf(wpf), $"{name} = {f:R} on {string.Join(",", start)}");
                }
            }
        }

        [Test]
        public void DefaultColor_MatchesWpf() {
            DescribeShim(default).Should().Be(DescribeWpf(Activator.CreateInstance(wpfColor)));
        }

        [Test]
        public void Equality_MatchesWpf() {
            var shims = new List<Shim.Color>();
            var wpfs = new List<object>();
            foreach (var v in ArgbSamples().Take(40)) {
                shims.Add(Shim.Color.FromArgb(v[0], v[1], v[2], v[3]));
                wpfs.Add(WpfFromArgb(v));
            }
            foreach (var f in new[] { 0f, 0.5f, 1f, float.NaN, 0.21586053f }) {
                shims.Add(Shim.Color.FromScRgb(1f, f, f, f));
                wpfs.Add(WpfOracle.Invoke(wpfColor, "FromScRgb", 1f, f, f, f));
            }
            shims.Add(Shim.Color.FromArgb(255, 128, 128, 128));
            wpfs.Add(WpfOracle.Invoke(wpfColor, "FromArgb", (byte)255, (byte)128, (byte)128, (byte)128));
            shims.Add(default);
            wpfs.Add(Activator.CreateInstance(wpfColor));

            var opEquality = wpfColor.GetMethod("op_Equality");
            var opInequality = wpfColor.GetMethod("op_Inequality");
            for (var i = 0; i < shims.Count; i++) {
                for (var j = 0; j < shims.Count; j++) {
                    (shims[i] == shims[j]).Should().Be((bool)opEquality.Invoke(null, new[] { wpfs[i], wpfs[j] }), $"{shims[i]} == {shims[j]}");
                    (shims[i] != shims[j]).Should().Be((bool)opInequality.Invoke(null, new[] { wpfs[i], wpfs[j] }));
                    shims[i].Equals((object)shims[j]).Should().Be(wpfs[i].Equals(wpfs[j]), $"{shims[i]}.Equals({shims[j]})");
                    if (shims[i] == shims[j]) {
                        shims[i].GetHashCode().Should().Be(shims[j].GetHashCode());
                    }
                }
                shims[i].Equals(null).Should().Be(wpfs[i].Equals(null));
                shims[i].Equals((object)42).Should().Be(wpfs[i].Equals(42));
            }
        }

        [Test]
        public void DataContractSerializer_WritesTheSameXmlAsWpf_AndReadsWpfXml() {
            var shimSerializer = new DataContractSerializer(typeof(Shim.Color));
            var wpfSerializer = new DataContractSerializer(wpfColor);
            var cases = new List<(Shim.Color shim, object wpf)>();
            foreach (var v in ArgbSamples().Take(300)) {
                cases.Add((Shim.Color.FromArgb(v[0], v[1], v[2], v[3]), WpfFromArgb(v)));
            }
            foreach (var f in ScSamples().Take(40)) {
                cases.Add((Shim.Color.FromScRgb(0.75f, f, 0.5f, 1f), WpfOracle.Invoke(wpfColor, "FromScRgb", 0.75f, f, 0.5f, 1f)));
            }
            cases.Add((default, Activator.CreateInstance(wpfColor)));

            foreach (var (shim, wpf) in cases) {
                var wpfXml = Serialize(wpfSerializer, wpf);
                Serialize(shimSerializer, shim).Should().Be(wpfXml);

                var shimBack = (Shim.Color)Deserialize(shimSerializer, wpfXml);
                var wpfBack = Deserialize(wpfSerializer, wpfXml);
                DescribeShim(shimBack).Should().Be(DescribeWpf(wpfBack), wpfXml);
            }
        }

        private static string Serialize(DataContractSerializer serializer, object value) {
            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb)) {
                serializer.WriteObject(writer, value);
            }
            return sb.ToString();
        }

        private static object Deserialize(DataContractSerializer serializer, string xml) {
            using var reader = XmlReader.Create(new StringReader(xml));
            return serializer.ReadObject(reader);
        }

        private static readonly string[] parseInputs = {
            "#FF000000", "#AABCBCBC", "#00ffffff", "#80123456", "#123456", "#F00", " #f00 ", "#8F00", "#ABCD", "\t#12345678\n",
            "Red", "red", "  AliceBlue ", "transparent", "YELLOWGREEN", "Grey", "RebeccaPurple", "NoSuchColor", "",
            "#", "#1", "#12", "#12345", "#1234567", "#123456789", "#GG0000", "#12 456",
            "sc#0.5,0.25,0.1,1", "sc# 0.5 0.25 0.1", "sc#0.5, 0.25,0.1", "sc#1,2", "sc#1,,2,3", "sc#1,2,3,", "sc#,1,2,3",
            "sc#1,2,3,4,5", "sc#abc,1,2", "sc#1e-3,2,3", "SC#1,2,3"
        };

        [Test]
        public void ConvertFromString_MatchesWpf() {
            var wpfParse = wpfConverter.GetMethod("ConvertFromString", BindingFlags.Public | BindingFlags.Static);
            foreach (var input in parseInputs) {
                var wpf = Outcome(() => wpfParse.Invoke(null, new object[] { input }), true);
                var shim = Outcome(() => Shim.ColorConverter.ConvertFromString(input), false);
                shim.Should().Be(wpf, $"ConvertFromString(\"{input}\")");
            }
            Shim.ColorConverter.ConvertFromString(null).Should().BeNull();
            wpfParse.Invoke(null, new object[] { null }).Should().BeNull();
        }

        [Test]
        public void TypeConverter_MatchesWpf() {
            var wpf = (TypeConverter)Activator.CreateInstance(wpfConverter);
            var shim = new Shim.ColorConverter();
            TypeDescriptor.GetConverter(typeof(Shim.Color)).Should().BeOfType<Shim.ColorConverter>();
            foreach (var type in new[] { typeof(string), typeof(int), typeof(System.ComponentModel.Design.Serialization.InstanceDescriptor) }) {
                shim.CanConvertFrom(type).Should().Be(wpf.CanConvertFrom(type), $"CanConvertFrom({type})");
                shim.CanConvertTo(type).Should().Be(wpf.CanConvertTo(type), $"CanConvertTo({type})");
            }
            foreach (var culture in cultures.Append(null)) {
                foreach (var input in new[] { "#80123456", "Red", "sc#0.5,0.25,0.1,1", "sc#0,5;0,25;0,1" }) {
                    var w = Outcome(() => wpf.ConvertFrom(null, culture, input), true);
                    var s = Outcome(() => shim.ConvertFrom(null, culture, input), false);
                    s.Should().Be(w, $"ConvertFrom(\"{input}\", {culture?.Name ?? "null"})");
                }
                var wpfSc = WpfOracle.Invoke(wpfColor, "FromScRgb", 0.5f, 0.25f, 0.1f, 1f);
                var wpfArgb = WpfOracle.Invoke(wpfColor, "FromArgb", (byte)1, (byte)2, (byte)3, (byte)4);
                shim.ConvertTo(null, culture, Shim.Color.FromScRgb(0.5f, 0.25f, 0.1f, 1f), typeof(string)).Should().Be(wpf.ConvertTo(null, culture, wpfSc, typeof(string)));
                shim.ConvertTo(null, culture, Shim.Color.FromArgb(1, 2, 3, 4), typeof(string)).Should().Be(wpf.ConvertTo(null, culture, wpfArgb, typeof(string)));
            }
            Outcome(() => shim.ConvertFrom(null, null, null), false).Should().Be(Outcome(() => wpf.ConvertFrom(null, null, null), true));
            Outcome(() => shim.ConvertFrom(null, null, 5), false).Split(':')[0].Should().Be(Outcome(() => wpf.ConvertFrom(null, null, 5), true).Split(':')[0]);
        }

        private string Outcome(Func<object> call, bool isWpf) {
            try {
                var result = WpfOracle.Unwrap(call);
                return result == null ? "null" : "ok " + (isWpf ? DescribeWpf(result) : DescribeShim((Shim.Color)result));
            } catch (Exception ex) {
                return $"{ex.GetType().FullName}: {ex.Message}";
            }
        }

        [Test]
        public void NamedColors_MatchWpf() {
            var wpfNamed = wpfColors.GetProperties(BindingFlags.Public | BindingFlags.Static).ToDictionary(p => p.Name, p => DescribeWpf(p.GetValue(null)));
            var shimNamed = typeof(Shim.Colors).GetProperties(BindingFlags.Public | BindingFlags.Static).ToDictionary(p => p.Name, p => DescribeShim((Shim.Color)p.GetValue(null)));
            wpfNamed.Should().HaveCount(141);
            shimNamed.Should().BeEquivalentTo(wpfNamed);
        }
    }
}
