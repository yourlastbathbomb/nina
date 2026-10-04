# macOS port (fork-only)

Native Apple Silicon (osx-arm64) build of N.I.N.A. for one rig: ZWO ASI585MC Pro, Meade LX200GPS (alt-az) with the #1209 focuser, M1 MacBook Air. The full plan is in `../../MAC_PORT_PLAN.md`, outside the repo.

Upstream projects are not modified for the port. The projects here compile upstream `.cs` files by link. The only upstream edits are small portability fixes that do nothing on Windows, kept small enough to offer upstream.

## Layout

| Path | What |
|---|---|
| `NINA.Mac.slnx` | macOS solution (net10.0, osx-arm64) |
| `src/NINA.Mac.Native` | `NativeLibraries`: a `DllImport` resolver mapping upstream Windows DLL names to bundled dylibs. It replaces `DllLoader` on macOS |
| `src/NINA.Mac.ZwoProbe` | **M1** `zwoprobe` console: upstream `ASICameraDll.cs` plus the resolver |
| `tests/NINA.Mac.Test` | NUnit: ZWO struct offsets on arm64 and the probe's FITS writer |
| `scripts/stage-zwo.sh` | Copies ZWO SDK arm64 dylib + Homebrew libusb into `native/stage`, fixes install names, re-signs |
| `native/` | Vendor SDKs and staged dylibs, **not committed** |
| `dotnet` | Wrapper for the user-local .NET 10 SDK in `~/.dotnet` |

## One-time setup

1. .NET SDK 10.0.4xx arm64 in `~/.dotnet` (`dotnet-install.sh --channel 10.0`).
2. `brew install libusb`
3. Get ZWO's `ASI_Camera_SDK.zip` (zwoastro.com › Software › Product SDK). It contains `ASI_linux_mac_SDK_V1.41.tar.bz2`. Then:

```bash
mac/scripts/stage-zwo.sh path/to/ASI_linux_mac_SDK_V1.41.tar.bz2
```

## Build and test

```bash
mac/dotnet build mac/NINA.Mac.slnx
```

```bash
mac/dotnet test mac/NINA.Mac.slnx
```

## M1: camera probe

`zwoprobe` lives at `mac/src/NINA.Mac.ZwoProbe/bin/Debug/net10.0/osx-arm64/zwoprobe`. Frames go to `~/Astro/NINA/probe` unless you pass `--out`.

| Step | Command | Pass when |
|---|---|---|
| Layout and SDK | `zwoprobe selftest` | all `ok`, SDK `1, 41` |
| Camera info | `zwoprobe info` | 3840 x 2160, colour RGGB, 2.9 µm, bins include 2, cooler true, control minimums not all 0 |
| Cooling (12 V on) | `zwoprobe cool --temp 0 --hold 2` | reaches 0 °C and holds ±0.5 °C |
| Bin-2 frame | `zwoprobe snap --exp 2 --bin 2 --gain 252` | 1920 x 1080 FITS with `BAYERPAT=RGGB`, `ROWORDER=TOP-DOWN`, `XPIXSZ=5.8` |
| Colour in Siril | light the bare sensor with something red, then blue; snap each; in Siril run `convert <name> -debayer`, then `stat` | the right channel dominates |
| Endurance | `zwoprobe loop --exp 30 --bin 2 --minutes 30 --temp 0` | 0 failed, 0 stalled; per-frame CSV next to the frames |

`synth` writes a synthetic RGGB frame (no camera needed). Siril 1.4.4 reads it as "RGGB, top-down" and debayers it to R 40000, G 10000, B 2000.
