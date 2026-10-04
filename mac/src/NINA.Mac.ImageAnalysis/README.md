# NINA.Mac.ImageAnalysis (M5 groundwork)

Managed image analysis for the macOS port: frame statistics, NINA's auto-stretch, debayering, star detection (star count, HFR, FWHM, eccentricity) and the Bahtinov analyser. It has no WPF, no `System.Drawing` and no native code. It is a port of upstream `NINA.Image` (develop @ ee69f27) that keeps the maths and every parameter and default. The GDI+ and Accord image operations are re-implemented on plain `ushort[]`/`byte[]` buffers.

## Layout

| Path | License | What |
|---|---|---|
| `NINA.Mac.ImageAnalysis.csproj` | MPL-2.0 | `PixelBuffer`, `ImageStatistics`, `AutoStretch`, `Debayer`, `StarDetector`, `BahtinovAnalyzer`, `FrameAnalyzer` |
| `Accord/NINA.Mac.ImageAnalysis.Accord.csproj` | LGPL-2.1+ | Ports of the Accord/AForge filters NINA calls: Canny (with and without blur), Gaussian kernel and convolution, SIS threshold, 3x3 dilation, blob counter and edge points, `SimpleShapeChecker.IsCircle`, bicubic resize, median, Hough lines, `Line`, grayscale, 16-to-8 bit. It is a separate assembly so it stays replaceable, as `Accord.Imaging` is upstream. |
| `Cli/` (`nina-ia`) | MPL-2.0 | Command-line runner for FITS files, with a minimal FITS reader |
| `../../tests/NINA.Mac.ImageAnalysis.Test` | MPL-2.0 | NUnit tests, the synthetic sky generator and the Bahtinov pattern generator |
| `../../tests/NINA.Mac.ImageAnalysis.Test/AccordOracle` | LGPL (upstream code) | **Test only, never shipped.** Compiles the in-repo `Accord.Imaging` sources plus NINA's `BayerFilter16bpp` and `NoBlurCannyEdgeDetector` by link, so the ports can be compared bit for bit with the original filters. |

**Accord decision.** The in-repo `Accord.Imaging (NETStandard)` cannot be used at runtime. It references `System.Drawing.Common` (252 of its 288 files reference `System.Drawing`), and on macOS Accord 3.8.2's `SystemTools` P/Invokes `memcpy`/`memset` from `ntdll.dll`. The oracle test project gets past that last problem only with a test-only resolver that maps `ntdll.dll` to `libSystem`. The managed port of the filters NINA uses is about 1,600 lines including headers, so OpenCvSharp was never needed.

## Build, test, run

```bash
mac/dotnet build mac/src/NINA.Mac.ImageAnalysis/Cli/NINA.Mac.ImageAnalysis.Cli.csproj -c Release
mac/dotnet test  mac/tests/NINA.Mac.ImageAnalysis.Test/NINA.Mac.ImageAnalysis.Test.csproj -c Release
mac/dotnet test  mac/tests/NINA.Mac.ImageAnalysis.Test/NINA.Mac.ImageAnalysis.Test.csproj -c Release --filter Category=Performance --logger "console;verbosity=normal"
mac/dotnet mac/src/NINA.Mac.ImageAnalysis/Cli/bin/Release/net10.0/osx-arm64/nina-ia.dll analyze frame.fits [--bin2|--monobin2] [--stars 10] [--bahtinov x,y,w,h --robust]
```

Run the performance tests in Release. Run `nina-ia` with no arguments to list its options.

```csharp
var frame = new PixelBuffer(zwoBuffer, 1920, 1080, bitDepth: 16, BayerPattern.RGGB); // top-down rows
var analysis = FrameAnalyzer.Analyze(frame, FrameAnalysisOptions.ForRig());          // NINA profile defaults + rig optics
double hfr = analysis.Stars.AverageHFR; int stars = analysis.Stars.DetectedStars;
var bahtinov = FrameAnalyzer.AnalyzeBahtinov(analysis, new PixelRect(860, 440, 200, 200), previous, robust: true);
```

## What is replayed from N.I.N.A.

`FrameAnalyzer` reproduces `ImageControlVM.PrepareImage` and `ProcessImage`, then `RenderedImage.DetectStars`:

1. Statistics come from the raw data (`ImageStatistics.Create`). The MAD is NINA's histogram walk, not the textbook MAD.
2. A Bayer frame with debayering on goes through `BayerFilter16bpp`: each channel is the 3x3 same-colour mean. It also keeps the R/G/B planes for an unlinked stretch and `Lum = (R+G+B)/3` for HFR. ZWO mono-bin frames carry no pattern, so they take the mono path.
3. The auto-stretch (factor 0.2, black clipping -2.8) is always applied before detection, because NINA forces it on.
4. Detection runs on the stretched image. For RGB it applies `Grayscale(0.2125, 0.7154, 0.0721)` to GDI+'s BGR view of WPF RGB memory, then converts 16 to 8 bit. Optional noise reduction follows, then a bicubic resize (`StarDetection.cs:70-103` rules), Canny(10, 80), SIS threshold, 3x3 dilation, blobs and the circle check.
5. Each blob is then measured on the linear data (raw, or the debayered Lum for colour frames). The steps are a sigma-clipped local background, a windowed centroid, the curve-of-growth HFR in a 1.5x radius aperture, the radial-profile FWHM, moment eccentricity, then the mean ± 1.5σ radius filter.

## Verification (run on this M1 Air, Release, all 154 tests pass)

- **Bit-exact parity with the original code.** Against the Accord/NINA oracle these all match exactly: the Gaussian kernel and blur, Canny, NoBlurCanny, SIS, dilation, blob labels/rectangles/edge points, bicubic resize, median, Hough lines, 48-to-16 grayscale and `BayerFilter16bpp` (8 patterns). Against the real Accord.Math DLL, `SimpleShapeChecker`, `Line` and `Point.DistanceTo` match exactly.
- **HFR maths** (noise-free, pixel-integrated Gaussian and Moffat stars, FWHM 4-12 px): averaged over 25 sub-pixel positions, the HFR is within 2% of the analytic aperture-limited value. The worst case is −1.8%, for a Gaussian with FWHM 4. Single stars scatter more, because upstream's curve of growth interpolates between pixel-centre distances: up to about ±10% at FWHM 4-5 px, ±3% at 8 px and ±1.5% at 12 px.
- **Full pipeline** (synthetic 1920x1080 frames with gradient, shot and read noise, 40 hot pixels and Gaussian/Moffat PSFs): precision ≥ 0.98, no hot pixels detected on mono frames, recall ≥ 0.9 for stars with peak SNR 80-500, and the average HFR within 2%. On OSC frames the HFR is larger than the PSF's by the expected demosaic blur: +2.9% measured against +3.2% predicted at FWHM 6 px.
- **Bahtinov** (35 synthetic patterns with known central-spike offsets of ±2-8 px). The faithful port is within 1 px on 91% of them, with the sign right on 29 of 30. The opt-in `AnalyzeRobust` is within 0.74 px on all 63 patterns (mean 0.18 px), at central-spike angles 0-170°. The faithful port gets the sign and size wrong whenever the pattern straddles horizontal (central spike at 0°, 10° or 170°). It is fine at 90°.
- **Speed** (Release, median of 5 runs after a warm-up, final build, other builds running, load average about 4): a bin-2 1920x1080 OSC frame takes 92 ms with the profile defaults and 66 ms with the rig optics. A mono-bin frame takes 58 ms, a bin-1 3840x2160 OSC frame 232 ms, and Bahtinov on a 200x200 crop 3.5 ms. Before the final parallelisation changes, and on a busier machine, the same runs took 156 / 105 / 126 / 320 ms. In Debug the same frames take 275 / 206 / 174 / 731 ms.
- **Real files** (informal; nothing was added to the repo):
  - `~/Pictures/Light/Light_001.fits` is an ASI585MC raw frame: 10 ms exposure, max 2889 ADU. It gives 0 stars at full size (224 ms), after a colour 2x2 bin (174 ms; 544 noise blobs, all rejected) and after a mono 2x2 bin (134 ms).
  - Seestar S30 Pro stacks give 98-1169 stars at HFR 1.7-2.4 px. With Siril 1.4.4 `findstar` on the same files:

    | Object | Stars (ours / Siril) | FWHM px (ours / Siril) |
    |---|---|---|
    | IC 434 | 397 / 386 | 3.56 / 3.55 |
    | NGC 7000 | 1169 / 1079 | 3.26 / 3.17 |
    | M51 | 98 / 352 | 3.30 / 3.10 |

## Rig notes

- **High sensitivity downsizes the detection image 4x on this rig.** It computes the image scale from the unbinned pixel size: 2.9 µm at 2500 mm is 0.24"/px, which is under 0.5"/px, so the factor is 1/4 (still 1/4 with the reducer). The HFR average is unaffected, but fewer faint stars are found.
- **For focusing on OSC frames, prefer ZWO mono-bin.** Debayered HFR includes the demosaic blur. NINA also has no hot-pixel rejection, so on uncalibrated OSC frames a hot pixel becomes a 3x3 bump and is counted as a star (characterised in the tests).
- **NINA's grayscale step gives red 0.0721 and blue 0.2125** (the channel weights are swapped). The oracle tests confirm that NINA's debayer writes red into memory slot 0 and that Accord's grayscale applies the red weight to slot 2. The rest is inferred from the documented WPF Rgb48 (RGB) and GDI+ 48bpp (BGR) memory orders. Hα behind the ALP-T would therefore be under-weighted in the detection image.
- **Bahtinov:** with `robust: false`, keep every spike more than about 20° away from horizontal.

## Where results can differ from Windows N.I.N.A.

Parity with Windows was not measured; no Windows machine was available. Possible differences:

1. `Vector<double>` has 2 lanes on M1 and 4 with AVX2, so floating-point summation order in the star maths differs (last-bit differences).
2. The platform math library (`Exp`, `Atan`, `Sqrt`) can differ by 1 ULP between Windows and macOS. That only matters at exact thresholds: Canny angle bins and the integer Gaussian kernel. The kernel was checked against Accord here.
3. Found by reading the code, not reproduced: upstream `ColorRemappingGeneral` walks the buffer as if it had no GDI+ row padding, so odd-width images (padded rows) are stretched wrongly. This port stretches each pixel.
4. Degenerate Bahtinov geometry that throws upstream returns `Success = false` here.

The extra fields `DetectionRadius`, `MeasurementRadius`, `StageTimes`, `SignedOffset` and `Normal` are diagnostics this fork adds; they do not change any result. The per-blob measurement and the Canny rows run in parallel. Results are identical, which the tests check.

## Proposed upstream patches (not applied)

1. `StarDetection.cs:776`: use `Math.Sqrt(Math.Max(0, …))`. By code reading (not reproduced): when all radii are equal, rounding can make the stdev NaN, and the filter then drops every star.
2. `StarDetection.cs:77-80`: include the binning in the High-sensitivity image scale.
3. `StarDetection.cs:114` and `BahtinovAnalysis.cs:45`: swap the R/B grayscale coefficients for the GDI+ BGR view.
4. `ColorRemappingGeneral.cs:101-130`: honour `Stride`.
5. `BahtinovAnalysis.cs:72-96`: merge Hough plateau peaks, order the lines by angle with a cut at the largest gap, and average in normal form. `AnalyzeRobust` is the reference implementation.
6. `ImageStatistics.cs:98`, by code reading: the median loop stops at 65534, so a frame that is more than half saturated reports a median of 0.

## Not done yet

- Hooking into the engine's `IImageData`/`IRenderedImage` (M3), the `IStarDetection` plugin signature (Hocus Focus), star annotation drawing, a display bitmap for the UI and `ContrastDetection`.
- An end-to-end HFR comparison against Windows NINA on the same FITS files.
