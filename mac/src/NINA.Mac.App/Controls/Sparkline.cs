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
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;

namespace NINA.Mac.App.Controls {

    /// <summary>
    /// Minimal line chart for temperature and HFR history (no charting dependency; OxyPlot's Avalonia 12 port is
    /// single-maintainer, research broad_verify_ui.md UI-11). Draws min/max labels and the latest value.
    /// </summary>
    public sealed class Sparkline : Control {

        public static readonly StyledProperty<IEnumerable<double>> ValuesProperty =
            AvaloniaProperty.Register<Sparkline, IEnumerable<double>>(nameof(Values));

        public static readonly StyledProperty<IBrush> StrokeProperty =
            AvaloniaProperty.Register<Sparkline, IBrush>(nameof(Stroke), Brushes.Gray);

        public static readonly StyledProperty<IBrush> LabelBrushProperty =
            AvaloniaProperty.Register<Sparkline, IBrush>(nameof(LabelBrush), Brushes.Gray);

        public static readonly StyledProperty<string> FormatProperty =
            AvaloniaProperty.Register<Sparkline, string>(nameof(Format), "0.0");

        static Sparkline() {
            AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, LabelBrushProperty, FormatProperty);
        }

        public IEnumerable<double> Values {
            get => GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        public IBrush Stroke {
            get => GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public IBrush LabelBrush {
            get => GetValue(LabelBrushProperty);
            set => SetValue(LabelBrushProperty, value);
        }

        public string Format {
            get => GetValue(FormatProperty);
            set => SetValue(FormatProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
            base.OnPropertyChanged(change);
            if (change.Property == ValuesProperty) {
                if (change.OldValue is INotifyCollectionChanged oldCollection) {
                    oldCollection.CollectionChanged -= OnCollectionChanged;
                }
                if (change.NewValue is INotifyCollectionChanged newCollection) {
                    newCollection.CollectionChanged += OnCollectionChanged;
                }
            }
        }

        public override void Render(DrawingContext context) {
            base.Render(context);
            var values = Values?.ToArray() ?? System.Array.Empty<double>();
            var bounds = new Rect(Bounds.Size);
            var labelWidth = 44.0;
            var plot = new Rect(labelWidth, 6, System.Math.Max(0, bounds.Width - labelWidth - 6), System.Math.Max(0, bounds.Height - 12));
            var pen = new Pen(Stroke, 2);
            if (values.Length == 0) {
                DrawText(context, "no data yet", new Point(labelWidth, (bounds.Height / 2) - 7));
                return;
            }
            var min = values.Min();
            var max = values.Max();
            if (max - min < 1e-9) {
                max = min + 1;
            }
            DrawText(context, max.ToString(Format, CultureInfo.CurrentCulture), new Point(0, plot.Top - 2));
            DrawText(context, min.ToString(Format, CultureInfo.CurrentCulture), new Point(0, plot.Bottom - 14));
            context.DrawLine(new Pen(LabelBrush, 1), new Point(plot.Left - 4, plot.Top), new Point(plot.Left - 4, plot.Bottom));
            if (values.Length == 1) {
                var y = plot.Bottom - ((values[0] - min) / (max - min) * plot.Height);
                context.DrawEllipse(Stroke, null, new Point(plot.Left, y), 3, 3);
                return;
            }
            var geometry = new StreamGeometry();
            using (var g = geometry.Open()) {
                for (var i = 0; i < values.Length; i++) {
                    var x = plot.Left + (i / (double)(values.Length - 1) * plot.Width);
                    var y = plot.Bottom - ((values[i] - min) / (max - min) * plot.Height);
                    if (i == 0) {
                        g.BeginFigure(new Point(x, y), false);
                    } else {
                        g.LineTo(new Point(x, y));
                    }
                }
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }

        private void DrawText(DrawingContext context, string text, Point origin) {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, LabelBrush);
            context.DrawText(formatted, origin);
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    }
}
