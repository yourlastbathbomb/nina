#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using NINA.Mac.App.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Mac.App.Theming {

    /// <summary>The app's colour roles. Views use them as DynamicResource "&lt;Role&gt;Brush".</summary>
    public sealed record NightPalette(
        Color Background,
        Color Sidebar,
        Color Surface,
        Color SurfaceRaised,
        Color Border,
        Color Text,
        Color TextMuted,
        Color Accent,
        Color OnAccent,
        Color Ok,
        Color Busy,
        Color Warn,
        Color Error,
        Color Idle,
        Color BannerBackground,
        Color BannerText) {

        /// <summary>Dark, low-luminance default: no pure white, muted blue accent.</summary>
        public static NightPalette Dark { get; } = new(
            Background: Color.Parse("#0E1116"),
            Sidebar: Color.Parse("#0A0D11"),
            Surface: Color.Parse("#151A21"),
            SurfaceRaised: Color.Parse("#1C232C"),
            Border: Color.Parse("#28303A"),
            Text: Color.Parse("#C5CCD5"),
            TextMuted: Color.Parse("#7F8994"),
            Accent: Color.Parse("#4F7DA6"),
            OnAccent: Color.Parse("#EEF2F6"),
            Ok: Color.Parse("#3E9E68"),
            Busy: Color.Parse("#C49A2C"),
            Warn: Color.Parse("#C98A2C"),
            Error: Color.Parse("#C9534A"),
            Idle: Color.Parse("#4A525C"),
            BannerBackground: Color.Parse("#5A1E1A"),
            BannerText: Color.Parse("#F0D8D5"));

        /// <summary>Night vision: red on black only, so dark adaptation survives a glance at the screen.</summary>
        public static NightPalette NightVision { get; } = new(
            Background: Color.Parse("#000000"),
            Sidebar: Color.Parse("#000000"),
            Surface: Color.Parse("#0C0000"),
            SurfaceRaised: Color.Parse("#160000"),
            Border: Color.Parse("#2E0505"),
            Text: Color.Parse("#C0322A"),
            TextMuted: Color.Parse("#7A1E19"),
            Accent: Color.Parse("#8E1F18"),
            OnAccent: Color.Parse("#000000"),
            Ok: Color.Parse("#C0322A"),
            Busy: Color.Parse("#8E1F18"),
            Warn: Color.Parse("#E0453A"),
            Error: Color.Parse("#FF5A4E"),
            Idle: Color.Parse("#3A0A07"),
            BannerBackground: Color.Parse("#3A0000"),
            BannerText: Color.Parse("#FF5A4E"));

        public static readonly string[] Roles = {
            nameof(Background), nameof(Sidebar), nameof(Surface), nameof(SurfaceRaised), nameof(Border), nameof(Text), nameof(TextMuted),
            nameof(Accent), nameof(OnAccent), nameof(Ok), nameof(Busy), nameof(Warn), nameof(Error), nameof(Idle), nameof(BannerBackground), nameof(BannerText),
        };

        public Color Get(string role) => role switch {
            nameof(Background) => Background,
            nameof(Sidebar) => Sidebar,
            nameof(Surface) => Surface,
            nameof(SurfaceRaised) => SurfaceRaised,
            nameof(Border) => Border,
            nameof(Text) => Text,
            nameof(TextMuted) => TextMuted,
            nameof(Accent) => Accent,
            nameof(OnAccent) => OnAccent,
            nameof(Ok) => Ok,
            nameof(Busy) => Busy,
            nameof(Warn) => Warn,
            nameof(Error) => Error,
            nameof(Idle) => Idle,
            nameof(BannerBackground) => BannerBackground,
            nameof(BannerText) => BannerText,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        /// <summary>Writes this palette into a Fluent palette so stock controls (buttons, text boxes, lists) follow it.</summary>
        public void CopyTo(ColorPaletteResources p) {
            p.Accent = Accent;
            p.AltHigh = Background;
            p.AltLow = Background;
            p.AltMedium = Background;
            p.AltMediumHigh = Background;
            p.AltMediumLow = Background;
            p.BaseHigh = Text;
            p.BaseLow = Border;
            p.BaseMedium = TextMuted;
            p.BaseMediumHigh = Text;
            p.BaseMediumLow = TextMuted;
            p.ChromeAltLow = Text;
            p.ChromeBlackHigh = Color.Parse("#000000");
            p.ChromeBlackLow = Text;
            p.ChromeBlackMedium = Background;
            p.ChromeBlackMediumLow = Background;
            p.ChromeDisabledHigh = Border;
            p.ChromeDisabledLow = TextMuted;
            p.ChromeGray = TextMuted;
            p.ChromeHigh = TextMuted;
            p.ChromeLow = Surface;
            p.ChromeMedium = SurfaceRaised;
            p.ChromeMediumLow = Surface;
            p.ChromeWhite = Text;
            p.ErrorText = Error;
            p.ListLow = SurfaceRaised;
            p.ListMedium = Border;
            p.RegionColor = Background;
        }
    }

    /// <summary>
    /// Switches between the dark theme and red night vision at runtime.
    /// <para>
    /// FluentTheme.Palettes accepts only the Light and Dark variants (Avalonia 12.1 throws for a custom variant), and
    /// most Fluent control brushes are resolved from the palette once, so changing the palette later does not reach
    /// them. So the app stays on the Dark variant and, at start-up, every solid-colour brush the Fluent theme defines
    /// is shadowed by a mutable copy in Application.Resources (looked up before the theme). Night vision then
    /// recolours those copies in place (a luminance-preserving map to red) and the app's own role brushes from the
    /// hand-tuned <see cref="NightPalette.NightVision"/>. Every DynamicResource consumer follows immediately.
    /// </para>
    /// </summary>
    public sealed class ThemeManager : IThemeController {
        private readonly ColorPaletteResources fluentPalette;
        private readonly Dictionary<string, SolidColorBrush> roleBrushes = new();
        private readonly List<(SolidColorBrush Brush, Color Day)> fluentBrushes = new();

        public ThemeManager(Application app) {
            ArgumentNullException.ThrowIfNull(app);
            var fluent = app.Styles.OfType<FluentTheme>().FirstOrDefault()
                ?? throw new InvalidOperationException("App.axaml must include FluentTheme");
            app.RequestedThemeVariant = ThemeVariant.Dark;
            fluentPalette = new ColorPaletteResources();
            NightPalette.Dark.CopyTo(fluentPalette);
            fluent.Palettes[ThemeVariant.Dark] = fluentPalette;
            foreach (var role in NightPalette.Roles) {
                var brush = new SolidColorBrush(NightPalette.Dark.Get(role));
                roleBrushes[role] = brush;
                app.Resources[role + "Brush"] = brush;
            }
            ShadowFluentBrushes(app, fluent);
            Apply(false);
        }

        public bool IsNightVision { get; private set; }

        /// <summary>Number of Fluent brushes shadowed (diagnostics/tests).</summary>
        public int ShadowedBrushCount => fluentBrushes.Count;

        public NightPalette Current => IsNightVision ? NightPalette.NightVision : NightPalette.Dark;

        public void Apply(bool nightVision) {
            var palette = nightVision ? NightPalette.NightVision : NightPalette.Dark;
            palette.CopyTo(fluentPalette);
            foreach (var (role, brush) in roleBrushes) {
                brush.Color = palette.Get(role);
            }
            foreach (var (brush, day) in fluentBrushes) {
                brush.Color = nightVision ? ToNightRed(day) : day;
            }
            IsNightVision = nightVision;
        }

        /// <summary>Same perceived brightness, red only (keeps alpha). Dark adaptation is spared by red light.</summary>
        public static Color ToNightRed(Color day) {
            var luma = (0.2126 * day.R) + (0.7152 * day.G) + (0.0722 * day.B);
            var r = (byte)Math.Clamp(Math.Round(luma * 1.05), 0, 255);
            return Color.FromArgb(day.A, r, (byte)Math.Round(r * 0.12), (byte)Math.Round(r * 0.10));
        }

        private void ShadowFluentBrushes(Application app, FluentTheme fluent) {
            var keys = new HashSet<object>();
            Collect(fluent.Resources, keys, 0);
            foreach (var key in keys) {
                if (key is not string || app.Resources.ContainsKey(key)) {
                    continue;
                }
                if (app.TryGetResource(key, ThemeVariant.Dark, out var value) && value is ISolidColorBrush solid) {
                    var copy = new SolidColorBrush(solid.Color, solid.Opacity);
                    app.Resources[key] = copy;
                    fluentBrushes.Add((copy, solid.Color));
                }
            }
        }

        private static void Collect(IResourceProvider provider, HashSet<object> keys, int depth) {
            if (provider == null || depth > 8) {
                return;
            }
            if (provider is ResourceInclude include) {
                Collect(include.Loaded, keys, depth + 1);
                return;
            }
            if (provider is not IResourceDictionary dictionary) {
                return;
            }
            foreach (var key in dictionary.Keys) {
                keys.Add(key);
            }
            foreach (var merged in dictionary.MergedDictionaries) {
                Collect(merged, keys, depth + 1);
            }
            foreach (var (variant, themed) in dictionary.ThemeDictionaries) {
                if (variant == ThemeVariant.Dark || variant == ThemeVariant.Default) {
                    Collect(themed, keys, depth + 1);
                }
            }
        }
    }
}
