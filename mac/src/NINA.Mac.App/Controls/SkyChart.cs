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
using Avalonia.Media;
using NINA.Mac.App.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NINA.Mac.App.Controls {

    /// <summary>
    /// The Target screen's sky view: a panorama of the site from azimuth 0° (N) through E, S, W back to N, altitude 0-90°.
    /// Draws the local horizon (the blocked sky below it filled), the zenith keyhole above the maximum altitude, the minimum
    /// altitude, the selected target's path over the rest of the night with its position now, and where the mount points.
    /// No charting dependency; colours come from the theme (styles in AppStyles), so night vision recolours it.
    /// </summary>
    public sealed class SkyChart : Control {

        public static readonly StyledProperty<HorizonProfile> HorizonProperty =
            AvaloniaProperty.Register<SkyChart, HorizonProfile>(nameof(Horizon));

        public static readonly StyledProperty<double> MaxAltitudeProperty =
            AvaloniaProperty.Register<SkyChart, double>(nameof(MaxAltitude), 75);

        public static readonly StyledProperty<double> MinAltitudeProperty =
            AvaloniaProperty.Register<SkyChart, double>(nameof(MinAltitude), 20);

        /// <summary>The target's path as (azimuth, altitude) points in time order; points below 0° are skipped.</summary>
        public static readonly StyledProperty<IReadOnlyList<Point>> TrackProperty =
            AvaloniaProperty.Register<SkyChart, IReadOnlyList<Point>>(nameof(Track));

        /// <summary>The target now as (azimuth, altitude); null when there is none.</summary>
        public static readonly StyledProperty<Point?> TargetProperty =
            AvaloniaProperty.Register<SkyChart, Point?>(nameof(Target));

        /// <summary>Where the mount points as (azimuth, altitude); null when it is not connected.</summary>
        public static readonly StyledProperty<Point?> MountProperty =
            AvaloniaProperty.Register<SkyChart, Point?>(nameof(Mount));

        public static readonly StyledProperty<IBrush> StrokeProperty =
            AvaloniaProperty.Register<SkyChart, IBrush>(nameof(Stroke), Brushes.Gray);

        public static readonly StyledProperty<IBrush> LabelBrushProperty =
            AvaloniaProperty.Register<SkyChart, IBrush>(nameof(LabelBrush), Brushes.Gray);

        public static readonly StyledProperty<IBrush> BlockedBrushProperty =
            AvaloniaProperty.Register<SkyChart, IBrush>(nameof(BlockedBrush), Brushes.DimGray);

        public static readonly StyledProperty<IBrush> LimitBrushProperty =
            AvaloniaProperty.Register<SkyChart, IBrush>(nameof(LimitBrush), Brushes.Orange);

        public static readonly StyledProperty<IBrush> MountBrushProperty =
            AvaloniaProperty.Register<SkyChart, IBrush>(nameof(MountBrush), Brushes.LightGreen);

        static SkyChart() {
            AffectsRender<SkyChart>(HorizonProperty, MaxAltitudeProperty, MinAltitudeProperty, TrackProperty, TargetProperty, MountProperty,
                StrokeProperty, LabelBrushProperty, BlockedBrushProperty, LimitBrushProperty, MountBrushProperty);
        }

        public HorizonProfile Horizon {
            get => GetValue(HorizonProperty);
            set => SetValue(HorizonProperty, value);
        }

        public double MaxAltitude {
            get => GetValue(MaxAltitudeProperty);
            set => SetValue(MaxAltitudeProperty, value);
        }

        public double MinAltitude {
            get => GetValue(MinAltitudeProperty);
            set => SetValue(MinAltitudeProperty, value);
        }

        public IReadOnlyList<Point> Track {
            get => GetValue(TrackProperty);
            set => SetValue(TrackProperty, value);
        }

        public Point? Target {
            get => GetValue(TargetProperty);
            set => SetValue(TargetProperty, value);
        }

        public Point? Mount {
            get => GetValue(MountProperty);
            set => SetValue(MountProperty, value);
        }

        public IBrush Stroke {
            get => GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public IBrush LabelBrush {
            get => GetValue(LabelBrushProperty);
            set => SetValue(LabelBrushProperty, value);
        }

        public IBrush BlockedBrush {
            get => GetValue(BlockedBrushProperty);
            set => SetValue(BlockedBrushProperty, value);
        }

        public IBrush LimitBrush {
            get => GetValue(LimitBrushProperty);
            set => SetValue(LimitBrushProperty, value);
        }

        public IBrush MountBrush {
            get => GetValue(MountBrushProperty);
            set => SetValue(MountBrushProperty, value);
        }

        public override void Render(DrawingContext context) {
            base.Render(context);
            var bounds = new Rect(Bounds.Size);
            var plot = new Rect(30, 6, Math.Max(10, bounds.Width - 38), Math.Max(10, bounds.Height - 26));
            Point Map(double az, double alt) => new(plot.Left + (Math.Clamp(az, 0, 360) / 360.0 * plot.Width), plot.Bottom - (Math.Clamp(alt, 0, 90) / 90.0 * plot.Height));

            var grid = new Pen(LabelBrush, 0.5, new DashStyle(new double[] { 2, 4 }, 0));
            foreach (var alt in new[] { 30.0, 60.0 }) {
                context.DrawLine(grid, Map(0, alt), Map(360, alt));
                DrawText(context, $"{alt:0}°", new Point(0, Map(0, alt).Y - 7));
            }
            foreach (var (az, label) in new[] { (0.0, "N"), (90.0, "E"), (180.0, "S"), (270.0, "W"), (360.0, "N") }) {
                var p = Map(az, 0);
                context.DrawLine(grid, p, Map(az, 90));
                DrawText(context, label, new Point(p.X - 4, plot.Bottom + 3));
            }

            // Keyhole: above the maximum altitude the fork cannot track
            var keyhole = new Rect(Map(0, 90), Map(360, MaxAltitude));
            context.DrawRectangle(null, new Pen(LimitBrush, 1, new DashStyle(new double[] { 6, 3 }, 0)), keyhole);
            context.DrawLine(new Pen(LimitBrush, 1, new DashStyle(new double[] { 2, 3 }, 0)), Map(0, MinAltitude), Map(360, MinAltitude));

            // Local horizon: the sky below it is blocked (filled with the label colour, faint, so night vision keeps it red)
            if (Horizon is { } horizon) {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open()) {
                    g.BeginFigure(Map(0, 0), true);
                    for (var az = 0; az <= 360; az += 2) {
                        g.LineTo(Map(az, horizon.GetAltitude(az)));
                    }
                    g.LineTo(Map(360, 0));
                    g.EndFigure(true);
                }
                var fill = LabelBrush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.25) : BlockedBrush;
                context.DrawGeometry(fill, new Pen(LabelBrush, 1.5), geometry);
                foreach (var p in horizon.Points) {
                    context.DrawEllipse(LabelBrush, null, Map(p.Azimuth, p.Altitude), 2.5, 2.5);
                }
            }
            context.DrawRectangle(null, new Pen(LabelBrush, 1), plot);
            // Limit labels last, over the open southern sky, so the horizon fill never hides them
            DrawText(context, $"keyhole above {MaxAltitude:0}°", new Point(Map(150, 0).X, keyhole.Bottom + 2), LimitBrush);
            DrawText(context, $"min {MinAltitude:0}°", new Point(Map(150, 0).X, Map(0, MinAltitude).Y - 14), LimitBrush);

            // The target's path over the rest of the night (split where it wraps through north)
            if (Track is { Count: > 1 } track) {
                var pen = new Pen(Stroke, 1.5);
                Point? last = null;
                foreach (var p in track) {
                    if (p.Y <= 0) {
                        last = null;
                        continue;
                    }
                    if (last is { } l && Math.Abs(p.X - l.X) < 180) {
                        context.DrawLine(pen, Map(l.X, l.Y), Map(p.X, p.Y));
                    }
                    last = p;
                }
            }
            if (Target is { } t && t.Y > 0) {
                context.DrawEllipse(Stroke, null, Map(t.X, t.Y), 5, 5);
            }
            if (Mount is { } m) {
                var c = Map(m.X, m.Y);
                var pen = new Pen(MountBrush, 2);
                context.DrawLine(pen, new Point(c.X - 7, c.Y), new Point(c.X + 7, c.Y));
                context.DrawLine(pen, new Point(c.X, c.Y - 7), new Point(c.X, c.Y + 7));
            }
        }

        private void DrawText(DrawingContext context, string text, Point origin, IBrush brush = null) {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush ?? LabelBrush);
            context.DrawText(formatted, origin);
        }
    }
}
