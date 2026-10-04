# Headless engine on macOS (M3)

The upstream engine libraries build for `net10.0` / `osx-arm64` without being copied or forked. Each mac project compiles the upstream `.cs` files by link. A small compatibility assembly supplies the WPF types that leak into engine code.

| Project | Assembly | What it is |
|---|---|---|
| `NINA.Mac.WpfCompat` | `NINA.Mac.WpfCompat` | Stand-ins for the WPF types that engine code uses, in their WPF namespaces |
| `NINA.Core.Mac` | `NINA.Core` | Upstream `NINA.Core`, minus pure UI, plus `MacReplacements/` |
| `NINA.Profile.Mac` | `NINA.Profile` | Upstream `NINA.Profile`, unchanged |
| `NINA.Astrometry.Mac` | `NINA.Astrometry` | Upstream `NINA.Astrometry` minus its XAML converters, plus `Mac/NativeRegistration.cs`. Carries the SOFA/NOVAS dylibs, `External/JPLEPH` and the catalogue SQL scripts into every output that references it |
| `../tests/NINA.Mac.Engine.Test` | | Smoke tests for Core and Profile, plus a WPF oracle for the compat types |
| `../tests/NINA.Mac.Astrometry.Test` | | SOFA/NOVAS native smoke tests, 50 upstream `NINA.Test` fixtures linked unchanged, and mac tests for the resolver, J2000 to JNow, sidereal time and the deep-sky database |

```bash
mac/dotnet build mac/src/NINA.Astrometry.Mac/NINA.Astrometry.Mac.csproj      # also builds WpfCompat, Core, Profile and Native
mac/dotnet test mac/tests/NINA.Mac.Engine.Test/NINA.Mac.Engine.Test.csproj
mac/dotnet test mac/tests/NINA.Mac.Astrometry.Test/NINA.Mac.Astrometry.Test.csproj
mac/dotnet build mac/src/NINA.Core.Mac/NINA.Core.Mac.csproj --no-incremental -p:MacPlatformInventory=true   # CA1416 inventory
```

`NINA.Astrometry.Mac` needs the natives from `mac/scripts/build-astrometry-natives.sh` and the DE421 `JPLEPH` in `mac/native/ephemeris/` (see `NINA.Mac.Native`). None of these projects is in `NINA.Mac.slnx` yet; build and test them by csproj path.

## How a project is put together

- **`Engine.props`** holds the shared settings and is imported by every engine csproj. It turns off `GenerateAssemblyInfo` (upstream supplies the attributes), allows unsafe code, keeps the WPF targets out, and links `CommonAssemblyInfo.cs`. That last item stamps the assembly with the upstream version, 3.3.0.x.
- **Sources** are linked with `<Compile Include="$(UpstreamDir)**/*.cs" LinkBase="Upstream" />`. The include skips upstream `obj/`, `bin/` and `publish/`. `Compile Remove` lists are grouped by reason.
- **Windows-1252 sources:** hundreds of upstream `.cs` files are Windows-1252, not UTF-8. The C# compiler reads a file without a BOM as UTF-8 and falls back to the OS ANSI code page when that fails. That is Windows-1252 on the Windows machines that build NINA, but on macOS it is UTF-8 with U+FFFD replacement, so a non-ASCII literal would compile differently. One such literal is the degree sign in `FocusTarget.Information`; more exist in Equipment, for example a regex in `CartesDuCiel.cs`. `Engine.props` therefore runs a step before `CoreCompile`: each `Compile` item that is neither valid UTF-8 nor BOM-marked is decoded as Windows-1252 and compiled from a UTF-8 copy in `obj/LegacyEncoding/`. A leading `#line` directive keeps diagnostics and debugging on the upstream file. Copies are rewritten only when their content changes. `LegacyEncodingTest` checks that no string literal in `NINA.Core`, `NINA.Profile` or `NINA.Astrometry` contains U+FFFD.
- **Packages** are the upstream package references at the same versions. There are two differences, both explained in the csproj:
  - `System.Configuration.ConfigurationManager` is added. On Windows it comes from the WindowsDesktop framework.
  - `Grpc.Tools` is 2.84.0, not 2.83.0. The 2.83.0 protoc for macOS is x86_64 only, so it runs only under Rosetta. 2.84.0 ships a universal protoc with the same libprotoc 35.1. The generated `CameraService*.cs` is byte-identical.
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
| Assembly attributes | `Properties/AssemblyInfo.cs` | WPF `ThemeInfo`; `Properties/AssemblyInfo.Mac.cs` carries the rest |

`NINA.Profile` compiles unchanged and needs no exclusions.

## NINA.Astrometry

- **Exclusions:** only `Converters/**` (6 XAML value converters). Everything else, including `Properties/AssemblyInfo.cs`, compiles unchanged. The WPF types it names are `Point` and `Vector` (sky projections), `Media3D.Vector3D` (`AstroUtil.Polar3DToCartesian`), `Media.Color` (`MoonInfo`) and `Media.Imaging.BitmapSource` (the optional sky-survey preview of a deep-sky object).
- **Natives:** `Mac/NativeRegistration.cs` is compiled into the assembly. Its `[ModuleInitializer]` calls `NativeLibraries.Register` for `NINA.Astrometry` before any of its code runs, so `SOFA_2023_10_11.dll` and `NOVAS31lib.dll` resolve to `libsofa.dylib` and `libnovas31.dylib` in the app folder (or `Contents/Frameworks`). No host call is needed. Upstream's `DllLoader.LoadDll` in the `SOFA`/`NOVAS` static constructors is a no-op off Windows. `NOVAS` opens `<BaseDirectory>/External/JPLEPH` itself; `NINA.Mac.Native` puts it there.
- **Catalogue database:** `NINADbContext` (NINA.Core) creates a new database from `<BaseDirectory>/Database/Initial/*.sql` and then applies `Database/Migration/<n>.sql` in numeric order (1 to 16, there is no 4). The project copies upstream's `NINA/Database/**/*.sql` (6.4 MB) to the output, as `NINA.csproj` does on Windows. EF6 6.5.1 and System.Data.SQLite 2.0.3 run unchanged on SourceGear's osx-arm64 `libe_sqlite3.dylib` (SQLite 3.53). Every upstream script applies cleanly, and NINA's build of the database matches applying the scripts directly, table by table.
- **Database location:** `new DatabaseInteraction()` used `Environment.ExpandEnvironmentVariables(@"%localappdata%\NINA\NINA.sqlite")`. On macOS that stays a literal relative file name, so a database called `%localappdata%\NINA\NINA.sqlite` was created in the working directory (`/` for a Finder-launched app). Off Windows it is now `Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NINA.sqlite")`, which is `~/Library/Application Support/NINA/NINA.sqlite` unless the host moves `APPLICATIONTEMPPATH` (upstream edit below). The folder must exist; `Logger` creates it on first use, as on Windows. The unused `DatabaseLocation` app setting in `NINA/Properties/Settings.Designer.cs` is not involved.
- **.app bundle (not done yet):** `BaseDirectory` is `Contents/MacOS` in a bundle, so `External/JPLEPH` and `Database/` must sit there or be linked there. Resolving them from `Contents/Resources` needs an upstream hook or a symlink; check codesign and notarization when packaging.

## Mac replacements (`NINA.Core.Mac/MacReplacements`)

Each replacement has the same public API as the file it replaces, plus the mac-only hooks noted below.

- **`Notification`**: every `Show*` raises the mac-only event `Notification.Posted` (`Kind`, `Header`, `Message`, `Lifetime`), with the header and lifetime upstream would use. With no subscriber the call is a no-op, which is what upstream does when no WPF `Application` exists.
- **`MyMessageBox`**: `Show` asks the mac-only `MyMessageBox.Host` (an `IMyMessageBoxVM`). With no host it throws `PlatformNotSupportedException`. Guessing an answer would silently change what equipment and sequencer code does.
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
| `Threading.Dispatcher`, `DispatcherOperation`, `DispatcherSynchronizationContext`, `DispatcherObject`, `DispatcherPriority`, `DispatcherOperationStatus` | Per-thread dispatcher (`CurrentDispatcher`, `CheckAccess`). If the creating thread had a `SynchronizationContext` (a UI loop), cross-thread `Invoke` uses Send and `BeginInvoke` uses Post. Otherwise work runs inline on the caller under the dispatcher's lock, so items never overlap. Priorities are not reordered; `Inactive` throws. A failing `BeginInvoke` faults the operation's `Task`, where WPF raises `UnhandledException` |
| `Application` | As in WPF, `Current` is null until the host constructs one, and only one can exist. `Resources` is a keyed store. Window and lifetime API is absent |
| `ResourceDictionary` | Hashtable-backed `IDictionary`; a missing key returns null |
| `Input.CommandManager` | WPF requery semantics: weak handlers, per-thread, coalesced, raised through the thread's dispatcher at Background priority. There is no input-driven requery |
| `Markup.MarkupExtension` | Abstract base, as in System.Xaml. It keeps `EnumDescriptionTypeConverter`, which lives in `EnumBindingSourceExtension.cs`, compiling |
| `Window`, `Data.Binding`, `Microsoft.Win32.OpenFileDialog` | Signature-only placeholders; their constructors throw `PlatformNotSupportedException` |
| `MessageBoxResult`, `MessageBoxButton`, `ResizeMode`, `WindowStyle`, `Data.BindingMode` | Enums with WPF's members and values |

## Contract for a host (app head or test runner)

1. Set `CoreUtil.APPLICATIONTEMPPATH` before anything touches `Logger`, `ProfileService` or `new DatabaseInteraction()`, if the data folder should move. The default is `~/Library/Application Support/NINA`. Logs, profiles and the catalogue database `NINA.sqlite` live there.
2. Create one `new System.Windows.Application()` at startup, on the UI thread if there is one. `ProfileService` raises its events through `Application.Current.Dispatcher`. Without an `Application` those calls throw `NullReferenceException`, on Windows as well.
3. Subscribe to `Notification.Posted`. Install `MyMessageBox.Host`, or accept that prompts throw.
4. Ship `libsofa.dylib`, `libnovas31.dylib`, `External/JPLEPH` and `Database/` next to `NINA.Astrometry.dll`. Referencing `NINA.Astrometry.Mac` copies all of them. SOFA/NOVAS resolution needs no host call.

## Upstream edits (Windows no-ops)

| File | Change |
|---|---|
| `NINA.Core/Utility/DllLoader.cs` | `LoadDllFromAbsolutePath` returns early with a debug log when `!OperatingSystem.IsWindows()`. It is reached by `LoadDll`, so this covers every `DllLoader.LoadDll` call in Astrometry, Equipment and Image (NINA.MGEN has its own `DllLoader`). macOS loads native libraries through `NativeLibrary` resolvers |
| `NINA.Core/Utility/SerialCommunication/SerialPortProvider.cs` | Off Windows, the constructor skips the WMI scan. `GetPortNames` lists `SerialPort.GetPortNames()`, filtered to `/dev/cu.*` on macOS, behind the usual divider. `GetComPortsForQuery` is marked `[SupportedOSPlatform("windows")]` |
| `NINA.Astrometry/DatabaseInteraction.cs` | The parameterless constructor gets its path from a new private `DefaultDatabaseLocation()`. On Windows that returns the same `Environment.ExpandEnvironmentVariables(@"%localappdata%\NINA\NINA.sqlite")` as before. Off Windows it returns `Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NINA.sqlite")`. The file stays ISO-8859-1 with LF line endings |

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

Results: 1842 tests, 1836 passed, 6 failed. Of these, 55 are mac tests, all passing, and 1787 are upstream cases, of which 1781 pass. The six failures are environment differences, not port bugs. Their expected values are left as upstream wrote them.

- **`CoordinatesTest.ShiftStereographic_CoordinatesTest` (5 cases)** are the cases with Dec 80, 10° offsets and rotation 0/90/180/270/360. RA is off by 1.39e-11 to 1.52e-11 degrees, and the test tolerance is 1e-12. This is managed code, not SOFA or NOVAS.
  - Replaying `Coordinates.ShiftStenographic` with correctly rounded `sin`/`cos`/`asin` (60-digit arithmetic) gives the macOS result bit for bit.
  - Upstream's expected values are reproduced exactly when one call, `asin(0.9851114915427427)` (the target declination), returns 1 ulp below the correctly rounded value. Windows' C runtime evidently does that.
  - The next `asin`, whose argument is near 1 (RA offset about 90°), magnifies that ulp (2.2e-16 rad) about 1,100 times, to 2.4e-13 rad. That is 5e-8 arcseconds. The declination itself moves by only 1.3e-14°, within tolerance.
- **`ImagePatternsTest.GetImageFileString_PathSegmentsAndImageTypeOverride_ReturnsSafePath`** expects `M31:Core` to become `M31_Core`. `CoreUtil.ReplaceInvalidFilenameChars` replaces `Path.GetInvalidFileNameChars()`, which is only `\0` and `/` on macOS, so `:` stays.
  - Such names are legal on APFS (Finder shows `:` as `/`), but they break on exFAT/FAT drives and SMB shares.
  - The fix would be a Windows-no-op upstream edit that replaces Windows' invalid set on every OS. It is not made yet because it changes file names that the Siril track models.

### Cross-checks added for M3

- **J2000 to JNow:** `Coordinates.Transform(Epoch.JNOW)` uses SOFA. It agrees with NOVAS `place()` (equinox of date, full accuracy, DE421) to better than 0.01 mas for M42, Polaris, Spica, Vega and RA 0/Dec 0, at the moment the test runs.
- **Sidereal time:** local sidereal time at 114.18 E agrees with Meeus's GMST plus the longitude to within 0.54 to 0.70 s. The bound is the equation of the equinoxes plus UT1-UTC. A UTC instant and the same instant as local time give the same value.
- **M42:** a database built from the upstream scripts in a temp folder finds "M42" as NGC1976 "M 42" at RA 83.82208333°, Dec -5.39111111° (05h35m17.3s, -05°23'28"), ORI, CL+NB, mag 4.0, with `LBN 974` from migration 7.

## Known Windows-only behaviour still in compiled code

- **`Logger` header:** the RAM probe uses WMI. It throws and is caught, so the header reads "Unable to determine Physical Memory". This accounts for the only 4 CA1416 hits left. `NINA.Astrometry` has none.
- **`ProfileService.ActivateInstance*`:** these use named `EventWaitHandle`s, which throw `PlatformNotSupportedException` on macOS.
- **`Profile` file locks:** `FileShare.Read` maps to an advisory `LOCK_SH`, so two instances can open the same profile.
- **`DllLoader.DllVersion`:** rewrites `/` to `\`. Only the SBIG, Atik and About screens use it.
- **`CoreUtil.UserAgent`:** says "Win64".
- **File-name sanitising:** `CoreUtil.ReplaceInvalidFilenameChars` follows the OS, so `:` `*` `?` `"` `<` `>` `|` survive in image file names on macOS (see the `ImagePatternsTest` failure above).
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
