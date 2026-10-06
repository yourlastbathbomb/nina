# First light with Nightglass: runbook

For one rig: ZWO ASI585MC Pro (bin 2), Meade 10" LX200GPS in alt-az (firmware 4.0g, #1209 focuser), M1 MacBook Air, Deep Water Bay (22.25 N 114.18 E, UTC+8, no northern sky). The aim of the first clear night (plan M8):

- connected and cooling within about 2 minutes;
- focused with the Bahtinov mask and HFR;
- goto, solve, sync and centre within 1′;
- 30-60 minutes of 10 s bin-2 subs, dithering every 5;
- Siril stacks it.

Screen names below are the app's sidebar: **Connect, Cool, Focus, Target, Run, Calibrate, Teardown**, and **Settings** at the bottom. `Nightglass` means the packaged app's binary, `mac/artifacts/Nightglass.app/Contents/MacOS/Nightglass` (or wherever you copied the app). In Terminal:

```bash
alias nightglass='"/path/to/Nightglass.app/Contents/MacOS/Nightglass"'
```

## 1. Daytime (the day before, or the afternoon)

| Do | How | Done when |
|---|---|---|
| Charge the Air | Plug it in | 100 % |
| Real devices | Settings › Devices › Device source: **Real**, Save, quit and reopen the app | Connect screen no longer says "Devices are simulated" |
| Optical train | Fit the **f/6.3 reducer** for the first night. Connect › Optical train: **f/6.3 reducer** (or Settings › Optics › Reducer fitted) | Connect shows the reducer selected. Native f/10's field (0.144°) is below ASTAP D80's 0.15° minimum (plan risk 4) |
| Preflight | `nightglass --preflight`, or Connect › Preflight › **Run preflight** | No `FAIL` lines. Read every `WARN` line |
| ASTAP | If preflight says `FAIL ASTAP`, follow its fix: unzip the downloaded `astap_cli` into `~/Astro/astap/cli/`, then run `xattr -d com.apple.quarantine ~/Astro/astap/cli/astap_cli` (a zip opened in Finder leaves the quarantine on, and macOS then kills the solver at every solve; preflight checks for it and starts `astap_cli -h` once to prove it runs). The D80 files go in `~/Astro/astap/d80` | `PASS ASTAP` ("(runs)") and `PASS ASTAP D80` |
| Index tiles (solve-field fallback) | If `WARN Index tiles` names files, download them from data.astrometry.net/4200/ into `~/Library/Application Support/Astrometry` | `PASS Index tiles` |
| Siril preference | Siril › Preferences › FITS: **untick "Update pixel size of binned images"**, then quit Siril | `PASS Siril binned pixel size` (otherwise Siril doubles the 5.8 µm binned pixel to 11.6 µm, and the stacking run starts from this configuration too) |
| Data folder | Leave Settings › Storage › Images folder empty (default `~/Astro/Nightglass`). Never put it under `~/Documents` or `~/Desktop` (iCloud) | `PASS Images folder` and `PASS Free disk` (about 1.5 GB per hour at 10 s subs). `Stacking space` says how many hours of lights Siril can stack afterwards: its 32-bit RGB intermediates take about 18 GB per hour of lights (12x the lights) |
| Dark library (optional) | Camera on 12 V, cap on, Connect › **Connect all** (it cools to 0 °C; without the mount cable the mount line says so, and the camera still connects and cools). Then Calibrate › Dark library: exposures `10`, 20 frames, **Take darks** at 0 °C | Darks in `~/Astro/Nightglass/library/darks/10.00s_g252_o8_0.00C_2x2/` |

Expected `WARN` lines on the first night:

- `Horizon: estimate - measure it`: the shipped horizon is a guess (south open to 15°, north blocked at 80° from azimuth 280° through north to 80°). Measure it at dusk (section 4).
- `Serial port`: when the mount cable is not plugged in.
- `Optical train`: when native f/10 is selected.

## 2. Carry out and set up

Cable plan (two cables to the Mac):

| From | To | Notes |
|---|---|---|
| Camera USB 3 | Air's USB-C port, directly | No hub between the camera and the Mac |
| FTDI serial adapter (mount cable) | One of the camera's two USB 2 hub ports | Appears as `/dev/cu.usbserial-DU0D8VUG` |
| Mount power | Mount | — |
| 12 V supply | Camera | Without it the camera works but does not cool |

Order:

1. Set up the tripod and level it, fork mount on top, scope pointing at the Autostar's home position. Fit the reducer and camera.
2. Power the mount. On the handset:
   - High Precision **OFF**.
   - Align: **Two-Star Alt/Az**, and pick **two southern stars**. If Automatic Align chooses a star behind the house (north), stop and use Two-Star.
   - **Never change the date or time after aligning.** Nightglass never writes them either.
3. Plug in the cables as above. Open the Mac's lid and wake the display, then open Nightglass. With every display asleep the app cannot start: "Avalonia.Native was not able to start the RenderTimer".

## 3. Nightglass, screen by screen

Target: the camera cooling within about 2 minutes of opening the app.

| Step | Screen and buttons | Success looks like | Time |
|---|---|---|---|
| 1 | **Connect** › Preflight › Run preflight (optional at the scope) | No FAIL lines. `Serial port` now PASS | 10 s |
| 2 | **Connect**: check Serial port is `/dev/cu.usbserial-DU0D8VUG` (Rescan if not), tick "Cool to 0 °C after connecting", press **Connect all** | Camera, mount and focuser dots turn green. The status bar shows the sensor temperature and cooler %, mount Alt/Az, and the battery | 30-60 s |
| 3 | **Cool**: watch the temperature chart | Falls about 1 °C per few seconds towards 0 °C; cooler power settles. Do not wait: go on focusing while it cools | 5-10 min, in parallel |
| 4 | **Focus**: see section 5 | HFR at its minimum; Bahtinov reads near 0 px | 5-15 min |
| 5 | **Target**: horizon first, if the scope is aligned (section 4), then the target (section 6) | "Observable now"; Max sub at or above 10 s | 2 min |
| 6 | **Target** › **Slew** | "Centred within 0.xx′ (n solves)" | 1-3 min |
| 7 | **Target** › **Use for Run**, then **Run** › **Start** (section 7) | Frames counting up, HFR stable, "Dithered" in the log | 30-60 min |
| 8 | **Calibrate** › Flats (section 8) | 30 flats in the night's `flats/` | 5 min |
| 9 | **Teardown** › **Run teardown** | Every step reads Done | 5-10 min |

The status bar shows a **Stop** button while the mount moves. Stop halts the mount (`:Q#`) and also stops a run.

## 4. Horizon (at dusk, once per site)

On **Target** › Horizon, with the mount connected and aligned:

1. With the handset arrows, put the scope just above the skyline (trees, roof, hill) at one azimuth.
2. Press **Record point from mount**. A row appears with the mount's azimuth and altitude.
3. Repeat about every 15° of azimuth across the open south (roughly 90° to 270°). Leave the northern rows high.
4. Untick "Estimate, not measured" if it is still ticked, then press **Save**.

You can also type rows or **Import .hrz…** from a file. The slew guard, the sky view and NINA's horizon conditions use the saved horizon at once. During a run, NINA's sequence gets it when the run ends.

## 5. Focus

1. Put the Bahtinov mask on. On **Focus**, set the exposure to 2-5 s and gain 252, then press **Loop**. Focus frames use ZWO mono-bin, so they come back brighter.
2. The #1209 has no position readout. Nudge with **‹ 50 / 50 ›** (small) and **« 250 / 250 »** (large), in ms of motor time at the current speed. Use speed 2 for coarse focus and 1 for the last steps. Changing the speed recentres the virtual position.
3. Read the **Bahtinov offset**:
   - Aim for |offset| below 0.3 px.
   - The "move in / move out" hint has **not been checked on the sky**. Confirm the direction once by eye on the mask's centre spike, then trust it.
4. Take the mask off, **Take one** a few times, and watch HFR (best value shown). Stop the loop (Loop again) before you start a run; a run also stops it.

## 6. Choosing a target, field rotation and the keyhole

On **Target**:

- **Search**: for example `NGC 253`, `NGC 7293` or `M83`.
- **Max sub now**: the longest sub before field rotation blurs the frame corner by 1 px. It is the same with or without the reducer. Keep the sub length (10 s) at or below it.
- **Image east or west of the meridian, not at transit.** Near transit the field rotates fastest, and above 75° the fork cannot track (the keyhole).
- **Sky from the site**: the target's path until dawn (dot = now), the horizon, the keyhole box and the mount (cross).

Field rotation at bin 2, 1 px corner blur (plan section 6):

| Target | At transit | ±3 h from transit | With 10 s subs |
|---|---|---|---|
| Dec −30 (M83, NGC 253) | 10.6 s (alt 38°) | 16.6 s | OK all night |
| Dec 0 | 5.1 s (alt 68°) | 28.7 s | Stay 1.5 h or more from transit |
| Dec +10 | 2.9 s (alt 78°) | 64.7 s | Well east or west only. It passes the 75° keyhole near transit |

First-night picks (October evenings): **NGC 253** (Dec −25) rises in the south-east and is fine at 10 s all night. **NGC 7293** (Dec −21) is in the south in the early evening.

## 7. Goto, solve, sync, centre; then the run

- **Target** › **Slew** does the goto, then NINA's centring: a 15 s solve frame at gain 450, bin 2. ASTAP (D80) solves first; when it fails, solve-field takes over (slower, up to about 2 minutes). Then a sync and a re-slew, until the target is within 1′ (Settings › Plate solving › Centred within).
- A centring that keeps failing gives up after at most about 9 minutes (Settings › Plate solving shows the exact figure). The mount is then where the goto put it.
- The run: set Sub length **10**, Frames **180-360** (30-60 min) and Dither every **5**, keep "Stop at astronomical dawn" ticked, then **Use for Run**.
- On **Run**:
  - Read the plan-check lines under the plan. "Holds the sequence until dawn" or "Never reached" means the target will not be imaged tonight: pick another.
  - Press **Start**. With "Centre before a run" on (Settings), the run centres again first.
  - If calibration is still running, the first **Start** says so; press **Start** again to cancel the calibration and start the run.
- Monitor:
  - Frames n/N, the exposure bar, Last HFR (rising HFR: dew or focus drift);
  - the log ("Dithered", any "Download failed … taken again");
  - the status bar (sensor temperature, cooler %, battery).
- **Pause / resume** takes effect after the current frame. **Stop** ends the run.
- The run stops by itself above 75° (keyhole), below 20°, and at astronomical dawn.

## 8. Before teardown: flats

The scope must still be focused and the camera must not have been rotated. Put the LED tracing panel on the dew shield. On **Calibrate** › Flats, keep 30 frames at a 50 % target and press **Take flats**: it finds the exposure itself. The flats go to the night's `flats/` folder.

## 9. Teardown

**Teardown** › **Run teardown** does, in order:

1. Stops the run.
2. Warms the sensor at 3 °C/min, then switches the cooler off.
3. Soft park: the driver stops tracking where the scope points.
4. Disconnects the camera and the mount.
5. Lets the Mac sleep again.

Nightglass never sends `:hP#` (the Autostar's park, which points at the blocked north and leaves the handset silent until power-cycled). Then switch the mount off, unplug and carry in.

## 10. After the night: stacking

```bash
nightglass --stack-night --dry-run    # what it will do: targets, master dark per target, flats, notes
nightglass --stack-night              # newest night with lights; or: nightglass --stack-night 2026-10-10
```

It does, through NINA.Mac.Siril with siril-cli 1.4:

1. Builds missing master darks from `library/darks/`.
2. For each target of the night: calibrates with the matching master dark and the night's flats, debayers, registers on the middle frame (to keep the mid-session orientation under field rotation), and stacks.
3. Writes `result_<seconds>s.fit` in `~/Astro/Nightglass/<night>/<target>/`.

With no matching dark it stacks without one and says which darks to shoot. The Siril GUI's configuration is only read, never written. **Teardown** shows the exact command for tonight. To process one target by hand in Siril instead: `cd ~/Astro/Nightglass/<night>/<target>` and run `OSC_Preprocessing` (lights are in `lights/`).

## Troubleshooting at the scope

| Symptom | Likely cause | Do |
|---|---|---|
| Banner: camera temperature frozen while the cooler power climbs | USB glitch: the SDK reports a stale temperature | Press **Reconnect** in the banner (the cooler set point is restored) |
| Banner: camera connection lost | USB 3 cable, or 12 V off | Check the cable goes straight into the Mac, then **Reconnect** |
| Camera not found at Connect | No USB 3 link, or another app holds the camera | Quit KStars, ASIStudio and the like; replug; Connect again |
| Not cooling (power 0 %, temperature at ambient) | 12 V supply not connected | Plug the 12 V in; Cool › **Cool** |
| Banner: serial link to the mount lost, reconnecting | FTDI unplugged or loose | Replug it; the driver reconnects by itself within seconds. If it gives up, press **Reconnect** |
| Mount does not answer at Connect | The Autostar hangs (power-on, or after a park) | The camera still connects and cools (**Connect all** reports the mount on its own line). Power-cycle the mount. That loses the alignment: align again (Two-Star, southern stars), then the Mount card's **Connect** |
| Preflight: `FAIL Settings` (and "Devices are simulated" on Connect) | `settings.json` could not be read, so the app runs on defaults | Fix the file named in the fix, or re-enter Settings (Devices: Real, Site, Optics, Storage) and **Save**, then restart the app |
| Slew refused: below the local horizon | The target is behind the horizon (or the estimate is too high) | Pick another target, or measure the horizon (section 4) |
| Slew refused: above the keyhole limit | Above 75° | Wait until it is lower, or image another target |
| Solve failed; on target by goto only | Wrong optical train selected, clouds, dew, bad focus, native f/10 field too small | Check Connect › Optical train matches what is fitted, check focus and dew; slew again. solve-field is the fallback (slow). Switch Settings › Centre before a run off to image anyway |
| "The camera is still busy with a focus or calibration frame" | A frame of the Focus or Calibrate screen did not end | Stop the loop or Cancel the calibration, then Start again |
| App does not start, or the window never appears | The Mac's display was asleep at launch | Open the lid, wake the display, start the app again |
| Mac slept in the field | Lid closed on battery (macOS gives apps no way to stop that) | Keep the lid open; the app keeps the Mac and the display awake while devices are connected |
| Want KStars/Ekos instead | — | Quit Nightglass first (or disconnect both devices). Never run both on the same cable or the same camera at the same time |

## What is not proven yet

None of the following was tried before this runbook was written. Check each on the first night and note the result:

- The real camera and mount through Nightglass's Real devices.
- Real plate solves.
- The Bahtinov direction.
- The `:Mg` dither shift.
- Whether `:Q#` halts a goto on firmware 4.0g.
