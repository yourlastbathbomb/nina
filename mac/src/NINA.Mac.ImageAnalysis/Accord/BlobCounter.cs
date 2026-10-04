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
using System.Collections.Generic;

namespace NINA.Mac.ImageAnalysis.AccordPort {

    /// <summary>Connected component found by <see cref="BlobCounter"/> (port of Accord.Imaging.Blob).</summary>
    public sealed class Blob {

        public Blob(int id, PixelRect rectangle) {
            ID = id;
            Rectangle = rectangle;
        }

        public int ID { get; }

        /// <summary>
        /// Bounding box as built by the in-repo Accord.Imaging (Blob Processing/BlobCounterBase.cs,
        /// CollectObjectsInfo): <b>Width = x2 - x1 and Height = y2 - y1</b>, i.e. one pixel smaller than the true
        /// extent. N.I.N.A. compiles that copy, so the quirk is kept on purpose.
        /// </summary>
        public PixelRect Rectangle { get; }

        public int Area { get; internal set; }

        public double Fullness { get; internal set; }

        public AccordPoint CenterOfGravity { get; internal set; }
    }

    /// <summary>
    /// Port of Accord.Imaging.BlobCounter (8 bpp path of BuildObjectsMap, Blob Processing/BlobCounter.cs) and the
    /// parts of BlobCounterBase used by N.I.N.A. (ProcessImage without filtering, ObjectsOrder.None,
    /// GetObjectsInformation, GetBlobsEdgePoints). 8-connected labelling with background threshold 0.
    /// </summary>
    public sealed class BlobCounter {
        private List<Blob> blobs = new List<Blob>();

        public int ObjectsCount { get; private set; }

        public int[] ObjectLabels { get; private set; }

        public int ImageWidth { get; private set; }

        public int ImageHeight { get; private set; }

        public void ProcessImage(Gray8Image image) {
            ImageWidth = image.Width;
            ImageHeight = image.Height;
            BuildObjectsMap(image);
            CollectObjectsInfo(image);
        }

        public Blob[] GetObjectsInformation() {
            if (ObjectLabels == null) {
                throw new InvalidOperationException("Image should be processed before to collect objects map.");
            }
            return blobs.ToArray();
        }

        private void BuildObjectsMap(Gray8Image image) {
            const byte backgroundThresholdG = 0;
            if (ImageWidth == 1) {
                throw new InvalidOperationException("BlobCounter cannot process images that are one pixel wide. Rotate the image or use RecursiveBlobCounter.");
            }
            byte[] src = image.Pixels;
            int width = ImageWidth;
            int height = ImageHeight;
            int imageWidthM1 = width - 1;

            var labels = new int[width * height];
            ObjectLabels = labels;
            int labelsCount = 0;

            int maxObjects = ((width / 2) + 1) * ((height / 2) + 1) + 1;
            var map = new int[maxObjects];
            for (int i = 0; i < maxObjects; i++) {
                map[i] = i;
            }

            int p = 0;
            // 1 - for pixels of the first row
            if (src[p] > backgroundThresholdG) {
                labels[p] = ++labelsCount;
            }
            ++p;
            for (int x = 1; x < width; x++, p++) {
                if (src[p] > backgroundThresholdG) {
                    if (src[p - 1] > backgroundThresholdG) {
                        labels[p] = labels[p - 1];
                    } else {
                        labels[p] = ++labelsCount;
                    }
                }
            }

            // 2 - for other rows
            for (int y = 1; y < height; y++) {
                // first pixel of the row: upper and upper-right only
                if (src[p] > backgroundThresholdG) {
                    if (src[p - width] > backgroundThresholdG) {
                        labels[p] = labels[p - width];
                    } else if (src[p + 1 - width] > backgroundThresholdG) {
                        labels[p] = labels[p + 1 - width];
                    } else {
                        labels[p] = ++labelsCount;
                    }
                }
                ++p;

                // left pixel and three upper pixels for the rest
                for (int x = 1; x < imageWidthM1; x++, p++) {
                    if (src[p] > backgroundThresholdG) {
                        if (src[p - 1] > backgroundThresholdG) {
                            labels[p] = labels[p - 1];
                        } else if (src[p - 1 - width] > backgroundThresholdG) {
                            labels[p] = labels[p - 1 - width];
                        } else if (src[p - width] > backgroundThresholdG) {
                            labels[p] = labels[p - width];
                        }

                        if (src[p + 1 - width] > backgroundThresholdG) {
                            if (labels[p] == 0) {
                                labels[p] = labels[p + 1 - width];
                            } else {
                                int l1 = labels[p];
                                int l2 = labels[p + 1 - width];

                                if ((l1 != l2) && (map[l1] != map[l2])) {
                                    // merge
                                    if (map[l1] == l1) {
                                        map[l1] = map[l2];
                                    } else if (map[l2] == l2) {
                                        map[l2] = map[l1];
                                    } else {
                                        map[map[l1]] = map[l2];
                                        map[l1] = map[l2];
                                    }

                                    // reindex
                                    for (int i = 1; i <= labelsCount; i++) {
                                        if (map[i] != i) {
                                            int j = map[i];
                                            while (j != map[j]) {
                                                j = map[j];
                                            }
                                            map[i] = j;
                                        }
                                    }
                                }
                            }
                        }

                        if (labels[p] == 0) {
                            labels[p] = ++labelsCount;
                        }
                    }
                }

                // last pixel of the row: left, upper-left and upper only
                if (src[p] > backgroundThresholdG) {
                    if (src[p - 1] > backgroundThresholdG) {
                        labels[p] = labels[p - 1];
                    } else if (src[p - 1 - width] > backgroundThresholdG) {
                        labels[p] = labels[p - 1 - width];
                    } else if (src[p - width] > backgroundThresholdG) {
                        labels[p] = labels[p - width];
                    } else {
                        labels[p] = ++labelsCount;
                    }
                }
                ++p;
            }

            // allocate remapping array
            var reMap = new int[map.Length];
            int objectsCount = 0;
            for (int i = 1; i <= labelsCount; i++) {
                if (map[i] == i) {
                    reMap[i] = ++objectsCount;
                }
            }
            for (int i = 1; i <= labelsCount; i++) {
                if (map[i] != i) {
                    reMap[i] = reMap[map[i]];
                }
            }
            for (int i = 0, n = labels.Length; i < n; i++) {
                labels[i] = reMap[labels[i]];
            }
            ObjectsCount = objectsCount;
        }

        private void CollectObjectsInfo(Gray8Image image) {
            int objectsCount = ObjectsCount;
            var x1 = new int[objectsCount + 1];
            var y1 = new int[objectsCount + 1];
            var x2 = new int[objectsCount + 1];
            var y2 = new int[objectsCount + 1];
            var area = new int[objectsCount + 1];
            var xc = new long[objectsCount + 1];
            var yc = new long[objectsCount + 1];

            for (int j = 1; j <= objectsCount; j++) {
                x1[j] = ImageWidth;
                y1[j] = ImageHeight;
            }

            int[] labels = ObjectLabels;
            int i = 0;
            for (int y = 0; y < ImageHeight; y++) {
                for (int x = 0; x < ImageWidth; x++, i++) {
                    int label = labels[i];
                    if (label == 0) {
                        continue;
                    }
                    if (x < x1[label]) {
                        x1[label] = x;
                    }
                    if (x > x2[label]) {
                        x2[label] = x;
                    }
                    if (y < y1[label]) {
                        y1[label] = y;
                    }
                    if (y > y2[label]) {
                        y2[label] = y;
                    }
                    area[label]++;
                    xc[label] += x;
                    yc[label] += y;
                }
            }

            blobs = new List<Blob>(objectsCount);
            for (int j = 1; j <= objectsCount; j++) {
                int blobArea = area[j];
                var blob = new Blob(j, new PixelRect(x1[j], y1[j], x2[j] - x1[j], y2[j] - y1[j])) {
                    Area = blobArea,
                    Fullness = (double)blobArea / ((x2[j] - x1[j] + 1) * (y2[j] - y1[j] + 1)),
                    CenterOfGravity = new AccordPoint((float)xc[j] / blobArea, (float)yc[j] / blobArea)
                };
                blobs.Add(blob);
            }
        }

        /// <summary>
        /// BlobCounterBase.GetBlobsEdgePoints: left/right edge per row, then top/bottom edge per column, skipping points
        /// already taken as left/right edges. Uses the (one pixel short) blob rectangle, as upstream.
        /// </summary>
        public List<IntPoint> GetBlobsEdgePoints(Blob blob) {
            if (ObjectLabels == null) {
                throw new InvalidOperationException("Image should be processed before to collect objects map.");
            }
            int[] objectLabels = ObjectLabels;
            var edgePoints = new List<IntPoint>();

            int xmin = blob.Rectangle.Left;
            int xmax = xmin + blob.Rectangle.Width - 1;
            int ymin = blob.Rectangle.Top;
            int ymax = ymin + blob.Rectangle.Height - 1;
            int label = blob.ID;

            var leftProcessedPoints = new int[Math.Max(0, blob.Rectangle.Height)];
            var rightProcessedPoints = new int[Math.Max(0, blob.Rectangle.Height)];

            // walk through all lines
            for (int y = ymin; y <= ymax; y++) {
                // left edge
                int p = (y * ImageWidth) + xmin;
                for (int x = xmin; x <= xmax; x++, p++) {
                    if (objectLabels[p] == label) {
                        edgePoints.Add(new IntPoint(x, y));
                        leftProcessedPoints[y - ymin] = x;
                        break;
                    }
                }
                // right edge
                p = (y * ImageWidth) + xmax;
                for (int x = xmax; x >= xmin; x--, p--) {
                    if (objectLabels[p] == label) {
                        if (leftProcessedPoints[y - ymin] != x) {
                            edgePoints.Add(new IntPoint(x, y));
                        }
                        rightProcessedPoints[y - ymin] = x;
                        break;
                    }
                }
            }

            // walk through all columns
            for (int x = xmin; x <= xmax; x++) {
                // top edge
                int p = (ymin * ImageWidth) + x;
                for (int y = ymin, y0 = 0; y <= ymax; y++, y0++, p += ImageWidth) {
                    if (objectLabels[p] == label) {
                        if ((leftProcessedPoints[y0] != x) &&
                            (rightProcessedPoints[y0] != x)) {
                            edgePoints.Add(new IntPoint(x, y));
                        }
                        break;
                    }
                }
                // bottom edge
                p = (ymax * ImageWidth) + x;
                for (int y = ymax, y0 = ymax - ymin; y >= ymin; y--, y0--, p -= ImageWidth) {
                    if (objectLabels[p] == label) {
                        if ((leftProcessedPoints[y0] != x) &&
                            (rightProcessedPoints[y0] != x)) {
                            edgePoints.Add(new IntPoint(x, y));
                        }
                        break;
                    }
                }
            }

            return edgePoints;
        }
    }
}
