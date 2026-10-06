# NINA.Mac.Siril

This is the Siril output side of the macOS port. It covers `MAC_PORT_PLAN.md` section 6 ("Siril output") and decisions 7 (calibration) and 8 (Siril). It is a managed class library with no upstream sources and no references to other `mac/` projects, and the engine is meant to call it later. Tests live in `mac/tests/NINA.Mac.Siril.Test`.

| Type | What it does |
|---|---|
| `SessionLayout` | Builds the per-night folder tree and routes each frame (`FrameInfo`) to its folder and file name |
| `NinaFilePatterns` | NINA profile file patterns that write the same tree for target names NINA sanitises the same way (`SessionLayout.KeepsNinaFolderName`). Also a port of `ImagePatterns.GetImageFileString` with the proposed upstream fix (split on `/` and `\`, plus `$$IMAGETYPEDIR$$`) |
| `SirilPathTemplate` | Siril 1.4 header tokens (`$EXPTIME:%d$`, `%f`, `%s`, `dmN`), resolved the same way siril-cli resolves them |
| `DarkLibrary` | Scans the raw-dark sets and builds masters with siril-cli. Siril names each master from the token template |
| `SirilScriptGenerator` | Generates a per-target script equivalent to `OSC_Preprocessing` v1.4, or `OSC_Extract_HaOIII` v1.5 on dual-band nights |
| `SirilSessionValidator` | Runs the checks Siril skips: one exposure/gain/offset/set-point/binning per lights folder, BAYERPAT present, sizes match, the master exists, no stray JPEG/TIFF files |
| `SirilRunner` | Runs `siril-cli -o -i <own ini> -d <dir> -s <script>`, captures the log (and streams each line to an optional callback), detects failure and parses `Error in line N` and missing files |
| `MasterDarkIndex` | Reads the masters' headers and finds the master for a night's lights: exposure, gain, offset, binning, size, camera (INSTRUME) and temperature within a tolerance; says in plain words why none matches and which darks to shoot |
| `SirilNightProcessor` | "Stack last night": every target of one night, its matching master (explicit path), flats and dark flats/biases if present, the generated script, the run with the log streamed, and a report naming each stack |
| `SirilPreprocessor` | Validate, then generate, then clean `process/`, then run, then collect `result_*.fit` |

## Layout (defaults)

```
~/Astro/NINA/                        root (outside the iCloud-synced ~/Documents, research SIR-15)
  2026-10-03/                        night = local date of (exposure start - 12 h), Asia/Hong_Kong
    flats/  biases/                  night-level frames taken without a target (fallback for every target)
    NGC 253/                         Siril working directory of one target
      lights/                        LIGHT only, top level, names start with the date-time
      flats/                         FLAT
      biases/                        dark flats (preferred) or biases; calibrate the flats only
      snapshots/                     SNAPSHOT, never stacked
      masters/ process/              Siril output; process/ holds symlinks and is cleaned before each run
      nina_siril.ssf  siril_*.log  result_<LIVETIME>s.fit
  library/
    darks/20.00s_g252_o50_0.00C_2x2/ raw darks, one folder per exposure/gain/offset/set-point/binning
    masters/dark_20s_G252_O50_T0_B2.fit
```

- **Night rollover.** The night changes at local noon (HKT), like NINA's `$$DATEMINUS12$$`, not at midnight.
- **Dark flats.** NINA 3 types dark flats as `DARK`. The engine must set `FrameInfo.IsDarkFlat` so they go to `biases/`.
- **Save through `SessionLayout.GetFramePath`.** The `NinaFilePatterns` profile patterns give the same paths only when `SessionLayout.KeepsNinaFolderName(target)` is true. NINA keeps `:`, `"`, `$`, control characters and a leading `.` in target folder names (on macOS `CoreUtil.ReplaceAllInvalidFilenameChars` only maps `\`, `/` and `\0`), while the layout replaces them because they break Siril scripts. NINA also writes the lights of an untitled target to `<night>/lights` instead of `<night>/untitled/lights`. Frames saved by stock patterns for such targets end up where `GetTargetFolders` does not look.
- **Master-dark template.** The template is `dark_$EXPTIME:%d$s_G$GAIN:%d$_O$OFFSET:%d$_T$SET-TEMP:%d$_B$XBINNING:%d$.fit`.
  - Lights are calibrated with `"-dark=<library>/masters/<template>"`. Siril fills the tokens from the first light's header.
  - If no master matches, Siril stops the script at `calibrate` (exit code 1). The validator reports the same problem before the run.
- **Default script choices:**
  - Master dark from the library.
  - Flats calibrated with the master of `biases/`.
  - Registration on the middle frame (`setref` followed by one-pass `register`), which keeps the mid-session orientation for alt-az.
  - Stacking: `stack rej 3 3 -norm=addscale -output_norm -rgb_equal -32b`, then `mirrorx -bottomup`.
- **Script options:**
  - `FlatCalibration.SyntheticOffset`: uses `"-bias==N*$OFFSET"`. Measure N once from biases and round it: N is an `int`, because siril-cli 1.4.4 aborts `calibrate` on a fractional multiplier. The rounding leaves at most OFFSET/2 ADU of pedestal.
  - `RegistrationReference.TwoPass` with `Framing.Min/Max/Cog`.
  - `DarkSource.Folder`: the stock darks/ step.
  - `ProcessingMode.HaOIII`.
- **siril-cli config.** siril-cli writes `wd=` and every `set` into the ini it loaded. The runner therefore copies the GUI config (`~/Library/Application Support/org.siril.Siril/siril/config.1.4.ini`, read only) to `~/Library/Application Support/Nightglass/siril/siril-cli.ini` (the app's own folder) before each run and passes that copy with `-i`. The working directory (`-d`) is the target's folder (or the library), never Siril's GUI working directory.
- **Pinned output format.** Every generated script (preprocessing and master darks) starts with `setext fit`, `set32bits` and `setcompress 0`. The runner seeds its ini from the GUI config, so without these lines the GUI preferences would decide the output format:
  - `extension`: master names would no longer be `.fit`.
  - `force_16bit=true`, which is set in William's GUI config: masters, `pp_` frames and the OIII result would be saved as 16-bit integers, with negative calibrated values clipped to 0. Only `stack -32b` overrides this preference.
  - `[compression] enabled=true`: masters and results would be written as `.fit.fz`, which the `-dark=` template, the validator and the result collection do not look for.

  These `set` commands land only in the runner's own ini.

## Dark library index and stacking a night

- **Matching (`MasterDarkIndex.Find`).** Siril's own lookup (the `-dark=` header-token template) matches exposure, gain, offset, set point and binning exactly after `%d` truncation, reads only the first light, and stops the script with only a file name in the log. The index reads every master's header (values missing from it come from a default-template file name, with a note) and matches:
  - exposure within 0.05 s, gain, offset and binning exactly, the frame size, and the camera (INSTRUME; a master without it is accepted with a warning, `RequireSameCamera` can relax it);
  - temperature within 2 °C (`TemperatureToleranceC`), compared on the sensor temperature (median CCD-TEMP of the lights; the master's CCD-TEMP) where known, else the set point. A cooler that never reached its set point (no 12 V supply) is therefore matched by what the sensor really was, and is reported. A master whose temperature is unknown (no CCD-TEMP or SET-TEMP in its header and a name that does not follow the default template) is refused for lights of known temperature, with a reason that says how to make it usable; only lights of unknown temperature are matched without comparing temperatures, with a warning.
  - The best candidate is the closest in temperature, then the one stacked from more frames. `NeedsExplicitPath` says when Siril's template would not have found it (e.g. a T-1 master for T0 lights).
  - With no match the message names the lights' settings, the nearest masters and why each was not taken, and the darks to shoot and where they go, e.g. "No master dark matches lights at 20 s, gain 252, offset 50, bin 2, sensor 24.3 °C (set point 0.0 °C), …. Nearest: dark_20s_G252_O50_T0_B2.fit (taken at 0.0 °C, the lights at 24.3 °C (tolerance 2 °C)). Shoot 30-50 darks of 20 s at gain 252, offset 50, bin 2, within 2 °C of 24.3 °C (they go to …/library/darks), then build the masters."
- **Explicit master.** `SirilPreprocessingPlan.MasterDarkPath` replaces the template with the chosen master's absolute path; the validator then checks that file and its size.
- **A night (`SirilNightProcessor`).** `Prepare(layout, night)` decides without running anything; `RunAsync(layout, night, runner, options, log)` first builds missing or stale masters from the library's raw dark sets, then per target (folders of the night with FITS lights; fewer than 3 lights are skipped):
  - the matching master, or with none (default `ProcessWithoutDark`) no dark and a note that says why, or the target is skipped;
  - the target's or the night's flats if present, calibrated with its or the night's dark flats/biases, else with the synthetic offset if `SyntheticOffsetMultiplier` is set, else uncalibrated with a warning; no flats is said too;
  - OSC script: lights as taken (bin 2), calibrate, debayer, register on the middle frame (`setref`, the mid-session orientation for alt-az field rotation), stack, `result_<LIVETIME>s.fit` in the target folder;
  - siril-cli with the app's ini; each log line goes to `log` as `[<target>] <line>`, the decisions as `Siril: …`; a failed target does not stop the others.
  - `SirilNightReport.Lines` is the report: per target the stack path or the failure (with the log path and a missing master dark), and the notes.

## Siril 1.4.4 behaviour verified here (siril-cli on this Mac)

- **`%d` truncates.** It casts with C `(int)`, so `EXPTIME = 19.99` gives `19s` and `SET-TEMP = -0.6` gives `T0`. The C# resolver matches `parse -r` output exactly for 16 cases (`SirilParityTest`).
- **`$DATE-OBS:dm12$` uses UTC.** In HKT it therefore rolls over at 20:00, not at noon. Use `$DATE-LOC:dm12$` when you need the local night.
- **`%s` drops apostrophes.** Siril shell-unquotes the raw FITS string, so `'Thor''s Helmet'` becomes `Thors_Helmet`.
- **A script without a leading `requires` is skipped.** siril-cli still prints "Script execution finished successfully" and exits 0, so the runner refuses such scripts.
- **`-i` must name an existing file.** siril-cli exits 1 if the file is missing. An empty file gives Siril's defaults.
- **The synthetic-offset multiplier must be a positive whole number.**
  - `-bias==16*$OFFSET` and `17*$OFFSET` work.
  - `16.5*$OFFSET`, `15.75*$OFFSET`, `16.0*$OFFSET` and `0*$OFFSET` fail at `calibrate`, logging "The offset value could not be parsed from expression".
  - A plain level such as `-bias==800` works, but is read as an integer prefix: `812.5` gives 812 and `1e3` gives 1.
- **Preferences apply wherever a command has no flag for them.** With the GUI's `force_16bit=true`, `bias_stacked`, `pp_flat_stacked`, `pp_light` and `r_pp_light` came out as BITPIX 16, and so did the PixelMath OIII result. With compression on, every output came out as `.fit.fz`. `set32bits` and `setcompress 0` restore BITPIX -32 and `.fit`. Besides paths, `force_16bit` is the only processing setting in which William's GUI config differs from Siril's defaults (star-finder optics, the photometry gain and the update check differ too).
- **`seqextract_HaOIII -resample=ha` upsamples Ha to full frame size.** The research (SIR-14) said the result would be half size. On the test data, both Ha and OIII results came out at the light-frame size.
- **Stock-mode output matches the stock script exactly.** With stock folders, the generated script produces a stack identical (max difference 0) to the installed `OSC_Preprocessing.ssf`.

## Build and test

Use the repo's .NET 10 wrapper. Plain `dotnet` is .NET 8 and fails.

```bash
mac/dotnet build mac/tests/NINA.Mac.Siril.Test/NINA.Mac.Siril.Test.csproj
mac/dotnet test  mac/tests/NINA.Mac.Siril.Test/NINA.Mac.Siril.Test.csproj
```

- **Siril tests.** Tests that need Siril run `/Applications/Siril.app/Contents/MacOS/siril-cli` and are ignored when it is missing.
- **Temp files.** Everything runs in `$TMPDIR/nina-mac-siril-tests/`, in paths containing spaces. Set `NINA_SIRIL_KEEP=1` to keep the files for inspection.
- **Siril configuration.** The tests never read or write the Siril GUI configuration, except one runner test. That test copies the configuration and asserts that the original's hash and mtime are unchanged.
- **End-to-end test (`EndToEndTest`).** It writes a synthetic night through `SessionLayout`, then builds the dark library with siril-cli, then runs the generated scripts. It measures:
  - The flux ratio of a corner star to a centre star.
  - The background offsets between corner and centre, and between left and right, in the stack.
  - The R/G/B sky ratios and flatness in the calibrated lights.
  - The master flat's corner/centre ratio per CFA channel against the rig's vignetting model. This is the check that sees a pedestal left in the flats: raw flats measure +1.2/+1.8/+3.6 % (R/G/B) against a 0.4 % tolerance, while the stack metrics barely move.
  - The same metrics for uncalibrated and raw-flat control runs.
  - Synthetic-offset and two-pass/min variants.
  - Ha/OIII output.
  - Aborts on exposure and gain mismatch.
  - Equivalence with the stock script.
  - A run seeded with GUI-style preferences (`.fits`, `force_16bit=true`, compression on), written by the test. All outputs must still be `.fit` at BITPIX -32. A control run with the pins removed shows that these preferences do take effect.
- **`MasterDarkIndexTest`** (no Siril): exact match equals Siril's template; a T-1 master within tolerance for T0 lights (explicit path needed); a cooler at 24.3 °C for a 0 °C set point finds nothing and says what to shoot; another camera or size is refused; the closer temperature, then more frames, wins; file-name fallback; a master of unknown temperature refused for warm and for cold lights; lights of unknown temperature still matched with a warning; empty library; `Prepare` for a night of three targets (dark + flats + dark flats; no dark and no flats; too few lights).
- **`SirilNightTest`** (siril-cli): a synthetic night, two targets and raw library darks without a master. `RunAsync` builds the master, stacks NGC 253 (8 lights, explicit master, flats with dark flats, `setref pp_light 4`) and M 42 (6 lights at a 24 °C sensor: no dark, said so) into one 640x480 RGB `result_*.fit` each, and streams Siril's log per target.
- **`SyntheticOffsetTest`.** It runs the generated flat step with N = 17 in siril-cli, and pins Siril's rejection of a fractional N.

## Use from the engine

```csharp
var layout = new SessionLayout(new SessionLayoutOptions());           // ~/Astro/NINA, HKT
var path = layout.GetFramePath(frameInfo);                            // where to save a frame
var runner = new SirilRunner();                                       // own ini, GUI config only read
await layout.Library.BuildMastersAsync(runner);                       // missing/stale masters
var plan = SirilPreprocessingPlan.ForTarget(layout.GetTargetFolders(night, "NGC 253"), layout.Library);
var result = await SirilPreprocessor.RunAsync(plan, runner);          // result.Issues, result.Run.Summary, result.Results

// or a whole night: masters built if missing, matching dark per target, log streamed
var night = await SirilNightProcessor.RunAsync(layout, new DateOnly(2026, 10, 3), runner, log: line => Console.WriteLine(line));
foreach (var line in night.Lines) { Console.WriteLine(line); }            // stacks, missing darks, missing flats
```

## Status

- **Done:** library and tests, built with 0 warnings. All tests pass against siril-cli 1.4.4, using synthetic frames only.
- **Not done:**
  - The library is not wired into the engine and is not listed in `mac/NINA.Mac.slnx`. The owner of the slnx should add both projects.
  - It has not been tried on real ASI585MC frames.
  - `-fitseq` for more than 9000 lights is not implemented. The validator only warns.
  - Drizzle is not implemented.
- **Upstream proposal.** The fix for `/` vs `\` separators and the `$$IMAGETYPEDIR$$` token are described in the task report as a proposed upstream patch. No upstream file is changed.
