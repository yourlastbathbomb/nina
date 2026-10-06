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
| `Astro/` | Sidereal time, alt/az, field-rotation max sub (reproduces the plan's §6 table), Sun/dawn, built-in target list |
| `Theming/` | Dark theme and red night vision (`ThemeManager`) |
| `Diagnostics/` | `--smoke-test`, `--startup-check` (real Avalonia.Native setup, run by the smoke test in a child process), `--gui-smoke` (a real launch with the window on screen for a few seconds) and the screen renderer used by tests |

These choices are specific to this rig:
- **No meridian flip and no hard park.** Soft park points the scope low in the south. The slew guard refuses targets below the horizon or above the 75° keyhole.
- **Timed focuser.** In/out nudges in ms, speed 1-4 (changing it recentres the virtual position), "recentre virtual position". Autofocus is hidden.
- **Target screen.** Shows the field-rotation sub limit, the blocked northern wedge and the keyhole.
- **Run screen.** Stops at max altitude, min altitude and astronomical dawn. Frames go to the Siril layout `~/Astro/<app>/<night>/lights/<target>/`, with shared `biases|darks|flats` and `snapshots/` kept out of lights.
- **Laptop at the scope.** While any device is connected, `NINA.Mac.Platform` keeps idle sleep, display sleep and App Nap away. The status bar shows battery and keep-awake state.

## Build, test, run

```bash
mac/dotnet build mac/src/NINA.Mac.App/NINA.Mac.App.csproj        # 0 warnings (TreatWarningsAsErrors)
mac/dotnet test  mac/tests/NINA.Mac.App.Test/NINA.Mac.App.Test.csproj
mac/dotnet test  mac/tests/NINA.Mac.App.Engine.Test/NINA.Mac.App.Engine.Test.csproj   # Real services vs the LX200 simulator and fakes, ~1 min
mac/packaging/package-app.sh                                       # .app in mac/artifacts/, see ../../packaging/README.md
```

- **Smoke test.** Run `mac/artifacts/Nightglass.app/Contents/MacOS/Nightglass --smoke-test [--screenshots DIR]`. It initialises the real platform services, composes the Real device services in a temporary data folder without opening anything (engine assemblies, NINA's ephemeris and catalogue data through the bundle's links, the ZWO/SOFA/NOVAS dylibs, a generated NINA sequence), renders every screen headlessly (Avalonia.Headless + Skia), runs a simulated night and exits 0 on success. Each page must show its own view with content in the page area, and night vision must have no white, grey, green or blue pixels. No window appears. `--version` prints the version.
- **Startup check.** `--startup-check` builds the app exactly as a Finder launch does (`Program.BuildAvaloniaApp()`: Avalonia.Native + Skia + HarfBuzz, Dock icon off), measures text and lays out the main window without showing it. The smoke test runs it in a child process because Avalonia allows one platform per process. Without `UseHarfBuzz()` every real launch threw "No text shaping system configured"; the headless platform hid this because it registers HarfBuzz itself. While every display is asleep it prints `partial:` and checks only the builder's configuration, because Avalonia.Native cannot start then (see below).
- **GUI smoke test.** `--gui-smoke [--screenshots DIR]` is a real launch: `Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime()`, the real `App` and services, the main window shown through Avalonia.Native (without taking focus). Once it opens, it checks that the window is an `NSWindow`, waits for the native compositor to render frames, draws every page with the real renderer (`RenderTargetBitmap`), checks the app menu, then quits by itself: exit 0 = pass, about 3 s. It always runs on the simulators, whatever Settings › Devices says, so a window on screen never opens the camera or the serial port. A 45 s watchdog ends it otherwise. `mac/packaging/package-app.sh` runs it on the packaged app; `GuiSmokeFlag_*` in the tests is `[Explicit]` (`--filter "TestCategory=Gui"` or by name).
- **Display asleep.** Avalonia.Native starts its render timer with `CVDisplayLinkCreateWithActiveCGDisplays`, so with every display asleep (or the lid closed without an external display) `AppBuilder.Setup()` throws "Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661". `--startup-check` and `--gui-smoke` detect this with `MacDisplays` and say so. It only affects starting the app (the timer is registered once); a display that sleeps while the app runs was not tested.
- **Dev build.** The output is framework-dependent, so set `DOTNET_ROOT=~/.dotnet` to run the dev binary directly.
- **Running the GUI.** Open `mac/artifacts/Nightglass.app` from Finder.

## Status

- Done: shell, eight screens, night vision, status bar, banner and reconnect, settings persistence, About, simulators, macOS app menu (About / Settings… ⌘, / Night Vision ⇧⌘R).
- Verified headlessly and on screen. The tests and the smoke test render every screen with Skia under Avalonia.Headless. `--gui-smoke` launches the packaged app for real: native window, compositor frames, every page through the real renderer, app menu items. Not exercised by any automated check: clicking or typing in the real window, the Dock icon, the native menu bar itself and the keyboard shortcuts.
- M8 phase 1 (done, no hardware): the Real device source wires every screen to the engine: NINA's ASICamera through CameraVM/ImagingVM (cooling with the stuck-sensor guard, mono-bin focus frames, frames saved through NINA's patterns into NINA.Mac.Siril's layout), the LX200 driver (port picker, gotos, sync, dithers, soft park, connection-lost state, timed focuser), plate-solve centring (ASTAP, solve-field fallback), HFR and Bahtinov from NINA.Mac.ImageAnalysis, and runs through NINA's sequencer with pause and stop. A Stop on the Target screen and in the status bar halts a goto (`:Q#`); Settings › Save reaches the engine's NINA profile without a restart (during a run, when it ends). Tested against the LX200 simulator, a fake camera and a fake solver (`mac/tests/NINA.Mac.App.Engine.Test`). Simulated stays the default until the hardware path is proven.
- Next: the Real path on the rig (camera, then mount), a horizon file, and a "Prepare for Siril" staging step.

### Avalonia 12 notes (macOS 26.6)

- `FluentTheme.Palettes` only accepts the Light and Dark variants; a custom variant throws.
- Fluent control brushes read the palette once, so changing it later does not recolour buttons. For night vision, `ThemeManager` therefore shadows every Fluent solid brush with a mutable copy and recolours it in place. A test asserts that night-vision frames contain no non-red pixels.
- `Avalonia.Headless.NUnit` 12.x needs NUnit ≥ 4.5.1, but the mac stack pins 4.4.0. The tests drive `HeadlessUnitTestSession` directly instead.
- `Avalonia.BuildServices` sends build telemetry. The csproj opts out by clearing `UsedAvaloniaProducts`.
- References `Avalonia.Native` + `Avalonia.Skia` + `Avalonia.HarfBuzz` rather than `Avalonia.Desktop`, so no Win32/X11 backends ship. `Program.BuildAvaloniaApp()` must call `UseAvaloniaNative()`, `UseSkia()` and `UseHarfBuzz()`, which is what `UsePlatformDetect()` picks on macOS.
