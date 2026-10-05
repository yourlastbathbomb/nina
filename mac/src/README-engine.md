# Headless engine on macOS (M3)

The upstream engine libraries build for `net10.0` / `osx-arm64` without being copied or forked. Each mac project compiles the upstream `.cs` files by link. A small compatibility assembly supplies the WPF types that leak into engine code.

| Project | Assembly | What it is |
|---|---|---|
| `NINA.Mac.WpfCompat` | `NINA.Mac.WpfCompat` | Stand-ins for the WPF types that engine code uses, in their WPF namespaces |
| `NINA.Core.Mac` | `NINA.Core` | Upstream `NINA.Core`, minus pure UI, plus `MacReplacements/` |
| `NINA.Profile.Mac` | `NINA.Profile` | Upstream `NINA.Profile`, unchanged |
| `NINA.Astrometry.Mac` | `NINA.Astrometry` | Upstream `NINA.Astrometry` minus its XAML converters, plus `Mac/NativeRegistration.cs`. Carries the SOFA/NOVAS dylibs, `External/JPLEPH` and the catalogue SQL scripts into every output that references it |
| `../tests/NINA.Mac.Engine.Test` | | Smoke tests for Core and Profile, a WPF oracle for the compat types, and merge guards against upstream drift (`UpstreamParityTest`) |
| `../tests/NINA.Mac.Astrometry.Test` | | SOFA/NOVAS native smoke tests, 50 upstream `NINA.Test` files (63 fixtures) linked unchanged, and mac tests for the resolver, J2000 to JNow, sidereal time, the data folder, the deep-sky database and the startup data check |

```bash
mac/dotnet build mac/src/NINA.Astrometry.Mac/NINA.Astrometry.Mac.csproj      # also builds WpfCompat, Core, Profile and Native
mac/dotnet test mac/tests/NINA.Mac.Engine.Test/NINA.Mac.Engine.Test.csproj
mac/dotnet test mac/tests/NINA.Mac.Astrometry.Test/NINA.Mac.Astrometry.Test.csproj
mac/dotnet build mac/src/NINA.Core.Mac/NINA.Core.Mac.csproj --no-incremental -p:MacPlatformInventory=true   # CA1416 inventory
```

`NINA.Astrometry.Mac` needs the natives from `mac/scripts/build-astrometry-natives.sh` and the DE421 `JPLEPH` in `mac/native/ephemeris/` (see `NINA.Mac.Native`). All of these projects are in `NINA.Mac.slnx`; the commands above build and test them on their own, by csproj path.

## How a project is put together

- **`Engine.props`** holds the shared settings and is imported by every engine csproj. It turns off `GenerateAssemblyInfo` (upstream supplies the attributes), allows unsafe code, keeps the WPF targets out, and links `CommonAssemblyInfo.cs`. That last item stamps the assembly with the upstream version, 3.3.0.x.
- **Sources** are linked with `<Compile Include="$(UpstreamDir)**/*.cs" LinkBase="Upstream" />`. The include skips upstream `obj/`, `bin/` and `publish/`. `Compile Remove` lists are grouped by reason.
- **Windows-1252 sources:** hundreds of upstream `.cs` files are Windows-1252, not UTF-8. The C# compiler reads a file without a BOM as UTF-8 and falls back to the OS ANSI code page when that fails. That is Windows-1252 on the Windows machines that build NINA, but on macOS it is UTF-8 with U+FFFD replacement, so a non-ASCII literal would compile differently. One such literal is the degree sign in `FocusTarget.Information`; more exist in Equipment, for example a regex in `CartesDuCiel.cs`. `Engine.props` therefore runs a step before `CoreCompile`: each `Compile` item that is neither valid UTF-8 nor BOM-marked is decoded as Windows-1252 and compiled from a UTF-8 copy in `obj/LegacyEncoding/`. A leading `#line` directive keeps diagnostics and debugging on the upstream file. Copies are rewritten only when their content changes. `LegacyEncodingTest` checks that no string literal in `NINA.Core`, `NINA.Profile` or `NINA.Astrometry` contains U+FFFD.
- **Packages** are the upstream package references at the same versions. There are two differences, both explained in the csproj:
  - `System.Configuration.ConfigurationManager` is added. On Windows it comes from the WindowsDesktop framework.
  - `Grpc.Tools` is 2.84.0, not 2.83.0. The 2.83.0 protoc for macOS is x86_64 only, so it runs only under Rosetta. 2.84.0 ships a universal protoc with the same libprotoc 35.1. The generated `CameraService*.cs` is byte-identical.
- **Merge guards** (`tests/NINA.Mac.Engine.Test/UpstreamParityTest.cs`): the mac csproj files restate upstream's package versions and resource exclusions, so a merge that only bumps upstream csproj files would otherwise build and pass while the mac engine stays behind. For every mac csproj that declares an `UpstreamDir`, the test compares it with the upstream csproj: every `PackageReference` and version, the embedded `.resx` files, the `.proto` files, upstream `Compile Remove` items, and sources upstream links from outside its folder. The two package differences above are pinned to upstream's current versions with their reasons; if upstream moves, the test asks for a re-check. It also pins a hash of each upstream file that `MacReplacements/` re-implements (copyright header excluded), and fails with "re-sync `MacReplacements/<file>`" when upstream changes one. New engine projects (M3b) are picked up automatically.
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
| `Media.Imaging.BitmapSource` | Abstract placeholder with `Freeze()` (a no-op; WPF declares it on `Freezable`). Nothing can create one on macOS; `NINA.Astrometry` only uses it to type the optional sky-survey preview, whose factory `DatabaseInteraction` passes as null. Pixel access arrives with `NINA.Image` |
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

## Upstream edits (Windows no-ops)

| File | Change |
|---|---|
| `NINA.Core/Utility/DllLoader.cs` | `LoadDllFromAbsolutePath` returns early with a debug log when `!OperatingSystem.IsWindows()`. It is reached by `LoadDll`, so this covers every `DllLoader.LoadDll` call in Astrometry, Equipment and Image (NINA.MGEN has its own `DllLoader`). macOS loads native libraries through `NativeLibrary` resolvers |
| `NINA.Core/Utility/SerialCommunication/SerialPortProvider.cs` | Off Windows, the constructor skips the WMI scan, which would throw `PlatformNotSupportedException`. `GetPortNames` lists `SerialPort.GetPortNames()`, filtered to `/dev/cu.*` on macOS, behind the usual divider. Both are `if (!OperatingSystem.IsWindows())` branches, so Windows runs the original code. This is generic portability, not something the rig needs today: only `SerialSdk` constructs the class, for the Alnitak, Artesky and Pegasus FlatMaster flat panels. The rig's LX200 link (`NINA.Mac.Lx200`) opens `System.IO.Ports` itself and does not use NINA.Core's serial classes. `CoreSmokeTest` covers the mac branch. The file stays ISO-8859-1 with LF line endings |
| `NINA.Astrometry/DatabaseInteraction.cs` | The parameterless constructor gets its path from a new private `DefaultDatabaseLocation()`. On Windows that returns the same `Environment.ExpandEnvironmentVariables(@"%localappdata%\NINA\NINA.sqlite")` as before. Off Windows it returns `Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NINA.sqlite")`. The file stays ISO-8859-1 with LF line endings |
| `NINA.Equipment/SDK/CameraSDKs/ASISDK/ASICameraDll.cs` | ZWO's header declares some values as C `long`: 8 bytes on macOS arm64, 4 on Windows. New private structs `ASI_CAMERA_INFO_NATIVE` and `ASI_CONTROL_CAPS_NATIVE` mirror the public ones with `CLong` for `MaxHeight`/`MaxWidth` and `MaxValue`/`MinValue`/`DefaultValue`. `ASIGetCameraProperty`, `ASIGetCameraPropertyByID` and `ASIGetControlCaps` fill a mirror, and the wrappers copy it into the unchanged public struct. The private externs `ASISetControlValue`/`ASIGetControlValue` (value) and `ASIGetVideoData`/`ASIGetDataAfterExp` (buffer size) take `CLong`. On Windows `CLong` is 4 bytes, so the native layout and calls are unchanged, and the public structs keep their `int` fields, so plugins compiled against NINA.Equipment keep working. Nothing else changes: the static constructor still calls `DllLoader.LoadDll`, which is a no-op off Windows (above). The probe's `ASIGetGainOffset`/`ASIGetLMHGainOffset` calls live in `NINA.Mac.ZwoProbe/GainPresets.cs`. `tests/NINA.Mac.Test/AsiAbiTest.cs` checks the mirrors against the arm64 header layout, field for field against the public structs, and that the public fields stay `int` fields. The file stays ASCII with LF line endings |

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
- `Utility/BlittableTest`, `ImageUtilityTest`, `CommandLineOptionsTest`, `MvvmLightCommandTest` and `PluggableIntegrationBehaviorTest` need Equipment, Image, the app or WPF.Base.
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

All of them come from unchanged upstream source.
