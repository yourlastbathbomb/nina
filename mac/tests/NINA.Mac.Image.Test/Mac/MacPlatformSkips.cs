#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NUnit.Framework.Interfaces;

[assembly: NINA.Mac.Image.Test.MacPlatformSkips]

namespace NINA.Mac.Image.Test {

    /// <summary>
    /// Upstream NINA.Test fixtures are linked unchanged. A few of their tests exercise features that do not exist on macOS
    /// by design (GDI+ and the WPF/WIC TIFF codec). Each such test is listed here by class and method, with the reason,
    /// and is reported as Skipped with that reason instead of run, as NUnit's [Platform(Exclude = "MacOsX")] would mark it.
    /// Tests that fail because macOS behaves differently from Windows are not listed: they stay visible as failures
    /// (see mac/src/README-engine.md, "Upstream tests on macOS"). NINA_MAC_RUN_SKIPPED=1 runs the listed tests anyway.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class MacPlatformSkipsAttribute : Attribute, ITestAction {

        public const string GdiPlus = "macOS: GDI+ (System.Drawing.Common) does not exist on macOS; the first Bitmap use throws "
            + "TypeInitializationException (gdiplus.dll not found). NINA.Mac.ImageAnalysis (M5) replaces this code path.";

        public const string WicTiff = "macOS: TIFF saving goes through WPF's WIC codecs (BitmapFrame, TiffBitmapEncoder), which do not "
            + "exist on macOS; NINA.Mac.WpfCompat throws PlatformNotSupportedException. The engine saves FITS or XISF.";

        private static readonly Dictionary<string, (string Reason, Func<ITest, bool> Applies)> skips = new() {
            // Accord/GDI+ Bayer filter: every case builds a System.Drawing.Bitmap
            ["NINA.Test.Image.ImageAnalysis.BayerFilter16bppTests.Demosaic_ProducesBitExactChannelsAndBuffers"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.BayerFilter16bppTests.Demosaic_HandlesEvenAndOddDimensions"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.BayerFilter16bppTests.Demosaic_RespectsBayerPatternOverride"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.BayerFilter16bppTests.Demosaic_HandlesBorderOnlyDimensions"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.BayerFilter16bppTests.RawMapping_NoDemosaicCopiesPixelsIntoPatternedChannels"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.BayerFilter16bppTests.RawMapping_HandlesEvenAndOddDimensions"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.BayerFilter16bppTests.RawMapping_RespectsBayerPatternOverride"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.BayerFilter16bppTests.Demosaic_SaveLumOnly_PopulatesLumArray"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.BayerFilter16bppTests.Demosaic_SaveColorOnly_PopulatesColorArrays"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.BayerFilter16bppTests.Demosaic_NoChannelsRequested_LeavesLRGBArraysNull"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.BayerFilter16bppTests.Apply_DoesNotModifySourceBuffer"] = (GdiPlus, Always),
            // GDI+ analysis helpers (debayer, detection crop, blur, palette, Canny, resize)
            ["NINA.Test.Image.ImageAnalysis.ImageAnalysisUtilityBehaviorTest.Debayer_RejectsUnsupportedBitmapPixelFormat"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.ImageAnalysisUtilityBehaviorTest.DetectionUtility_CropAndRoiMathIsCenteredAndBoundaryAware"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.ImageAnalysisUtilityBehaviorTest.FastGaussianBlur_ProcessPreservesConstantFrame"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.ImageAnalysisUtilityBehaviorTest.GetGrayScalePalette_ReturnsMonotonicGrayRamp"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.ImageAnalysisUtilityBehaviorTest.NoBlurCannyEdgeDetector_DetectsSyntheticStepEdgeAndClearsBorder"] = (GdiPlus, Always),
            ["NINA.Test.Image.ImageAnalysis.ImageAnalysisUtilityBehaviorTest.ResizeForDetection_ReturnsOriginalOrScaledBitmapAccordingToMaximumWidth"] = (GdiPlus, Always),
            // DebayeredImage.ReRender debayers through BayerFilter16bpp (GDI+)
            ["NINA.Test.Image.ImageData.DebayeredImageBehaviorTest.ReRender_PreservesBayerPatternAndChannelData"] = (GdiPlus, Always),
            // TIFF cases only; the FITS and XISF cases of the same tests run. A failed TIFF save would also leave an empty
            // TestFile.bar / .tif in the test folder (SaveTiff opens the file first), which breaks the next run's XISF case.
            ["NINA.Test.ImageDataTest.SaveToDiskSimpleTest"] = (WicTiff, IsTiffCase),
            ["NINA.Test.ImageDataTest.SaveToDiskForceExtensionTest"] = (WicTiff, IsTiffCase),
            ["NINA.Test.ImageDataTest.SaveToDiskPatternEmptyMetaDataTest"] = (WicTiff, Always),
        };

        public ActionTargets Targets => ActionTargets.Test;

        /// <summary>Every listed test, by "Class.Method", with its reason (for the coverage check in MacPlatformSkipsTest).</summary>
        public static IReadOnlyDictionary<string, string> Listed => skips.ToDictionary(s => s.Key, s => s.Value.Reason);

        /// <summary>Set NINA_MAC_RUN_SKIPPED=1 to run the listed tests anyway, e.g. to confirm they still fail for the stated reason.</summary>
        public static bool Disabled => Environment.GetEnvironmentVariable("NINA_MAC_RUN_SKIPPED") == "1";

        public void BeforeTest(ITest test) {
            var reason = ReasonFor(test);
            if (reason != null && !Disabled) {
                Assert.Ignore(reason);
            }
        }

        public void AfterTest(ITest test) {
        }

        public static string? ReasonFor(ITest test) {
            if (test.ClassName == null || test.MethodName == null) {
                return null;
            }
            return skips.TryGetValue(test.ClassName + "." + test.MethodName, out var skip) && skip.Applies(test) ? skip.Reason : null;
        }

        private static bool Always(ITest test) {
            return true;
        }

        private static bool IsTiffCase(ITest test) {
            return test.Arguments.Length > 0 && test.Arguments[0] is FileTypeEnum fileType && fileType == FileTypeEnum.TIFF;
        }
    }
}
