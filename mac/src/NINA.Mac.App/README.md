# NINA.Mac.App: native macOS app shell (M8/M9 groundwork)

Avalonia **12.1.3** (newest stable on NuGet, 2026-10) + CommunityToolkit.Mvvm 8.4.2. The app is called **Nightglass**: one MSBuild property, `AppDisplayName`, from which the short name, executable, data folders and bundle id (`local.nightglass.mac`) are derived. The build fails if the display name, short name, executable name or bundle id contains "NINA" once spaces and punctuation are removed (MPL grants no trademark rights). The About box credits "based on N.I.N.A." under MPL-2.0. The devices sit behind small service interfaces (`NINA.Mac.App.Engine/Contracts`): **simulated** by default, or **Real** (Settings › Devices, next start): NINA's own engine through `NINA.Mac.App.Engine` (see its README).

## What is here

| Path | What |
|---|---|
| `ViewModels/` | Shell (`MainWindowViewModel`: navigation, status bar, connection-lost banner, night vision) and the eight screens of the night: **Connect, Cool, Focus, Target, Run, Calibrate, Teardown, Settings**, plus About |
| `Views/` | One AXAML view per screen, `MainWindow`, `AboutWindow`. Compiled bindings throughout |
| `Services/` | Keep-awake and power coordinators, the camera warm-up ramp. The service contracts, the settings (JSON in Application Support) and the simulators' session layout live in `NINA.Mac.App.Engine/Contracts` (same namespace) |
| `AppServices.cs` | Composition root: the simulators or, with `DeviceSource = Real`, `EngineDevices` (falls back to the simulators with the reason if the engine cannot start) |
| `Services/Simulation/` | ASI585MC (TEC model, exposures), LX200GPS alt-az (slew guard, soft park, dither), #1209 timed focuser (virtual ms position, speed 1-4), session runner, slew-only centring, calibration (also used with the Real camera) |
| `Astro/` | Sidereal time, alt/az, field-rotation max sub (reproduces the plan's §6 table), Sun/dawn, the Moon (low precision: illumination, altitude), astronomical night, built-in target list |
| `Controls/` | `Sparkline` (temperature, HFR) and `SkyChart` (Target screen: horizon, keyhole, minimum altitude, the target's path to dawn, the mount) |
| `Theming/` | Dark theme and red night vision (`ThemeManager`) |
| `Diagnostics/` | `--preflight` (`Preflight.cs`), `--stack-night` (`StackNight.cs`), `--smoke-test`, `--startup-check` (real Avalonia.Native setup, run by the smoke test in a child process), `--gui-smoke` (a real launch with the window on screen for a few seconds) and the screen renderer used by tests |

These choices are specific to this rig:
- **No meridian flip and no hard park.** Soft park points the scope low in the south. The slew guard refuses targets below the horizon or above the 75° keyhole.
- **Timed focuser.** In/out nudges in ms, speed 1-4 (changing it recentres the virtual position), "recentre virtual position". Autofocus is hidden.
- **Target screen.** Shows the field-rotation sub limit, the blocked northern wedge, the local horizon and the keyhole, as text and in the sky view. It also holds the horizon editor (below).
- **Connect screen.** **Connect all** connects the camera and the mount side by side and starts cooling as soon as the camera is up; a mount that fails or hangs is reported on its own line and never stops the cooling. The optical train (native f/10 or the f/6.3 reducer: the focal length the solver, the FITS headers and the pixel scale use) and the Preflight card.
- **Run screen.** Stops at max altitude, min altitude and astronomical dawn. Frames go to NINA.Mac.Siril's layout: `~/Astro/<app>/<night>/<target>/lights/`; the night's `flats/` and `biases/`; darks in `library/darks/`; snapshots in `<target>/snapshots/`, out of lights. The simulators report the same paths. Under the plan it shows the plan check: with Real devices, NINA.Mac.Sequencing's PlanValidator (tonight's window, "Holds the sequence until dawn", "Never reached").
- **Laptop at the scope.** While any device is connected, `NINA.Mac.Platform` keeps idle sleep, display sleep and App Nap away. The status bar shows battery and keep-awake state.

## Preflight

`Nightglass --preflight [--with-devices]`, the Connect screen's **Run preflight**, and (without devices) the smoke test run `Diagnostics/Preflight.cs`. It finishes in well under a second, opens no device unless asked, and prints one `PASS`/`INFO`/`WARN`/`FAIL` line per check, each WARN and FAIL with its fix. The exit code is 1 when anything FAILs. It checks:

- **Bundle:** the engine assemblies load; `External/JPLEPH` and the catalogue scripts are present, and NOVAS reads the ephemeris when the engine runtime is set up; the ZWO, SOFA and NOVAS dylibs load; the ZWO SDK reports its version. Only `--with-devices` asks the SDK how many cameras are on USB, and never while the app has the camera connected.
- **NINA database:** readable, or buildable in the engine data folder.
- **Serial port:** `/dev/cu.usbserial-*`, against the port the rig profile names (default `/dev/cu.usbserial-DU0D8VUG`).
- **Settings:** `settings.json` reads (FAIL otherwise: the app is on defaults, so the Device source line says the cause is the file). The CLI reads it without keeping a `.bad` copy; the Connect screen also says so while it runs on defaults.
- **Solvers:** ASTAP's `astap_cli` (the fix names a downloaded zip in `~/Astro/astap/dl`): FAIL when it still carries `com.apple.quarantine` (macOS kills it at every solve; the fix is the `xattr -d` command), otherwise it is started once with `-h` (about 30 ms, 5 s timeout) and must exit 0 or print ASTAP's banner; and its D80 files; `solve-field`; the 4200-series index tiles the selected optical train needs (quads 30-100 % of the field width: 4202-4205 native, 4203-4207 with the reducer), naming each missing tile.
- **Siril:** `siril-cli`, and the GUI's `binning_update` preference ("Update pixel size of binned images" must be unticked).
- **Storage:** the images folder is writable and not under iCloud; free disk against the night's need (a bin-2 frame is 4.2 MB, so 10 s subs take about 1.5 GB/h, over the dark hours plus 1.5 h), WARN when what is left could not stack even one hour of them; **Stacking space**: Siril's 32-bit RGB `pp_` and `r_` frames take 49.8 MB per light (about 12x the lights, ~18 GB per hour of lights) and stay in each target's `process/`, so the line says how many hours of the night can be stacked.
- **Battery:** FAIL below 30 % unless charging; WARN below 90 %.
- **Site:** within 1′ of 22.25 N 114.18 E, and the Mac's time zone matches the site's UTC offset.
- **Horizon:** "estimate - measure it" until a measured one is saved.
- **Tonight:** astronomical dusk and dawn; the Moon's illumination and when it is up.
- **Optical train:** focal length, pixel scale, field. Native f/10's 0.1436° field height is below ASTAP D80's 0.15° minimum (plan risk 4), so preflight recommends the reducer.
- **Device source:** Simulated is a WARN on a night.

The optical train stays **native by default**. The settings model has no notion of what is actually fitted, so a reducer default would silently give the solver the wrong scale whenever it is not. Connect shows both trains with their scale and field, and preflight and Settings warn about native f/10.

## Horizon

`HorizonProfile` (`NINA.Mac.App.Engine/Contracts/Horizon.cs`) is the site's local horizon:

- **Where it is:** `~/Library/Application Support/Nightglass/horizon.hrz`, in NINA's plain format (one "azimuth altitude" pair per line, comments on their own lines, explicit 0° and 360° points). It is read with the engine's reader (`NINA.Mac.Sequencing.Horizon.HorizonFile`: inline comments accepted, NINA's grooming).
- **Without a file:** the built-in **estimate** is used (south open to 15°, north blocked at 80° from azimuth 280° through north to 80°). It is marked with `# nightglass: estimate`, and preflight warns until a measured horizon is saved. Nothing is written at start-up.
- **Who uses it:**
  - the slew guard (`SlewGuard`, both mounts);
  - the Target screen's planner warnings and sky view;
  - with Real devices, the rig profile: `RigProfile.ApplyHorizon` writes `<engine data>/horizon.hrz` and loads it as NINA's horizon. WaitUntilAboveHorizon, AboveHorizonCondition and the plan check therefore use the same horizon. During a run the new horizon applies when the run ends.
- **Editor (Target › Horizon):**
  - a table of points;
  - **Record point from mount** (the mount's current alt/az);
  - Add point, Save, Revert;
  - **Import .hrz…** and **Export .hrz…**;
  - **Built-in estimate**;
  - the "Estimate, not measured" tick.

## After the night

`Nightglass --stack-night [YYYY-MM-DD] [--dry-run]` runs NINA.Mac.Siril's night processor (`SirilNightProcessor`). It builds missing master darks, then for each target calibrates, debayers, registers on the middle frame and stacks with siril-cli, using the app's own ini in `~/Library/Application Support/Nightglass/siril/`. Without a date it takes the newest night that has lights. The Teardown screen shows the command for tonight.

## Build, test, run

```bash
mac/dotnet build mac/src/NINA.Mac.App/NINA.Mac.App.csproj        # 0 warnings (TreatWarningsAsErrors)
mac/dotnet test  mac/tests/NINA.Mac.App.Test/NINA.Mac.App.Test.csproj
mac/dotnet test  mac/tests/NINA.Mac.App.Engine.Test/NINA.Mac.App.Engine.Test.csproj   # Real services vs the LX200 simulator and fakes, ~1 min
mac/packaging/package-app.sh                                       # .app in mac/artifacts/, see ../../packaging/README.md
```

- **Smoke test.** Run `mac/artifacts/Nightglass.app/Contents/MacOS/Nightglass --smoke-test [--screenshots DIR]`. It initialises the real platform services, composes the Real device services in a temporary data folder without opening anything (engine assemblies, NINA's ephemeris and catalogue data through the bundle's links, the ZWO/SOFA/NOVAS dylibs, a generated NINA sequence), runs the preflight without devices (its lines are printed; only the bundle checks fail the smoke test), renders every screen headlessly (Avalonia.Headless + Skia), runs a simulated night and exits 0 on success. Each page must show its own view with content in the page area, and night vision must have no white, grey, green or blue pixels. No window appears. `--version` prints the version.
- **Startup check.** `--startup-check` builds the app exactly as a Finder launch does (`Program.BuildAvaloniaApp()`: Avalonia.Native + Skia + HarfBuzz, Dock icon off), measures text and lays out the main window without showing it. The smoke test runs it in a child process because Avalonia allows one platform per process. Without `UseHarfBuzz()` every real launch threw "No text shaping system configured"; the headless platform hid this because it registers HarfBuzz itself. While every display is asleep it prints `partial:` and checks only the builder's configuration, because Avalonia.Native cannot start then (see below).
- **GUI smoke test.** `--gui-smoke [--screenshots DIR]` is a real launch: `Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime()`, the real `App` and services, the main window shown through Avalonia.Native (without taking focus). Once it opens, it checks that the window is an `NSWindow`, waits for the native compositor to render frames, draws every page with the real renderer (`RenderTargetBitmap`), checks the app menu, then quits by itself: exit 0 = pass, about 3 s. It always runs on the simulators, whatever Settings › Devices says, so a window on screen never opens the camera or the serial port. A 45 s watchdog ends it otherwise. `mac/packaging/package-app.sh` runs it on the packaged app; `GuiSmokeFlag_*` in the tests is `[Explicit]` (`--filter "TestCategory=Gui"` or by name).
- **Display asleep.** Avalonia.Native starts its render timer with `CVDisplayLinkCreateWithActiveCGDisplays`, so with every display asleep (or the lid closed without an external display) `AppBuilder.Setup()` throws "Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661". `--startup-check` and `--gui-smoke` detect this with `MacDisplays` and say so. It only affects starting the app (the timer is registered once); a display that sleeps while the app runs was not tested.
- **Dev build.** The output is framework-dependent, so set `DOTNET_ROOT=~/.dotnet` to run the dev binary directly.
- **Running the GUI.** Open `mac/artifacts/Nightglass.app` from Finder.

## Status

- Done: shell, eight screens, night vision, status bar, banner and reconnect, settings persistence, About, simulators, macOS app menu (About / Settings… ⌘, / Night Vision ⇧⌘R).
- Verified headlessly and on screen. The tests and the smoke test render every screen with Skia under Avalonia.Headless. `--gui-smoke` launches the packaged app for real: native window, compositor frames, every page through the real renderer, app menu items. Not exercised by any automated check: clicking or typing in the real window, the Dock icon, the native menu bar itself and the keyboard shortcuts.
- M8 phase 1 (done, no hardware): the Real device source wires every screen to the engine: NINA's ASICamera through CameraVM/ImagingVM (cooling with the stuck-sensor guard, mono-bin focus frames, frames saved through NINA's patterns into NINA.Mac.Siril's layout), the LX200 driver (port picker, gotos, sync, dithers, soft park, connection-lost state, timed focuser), plate-solve centring (ASTAP, solve-field fallback), HFR and Bahtinov from NINA.Mac.ImageAnalysis, and runs through NINA's sequencer with pause and stop. A Stop on the Target screen and in the status bar halts a goto (`:Q#`); Settings › Save reaches the engine's NINA profile without a restart (during a run, when it ends). Tested against the LX200 simulator, a fake camera and a fake solver (`mac/tests/NINA.Mac.App.Engine.Test`). Simulated stays the default until the hardware path is proven.
- M8 phase 2 (no hardware): preflight (CLI, Connect card, smoke test), the optical-train switch on Connect, the horizon (editor, sky view, slew guard, engine profile), `--stack-night`, plan-check lines on Run, and a Run that offers to cancel a running calibration. The simulators now report NINA.Mac.Siril's layout and NINA's file names, as the Real devices write them. The procedure for the night is `mac/docs/first-light-runbook.md`.
- Next: the Real path on the rig (camera, then mount), and a measured horizon.

### Avalonia 12 notes (macOS 26.6)

- `FluentTheme.Palettes` only accepts the Light and Dark variants; a custom variant throws.
- Fluent control brushes read the palette once, so changing it later does not recolour buttons. For night vision, `ThemeManager` therefore shadows every Fluent solid brush with a mutable copy and recolours it in place. A test asserts that night-vision frames contain no non-red pixels.
- `Avalonia.Headless.NUnit` 12.x needs NUnit ≥ 4.5.1, but the mac stack pins 4.4.0. The tests drive `HeadlessUnitTestSession` directly instead.
- `Avalonia.BuildServices` sends build telemetry. The csproj opts out by clearing `UsedAvaloniaProducts`.
- References `Avalonia.Native` + `Avalonia.Skia` + `Avalonia.HarfBuzz` rather than `Avalonia.Desktop`, so no Win32/X11 backends ship. `Program.BuildAvaloniaApp()` must call `UseAvaloniaNative()`, `UseSkia()` and `UseHarfBuzz()`, which is what `UsePlatformDetect()` picks on macOS.
