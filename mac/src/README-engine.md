# Headless engine on macOS (M3)

The upstream engine libraries build for `net10.0` / `osx-arm64` without being copied or forked. Each mac project compiles the upstream `.cs` files by link. A small compatibility assembly supplies the WPF types that leak into engine code. M3a ported Core, Profile and Astrometry; M3b part 1 adds NINA.Image and Accord.Imaging; M3b part 2 adds the rig subset of NINA.Equipment; M3b part 3 adds NINA.Platesolving.

| Project | Assembly | What it is |
|---|---|---|
| `NINA.Mac.WpfCompat` | `NINA.Mac.WpfCompat` | Stand-ins for the WPF types that engine code uses, in their WPF namespaces |
| `NINA.Core.Mac` | `NINA.Core` | Upstream `NINA.Core`, minus pure UI, plus `MacReplacements/` |
| `NINA.Profile.Mac` | `NINA.Profile` | Upstream `NINA.Profile`, unchanged |
| `NINA.Astrometry.Mac` | `NINA.Astrometry` | Upstream `NINA.Astrometry` minus its XAML converters, plus `Mac/NativeRegistration.cs`. Carries the SOFA/NOVAS dylibs, `External/JPLEPH` and the catalogue SQL scripts into every output that references it |
| `Accord.Imaging.Mac` | `Accord.Imaging` | Upstream `Accord.Imaging` (NINA's LGPL fork of the Accord.NET imaging library), all 288 files, `netstandard2.0` as upstream |
| `NINA.Image.Mac` | `NINA.Image` | Upstream `NINA.Image`, all 68 files unchanged, plus `Mac/NativeRegistration.cs` |
| `NINA.Equipment.Mac` | `NINA.Equipment` | The rig subset of upstream `NINA.Equipment` (125 of 302 files, an opt-in include list) unchanged, plus `Mac/`: resolver registration and the vendor-free device choosers |
| `NINA.Platesolving.Mac` | `NINA.Platesolving` | Upstream `NINA.Platesolving`, 25 of 26 files unchanged (one upstream edit, P1), plus `MacReplacements/` (the macOS local solver in place of the Cygwin one, two compile-only stand-ins) and `Mac/` (ASTAP launcher, `astrometry.cfg`, the rig's solver settings) |
| `../tests/NINA.Mac.Engine.Test` | | Smoke tests for Core and Profile, a WPF oracle for the compat types, and merge guards against upstream drift (`UpstreamParityTest`) |
| `../tests/NINA.Mac.Astrometry.Test` | | SOFA/NOVAS native smoke tests, 50 upstream `NINA.Test` files (63 fixtures) linked unchanged, and mac tests for the resolver, J2000 to JNow, sidereal time, the data folder, the deep-sky database and the startup data check |
| `../tests/NINA.Mac.Image.Test` | | 18 upstream `NINA.Test` files (16 fixture classes) linked unchanged, and mac tests for the FITS writer (independent reader, fitsverify, CFITSIO's listhead, Siril), the FITS header card by card, XISF round trips, ImageArray/ImageStatistics, the rendering glue, the WpfCompat imaging stand-ins, the star-detection seam, the platform limits and the Accord.Imaging parity. One `[Explicit]` hardware test |
| `../tests/NINA.Mac.Equipment.Test` | | 9 upstream `NINA.Test` files (10 fixture classes) linked unchanged, and mac tests for the include list's merge guards, the built assembly's references and native imports, the ZWO binding through the resolver, the device choosers, `DirectGuider` with the rig's numbers and the headless capture path. One `[Explicit]` hardware test: NINA's own `ASICamera` takes a bin-2 light into a FITS that Siril opens |
| `../tests/NINA.Mac.Platesolving.Test` | | All 13 upstream `NINA.Test/PlateSolving` files linked unchanged (5 cases skipped as Windows-only), and mac tests for the replacements' merge guards, the built assembly, ASTAP's and solve-field's arguments and results for this rig, the ASTAP launcher, the generated `astrometry.cfg`, the local solver end to end through NINA's `CLISolver`, and the centring loop against a simulated mount. `[Explicit]` `LocalData` tests solve real FITS files from this Mac with both solvers and run the centring loop with the real ASTAP |

```bash
mac/dotnet build mac/src/NINA.Astrometry.Mac/NINA.Astrometry.Mac.csproj      # also builds WpfCompat, Core, Profile and Native
mac/dotnet test mac/tests/NINA.Mac.Engine.Test/NINA.Mac.Engine.Test.csproj
mac/dotnet test mac/tests/NINA.Mac.Astrometry.Test/NINA.Mac.Astrometry.Test.csproj
mac/dotnet build mac/src/NINA.Core.Mac/NINA.Core.Mac.csproj --no-incremental -p:MacPlatformInventory=true   # CA1416 inventory
mac/dotnet build mac/src/NINA.Image.Mac/NINA.Image.Mac.csproj                 # also builds Accord.Imaging.Mac
mac/dotnet test mac/tests/NINA.Mac.Image.Test/NINA.Mac.Image.Test.csproj
mac/dotnet test mac/tests/NINA.Mac.Image.Test/NINA.Mac.Image.Test.csproj --filter "FullyQualifiedName~AsiFrameHardwareTest"   # camera on USB
mac/dotnet build mac/src/NINA.Equipment.Mac/NINA.Equipment.Mac.csproj         # also builds Image, Accord.Imaging, Astrometry, Core, Profile
mac/dotnet test mac/tests/NINA.Mac.Equipment.Test/NINA.Mac.Equipment.Test.csproj
mac/dotnet test mac/tests/NINA.Mac.Equipment.Test/NINA.Mac.Equipment.Test.csproj --filter "FullyQualifiedName~AsiCameraHardwareTest"   # camera on USB
mac/dotnet build mac/src/NINA.Platesolving.Mac/NINA.Platesolving.Mac.csproj   # also builds Equipment, Image, Accord.Imaging, Astrometry, Core, Profile
mac/dotnet test mac/tests/NINA.Mac.Platesolving.Test/NINA.Mac.Platesolving.Test.csproj
NINA_MAC_SOLVE_FRAMES="<a.fit>|<b.fit>" mac/dotnet test mac/tests/NINA.Mac.Platesolving.Test/NINA.Mac.Platesolving.Test.csproj --filter "FullyQualifiedName~RealSolveTest"   # real solves, ASTAP + solve-field
```

`NINA.Astrometry.Mac` needs the natives from `mac/scripts/build-astrometry-natives.sh` and the DE421 `JPLEPH` in `mac/native/ephemeris/` (see `NINA.Mac.Native`). All of these projects are in `NINA.Mac.slnx`; the commands above build and test them on their own, by csproj path.

## How a project is put together

- **`Engine.props`** holds the shared settings and is imported by every engine csproj. It turns off `GenerateAssemblyInfo` (upstream supplies the attributes), allows unsafe code, keeps the WPF targets out, and links `CommonAssemblyInfo.cs`. That last item stamps the assembly with the upstream version, 3.3.0.x.
- **Sources** are linked with `<Compile Include="$(UpstreamDir)**/*.cs" LinkBase="Upstream" />`. The include skips upstream `obj/`, `bin/` and `publish/`. `Compile Remove` lists are grouped by reason. NINA.Equipment.Mac is the exception: it names its files in an opt-in include list (see NINA.Equipment below).
- **Windows-1252 sources:** hundreds of upstream `.cs` files are Windows-1252, not UTF-8. The C# compiler reads a file without a BOM as UTF-8 and falls back to the OS ANSI code page when that fails. That is Windows-1252 on the Windows machines that build NINA, but on macOS it is UTF-8 with U+FFFD replacement, so a non-ASCII literal would compile differently. One such literal is the degree sign in `FocusTarget.Information`; more exist in Equipment, for example a regex in `CartesDuCiel.cs`. `Engine.props` therefore runs a step before `CoreCompile`: each `Compile` item that is neither valid UTF-8 nor BOM-marked is decoded as Windows-1252 and compiled from a UTF-8 copy in `obj/LegacyEncoding/`. A leading `#line` directive keeps diagnostics and debugging on the upstream file. Copies are rewritten only when their content changes. `LegacyEncodingTest` checks that no string literal in `NINA.Core`, `NINA.Profile` or `NINA.Astrometry` contains U+FFFD.
- **Packages** are the upstream package references at the same versions. There are two differences, both explained in the csproj:
  - `System.Configuration.ConfigurationManager` is added. On Windows it comes from the WindowsDesktop framework.
  - `Grpc.Tools` is 2.84.0, not 2.83.0. The 2.83.0 protoc for macOS is x86_64 only, so it runs only under Rosetta. 2.84.0 ships a universal protoc with the same libprotoc 35.1. The generated `CameraService*.cs` is byte-identical.
- **Merge guards** (`tests/NINA.Mac.Engine.Test/UpstreamParityTest.cs`): the mac csproj files restate upstream's package versions and resource exclusions, so a merge that only bumps upstream csproj files would otherwise build and pass while the mac engine stays behind. For every mac csproj that declares an `UpstreamDir`, the test compares it with the upstream csproj: every `PackageReference` and version, the embedded `.resx` files, the `.proto` files, upstream `Compile Remove` items, and sources upstream links from outside its folder. The two package differences above are pinned to upstream's current versions with their reasons; if upstream moves, the test asks for a re-check. It also pins a hash of each upstream file that `MacReplacements/` re-implements (copyright header excluded), and fails with "re-sync `MacReplacements/<file>`" when upstream changes one. New engine projects (M3b) are picked up automatically. NINA.Equipment.Mac opts out (it declares `EquipmentUpstreamDir`), because it drops upstream's vendor packages on purpose; `EquipmentParityTest` guards it instead.
- **Resources:**
  - `Locale/*.resx` builds `NINA.Core.Locale.Locale.resources` plus 25 satellite assemblies. As upstream, ar-SA and sv-SE are left out.
  - `Properties/Resources.resx` is included.
  - The WPF `Resource` `Logo_Nina.png` is not included; only WPF pack URIs use it.
  - The `.proto` file is compiled at build time.
- **CA1416:** upstream's root `.editorconfig` switches CA1416 off for every file, so a normal build is silent about Windows-only APIs. Build with `-p:MacPlatformInventory=true` to see them.

## NINA.Core exclusions

| Group | Files | Why |
|---|---|---|
| XAML value converters | `Utility/Converters/**` (76) | `IValueConverter`; only XAML bindings use them |
| XAML validation rules | `Utility/ValidationRules/**`, `Utility/ValidationRules.cs` | `System.Windows.Controls.ValidationRule` |
| XAML code-behind, WPF controls, visual-tree helpers | `MyMessageBoxView.xaml.cs`, `NotificationHostWindow.xaml.cs`, `CustomWindow.cs`, `TabControlEx.cs`, `ButtonHelper.cs`, `BindingProxy.cs`, `DataPipes.cs`, `DeferredContent.cs`, `DialogCloser.cs`, `Extensions/WPFExtensions.cs` | Need the WPF runtime; nothing in the engine calls them |
| WPF toast windows (replaced) | `Notification/Notification.cs`, `NotificationManager.cs`, `CustomNotification.cs`, the four `*WorkAreaProvider.cs` | `MacReplacements/Notification.cs` keeps the static API |
| WPF dialogs (replaced) | `MyMessageBox/MyMessageBox.cs`, `WindowService/WindowService.cs` | `MacReplacements/` keeps `MyMessageBox.Show` and `IWindowService` |
| WPF imaging in UI helpers | `Model/IImageGeometryProvider.cs`, `Utility/Http/HttpDownloadImageRequest.cs` | `GeometryGroup` icons; `BitmapSource` download for the framing / astrometry.net preview. Revisit with Platesolving |
| Windows process plumbing | `Utility/InvokeProcess.cs` | kernel32 `CreateProcess` plus advapi32; launches the Windows installer |

`Properties/AssemblyInfo.cs` compiles unchanged: `NINA.Mac.WpfCompat` supplies WPF's `ThemeInfoAttribute`. `NINA.Profile` compiles unchanged and needs no exclusions.

## NINA.Astrometry

- **Exclusions:** only `Converters/**` (6 XAML value converters). Everything else, including `Properties/AssemblyInfo.cs`, compiles unchanged. The WPF types it names are `Point` and `Vector` (sky projections), `Media3D.Vector3D` (`AstroUtil.Polar3DToCartesian`), `Media.Color` (`MoonInfo`) and `Media.Imaging.BitmapSource` (the optional sky-survey preview of a deep-sky object).
- **Natives:** `Mac/NativeRegistration.cs` is compiled into the assembly. Its `[ModuleInitializer]` calls `NativeLibraries.Register` for `NINA.Astrometry` before any of its code runs, so `SOFA_2023_10_11.dll` and `NOVAS31lib.dll` resolve to `libsofa.dylib` and `libnovas31.dylib` in the app folder (or `Contents/Frameworks`). No host call is needed. Upstream's `DllLoader.LoadDll` in the `SOFA`/`NOVAS` static constructors is a no-op off Windows. `NOVAS` opens `<BaseDirectory>/External/JPLEPH` itself; `NINA.Mac.Native` puts it there.
- **Catalogue database:** `NINADbContext` (NINA.Core) creates a new database from `<BaseDirectory>/Database/Initial/*.sql` and then applies `Database/Migration/<n>.sql` in numeric order (1 to 16, there is no 4). The project copies upstream's `NINA/Database/**/*.sql` (6.4 MB) to the output, as `NINA.csproj` does on Windows. EF6 6.5.1 and System.Data.SQLite 2.0.3 run unchanged on SourceGear's osx-arm64 `libe_sqlite3.dylib` (SQLite 3.53). Every upstream script applies cleanly, and NINA's build of the database matches applying the scripts directly, table by table.
- **Database location:** `new DatabaseInteraction()` used `Environment.ExpandEnvironmentVariables(@"%localappdata%\NINA\NINA.sqlite")`. On macOS that stays a literal relative file name, so a database called `%localappdata%\NINA\NINA.sqlite` was created in the working directory (`/` for a Finder-launched app). Off Windows it is now `Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NINA.sqlite")`, which is `~/Library/Application Support/NINA/NINA.sqlite` unless the host moves `APPLICATIONTEMPPATH` (upstream edit below). The folder must exist; `Logger` creates it on first use, as on Windows. The unused `DatabaseLocation` app setting in `NINA/Properties/Settings.Designer.cs` is not involved.
- **Missing data fails silently upstream:** without `External/JPLEPH`, NOVAS does not fail. `app_planet` returns error 0 with NaN, so `NOVAS.PlanetApparentCoordinates` gives NaN coordinates, and `AstroUtil.GetMoonPosition` returns a believable but wrong position (checked with a probe app: RA 10.5 h, Dec -22.1°, distance 4.3e-5 AU instead of RA 7.86 h, Dec 23.4°, 0.0025 AU). The only trace is one `Logger.Error` from the `NOVAS` static constructor. Missing `Database/Initial` scripts are likewise only logged. `Mac/EngineData.cs` therefore provides `EngineData.EnsureAvailable()`, which a host calls at startup (contract below). It checks that `External/JPLEPH`, `Database/Initial/initial_schema.sql`, `initial_data.sql` and at least one `Database/Migration/*.sql` can be opened under `BaseDirectory` (symlinks are followed, and a dangling one counts as missing: on Unix `File.Exists` reports a dangling symlink as present), and that a NOVAS Moon place is finite. If anything fails, it logs each problem and throws `InvalidOperationException` listing all of them.
- **.app bundle:** `BaseDirectory` is `Contents/MacOS` in a bundle, so `Contents/MacOS/External/JPLEPH` and `Contents/MacOS/Database/` must exist, either as the files or as relative symlinks into `Contents/Resources` (`External/JPLEPH -> ../../Resources/JPLEPH`, `Database -> ../Resources/Database`). A simulated bundle with those symlinks, the SOFA/NOVAS dylibs in `Contents/Frameworks` and the working directory `/` passed `EnsureAvailable`, gave the same Moon and Mars positions as the build output and found M42. Codesign and notarization of the symlinked layout have not been checked; do that when packaging.

## NINA.Image and Accord.Imaging (M3b part 1)

Follows `mac/docs/m3b-engine-compile-plan.md` §0-3a. Where that plan turned out wrong or incomplete, it is listed at the end of this section.

### NINA.Image.Mac: all 68 files, no exclusions

- **Why everything compiles.** The rendering and GDI+ analysis code is type-coupled to the save path: `BaseImageData.RenderImage` returns a `RenderedImage`, `ExposureDataFactory` builds them, and `IImageData`, `IRenderedImage` and `IExposureDataFactory` carry `BitmapSource`. Leaving the rendering files out would mean forking about 630 upstream lines and would still need `BitmapSource`. Plugins compiled against Windows NINA.Image (Hocus Focus implements `IStarDetection`) also see the same public types.
- **Packages:** exactly upstream's five (`Iconic.Zlib.Netstandard`, `K4os.Compression.LZ4`, `Nito.AsyncEx`, `System.Data.SqlClient`, `ZstdSharp.Port`), checked by `UpstreamParityTest`. `System.Drawing.Common` comes from `Accord.Imaging.Mac` (10.0.2, as Accord declares it); on Windows NINA.Image gets it from the WindowsDesktop framework. The XISF codecs (LZ4, zlib, Zstandard) are managed and work on arm64.
- **Natives:** `Mac/NativeRegistration.cs` registers the `NativeLibraries` resolver for `NINA.Image` in a `[ModuleInitializer]`, as Astrometry does. NINA.Image imports `cfitsionative.dll` (42 `DllImport` declarations: FITS reading, the non-default CFITSIO writer) and `libraw_0_22_1.dll` (7, DSLR RAW). Neither is mapped or shipped, so those calls fail with `DllNotFoundException`. Every save also calls SOFA (`MJD-OBS`, the `$$MJD$$` pattern); that resolves through NINA.Astrometry's own registration, so a host needs no call.
- **CA1416:** 140 unique warnings on 96 lines in 11 files with `-p:MacPlatformInventory=true`, exactly the plan's §3b list (ImageUtility 26 lines, StarAnnotator 18, BahtinovAnalysis 17, FastGaussianBlur 14, DetectionUtility 7, StarDetection 5, ContrastDetection 3, ColorRemappingGeneral 2, NoBlurCannyEdgeDetector 2, BayerFilter16bpp 1, DebayeredImage 1). All are `System.Drawing` (GDI+). A changed count after an upstream merge is a review trigger.
- **Encoding:** 40 of the 68 files are Windows-1252. `Engine.props` transcodes them; the built assembly's only U+FFFD literal is the deliberate `"\uFFFD"` in `XISFHeader.cs` (`LegacyEncodingImageTest`).

### Accord.Imaging.Mac

A shadow of `Accord.Imaging (NETStandard).csproj`: same assembly name, `netstandard2.0`, version 3.5.3 / file version 3.8.3.6155, the same four packages, every `.cs` under `Accord.Imaging/` (288). Retargeting to `net10.0` fails (`Accord.Range` collides with `System.Range`), so it stays `netstandard2.0`. Differences from the plan's sketch, both to keep upstream's build: `RuntimeIdentifier` is cleared (a portable library, as upstream) and `LangVersion` is 7.3 (the SDK default for netstandard2.0; `mac/Directory.Build.props` sets `latest`). It does not import `Engine.props` (Accord keeps its own versioning) and declares its folder as `AccordUpstreamDir`, because `UpstreamParityTest` would compare it with the legacy `Accord.Imaging.csproj` in the same folder; `AccordImagingParityTest` compares it with the NETStandard csproj instead. 149 of its files are Windows-1252, but only in comments: the built assembly has no non-ASCII literal (checked). Licence: LGPL-2.1, a separate assembly as upstream ships it. On macOS NINA.Image only uses `Accord.Point` from Accord outside the GDI+ code.

### What works on macOS, and what does not

Verified by `tests/NINA.Mac.Image.Test` (no hardware):

- **Capture path to FITS:** `ImageArrayExposureData` (as `ASICamera.DownloadExposure` builds it) → `ToImageData` → `SaveToDisk` with the default managed writer. Every pixel round-trips, read back by a FITS reader written from the standard that shares no code with NINA, for sizes from 1 x 1 to 3840 x 2160 and the full 0-65535 range. CFITSIO's `fitsverify` reports 0 warnings and 0 errors, its `listhead` reads the same cards, and Siril 1.4.4 debayers the synthetic RGGB frame from the header ("RGGB from header, Orientation: top-down from header") to R 40000, G 10000, B 2000 exactly.
- **The header is the one Windows NINA writes.** `FitsHeaderTest` lists all 42 cards of a bin-2 ASI585MC light as literal 80-column strings, written by hand from `FITSHeaderCard`'s formatting rules (not captured from a run), among them `BAYERPAT= 'RGGB'`, `XBAYROFF=0`, `ROWORDER= 'TOP-DOWN'`, `XPIXSZ=5.8` (2.9 µm × `XBINNING` 2), `XBINNING=2`, `GAIN=200`, `OFFSET=3`, `CCD-TEMP=-9.8`, `EXPTIME=10.0`, `EGAIN=0.239999994635582` (the SDK's C float 0.24 widened to double, as on Windows) and `DATE-OBS= '2026-10-04T13:45:30.1234567'`. The header is managed code with invariant-culture formatting; cards and pixels are identical under en-US, de-DE, fr-FR, sv-SE, tr-TR, ar-SA and zh-HK. Four cards depend on the machine and are checked separately: `DATE-LOC` (local time zone), `MJD-OBS`/`MJD-AVG` (native SOFA; within 1e-9 d of the calendar arithmetic, 61317.5732653178 for the test instant) and `SWCREATE`, which says `(x64)` because `DllLoader.IsX86` only tells 32 from 64 bit, as Windows x64 writes it. With ZWO mono bin (`BayerPattern` None) the Bayer cards are left out, as on Windows. Reading the cards back through `FITSHeader.ExtractMetaData`, the managed half of NINA's FITS reader, restores the metadata (upstream does not parse `DATE-OBS` back).
- **File names:** NINA's default pattern `$$DATEMINUS12$$\$$IMAGETYPE$$\...` makes the folders `2026-10-04/LIGHT/` (the `CoreUtil.PATHSEPARATORS` fix).
- **XISF:** NINA's managed writer and reader round-trip pixels and metadata with every codec (none, LZ4, LZ4HC, zlib, Zstandard), with and without byte shuffling, and with SHA-1/256/512 checksums. The XML header now has Windows' bytes (upstream edit below).
- **ImageArray and statistics:** the SDK buffer reaches the image without a copy; `Flipped2DExposureData`'s unsafe transposition works for byte/short/ushort/int/uint arrays; `ImageStatistics` matches a sorting reference on noisy frames up to 3840 x 2160 (mean 15500, median 10000, NINA's MAD 8000 on the rig mosaic).
- **Rendering glue:** `RenderBitmapSource`/`RenderImage` give a frozen Gray16 bitmap over the raw array, `GetThumbnail` returns a 300 x 169 thumbnail (WPF's size rounding, box-averaged) instead of null, `FromBitmapSource` reads Gray8/Gray16 back.

Does not work, pinned by `MacLimitsTest` so that each failure stays the documented one:

| Feature | On macOS | Engine rule |
|---|---|---|
| Stretch, debayer, upstream star detection and annotation, Bahtinov, contrast detection | GDI+: the first `System.Drawing.Bitmap` fails with `DllNotFoundException: gdiplus.dll` (inside a `TypeInitializationException`) | never call `RenderedImage.Stretch`/`Debayer`; star detection through the seam below |
| Reading FITS (`CreateFromFile`, `FITS.Load`), CFITSIO writer (`FITSUseLegacyWriter = false`, compressed FITS) | `DllNotFoundException: cfitsionative.dll`; no file is left behind | keep the legacy writer (the default). Reading needs plan P5 (C `long` on LP64) plus a `NativeLibraries` entry and a staged `libcfitsio` |
| TIFF saving, GIF/TIFF/JPEG/PNG loading | WIC: `PlatformNotSupportedException`. TIFF saving leaves a 0-byte `.tif`, because `SaveTiff` opens the file first (upstream behaviour for any encoder failure) | do not offer TIFF |
| XISF SHA3-256/SHA3-512 checksums | .NET has no SHA-3 on macOS (`SHA3_256.IsSupported` is false on macOS 26.6): `SaveToDisk` throws `PlatformNotSupportedException` before any file is written | offer SHA-1/256/512 only |
| DSLR RAW (LibRaw), `$$SENSORTEMP$$` via exiftool | `libraw_0_22_1.dll` not mapped; exiftool.exe absent (by code reading, upstream catches and logs that failure; not run) | not needed for the rig |

### Star detection: compile upstream, plug in through `IStarDetection`

Decision: NINA.Image.Mac compiles upstream `StarDetection`, `StarAnnotator`, `BahtinovAnalysis` and `ContrastDetection` unchanged and does not exclude them in favour of `NINA.Mac.ImageAnalysis`. They are part of NINA.Image's public surface (plugins and upstream tests use the types), constructing them does not touch GDI+ (`MacLimitsTest`), and NINA already has the extension point the mac analysis needs: `ImageDataFactory`, `ExposureDataFactory` and `RenderedImage` take the detection and the annotator from `IPluggableBehaviorSelector<IStarDetection>` / `<IStarAnnotator>`, where Windows NINA registers `PluggableBehaviorSelector<IStarDetection, StarDetection>` (`NINA/Utility/IoCBindings.cs:268`) and plugins add theirs. Excluding the files would fork upstream code for no gain. `BahtinovAnalysis` is only constructed by the Windows `ImageControlVM`; the mac UI calls `NINA.Mac.ImageAnalysis`'s Bahtinov analyser directly.

The seam, as `StarDetectionSeamTest` exercises it with a stand-in detection: `RenderedImage.DetectStars(annotate, sensitivity, noiseReduction)` builds `StarDetectionParams` from the profile (autofocus crop ratios → `UseROI`/`InnerCropRatio`/`OuterCropRatio`, `AutoFocusUseBrightestStars` → `NumberOfAFStars`), calls `IStarDetection.Detect(renderedImage, Gray16, params, progress, token)`, optionally `IStarAnnotator.GetAnnotatedImage`, then `UpdateAnalysis`, which fills `IImageData.StarDetectionAnalysis` and through it the `$$HFR$$`/`$$STARCOUNT$$` patterns.

Adapter to write when the engine host is assembled (M5/M7). It needs both assemblies, so it belongs in host-side code, not in NINA.Image.Mac (which stays upstream plus resolver registration) and not in NINA.Mac.ImageAnalysis (which stays NINA-free; the `NINA.Mac.ImageAnalysis*` names belong to that workflow):

| `IStarDetection` member | Adapter |
|---|---|
| `Name`, `ContentId` | its own (for example "NINA (macOS)" and its type name); the mac host's selector defaults to it and does not list upstream `StarDetection`, which would throw |
| `Detect(image, pf, p, progress, token)` | Input: `image.RawImageData.Data.FlatArray`, `Properties` (width, height, bit depth, `IsBayered`) and `MetaData.Camera` (`SensorType`, `BayerOffsetX/Y`) → `PixelBuffer`; `pf` is ignored, because the mac pipeline never debayers through GDI+ and the analyser debayers Bayer frames itself. `p.Sensitivity`, `NoiseReduction`, `IsAutoFocus`, `UseROI`, `InnerCropRatio`, `OuterCropRatio`, `NumberOfAFStars`, `MatchStarPositions` → `FrameAnalysisOptions`. Output: `StarDetectionResult` (`AverageHFR`, `AverageFWHM`, `AverageEccentricity`, `HFRStdDev`, `DetectedStars`, `StarList` of `DetectedStar` with `Accord.Point` positions and `System.Drawing.Rectangle` boxes from System.Drawing.Primitives, `BrightestStarPositions`, `Params = p`). Runs on the thread pool and honours `token` |
| `CreateAnalysis()` | `new StarDetectionAnalysis()` |
| `UpdateAnalysis(...)` | as upstream (`StarDetection.cs:989-999`), units in pixels |
| `IStarAnnotator` | returns the bitmap unchanged; the Avalonia UI draws its own overlay from `StarList` |

### Where the M3b plan was wrong or incomplete

- §6 asked for a real Gray16 → Gray8 `FormatConvertedBitmap` for `ImageUtility.Convert16BppTo8BppSource`. Nothing in the repository calls that method. The stand-in converts only to the source's own format (a copy) and throws `NotSupportedException` otherwise; the other caller, `ImageArrayExposureData.FromBitmapSource` for Bgr24/Bgr32/Pbgra32, is only reached from the WPF simulator camera.
- §6 offered "throw, or nearest-neighbour" for `TransformedBitmap`. It is an area-weighted downscale with WPF's size rule (`max(1, (uint)(scale × pixels + 0.5))`), so `GetThumbnail` and the plate-solve progress thumbnail work. WPF's Fant filter is not bit-identical; that only matters for display.
- §3c listed `System.Drawing.Common` 10.0.10 as a mac-only package of NINA.Image. It arrives through Accord.Imaging.Mac (10.0.2), so NINA.Image.Mac's package list equals upstream's, which `UpstreamParityTest` requires.
- §2 asked for a README per project. The engine projects keep M3a's single `README-engine.md`.
- §3a's csproj sketch lacked the cleared `RuntimeIdentifier`, `LangVersion` 7.3 and the `AccordUpstreamDir` name (above).
- §7 P5 (CFITSIO C `long`) was checked against Homebrew's `fitsio.h` (CFITSIO 4.7.0) for all 42 active imports (a plain grep for `DllImport`, as in the plan, also counts the commented-out `ffukyd`: 43): the list is complete (`ffgpxv` `firstpix`, `ffcrim` `naxes`, the unused `ffgisz`, and `ffgkyj`, which is correct on LP64 but 8 bytes against Windows' 4). One more mismatch, on every platform: `ffomem` takes `size_t deltasize` by value, and NINA passes `ref UIntPtr`. By reading CFITSIO, `deltasize` is only used when a memory file grows, which a read-only open never does. Not applied here: P5 is an M5 prerequisite and needs a staged `libcfitsio` and a resolver entry. P6 (`SWCREATE` architecture) is not applied either; the card stays byte-identical to Windows x64.
- Not in the plan: the XISF XML header used `Environment.NewLine` (upstream edit below); SHA-3 XISF checksums are unavailable on macOS; and the two upstream issues below.

### Upstream issues found (same on Windows, not fixed here)

- `ImageArrayExposureData.ArrayFrom16BitSource` (`ImageArrayExposureData.cs:113-119`) sizes its `ushort[]` as `stride × height` with the stride in bytes, so images made from a Gray16 `BitmapSource` (simulator and file cameras) carry `2 × width × height` values, the second half zeros. By code reading, statistics then count the zeros and a FITS save writes twice the declared data. Fix: `new ushort[source.PixelWidth * source.PixelHeight]`. Pinned in `RenderingGlueTest`.
- `XISFHeader.ExtractMetaData` reads `Observation:Time:Start` (UTC, written without a zone designator) with `DateTime.Parse`, so the exposure start comes back as the UTC clock time with `DateTimeKind.Unspecified`; `ToUniversalTime()` then shifts it by the local offset.

## NINA.Equipment (M3b part 2)

Follows `mac/docs/m3b-engine-compile-plan.md` §4, 6, 8, 10 and 11. §7 holds no NINA.Equipment patch, and NINA.Equipment needed no upstream edit (the ZWO `CLong` fix below predates M3b). Where the plan turned out wrong is listed at the end of this section.

### NINA.Equipment.Mac: an opt-in include list

Unlike the other engine projects, nothing is globbed from the upstream root: the csproj names what the rig needs, so a driver or SDK binding that upstream adds later stays out of the mac build without anyone having to notice. Only the contract folders and the `*Info` classes are globbed; a new file there that needs an excluded type or WPF fails the compile, which is the review trigger. 125 of the 302 upstream files compile, unchanged:

| Kept | Files | What for |
|---|---|---|
| `Interfaces/**` minus `ISVBonySDK.cs` | 64 | `IDevice`, `ICamera`, `ITelescope`, `IFocuser`, `IGuider` and the other device contracts; every `I*Mediator`, `I*Consumer` and `I*VM`; `IDeviceChooserVM`, `IEquipmentProvider<T>`. The interfaces the M4 LX200 drivers implement and the engine consumes |
| `Model/**`, `Exceptions/**` | 2 + 11 | `CaptureSequence`, `CaptureSequenceList`; `CameraDownloadFailedException` and friends |
| `Equipment/DeviceInfo.cs`, `DummyDevice.cs`, `OfflineDevice.cs`, `GuideStepsHistory.cs` and every `Equipment/My*/*Info.cs` | 4 + 11 | Device plumbing; the mediator and view-model interfaces carry every device's info class |
| `MyCamera/ASICamera.cs`, `ASICameras.cs`, `PersistSettingsCameraDecorator.cs`, `SDK/CameraSDKs/ASISDK/ASICameraDll.cs` | 4 | The ZWO camera, its enumeration, the profile decorator `CameraVM` wraps every camera in, the SDK binding |
| `MyGuider/DirectGuider.cs`, `DitherOffsetSelector.cs`, `DummyGuider.cs`, `RMSError.cs`, `LockPosition.cs`, `MyGuider/PHD2/PhdEvents/*` | 5 + 22 | Mount-only dithering ("Mount Dither") and the no-op guider. The PHD2 event classes are data that the `IGuider` contract carries (`GuideStepsHistory`, `IGuiderMediator`); the PHD2 client itself is not compiled |
| `Utility/ImageMetaDataExtension.cs`, `Properties/AssemblyInfo.cs` | 2 | `FromCamera`, `FromTelescopeInfo` and the other metadata fillers; upstream's assembly attributes |

Left out (177 files): every other vendor's driver and SDK binding (`SDK/**` except the ZWO camera binding: ASI EAF/EFW, ASTPAN, Atik, Canon, FLI, Moravian, PlayerOne, QHY, SBIG, SVBony, the ToupTek family, Oasis, the flat panels), the ASCOM and Alpaca drivers and their discovery (`AscomDevice`, `AscomLogger`, `AlpacaDirectSettings`, `Utility/ASCOMInteraction`, `AlpacaInteraction`), the PHD2, MetaGuide, SkyGuard and MGEN guiders, the focuser and mount drivers (the LX200GPS and its #1209 focuser come from M4), the filter wheel, flat device, rotator, dome, switch, weather, safety monitor, GNSS and planetarium drivers (their `*Info` classes stay), `Utility/FilterManager.cs`, the WMI `UsbDeviceWatcher.cs` (its interface stays; nothing binds one), the three WPF value converters and `Interfaces/ISVBonySDK.cs` (it extends the SVBony binding).

- **Packages:** only `System.Data.SqlClient` 4.9.1 (upstream's EF6 pin). `CommunityToolkit.Mvvm`, `Newtonsoft.Json` and `Accord.Statistics` arrive through Core and Image as on Windows; `DeviceInfo`'s `[ObservableProperty]` generator runs without an explicit reference, as upstream builds it. Upstream's vendor packages are dropped: ASCOM.Alpaca.Components, ASCOM.Alpaca.Device, ASCOM.Com.Components, Castle.Core, Castle.Core.AsyncInterceptor, GrpcDotNetNamedPipes, NJsonSchema, SharpGIS.NmeaParser, plus the build-only Google.Protobuf.Tools, Grpc.Tools and Microsoft.NETFramework.ReferenceAssemblies; so are the project references to `nikoncswrapper` and `NINA.MGEN`.
- **Merge guard:** the csproj declares its folder as `EquipmentUpstreamDir`, not `UpstreamDir`, because `UpstreamParityTest` requires every upstream package. `EquipmentParityTest` (in `NINA.Mac.Equipment.Test`) compares the csproj with `NINA.Equipment.csproj` instead: every upstream package is either referenced at upstream's version or pinned as dropped with its reason and upstream's current version; a new upstream package, project reference, resource, `.proto` or foreign source fails it; every include still matches files; and the compiled list keeps `SDK/` down to the ZWO camera binding and `MyCamera/` down to the three ZWO files. `EquipmentAssemblyTest` reads the built `NINA.Equipment.dll` as metadata (plan §11, "re-run the scan after each merge"): no ASCOM, Castle, gRPC pipe, MGEN, Nikon, WPF, GDI+ or WMI assembly reference; native imports exactly `ASICamera2.dll` ×29; and the concrete `IDevice` classes exactly `ASICamera`, `PersistSettingsCameraDecorator`, `DirectGuider`, `DummyGuider`, `DummyDevice` and `OfflineDevice`.
- **WPF:** the kept code names two WPF types, both by signature only: `BitmapSource` (`IImagingMediator`, `IImagingVM`, `IImageControlVM`; already in WpfCompat) and `GeometryGroup` (`IDockableVM.ImageGeometry`, the dock icon), which WpfCompat now adds with its base `Geometry` (see the inventory). `System.Drawing` in `ASICamera.cs`/`ASICameraDll.cs` is `Point`/`Size` from System.Drawing.Primitives, not GDI+.
- **Natives:** `Mac/NativeRegistration.cs` registers the `NativeLibraries` resolver for NINA.Equipment in a `[ModuleInitializer]`, so `ASICamera2.dll` (the binding's only library, 29 imports) resolves to `libASICamera2.dylib` next to the app with no host call. `AsiSdkTest` proves it: the test host never calls `Register`, and `GetSDKVersion()` returns `1, 41` through NINA.Equipment's own binding.
- **CA1416:** 0 with `-p:MacPlatformInventory=true`, as the plan predicted (the same build reports Core's 8 and Image's 140, so the inventory switch is live).
- **Encoding:** 75 of the 125 files are Windows-1252, but their non-ASCII bytes are only in comments; the built assembly has no non-ASCII literal (`EquipmentAssemblyTest`).

### Device choosers without the other vendors (`Mac/`)

Upstream's choosers live in NINA.WPF.Base (`ViewModel/Equipment/*/…ChooserVM.cs`) and reference every vendor SDK, so the mac build does not compile them. `NINA.Equipment.Mac/Mac/` carries vendor-free counterparts in namespace `NINA.Equipment.Mac`, public, as `NINA.Astrometry.Mac.EngineData` is in its assembly:

| Mac chooser | Lists, in upstream's order | Selection from the profile |
|---|---|---|
| `CameraChooser` | "No Camera", the ZWO ASI cameras (upstream's ASI block, unchanged), provider cameras | `CameraSettings.Id` / `LastDeviceName` |
| `TelescopeChooser` | "No Mount", provider mounts (the LX200GPS driver, M4) | `TelescopeSettings.Id` / `LastDeviceName` |
| `FocuserChooser` | "No Focuser", provider focusers (the #1209 through the mount, M4) | `FocuserSettings.Id` / `LastDeviceName` |
| `GuiderChooser` | `DummyGuider`, `DirectGuider` ("Mount Dither"), provider guiders | `GuiderSettings.GuiderName` / `LastDeviceName` |

- The base class `DeviceChooser<T>` implements `IDeviceChooserVM` and ports `DeviceChooserVM.DetermineSelectedDevice` unchanged: the saved device is selected; if it is missing, an `OfflineDevice` with its id and name goes to the top and is selected; an empty list stays empty. Upstream's three `DeviceChooserVMTest` cases for it pass against the mac class.
- Devices from outside NINA.Equipment come in as `IEquipmentProvider<T>`, the contract upstream's plugin providers use; a provider that throws is logged and skipped, as upstream does. The host passes the providers in; there is no plugin scan.
- The setup-dialog command is left out: upstream runs the driver dialog on an STA thread (`Thread.SetApartmentState` throws off Windows), and no mac device has one, so `SetupDialogOpen` is always false.
- As upstream, each `GuiderChooser.GetEquipment` creates a new `DirectGuider`, which registers itself with the telescope mediator, and earlier ones are not disposed.
- NINA's default `GuiderSettings.GuiderName` is `PHD2`, which the mac list does not have, so a default profile shows "PHD2 (OFFLINE)" selected, as upstream would for any missing guider. A mac host's default profile should set `Direct_Guider` (or `No_Guider`).
- `CameraChooser` opens each ASI camera briefly to read its alias (`ASICamera`'s constructor, upstream behaviour), so its default-run test runs only when no camera is connected and is ignored otherwise.

### What works on macOS (verified)

- **NINA's ZWO driver on the arm64 SDK:** `ASICameras.Count`, `GetSDKVersion` and the chooser's ASI enumeration work through the resolver (no camera connected at test time: `Count` 0). The `[Explicit]` hardware test (below) covers the whole capture on the real camera; it has not passed yet, because no camera was on USB when it ran.
- **Mount dithering:** upstream `DirectGuider` with the rig's numbers (`DirectGuiderRigTest`, real `Profile`): pixel scale `ArcsecPerPixel(2.9 µm, 2500 mm)` = 0.2393″/px; with no guide rate from the mount it uses half sidereal (7.52″/s); each dither sends one `ITelescopeMediator.PulseGuide` per axis from the previous offset (a 3 px east step at 7.5″/s is a 96 ms pulse), only east/west with `DitherRAOnly`, waits while the mount reports `IsPulseGuiding`, sends nothing without a connected mount, and disconnects with a warning notification when the mount goes away. The pixel scale uses the profile's unbinned pixel size, as on Windows, so the dither distance is in unbinned pixels (5 px = 2.5 px in a bin-2 frame).
- **Headless capture path (no hardware):** a stand-in `ICamera` behind NINA's `PersistSettingsCameraDecorator` gets the profile's binning, gain and offset on connect (`RestoreCameraProfileDefaults`), and its frame goes `StartExposure` → `WaitUntilExposureIsReady` → `DownloadExposure` (metadata `FromCamera`, `ExposureDataFactory`) → `FromProfile` → `ToImageData` → `SaveToDisk` with the profile's `FileSaveInfo`, landing in `<date>/LIGHT/…_-9.80_1.00s_0007.fits` with the pixels, `BAYERPAT`, `XBINNING`, `XPIXSZ`, `GAIN`, `OFFSET`, `FOCALLEN` and `TELESCOP` cards as NINA writes them (`HeadlessCapturePathTest`).
- **Upstream behaviour:** 93 linked upstream cases pass, among them `DirectGuiderTest`, `GuideStepsHistoryTest`, `ImageMetaDataTest`, `ImageMetaDataExtensionBehaviorTest` and the info-class contracts.

Not verified: the LX200 side (M4 builds `ITelescope`/`IFocuser` drivers on these interfaces), `ASICamera`'s live view (`StartLiveView`/`DownloadLiveView`), sub-frames and the cooler control loop. `ASICamera.Disconnect` does not switch the cooler off (upstream, by code reading), so a host switches it off, or warms up, before disconnecting.

### Where the M3b plan was wrong or incomplete (Equipment)

- §4a excluded `Model/CaptureSequenceList.cs`. Its only problem was a scratch-build artefact (two different `BitmapSource` types, E6); against WpfCompat it compiles, and upstream's `CaptureSequenceTest` and `CaptureSequenceBehaviorTest` need it, so it is kept: 125 files, not 124.
- §4c asked for an explicit `CommunityToolkit.Mvvm` reference for `DeviceInfo`'s `[ObservableProperty]`. The generator already runs through NINA.Core's reference, as on Windows, where `NINA.Equipment.csproj` has none either; the mac project keeps upstream's package list.
- §4b counted 30 `ASICamera2.dll` imports. That was while the fork's binding also declared the probe-only `ASIGetLMHGainOffset`, which now lives in `NINA.Mac.ZwoProbe/GainPresets.cs`; upstream's file has 29.
- §4d placed the choosers in the engine host (M7/M8). They are in `NINA.Equipment.Mac/Mac/` so they compile and are tested with the include list they keep link-clean; only the composition (which providers, which profile) stays in the host.
- The plan did not name the parity problem: `UpstreamParityTest` would demand the dropped vendor packages, hence `EquipmentUpstreamDir` and `EquipmentParityTest`. `Mac/AssemblyInfo.Mac.cs` adds `[InternalsVisibleTo("NINA.Mac.Equipment.Test")]`, as upstream's `AssemblyInfo.cs` grants `NINA.Test`, for upstream's `DirectGuider`/`DitherOffsetSelector` test constructors.
- "Serial helpers": NINA.Equipment has none of its own outside the excluded flat-panel SDKs. NINA's serial classes are in NINA.Core (`Utility/SerialCommunication`, ported in M3a with the `SerialPortProvider` fix); the LX200 library opens `System.IO.Ports` itself.

## NINA.Platesolving (M3b part 3, M6 groundwork)

Follows `mac/docs/m3b-engine-compile-plan.md` §5 (with §5d, solver choice and the two known solver bugs) and MAC_PORT_PLAN.md M6 and risk 4. Where the plan turned out wrong is listed at the end of this section.

### NINA.Platesolving.Mac: 25 of 26 files, one replacement

Every upstream `.cs` under `NINA.Platesolving/` is compiled by glob, as for Core, Image and Astrometry, except `Solvers/LocalPlateSolver.cs`. `ASTAPSolver.cs` carries one upstream edit (P1, below). The mac-only files:

| File | What | Delete when |
|---|---|---|
| `MacReplacements/LocalPlateSolver.cs` | Replaces upstream's Cygwin solver by name: same `internal` type in `NINA.PlateSolving.Solvers`, same `(string cygwinRoot)` constructor, so the static `PlateSolverFactory` (which the sequencer's `Center`, `CenterAfterDriftTrigger`, `PlatesolvingImageFollower` and WPF.Base's `MeridianFlipVM` call directly) creates it without any change. Details below | never (upstream stays Cygwin) |
| `MacReplacements/AscomNamespaceAnchor.cs` | `namespace ASCOM { internal static class MacNamespaceAnchor { } }`: `TheSkyXImageLinkSolver.cs` has an unused `using ASCOM;` that resolves on Windows only through NINA.Equipment's ASCOM packages, which the mac Equipment drops. It declares no types, so real ASCOM use would still fail to compile | upstream drops the using (plan P3) |
| `MacReplacements/HttpDownloadImageRequest.cs` | `internal` stand-in for the NINA.Core class that NINA.Core.Mac leaves out (it decodes with WPF's `BitmapImage`). Its only user is `AstrometryPlateSolver.GetJobImage`, private and never called; `Request` throws `PlatformNotSupportedException` | upstream drops `GetJobImage` (plan P2) or NINA.Core.Mac compiles the class again |
| `Mac/AssemblyInfo.Mac.cs` | `[InternalsVisibleTo("NINA.Mac.Platesolving.Test")]`, as upstream grants `NINA.Test` | never |
| `Mac/AstrometryNetSetup.cs` | The local solver's folders and the Nightglass-owned `astrometry.cfg` | never |
| `Mac/AstapSetup.cs` | The ASTAP launcher that passes the star database (`-d`) | never |
| `Mac/RigPlateSolveDefaults.cs` | This rig's plate-solve profile settings | never |

`PlatesolvingParityTest` keeps all three replacements honest: it pins a hash of upstream `LocalPlateSolver.cs` (re-sync on change), and fails as soon as the anchor or the stand-in is no longer needed. P2 and P3 are therefore not applied: each would be an upstream edit where a mac file does the job, and P4 (`-center` → `--crpix-center` in the Cygwin solver) changes Windows behaviour, so it stays a proposal.

- **Packages:** `System.Data.SqlClient` 4.9.1 only, as upstream; `UpstreamParityTest` (NINA.Mac.Engine.Test) picks the project up automatically. On the case-insensitive APFS volume it finds upstream's `NINA.PlateSolving.csproj` from the folder name `NINA.Platesolving`.
- **References:** NINA.Core, Profile, Astrometry, Image, Equipment and WpfCompat (`PlateSolveProgress.Thumbnail` is a `BitmapSource`). No ASCOM, WPF, GDI+ or WMI assembly and no native import (`PlatesolvingAssemblyTest`).
- **CA1416:** 2 with `-p:MacPlatformInventory=true`, exactly the plan's: `Dc3PinPointSolver.cs:78,174` (`Type.GetTypeFromProgID`, COM). PinPoint, PlateSolve 2/3, ASPS and TheSkyX stay compiled and fail at run time only if chosen; the mac UI offers ASTAP, `LOCAL` and `ASTROMETRY_NET`.
- **Encoding:** the four ISO-8859-1 interface files have non-ASCII bytes in comments only; the assembly has no U+FFFD literal.

### The two solver bugs, and what macOS needs besides

| Problem | On macOS | Fix |
|---|---|---|
| **Bug 1: ASTAP version check** (`ASTAPSolver.EnsureSolverValid`) | `FileVersionInfo` reads only managed metadata off Windows, so `FileVersion` is null for the Mach-O `astap_cli` (checked on the installed binary). With NINA's default `DownSampleFactor` 0 every ASTAP solve threw "ASTAP version below 0.9.1.0" | Upstream edit P1: the check runs on Windows only. ASTAP with `-z 0` solves on this Mac (real-solve table below) |
| **ASTAP's star database** | `astap_cli` looks only in `/usr/local/opt/astap/` unless given `-d`, and NINA passes no `-d` (checked: run through NINA's `ASTAPSolver` it answers `ERROR=No star database found`) | `AstapSetup.Resolve(astapExecutable, databaseFolder)` returns the executable when the database is in that default folder, else writes `<data folder>/Solvers/astap`, an `sh` script `exec '<astap_cli>' -d '<folder>' "$@"`, and returns its path for `ASTAPLocation`. `exec` keeps the process id, so CLISolver's timeout still kills ASTAP. Paths are single-quoted for the shell; argument boundaries survive (`AstapSetupTest` runs it with spaces and quotes in every path) |
| **Bug 2: Cygwin-bound local solver** | upstream runs `cmd.exe`, then Cygwin `bash.exe`, then `/usr/bin/solve-field` and `wcsinfo` | `MacReplacements/LocalPlateSolver.cs`, below |
| **`solve-field` needs `pnmfile`** | `solve-field` runs netpbm's `pnmfile` through `/bin/sh`, so with an app's default `PATH` (`/usr/bin:/bin:/usr/sbin:/sbin`, as Finder starts apps) it fails before solving: `/bin/sh: pnmfile: command not found` (reproduced with `env -i`) | the replacement sets `PATH` for solve-field only |
| **CLISolver never reads the solver's output** (upstream, all platforms) | `StartCLI` sets `RedirectStandardOutput` but never calls `BeginOutputReadLine`, so a program that writes more than the pipe holds (64 KiB on macOS) blocks until the timeout (`CliSolverMacTest`: 1 MiB stalls, 16 KiB does not). ASTAP writes under 1 KiB; a long blind solve-field run prints a line per index tried | the replacement sends solve-field's output to a log file |

The **mac `LocalPlateSolver`** (`LOCAL`, the rig's blind solver):

- `CygwinLocation` is the folder holding `solve-field` and `wcsinfo`; empty (NINA's default) means `/opt/homebrew/bin`, `~/` is expanded, and a path to `solve-field` itself means its folder.
- It starts `/bin/sh -c 'PATH=$1; export PATH; log=$2; mkdir -p "$3" 2>"$log" || exit 1; shift 2; exec "$0" --temp-dir "$@" >"$log" 2>&1' <solve-field> <bin>:/usr/bin:/bin:/usr/sbin:/sbin <image>.solve-field.log <image>.solve-field-tmp --config <cfg> <options> <image>`. Paths travel as arguments, never inside the script, so the shell parses none of them; `exec` keeps the process id, so the timeout kills solve-field and its `astrometry-engine` (`LocalPlateSolverMacTest` checks the argv a stand-in `solve-field` receives through NINA's real `CLISolver`, and that a timeout kills its child).
- **Temporary files:** solve-field converts every image into temporary files (an uncompressed copy and a PPM/PGM, 2 MB for a 1920 × 1080 frame, 8 MB for a 2160 × 3840 one), by default in `/tmp`. It deletes them after a solve that ends by itself (checked), but not when it fails early (checked: netpbm missing from `PATH`, an engine error) or is killed: the previous, cut-off attempt at this work had left 33 of them in `/tmp`, 29 of them 2 MB conversions of 1920 × 1080 frames written during its rig-geometry runs, where a failing solve ends in the timeout kill (removed). The launcher passes `--temp-dir <image>.solve-field-tmp` (a folder in NINA's `PlateSolver` working folder, created by the script), and the solver deletes that folder after every solve, whatever the outcome (`LocalPlateSolverMacTest`: after a success, a failure and a timeout kill). After the real solves below, `/tmp` held no `tmp.*` file.
- The options are upstream's (`--overwrite`, the `none` outputs, `--objs`, `--no-plots`, `--resort`, `--downsample`, `--scale-units arcsecperpix`, `-L`/`-H` = scale ∓ 0.2″/px, `--ra`/`--dec`/`--radius` for a near solve), except that upstream's `-center`, which `getopt_long` reads as `-c enter` (code tolerance 0), is the intended `--crpix-center`, and `--config` names the generated `astrometry.cfg`. At 0.4785″/px that is `-L 0.28 -H 0.68`.
- **`astrometry.cfg`** (`AstrometryNetSetup`): written before every solve to `<CoreUtil.APPLICATIONTEMPPATH>/Solvers/astrometry.cfg` (only when its text changed): `cpulimit` (default 300 s), one `add_path` per index folder (default `~/Library/Application Support/Astrometry`, spaces kept verbatim; a folder with a line break or leading or trailing whitespace is rejected) and `autoindex`. Homebrew's `etc/astrometry.cfg` is never read or written, and the index folder is only listed (`AstrometryNetSetupTest` compares its listing and timestamps before and after). Validation fails with a clear message when `solve-field` or `wcsinfo` is missing or no folder holds an `index-*.fits` file. `autoindex` also tries the non-index files in that folder (`fetch.log`, `fetch.done`), which only prints "Failed to add index" into the log.
- **Result:** `ReadResult` runs `<bin>/wcsinfo <file>.wcs` directly and parses its `key value` lines exactly as upstream (`ra_center`/`dec_center`, `orientation_center` → position angle `orientation - 180`, `pixscale`, and the CD matrix for the parity). Difference: success needs `ra_center` and `dec_center`; upstream reported success at RA 0, Dec 0 whenever the `.wcs` existed.
- **Files:** the `.wcs` output (as upstream), plus solve-field's `.axy` and `.solved` and the log as sidecars: deleted after a solve, archived in `PlateSolver/Failed` with the image after a failure (upstream left `.axy`/`.solved` in the working folder). The tail of the log goes to NINA's log when no `.wcs` was written.
- **Timeout:** `cpulimit` + 60 s (360 s by default) instead of CLISolver's 10 minutes. The engine honours the config's `cpulimit` only loosely. Measured on this M1 with a rig-size near solve that cannot succeed (a constructed IC 434 patch, below), run by hand with NINA's arguments while another solve ran alongside: limit 5 s → "Total CPU time limit reached!" after 29.5 s user CPU (42.9 s wall); limit 15 s → 42.4 s CPU (67.0 s wall). With limit 60 s, 25 of the 26 failing near solves in the rig-geometry run below reached the 120 s timeout first. For a failing solve the timeout is therefore usually what ends it; CLISolver then kills solve-field with its engine, the solver deletes the temporary folder and archives the image, `.axy` and log, with the same "not solved" result.

Both upstream position-angle conventions agree: the same CD matrix through `ASTAPSolver.ReadResult` and through the real `wcsinfo` plus the mac `ParseWcsInfo` gives the same position angle and parity (`LocalPlateSolverMacTest`), and so do the two solvers on every real frame below.

### Profile settings for this rig (`RigPlateSolveDefaults.Apply`)

NINA's defaults do not work here: `ASTAPLocation` is empty (`%programfiles%\astap\astap.exe` does not exist), the blind solver is ASTAP's all-sky `-r 180`, the search radius 30°, solve frames 2 s at bin 1 with auto downsample. `Apply(settings, astapLocation, astrometryBinDirectory)` sets:

| Setting | Value | Why |
|---|---|---|
| `PlateSolverType` | ASTAP | near solves (goto, centring, drift checks) |
| `ASTAPLocation` | `AstapSetup.Resolve(...)`, e.g. the launcher for `~/Astro/astap/cli/astap_cli` with `~/Astro/astap/d80` | `-d` |
| `BlindSolverType`, `CygwinLocation`, `BlindFailoverEnabled` | `LOCAL`, `/opt/homebrew/bin`, on | blind fallback through solve-field with the scale hint, never ASTAP `-r 180` (research SLV-M2) |
| `DownSampleFactor` | 2 | ASTAP `-z 2` / `--downsample 2`; merges the RGGB mosaic of a bin-2 OSC frame (SLV-08) |
| `SearchRadius` | 5° | after a handset alignment (SLV-08: 2-5°) |
| `ExposureTime`, `Gain`, `Binning` | 15 s, 450, 2 | risk 4: 10-20 s at high gain; 450 is the gain `ASIGetGainOffset` names as lowest read noise for the ASI585MC (M1, `mac/docs/m1-camera-results.md`). The SDK pairs it with offset 15; `PlateSolveSettings` has no offset, so solve frames use the camera's offset (profile default 3), which may clip some background pixels at gain 450. Not checked on the sky |

Threshold, attempts, re-attempt delay, filter and max stars keep their values. The optical train (2500 mm native, about 1575-1640 mm with the reducer) is `TelescopeSettings.FocalLength`, one profile per train.

### Centring loop (verified without hardware)

`CenteringLoopTest` runs NINA's `CenteringSolver` with everything between mount and solver real: it creates its own `CaptureSolver`, which captures through the imaging mediator, renders the frame with NINA.Image (the 300-pixel thumbnail included) and solves through `ImageSolver` with its blind failover. The simulated mount reports JNow, as the LX200GPS does, and carries a pointing error; the simulated solver reports where the mount really points. With the rig's 1′ threshold:

- Sync accepted: a 13.9′ goto error is measured, synced and removed in 2 solves and 1 slew; the mount ends on the target.
- Sync off (`NoSync`), sync ignored silently (detected by the < 1″ sync effect) or sync rejected: NINA's measured correction (the target rotated by the solved-to-reported rotation) lands where an independent Rodrigues-rotation calculation predicts, within 0.5″, a few arcseconds from the target, so 2 solves suffice.
- Slews scattered by about 0.8′ with 2″ solve noise: centred (29.2′, then 0.89′ in the run recorded here; the test allows 2-5 solves). A mount that scatters by 6′ is given up after 10 slews.
- Near solve fails, blind failover (coordinates cleared) succeeds: centring continues. Nothing solves: CaptureSolver's attempts run, the mount never moves.

### Real solves and plan risk 4 (run on this Mac, 2026-10-05; details in the test section below)

- **Both solvers work through NINA's own classes on real data.** Five Seestar S30 Pro stacks from this Mac (IC 434, M51, M42, NGC 7000 twice; 3.66″/px, 1910-3840 px) gave 20 of 20 solves: ASTAP with D80 (`-z 2`, and `-z 0`, NINA's default, which bug 1 used to reject) in 0.3-1.3 s, solve-field near and blind in 2.6-12 s. Against the header WCS the centre is 0.5-1.6″ off, the scale within 0.05 %, the position angle within 0.03° and the parity the same; ASTAP and solve-field agree to 1.5″. The NGC 7000 stack without a WCS agrees with Siril's solution of a crop of the same stack to 0.7″ (ASTAP) and 0.6″ (solve-field).
- **NINA's centring loop with the real ASTAP** (`RealCenteringTest`): a simulated mount with a 13.9′ goto error, a camera that cuts 1280 × 720 frames out of a stack where the mount really points. On all four stacks with a WCS NINA's `CenteringSolver` measured the error to within 0.03′, synced, re-slewed and finished within 0.03′ of the target in 2 solves. On the dense NGC 7000 field ASTAP's near solve failed both times (`-z 2 -s 500` on a 1280 × 720 crop; by hand, `-s 50` solves the crop at the target, and so does `-z 1` on the same crop of the unstretched stack), and NINA's blind failover to solve-field rescued both solves (the whole centring took 42-64 s in two runs, against 0.3-2.9 s on the other stacks).
- **Risk 4, the native f/10 field (0.14356° high), cannot be settled from the data on this Mac.** Frames built in this rig's exact geometry from the stacks (36 patches, below) mostly have too few stars: the Seestar stacks show 0-14 stars above 5σ in a 15.3′ × 8.6′ patch away from the Milky Way, and ASTAP stops before it reaches the database ("Only 4 stars found in image. Abort"). Where the M51 stack gives a few more, ASTAP with D80 did solve native-geometry frames (4 solves on 3 patches, 0.6-1.3″ off), so it neither refuses nor misreads a 0.14356° field. On the database side D80 is not the limit: for a native frame with 66 detected stars ASTAP compares 78 database stars from a 0.21° window, and D80 holds them down to magnitude 19.7 at M51, 18.5 at IC 434, 15.4 at NGC 7000 and 14.4 at M42 (ASTAP's `-progress` output, first search position). Whether a 15 s ALP-T frame at f/10 shows enough stars needs a sky test.
- **The reducer helps, as the plan says:** the same patch centres at the reducer's 0.748″/px (0.224° high) solved 16 of 27 times outside the Milky Way with ASTAP in under a second (0 of 9 on the dense, stretched NGC 7000 stack), against 1-3 of 36 at the native scale.
- **solve-field as the fallback:** a near solve-field run solved 10 of the 36 native-geometry frames in 2-11 s (8 of them in the star-rich NGC 7000 field), 0.2-2.1″ off; 25 of the 26 failing runs ran into the 120 s timeout (cpulimit 60), the other gave up after 42 s on a patch with no star. With the default cpulimit 300 a failing solve takes 360 s, and NINA's failover is a **blind** solve-field run, which on a 15′ field cannot be expected to be faster. For the native train, a near solve-field (`PlateSolverType = LOCAL`) is the better second choice than the blind failover; this is left as a profile decision, not a default.

### Where the M3b plan was wrong or incomplete (Platesolving)

- §5d's replacement launched `base(Path.Combine(dir, "solve-field"))` directly. That fails twice on macOS: solve-field needs netpbm on `PATH`, which an app started from Finder does not have, and CLISolver never drains the redirected output, which a long blind solve can fill. Hence the `/bin/sh` launcher; the missing-program error now comes from `EnsureSolverValid` and names the folder.
- §5d assumed ASTAP finds its database. `astap_cli` only looks in `/usr/local/opt/astap/`, and NINA has no `-d` setting, hence `AstapSetup`. The plan's `ASTAPLocation` (`/Applications/ASTAP.app/...`) is not what this Mac has: the command-line build in `~/Astro/astap/cli/astap_cli` with D80 in `~/Astro/astap/d80`.
- §5d's `ReadResult` port was to report success whenever the `.wcs` exists, as upstream; the mac solver needs the field center (above).
- §5a/§8 recommended P2 plus the interim stub; only the stub is used (no upstream edit is needed while it works, and the parity test flags when it can go). P3 likewise stays a proposal; the anchor covers it.
- §10 item 5's `-fov` "about 0.1436°": NINA's own value is 0.14356 (1080 × 0.47853″/px), passed to ASTAP rounded to 6 places as `0.14356`.
- §5b counted `CLISolver.cs:149`'s `"cmd.exe"` comparison as harmless; it is, but the same method's unread output pipe (above) was not in the inventory.
- Not in the plan: upstream's `CliSolverBehaviorTest` needs `cmd.exe`, and two `SolverTranslationBehaviorTest` cases test Windows-only code (see the test section).
- Not in the plan: solve-field's temporary files in `/tmp` leak whenever a solve is killed or fails early (above), hence `--temp-dir`.
- Not in the plan: the position angle that NINA reports is the angle at the frame centre. A header whose `CRVAL` lies far from the centre (the Seestar's M51 stack: 1.6° of RA away, at +47°) has north turned by about ΔRA·sin Dec there (1.18° for M51), so comparing a solve with a header needs the header's orientation at the centre, not its CD matrix (`ReferenceWcs.LocalCd`).

## NINA.Mac.Equipment.Lx200: the LX200GPS driver (M4)

The rig's mount and focuser as NINA devices: `Lx200Telescope : ITelescope` and `Lx200Focuser : IFocuser`, against NINA.Equipment.Mac's compiled interfaces. They share one serial link built on the M2 protocol library `NINA.Mac.Lx200`, which supplies the command catalog with a reply shape per command, NAK retry, ACK resync and the byte trace. Nothing upstream was changed, and `NINA.Mac.Lx200` and its simulator were used read-only. The design follows plan §2 (rows Mount + focuser and Focuser model) and §6, and `research/rig_investigate_mount.md` Tables 2-4 as corrected by `rig_verify_mount.md`.

```bash
mac/dotnet build mac/src/NINA.Mac.Equipment.Lx200/NINA.Mac.Equipment.Lx200.csproj
mac/dotnet test mac/tests/NINA.Mac.Equipment.Lx200.Test/NINA.Mac.Equipment.Lx200.Test.csproj   # about 2 min, simulator only
```

| File | What |
|---|---|
| `Lx200Link.cs` | The shared link: lanes, reconnect, owed halts, trace file |
| `Lx200LinkPool.cs` | One link per port, reference counted. The default opener is `Lx200Serial.Open` (9600 8N1, no handshake) |
| `Lx200Telescope.cs`, `Lx200PulseGuider.cs` | `ITelescope`, and the three pulse-guide strategies |
| `Lx200Focuser.cs` | `IFocuser` with a virtual position in milliseconds |
| `Lx200Settings.cs` | Driver settings, stored in the active profile's plugin store |
| `Lx200EquipmentProvider.cs` | `IEquipmentProvider<ITelescope>` and `<IFocuser>` for the mac choosers |
| `Lx200Support.cs` | Port resolution, firmware and `:GW#` decoding, the injectable clock |

### The link

- **One link per port.** One worker thread owns the port and runs one transaction at a time through `Lx200Connection`. macOS opens a serial port exclusively, so the mount and the focuser must share it.
- **Four lanes, taken in this order:**

  | Lane | Used for | Rule |
  |---|---|---|
  | Stop | `:Q#`, `:Qn/s/e/w#`, `:FQ#` | Goes out as soon as the transaction on the wire ends. A halt the mount refuses (NAK) is resent for up to 3 s, then the link raises an error notification |
  | Timed | Pulse and focus starts and stops, optionally due at a given time | While one is due within 120 ms, no command or poll starts, so it leaves on time. A command that can take longer than 5 s (`:SC`) waits while any timed item is pending |
  | Command | Gotos, syncs, setters, pass-through commands | First in, first out |
  | Poll | Property reads | A waiting poll with the same key is reused |

- **Timing:** the NAK window after a command with no reply is 40 ms (P07 says 10 ms; up to 16 ms FTDI latency), not the probe's 60 ms, so a host-timed stop is not held up. The minimum gap between transactions is 20 ms. Measured against the simulator over a cable emulated at 9600 baud with a 15 ms turnaround:
  - a halt left 56 ms after it was queued, with 28 polls waiting;
  - timed sends left 0.4-1.0 ms after their due time, under continuous polling;
  - host-timed pulses of 300 and 200 ms ran 300.4 and 200.5 ms (start to halt on the link clock), starting 51 ms after `PulseGuide`;
  - `StopSlew`'s `:Q#` left 26 ms after the call, with the mount polled every 100 ms.
- **Blocklist:** `:hP#` and the rest of `Lx200Catalog.BlockedCommands` throw `Lx200BlockedCommandException` before anything is queued, and the connection refuses them again before writing.
- **Detecting a lost link.** The link counts as lost when any of these happens:
  - the stream ends or a write fails;
  - the port closes while the link is idle (checked every 100 ms);
  - three transactions in a row get no usable reply and the ACK resync fails each time (mount switched off).
- **Reconnecting.** The link reopens the port every second and needs an ACK answer. Its states are `Connected`, `Reconnecting` and `Failed` (terminal), and it posts NINA notifications for lost, restored and given up. While it is down:
  - polls fail at once, and the devices keep reporting their last values;
  - commands and timed sends wait up to 5 s (`WaitForReconnect`), then fail;
  - the devices stay `Connected`, so a short outage does not stop a sequence;
  - after `ReconnectGiveUp` (10 min) both devices disconnect.
- **Owed halts.** A host-timed motion needs its halt. The link remembers it from `:Mn/s/e/w#` (owes `:Qn/s/e/w#`) and `:F+#`/`:F-#` (owes `:FQ#`), and also every halt asked for while the link was down. Owed halts are the first bytes after the reconnect handshake (`:FQ#` twice). If the link closes with halts still owed, the trace and the log say so.
- **Trace:** every byte, plus REPLY, DISCARD and resync lines, goes to `<APPLICATIONTEMPPATH>/Logs/lx200/lx200-<time>-<port>.log`. The last 20 000 lines are also kept in memory (`Lx200Link.Trace`).

### `Lx200Telescope`

| Member | Behaviour |
|---|---|
| Properties | Served from a cache refreshed through the Poll lane every `PollIntervalMs` (1 s): `:GR#`/`:GD#` every cycle, `:GA#`/`:GZ#` every second cycle, `:GW#` (or ACK) every fifth, `:GS#` every thirtieth. NINA's 2-second polling of about 27 properties never touches the line |
| Connect | Checks in this order:<br>• `:GVP#` and `:GVN#`, with a warning on stock 4.2g: it has no GPS rollover fix.<br>• `:GW#`; when it is not answered, ACK gives the mode and tracking.<br>• `:U#` until `:GR#` is high precision.<br>• `:P#` toggled until it reads LOW (High Precision pointing off).<br>• Site (`:Gt#`/`:Gg#`).<br>• Clock check (below).<br>• `:Rg` at the profile's guide rate. |
| `SlewToCoordinates`, `Sync` | Any epoch in; JNow out from NINA's own `Coordinates.Transform(Epoch.JNOW)` (SOFA). Goto: `:Sr` + `:Sd` (each `1`), `:MS#` (`1`/`2`/`3` reported as below horizon / above limit / could hit the mount). It ends when `:D#` shows no bar after `MinimumSlewSeconds` (1.5 s) and the position is within 10' or has stopped changing. Cancellation and the 240 s timeout send `:Q#`. Sync: `:Sr`, `:Sd`, `:CM#` (its fixed reply is dropped), refused while slewing or more than 3° from the mount's position. No tracking precondition |
| Reported position after a goto or sync | The target itself, while the mount's reply is within its resolution of it (0.5 s RA, 0.5″ Dec). NINA's 1″ post-sync settle loop then ends at once instead of waiting out 5 s on rounding (plan §6). Cleared by any motion or a reply that moves off |
| `StopSlew` | `:Q#` through the Stop lane; a running goto wait returns false |
| `SlewToAltAz` | `:Sa`, `:Sz`, `:MA#` |
| `TrackingEnabled` | Get: `:GW#`'s second character (ACK ≠ `L` without `:GW#`). Set: `:AA#` only when not tracking, `:AL#` to stop. Modes are sidereal (`:TQ#`), lunar (`:TL#`) and stopped; no custom rate. `:AP#` is blocklisted |
| Meridian flip, pier side, home | `TimeToMeridianFlip` NaN (NINA's flip trigger stands down). `SideOfPier`/`DestinationSideOfPier` unknown, `TargetSideOfPier` null. `MeridianFlip` does nothing and returns true. No home |
| Park | Soft park: `:Q#`, then an alt-az goto to the stored position if set (`Setpark` stores the current alt/az), then `:AL#` (setting). `CanPark` is true, so NINA's fallback for non-parking mounts (slew to Dec +89, then `:AL#`; TelescopeVM.cs:131-159) never runs. `Unpark` resumes tracking if the park stopped it. Gotos and moves are refused while parked |
| Site | When the mount's 1' value is within `SiteToleranceArcmin` (1') of the last value written, or else of the profile's, that value is reported; otherwise the mount's. NINA's 0.001° site check therefore does not prompt on every connect (RIM MNT-14). Writes use `:St`/`:Sg` (westward 0-360 form). `SiteElevation` is the profile's (the mount stores none) |
| Time | `UTCDate` is the Mac's UTC plus the mount clock's offset, measured at connect (`:GL#` + `:GG#`, with the `:GC#` day nearest the Mac's). The `:GS#` sidereal time is compared with the sidereal time computed for the mount's longitude, which also catches a date or longitude error. Over 10 s, with the profile's `TimeSync` on and `:GW#` saying not aligned, the driver writes `:SG`, `:SL` and `:SC`. Otherwise it posts a warning. Never after alignment, and never when `:GW#` cannot tell. `:SC` is written only if the date convention is settled (`DateConvention`) or the UTC and local dates agree (not 00:00-08:00 in Hong Kong). `SetMountClock()` applies the same rules |
| `MoveAxis` | Primary is azimuth (`:Me#` for a positive rate, `:Mw#` negative), secondary is altitude (`:Mn#` up, `:Ms#` down). The rate is the nearest of guide (`:RG#`, the `:Rg` rate), centre (`:RC#`, 0.067°/s), find (`:RM#`, 1.5°/s) and max (`:RS#`, 8°/s). The last three are the manual's speeds 4, 7 and 9, assumed until measured. Rate 0 sends both halts for the axis |
| `SendCommandString`/`Bool`/`Blind` | Through the Command lane, with the catalog's reply shape (unknown commands: read until quiet). Blocklisted commands throw |
| Actions | `Lx200.LinkState`, `Lx200.TraceFile`, `Lx200.Firmware`, `Lx200.PulseStrategy`, `Lx200.SetMountClock` |

### Pulse guiding: NINA's mount dither

NINA's `DirectGuider` dithers only through `ITelescopeMediator.PulseGuide` and the `TelescopeInfo` the host broadcasts. The driver reports the `:Rg` rate as both guide rates (there is no getter), which turns dither pixels into pulse lengths. `PulseGuide` returns at once. `IsPulseGuiding` is true from the call until the mount has stopped. With `SerializePulseAxes` (default), the second axis starts when the first ends, until the bench shows that the Autostar takes two axes at once.

| `PulseStrategy` | Commands | Notes |
|---|---|---|
| `NativePulse` | `:Mg{n,s,e,w}DDDD#`, mount-timed. Longer than 9999 ms goes in pieces | Whether stock firmware accepts it in alt-az mode is plan risk 1 |
| `HostTimedMove` | `:RG#` before every pulse, `:M{d}#`, then `:Q{d}#` due at start + duration in the Timed lane | Moves on any firmware. In alt-az, n/s/e/w move altitude and azimuth |
| `GotoOffset` | Fresh `:GR#`/`:GD#`, plus duration × rate (east = +RA, north = +Dec), then `:Sr`/`:Sd`/`:MS#`. Both of a dither's pulses arrive within 150 ms and become one goto | RA moves in whole seconds of time (about 14″ at Dec -20, RVM MNT-M4), so this is for large dithers only. The achieved offset is logged |
| `Auto` (default) | `NativePulse` on StarPatch firmware (`:GVN#` ends in an upper-case letter, e.g. `4.2G`), `HostTimedMove` otherwise | Meade.net's rule (RVM MNT-M5) |

### `Lx200Focuser`

- **Virtual position.** The position is in milliseconds of motor time at one locked speed. It starts at `MaxStep/2` (65000/2) on connect, as NINA does for relative ASCOM focusers (`AscomFocuser.cs:42-55`, `169-174`).
- **A move.** A move to *p* runs |*p* − `Position`| ms, then sets `Position` = *p* exactly, so `FocuserVM`'s `while (Position != target)` loop ends.
  - The speed (`:F1#`-`:F4#`, default 2) is sent before every move, because the handbox can change it.
  - A lower position is inward (`:F+#`), as NINA's In buttons expect. `FocuserReverse` swaps it.
- **Host-timed moves** (default): `:F+#`/`:F-#`, then `:FQ#` due at start + ms in the Timed lane, and a second `:FQ#`.
- **Mount-timed moves** (`FocusMethod = MountPulse`): one `:FP±DDDD#` per 65 s, waited out.
- **Halt and cancellation** stop at once with `:FQ#` through the Stop lane; the position then grows by the time that actually ran.
- **Backlash.** `FocuserBacklashMs` (default 0) adds motor time on a change of direction and does not count it in the position. With 0, NINA's own backlash compensation does the work.
- **If the link drops during a host-timed move**, its halt is owed and is the first thing sent when the mount answers again. The move counts as having run until that halt (`Lx200Link.OwedStopSentUtc`), and a warning says the position is an estimate. If the link does not come back, `Move` throws.
- **Other members.**
  - `Temperature` is `:fT#` (polled every 60 s) when the mount answers it, otherwise NaN.
  - `StepSize` is NaN; there is no temperature compensation.
  - The `Lx200.RecenterPosition` action puts the position back to the middle.

### Settings (`Lx200Settings`)

Settings live in the active NINA profile, under the plugin-store key `Lx200Settings.SettingsId`, so each optical-train profile keeps its own. The M2 bench session decides the values marked "bench".

| Setting | Default | Source / decided by |
|---|---|---|
| `PortPath` | empty: the only `/dev/cu.usbserial-*`/`usbmodem*` | Several or none: a connect error that names them |
| `PollIntervalMs` | 1000 | |
| `PulseStrategy` | `Auto` | bench step 6 |
| `GuideRateArcsecPerSec`, `SetGuideRateOnConnect` | 10.0, true | close to Meade.net's 10.08; at most 15.0417 |
| `SerializePulseAxes` | true | bench step 6 (two axes back to back) |
| `MaxSyncOffsetDegrees` | 3 | RIM MNT-11 |
| `MinimumSlewSeconds`, `SlewTimeoutSeconds`, `ArrivalToleranceArcmin` | 1.5, 240, 10 | RVM MNT-04; the probe's 10' guard; bench step 5 trace |
| `EnsureLowPrecisionPointing` | true | RIM MNT-09 |
| `ReportTargetWithinResolution` | true | plan §6 settle loop |
| `DateConvention` | `Unknown` | bench step 4 |
| `SiteToleranceArcmin` | 1 | RIM MNT-14 |
| `SoftParkStopsTracking`, `SoftParkAltitude`/`Azimuth` | true, NaN (park in place) | bench step 8 (`:AL#`/`:AA#` keeps the alignment?) |
| `FocuserSpeed`, `FocuserMaxStep`, `FocusMethod` | 2, 65000, `HostTimed` | bench step 7 |
| `FocuserReverse`, `FocuserBacklashMs`, `FocuserDoubleHalt` | false, 0, true | bench step 7 |

### Host contract (M7/M8)

- **Devices.** Create one `Lx200EquipmentProvider(profileService)` and pass it to `TelescopeChooser` and `FocuserChooser` as their `IEquipmentProvider`. It always lists the same two instances, cable or not; the port is resolved at connect.
- **The telescope mediator.** It must forward `PulseGuide` to `ITelescope.PulseGuide`, as `TelescopeVM` does. It must also broadcast `TelescopeInfo`, including `IsPulseGuiding` and both guide rates, to `DirectGuider`.
- **What NINA's own logic does with this driver.**
  - Park goes to the soft park.
  - The meridian-flip trigger stays idle.
  - The site check stays quiet within 1'.
  - The post-sync settle loop ends at once.
- **`TimeSync`** in the profile means for this driver: write the Mac's time only to a mount that is not yet aligned.
- **Quitting.**
  - On a normal quit, disconnect both devices: the link sends any owed halts before it closes.
  - On an abnormal exit path, call `Lx200Link.EmergencyStop(reason)`.
  - Blocking sleep and App Nap while connected (RIM MNT-26) is the host's job, not the driver's.

### Tests (`tests/NINA.Mac.Equipment.Lx200.Test`)

The Autostar II simulator (`NINA.Mac.Lx200.Sim`) sits behind `SimCable`, an in-memory cable that can be unplugged, muted (the mount is off but the cable is in) and throttled to 9600 baud with a 15 ms turnaround. One test runs the real path instead: a pseudo-terminal through `System.IO.Ports` and the pool's default opener. Every rig checks on dispose that the simulator never received `:hP`.

| Fixture | What it shows |
|---|---|
| `Lx200LinkTest` (12) | **Lanes:**<br>• Stop, then Timed, then Command, then the 28 queued polls; the halt left in 56 ms.<br>• Coalesced polls.<br>• Timed sends at their due time under polling.<br>• A cancelled request is never sent.<br>**Errors:**<br>• NAK retry.<br>• Junk replies dropped without losing step.<br>• Blocklist before queueing.<br>• Every byte in the trace, 0xDF included.<br>**Recovery:**<br>• Unplug, then replug: the link reconnects and sends the owed `:Qn#`/`:Qs#` first.<br>• A silent mount counts as lost and comes back.<br>• Give-up after the configured time.<br>• A refused halt is resent, then reported. |
| `Lx200TelescopeTest` (24) | **Connect:**<br>• Format and High Precision pointing switched.<br>• Firmware, `Auto` strategy, guide rate.<br>• Property reads send nothing.<br>**Gotos and sync:**<br>• J2000 in, JNow from NINA's transform out (more than 0.2° apart), and the mount arrives.<br>• Goto below the horizon refused.<br>• Alt-az goto.<br>• Sync moves the mount; the target is reported exactly.<br>• Sync more than 3° away refused.<br>• `StopSlew` and cancellation send `:Q#`.<br>**Alt-az specifics:**<br>• Soft park, including to a stored position.<br>• `:hP#` refused on every path.<br>• No flip, pier side unknown.<br>• Site tolerance and writes.<br>**Clock rule:**<br>• Clock never written on an aligned mount (warned instead).<br>• Written before alignment when the dates agree.<br>• Not written at 00:30 HKT until the convention is known.<br>• A mount without `:GW#` counts as aligned.<br>**Other:**<br>• Tracking with `:AL#`/`:AA#`/`:TL#`.<br>• `MoveAxis` rates and halts.<br>• Disconnect closes the link.<br>• Connecting with no mount fails with a message. |
| `Lx200DitherTest` (5) | NINA's `DirectGuider` (through a mediator and a 100 ms info pump) dithers the simulated mount with each strategy, and the shift matches the pulses:<br>• `:Mg` (exact, simulator in equatorial pulse mode);<br>• host-timed moves (the axes moved by the simulator-received move times, within 0.5″);<br>• goto offset (one goto per dither, offset within the 1 s RA step).<br>Also: `Auto` on stock firmware, and pulse timing to the millisecond at 9600 baud under 100 ms polling. |
| `Lx200FocuserTest` (11) | **Moves:**<br>• Start at 32500; speed locked at connect and before every move, even after a handbox change.<br>• A host-timed 800 ms move runs 795-830 ms (drawtube travel to match) and lands exactly on the target.<br>• `:FP`.<br>• Clamping and recentre.<br>**Stopping:**<br>• Halt and cancellation count what ran.<br>**Settings:**<br>• Backlash on reversal is not counted.<br>• Reverse.<br>**Shared link:**<br>• One link shared with the mount; the halt is on time under polling at 9600 baud.<br>**Recovery:**<br>• Unplug during a move: the halt goes out on replug and the position is an estimate. |
| `Lx200RecoveryTest` (4) | **Unplug and replug while connected:**<br>• The devices stay connected and the same link recovers.<br>• Notifications.<br>• A goto during the outage fails cleanly.<br>• After the replug: polling, a goto and a focuser move all work.<br>**Other recovery cases:**<br>• A manual move running at the unplug is halted first thing on replug.<br>• If the cable never comes back, the devices disconnect.<br>**Latency:**<br>• `StopSlew` latency at 9600 baud. |
| `Lx200IntegrationTest` (5) | • The choosers list and select both devices from the profile.<br>• Goto and focus over a pseudo-terminal through `System.IO.Ports`, with the trace file in NINA's log folder.<br>• No string in the compiled driver spells the park command.<br>• Settings defaults and storage.<br>• Port resolution, firmware and `:GW#` decoding. |

### Not verified, and open

- **No hardware.** Every result above is against the simulator.
  - Not verified on the real LX200GPS or FTDI cable: real reply bytes and timings, what `System.IO.Ports` does on macOS when `/dev/cu.usbserial-*` disappears, latency, and whether the Autostar keeps an alignment through `:AL#`/`:AA#`.
  - The driver treats a stream end, a read or write exception and a silent mount as a lost link. Which of these a real unplug produces is not known.
- **The simulator's figures are assumptions:** focuser speeds, move rates and slew times. The bench decides the strategy, the date convention, the focus method, speeds and backlash.
- **Plan M4's "centring converges against simulated solves"** is not covered here. `NINA.Platesolving.Mac`'s `CenteringLoopTest` runs NINA's `CenteringSolver` against its own simulated mount (see NINA.Platesolving above). It has not been run with `Lx200Telescope` and the Autostar simulator behind the telescope mediator.
- **Protocol-library observations** (it is read-only for M4):
  - **Junk before a `#`-terminated reply** (simulator quirk `garbage-every`) is accepted as that reply. Only the driver's parsers catch it; the real reply is then dropped as stale before the next command. A per-command reply grammar in the catalog would catch it at the link.
  - **The NAK window is per connection.** A per-command window would let timed starts skip it.
  - **`Lx200Reply` cannot be constructed outside the library,** so the link reports a lost link as `Lx200DisconnectedException`, not as a reply.

## Mac replacements (`NINA.Core.Mac/MacReplacements`)

Each replacement keeps the static API that engine code calls, plus the mac-only hooks noted below. `MyMessageBox` drops upstream's instance API (see below). `UpstreamParityTest` pins a hash of each replaced upstream file, so an upstream change to one fails the tests until the replacement is re-synced.

- **`Notification`**: every `Show*` raises the mac-only event `Notification.Posted` (`Kind`, `Header`, `Message`, `Lifetime`), with the header and lifetime upstream would use. With no subscriber the call is a no-op, which is what upstream does when no WPF `Application` exists.
- **`MyMessageBox`**: `Show` asks the mac-only `MyMessageBox.Host` (an `IMyMessageBoxVM`). With no host it throws `PlatformNotSupportedException`. Guessing an answer would silently change what equipment and sequencer code does. Upstream's `MyMessageBox` is also the view model of its WPF dialog; the mac class keeps only the three static `Show` overloads and drops the `BaseINPC` base class and the instance members `Title`, `Text`, `DialogResult`, `CancelVisibility`, `OKVisibility`, `YesVisibility` and `NoVisibility`, which only the WPF dialog uses.
- **`WindowService`**: `Show` and `ShowDialog` throw `PlatformNotSupportedException`. `Close` and `DelayedClose` do nothing, as upstream does with no window open. `IWindowService`, `IDispatcherOperationWrapper` and `DialogResultEventArgs` are copied unchanged. The upstream `DispatcherOperationWrapper` class is omitted.

## WpfCompat inventory

The rule: the shims contain only WPF API, and only what the compile errors required. Value semantics match WPF. UI members throw. `WpfApiSurfaceTest` enforces that every public type and member exists in WPF with the same signature, and that enum values match.

| Type | Behaviour |
|---|---|
| `Media.Color` | Full WPF value semantics, with sRGB bytes and scRGB floats kept in step: `FromArgb`, `FromRgb`, `FromScRgb`, the channel setters (including WPF's truncating `ScA`), equality on the scRGB floats, every `ToString` form, and DataContractSerializer XML. ICC `ColorContext` is absent. `GetHashCode` is consistent with equality but its values differ from WPF's |
| `Media.Colors` | The 141 named colours |
| `Media.ColorConverter` | Parses hex, `sc#` and named colours exactly like WPF, with the same exception types and messages. It is `Color`'s `TypeConverter`, so Newtonsoft writes `"#AARRGGBB"` as on Windows. `ContextColor` strings throw |
| `Point`, `Vector`, `Media.Media3D.Vector3D` | Components with setters, `==`/`!=` (IEEE `==`), `Equals` (`double.Equals`, so NaN equals NaN), WPF's XOR hash code, and every `ToString` form with WPF's numeric list separator (`,`, or `;` when the decimal separator is `,`). `GeometryOracleTest` matches all of it bit for bit against WindowsBase/PresentationCore, including NaN, -0.0 and infinities. Arithmetic, `Parse` and the matrix/point/vector operators are absent |
| `Freezable`, `Media.ImageSource`, `Media.ImageMetadata` | WPF's base classes (`BitmapSource : ImageSource : Freezable`). `Freeze` sets `IsFrozen`, and the settable compat types (`ScaleTransform`, `BitmapMetadata`, the initialisable bitmaps) throw `InvalidOperationException` once frozen, as WPF does. `ImageSource.Width`/`Height` are device-independent units with WPF's single-precision pixel-to-DIP rule. No `DependencyObject`, thread affinity or `Changed` event |
| `Int32Rect` | WPF value semantics: empty when all four fields are 0, equality on the fields, hash 0 when empty and the XOR of the fields otherwise, `ToString` "Empty" or "x,y,w,h" with WPF's numeric list separator. `Parse` is absent |
| `Media.PixelFormat`, `Media.PixelFormats` | The nine predefined formats NINA uses (`Indexed8`, `Gray8`, `Bgr565`, `Gray16`, `Bgr24`, `Bgr32`, `Bgra32`, `Pbgra32`, `Rgb48`), identified by WPF's WIC GUIDs, with WPF's names and bit depths. Masks and custom formats are absent |
| `Media.Imaging.BitmapSource` | A managed pixel buffer in place of a WIC bitmap. `Create` (array or pointer) copies the pixels into rows packed at the minimum stride, as WIC's `CreateBitmapFromMemory` does; `CopyPixels` (array with an element offset, or pointer; whole bitmap or a rectangle) copies them out. Argument checks follow WPF's (accepted element types, stride and buffer minimums, the rectangle inside the bitmap, a palette required for indexed formats). `NINA.Image` renders every frame through it (Gray16 over the raw array) and reads pixels back from it; `NINA.Astrometry` only types the optional sky-survey preview with it |
| `Media.Imaging.WriteableBitmap`, `FormatConvertedBitmap`, `TransformedBitmap` | Copy constructor only for `WriteableBitmap`. `FormatConvertedBitmap` keeps WPF's `BeginInit`/`EndInit` protocol but converts only to the source's own format; other conversions throw `NotSupportedException` (WIC's gamma and colour rules cannot be checked here, and no macOS engine path converts). `TransformedBitmap` supports positive `ScaleTransform`s with WPF's output size (`max(1, (uint)(scale × pixels + 0.5))`) and an area-weighted average per channel for Gray8, Gray16, Bgr24, Bgr32, Bgra32, Pbgra32 and Rgb48; WPF's Fant filter is close for downscaling but not bit-identical. `GetThumbnail` relies on it |
| `Media.Geometry`, `Media.GeometryGroup` | Signature-only, with WPF's hierarchy minus `Animatable` (`GeometryGroup : Geometry : Freezable`; `Geometry`'s constructor is internal, as in WPF). NINA.Equipment's `IDockableVM.ImageGeometry` (the dock icon) is typed with it; on Windows the icons come from XAML resources. An empty group can be created; children, fill rule and bounds are absent |
| `Media.Transform`, `Media.ScaleTransform`, `Media.ColorContext`, `Media.Imaging.BitmapPalette` | `ScaleTransform` holds its two factors; the others are signature-only (NINA passes null palettes and colour contexts) |
| `Media.Imaging.BitmapMetadata`, `BitmapFrame`, `BitmapEncoder`, `TiffBitmapEncoder`, `BitmapDecoder`, `Gif/Tiff/Jpeg/PngBitmapDecoder`, `TiffCompressOption`, `BitmapCreateOptions`, `BitmapCacheOption` | WIC codecs. `BitmapMetadata` and the encoder settings hold values; `BitmapFrame.Create`, `BitmapEncoder.Save` and every decoder constructor throw `PlatformNotSupportedException`, so TIFF saving and GIF/TIFF/JPEG/PNG loading are unavailable. The enums carry WPF's members and values |
| `Threading.Dispatcher`, `DispatcherOperation`, `DispatcherSynchronizationContext`, `DispatcherObject`, `DispatcherPriority`, `DispatcherOperationStatus` | Per-thread dispatcher (`CurrentDispatcher`, `CheckAccess`). If the owning thread has a `SynchronizationContext` (a UI loop), cross-thread `Invoke` uses Send and `BeginInvoke` uses Post. The loop is taken when the dispatcher is created, or when an `Application` is created on that thread later, so touching the dispatcher (for example through `CommandManager`) before the UI framework installs its context is harmless. Without a loop, work items never overlap: on an idle dispatcher `Invoke` and `BeginInvoke` run inline on the caller; while an item runs, `Invoke` from another thread waits for it, `Invoke` from inside an item runs inline, and `BeginInvoke` queues and returns `Pending`, so it never blocks. Queued items run in order on a thread-pool thread after the current item, which also means a `BeginInvoke` from inside an item runs after it, as in WPF. A work item that blocks on a thread which calls `Invoke` deadlocks, as in WPF. Priorities are not reordered; `Inactive` throws. A failing `BeginInvoke` faults the operation's `Task`, where WPF raises `UnhandledException` |
| `Application` | As in WPF, `Current` is null until the host constructs one, and only one can exist. `Resources` is a keyed store. Window and lifetime API is absent |
| `ResourceDictionary` | Hashtable-backed `IDictionary`; a missing key returns null |
| `Input.CommandManager` | WPF requery semantics: weak handlers, per-thread, coalesced, raised through the thread's dispatcher at Background priority (inline when that dispatcher is idle and has no loop). There is no input-driven requery |
| `ThemeInfoAttribute`, `ResourceDictionaryLocation` | WPF's assembly attribute and its enum, so upstream `Properties/AssemblyInfo.cs` compiles unchanged. Metadata only |
| `Markup.MarkupExtension` | Abstract base, as in System.Xaml. It keeps `EnumDescriptionTypeConverter`, which lives in `EnumBindingSourceExtension.cs`, compiling |
| `Window`, `Data.Binding`, `Microsoft.Win32.OpenFileDialog` | Signature-only placeholders; their constructors throw `PlatformNotSupportedException` |
| `MessageBoxResult`, `MessageBoxButton`, `ResizeMode`, `WindowStyle`, `Data.BindingMode` | Enums with WPF's members and values |

## Contract for a host (app head or test runner)

1. Set `CoreUtil.APPLICATIONTEMPPATH` before anything touches `Logger`, `ProfileService` or `new DatabaseInteraction()`, if the data folder should move. The default is `~/Library/Application Support/NINA`. Logs, profiles and the catalogue database `NINA.sqlite` live there.
2. Create one `new System.Windows.Application()` at startup, on the UI thread if there is one, after the UI framework has installed its `SynchronizationContext` on that thread (with Avalonia, once its main loop is set up). That thread's dispatcher then marshals through the loop, even if it was used earlier. `ProfileService` raises its events through `Application.Current.Dispatcher`. Without an `Application` those calls throw `NullReferenceException`, on Windows as well. A headless host without a loop gets the serialized loop-less dispatcher described above.
3. Subscribe to `Notification.Posted`. Install `MyMessageBox.Host`, or accept that prompts throw.
4. Ship `libsofa.dylib`, `libnovas31.dylib`, `External/JPLEPH` and `Database/` next to `NINA.Astrometry.dll`. Referencing `NINA.Astrometry.Mac` copies all of them. SOFA/NOVAS resolution needs no host call. In an .app, `Contents/MacOS/External/JPLEPH` and `Contents/MacOS/Database` may be relative symlinks into `Contents/Resources` (see NINA.Astrometry above).
5. Call `NINA.Astrometry.Mac.EngineData.EnsureAvailable()` once at startup and show its exception to the user. Without it, a missing ephemeris gives silently wrong Moon and planet positions.
6. NINA.Image needs no host call (its resolver registers itself). Bind `IPluggableBehaviorSelector<IStarDetection>` and `<IStarAnnotator>` to mac behaviours (see "Star detection" above), never call `RenderedImage.Stretch` or `Debayer`, and do not offer TIFF, the CFITSIO writer or SHA-3 XISF checksums in the UI (see "What works on macOS").
7. NINA.Equipment needs no host call for the ZWO SDK (its resolver registers itself; `libASICamera2.dylib` and `libusb-1.0.0.dylib` come from `mac/scripts/stage-zwo.sh` through `NINA.Mac.Native`). Build the device lists with `NINA.Equipment.Mac`'s choosers, passing the M4 LX200 drivers as `IEquipmentProvider<ITelescope>`/`<IFocuser>`; bind no `UsbDeviceWatcher`, `ISbigSdk` or plugin scan. Wrap the selected camera in `PersistSettingsCameraDecorator` before `Connect`, as `CameraVM` does, so the profile's binning, gain, offset and USB limit are restored. Switch the cooler off (or warm up) before `Disconnect`: `ASICamera.Disconnect` only closes the camera. Give a new mac profile `GuiderSettings.GuiderName = "Direct_Guider"` (upstream's default `PHD2` shows as offline).
8. NINA.Platesolving needs no host call for the solvers themselves; `PlateSolverFactory` creates them from the profile. At startup, set `AstrometryNetSetup.IndexDirectories` if the index files are not in `~/Library/Application Support/Astrometry` (and `CpuLimitSeconds` if 300 s per field is too long), and put `AstapSetup.Resolve(<astap_cli>, <database folder>)` into the profile's `PlateSolveSettings.ASTAPLocation`; a new rig profile gets `RigPlateSolveDefaults.Apply`. Both helpers write into `<CoreUtil.APPLICATIONTEMPPATH>/Solvers`, so set `APPLICATIONTEMPPATH` first (item 1). Never offer ASTAP as the blind solver for this field.

## Upstream edits (Windows no-ops)

| File | Change |
|---|---|
| `NINA.Core/Utility/DllLoader.cs` | `LoadDllFromAbsolutePath` returns early with a debug log when `!OperatingSystem.IsWindows()`. It is reached by `LoadDll`, so this covers every `DllLoader.LoadDll` call in Astrometry, Equipment and Image (NINA.MGEN has its own `DllLoader`). macOS loads native libraries through `NativeLibrary` resolvers |
| `NINA.Core/Utility/SerialCommunication/SerialPortProvider.cs` | Off Windows, the constructor skips the WMI scan, which would throw `PlatformNotSupportedException`. `GetPortNames` lists `SerialPort.GetPortNames()`, filtered to `/dev/cu.*` on macOS, behind the usual divider. Both are `if (!OperatingSystem.IsWindows())` branches, so Windows runs the original code. This is generic portability, not something the rig needs today: only `SerialSdk` constructs the class, for the Alnitak, Artesky and Pegasus FlatMaster flat panels. The rig's LX200 link (`NINA.Mac.Lx200`) opens `System.IO.Ports` itself and does not use NINA.Core's serial classes. `CoreSmokeTest` covers the mac branch. The file stays ISO-8859-1 with LF line endings |
| `NINA.Astrometry/DatabaseInteraction.cs` | The parameterless constructor gets its path from a new private `DefaultDatabaseLocation()`. On Windows that returns the same `Environment.ExpandEnvironmentVariables(@"%localappdata%\NINA\NINA.sqlite")` as before. Off Windows it returns `Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NINA.sqlite")`. The file stays ISO-8859-1 with LF line endings |
| `NINA.Image/FileFormat/XISF/XISFHeader.cs` | `Save` sets `NewLineChars = "\r\n"` on its `XmlWriterSettings`. The default is `Environment.NewLine`, which is already `"\r\n"` on Windows, so Windows writes the same bytes as before. On macOS the XML header would otherwise use `"\n"`: 6 bytes shorter for an empty header (one per line break), which also moves the attached image's block offset, so files differed from Windows' and upstream `XISFTest.XISFAddAttachedImage_Special_Test` failed (offset 6144 instead of 7168). Now the header bytes, `ByteCount` and the attachment location equal Windows'. There is no mac-side alternative short of forking the file. The file stays UTF-8 with BOM and LF line endings |
| `NINA.Equipment/SDK/CameraSDKs/ASISDK/ASICameraDll.cs` | ZWO's header declares some values as C `long`: 8 bytes on macOS arm64, 4 on Windows. New private structs `ASI_CAMERA_INFO_NATIVE` and `ASI_CONTROL_CAPS_NATIVE` mirror the public ones with `CLong` for `MaxHeight`/`MaxWidth` and `MaxValue`/`MinValue`/`DefaultValue`. `ASIGetCameraProperty`, `ASIGetCameraPropertyByID` and `ASIGetControlCaps` fill a mirror, and the wrappers copy it into the unchanged public struct. The private externs `ASISetControlValue`/`ASIGetControlValue` (value) and `ASIGetVideoData`/`ASIGetDataAfterExp` (buffer size) take `CLong`. On Windows `CLong` is 4 bytes, so the native layout and calls are unchanged, and the public structs keep their `int` fields, so plugins compiled against NINA.Equipment keep working. Nothing else changes: the static constructor still calls `DllLoader.LoadDll`, which is a no-op off Windows (above). The probe's `ASIGetGainOffset`/`ASIGetLMHGainOffset` calls live in `NINA.Mac.ZwoProbe/GainPresets.cs`. `tests/NINA.Mac.Test/AsiAbiTest.cs` checks the mirrors against the arm64 header layout, field for field against the public structs, and that the public fields stay `int` fields. The file stays ASCII with LF line endings |
| `NINA.Platesolving/Solvers/ASTAPSolver.cs` | Plan P1. `EnsureSolverValid` runs the legacy-version check (`FileVersionInfo.GetVersionInfo(...).FileVersion == null` with `DownSampleFactor == 0` → "ASTAP version below 0.9.1.0") only `if (OperatingSystem.IsWindows())`. On Windows the code inside the guard is the original, so behaviour is unchanged. Off Windows `FileVersionInfo` reads only managed metadata, so `FileVersion` is always null for ASTAP and every solve with NINA's default auto downsample was rejected. The two checks before it (location missing, executable not found) still run everywhere. Upstream's `SolverTranslationBehaviorTest.ASTAPSolver_ValidationRejectsMissingOrLegacyAutoDownsampleConfiguration` tests the Windows-only branch and is skipped on macOS (see below). The file stays UTF-8 without BOM, LF line endings |

## Upstream tests on macOS (`tests/NINA.Mac.Astrometry.Test`)

The test project links these `NINA.Test` files unchanged, with the same `ImplicitUsings`/`Nullable` settings as `NINA.Test.csproj`:

- `Usings.cs`
- `AstrometryTest/*` and its `HorizonData` files
- `AngleTest`, `CoordinatesTest`, `NighttimeCalculatorTest`, `NighttimeDataTest` and `Database/DatabaseInteractionTest`
- `RMSTest`, `Model/*`, `SerialCommunication/*` and `Utility/SerialCommunication/*`
- 13 `Utility/*` fixtures
- `ProfileTest/*` except one

A module initializer (`Mac/TestHost.cs`) points `CoreUtil.APPLICATIONTEMPPATH` at a fresh temp folder before anything runs, so logs, profiles and databases never touch the user's folder. Set `NINA_MAC_KEEP_TEST_DATA=1` to keep that folder after the run.

Left out because they need code that is not ported:

- `ProfileTest/ProfileServiceBehaviorTest` needs an STA apartment and WPF `Application.ShutdownMode`.
- `Utility/CustomWindowTest`, `DataPipesTest`, `ValidationRules/*` and `Converters/*` test WPF code that `NINA.Core.Mac` excludes.
- `Utility/BlittableTest`, `CommandLineOptionsTest`, `MvvmLightCommandTest` and `PluggableIntegrationBehaviorTest` need Equipment, the app or WPF.Base. `Utility/ImageUtilityTest` runs in `NINA.Mac.Image.Test`.
- Everything else in `NINA.Test` needs Equipment, Image, PlateSolving, Sequencer, WPF.Base or the app.

### Results and known failures

Results: 1854 tests, 1848 passed, 6 failed, 0 skipped. Of these, 67 are mac tests, all passing, and 1787 are upstream cases (63 fixture classes), of which 1781 pass. The six failures are platform differences, not port bugs. Their expected values are left as upstream wrote them.

The result is the same under `TZ=UTC` with `en_US`, `TZ=America/New_York` with `de_DE` (decimal comma, DST, negative offset), `TZ=Pacific/Auckland` with `fr_FR`, and this Mac's own Hong Kong (UTC+8) with `en_HK`: the same six failures and nothing else, so no linked test depends on the time zone or culture.

- **`CoordinatesTest.ShiftStereographic_CoordinatesTest` (5 cases)** are the cases with Dec 80, 10° offsets and rotation 0/90/180/270/360. RA is off by 1.39e-11 to 1.52e-11 degrees, and the test tolerance is 1e-12. This is managed code (`System.Math`), not SOFA or NOVAS.
  - A replay of `Coordinates.ShiftStenographic` (same operations, `Math.Asin`/`Sin`/`Cos`) matches `NINA.Astrometry` on this Mac bit for bit in all 30 Dec 80 cases.
  - Making one call, `asin(0.9851114915427427)` (the target declination), return 1 ulp less reproduces upstream's expected values exactly in all 30 cases, the five failing ones included.
  - In 70-digit arithmetic that asin is 1.39802132595848117566...: macOS returns the correctly rounded double (0.45 ulp off), and upstream's expectations need the neighbour 0.55 ulp off. So the runtime that produced upstream's expected values (Windows) evidently does not round this `asin` correctly; macOS does. This is inferred from the values, not checked on Windows.
  - The next `asin`, whose argument is near 1 (RA offset about 90°), magnifies that ulp (2.2e-16 rad) about 1,100 times, to 2.4e-13 rad. That is 5e-8 arcseconds. The declination itself moves by only 1.3e-14°, within tolerance.
- **`ImagePatternsTest.GetImageFileString_PathSegmentsAndImageTypeOverride_ReturnsSafePath`** now passes: see "File-name sanitising" below.

### Cross-checks added for M3

- **J2000 to JNow:** `Coordinates.Transform(Epoch.JNOW)` uses SOFA. It agrees with NOVAS `place()` (equinox of date, full accuracy, DE421) to better than 0.01 mas for M42, Polaris, Spica, Vega and RA 0/Dec 0, both at the moment the test runs and at four fixed instants from 2024 to 2035 given through NINA's `ICustomDateTime` hook as Hong Kong local time. Transforming back to J2000 returns the input to within 0.0001 mas.
- **Sidereal time:** local sidereal time at 114.18 E agrees with SOFA's IAU 2006/2000A apparent sidereal time (`iauGst06a`, an implementation independent of the NOVAS code NINA calls) plus the longitude to within 0.0004 ms at the same UT1 and TT, for 2000, 2024, 2026 and 2035. Against Meeus's GMST plus the longitude, which checks NINA's UTC to UT1 and TT steps as well, it agrees within 0.54 to 0.70 s; the bound is the equation of the equinoxes plus UT1-UTC. A UTC instant and the same instant as local time give the same value.
- **M42:** a database built from the upstream scripts in a temp folder finds "M42" as NGC1976 "M 42" at RA 83.82208333°, Dec -5.39111111° (05h35m17.3s, -05°23'28"), ORI, CL+NB, mag 4.0, with `LBN 974` from migration 7.
- **Data folder:** NINA's default `CoreUtil.APPLICATIONTEMPPATH` resolves to `~/Library/Application Support/NINA` on .NET 10 (checked before the test host moves it to a temp folder).
- **Matching Windows NINA:** there is no Windows machine and the shipped Windows DLLs are Git LFS objects that are not checked out, so nothing here compares against a Windows run directly. The evidence is that the upstream expected values (written on Windows) pass, that SOFA's and NOVAS's own validation programs pass against the arm64 dylibs, and that SOFA and NOVAS agree with each other as above.

## Upstream tests on macOS (`tests/NINA.Mac.Image.Test`)

Linked unchanged, with `NINA.Test.csproj`'s `ImplicitUsings`/`Nullable` settings: `Usings.cs`, `ImageDataFactoryTestUtility.cs`, `FITSTest`, `XISFTest`, `ImageDataTest`, `FilePatternTest`, `Utility/ImageUtilityTest`, `Image/ExposureDataFactoryTest`, `Image/StarDetectionMeasurementTest`, `Image/FileFormat/*` (2), `Image/ImageAnalysis/*` (3), `Image/ImageData/*` (3) and `Image/RawConverter/*` (1): 16 fixture classes. `Mac/UpstreamTestShims.cs` declares the empty namespace `NINA.Equipment.Equipment.MyCamera`, which `ImageDataTest.cs` imports without using. Left out: `ImageMetaDataTest` and `Equipment/ImageMetaDataExtensionBehaviorTest` (NINA.Equipment), `ImageHistoryVMTest`, `Mediator/ImageSaveMediatorTest` and `ViewModel/*` (app, WPF.Base), `PlateSolving/*` (M3b part 2), `Autofocus/*` and `Sequencer/*`. The host setup is the same as Astrometry's (`Mac/TestHost.cs`, `NINA_MAC_KEEP_TEST_DATA=1`).

Tests inside the linked fixtures that exercise GDI+ or the WIC TIFF codec are reported as Skipped with the reason, by `Mac/MacPlatformSkips.cs` (an assembly-level NUnit action that lists them by class and method). `MacPlatformSkipsTest` checks that every entry names a linked test. `NINA_MAC_RUN_SKIPPED=1` runs them anyway: then all 34 fail, 31 with `DllNotFoundException: gdiplus.dll` and 3 with the WIC `PlatformNotSupportedException` (checked), and the TIFF cases leave `TestFile.bar`/`TestFile.tif` in the test output folder, which the next run's XISF case trips over; delete them afterwards.

### Results

Last full run (`mac/dotnet test mac/tests/NINA.Mac.Image.Test/NINA.Mac.Image.Test.csproj`, about 9 s; the console prints 358 total, 309 passed, 2 failed, 47 skipped and a duration of about 8 h, which is NUnit's bogus duration for the STA case it cannot start; the trx counted below lists two more not-run entries, from duplicate-named upstream cases and the explicit hardware test):

| | Passed | Failed | Not run |
|---|---|---|---|
| Upstream cases (16 fixtures, 271 results) | 221 | 2 | 48: 31 GDI+ and 3 WIC (skip list), 13 upstream `[Ignore]` (file-backed and large real-world Bayer cases, opt-in upstream too), 1 `[Apartment(STA)]` Bahtinov case that NUnit cannot run off Windows (GDI+ as well) |
| Mac tests (13 fixtures) | 88 | 0 | 1: the `[Explicit]` hardware test |

The two failures are platform differences in the tests' expectations, not port bugs; their expected values are left as upstream wrote them:

- **`FilePatternTest.Pattern_Remove_TrailingAndLeading_Whitespace_FromFilesAndFolders`** expects the folder segments joined with `\`. macOS joins them with `/` (`Path.Combine`), which is the right path here; the segments themselves are identical.
- **`XISFTest.XISFHeaderConstructorTest`** expects `472 + bytes(declaration + Environment.NewLine)`: 512 on Windows, 511 on macOS. The 472 was measured with CRLF line breaks in the body, so the formula only holds where `Environment.NewLine` is CRLF. NINA writes 512 bytes, the Windows value (upstream edit above); without the edit it wrote 506 and `XISFAddAttachedImage_Special_Test` failed as well.

Same two failures, and nothing else, under `TZ=UTC` with `en_US`, `TZ=America/New_York` with `de_DE`, `TZ=Pacific/Auckland` with `fr_FR`, and Hong Kong with `en_HK`. Under `sv_SE` one more upstream case fails, `FITSTest.FITSOriginalValue_StringTest(-100)`: it builds its expectation with the current culture, and ICU's Swedish minus sign is U+2212, while NINA writes the card with the invariant culture (`-100`, correct for FITS). .NET uses ICU on current Windows too, so this is a test-culture issue, not a macOS one.

The `[Explicit]` hardware test (`Mac/AsiFrameHardwareTest.cs`, category `Hardware`) captures a 0.1 s bin-2 RAW16 frame from the ASI585MC through upstream `ASICameraDll` (compiled into the M1 probe, which the test project references), fills the metadata as `FromCamera` does, saves it through NINA.Image and checks the pixels, the Bayer and binning cards, fitsverify and Siril. It never switches the cooler on and switches it off on every exit path. Run on 2026-10-05: Inconclusive, "No ZWO camera connected" (`ioreg` listed no USB device at the time).

## Upstream tests on macOS (`tests/NINA.Mac.Equipment.Test`)

Linked unchanged, with `NINA.Test.csproj`'s `ImplicitUsings`/`Nullable` settings: `Usings.cs`, `CaptureSequenceTest`, `GuideStepsHistoryTest`, `ImageMetaDataTest` and, from `Equipment/`, `CaptureSequenceBehaviorTest`, `DeviceContractBehaviorTest`, `DirectGuiderTest`, `EquipmentInfoBehaviorTest`, `GuiderModelBehaviorTest` and `ImageMetaDataExtensionBehaviorTest`: 10 fixture classes (`CaptureSequenceTest.cs` holds two). Left out because they need excluded code: `Equipment/ManualDeviceBehaviorTest` (manual filter wheel and rotator), `FilterManagerBehaviorTest`, `GnssBehaviorTest`, `Phd2RpcContractBehaviorTest` (PHD2 client), `Equipment/Camera/*` and `Equipment/SDK/*` (other vendors), and everything that needs NINA.WPF.Base (mediators, view models, choosers, focuser decorators) or the sequencer. The independent FITS reader `FitsFile.cs` is linked from `NINA.Mac.Image.Test`. The host setup is the same as Image's (`Mac/TestHost.cs`, `NINA_MAC_KEEP_TEST_DATA=1`).

### Results

`mac/dotnet test mac/tests/NINA.Mac.Equipment.Test/NINA.Mac.Equipment.Test.csproj` (about 3 s): 127 passed, 0 failed, 0 skipped; the `[Explicit]` hardware test is not run.

| | Passed | Failed | Not run |
|---|---|---|---|
| Upstream cases (10 fixtures) | 93 | 0 | 0 |
| Mac tests (`EquipmentParityTest` 6, `EquipmentAssemblyTest` 7, `AsiSdkTest` 4, `DeviceChooserTest` 10, `DirectGuiderRigTest` 6, `HeadlessCapturePathTest` 1) | 34 | 0 | 1: the `[Explicit]` hardware test |

`DeviceChooserTest.CameraChooser_WithoutACamera_…` is reported as Ignored when a ZWO camera is connected, because enumerating it opens the camera.

The `[Explicit]` hardware test (`Mac/AsiCameraHardwareTest.cs`, category `Hardware`) is the M3 "headless capture" proof on the real camera: the mac `CameraChooser` lists NINA's own `ASICamera` (upstream code, through NINA.Equipment's resolver); it is wrapped in `PersistSettingsCameraDecorator` and connected; its `ICamera` properties must match what `zwoprobe` read in M1 (3840 × 2160, RGGB with offsets 0, 2.9 µm, bins 1-4, gain 0-600, offset 0-200, USB limit 40-100, exposure 32 µs to 2000 s, a cooler, no shutter, 16 bit); then a 1 s bin-2 light runs as `CameraVM.Capture`/`Download` and `ImagingVM.AddMetaData` drive it (`StartExposure`, `WaitUntilExposureIsReady` with exposure + `CameraSettings.Timeout`, `DownloadExposure`, `FromProfile`, `ToImageData`, `SaveToDisk` with the profile's `FileSaveInfo`). The FITS must land in `<date>/LIGHT/…_1.00s_0001.fits`, hold exactly the downloaded pixels (independent reader) with `BAYERPAT` RGGB, `ROWORDER` TOP-DOWN, `XBINNING` 2, `XPIXSZ` 5.8, `GAIN` 200 and `EXPTIME` 1.0, pass fitsverify, and siril-cli 1.4.4 must report `Reading FITS: file frame.fits, 1 layer(s), 1920x1080 pixels, 16 bits`, `Filter Pattern: RGGB from header, Orientation: top-down from header` and a 3-layer 1920x1080 debayered result. The cooler is never switched on; on every exit path it is switched off through `ICamera.CoolerOn` and the raw SDK, read back (must be 0), and only then is the camera disconnected. Run on 2026-10-05 (16:30 and 16:52 HKT): Inconclusive, "No ZWO camera connected (ASICameras.Count = 0)"; `ioreg -p IOUSB` listed no USB device on either bus, so the headless capture on the real camera is still to be run.

## Upstream tests on macOS (`tests/NINA.Mac.Platesolving.Test`)

Linked unchanged, with `NINA.Test.csproj`'s `ImplicitUsings`/`Nullable` settings: `Usings.cs` and all 13 files of `NINA.Test/PlateSolving/` (13 fixture classes). The host setup is the same as Equipment's (`Mac/TestHost.cs`, `NINA_MAC_KEEP_TEST_DATA=1`); NINA's data folder for the run has spaces in its path on purpose, like `~/Library/Application Support`.

Five upstream cases test code or programs that do not exist on macOS by design. `Mac/MacPlatformSkips.cs` (the mechanism of NINA.Mac.Image.Test) reports them as Skipped with the reason and the mac test that covers the same behaviour; `MacPlatformSkipsTest` checks that each entry names a linked test, and `NINA_MAC_RUN_SKIPPED=1` runs them anyway (then all five fail exactly as stated, checked):

| Upstream case | Why it cannot run on macOS | Covered by |
|---|---|---|
| `CliSolverBehaviorTest` ×3 (success clean-up, failure archive, timeout) | its stand-in solver is `cmd.exe /C ...`: `Win32Exception: ... start process 'cmd.exe' ... No such file or directory` | `CliSolverMacTest`, the same three behaviours with `/bin/sh` and a frame saved by NINA's FITS writer |
| `SolverTranslationBehaviorTest.LocalPlateSolver_TranslatesHintedAndBlindArguments` | asserts the Cygwin command line (`/C ""C:\cygwin64\bin\bash.exe" --login -c '/usr/bin/solve-field ...`) of the file the mac build replaces | `LocalPlateSolverMacTest` |
| `SolverTranslationBehaviorTest.ASTAPSolver_ValidationRejectsMissingOrLegacyAutoDownsampleConfiguration` | expects an empty `astap.exe` with auto downsample to be rejected as a pre-0.9.1 ASTAP; that check reads a Windows version resource and runs on Windows only (upstream edit P1) | `AstapSolverMacTest` (missing executable still rejected; auto downsample accepted, also on the real `astap_cli`) |

### Results

`mac/dotnet test mac/tests/NINA.Mac.Platesolving.Test/NINA.Mac.Platesolving.Test.csproj` (2026-10-05, final run): Passed 139, Failed 0, Skipped 5, Total 144, in about 15 s. The trx file has 147 results: the 3 `[Explicit]` `LocalData` tests are listed as not executed.

- Upstream: 13 fixture classes, 70 results. 65 passed and 5 were skipped by `MacPlatformSkips` (table above): `PlateSolverFactoryBehaviorTest` 15, `CaptureSolverTest` 9, `CenterSolverTest` 8, `SolverTranslationBehaviorTest` 7 (2 skipped), `CenteringSolverBehaviorTest` 5, `ImageLinkBehaviorTest` 5, `ImageSolverTest` 5, `AstrometryPlateSolverBehaviorTest` 4, `PlateSolveModelBehaviorTest` 4, `CliSolverBehaviorTest` 3 (all skipped), `BaseSolverBehaviorTest` 2, `ImageSolverBehaviorTest` 2, `TheSkyXImageLinkSolverBehaviorTest` 1.
- Mac: 74 passed: `AstrometryNetSetupTest` 17, `LocalPlateSolverMacTest` 12, `AstapSetupTest` 10, `CenteringLoopTest` 9, `AstapSolverMacTest` 6, `PlatesolvingParityTest` 5, `CliSolverMacTest` 4, `PlatesolvingAssemblyTest` 4, `ReferenceWcsTest` 4, `RigPlateSolveDefaultsTest` 2, `MacPlatformSkipsTest` 1. The tests that run the installed `astap_cli` and `wcsinfo` ran (they are ignored, not failed, where a solver is missing).
- `NINA_MAC_RUN_SKIPPED=1` with a filter on the five skipped cases: all five fail exactly as the table says (3 × `Win32Exception` starting `cmd.exe`; "Expected a ASTAPValidationFailedException to be thrown, but no exception was thrown"; "Expected hintedArgs to start with "/C """). Upstream's `CliSolverBehaviorTest` then writes into `CliSolver/` under the test output folder, as it does on Windows.

### Real solves on this Mac (`Mac/LocalData/RealSolveTest`, `RealCenteringTest`, `[Explicit]`, category `LocalData`)

Read-only: the paths come from `NINA_MAC_SOLVE_FRAMES` (`|`-separated) and nothing is copied into the repository. Each frame is read by an independent FITS reader (`FitsCube`, BITPIX 16/-32, 2 or 3 axes), averaged to mono, and handed to NINA as a `BaseImageData`; the solvers come from `PlateSolverFactory` with `RigPlateSolveDefaults` applied (ASTAP through the launcher, `LOCAL` through the generated `astrometry.cfg` and the user's index folder) and run through `ImageSolver`, so CLISolver writes the frame with NINA's FITS writer exactly as in a capture. The reference is the WCS in the file's header (Seestar's or Siril's), evaluated with an independent TAN+SIP implementation (`ReferenceWcs`, unit-tested in `ReferenceWcsTest`; at the centre of every frame with `CTYPE` cards it agrees with astrometry.net's `wcs-xy2rd` on the same header to better than 0.005″). The position angle and parity are compared with the header's orientation at the frame centre (`ReferenceWcs.LocalCd`), in NINA's convention.

```bash
NINA_MAC_SOLVE_FRAMES="<a.fit>|<b.fit>" NINA_MAC_SOLVE_REPORT=/tmp/solves.md \
  mac/dotnet test mac/tests/NINA.Mac.Platesolving.Test/NINA.Mac.Platesolving.Test.csproj --filter "FullyQualifiedName~RealSolveTest|FullyQualifiedName~RealCenteringTest"
```

`NINA_MAC_SOLVE_GRID` (default `3x3`) sets the patches per frame for `RigGeometry_*`, `NINA_MAC_SOLVE_CPULIMIT` (default 60) solve-field's CPU limit there, `NINA_MAC_SOLVE_BLIND=1` adds blind solve-field runs, `NINA_MAC_CENTER_REPORT=<file>` writes the centring table. A full 3 × 3 run on four frames takes about an hour, almost all of it failing solve-field runs waiting for the timeout.

Frames used (all Seestar S30 Pro stacks of 10 s subs, IMX585 at 160 mm, 16-bit RGB; read in place, never copied):

| Frame | Size | Filter, subs | Reference |
|---|---|---|---|
| IC 434 | 2160 × 3840 | LP (dual band), 247 | the Seestar's TAN-SIP header WCS |
| M51 (the Seestar original in Downloads) | 2160 × 3840 | IR-cut, 305 | the Seestar's TAN-SIP header WCS |
| M42 | 1910 × 1469 (cropped by Siril 1.2.5) | LP, 88 | CDELT/PC header WCS without `CTYPE` cards (TAN assumed) |
| NGC 7000 | 2160 × 3840 | LP, 595 | none: only the target's RA/DEC |
| NGC 7000 stretched | 1938 × 2876 (the same stack cropped, stretched and plate-solved by Siril 1.4.4) | LP, 595 | Siril's TAN-SIP header WCS |

The copy of the M51 stack on the Desktop is Siril 1.2.5's background-extracted version without `CTYPE` cards and without the SIP terms; the original was used.

**Full frames** (`FullFrames_BothSolvers_AgreeWithTheHeaderWcs`, final run 20:41 HKT): time per solve through `ImageSolver` (FITS write, solver, result parsing) / centre error against the reference.

| Frame | ASTAP D80 `-z 2` | ASTAP D80 `-z 0` | solve-field near (5°) | solve-field blind | Scale ″/px, ASTAP / solve-field (header) | PA °, ASTAP / solve-field (header at centre) |
|---|---|---|---|---|---|---|
| IC 434 | 1.0 s / 1.1″ | 0.5 s / 1.1″ | 12.0 s / 1.3″ | 10.0 s / 1.3″ | 3.6618 / 3.6609 (3.66088) | 50.25 / 50.25 (50.26) |
| M51 | 0.6 s / 1.6″ | 0.7 s / 1.6″ | 10.3 s / 0.5″ | 10.4 s / 0.5″ | 3.6617 / 3.6607 (3.66075) | 263.65 / 263.64 (263.64; 262.45 from the CD matrix, which holds at CRVAL) |
| M42 | 0.3 s / 1.2″ | 0.4 s / 1.0″ | 2.6 s / 1.2″ | 2.8 s / 1.2″ | 3.6629 / 3.6623 (3.66151) | 57.30 / 57.30 (57.31) |
| NGC 7000 | 1.3 s | 1.1 s | 8.4 s | 7.3 s | 3.6650 / 3.6636 | 295.58 / 295.58 |
| NGC 7000 stretched | 0.3 s / 1.4″ | 0.3 s / 1.4″ | 5.6 s / 0.5″ | 5.7 s / 0.5″ | 3.6669 / 3.6643 (3.66507) | 295.31 / 295.28 (295.29) |

All parities agree with the header (not flipped). The NGC 7000 stack has no WCS; its solved centre (314.81435°, +44.54132° from ASTAP; 314.81425°, +44.54139° from solve-field) is 11.6′ from the header's target RA/DEC. As an independent check, Siril's WCS of the stretched crop of the same stack, evaluated at the pixel that corresponds to the raw frame's centre, lands 0.7″ from ASTAP's and 0.6″ from solve-field's result. That needed Siril's crop offset (`HISTORY Crop (x=200, y=774, …)`) read with y counted from the top; the other reading is 36′ off. This check was a separate script, not part of the test. Earlier runs the same day gave the same centres; solve-field times vary by a few seconds with the machine's load.

**Centring with the real ASTAP** (`RealCenteringTest`, 1280 × 720 frames cut from the stack at its own 3.66″/px, goto error 12′ E and 7′ S, threshold 1′, sync accepted):

| Frame | Solves | Measured separation, first / second | True error before / after | ASTAP solve times | Blind failovers |
|---|---|---|---|---|---|
| IC 434 | 2 | 13.90′ / 0.02′ | 13.89′ / 0.01′ | 0.7 s, 0.2 s | 0 |
| M51 | 2 | 13.88′ / 0.02′ | 13.88′ / 0.02′ | 0.3 s, 0.2 s | 0 |
| M42 | 2 | 13.92′ / 0.02′ | 13.89′ / 0.03′ | 0.2 s, 0.2 s | 0 |
| NGC 7000 stretched | 2 | 13.86′ / 0.01′ | 13.88′ / 0.03′ | 0.7 s, 1.0 s (both failed) | 2 (solve-field; 42-64 s for the whole centring in two runs) |

The test first counted a blind failover as a failure. After the NGC 7000 result it reports failovers instead and still requires that centring converges within the threshold and that the first solve measures the goto error to 10″. On those crops ASTAP finds plenty of stars (554 above its detection level, the brightest 500 used) and searches D80 (282 database stars to magnitude 13.7 per 0.73° window) without a match. Run by hand on a 1280 × 720 crop centred on the target (the second solve's frame) with `-fov 0.7333 -r 5`: the stretched crop solves with `-s 50` (`-z 1` or `-z 2`) and with `-z 1 -s 200`, not with `-s 100` or `-s 1000`; the same crop of the linear (unstretched) stack solves with `-z 1` at every `-s` tried (50-1000) and with `-z 2` only at `-s 50`.

**Rig geometry** (`RigGeometry_PatchesResampledToTheNativeField`, 3 × 3 patch centres per frame on the four frames with a WCS, solve-field cpulimit 60 s, run 19:34-20:33 HKT). Each patch is this rig's native frame, 1920 × 1080 at 0.4785″/px (2500 mm, 2.9 µm × bin 2, ASTAP `-fov 0.14356`), bilinearly resampled from the stack around a patch centre whose sky position the header WCS gives; the hint is 8′ E and 5′ S of the truth, as after a goto. "Stars" counts local maxima above 5 robust σ in the same 15.3′ × 8.6′ at the stack's own resolution, independently of either solver.

| Variant | Solved | Median / max time | Where it solved |
|---|---|---|---|
| A: native frame, mono, ASTAP D80 `-z 2` | 1/36 | 0.7 / 22.8 s | M51 (7 stars), 1.3″ |
| B: native frame as an RGGB mosaic (a bin-2 OSC frame), ASTAP D80 `-z 2` | 3/36 | 0.3 / 27.0 s | M51 (4-7 stars), 0.6-1.3″ |
| C: the same 15.3′ × 8.6′ at the stack's 3.66″/px (251 × 141 px, no resampling), ASTAP D80 `-z 1` | 0/36 | 0.1 / 13.3 s | none |
| D: native frame, mono, solve-field near | 10/36 | 120 s (timeout) / 120 s | NGC 7000 8/9 (24-125 stars), IC 434 and M42 one each (6 and 14 stars), 0.2-2.1″, 2.3-11.2 s when solved |
| R: reducer frame, 1920 × 1080 at 0.748″/px (1600 mm, 0.224° high), mono, ASTAP D80 `-z 2` | 16/36 | 0.5 / 7.8 s | IC 434 5/9, M51 7/9, M42 4/9, NGC 7000 0/9; 0.4-4.0″ |

No reported solution was more than 15″ from where the header WCS puts the patch (the test fails otherwise). Star counts per patch: IC 434 2-8, M51 2-7, M42 0-14, NGC 7000 stretched 24-125.

What this construction can and cannot say:
- The stacks are 30 mm-aperture integrations at 3.66″/px. Resampled 7.8 × to 0.4785″/px, their stars become 15-20 px blobs with smooth, correlated noise, nothing like a real f/10 frame (seeing-limited stars of about 5 px). Their depth is also not this rig's (25 cm aperture, a 15 s solve frame, the ALP-T dual-band filter).
- Most native failures in the sparse fields are image-side: ASTAP aborts with "Not enough stars" ("Only 4 stars found in image" on the M51 centre patch) and never reaches the database. They say nothing about D80. Variant C, with no interpolation, fails the same way.
- D80 itself is not the limit. ASTAP solved native-geometry frames (4 solves on 3 M51 patches), and D80 has the stars a 0.14356° field needs (78 per 0.21° window for 66 image stars, down to magnitude 14.4-19.7 at these targets, see above). The dense NGC 7000 patches are a different case: ASTAP detects enough stars there (66 on the centre patch, against 78 database stars to magnitude 15.4 per window) and searches without a match. That is a stretched, resampled, dual-band (LP) image of a Milky Way emission field, so whether nebula knots, the stretch or the resampling is to blame was not separated.
- Decision for the first nights is unchanged from the plan: the reducer first, ASTAP `-z 2`, longer solve frames at high gain, and a sky test of the native field before relying on it.

## Known Windows-only behaviour still in compiled code

- **`Logger` header:** the RAM probe uses WMI. It throws and is caught, so the header reads "Unable to determine Physical Memory". It accounts for 4 of the 8 CA1416 hits in the inventory build. The other 4 are the WMI calls in `SerialPortProvider.GetComPortsForQuery`, which only the Windows branch of `GetPortNames` reaches (the analyzer does not follow the guard into the method). `NINA.Astrometry` has none.
- **`ProfileService.ActivateInstance*`:** these use named `EventWaitHandle`s, which throw `PlatformNotSupportedException` on macOS.
- **`Profile` file locks:** `FileShare.Read` maps to an advisory `LOCK_SH`, so two instances can open the same profile.
- **`DllLoader.DllVersion`:** rewrites `/` to `\`. Only the SBIG, Atik and About screens use it.
- **`CoreUtil.UserAgent`:** says "Win64".
- **File-name sanitising (fixed):** upstream `CoreUtil.ReplaceInvalidFilenameChars` follows the OS, so `:` `*` `?` `"` `<` `>` `|` would survive in image file names on macOS. The fork now splits on Windows' 41-character set (control characters, `"` `<` `>` `|` `:` `*` `?` `\` `/`) off Windows, a Windows no-op edit. `NINA.Mac.Siril` (`NinaTokenValues.PortableInvalidFileNameChars`, `SessionLayout.SanitizeFolderName`, `NinaFilePatterns.Expand`) uses the same set, so NINA's pattern paths and the Siril layout agree for such names.
- **Backslash file patterns (research SIR-01, fixed):** `ImagePatterns.GetImageFileString` splits a pattern into folders on `CoreUtil.PATHSEPARATORS`, upstream `{ Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }`: `\` and `/` on Windows, but `/` twice on macOS, so the default `$$DATEMINUS12$$\$$IMAGETYPE$$\...` pattern made one flat file name. The fork now sets `PATHSEPARATORS = new char[] { '\\', '/' }`, the same array on Windows. Still open for the image-saving work: macOS pattern defaults and `$$IMAGETYPEDIR$$`.
- **Local sidereal time above 24 h:** `AstroUtil.GetLocalSiderealTime` adds the longitude without wrapping, so at 114.18 E it returns 24 to 31.6 h whenever Greenwich sidereal time is past 16.4 h (about a third of the day). This is upstream behaviour, identical on Windows. The consumers checked tolerate it: hour angles feed sines and cosines (altitude, azimuth), `MeridianFlip.TimeToMeridian` reduces modulo 12 h and `TelescopeVM` wraps it. The sequencer's LST expression symbol (`SymbolBroker`) only wraps negative values, so at this site it can read above 24 h.
- **`NINA.Test/Usings.cs`** logs "The environment is x64: True" (it prints `Is64BitProcess`) and SOFA/NOVAS DLL paths it never loads. This is cosmetic.

## Build warnings

Normal build, unique warnings:

| Project | Warnings |
|---|---|
| `NINA.Mac.WpfCompat` | 0 |
| `NINA.Core.Mac` | 11: CS0618 ×10 (`[Obsolete]` `SerializableINPC`, `AsyncCommand`, `IAsyncCommand`) and SYSLIB0050 ×1 (`Blittable.cs`) |
| `NINA.Profile.Mac` | 14: CS0618 ×8 and CS0612 ×6 (obsolete flat-device filter settings) |
| `NINA.Astrometry.Mac` | 4: CS0618 ×3 (`MoonInfo` calls the obsolete `GetMoonPhase`/`GetMoonPositionAngle`, `SkyObjectBase.Rotation`) and CS0672 ×1 (`CustomRiseAndSet.Calculate`) |
| `NINA.Mac.Astrometry.Test` | Upstream fixtures only: nullable warnings (CS86xx) and `[Obsolete]` (CS0612/CS0618), as `NINA.Test` produces with `Nullable` enabled. The mac test files have none |
| `Accord.Imaging.Mac` | 0 |
| `NINA.Image.Mac` | 0 |
| `NINA.Mac.Image.Test` | 13, all in linked upstream fixtures: CS8618 ×6, CS8600, CS8602, CS8625 (nullable) and CS0612 ×4 (`ImageDataTest` calls the obsolete `PrepareSave`/`FinalizeSave`). The mac test files have none |
| `NINA.Equipment.Mac` | 5: CS0169 ×2 (`PhdEventGuideStep`'s unused `raDistanceDisplay`/`decDistanceDisplay`) and CS0618 ×3 (`IImageControlVM` exposes the obsolete `IAsyncCommand`/`AsyncCommand<bool>`). The `Mac/` files have none |
| `NINA.Mac.Equipment.Test` | 4, all CS8604 (nullable) in the linked upstream `EquipmentInfoBehaviorTest`. The mac test files have none |
| `NINA.Platesolving.Mac` | 0 (CA1416 inventory: 2, `Dc3PinPointSolver.cs:78,174`) |
| `NINA.Mac.Platesolving.Test` | 26, all in linked upstream fixtures: CS8618 ×19, CS8600 ×3, CS8602, CS8625 (nullable) and CS0618 ×2 (`PlateSolveModelBehaviorTest` reads the obsolete `PlateSolveResult.Orientation`). The mac test files have none |

All of them come from unchanged upstream source.
