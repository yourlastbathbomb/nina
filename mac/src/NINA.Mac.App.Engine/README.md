# NINA.Mac.App.Engine: Nightglass on the real engine (M8 phase 1)

The device services of the Nightglass app (`NINA.Mac.App`) in two forms, behind one set of contracts:

- **Simulated** (default): the simulators in `NINA.Mac.App/Services/Simulation`. Nothing is opened.
- **Real**: this project. NINA's own engine, composed headless through `NINA.Mac.Sequencing`'s `HeadlessHost`.

Settings › Devices picks one (`AppSettings.DeviceSource`, saved as `"deviceSource": "Real"`). The choice takes effect at the next start of the app. If the engine cannot start (missing ephemeris or catalogue data, for example), the app falls back to the simulators and shows the reason on the Connect and Settings screens. `--gui-smoke` always uses the simulators (`AppServicesOptions.ForceSimulatedDevices`).

## Layout

| Path | What |
|---|---|
| `Contracts/` | `ICameraService`, `IMountService`, `IFocuserService`, `ISessionService` (+ `PlanCheck`), `ICalibrationService`, `ICentringService`, `IImageFolders`, `IPolledService`, `AppSettings` (+ `DeviceSource`, `SolverSettings`), `IClock`, `HorizonProfile` and `SlewGuard` (`Horizon.cs`), the simulators' `SessionLayout` (NINA.Mac.Siril's layout and NINA's file names, as the Real devices write them). Moved here from `NINA.Mac.App` (same namespace, `NINA.Mac.App.Services`) so both implementations share them |
| `Engine/EngineRuntime.cs` | Process set-up (README-engine "Contract for a host" items 1-3): NINA's data folder becomes `~/Library/Application Support/Nightglass/Engine`; the WpfCompat `Application` is created on the UI thread; NINA notifications are collected so a failed connect can say why |
| `Engine/EngineProfileService.cs` | One NINA profile file for the rig (`Profiles/<fixed id>.profile`, NINA's own format, journal and backup). `RigProfile.Apply` copies the app's settings in at every start and again on every Settings › Save (`EngineDevices.ApplySettings`; a save during a run applies when the run ends, and the Focus screen's focuser speed is kept unless the Settings default changed): site, optics, gain/offset/bin, the keyhole limit (`Lx200Settings.MaxAltitudeDegrees`, so the driver's own slew guard refuses above the same altitude as the app's), the LX200 driver and focuser ids, `Direct_Guider`, NINA.Mac.Siril's file patterns, `RigPlateSolveDefaults` with the ASTAP launcher |
| `Engine/EngineDevices.cs` | Composition: profile, gated camera list, `Lx200EquipmentProvider`, `HeadlessHost`, the services; `ShutdownAsync` (stop the run, cooler off, disconnect) and `EmergencyStop` |
| `Engine/EngineCameraService.cs` | Camera through NINA's `CameraVM`/`ImagingVM`; saving through NINA's patterns; mono-bin for focus frames |
| `Engine/StuckSensorGuard.cs` | The frozen-temperature guard (mac/docs/m1-camera-results.md finding 3) |
| `Engine/EngineMountService.cs` | Mount through `TelescopeVM` over `Lx200Telescope`; port picker; link-loss state; `EngineFocuserService` over `Lx200Focuser` |
| `Engine/EngineCentringService.cs` | Goto plus NINA's `CenteringSolver`; `SirilFolders` (`IImageFolders` over NINA.Mac.Siril's layout) |
| `Engine/EngineSessionService.cs` | Target form → `NightPlan` → NINA's sequence tree → NINA's `Sequencer`, with pause gates |
| `Engine/FrameAnalysisService.cs` | HFR, stars, mean and Bahtinov offset with NINA.Mac.ImageAnalysis |
| `Engine/EngineSupport.cs` | `UiPoster` (Changed events on the UI thread), `GatedDeviceChooser`, fixed star-detection selectors |

## How each service maps onto the engine

**Nothing is opened at start-up.** NINA's `CameraVM` rescans its device list in its constructor. NINA.Equipment.Mac's `CameraChooser` would then open every ZWO camera to read its name. So the camera list sits behind `GatedDeviceChooser`, which stays empty until the camera's Connect and closes again on Disconnect. The serial port opens only in the mount's Connect. Tests check both (`Create_OpensNothing_…`).

**Camera.**
- **Connect:** opens the gate, rescans and picks the camera (not "No Camera", and not an offline placeholder), then `CameraVM.Connect()`, which wraps the camera in NINA's `PersistSettingsCameraDecorator` as on Windows.
- **Readouts:** from `CameraVM.CameraInfo`, which NINA's device timer polls every second. The UI thread never calls the SDK. A temperature in the first 3 s after connecting is ignored (M1 finding 2).
- **Health warning:** set when the temperature reading does not move by more than 0.05 °C for 90 s while the cooler power rises by 15 points within those 90 s (`StuckSensorGuard`). A camera holding its set point while the power drifts slowly does not trigger it. The banner offers a reconnect, which disconnects, reconnects and restores the cooler.
- **Exposures:** `ImagingVM.CaptureImage` sets gain, offset and bin, and NINA fills the FITS metadata. Then `NINA.Mac.ImageAnalysis` measures the frame off the UI thread. Kept lights, darks, flats and biases are saved with `SaveToDisk` and the profile's pattern for their type, so they land in NINA.Mac.Siril's layout:
  - lights: `<root>/<night>/<target>/lights/`;
  - flats and biases taken without a target: the night's `flats/` and `biases/`;
  - darks: `library/darks/<exp>_g<gain>_o<offset>_<setpoint>C_<bin>/`.
- **What is never saved:** snapshots, and the trial frames of the flat auto-exposure (`Keep = false`).
- **Mono-bin:** focus frames switch ZWO mono-bin on (ASICamera's `ZwoAsiMonoBinMode`) for that one frame only, so lights keep `BAYERPAT`. It goes off as soon as `CaptureImage` returns, before the analysis. ASICamera stores the mode in the profile and switches it on at connect when the profile says so, so the profile flag is cleared after every focus frame (even when the device write failed, USB gone), before every connect, and at the start of a run.
- **A run owns the camera:** from the start of `RunAsync` to its end, `ExposeAsync` refuses, so a focus loop left running cannot slip frames between the lights.
  - The run first waits for a frame of the service that is still going (a long Calibrate dark included): its remaining exposure plus 30 s, at least 15 s. The log says so.
  - The Focus screen stops its loop when a run starts. The Run screen offers to cancel a running calibration (a second Start).
- **Loss:** the state turns Lost when CameraVM reports the camera disconnected, or when a frame fails with `CameraConnectionLostException` or `CameraDownloadFailedException`.
  - **During a run**, the disconnect must last `EngineDevicesOptions.CameraReconnectGrace` (60 s) before the state turns Lost. NINA's ReconnectOnDownloadFailure disconnects and reconnects the camera between two lights, and the app's 1 Hz tick must not raise the banner (or invite a Reconnect) meanwhile. `ReconnectNote` says so in the meantime.
  - A camera NINA reconnects after the grace returns to Connected by itself.
  - Outside a run a disconnect is Lost at once.
- **Disconnect:** switches the cooler off first (host contract item 7).

**Mount and focuser.**
- **Port picker:** lists `/dev/cu.*`. The choice is `Lx200Settings.PortPath` in the rig profile. An empty choice takes the first USB serial adapter. A saved port is never replaced while its adapter is unplugged.
- **Connect:** `TelescopeVM` over `Lx200Telescope`. The #1209 focuser connects on the same link, and `DirectGuider` connects for mount dithers.
- **Gotos and syncs:** take J2000. The driver converts to JNow with NINA's transform.
  - The slew guard (`SlewGuard`, shared with the simulator) refuses targets below 0°, below the local horizon (`EngineDevicesOptions.Horizon`, the app's horizon) or above the keyhole limit, before anything is sent.
  - `IsMotionCommandActive` is true from the call of a goto, a park or a slew-and-centre until it returns. The status bar shows Stop from it as well as from the polled `IsSlewing`, which a short goto can finish between two polls.
- **Dither:** `GuiderMediator.Dither` (DirectGuider), then a wait on the driver's live `IsPulseGuiding`. DirectGuider itself only checks the polled `TelescopeInfo`, so it can return before a serialised second pulse has gone out.
- **Soft park:** NINA's park, which is the driver's soft park. Never `:hP#`.
- **Halt:** `Abort` sends `:Q#` (NINA's `StopSlew`) and cancels a halt token that gotos, parks and centring link, because `TelescopeVM` reports a goto that `:Q#` halted as arrived. Disconnect (and so quitting) sends `:Q#` first when the mount is connected: the driver's teardown halts only `MoveAxis` motions, and a goto would carry on with the link closed. The Target screen has a Stop while it slews; the status bar shows a Stop whenever the mount slews, which also stops a run in progress.
- **Link loss:** while the serial link is Reconnecting, the state is Lost and the banner says so. The driver keeps the devices connected and reconnects by itself; the state returns to Connected when the link does. Reconnect during an outage first waits up to 10 s for the link, then starts over. When the driver gives up, NINA disconnects the mount, the next tick reports Lost, and Reconnect starts afresh.
- **Focuser:** moves are timed nudges. Speed 1-4 is `Lx200Settings.FocuserSpeed`, sent before every move; changing it recentres the virtual position. A single move is at most 30 s of motor time.

**Centring** (Target › Slew and centre) runs the goto, then NINA's `CenteringSolver`, built exactly as the sequencer's `Center` builds it:
- the profile's plate-solve settings: ASTAP with its D80 database through `AstapSetup`'s `-d` launcher, solve-field as the blind failover, 15 s solve frames at gain 450, bin 2;
- solve frames through ImagingVM;
- sync and re-slew through TelescopeVM.

Without ASTAP configured, or without the camera, the result says "goto only" and why. Centring is refused while a run is active, and a halt (Stop, disconnect, quit) ends it.

**Runs.** `ToNightPlan` maps the Target form one to one:
- J2000 coordinates, sub length, gain, offset, bin and count;
- dither cadence;
- the maximum (keyhole) and minimum altitude;
- a dawn stop: astronomical, or civil when "stop at dawn" is off, because NINA's night always ends at a dawn;
- centring first, when a solver is available and Settings › Centre before a run is on;
- drift recentring (`SolverSettings.RecenterArcmin`, 0 = off).

The Cool and Teardown screens own the cooler and the park, so the generated night neither cools, warms nor parks. It does not switch the dew heater either (M1 notes failed `ASI_ANTI_DEW_HEATER` writes).

`HeadlessSequenceRunner.Validate` runs before the start; any issue fails the run with that issue.

`CheckPlan` runs NINA.Mac.Sequencing's PlanValidator on the same NightPlan without running it. It reports tonight's window and its warnings and errors ("Holds the sequence until dawn", "Never reached"), and the Run screen shows them under the plan.

**Horizon.** `RigProfile.ApplyHorizon` writes the app's horizon to `<engine data>/horizon.hrz` with NINA.Mac.Sequencing's `HorizonFile.Save`: the plain format, with explicit 0° and 360° points. `EngineProfileService.ChangeHorizon` then loads it with `HorizonFile.Load`, and HeadlessHost's `ProfileHorizonWatcher` reloads it the same way. This happens at start-up, on Settings › Save, and on `ApplyHorizon()` (Target › Horizon › Save). During a run it waits for the end, like settings. A pause gate (a small `SequenceItem`) goes before every `TakeExposure`. Pause therefore takes effect after the current frame, and NINA's conditions still end the target while paused.

Stop cancels the run, which aborts the exposure. After the sequence ends, the service waits for NINA's save queue to write the last lights.

A light whose download fails (`CameraDownloadFailedException` or `CameraExposureFailedException`): NINA's `ReconnectOnDownloadFailure` reconnects the camera before the next item, and the service adds one iteration to the light loop's `LoopCondition`, so the frame is taken again. That happens once per failed frame: a retry that fails too is skipped, so a camera that keeps failing cannot extend the run without end. The stop reason of a run that ends short then names the failed downloads.

Progress comes from three sources:
- `ImageSaved` gives the frames done and the last file;
- `ImagePrepared` gives the HFR, measured off the UI thread;
- NINA's status and notifications go into the log.

The stop reason is the run's own (all frames, stopped). Otherwise it is worked out afterwards: the target above the keyhole, below the minimum, or past dawn.

## Tests

`mac/tests/NINA.Mac.App.Engine.Test`: 53 tests, about two and a half minutes, no hardware. Every test passes its own camera list; the real camera list is never used.

| Fixture | What it shows |
|---|---|
| `CompositionTests` | **Composing opens nothing:** no camera scan, no serial open.<br>**The rig profile:**<br>• settings and Siril patterns;<br>• driver ids;<br>• `Direct_Guider`;<br>• `NOSYNC`;<br>• it persists between starts, LX200 plugin-store values included.<br>**Solvers:** the ASTAP launcher with `-d`, and NINA's factory picking `ASTAPSolver`, then `LocalPlateSolver`.<br>**Other:** NINA's data folder cannot move; `SirilFolders`. |
| `CameraServiceTests` | Through NINA's CameraVM and ImagingVM over `Fakes/FakeCamera`:<br>• connect and info; cooler on, and off at disconnect;<br>• first-reading guard;<br>• a light saved through NINA's patterns into `<night>/<target>/lights` with the FITS cards;<br>• calibration frames in the night folders and the dark library; trial flats and snapshots not saved;<br>• mono-bin only for the focus frame;<br>• HFR that follows focus;<br>• the Bahtinov offset of a synthetic mask pattern, sign and size (+6 px measured 6.15, -6 px measured -6.08);<br>• mono-bin left on by a USB drop during a focus frame does not come back with the reconnect;<br>• USB loss to Lost and reconnect; a failed download to Lost;<br>• the stuck-sensor guard in the service and on its own (no false alarm at a held set point). |
| `MountServiceTests` | LX200 driver over the Autostar simulator (`SimCable`, linked):<br>• port picker;<br>• connect with focuser and DirectGuider; J2000 readouts;<br>• gotos, with the guard refusing the horizon and the keyhole;<br>• sync, dither pulses, tracking, soft park;<br>• cable out gives Lost, and the link recovers by itself;<br>• Reconnect during an outage;<br>• focuser speed, moves and recentring;<br>• a silent mount fails with a reason;<br>• disconnect and shutdown during a goto send `:Q#` and the simulator stops; cancelling slew-and-centre halts the goto. |
| `CentringServiceTests` | NINA's CenteringSolver measures a 13.9′ goto error, syncs once and re-slews to within 1′ in 2 solves; a failed solve leaves the goto; no camera means slew only; frame analysis. |
| `FirstLightEngineTests` | **The camera during NINA's reconnect:** a run's CameraVM disconnect and reconnect never turns the camera Lost; past the grace it is Lost and comes back by itself; while idle it is Lost at once.<br>**Long frames:** a run started during a 16.5 s dark waits for it (it used to fail after 15 s).<br>**Gotos:** `IsMotionCommandActive` from the call, for goto and slew-and-centre.<br>**Horizon:** the local horizon refuses a goto before anything is sent; it reaches NINA's profile (CustomHorizon at 15° south and 80° north), and a new one applies at once.<br>**Plan check:** the window tonight; a target that never rises is flagged. |
| `SessionServiceTests` | **The form-to-plan mapping.**<br>**A full run through NINA's Sequencer:**<br>• Center with one `:CM` sync;<br>• 3 lights in the Siril layout with `OBJECT`;<br>• HFR on the Run screen;<br>• dither pulses on the wire;<br>• the generated tree checked.<br>**Control:** pause holds between frames, resume continues, stop ends with "Stopped by user"; preconditions; a target above the keyhole ends with that reason and no light.<br>**The camera at the start of a run:** a focus loop left running gets no frame between the lights, and the lights are colour (`BAYERPAT`).<br>**Failed downloads:** one is retried after NINA's reconnect and the run takes every frame; a retry that fails too ends the run one short with the reason.<br>**Settings:** a save reaches the profile and the next run (new images root); a save during a run applies when it ends; the Focus screen's speed survives a save. |

`mac/tests/NINA.Mac.App.Test/Ui/RealDevicesTests.cs` checks the app side:
- `AppServices` with Real composes these services;
- every screen renders on them;
- Connect with no camera says so after one scan;
- a failed engine falls back to the simulators with the reason;
- `ForceSimulatedDevices` overrides the setting;
- the device source round-trips through `settings.json`;
- Settings › Save reaches the engine's NINA profile without a restart.

`mac/tests/NINA.Mac.App.Test/Ui/FirstLightTests.cs` checks:
- the horizon format both ways, and that NINA's own `CustomHorizon` reads the written file the same way;
- the slew guard with the horizon;
- preflight against fakes: each check, and every WARN or FAIL has a fix;
- the Moon and astronomical night;
- the horizon editor (record from the mount, save, import, export);
- the Connect screen's preflight and optical train;
- the Run screen's plan check and the hand-over from a running calibration;
- `--stack-night`'s choice of night.

`mac/tests/NINA.Mac.App.Test/Ui/StopTests.cs`: the Target screen's Stop and the status bar's Stop halt a goto (the latter also the run moving it), and the Focus loop gives the camera up when a run starts.

## Not verified, and open

- **No hardware.**
  - The real ASI585MC through `ASICamera` (connect, cooler, mono-bin on the SDK, a real USB unplug) has not run through these services. The camera is reserved, so its explicit hardware tests were not run either.
  - The real LX200GPS on `/dev/cu.usbserial-DU0D8VUG` has not run through them either.
- **Real plate solves:** ASTAP and solve-field are configured, but only the fake solver ran here. `NINA.Platesolving.Mac`'s `[Explicit]` LocalData tests cover real solves.
- **Bahtinov direction:** the sign of the offset follows the analyser's convention (`BahtinovResult.SignedOffset`). Which focuser direction it means on this rig (the Focus screen says "move in" for a positive offset) has not been checked on the sky.
- **Soft park position:** soft park uses the driver's defaults (`Lx200Settings.SoftParkAltitude/Azimuth` NaN: park in place, tracking off) until bench step 8 decides. The Teardown text "low in the south" is the simulators'.
- **Dither settle:** sequence dithers rely on NINA's DirectGuider. It waits the longer pulse, the profile's settle time (NINA default 10 s) and the polled `IsPulseGuiding`. The driver serialises the two axes, so with a short settle time a light can start before the second pulse ends. The app's own `DitherAsync` also waits on the driver's live flag.
