#region "copyright"

/*
    Part of the LGPL Accord.NET / AForge.NET port used by the N.I.N.A. macOS image-analysis library.

    Copyright © Andrew Kirillov (AForge.NET framework), 2005-2009
    Copyright © César Souza (Accord.NET Framework), 2009-2017
    Managed port for the N.I.N.A. macOS fork, 2026.

    This library is free software; you can redistribute it and/or modify it under the terms of the GNU Lesser
    General Public License as published by the Free Software Foundation; either version 2.1 of the License, or
    (at your option) any later version.

    This library is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the
    implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public
    License for more details: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
*/

#endregion "copyright"

using System;

namespace NINA.Mac.ImageAnalysis.AccordPort {

    /// <summary>
    /// Single precision point, a port of <c>Accord.Point</c> (Accord 3.8.2 core). Upstream N.I.N.A. stores star
    /// positions in this type, so star centroids are rounded to float exactly as on Windows.
    /// </summary>
    public readonly struct AccordPoint : IEquatable<AccordPoint> {
        public readonly float X;
        public readonly float Y;

        public AccordPoint(float x, float y) {
            X = x;
            Y = y;
        }

        /// <summary>Accord.Point.DistanceTo: float differences, (float)Math.Sqrt.</summary>
        public float DistanceTo(AccordPoint anotherPoint) {
            float dx = X - anotherPoint.X;
            float dy = Y - anotherPoint.Y;
            return (float)Math.Sqrt((dx * dx) + (dy * dy));
        }

        public static AccordPoint operator +(AccordPoint a, AccordPoint b) => new AccordPoint(a.X + b.X, a.Y + b.Y);

        public static AccordPoint operator -(AccordPoint a, AccordPoint b) => new AccordPoint(a.X - b.X, a.Y - b.Y);

        public static AccordPoint operator /(AccordPoint p, float factor) => new AccordPoint(p.X / factor, p.Y / factor);

        public static AccordPoint operator *(AccordPoint p, float factor) => new AccordPoint(p.X * factor, p.Y * factor);

        public static implicit operator AccordPoint(IntPoint p) => new AccordPoint(p.X, p.Y);

        public bool Equals(AccordPoint other) => X == other.X && Y == other.Y;

        public override bool Equals(object obj) => obj is AccordPoint other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y);

        public override string ToString() => FormattableString.Invariant($"({X}, {Y})");
    }

    /// <summary>Integer point, a port of <c>Accord.IntPoint</c>.</summary>
    public readonly struct IntPoint : IEquatable<IntPoint> {
        public readonly int X;
        public readonly int Y;

        public IntPoint(int x, int y) {
            X = x;
            Y = y;
        }

        public static IntPoint operator -(IntPoint a, IntPoint b) => new IntPoint(a.X - b.X, a.Y - b.Y);

        public static IntPoint operator +(IntPoint a, IntPoint b) => new IntPoint(a.X + b.X, a.Y + b.Y);

        public bool Equals(IntPoint other) => X == other.X && Y == other.Y;

        public override bool Equals(object obj) => obj is IntPoint other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y);

        public override string ToString() => FormattableString.Invariant($"({X}, {Y})");
    }

    /// <summary>
    /// Integer rectangle with the semantics of System.Drawing.Rectangle (Right = X + Width, Bottom = Y + Height),
    /// without depending on System.Drawing.
    /// </summary>
    public readonly struct PixelRect : IEquatable<PixelRect> {
        public readonly int X;
        public readonly int Y;
        public readonly int Width;
        public readonly int Height;

        public PixelRect(int x, int y, int width, int height) {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int Left => X;
        public int Top => Y;
        public int Right => X + Width;
        public int Bottom => Y + Height;

        public bool Equals(PixelRect other) => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

        public override bool Equals(object obj) => obj is PixelRect other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

        public override string ToString() => FormattableString.Invariant($"{{X={X},Y={Y},Width={Width},Height={Height}}}");
    }

    /// <summary>
    /// Infinite line in slope/intercept form, a port of <c>Accord.Math.Geometry.Line</c> (Accord.Math 3.8.2). For a
    /// vertical line the slope is infinite and the intercept holds the X coordinate. All maths is single precision,
    /// as in Accord.
    /// </summary>
    public sealed class Line {
        private readonly float k;
        private readonly float b;

        private Line(AccordPoint start, AccordPoint end) {
            if (start.Equals(end)) {
                throw new ArgumentException("Start point of the line cannot be the same as its end point.");
            }
            k = (end.Y - start.Y) / (end.X - start.X);
            b = float.IsInfinity(k) ? start.X : start.Y - (k * start.X);
        }

        private Line(float slope, float intercept) {
            k = slope;
            b = intercept;
        }

        public bool IsVertical => float.IsInfinity(k);

        public bool IsHorizontal => k == 0;

        public float Slope => k;

        public float Intercept => b;

        public static Line FromPoints(AccordPoint point1, AccordPoint point2) => new Line(point1, point2);

        public static Line FromSlopeIntercept(float slope, float intercept) => new Line(slope, intercept);

        /// <summary>Returns null for parallel lines; throws for identical lines (as Accord does).</summary>
        public AccordPoint? GetIntersectionWith(Line secondLine) {
            float k2 = secondLine.k;
            float b2 = secondLine.b;

            bool isVertical1 = IsVertical;
            bool isVertical2 = secondLine.IsVertical;

            AccordPoint? intersection = null;

            if ((k == k2) || (isVertical1 && isVertical2)) {
                if (b == b2) {
                    throw new InvalidOperationException("Identical lines do not have an intersection point.");
                }
            } else {
                if (isVertical1) {
                    intersection = new AccordPoint(b, (k2 * b) + b2);
                } else if (isVertical2) {
                    intersection = new AccordPoint(b2, (k * b2) + b);
                } else {
                    float x = (b2 - b) / (k - k2);
                    intersection = new AccordPoint(x, (k * x) + b);
                }
            }
            return intersection;
        }

        public override string ToString() => FormattableString.Invariant($"k = {k}, b = {b}");
    }
}
