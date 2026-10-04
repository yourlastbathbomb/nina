# NINA.Mac.App: native macOS app shell (M8/M9 groundwork)

Avalonia **12.1.3** (newest stable on NuGet, 2026-10) + CommunityToolkit.Mvvm 8.4.2. The display name is a single MSBuild property, `AppDisplayName`, defaulting to **"Nightglass (working name)"**. The build fails if the name contains "NINA" (MPL grants no trademark rights). The About box credits "based on N.I.N.A." under MPL-2.0. **No engine yet:** every device is simulated behind small service interfaces.

## What is here

| Path | What |
|---|---|
| `ViewModels/` | Shell (`MainWindowViewModel`: navigation, status bar, connection-lost banner, night vision) and the eight screens of the night: **Connect, Cool, Focus, Target, Run, Calibrate, Teardown, Settings**, plus About |
| `Views/` | One AXAML view per screen, `MainWindow`, `AboutWindow`. Compiled bindings throughout |
| `Services/` | `ICameraService`, `IMountService`, `IFocuserService`, `ISessionService`, `ICalibrationService`; settings (JSON in Application Support); Siril session layout; keep-awake and power coordinators |
| `Services/Simulation/` | ASI585MC (TEC model, exposures), LX200GPS alt-az (slew guard, soft park, dither), #1209 timed focuser (virtual ms position, speed 1-4), session runner, calibration |
| `Astro/` | Sidereal time, alt/az, field-rotation max sub (reproduces the plan's §6 table), Sun/dawn, built-in target list |
| `Theming/` | Dark theme and red night vision (`ThemeManager`) |
| `Diagnostics/` | `--smoke-test` and the headless screen renderer used by tests |

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
mac/packaging/package-app.sh                                       # .app in mac/artifacts/, see ../../packaging/README.md
```

- **Smoke test.** Run `"mac/artifacts/Nightglass (working name).app/Contents/MacOS/Nightglass" --smoke-test [--screenshots DIR]`. It initialises the real platform services, renders every screen headlessly (Avalonia.Headless + Skia), runs a simulated night and exits 0 on success. No window appears. `--version` prints the version.
- **Dev build.** The output is framework-dependent, so set `DOTNET_ROOT=~/.dotnet` to run the dev binary directly.
- **Running the GUI.** Open the packaged `.app` from Finder. The real AppKit window path has not yet been exercised by any automated test; see Status.

## Status

- Done: shell, eight screens, night vision, status bar, banner and reconnect, settings persistence, About, simulators, macOS app menu (About / Settings… ⌘, / Night Vision ⇧⌘R).
- Verified headlessly only. The tests and the smoke test render every screen with Skia under Avalonia.Headless. The Avalonia.Native (AppKit) windowing path has not been launched on screen; only `libAvaloniaNative.dylib` loading was checked.
- Next (M8): replace the simulators with the engine (NINA's ASICamera via `NINA.Mac.Native`, the LX200 driver, the headless sequencer) behind the same interfaces. Also: real Bahtinov/HFR (M5), plate-solve centring (M6), a horizon file, and a "Prepare for Siril" staging step.

### Avalonia 12 notes (macOS 26.6)

- `FluentTheme.Palettes` only accepts the Light and Dark variants; a custom variant throws.
- Fluent control brushes read the palette once, so changing it later does not recolour buttons. For night vision, `ThemeManager` therefore shadows every Fluent solid brush with a mutable copy and recolours it in place. A test asserts that night-vision frames contain no non-red pixels.
- `Avalonia.Headless.NUnit` 12.x needs NUnit ≥ 4.5.1, but the mac stack pins 4.4.0. The tests drive `HeadlessUnitTestSession` directly instead.
- `Avalonia.BuildServices` sends build telemetry. The csproj opts out by clearing `UsedAvaloniaProducts`.
- References `Avalonia.Native` + `Avalonia.Skia` rather than `Avalonia.Desktop`, so no Win32/X11 backends ship.
