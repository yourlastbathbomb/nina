# M3b compile plan: NINA.Image, NINA.Equipment (rig subset), NINA.Platesolving

*Read-only scout, 2026-10-04. Upstream base `ee69f27` on branch `macos`, read alongside the M3a working tree as it stood around 18:40 (`NINA.Core.Mac`, `NINA.Profile.Mac`, `NINA.Mac.WpfCompat`, `Engine.props`, `README-engine.md`, and M3a's `DllLoader`/`SerialPortProvider` edits). This file is the only thing this scout wrote in the repo. Every compile and run below used throwaway projects under `/private/tmp/.../scratchpad/m3b`, built with `mac/dotnet` (SDK 10.0.401). Those scratch projects will not persist, so Appendix A reproduces the one non-obvious tool.*

M3b means making upstream `NINA.Image`, the parts of `NINA.Equipment` this rig needs, and `NINA.Platesolving` compile for `net10.0` / `osx-arm64`. It uses the M3a method: fork-only `mac/src/*.Mac` projects that link upstream `.cs` files, `NINA.Mac.WpfCompat` for leaked WPF types, `NativeLibraries` in place of `DllLoader`, and upstream edits only as Windows no-ops.

---

## 0. Bottom line

- **All three compile for plain `net10.0` today with 0 errors.** That needs about 27 more WPF imaging types in WpfCompat (exact list in §6), `System.Drawing.Common` as a package, and four small mac-side files. The test built NINA.Image, the Equipment subset and NINA.Platesolving against a scratch stand-in that declares exactly the members listed in §6. The only remaining error is a known artefact of the scratch setup, explained in §1 and removed in the test.
- **NINA.Image: compile all 68 files, exclude none.** The "rendering" code is type-coupled to the save path:
  - `BaseImageData.RenderImage` → `RenderedImage` → `ImageUtility`.
  - `ExposureDataFactory` → `RenderedImage`.
  - `IImageData`, `IRenderedImage` and `IExposureDataFactory` carry `BitmapSource`.

  Excluding the rendering files would mean forking about 630 upstream lines and would still need `BitmapSource`. Compiled as-is, the GDI+ analysis code builds against `System.Drawing.Common` and throws only if called.
- **NINA.Equipment: an include list of 124 of 302 files.** The kept code needs exactly two WPF types (`BitmapSource`, `GeometryGroup`), and only by name in interface signatures. It has zero CA1416 hits and one native library (`ASICamera2.dll`, 30 imports). No ASCOM, Alpaca, gRPC, Castle, NJsonSchema or NMEA package is needed.
- **NINA.Platesolving: all 26 files except `LocalPlateSolver.cs`, which gets a same-name mac replacement.** That is the only clean cut: Sequencer code calls the static `PlateSolverFactory` directly. The ASTAP version-check bug gets a one-line Windows-no-op guard (patch in §7, compile-tested).
- **Runtime smoke on this Mac:**
  - Upstream `ImageArrayExposureData` → `BaseImageData.SaveToDisk` (managed FITS writer) wrote a bin-2 RGGB frame.
  - Siril 1.4.4 read it as *"RGGB from header, top-down from header"* and debayered it to R 40000 / G 10000 / B 2000.
  - The kept ASI binding loaded `libASICamera2.dylib` from the M3b-shaped NINA.Equipment and returned `ASICameras.Count = 0` (no camera attached).
- **New facts that change M3a/M3b work:**
  1. Every image save calls SOFA natively (`BaseImageData.cs:242` `ToMJD()`, `FITSHeader.cs:518,525`), so the first capture needs `libsofa.dylib` and the resolver.
  2. The backslash file-pattern bug is real at run time: the file was literally named `LIGHT\Synthetic_ALPT_10.00s_0001.fits`.
  3. NINA.Astrometry also exposes `BitmapSource` (`SkyObjectBase.Image`, the `DeepSkyObject` constructors). M3a has since added a member-less `BitmapSource` placeholder for it; M3b must extend that file, not add its own.
  4. M3a's `Core.Mac` excludes `HttpDownloadImageRequest.cs`, which NINA.Platesolving needs for dead code (`AstrometryPlateSolver.cs:118-121`).
  5. WPF XAML markup compilation works on macOS: NINA.Core's 2 XAML files built with `EnableWindowsTargeting`. The research had called this unverified.

---

## 1. Evidence (what was actually run)

| # | Experiment | Result (exact) |
|---|---|---|
| E1 | WPF-flavoured reference builds on macOS: scratch shadow projects (`net10.0-windows`, `UseWPF`, `EnableWindowsTargeting`) for Accord.Imaging, Core (with its 2 XAML pages and the `.proto`), Profile, Astrometry, MGEN, nikoncswrapper, Image, Equipment (all vendors) and Platesolving | All `0 Error(s)`. `MyMessageBoxView.g.cs` and `NotificationHostWindow.g.cs` were generated, so markup compilation ran. The `Microsoft.WindowsDesktop.App.Ref` 10.0.12 targeting pack was restored from NuGet |
| E2 | NINA.Image (all files) as plain `net10.0` against E1's Core/Profile/Astrometry, with **no** stand-ins | 244 error lines (122 unique), all first-layer CS0234/CS0246/CS1069: missing `System.Windows.Media*`, `BitmapSource`, `System.Drawing.Bitmap` and friends, plus OxyPlot (a transitive package). By file: ImageUtility 32, StarDetection 12, RenderedImage 11, StarAnnotator 10, ImageArrayExposureData 6, NoBlurCannyEdgeDetector 5, ContrastDetection 5, BahtinovAnalysis 5, IRenderedImage 4, BaseImageData 4, IExposureData 3, ExposureData 3, IStarAnnotator 3, FastGaussianBlur 3, DetectionUtility 3, and 1–2 each in 8 more files |
| E3 | Metadata scan (Appendix A) of every TypeRef/MemberRef from the E1 assemblies into PresentationCore, PresentationFramework, WindowsBase, System.Xaml, WinForms, System.Drawing.Common, Win32, WMI and ASCOM COM | NINA.Image: 48 target TypeRefs (PresentationCore 25, WindowsBase 6, PresentationFramework 1, System.Drawing.Common 16). Equipment, kept subset: 2 (`GeometryGroup`, `BitmapSource`), 0 member refs. Platesolving: 1 (`BitmapSource`), 0 member refs. Image and Equipment use no Core/Profile/Astrometry member whose signature has a WPF type; Image's only signature leaks are into Accord's `System.Drawing.Bitmap` overloads. The compiler can still need a WPF type for overload resolution, see E6 |
| E4 | WPF-flavoured compile of the **proposed Equipment include list** (§4), to check it is self-consistent | `0 Error(s)` after two corrections the compiler found: `PHD2/PhdEvents/*` is needed (`GuideStepsHistory`, `DirectGuider`, `DummyGuider`, `IGuiderMediator`, `IGuiderVM` use `PhdEventGuideStep` and related types), and `Interfaces/ISVBonySDK.cs` must go (it uses the SVBony SDK namespace) |
| E5 | NINA.Image as plain `net10.0` plus a scratch stand-in with exactly the §6 members, plus `System.Drawing.Common` 10.0.10 | `0 Error(s)`. CA1416: **0** in a normal build, because upstream `.editorconfig:227-228` sets it to `none`. **140** unique CA1416 warnings with `.editorconfig` discovery off (same switch as M3a's `-p:MacPlatformInventory=true`). Also with `-r osx-arm64`: 0 errors |
| E6 | Equipment include list as plain `net10.0` against E5's Image | `0 Error(s)`, CA1416 0. One CS0012 (`BitmapSource` defined in PresentationCore, `Model/CaptureSequenceList.cs:429` via the `DeepSkyObject` ctor) was an artefact of the WPF-flavoured Astrometry. It goes away when `CaptureSequenceList.cs` is excluded (§4), which was compile-tested with no workaround |
| E7 | Platesolving as plain `net10.0` against E5/E6 | 1 error, CS0029 at `AstrometryPlateSolver.cs:120`: Core's `HttpDownloadImageRequest` returns PresentationCore's `BitmapSource`, the stand-in's is a different type. With that upstream file compiled against the stand-in instead (CS0436 local-wins): `0 Error(s)`. CA1416: 2 (`Dc3PinPointSolver.cs:78,174`, `Type.GetTypeFromProgID`) |
| E8 | Platesolving with the ASTAP guard (patch P1) and the `LocalPlateSolver` exclude plus same-name replacement (skeleton) | `0 Error(s)`. The static `PlateSolverFactory` links to the replacement |
| E8b | Platesolving with patches P1–P4 applied (scratch copies of the 4 files) and **neither** the ASCOM anchor nor the `HttpDownloadImageRequest` stub | `0 Error(s)`, CA1416 2 (the same PinPoint lines) |
| E9 | Accord.Imaging shadow under mac's global props (`net10.0` + `RuntimeIdentifier=osx-arm64`), TFM overridden | `netstandard2.0`: 0 warnings, 0 errors. Retargeted to `net10.0`: 35 errors (`Range` is ambiguous between `Accord.Range` and `System.Range`), so keep netstandard2.0 |
| E10 | **Runtime smoke** on M1 arm64. Scratch resolver: `ASICamera2.dll`, `SOFA_2023_10_11`, `NOVAS31lib` → `mac/native/stage/*.dylib`. Two scratch-only assembly satisfiers stood in for the WPF-flavoured deps (a `WindowsBase` with `Point`, a `PresentationFramework` with `ThemeInfo`) | `ASICameras.Count = 0`; `Stats: mean=15500.0 median=10000 min=2000 max=40000` (exact for the synthetic RGGB pattern); `Saved image to …/LIGHT\Synthetic_ALPT_10.00s_0001.fits`; `RenderImage ok: 1920x1080 Gray16`; `GetThumbnail`: the stand-in's `TransformedBitmap` threw, upstream logged it, thumbnail `null`; `Stretch threw TypeInitializationException: Unable to load shared library 'gdiplus.dll'` |
| E11 | The E10 FITS header and Siril | Header: `BITPIX 16`, `BZERO 32768`, `XBINNING 2`, `XPIXSZ 5.8`, `BAYERPAT 'RGGB'`, `XBAYROFF 0`, `ROWORDER 'TOP-DOWN'`, `MJD-OBS 61317.4389178785` (correct for 2026-10-04 10:32 UTC, computed through M3a's arm64 `libsofa.dylib`), `SWCREATE 'N.I.N.A. 3.3.0.1064 (x64)'`. `siril-cli -o -i <scratch ini>`: `convert synth -debayer` gave `Filter Pattern: RGGB from header, Orientation: top-down from header`, then `stat`: `Red layer: Mean: 40000.000000 … Green layer: Mean: 10000.000000 … Blue layer: Mean: 2000.000000` |

Side effect, cleaned up: E10 ran NINA's `Logger`, which wrote 3 log files to the real `~/Library/Application Support/NINA/Logs`. .NET on macOS resolves `LocalApplicationData` natively and ignores a `HOME` override. The files were moved into the scratchpad and the empty `NINA/Logs` folders this run had created were removed. Tests must set `CoreUtil.APPLICATIONTEMPPATH` first, as M3a's host contract already says.

---

## 2. Project layout

New fork-only projects. Each imports `../Engine.props` except Accord, whose versioning is its own.

| Project | Assembly | Compiles | References |
|---|---|---|---|
| `mac/src/Accord.Imaging.Mac` | `Accord.Imaging` | upstream `Accord.Imaging/**/*.cs` (288 files), `netstandard2.0` | packages only |
| `mac/src/NINA.Image.Mac` | `NINA.Image` | upstream `NINA.Image/**/*.cs` (68 files) | Accord.Imaging.Mac, Core.Mac, Profile.Mac, Astrometry.Mac, WpfCompat |
| `mac/src/NINA.Equipment.Mac` | `NINA.Equipment` | include list, 124 of 302 files | Image.Mac, Core.Mac, Profile.Mac, Astrometry.Mac, WpfCompat |
| `mac/src/NINA.Platesolving.Mac` | `NINA.Platesolving` | 25 of 26 files plus `MacReplacements/` | Equipment.Mac, Image.Mac, Core.Mac, Profile.Mac, Astrometry.Mac |
| `mac/tests/NINA.Mac.Image.Test` (or one combined M3b test project) | | NUnit 4.4.0, FluentAssertions [7.0.0], Microsoft.NET.Test.Sdk 18.8.1, NUnit3TestAdapter 6.2.0 | the above |

Each project needs a `README.md` in its own directory. `mac/README.md` and `mac/NINA.Mac.slnx` gain entries only when M3a's work has landed; they are M3a-owned while it runs. `NINA.MGEN` and `nikoncswrapper` are not needed: only excluded Equipment files reference them.

---

## 3. NINA.Image.Mac

### (a) Include/exclude

```xml
<Compile Include="$(UpstreamDir)**/*.cs" Exclude="$(UpstreamDir)obj/**;$(UpstreamDir)bin/**;$(UpstreamDir)publish/**" LinkBase="Upstream" />
<!-- no Compile Remove: every file compiles against WpfCompat + System.Drawing.Common (see plan §3) -->
```

| Group | Files | Decision | Why |
|---|---|---|---|
| Data and I/O: `FileFormat/**` (27: FITS managed writer, CFITSIO, XISF), `ImageData/**` except the two below (11), `Interfaces/**` (8), `RawConverter/**` (2), `Thumbnail.cs`, `ImageAnalysis/{BayerPatternUtility, HistogramMath, IContrastDetection, IStarAnnotator, IStarDetection}.cs`, `Properties/AssemblyInfo.cs` | 55 | keep | Capture → `IExposureData` → `IImageData` → `SaveToDisk` is the core of M3b |
| Rendering glue: `ImageData/RenderedImage.cs`, `DebayeredImage.cs`, `ImageAnalysis/ImageUtility.cs` | 3 | keep (stand-ins) | `BaseImageData.cs:79-85` and `ExposureData.cs:343-348` call them. `IRenderedImage` is what `CaptureSolver` consumes (`CaptureSolver.cs:56-68`: `renderedImage.RawImageData`, `GetThumbnail()`), and `CenteringSolver` goes through it (`CenteringSolver.cs:49,81`). Mac-side copies would fork about 630 lines and still need `BitmapSource` |
| GDI+/Accord analysis: `ImageAnalysis/{StarDetection, StarAnnotator, BahtinovAnalysis, BahtinovImage, ContrastDetection, DetectionUtility, FastGaussianBlur, NoBlurCannyEdgeDetector, ColorRemappingGeneral, BayerFilter16bpp}.cs` | 10 | keep; replace at run time later | They compile with `System.Drawing.Common`. `StarDetection` and `StarAnnotator` are the default `IStarDetection`/`IStarAnnotator` behaviours, and `ImageDataFactory` constructs them for every image; construction doesn't touch GDI+ (E10). M5 (`NINA.Mac.ImageAnalysis`, already being built standalone) supplies a mac `IStarDetection` through the pluggable-behaviour selector, without excluding upstream files |

Rejected alternative: exclude the 10 GDI+ files plus `ImageUtility`, `RenderedImage` and `DebayeredImage`, and write mac copies. It saves `System.Drawing.Common` and about 10 stand-in types, at the cost of a long-lived fork of 628 upstream lines that change with every upstream image-pipeline commit.

### (b) Windows-only inventory (kept = all 68 files)

All counts are from `/usr/bin/grep -nE` over the 68 files, or from the compiler.

| Category | Count | Where | Classification |
|---|---|---|---|
| `using System.Windows*` | 25 lines / 17 files | see the next row | WpfCompat |
| WPF imaging, Dispatcher, `Media.Color`, `Freeze` (regex: `BitmapSource\|WriteableBitmap\|BitmapImage\|FormatConvertedBitmap\|TransformedBitmap\|BitmapDecoder\|BitmapEncoder\|BitmapFrame\|BitmapMetadata\|PixelFormats\|BitmapPalette\|Int32Rect\|ScaleTransform\|TiffCompressOption\|BitmapCacheOption\|BitmapCreateOptions\|Dispatcher\|Application\.Current\|Media\.PixelFormat\|Media\.Color\|\.Freeze\(\)`) | 122 lines / 18 files | ImageUtility.cs (38): 37,40,43,57,61,135,136,140,144,148,152,158,165,175,179,191,203,211,212,215,217,221,226,227,231,289,290,311,314,318,328,330,344,349,353,358,363,369 · BaseImageData.cs (18): 80,83,84,432,436,440,444,448,465,571,574,579,584,588,638,639,642,646 · RenderedImage.cs (14): 38,40,48,50,58,59,64,69,115,116,117,120,121,133 · ImageArrayExposureData.cs (9): 72,84,85,87,89,90,91,99,113 · StarDetection.cs (9): 47,54,69,112,113,115,116,599,754 · BahtinovAnalysis.cs (9): 29,34,36,42,43,46,47,147,149 · StarAnnotator.cs (6): 41,44,47,48,97,99 · ExposureData.cs (4): 343,344,347,348 · IRenderedImage.cs (3): 28,30,45 · ContrastDetection.cs (3): 43,144,166 · IExposureData.cs (2): 47,49 · 1 each: Thumbnail.cs:106, IImageData.cs:42, DebayeredImageData.cs:21, DebayeredImage.cs:33, IStarDetection.cs:29, IStarAnnotator.cs:24, BahtinovImage.cs:20 | WpfCompat (§6). `Dispatcher`, `Application.Current` and `Color` already exist in M3a's WpfCompat |
| WPF codecs: TIFF save (`BaseImageData.cs:427-471`), GIF/TIFF/JPEG/PNG load (`:571-589`, `:638-711`) | inside the rows above | | WpfCompat members that **throw** `PlatformNotSupportedException`. The rig saves FITS only. FITS/XISF load paths don't touch them |
| `System.Drawing` (GDI+), CA1416 with inventory on | **140 warnings on 96 lines / 11 files** | ImageUtility.cs (26 lines): 138,139,143,147,161,162,163,166,167,170,176,180,186,187,188,192,193,194,197,236,298,299,301,315,319,337 · StarAnnotator.cs (18): 29,30,31,32,33,34,35,45,54,55,56,58,76,77,83,85,91,93 · BahtinovAnalysis.cs (17): 44,54,55,58,59,62,63,64,65,79,99,101,103,132,135,139,148 · FastGaussianBlur.cs (14): 34,35,36,38,40,45,52,61,62,63,64,66,72,76 · DetectionUtility.cs (7): 25,26,27,28,54,55,56 · StarDetection.cs (5): 113,653,660,959,978 · ContrastDetection.cs (3): 60,127,135 · ColorRemappingGeneral.cs (2): 83,90 · NoBlurCannyEdgeDetector.cs (2): 29,74 · BayerFilter16bpp.cs (1): 30 · DebayeredImage.cs (1): 57 | **Package** `System.Drawing.Common` (compiles; throws `TypeInitializationException`/`gdiplus` at run time, E10). Run-time replacement is M5. Engine rule until then: `PrepareImageParameters(detectStars:false)`, no `Stretch`/`Debayer`/annotation |
| WinForms, Registry, WMI, COM, Win32 system DLLs, STA | 0 | | |
| `[DllImport]` | 50 declarations / 2 files | `FileFormat/FITS/CfitsioNative.cs` 43 × `cfitsionative.dll` (FITS **read** and compressed write). `RawConverter/LibRawConverter.cs` 7 × `libraw_0_22_1.dll` (DSLR RAW) | NativeLibraries map. Not needed for M3b: the default `FITSUseLegacyWriter = true` (`FileSaveInfo.cs:31`, `ImageFileSettings.cs:45`) writes FITS in managed code, and `CLISolver.PrepareAndSaveImage` (`CLISolver.cs:126-134`) uses the same default. CFITSIO is needed by M5 (archived-FITS reading) and has C `long` bugs: patch P5 |
| `DllLoader.LoadDll` / `IsX86` | 5 lines | `CfitsioNative.cs:21`, `LibRawConverter.cs:104` (load); `FITSHeader.cs:773`, `CFitsioFITS.cs:368`, `XISFHeader.cs:686` (`IsX86()` label) | Load calls are already covered by M3a's `DllLoader.LoadDllFromAbsolutePath` guard. The label says "x64" on arm64, which is cosmetic (optional P6) |
| Process launch | 1 file | `BaseImageData.cs:174-222` runs `exiftool.exe` for DSLR sensor temperature (`$$SENSORTEMP$$` with RAW) | Leave: DSLR-only, wrapped in try/catch, logs and returns empty |
| Native **astrometry** on the save path | 5 call sites | `BaseImageData.cs:242` (`ExposureStart.ToMJD()` → `AstroUtil.GetJulianDate` → `SOFA.Dtf2d`), `FITSHeader.cs:518,525` (`MJD-OBS`, `MJD-AVG`), `ImageMetaData.cs:201,238` (`Coordinates.Transform(J2000)`) | Hard dependency on `libsofa.dylib` (and NOVAS for transforms) plus resolver registration **before the first save** (proven E10) |

### (c) Packages

| Package | Version | Source | osx-arm64 |
|---|---|---|---|
| Iconic.Zlib.Netstandard | 1.0.0 | upstream | managed (lib/netstandard1.3) |
| K4os.Compression.LZ4 | 1.3.8 | upstream | managed |
| Nito.AsyncEx | 5.1.2 | upstream | managed |
| System.Data.SqlClient | 4.9.1 | upstream (EF6 pin) | `runtimes/unix/lib` managed; no native SNI |
| ZstdSharp.Port | 0.8.8 | upstream | managed port |
| **System.Drawing.Common** | 10.0.10 | **mac-only addition**; Windows gets it via `UseWindowsForms` | managed `lib/net10.0`; throws on non-Windows at first GDI+ use. Same version as `NINA.MGEN.csproj` |
| (transitive) Accord, Accord.Math, Accord.Statistics 3.8.2-alpha; OxyPlot.Core 2.2.0; CommunityToolkit.Mvvm 8.4.2 | | via Accord.Imaging.Mac / Core.Mac | managed |

### (d) Vendor code

None. `RawConverterFactory` only knows LibRaw. The `FileCamera`/`SimulatorCamera` image loaders that use the WPF codecs live in Equipment (excluded) and WPF.Base (not compiled).

---

## 3a. Accord.Imaging.Mac

```xml
<PropertyGroup>
  <AssemblyName>Accord.Imaging</AssemblyName>
  <RootNamespace>Accord.Imaging</RootNamespace>
  <TargetFramework>netstandard2.0</TargetFramework>   <!-- as upstream; net10.0 fails (E9) -->
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <Version>3.5.3</Version>
  <FileVersion>3.8.3.6155</FileVersion>
  <IsPackable>false</IsPackable>
  <UpstreamDir>$(NinaRoot)Accord.Imaging/</UpstreamDir>
</PropertyGroup>
<ItemGroup>
  <Compile Include="$(UpstreamDir)**/*.cs" Exclude="$(UpstreamDir)obj/**;$(UpstreamDir)bin/**" LinkBase="Upstream" />
  <PackageReference Include="Accord" Version="3.8.2-alpha" />
  <PackageReference Include="Accord.Math" Version="3.8.2-alpha" />
  <PackageReference Include="Accord.Statistics" Version="3.8.2-alpha" />
  <PackageReference Include="System.Drawing.Common" Version="10.0.2" />
</ItemGroup>
```

- Use a shadow project, not a `ProjectReference` to `Accord.Imaging/Accord.Imaging (NETStandard).csproj`. The upstream project would build into the upstream tree and runs `GeneratePackageOnBuild=True` into `$(SolutionDir)publish`.
- CA1416 cannot see into netstandard2.0. That is acceptable, because NINA's call sites into Accord are already flagged in NINA.Image.
- Licence: LGPL-2.1, shipped as a separate assembly as upstream does.

---

## 4. NINA.Equipment.Mac

### (a) Include list (opt-in, so new upstream vendor files stay out automatically)

```xml
<ItemGroup>
  <Compile Include="$(UpstreamDir)Properties/AssemblyInfo.cs" LinkBase="Upstream" />
  <!-- Device-independent contracts: device, mediator and view-model interfaces, capture model, exceptions -->
  <Compile Include="$(UpstreamDir)Interfaces/**/*.cs;$(UpstreamDir)Model/**/*.cs;$(UpstreamDir)Exceptions/**/*.cs" LinkBase="Upstream" />
  <!-- ISVBonySDK uses the excluded SVBony SDK namespace; CaptureSequenceList is the legacy simple sequencer (only NINA/ViewModel/Sequencer/SimpleSequence uses it) -->
  <Compile Remove="$(UpstreamDir)Interfaces/ISVBonySDK.cs;$(UpstreamDir)Model/CaptureSequenceList.cs" />
  <!-- Shared device plumbing, and every *Info class: the mediator interfaces carry them -->
  <Compile Include="$(UpstreamDir)Equipment/DeviceInfo.cs;$(UpstreamDir)Equipment/DummyDevice.cs;$(UpstreamDir)Equipment/OfflineDevice.cs;$(UpstreamDir)Equipment/GuideStepsHistory.cs;$(UpstreamDir)Equipment/My*/*Info.cs" LinkBase="Upstream" />
  <!-- ZWO ASI camera -->
  <Compile Include="$(UpstreamDir)Equipment/MyCamera/ASICamera.cs;$(UpstreamDir)Equipment/MyCamera/ASICameras.cs;$(UpstreamDir)Equipment/MyCamera/PersistSettingsCameraDecorator.cs;$(UpstreamDir)SDK/CameraSDKs/ASISDK/ASICameraDll.cs" LinkBase="Upstream" />
  <!-- Mount-only dithering (DirectGuider.cs:228-265) and the no-op guider; PHD2 event POCOs are part of the IGuider contract -->
  <Compile Include="$(UpstreamDir)Equipment/MyGuider/DirectGuider.cs;$(UpstreamDir)Equipment/MyGuider/DitherOffsetSelector.cs;$(UpstreamDir)Equipment/MyGuider/DummyGuider.cs;$(UpstreamDir)Equipment/MyGuider/RMSError.cs;$(UpstreamDir)Equipment/MyGuider/LockPosition.cs;$(UpstreamDir)Equipment/MyGuider/PHD2/PhdEvents/*.cs" LinkBase="Upstream" />
  <!-- Image metadata from device infos -->
  <Compile Include="$(UpstreamDir)Utility/ImageMetaDataExtension.cs" LinkBase="Upstream" />
</ItemGroup>
```

Kept: 124 files (10,525 lines). The interfaces cover `ICamera`, `ITelescope`, `IFocuser`, `IGuider`, `IDevice` and all `I*Mediator`/`I*Consumer`/`I*VM`. Excluded: 178 files.

| Excluded group | Files | Reason |
|---|---|---|
| `SDK/**` except `ASISDK/ASICameraDll.cs` | 64 | Vendor SDK bindings: ASI EAF/EFW 2, ASTPAN 5, Atik 1, Canon EDSDK 2, FLI 3, Moravian 9, PlayerOne 6, QHY 4, SBIG 5, SVBony 5, ToupTek family 7, Oasis 2, flat panels 13 |
| `Equipment/MyCamera/*` except ASI and the decorator | 21 | ASCOM, AlpacaDirect, Atik, Canon, FLI, `FileCamera`*, `GenericCamera` (PlayerOne/SVBony/ASTPAN base), Moravian, Nikon, QHY (WMI), SBIG (gRPC named pipes, Castle), SVBony, ToupTek family + 7 wrappers |
| `Equipment/MyGuider/*` other | 18 | MetaGuide (10; user32 window messages), SkyGuard (3; `HttpListener`, WPF `Dispatcher`), MGEN (1; NINA.MGEN plus a GDI+ `Bitmap`), PHD2 client (4)* |
| `MyFocuser` (5), `MyTelescope` (2) device classes | 7 | ASI EAF, Oasis, ToupTek, ASCOM, Alpaca. The mount and the #1209 focuser come from the native LX200 driver (M4) |
| FilterWheel 14, FlatDevice 7, Rotator 3, Dome 3, Switch 7, Weather 6, SafetyMonitor 2, GPS 7, Planetarium 7 | 56 | Not on this rig. Their `*Info` classes stay |
| `Equipment/AscomDevice.cs`, `AscomLogger.cs`, `AlpacaDirectSettings.cs` | 3 | ASCOM COM/Alpaca bases |
| `Utility/ASCOMInteraction.cs`, `AlpacaInteraction.cs`, `FilterManager.cs`, `UsbDeviceWatcher.cs` | 4 | Discovery for excluded drivers. `UsbDeviceWatcher` is WMI; keep `IUsbDeviceWatcher` and add a mac implementation later (BLD-M5) |
| `Converter/*` | 3 | WPF `IValueConverter` |
| `Interfaces/ISVBonySDK.cs`, `Model/CaptureSequenceList.cs` | 2 | See the comment in the snippet |

\* Optional dev aids, compile-tested WPF-flavoured. `Equipment/MyCamera/FileCamera.cs` (feeds a folder as a camera, for indoor tests) and `Equipment/MyGuider/PHD2/*.cs` (networked PHD2). Together they add one stand-in: `Microsoft.Win32.OpenFolderDialog` (`.ctor`, `InitialDirectory` set, `FolderName` get, `CommonDialog.ShowDialog()`). `OpenFileDialog`, `ResizeMode` and `WindowStyle` are already in WpfCompat, and `WindowService.ShowDialog` is already a Core mac replacement (it throws).

### (b) Windows-only inventory (kept 124 files)

| Category | Count | Where | Classification |
|---|---|---|---|
| `using System.Windows*` | 7 lines / 5 files | `Interfaces/Mediator/IImagingMediator.cs:24`, `Interfaces/ViewModel/IDeviceVM.cs:19`, `IDockableVM.cs:16-17`, `IImageControlVM.cs:21-22`, `IImagingVM.cs:23` | `System.Windows.Input` (`ICommand`) is in .NET itself (System.ObjectModel). The rest need WpfCompat types by name only |
| WPF types | 2 types, 0 members | `GeometryGroup` (`IDockableVM.cs:25`), `BitmapSource` (`IImagingMediator.cs:56`, `IImagingVM.cs:32`, `IImageControlVM.cs:42`) | WpfCompat, signature-only (§6) |
| `System.Drawing` | 2 lines | `ASICamera.cs:22`, `ASICameraDll.cs:3` | `System.Drawing.Primitives` (`Point`/`Size`/`Rectangle`), which is cross-platform. Metadata confirms there is no System.Drawing.Common reference |
| `[DllImport]` | 30 | `ASICameraDll.cs` → `ASICamera2.dll` (CLong fix already on `macos`, ba53e8547) | `NativeLibraries` already maps `ASICamera2.dll`. The host must call `NativeLibraries.Register(typeof(ASICameras).Assembly)` (E10 used an equivalent scratch resolver) |
| `DllLoader` | 1 | `ASICameraDll.cs:23`, inside its own `OperatingSystem.IsWindows()` guard | Now redundant with M3a's central guard; harmless |
| CA1416 (inventory on) | 0 | | |
| "COM" regex hits | 2, both false positives | `DeviceInfo.cs:52` (`Activator.CreateInstance(this.GetType())`), `IToupTekAlikeCameraSDK.cs:483` (a comment) | none |
| Registry, WMI, WinForms, processes, env-var paths, STA | 0 | | |

### (c) Packages

| Package | Upstream | Mac | Note |
|---|---|---|---|
| CommunityToolkit.Mvvm 8.4.2 | transitive via Core | **reference explicitly** | `DeviceInfo.cs:23` and `DomeInfo.cs:23` are `partial` with `[ObservableProperty]`, so the source generator must run here |
| System.Data.SqlClient 4.9.1 | yes | keep | upstream's EF6 pin; managed on unix |
| Accord.Statistics (`DitherOffsetSelector.cs:15`), Newtonsoft.Json, OxyPlot.Core | transitive | transitive (Image.Mac/Core.Mac) | managed |
| ASCOM.Alpaca.Components/Device 3.1.0 | yes | **drop** | 22 files use them, all excluded |
| ASCOM.Com.Components 3.1.0 | yes | **drop** | COM, Windows-only; 18 files, all excluded |
| Castle.Core 5.2.1 / AsyncInterceptor 2.1.0 | yes | **drop** | only `SBIGCameraASCOMService.cs` |
| GrpcDotNetNamedPipes 3.1.0 | yes | **drop** | only `SBIGCamera.cs` |
| NJsonSchema 11.6.1 | yes | **drop** | only `MyGPS/Gpsd.cs` |
| SharpGIS.NmeaParser 2.2.2 | yes | **drop** | only `MyGPS/NMEAGps.cs` |
| Grpc.Tools 2.83.0, Google.Protobuf.Tools 3.35.1 | yes | **drop** | Equipment has no `<Protobuf>` items; 2.83.0's macOS protoc is x86_64-only |
| Microsoft.NETFramework.ReferenceAssemblies 1.0.3 | yes | drop | build-time, .NET Framework only |
| ProjectReferences `nikoncswrapper`, `NINA.MGEN` | yes | **drop** | only excluded files use them |

### (d) Cutting the vendors cleanly

- **Inside NINA.Equipment** only two kept files touch vendor code. `ISVBonySDK.cs` is excluded. `GuideStepsHistory.cs:235` uses `PhdEventGuideStep`, so `PHD2/PhdEvents/*` (POCOs, no WPF) is kept. Everything else excluded is a leaf, and E4/E6 confirm nothing kept links to it.
- **The choosers are not in NINA.Equipment.** They live in `NINA.WPF.Base/ViewModel/Equipment/*ChooserVM.cs`, which M3b does not compile, and they reference every vendor:

  | Upstream chooser | Excluded types it references | Mac counterpart (engine glue, about 40 lines each) |
  |---|---|---|
  | `Camera/CameraChooserVM.cs:66-347` | Altair :81, Atik :96, FLI :110, QHY :129, ToupTek :158, Ogma :173, Omegon :188, Risingcam :203, MallinCam :218, SVBony :243, SBIG :258, ASCOM :289, Alpaca :301, Canon EDSDK :313, Nikon :337, `FileCamera`/`SimulatorCamera` :346-347 | `DummyDevice` + the ASI loop (:66-79, ported verbatim) |
  | `Telescope/TelescopeChooserVM.cs:35-74` | ASCOM :54, Alpaca :64, plugin providers :43 | `DummyDevice` + `Lx200Telescope` (M4) |
  | `Focuser/FocuserChooserVM.cs:40-122` | ASI EAF :64, Oasis :78, ToupTek :93, ASCOM :104, Alpaca :112 | `DummyDevice` + `Lx200Focuser` (M4) |
  | `Guider/GuiderChooserVM.cs:47-82` | PHD2 :52, MetaGuide :54, SkyGuard :55, MGEN :59-66 | `DummyGuider` + `DirectGuider` (:51, :53) |

  - The mac choosers implement `NINA.Equipment.Interfaces.ViewModel.IDeviceChooserVM` (kept) directly.
  - They port `DeviceChooserVM.DetermineSelectedDevice` (`NINA.WPF.Base/ViewModel/Equipment/DeviceChooserVM.cs:98-111`) faithfully, with a citation.
  - They drop the STA setup-dialog thread (`:88`, `SetApartmentState`), which throws on macOS.
  - Like `CameraVM.cs:351`, the host wraps the selected camera in `PersistSettingsCameraDecorator`.
  - **The NINA app's `IoCBindings` is not compiled.** The mac host binds only what the kept code needs. In particular it binds no `UsbDeviceWatcher`, `ISbigSdk` or `IEquipmentProviders<T>` plugin scan.
  - This glue belongs to the engine host (M7/M8). It is listed here because it is what keeps the vendor cut link-clean.

---

## 5. NINA.Platesolving.Mac

### (a) Include/exclude

```xml
<Compile Include="$(UpstreamDir)**/*.cs" Exclude="$(UpstreamDir)obj/**;$(UpstreamDir)bin/**;$(UpstreamDir)publish/**" LinkBase="Upstream" />
<!-- Cygwin-bound (cmd.exe + bash.exe): replaced by MacReplacements/LocalPlateSolver.cs, same type and ctor, runs Homebrew solve-field -->
<Compile Remove="$(UpstreamDir)Solvers/LocalPlateSolver.cs" />
```

| Mac file | Purpose | Delete when |
|---|---|---|
| `MacReplacements/LocalPlateSolver.cs` | `internal class LocalPlateSolver : CLISolver` with ctor `(string cygwinRoot)`, so `PlateSolverFactory.cs:58` links unchanged. `CLISolver` is `internal`, but the replacement compiles into the same `NINA.Platesolving` assembly | never (upstream stays Cygwin) |
| `MacReplacements/AscomNamespaceAnchor.cs` | `namespace ASCOM { internal static class MacNamespaceAnchor { } }`. `TheSkyXImageLinkSolver.cs:15` has an **unused** `using ASCOM;` that resolved upstream only because Equipment's ASCOM packages flow in transitively. The anchor compiles (E7), which proves nothing in the file uses an ASCOM type | P3 merged |
| `MacReplacements/HttpDownloadImageRequest.cs` | only if `NINA.Core.Mac` keeps excluding it: `internal sealed class HttpDownloadImageRequest : HttpRequest<BitmapSource>` in `NINA.Core.Utility.Http`, whose `Request` throws `PlatformNotSupportedException`. Its only user is the dead private `AstrometryPlateSolver.GetJobImage` (`:118-121`, no callers in the repo) | P2 merged, or Core.Mac re-includes the file |
| `Properties/AssemblyInfo.Mac.cs` | `[assembly: InternalsVisibleTo("NINA.Mac.Platesolving.Test")]`, so tests reach the internal `CLISolver` and solvers. Upstream `AssemblyInfo.cs:30` only names `NINA.Test`, and `GenerateAssemblyInfo=false` in Engine.props disables `<InternalsVisibleTo>` items | never |

**Why replace by name, not by factory:** consumers bypass DI. `NINA.Sequencer/Trigger/Platesolving/PlatesolvingImageFollower.cs:136-137` calls `PlateSolverFactory.GetPlateSolver/GetBlindSolver`, `CenterAfterDriftTrigger.cs:142` does `new PlateSolverFactoryProxy()`, and `NINA.WPF.Base/ViewModel/MeridianFlipVM.cs:282-283` does the same. Only a same-name type reaches all of them.

### (b) Windows-only inventory (25 kept + replacement)

| Category | Count | Where | Classification |
|---|---|---|---|
| `using System.Windows*` | 3 / 3 files | `CaptureSolver.cs:22` (unused `System.Windows.Media`), `PlateSolveProgress.cs:15`, `AstrometryPlateSolver.cs:27` | WpfCompat namespaces |
| `BitmapSource` | 2 lines | `PlateSolveProgress.cs:20` (`Thumbnail`), `AstrometryPlateSolver.cs:118` | WpfCompat type only. `CaptureSolver.cs:61` awaits `IRenderedImage.GetThumbnail()` (null on mac unless `TransformedBitmap` is implemented, §6) |
| COM late binding | 7 lines / 1 file | `Dc3PinPointSolver.cs:62-79,174-175` (`dynamic`, `Type.GetTypeFromProgID`) — CA1416 ×2 at :78, :174 | Leave: reached only if PinPoint is chosen. The mac UI never offers it |
| Process launch | 3 files | `CLISolver.cs:148-189` (generic `ProcessStartInfo`; a single `Arguments` string, which .NET splits with Windows quoting rules on Unix too, so quoted paths with spaces work); `LocalPlateSolver.cs:63,69-78` (`cmd.exe`, `bash.exe`) | `CLISolver` is kept; `LocalPlateSolver` is replaced |
| `FileVersionInfo` | 1 | `ASTAPSolver.cs:171` | **bug 1**, patch P1 |
| `.exe` / `cmd.exe` names | 4 lines / 2 files | `LocalPlateSolver.cs:28-29,71`; `CLISolver.cs:149` compares against `"cmd.exe"` | replaced; the comparison is harmless |
| Backslash literals | 3 / 2 files | `AllSkyPlateSolver.cs:74`, `LocalPlateSolver.cs:63,75` (`Replace("\\","/")`) | harmless no-ops on Unix |
| `System.Drawing` | 1 | `TheSkyXImageLinkSolver.cs:25` | Primitives only (metadata confirms) |
| CA1416 (inventory on) | 2 | `Dc3PinPointSolver.cs:78,174` | as above |

### (c) Packages

`System.Data.SqlClient` 4.9.1 only, as upstream; everything else comes transitively. No native assets.

### (d) Solver selection and the two known solver bugs

- **Keep `PlateSolverFactory.cs` unchanged.** PlateSolve2/3, ASPS, PinPoint and TheSkyX stay compiled. They are managed and fail at run time only if selected. The mac UI offers ASTAP (near), `LOCAL` (solve-field, blind) and `ASTROMETRY_NET` (online).
- **Bug 1, ASTAP version check** (`ASTAPSolver.cs:171-178`). On Unix `FileVersionInfo` reads only managed metadata, so `FileVersion` is null for the Mach-O `astap`. With the default `DownSampleFactor = 0` (`PlateSolveSettings.cs:53`) every solve then throws. Fix: upstream guard P1, a Windows no-op (compile-tested, E8). Excluding the 191-line `ASTAPSolver.cs` for a mac copy is clearly worse.
- **Bug 2, Cygwin-bound local solver** (`LocalPlateSolver.cs:27-75`). It runs `cmd.exe` → `bash --login -c '/usr/bin/solve-field …'`, then `wcsinfo` the same way. It also passes `-center` (`:46`), which `getopt_long` parses as `-c enter` (code tolerance, `atof` gives 0), so `--crpix-center` is never set (verify SLV-04). Mac replacement spec:
  - Ctor `(string cygwinRoot)`: treat the setting as the astrometry.net **bin directory**, defaulting to `/opt/homebrew/bin` when empty. Call `base(Path.Combine(dir, "solve-field"))`, so `CLISolver.StartCLI`'s existence check (`CLISolver.cs:149-151`) produces upstream's "executable not found" error.
  - `GetArguments`: a faithful port of `LocalPlateSolver.cs:32-64`, with these changes:
    - The option list is identical (`--overwrite`, `--index-xyls none`, `--corr none`, `--rdls none`, `--match none`, `--new-fits none`, `--objs`, `--no-plots`, `--resort`, `--downsample`, `--scale-units arcsecperpix`, `-L/-H` = scale ∓ 0.2, `--ra/--dec/--radius`), except `-center` becomes `--crpix-center`.
    - There is no shell wrapper. The quoted image path is the last argument, and `--config <cfg>` is added only if a config is set; `add_path` takes spaces verbatim, per verify SLV-04.
    - `--downsample 0` stays harmless (`atoi` gives 0).
  - `ReadResult`: a faithful port of `:66-150` (the same `wcsinfo` key parsing, `WorldCoordinateSystem`, `Flipped`, `PositionAngle`, `Pixscale`/`Radius`), running `<dir>/wcsinfo "<file.wcs>"` directly.
  - `GetOutputPath` is unchanged (`.wcs` next to the image). Override `GetSideCarFilePaths` to also delete `.axy`/`.solved`. This is a deliberate, documented difference: upstream leaves them in the working directory.
  - Tests: an argument-builder test (no `-center`; `-L 0.28 -H 0.68` at 0.479″/px), and a `ReadResult` test against a `wcsinfo` stdout fixture recorded once from a real `solve-field` run on an archived frame (M6).
- **Profile settings for this rig** (data, not code; recorded so M6 doesn't rediscover them):
  - `ASTAPLocation` = `/Applications/ASTAP.app/Contents/MacOS/astap`. The default `%programfiles%\astap\astap.exe` (`PlateSolveSettings.cs:65-68`) never expands on macOS, so it ends up empty.
  - `DownSampleFactor` 2 (OSC bin-2 frames keep the Bayer mosaic, verify SLV-08).
  - `BlindSolverType` = `LOCAL`, never ASTAP `-r 180` (`ASTAPSolver.cs:158`, SLV-M2).
  - `CygwinLocation` = `/opt/homebrew/bin`.
  - Consider a shorter `SolverTimeout` than the 10 min in `CLISolver.cs:50` (a mac-side override in the replacement only).

---

## 6. WpfCompat additions needed by M3b (exact members)

All are additions to `mac/src/NINA.Mac.WpfCompat` (M3a-owned). M3a's rules apply: WPF namespaces, WPF-exact public signatures and enum values (enforced by `WpfApiSurfaceTest`), and only what the compile requires.

Already present in WpfCompat and used by M3b unchanged:
- `Threading.Dispatcher.CurrentDispatcher`, `BeginInvoke(DispatcherPriority, Delegate)`, `DispatcherOperation.GetAwaiter()`, `DispatcherObject.Dispatcher`, `DispatcherPriority.Normal` (`RenderedImage.cs:117,133`)
- `Application.Current` (`RenderedImage.cs:133`)
- `Media.Color` A/R/G/B (`BahtinovAnalysis.cs:29,34`)
- `Microsoft.Win32.OpenFileDialog`, `ResizeMode`, `WindowStyle` (optional groups only)

New types (27). The member lists are the union of every MemberRef found by the metadata scan:

| Namespace | Type | Members needed | Behaviour |
|---|---|---|---|
| `System.Windows` | `struct Int32Rect` | `static Empty`; recommended ctor `(int x, int y, int width, int height)`, `X/Y/Width/Height`, `IsEmpty` | value type |
| `System.Windows` | `abstract class Freezable` | `Freeze()`, `IsFrozen` | flag only |
| `System.Windows.Media` | `abstract class ImageSource : Freezable` | `Width`, `Height` (double, DIPs = pixels at 96 dpi), `Metadata` | |
| `System.Windows.Media` | `abstract class ImageMetadata`, `class ColorContext`, `abstract class Transform` | signature-only | |
| `System.Windows.Media` | `sealed class ScaleTransform : Transform` | ctor `(double scaleX, double scaleY)`, `ScaleX`, `ScaleY` | value holder |
| `System.Windows.Media` | `struct PixelFormat` | `BitsPerPixel`, `==`, `!=`, `Equals`, `GetHashCode`, `ToString` | real |
| `System.Windows.Media` | `static class PixelFormats` | `Bgr24`, `Bgr32`, `Bgr565`, `Bgra32`, `Gray16`, `Gray8`, `Indexed8`, `Pbgra32`, `Rgb48` | real (bpp 24/32/16/32/16/8/8/32/48) |
| `System.Windows.Media` | `class GeometryGroup` | signature-only (`IDockableVM.cs:25`; Sequencer icons will need it in M7 too) | |
| `…Media.Imaging` | `abstract class BitmapSource : ImageSource` | `PixelWidth`, `PixelHeight`, `Format`; `CopyPixels(Array, int stride, int offset)`; `CopyPixels(Int32Rect, IntPtr, int bufferSize, int stride)`; `static Create(int, int, double, double, PixelFormat, BitmapPalette, Array, int)`; `static Create(int, int, double, double, PixelFormat, BitmapPalette, IntPtr, int, int)` | **real managed pixel buffer.** Makes `CreateSourceFromArray` (`ImageUtility.cs:221-229`), `RenderImage`, and `ImageArrayExposureData.FromBitmapSource` (Gray8/Gray16) work. E10: `RenderImage ok: 1920x1080 Gray16` |
| `…Media.Imaging` | `sealed class BitmapPalette` | signature-only | |
| `…Media.Imaging` | `sealed class FormatConvertedBitmap : BitmapSource` | ctors `()` and `(BitmapSource, PixelFormat, BitmapPalette, double)`; `Source`, `DestinationFormat` set; `BeginInit()`, `EndInit()` | real for Gray16 → Gray8 (`ImageUtility.cs:211-219`); other conversions throw `NotSupportedException` |
| `…Media.Imaging` | `sealed class WriteableBitmap : BitmapSource` | ctor `(BitmapSource)` | copy |
| `…Media.Imaging` | `sealed class TransformedBitmap : BitmapSource` | ctor `(BitmapSource, Transform)` | either throw (thumbnail null, logged; E10) or nearest-neighbour downscale for `ScaleTransform` (gives thumbnails to `CaptureSolver.cs:61`) |
| `…Media.Imaging` | `sealed class BitmapMetadata : ImageMetadata` | ctor `(string)`, `Title` get/set, `ApplicationName` set | value holder |
| `…Media.Imaging` | `abstract class BitmapFrame : BitmapSource` | `static Create(BitmapSource, BitmapSource, BitmapMetadata, ReadOnlyCollection<ColorContext>)` | throws `PlatformNotSupportedException` |
| `…Media.Imaging` | `abstract class BitmapEncoder`, `sealed class TiffBitmapEncoder` | `Frames` get, `Save(Stream)`; ctor, `Compression` set | `Save` throws, so TIFF save (`BaseImageData.cs:427-471`) is unavailable on mac |
| `…Media.Imaging` | `enum TiffCompressOption` | `None`, `Lzw`, `Zip` used; WPF's full set and values | |
| `…Media.Imaging` | `abstract class BitmapDecoder`; `GifBitmapDecoder`, `TiffBitmapDecoder`, `JpegBitmapDecoder`, `PngBitmapDecoder` | `Frames` get; ctor `(Uri, BitmapCreateOptions, BitmapCacheOption)` | ctors throw, so GIF/TIFF/JPEG/PNG load (`BaseImageData.cs:571-589`) is unavailable |
| `…Media.Imaging` | `[Flags] enum BitmapCreateOptions`, `enum BitmapCacheOption` | `PreservePixelFormat`, `OnLoad` used; WPF's full set and values | |
| `…Media.Imaging` | `BitmapImage` *(only if Core.Mac re-includes `HttpDownloadImageRequest.cs`)* | `BeginInit`, `EndInit`, `StreamSource`, `CacheOption` | `EndInit` throws |

**Ownership.** `NINA.Astrometry` needs `BitmapSource` too:
- `SkyObjectBase.cs:20,23,233-238`
- `Interfaces/IDeepSkyObject.cs:33`
- `DeepSkyObject.cs:99-105`
- `DatabaseInteraction.cs:274-278`

M3a has since added `mac/src/NINA.Mac.WpfCompat/Media/Imaging/BitmapSource.cs` (seen at about 18:45). It is an abstract placeholder with a protected ctor and a no-op `Freeze()`, and its comment says "pixel access arrives when NINA.Image is ported". M3b should **extend that file**, not add a second one:
1. Add `Freezable` and `ImageSource` as in WPF (`BitmapSource : ImageSource : Freezable`).
2. Move `Freeze()` to `Freezable`, where WPF declares it, if `WpfApiSurfaceTest` accepts inherited members.
3. Add the members above. The rest of the family goes in sibling files under `Media/` and `Media/Imaging/`.

---

## 7. Proposed upstream patches (not applied; Windows no-ops or plain bug fixes)

**P1: `NINA.Platesolving/Solvers/ASTAPSolver.cs`.** Fixes bug 1. Compile-tested (E8). LF line endings, as the file has.

```diff
@@ -168,12 +168,15 @@
             if (!File.Exists(this.executableLocation)) {
                 throw new ASTAPValidationFailedException($"ASTAP executable not found at {this.executableLocation}");
             }
-            var astapVersionInfo = FileVersionInfo.GetVersionInfo(this.executableLocation);
-            if (astapVersionInfo.FileVersion == null) {
-                // Version below 0.9.1.0
-                // Only allows downsample in the range of 1 to 4
-                if (parameter.DownSampleFactor == 0) {
-                    throw new ASTAPValidationFailedException($"ASTAP version below 0.9.1.0 does not allow auto downsample factor value of 0! Please update your ASTAP version!");
+            // FileVersionInfo only reads Windows version resources; elsewhere FileVersion is always null
+            if (OperatingSystem.IsWindows()) {
+                var astapVersionInfo = FileVersionInfo.GetVersionInfo(this.executableLocation);
+                if (astapVersionInfo.FileVersion == null) {
+                    // Version below 0.9.1.0
+                    // Only allows downsample in the range of 1 to 4
+                    if (parameter.DownSampleFactor == 0) {
+                        throw new ASTAPValidationFailedException($"ASTAP version below 0.9.1.0 does not allow auto downsample factor value of 0! Please update your ASTAP version!");
+                    }
                 }
             }
         }
```

**P2–P4** were generated with `diff -u` from scratch copies and compile-tested together with P1 (E8b). All three files use LF line endings.

**P2: `NINA.Platesolving/Solvers/AstrometryPlateSolver.cs`.** Removes dead code: `GetJobImage` is private and has no callers anywhere in the repo. This drops Platesolving's only need for `HttpDownloadImageRequest` and for `System.Windows.Media.Imaging`.

```diff
--- a/NINA.Platesolving/Solvers/AstrometryPlateSolver.cs
+++ b/NINA.Platesolving/Solvers/AstrometryPlateSolver.cs
@@ -24,7 +24,6 @@
 using System.Text.RegularExpressions;
 using System.Threading;
 using System.Threading.Tasks;
-using System.Windows.Media.Imaging;
 using NINA.Core.Model;
 using NINA.Image.FileFormat;
 using NINA.Core.Locale;
@@ -38,7 +37,6 @@
         private const string JOBSTATUSURL = "/api/jobs/{0}";
         private const string JOBINFOURL = "/api/jobs/{0}/info/";
         private const string JOBCALIBRATIONURL = "/api/jobs/{0}/calibration/";
-        private const string ANNOTATEDIMAGEURL = "/annotated_display/{0}";
 
         private string _apiurl;
         private string _apikey;
@@ -115,11 +113,6 @@
             return JObject.Parse(response);
         }
 
-        private Task<BitmapSource> GetJobImage(string jobid, CancellationToken canceltoken) {
-            var request = new HttpDownloadImageRequest(_apiurl + ANNOTATEDIMAGEURL, jobid);
-            return request.Request(canceltoken);
-        }
-
         private async Task<string> GetAuthenticationToken(CancellationToken cancelToken) {
             JObject authentication = await Authenticate(cancelToken);
             var status = authentication.GetValue("status");
```

**P3: `NINA.Platesolving/Solvers/TheSkyXImageLinkSolver.cs`.** Removes an unused using. E7 and E8b show no ASCOM type is used.

```diff
--- a/NINA.Platesolving/Solvers/TheSkyXImageLinkSolver.cs
+++ b/NINA.Platesolving/Solvers/TheSkyXImageLinkSolver.cs
@@ -12,7 +12,6 @@
 
 #endregion "copyright"
 
-using ASCOM;
 using NINA.Astrometry;
 using NINA.Core.Enum;
 using NINA.Core.Model;
```

**P4: `NINA.Platesolving/Solvers/LocalPlateSolver.cs`.** Fixes Windows too. On mac the file is replaced anyway.

```diff
--- a/NINA.Platesolving/Solvers/LocalPlateSolver.cs
+++ b/NINA.Platesolving/Solvers/LocalPlateSolver.cs
@@ -43,7 +43,7 @@
             options.Add("--match none");
             options.Add("--new-fits none");
             //options.Add("-C cancel--crpix");
-            options.Add("-center");
+            options.Add("--crpix-center");
             options.Add($"--objs {parameter.MaxObjects}");
             options.Add("--no-plots");
             options.Add("--resort");
```

**P5 (M5 prerequisite, not compile-tested here): CFITSIO C `long` on LP64.** This is the same defect class as the ZWO fix in ba53e8547.
- `CfitsioNative.cs`: `ffgpxv` `int[] firstpix` (`:158`) and `ffcrim` `int[] naxes` (`:442`) become `CLong[]`. `ffgisz` `out int[] naxes` (`:147`, unused) is fixed the same way or deleted. `ffgkyj` `out long value` (`:170`, `:214`) becomes `out CLong`; this also fixes Windows, where C `long` is 4 bytes.
- Callers:
  - `CfitsioNative.cs:382` `new int[naxes]`
  - `CFitsioFITS.cs:30,37,44,51,58` `new int[] { width, height }`
  - `CFitsioFITSReader.cs:72,91`
- `ffppr` (`LONGLONG`) is already correct.
- Pair it with a `NativeLibraries` entry `cfitsionative.dll` → `libcfitsio` (Homebrew cfitsio 4.7 has arm64 bottles, per verify NAT-4), and a read/write round-trip test.

**P6 (optional, cosmetic).** `FITSHeader.cs:773`, `CFitsioFITS.cs:368` and `XISFHeader.cs:686` write `(x64)` on arm64. Use `RuntimeInformation.ProcessArchitecture`.

Already applied by M3a in the working tree: the `DllLoader.LoadDllFromAbsolutePath` guard. It covers `CfitsioNative.cs:21`, `LibRawConverter.cs:104`, `SOFA.cs:29` and `NOVAS.cs:44`.

---

## 8. Coordination items with M3a (in priority order)

1. **SOFA/NOVAS before the first save.** `BaseImageData.GetImagePatterns` (`:242`) and the FITS/XISF headers call SOFA on every save, and `ImageMetaData` coordinate setters call NOVAS/SOFA. The host must call `NativeLibraries.Register` for the NINA.Astrometry assembly, and for NINA.Equipment (`ASICamera2.dll`), at startup.
   - E10's first attempt died in `SOFA..cctor`: the scratch Core predated M3a's `DllLoader` guard, so `kernel32` was called.
   - After rebuilding the scratch Core with the guard, and with a SOFA/NOVAS resolver registered, the save worked and `MJD-OBS` was right.
   - A save without the resolver was not run. The expected failure is a `DllNotFoundException` for `SOFA_2023_10_11`.
2. **One owner for the imaging stand-ins** (§6). Astrometry.Mac already added the `BitmapSource` placeholder; M3b extends that same file with the members, base types and siblings.
3. **`HttpDownloadImageRequest.cs`** is excluded in Core.Mac ("Revisit with Platesolving"). Choose one:
   - P2 upstream plus the interim mac stub in Platesolving.Mac (recommended);
   - or re-include the file with a `BitmapImage` stand-in.
4. **The file-pattern separator fix is needed before any Siril folder layout** (`ImagePattern.cs`; M3a scope). On macOS, `CoreUtil.PATHSEPARATORS` (`CoreUtil.cs:31`) is `{ '/', '/' }`, so `\` is not a separator. E10 produced the flat file `LIGHT\Synthetic_ALPT_10.00s_0001.fits` from the pattern `$$IMAGETYPE$$\…`.
5. **CA1416.** Use M3a's `-p:MacPlatformInventory=true` for the M3b inventories. Expected baseline: Image 140, Equipment 0, Platesolving 2. A narrower alternative was also verified: `<Target Name="DropUpstreamEditorConfig" BeforeTargets="CoreCompile"><ItemGroup><EditorConfigFiles Remove="$(NinaRoot).editorconfig" /></ItemGroup></Target>` gives the same 140.
6. **Never name an assembly `WindowsBase`.** The .NET shared framework ships a `WindowsBase.dll` facade (`~/.dotnet/shared/Microsoft.NETCore.App/10.0.12/WindowsBase.dll`), so a same-name assembly fails to load in the default context (E10: "manifest definition does not match"). WpfCompat's name is already safe. The same failure would hit any third-party DLL compiled against real WPF.
7. **The host must set `CoreUtil.APPLICATIONTEMPPATH` first.** This is in M3a's host contract and was re-confirmed here: overriding `HOME` does not redirect it. `BaseSolver`'s static ctor (`BaseSolver.cs:34-38`) also creates `PlateSolver/` and `PlateSolver/Failed` there. The path contains a space, and both ASTAP `-f "…"` and the solve-field replacement quote it.

---

## 9. Ordering and effort (full-time-equivalent)

| Step | Depends on | Effort | Done when |
|---|---|---|---|
| 0. M3a lands Core.Mac + Profile.Mac (README says done), **Astrometry.Mac** (in the working tree since about 18:45; not built by this scout), the SOFA/NOVAS map (in `NativeLibraries.cs`) | n/a | (M3a) | Astrometry.Mac builds |
| 1. WpfCompat imaging additions (§6) plus WPF-surface and buffer tests | step 0 for ownership agreement | 1–1.5 d | §6 types pass `WpfApiSurfaceTest`; `Create`/`CopyPixels` round-trip tests |
| 2. Accord.Imaging.Mac | none (parallel with 1) | 0.25 d | 0 errors (E9) |
| 3. NINA.Image.Mac + tests | 0, 1, 2 | 0.5 d + 0.5 d tests | 0 errors; FITS header test (§10); Siril check |
| 4. NINA.Equipment.Mac | 3 | 0.5 d | 0 errors, CA1416 0; `ASICameras.Count` via `NativeLibraries` |
| 5. NINA.Platesolving.Mac (anchor, interim stub, P1 proposed) | 4 | 0.5 d | 0 errors, CA1416 2 |
| 6. `LocalPlateSolver` mac replacement + tests | 5 | 1–2 d (may slip into M6) | argument and parse tests; one real `solve-field` run on an archived frame |
| 7. Mac choosers + host composition (§4d) | 4 | 0.5 d (engine glue, M7/M8) | ASI listed; `DirectGuider` dithers through `ITelescopeMediator.PulseGuide` |

Total: **about 4–6.5 FTE days**, inside the plan's 1.5–3 week M3 time-box. M4 (`Lx200Telescope : ITelescope`, `Lx200Focuser : IFocuser`) needs step 4's interfaces, so step 4 is on M4's critical path; the protocol layer `NINA.Mac.Lx200` is standalone so far.

---

## 10. M3b definition of done (tests to write)

1. `mac/dotnet build mac/src/<each>.csproj`: 0 errors. Warning counts recorded in each project README.
2. Inventory build: CA1416 = Image 140 (the files in §3b), Equipment 0, Platesolving 2. A changed count after an upstream merge is a review trigger.
3. FITS test (no hardware). A synthetic 1920×1080 RGGB bin-2 frame goes through `ImageArrayExposureData` → `ToImageData` → `SaveToDisk` (legacy writer) into a temp dir with `APPLICATIONTEMPPATH` redirected. Assert:
   - `BAYERPAT='RGGB'`, `ROWORDER='TOP-DOWN'`, `XBINNING=2`, `XPIXSZ=5.8`;
   - `MJD-OBS` against SOFA for a fixed `ExposureStart`;
   - statistics mean 15500, median 10000.

   Optionally run `siril-cli -o -i <temp ini>`, `convert -debayer`, `stat` (R 40000 / G 10000 / B 2000), skipped when Siril is absent.
4. `ASICameras.Count` returns 0 with no camera through `NativeLibraries` (requires the staged dylib, as the M1 tests do).
5. Platesolving: ASTAP arguments for the rig, through the internal `GetArguments`:
   - `-fov` equals NINA's own `PlateSolveImageProperties.FoVH` for focal length 2500 mm, 2.9 µm × bin 2 and 1080 rows (about 0.1436°; assert against the computed value, not a literal);
   - `-z 2`;
   - `-r`/`-ra`/`-spd` present when coordinates are given.

   Plus the `LocalPlateSolver` replacement tests from §5d.
6. Unchanged-behaviour guards: the `RenderedImage.GetThumbnail()` result is accepted as null-or-image; `Stretch` is not called by the engine before M5 (documented).

---

## 11. Risks

| Risk | Likelihood / impact | Mitigation |
|---|---|---|
| GDI+ paths are reached at run time (`Stretch`, `Debayer`, `DetectStars`, annotation, Bahtinov) and throw `TypeInitializationException` (E10) | High until M5 / medium | Engine passes `detectStars:false` and doesn't stretch. M5 registers `NINA.Mac.ImageAnalysis` through `IPluggableBehaviorSelector<IStarDetection>`. CA1416 list (§3b) is the checklist |
| SOFA/NOVAS dylib or resolver missing, so every save fails | Medium / high | Coordination item 1; startup self-test computes one MJD |
| WpfCompat ownership collision (M3a and M3b both add `BitmapSource`) | Medium / low | §6 as a single change set; `WpfApiSurfaceTest` |
| The excluded `HttpDownloadImageRequest` breaks Platesolving silently later (dead code) | Low / low | P2 upstream; interim stub |
| Upstream adds a kept-file dependency on an excluded type (for example a new interface using a vendor enum) | Medium (about 8 commits/week) / low | Include list fails at compile time (good). Re-run the Appendix A scan after each merge and diff it |
| New upstream file in an included glob pulls in WPF or vendor code (`Interfaces/**`, `Model/**`) | Low / low | Compile failure shows it; add a `Compile Remove` with a reason |
| Same-name replacement plus a forgotten `Remove` gives CS0101 duplicate types | Low / low | Each `MacReplacements/` file names the upstream file it replaces |
| CFITSIO not yet usable (C `long` bugs, no dylib staged), so FITS **reading** fails | Certain until P5 / medium (M5's archived-FITS tests need it) | P5, plus `libcfitsio` in `stage-*.sh` (M3a/M5 scripts) |
| Accord stays on 2017 prerelease netstandard2.0 code with no analyzer coverage | Low / low | Its NINA call sites are already flagged; M5 replaces runtime use |
| `ASCOM` namespace anchor hides a future real ASCOM dependency in TheSkyX | Low / low | A real type use would still fail to compile (the anchor is internal and empty); P3 removes the need |
| LX200 focuser/telescope interfaces (`ITelescope`, `IFocuser`) change upstream mid-M4 | Low / medium | Interfaces are compiled unchanged, so drift shows at compile time |

---

## Appendix A: metadata scanner (reproduces E3)

A plain `net10.0` console with no packages (`System.Reflection.Metadata` is in the framework). It walks a compiled assembly and prints:
- every TypeRef into a target assembly;
- every MemberRef whose parent is such a type, or whose signature mentions one ("sig-leak");
- every declaration and local whose type is one;
- every `[DllImport]` target;

each with the user type and method. For the exact WpfCompat member list, run it on a **WPF-flavoured** build (`net10.0-windows`, `UseWPF`, `EnableWindowsTargeting`) of the include set, which builds on macOS (E1). Diff the output after upstream merges. Target prefixes default to `PresentationCore, PresentationFramework, WindowsBase, System.Xaml, System.Windows.Forms, System.Drawing.Common, System.Management, Microsoft.Win32.Registry, Microsoft.Win32.SystemEvents, ASCOM.Com, ASCOM.Common, ReachFramework, UIAutomation`.

<details><summary>Program.cs (scratch tool used for this plan)</summary>

```csharp
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// usage: scan <assembly.dll> [comma-separated assembly-name prefixes]
var path = args[0];
var targets = (args.Length > 1 ? args[1] : "PresentationCore,PresentationFramework,WindowsBase,System.Xaml,System.Windows.Forms,System.Drawing.Common,System.Management,Microsoft.Win32.Registry,Microsoft.Win32.SystemEvents,ASCOM.Com,ASCOM.Common,ReachFramework,UIAutomation")
    .Split(',', StringSplitOptions.RemoveEmptyEntries);
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();

var opMap = new Dictionary<ushort, System.Reflection.Emit.OperandType>();
foreach (var f in typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) {
    var oc = (System.Reflection.Emit.OpCode)f.GetValue(null);
    opMap[(ushort)oc.Value] = oc.OperandType;
}
string AsmOfTypeRef(TypeReferenceHandle h) {
    var tr = md.GetTypeReference(h); var scope = tr.ResolutionScope;
    while (scope.Kind == HandleKind.TypeReference) { tr = md.GetTypeReference((TypeReferenceHandle)scope); scope = tr.ResolutionScope; }
    return scope.Kind == HandleKind.AssemblyReference ? md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) : "<" + scope.Kind + ">";
}
string NameOfTypeRef(TypeReferenceHandle h) {
    var tr = md.GetTypeReference(h); var n = md.GetString(tr.Name);
    if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return NameOfTypeRef((TypeReferenceHandle)tr.ResolutionScope) + "+" + n;
    var ns = md.GetString(tr.Namespace); return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
}
bool IsTarget(string asm) => targets.Any(t => asm.StartsWith(t, StringComparison.OrdinalIgnoreCase));
var prov = new Prov(AsmOfTypeRef, NameOfTypeRef, IsTarget);
string TypeDefName(TypeDefinitionHandle h) {
    var td = md.GetTypeDefinition(h); var n = md.GetString(td.Name); var decl = td.GetDeclaringType();
    if (!decl.IsNil) return TypeDefName(decl) + "+" + n;
    var ns = md.GetString(td.Namespace); return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
}
var uses = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
void Use(string key, string user) { if (!uses.TryGetValue(key, out var s)) uses[key] = s = new SortedSet<string>(StringComparer.Ordinal); s.Add(user); }
string Describe(EntityHandle h, out bool hit) {
    hit = false;
    switch (h.Kind) {
        case HandleKind.TypeReference: { var a = AsmOfTypeRef((TypeReferenceHandle)h); hit = IsTarget(a); return $"[{a}] type {NameOfTypeRef((TypeReferenceHandle)h)}"; }
        case HandleKind.TypeSpecification: { prov.Hit = false; var s = md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(prov, null); hit = prov.Hit; return $"[spec] type {s}"; }
        case HandleKind.MemberReference: {
            var mr = md.GetMemberReference((MemberReferenceHandle)h); var parent = mr.Parent; string pdesc; bool ptarget = false; string pasm = "";
            if (parent.Kind == HandleKind.TypeReference) { pasm = AsmOfTypeRef((TypeReferenceHandle)parent); ptarget = IsTarget(pasm); pdesc = NameOfTypeRef((TypeReferenceHandle)parent); }
            else if (parent.Kind == HandleKind.TypeSpecification) { prov.Hit = false; pdesc = md.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(prov, null); ptarget = prov.Hit; pasm = "spec"; }
            else pdesc = parent.Kind.ToString();
            prov.Hit = false; string sig;
            if (mr.GetKind() == MemberReferenceKind.Method) { var ms = mr.DecodeMethodSignature(prov, null); sig = $"{ms.ReturnType} {pdesc}::{md.GetString(mr.Name)}({string.Join(", ", ms.ParameterTypes)})"; }
            else { sig = $"{mr.DecodeFieldSignature(prov, null)} {pdesc}::{md.GetString(mr.Name)}"; }
            if (ptarget) { hit = true; return $"[{pasm}] member {sig}"; }
            if (prov.Hit) { hit = true; return $"[sig-leak via {pasm}] member {sig}"; }
            return sig;
        }
        case HandleKind.MethodSpecification: {
            var ms = md.GetMethodSpecification((MethodSpecificationHandle)h); var inner = Describe(ms.Method, out hit);
            prov.Hit = false; var ins = ms.DecodeSignature(prov, null); if (prov.Hit) hit = true; return inner + "<" + string.Join(",", ins) + ">";
        }
    }
    return null;
}
foreach (var tdh in md.TypeDefinitions) {
    var td = md.GetTypeDefinition(tdh); var tname = TypeDefName(tdh);
    if (!td.BaseType.IsNil) { var d = Describe(td.BaseType, out var t); if (t) Use(d, tname + " (base type)"); }
    foreach (var ih in td.GetInterfaceImplementations()) { var d = Describe(md.GetInterfaceImplementation(ih).Interface, out var t); if (t) Use(d, tname + " (implements)"); }
    foreach (var fh in td.GetFields()) { var fd = md.GetFieldDefinition(fh); prov.Hit = false; var ft = fd.DecodeSignature(prov, null); if (prov.Hit) Use($"[decl] field type {ft}", tname + "::" + md.GetString(fd.Name)); }
    foreach (var ph in td.GetProperties()) { var p = md.GetPropertyDefinition(ph); prov.Hit = false; var ps = p.DecodeSignature(prov, null); if (prov.Hit) Use($"[decl] property type {ps.ReturnType}", tname + "::" + md.GetString(p.Name)); }
    foreach (var mh in td.GetMethods()) {
        var mdef = md.GetMethodDefinition(mh); var mname = tname + "::" + md.GetString(mdef.Name);
        prov.Hit = false; var msig = mdef.DecodeSignature(prov, null);
        if (prov.Hit) Use($"[decl] signature {msig.ReturnType} ({string.Join(", ", msig.ParameterTypes)})", mname);
        var imp = mdef.GetImport();
        if (!imp.Module.IsNil) Use($"[DllImport] {md.GetString(md.GetModuleReference(imp.Module).Name)}!{md.GetString(imp.Name)}", tname);
        if (mdef.RelativeVirtualAddress == 0) continue;
        var body = pe.GetMethodBody(mdef.RelativeVirtualAddress);
        if (!body.LocalSignature.IsNil) { prov.Hit = false; var locals = md.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(prov, null); if (prov.Hit) Use($"[local] {string.Join(", ", locals.Where(l => l.Contains('[')))}", mname); }
        var il = body.GetILReader();
        while (il.RemainingBytes > 0) {
            ushort op = il.ReadByte(); if (op == 0xFE) op = (ushort)(0xFE00 | il.ReadByte());
            switch (opMap[op]) {
                case System.Reflection.Emit.OperandType.InlineNone: break;
                case System.Reflection.Emit.OperandType.ShortInlineBrTarget: case System.Reflection.Emit.OperandType.ShortInlineI: case System.Reflection.Emit.OperandType.ShortInlineVar: il.ReadByte(); break;
                case System.Reflection.Emit.OperandType.InlineVar: il.ReadInt16(); break;
                case System.Reflection.Emit.OperandType.InlineI8: case System.Reflection.Emit.OperandType.InlineR: il.ReadInt64(); break;
                case System.Reflection.Emit.OperandType.InlineSwitch: { var n = il.ReadInt32(); for (int i = 0; i < n; i++) il.ReadInt32(); break; }
                case System.Reflection.Emit.OperandType.InlineField: case System.Reflection.Emit.OperandType.InlineMethod: case System.Reflection.Emit.OperandType.InlineTok: case System.Reflection.Emit.OperandType.InlineType: {
                    var h = MetadataTokens.EntityHandle(il.ReadInt32());
                    if (h.Kind is HandleKind.TypeReference or HandleKind.TypeSpecification or HandleKind.MemberReference or HandleKind.MethodSpecification) { var d = Describe(h, out var t); if (t) Use(d, mname); }
                    break;
                }
                default: il.ReadInt32(); break;
            }
        }
    }
}
var trs = new SortedSet<string>();
foreach (var trh in md.TypeReferences) { var a = AsmOfTypeRef(trh); if (IsTarget(a)) trs.Add($"{a}: {NameOfTypeRef(trh)}"); }
Console.WriteLine($"### Target TypeRefs ({trs.Count})"); foreach (var t in trs) Console.WriteLine("  " + t);
Console.WriteLine($"### Uses ({uses.Count})"); foreach (var kv in uses) { Console.WriteLine(kv.Key); foreach (var u in kv.Value) Console.WriteLine("      <- " + u); }

class Prov : ISignatureTypeProvider<string, object> {
    readonly Func<TypeReferenceHandle, string> asm, name; readonly Func<string, bool> isT; public bool Hit;
    public Prov(Func<TypeReferenceHandle, string> a, Func<TypeReferenceHandle, string> n, Func<string, bool> t) { asm = a; name = n; isT = t; }
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) { var a = asm(h); var n = name(h); if (isT(a)) { Hit = true; return "[" + a + "]" + n; } return n; }
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
}
```

</details>

## Appendix B: commands behind the numbers

- Inventories: `/usr/bin/grep -nE '<pattern>'` over the exact kept-file lists (68 Image, 124 Equipment, 26 Platesolving).
- CA1416: `mac/dotnet build <proj> --no-incremental -p:DiscoverEditorConfigFiles=false` (the switch M3a's `MacPlatformInventory` sets), then `grep CA1416 | sort -u`.
- WPF-flavoured references: scratch `net10.0-windows` + `UseWPF` + `EnableWindowsTargeting` shadow projects linking upstream sources, built with `mac/dotnet build` on macOS.
- Siril: `/Applications/Siril.app/Contents/MacOS/siril-cli -o -i <scratch>/siril.ini -d <scratch> -s check.ssf`, with `check.ssf` = `requires 1.4.0` / `convert synth -debayer -out=deb` / `load deb/synth_00001` / `stat`. Nothing was written to the user's Siril folders (checked with `find -newermt`).
