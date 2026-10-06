# M1 camera results: ASI585MC Pro on the M1 Air

These results were measured on 2026-10-04 with `zwoprobe`, using ZWO SDK 1.41 (osx-arm64) on macOS 26.6.2 and .NET 10.0.12, with the camera on 12 V. **M1 passed.**

## Checklist

| Check | Result |
|---|---|
| SDK loads natively; struct layout | SDK `1, 41, 0, 0`, loaded through the `NativeLibraries` resolver with bundled libusb. `ASI_CAMERA_INFO` is 248 bytes and `ASI_CONTROL_CAPS` 264, with offsets as clang computes them from the header |
| Camera properties | `ZWO ASI585MC Pro`: 3840 x 2160, 2.9 µm, 12-bit, colour RGGB, bins 1/2/3/4, RAW8/RGB24/Y8/RAW16, cooler present, no ST4 |
| Control ranges (proves the `CLong` fix) | Gain 0-600 (default 200); offset 0-200 (default 3); exposure 32 µs to 2000 s; target temperature -40 to 30 °C; USB bandwidth 40-100 |
| Gain presets | `ASIGetLMHGainOffset`: low 0, **HCG 200**, high 450, high-gain offset 15. `ASIGetGainOffset`: offset 3 for HighestDR and Unity; LowestRN is gain 450 with offset 15 |
| Frames and headers | Bin 1 gives 3840 x 2160 and bin 2 gives 1920 x 1080. Headers: `BAYERPAT=RGGB`, `ROWORDER=TOP-DOWN`, `XPIXSZ` 2.9 at bin 1 and 5.8 at bin 2, plus `CCD-TEMP`, `GAIN` and `OFFSET`. Mono-bin frames correctly omit `BAYERPAT` |
| Colour | A phone showing pure red, then pure blue, was held in front of the bare sensor. Raw Bayer means: red light gave R/G 3.0 and B/G 0.11; blue light gave R/G 0.37 and B/G 4.0. **Siril 1.4.4** (`convert -debayer`, `stat`) reported "RGGB from header, top-down from header": the red frame came out R 30609 / G 10333 / B 1483 and the blue frame R 1687 / G 4180 / B 16114 |
| Cooling | 12.0 °C to 0 °C in 3 min 20 s. It then held 0 ± 0.5 °C at 36-44 % cooler power (41 % mean) for 30 min |
| Endurance | 58 frames of 30 s at bin 2, gain 200: **0 failed, 0 stalled, 0 dropped**. Overhead per frame was 0.98-1.28 s (mean 1.14 s); download after the exposure ended took 13-29 ms. Median 340 ADU with the cap on, 16-bit scaled |

## Findings the plan did not have

1. **The camera was on USB 2.0.** It went through a USB-C dongle (WCH hub, wch.cn Ethernet, Genesys card reader), and ioreg reported `Device Speed = 2` (480 Mb/s). The SDK still reported `IsUSB3Host = True`, so that flag can't be trusted. This is the likely cause of the ~1 s overhead per frame, and it slows live focusing loops. **At the scope:** connect the camera directly to the Air with a USB-C to USB 3 Type-B cable, or through a USB 3 hub. The mount's serial adapter can hang off the camera's built-in USB 2 hub.
2. **The temperature reads 0.0 °C for the first ~2 s after opening.** That is the SDK's value before its first poll, not a real reading. Ignore readings taken before the first poll.
3. **The temperature froze after a USB disconnect.** The USB cable came loose once while the camera stayed powered on 12 V. In the next session the SDK reported a constant 20.0 °C while cooler power climbed from 0 to 54 % in 90 s, much faster than the normal ramp. A fresh session behaved normally. **Nightglass needs a stuck-sensor guard:** if cooler power rises while the temperature hasn't changed for N minutes, warn and offer a reconnect. It also needs the "connection lost" banner (M9).
4. **The HCG point is gain 200**, as the SDK reports. That settles the conflict between the manual (252) and the product page (200). Maximum gain is 600, not about 450.
5. Short exposures (0.5-50 ms) also took about 1 s each, because every `snap` reopens the camera. The endurance overhead above is the steady state on USB 2.

## Follow-ups

- **Fixed (2026-10-05):** upstream test `GetImageFileString_PathSegmentsAndImageTypeOverride_ReturnsSafePath` failed on macOS. `Path.GetInvalidFileNameChars()` doesn't include `:`, so a target named "M31:Core" becomes `M31:Core` on macOS, where Windows writes `M31_Core`. Colons break copying to exFAT or Windows drives, and Finder shows them as `/`. The Mac build should sanitise file names with the Windows character set.
- Re-run the endurance loop on a direct USB 3 connection and compare the overhead.

## Engine hardware tests (2026-10-06)

The camera was plugged directly into the Air: ioreg shows `ASI585MC Pro` at **5 Gb/s (USB 3)**. Both explicit hardware tests passed through NINA's own engine on macOS:

| Test | Result |
|---|---|
| `AsiCameraHardwareTest` (Equipment.Test) | NINA's `ASICamera` connected (SDK `1, 41, 0, 0`, cooler off at connect), took a 1 s bin-2 light through `StartExposure` / `DownloadExposure` and NINA's FITS writer: 1920 x 1080, `BAYERPAT=RGGB`, `ROWORDER=TOP-DOWN`, `XPIXSZ=5.8`, `GAIN=200`, `OFFSET=3`, `CCD-TEMP=16.5`. Siril 1.4.4 read "RGGB from header, top-down from header" and debayered it (R/G/B medians 340). `ASI_COOLER_ON` read back 0 at the end |
| `AsiFrameHardwareTest` (Image.Test) | A real bin-2 frame saved through `NINA.Image` into NINA's dated `LIGHT` folder; Siril debayered means R 348.2 / G 347.9 / B 347.9 (cap on). Cooler off on exit |

Notes for later: the FITS header still says `SWCREATE='N.I.N.A. 3.3.0.1064 (x64)'` on arm64 (branding and architecture for M9), and NINA's connect-time defaults log failed writes for `ASI_GAMMA`, `ASI_OVERCLOCK`, `ASI_PATTERN_ADJUST` and `ASI_ANTI_DEW_HEATER` (harmless warnings, but check the dew heater control before relying on it).
