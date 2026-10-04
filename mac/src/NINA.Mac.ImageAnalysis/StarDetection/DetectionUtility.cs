#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Mac.ImageAnalysis.AccordPort;
using System;

namespace NINA.Mac.ImageAnalysis {

    /// <summary>Port of NINA.Image/ImageAnalysis/DetectionUtility.cs (System.Drawing types replaced).</summary>
    public static class DetectionUtility {

        /// <summary>DetectionUtility.GetCropRectangle (DetectionUtility.cs:24-30).</summary>
        public static PixelRect GetCropRectangle(int imageWidth, int imageHeight, double cropRatio) {
            int xcoord = (int)Math.Floor((imageWidth - imageWidth * cropRatio) / 2d);
            int ycoord = (int)Math.Floor((imageHeight - imageHeight * cropRatio) / 2d);
            int width = (int)Math.Floor(imageWidth * cropRatio);
            int height = (int)Math.Floor(imageHeight * cropRatio);
            return new PixelRect(xcoord, ycoord, width, height);
        }

        /// <summary>
        /// DetectionUtility.ResizeForDetection (DetectionUtility.cs:52-61): bicubic resize to
        /// floor(size * resizeFactor) when the image is wider than maxWidth.
        /// </summary>
        public static Gray8Image ResizeForDetection(Gray8Image image, int maxWidth, double resizeFactor) {
            if (image.Width > maxWidth) {
                return ResizeBicubic.Apply(image, (int)Math.Floor(image.Width * resizeFactor), (int)Math.Floor(image.Height * resizeFactor));
            }
            return image;
        }

        /// <summary>DetectionUtility.InROI (DetectionUtility.cs:63-93): blob inside the outer box and not inside the inner box.</summary>
        public static bool InROI(int imageWidth, int imageHeight, PixelRect blob, double outerCropRatio = 1.0, double innerCropRatio = 1.0) {
            PixelRect outsideCropRect;

            // NOTE: OuterCrop internally set to 0 if we took a subframe and want a donut shape
            if (innerCropRatio >= 1.0 || outerCropRatio <= 0.0) {
                outsideCropRect = new PixelRect(0, 0, imageWidth, imageHeight);
            } else {
                // if only inner crop is set, then it is the outer boundary. Otherwise we use the outer crop
                var outsideCropRatio = outerCropRatio >= 1.0 ? innerCropRatio : outerCropRatio;
                var startFactor = (1.0 - outsideCropRatio) / 2.0;
                outsideCropRect = new PixelRect(
                    (int)Math.Floor(imageWidth * startFactor),
                    (int)Math.Floor(imageHeight * startFactor),
                    (int)(imageWidth * outsideCropRatio),
                    (int)(imageHeight * outsideCropRatio));
            }

            PixelRect insideCropRect;
            if (outerCropRatio >= 1.0) {
                // This rectangle is used to indicate no inside cropping should be done
                insideCropRect = new PixelRect(imageWidth / 2, imageHeight / 2, 0, 0);
            } else {
                var startFactor = (1.0 - innerCropRatio) / 2.0;
                insideCropRect = new PixelRect(
                    (int)Math.Floor(imageWidth * startFactor),
                    (int)Math.Floor(imageHeight * startFactor),
                    (int)(imageWidth * innerCropRatio),
                    (int)(imageHeight * innerCropRatio));
            }
            return FullyInsideRect(blob, outsideCropRect) && !FullyInsideRect(blob, insideCropRect);
        }

        /// <summary>DetectionUtility.FullyInsideRect (DetectionUtility.cs:95-107).</summary>
        public static bool FullyInsideRect(PixelRect lhs, PixelRect rhs) {
            var rhsRightX = rhs.X + rhs.Width;
            var rhsBottomY = rhs.Y + rhs.Height;
            if (lhs.X < rhs.X || lhs.Y < rhs.Y || lhs.X >= rhsRightX || lhs.Y >= rhsBottomY) {
                return false;
            }

            var lhsRightX = lhs.X + lhs.Width;
            var lhsBottomY = lhs.Y + lhs.Height;
            return lhsRightX <= rhsRightX && lhsBottomY <= rhsBottomY;
        }
    }
}
