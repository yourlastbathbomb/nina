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
| `scripts/build-astrometry-natives.sh` | Builds arm64 `libsofa.dylib` / `libnovas31.dylib` from the repo's SOFA and NOVAS C sources into `native/stage`; `--selftest` runs the vendor test programs |
| `src/Engine.props`, `src/NINA.Mac.WpfCompat`, `src/NINA.Core.Mac`, `src/NINA.Profile.Mac`, `src/NINA.Astrometry.Mac` | **M3** headless engine: upstream Core, Profile and Astrometry compiled by link, plus WPF stand-ins. See `src/README-engine.md` |
| `tests/NINA.Mac.Engine.Test`, `tests/NINA.Mac.Astrometry.Test` | NUnit: WPF oracle for the stand-ins; SOFA/NOVAS smoke tests, 50 upstream `NINA.Test` fixtures, J2000/JNow, sidereal time and the M42 database search |
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

## M2: mount probe

`lx200probe` talks LX200 to the LX200GPS byte by byte (9600 8N1, no handshake) and logs every byte with a timestamp. It refuses `:hP#` (park: the Autostar goes silent until it is power-cycled) and the other blocklisted commands (`.blocked` in the raw console lists them with reasons). It asks before anything moves or is written, and Ctrl+C sends `:Q#` and `:FQ#`. The library (`src/NINA.Mac.Lx200`), simulator (`src/NINA.Mac.Lx200.Sim`, `lx200sim`) and tests (`tests/NINA.Mac.Lx200.Test`) are not in `NINA.Mac.slnx` yet, so build them by project:

```bash
mac/dotnet build mac/src/NINA.Mac.Lx200Probe/NINA.Mac.Lx200Probe.csproj
mac/dotnet test mac/tests/NINA.Mac.Lx200.Test/NINA.Mac.Lx200.Test.csproj
alias lx200probe='mac/dotnet mac/src/NINA.Mac.Lx200Probe/bin/Debug/net10.0/osx-arm64/lx200probe.dll'
alias lx200sim='mac/dotnet mac/src/NINA.Mac.Lx200.Sim/bin/Debug/net10.0/osx-arm64/lx200sim.dll'
```

The aliases run the dlls through the wrapper. The `lx200probe` apphost next to the dll also works once `DOTNET_ROOT=~/.dotnet` is set. Each run writes `results.md` and `trace.log` to `~/Astro/NINA/m2-bench/<timestamp>` unless you pass `--out`.

Before the bench session: do a quick handbox alignment indoors, set High Precision off, plug in the #1209, clear the scope's swing, and quit KStars (macOS opens the port exclusively).

| Step | Command | Pass when |
|---|---|---|
| Dry run, no mount | `lx200probe checklist --sim` | all 8 steps `Done`; results.md reads sensibly (it is marked as a simulator run) |
| Find the cable | `lx200probe ports` | a `/dev/cu.usbserial-*` line marked "use this path" |
| Link check | `lx200probe raw --port /dev/cu.usbserial-XXXX`, then `ack`, `:GVP#`, `:GVN#`, `:GW#`, `.quit` | ACK answers `A`, `:GVP#` is `LX2001`, no `TRAILING` bytes |
| Bench checklist | `lx200probe checklist --port /dev/cu.usbserial-XXXX` | steps 1-3 and 5-8 `Done`; answer the focuser questions as you watch the drawtube |
| Pulse axes | if step 6 says the axes are ambiguous: point 30° or more east or west of south, then `lx200probe checklist --port ... --skip 1,2,3,4,5,7,8` | step 6 names one axis per direction |
| Date convention (bench only, destroys the alignment) | `lx200probe checklist --port ... --date-test --skip 1,2,3,5,6,7,8`, then type `DESTROY` | step 4 says `VERDICT: ... UTC` or `LOCAL`, and the restore line shows LST within a few seconds; re-align before any goto |
| Without hardware, through System.IO.Ports | terminal 1: `lx200sim pty` (prints `/dev/ttysNNN`); terminal 2: `lx200probe checklist --port /dev/ttysNNN` | same results as `--sim` |

The results folder settles the dither strategy (step 6: native `:Mg`, host-timed `:RG#` + `:Mx#`/`:Qx#`, or goto offset), the `:SC` date flag (step 4) and the focus method (step 7: `:FP` pulse or host-timed `:F+#`/`:F-#` + `:FQ#`). Send the whole folder back; `trace.log` holds the raw bytes. `lx200sim quirks` lists the switches that make the simulator behave like other firmware (`--sim-quirk pulse=ignored`, `date=local`, `degree=star`, ...).

## M3: engine on macOS

Upstream `NINA.Core`, `NINA.Profile` and `NINA.Astrometry` build for osx-arm64 from upstream sources by link, with `NINA.Mac.WpfCompat` standing in for the WPF types they use. `src/README-engine.md` covers how the projects are put together, the upstream edits (Windows no-ops) and the known gaps. These projects are not in `NINA.Mac.slnx` yet, so build and test them by project:

```bash
mac/scripts/build-astrometry-natives.sh      # once: libsofa.dylib + libnovas31.dylib into native/stage
mac/dotnet build mac/src/NINA.Astrometry.Mac/NINA.Astrometry.Mac.csproj
mac/dotnet test mac/tests/NINA.Mac.Engine.Test/NINA.Mac.Engine.Test.csproj
mac/dotnet test mac/tests/NINA.Mac.Astrometry.Test/NINA.Mac.Astrometry.Test.csproj
```

NOVAS also needs the JPL DE421 ephemeris: put the `JPLEPH` file from nina.external in `mac/native/ephemeris/JPLEPH`. Builds copy it to `External/JPLEPH`, next to the SOFA/NOVAS dylibs and the catalogue SQL scripts. Test runs keep logs, profiles and databases in a temp folder.

| Plan check (M3) | Status |
|---|---|
| Core, Profile, Astrometry build | 0 errors; the only warnings are upstream `[Obsolete]` uses |
| J2000 to JNow | Upstream transform tests pass. NINA's SOFA-based JNow agrees with NOVAS's apparent place to better than 0.01 mas |
| Local sidereal time | Upstream sidereal-time tests pass. At 114.18 E it agrees with Meeus's GMST plus longitude within 0.7 s (bound: equation of the equinoxes + UT1-UTC) |
| "M42" search | A database built from the upstream SQL in a temp folder finds NGC1976 "M 42" at 05h35m17.3s -05°23'28". The default database is `~/Library/Application Support/NINA/NINA.sqlite` |
| Upstream tests | 1781 of the 1787 linked upstream cases pass. Five fail on a last-bit `asin` difference between the Windows and macOS C runtimes, one on macOS's shorter list of invalid file-name characters; details in `src/README-engine.md` |
| Image (non-rendering), Equipment, PlateSolving, headless capture into the Siril folder layout | Not started |
