#region "copyright"

/*
    Part of the LGPL Accord.NET / AForge.NET port used by the N.I.N.A. macOS image-analysis library.

    Copyright © Andrew Kirillov (AForge.NET framework), 2007-2010
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
using System.Collections.Generic;

namespace NINA.Mac.ImageAnalysis.AccordPort {

    /// <summary>
    /// Port of Accord.Math.Geometry.SimpleShapeChecker.IsCircle (Accord.Math 3.8.2 NuGet; not in the repo) with its
    /// defaults (minAcceptableDistortion 0.5, relativeDistortionLimit 0.03). The circle is estimated from the
    /// bounding box of the edge points; it is a circle when the mean |distance - radius| is within the limit.
    /// Parity with the real Accord.Math assembly is checked by the tests.
    /// </summary>
    public sealed class SimpleShapeChecker {
        private float minAcceptableDistortion = 0.5f;
        private float relativeDistortionLimit = 0.03f;

        public float MinAcceptableDistortion {
            get => minAcceptableDistortion;
            set => minAcceptableDistortion = Math.Max(0, value);
        }

        public float RelativeDistortionLimit {
            get => relativeDistortionLimit;
            set => relativeDistortionLimit = Math.Max(0, Math.Min(1, value));
        }

        public bool IsCircle(List<IntPoint> edgePoints, out AccordPoint center, out float radius) {
            // make sure we have at least 8 points for a circle shape
            if (edgePoints.Count < 8) {
                center = new AccordPoint(0, 0);
                radius = 0;
                return false;
            }

            // PointsCloud.GetBoundingRectangle
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int i = 0, n = edgePoints.Count; i < n; i++) {
                var pt = edgePoints[i];
                if (pt.X < minX) { minX = pt.X; }
                if (pt.Y < minY) { minY = pt.Y; }
                if (pt.X > maxX) { maxX = pt.X; }
                if (pt.Y > maxY) { maxY = pt.Y; }
            }
            var minXY = new IntPoint(minX, minY);
            var maxXY = new IntPoint(maxX, maxY);

            // cloud size and center point
            IntPoint cloudSize = maxXY - minXY;
            center = (AccordPoint)minXY + ((AccordPoint)cloudSize / 2);

            radius = ((float)cloudSize.X + cloudSize.Y) / 4;

            // mean distance between the edge points and the estimated circle
            float meanDistance = 0;
            for (int i = 0, n = edgePoints.Count; i < n; i++) {
                meanDistance += (float)Math.Abs(center.DistanceTo(edgePoints[i]) - radius);
            }
            meanDistance /= edgePoints.Count;

            float maxDitance = Math.Max(minAcceptableDistortion,
                ((float)cloudSize.X + cloudSize.Y) / 2 * relativeDistortionLimit);

            return meanDistance <= maxDitance;
        }
    }
}
