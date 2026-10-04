# NINA.Mac.RigTools

Planning maths for this rig that NINA lacks (`MAC_PORT_PLAN.md` section 6, decisions 4 and 5). It is a small managed library plus a `rigplan` console. It needs no SOFA/NOVAS, no engine projects and no NuGet packages, so it builds and runs on its own. The test project alone adds Accord.Math 3.8.2-alpha and Newtonsoft.Json 13.0.4, the versions upstream uses, for the horizon parity tests.

| Area | Type | What it does |
|---|---|---|
| Field rotation | `Rotation/FieldRotation` | Alt-az field-rotation rate, max sub length for an allowed corner blur, and frame rotation over a session |
| Optics | `Optics/ImagingTrain` | ASI585MC with the native (2500 mm) or reducer (1575 mm) train: pixel scale, FOV, corner radius |
| Time and coordinates | `Astronomy/*` | Julian date, GMST/LST, precession from J2000, alt/az, parallactic angle, transit, culminations, time above an altitude, low-precision Sun, darkness interval |
| Horizon | `Horizon/*` | Parser for NINA `.hrz` files that keeps inline `#` comments, MountWizzard4 `.hpts`, upstream-identical interpolation, and a placeholder "no northern sky" profile |
| Planner | `Planning/*` | Per-target imaging windows for one night, split east and west of the meridian, with max sub, recommended exposure step, zenith-keyhole and other flags, and a text report |
| CLI | `rigplan/` | `rigplan`, `rigplan table`, `rigplan horizon` |

## Build, test, run

Use only the user-local .NET 10 SDK wrapper, and build by project path:

```bash
mac/dotnet build mac/src/NINA.Mac.RigTools/rigplan/rigplan.csproj
mac/dotnet test  mac/tests/NINA.Mac.RigTools.Test/NINA.Mac.RigTools.Test.csproj
```

```bash
B=mac/src/NINA.Mac.RigTools/rigplan/bin/Debug/net10.0/osx-arm64
DOTNET_ROOT=~/.dotnet $B/rigplan                     # tonight: M42 M83 NGC253 M8 M20 Omega Cen
DOTNET_ROOT=~/.dotnet $B/rigplan table               # the section 6 max-sub table
DOTNET_ROOT=~/.dotnet $B/rigplan --date 2026-12-15 --target "M1,05:34:31.9,+22:00:52" --twilight nautical
DOTNET_ROOT=~/.dotnet $B/rigplan horizon --horizon ~/Astro/NINA/horizon.hrz
```

`rigplan --help` lists every option: `--min-alt`, `--max-alt`, `--horizon FILE|flat`, `--bin`, `--reducer [MM]`, `--blur`, `--min-sub`, `--steps`, `--every`, `--no-samples` and `--lat/--lon/--utc`.

The projects are not in `mac/NINA.Mac.slnx`, because this workflow may not edit that file. To add them (not run here):

```bash
mac/dotnet sln mac/NINA.Mac.slnx add mac/src/NINA.Mac.RigTools/NINA.Mac.RigTools.csproj mac/src/NINA.Mac.RigTools/rigplan/rigplan.csproj mac/tests/NINA.Mac.RigTools.Test/NINA.Mac.RigTools.Test.csproj
```

## Status

- The library and CLI build with 0 warnings.
- 162 NUnit tests pass, covering:
  - the `MAC_PORT_PLAN.md` section 6 table;
  - the research tables (alt x az grid, hour-angle table, FOV table, 58–126° session rotation);
  - Meeus examples 7.a, 12.a, 12.b, 13.b, 21.b and 25.a;
  - absolute clock times for one night (see below);
  - horizon parsing (`.hrz` and `.hpts`), including parity with upstream;
  - planner edge cases;
  - the CLI, including exit code 2 for bad arguments and bad horizon files.
- Absolute times are pinned for the night of 2026-10-04 at Deep Water Bay (`AlmanacRegressionTest`, ±5 s): dusk and dawn at −6/−12/−18°, and the NGC 253, M42, M8 and M20 transits and 15° window edges. The expected values come from `tests/NINA.Mac.RigTools.Test/Oracle/almanac_oracle.py`, an independent standard-library Python script. It uses different and more complete models: IAU 2006 sidereal time with the equation of the equinoxes, a Meeus ch. 25 Sun with nutation and aberration, and IAU 2006 precession plus nutation and annual aberration for the targets. Its self-checks reproduce Meeus 12.a, 23.a and 25.a. RigTools agrees with it to 0.1–0.9 s for twilight and 0.2–2.4 s for target edges; the largest gap is the annual aberration RigTools leaves out.
- Hardware isn't involved. No result has been checked against the sky or an external almanac (HKO or USNO).

## The maths, with sources and accuracy

Field rotation:

- **Rate.** On an alt-az mount without a rotator, the frame turns on the sky at the rate the parallactic angle `q` changes. Differentiating Meeus eq. 14.1 gives `dq/dt = -omega cos(lat) cos(Az) / cos(alt)`, with Az measured from north and omega = 7.292115e-5 rad/s (IERS). Its magnitude is the usual 13.92°/h × |cos Az| / cos alt at 22.25°N. A test checks it against a numerical derivative of eq. 14.1.
- **Max sub.** `t = (blur px / r px) / |rate|`. Here `r` is the distance from the frame centre (the rotation centre) to the corner: half the binned diagonal, 1101.45 px at bin 2. One binned pixel of blur corresponds to 187.3″ of rotation.

**The limit doesn't depend on the reducer.** Focal length changes how much sky a pixel covers. It doesn't change the rotation angle or the corner's distance in pixels, so the limit in pixels is identical. The code agrees with the plan, and a test checks 1575 mm and 1617 mm against 2500 mm. The catch is that the reducer's pixels are 1.59 times bigger on the sky, so 1 px of blur is a larger fraction of a star's FWHM.

Reproduced values at 22.25°N, bin 2, 1 px:

| Dec | Transit | ±3 h | ±2 h session rotation |
|---|---|---|---|
| −30 | 10.64 s, alt 37.75° | 16.61 s | 64.8° |
| 0 | 5.09 s, alt 67.75° | 28.72 s | 101.4° |
| +10 | 2.85 s, alt 77.75° | 64.74 s | 126.4° |
| −45 | 12.41 s, alt 22.75° | 15.34 s | 58.0° |

The tests use half the last printed digit as tolerance: ±0.05 s and ±0.5° (±0.05° where the source prints tenths).

| Quantity | Method | Accuracy for planning |
|---|---|---|
| Sidereal time | IAU 1982 GMST (Meeus 12.4), UTC used as UT1, mean rather than apparent | about 2 s of time |
| Catalogue to date | IAU 1976 precession (Meeus 21.2–21.4). Nutation, aberration and proper motion are ignored | about 0.01° |
| Alt/az | Meeus 13.5/13.6, azimuth from north towards east as in NINA and the LX200 protocol. **Geometric, no refraction** (refraction adds ~3.5′ at 15° and ~0.5° at the horizon) | about 0.01° |
| Sun | Astronomical Almanac low-precision formula, 0.01° over 1950–2050. Twilight at −18/−12/−6° as upstream `*TwilightRiseAndSet` | a few seconds of twilight time |
| Edges | 60 s sampling, then each change bisected to 0.5 s | 0.5 s |

This is for planning, not pointing. Gotos and centring belong to the mount, the plate solver and NINA's SOFA/NOVAS path.

## Horizon files

`HorizonFile.ParseStandard` reads NINA's format: an `azimuth altitude` pair per line, separated by tab, space, comma or semicolon. It uses the same grooming as upstream (0 and 360 added) and the same piecewise-linear `Interpolate1D`. Parity tests show identical altitudes to upstream at every 0.25° from −30° to 400°, using a snapshot of upstream `CustomHorizon` and the real Accord.Math 3.8.2-alpha `Interpolate1D`.

How it differs from upstream:

1. **Inline comments work.** Upstream (`NINA.Core/Model/CustomHorizon.cs:97-113`) treats only lines that start with `#` as comments. A line such as `180 4  # over water` therefore has more than two tokens and is dropped, with only a log warning. The research template lost its 60.1° steep edge and its 4° over-water point this way. A test shows the effect: upstream returns 6° at azimuth 180 instead of 4°, and 58.5° at azimuth 61 instead of 24.7°.
2. **Rejected lines are reported** with a line number and reason. `strict: true` throws instead.
3. **Values that make no sense are rejected:** NaN or infinity, azimuth outside 0–360, altitude outside ±90. A repeated azimuth still replaces the earlier one (last wins), and a warning says so.
4. **`.hpts` files** are read as MountWizzard4 JSON with upstream's structure checks, range checks and messages. The parity tests use the same Newtonsoft.Json 13.0.4 as upstream (test-only). Like upstream, a coordinate may be a number or a numeric string (`"10"`, `" 1e1 "`), and comments and trailing commas are allowed. Upstream loads some damaged files without complaint: a truncated file, text after the array, booleans (read as 1/0) and `"NaN"`. These are rejected here. Every failure is an `ArgumentException`, including invalid JSON and an empty file, so `rigplan` prints `rigplan: …` and exits with 2. Upstream instead throws `JsonReaderException`, `InvalidCastException`, `FormatException` or `NullReferenceException`.
5. **`GetAltitude` never reads open sky by accident.** Upstream reduces a tiny negative azimuth (|az| ≤ ~2.8e-14, e.g. from `atan2` or a mount reporting −0.00000x) to `x % 360 + 360`, which rounds to exactly 360.0. `Interpolate1D` then falls off the last knot and returns 0, so the placeholder's 60° northern wall reads as 0° there. NaN and ±infinity also give 0. Here a reduced 360 wraps to 0, and a non-finite azimuth returns the profile's maximum, so a bad azimuth fails closed. Every other finite azimuth gives exactly upstream's value. The planner was never affected, because it normalises azimuths to [0, 360) first.

**Placeholder profile.** `SiteHorizons.DeepWaterBayPlaceholder` is the embedded `Horizon/DeepWaterBay.placeholder.hrz`, shaped like the corrected research Table 7:

- 60° from azimuth 300 through north to 60 (the northern sky is blocked);
- a steep edge to 25°;
- 15° east and west;
- 4° due south over the water.

**The altitudes are not measured.** Measure the real profile at about one point every 15° of azimuth and pass it with `--horizon`. The placeholder keeps its comments on their own lines, so unpatched NINA reads it unchanged (tested). The report flags it as a PLACEHOLDER.

### Proposed upstream patch (not applied; this workflow does not edit upstream files)

It makes three changes. Each is tested on a copy in `tests/NINA.Mac.RigTools.Test/Upstream/UpstreamCustomHorizonSnapshot.cs` (`FromReader_Standard_ProposedPatch`, `GetAltitude_ProposedPatch`):

- **Comments.** Everything from `#` is stripped before splitting. Comment-free files parse exactly as before.
- **Altitude warning.** It logs `columns[1]`, the altitude token, instead of `columns[0]`, the azimuth.
- **`GetAltitude`.** A reduced azimuth of 360 wraps to 0, and a non-finite azimuth returns `GetMaxAltitude()`. Altitudes are unchanged at every 0.25° from −30° to 400°. The non-finite line is a judgement call (fail closed) that upstream may prefer to drop; the wrap fix stands on its own.

`git apply --check` accepts the block below against the current `NINA.Core/Model/CustomHorizon.cs` (unchanged since ee69f27). Suggested PR title: "CustomHorizon: accept inline # comments and fix azimuth wrap at 360".

```diff
--- a/NINA.Core/Model/CustomHorizon.cs
+++ b/NINA.Core/Model/CustomHorizon.cs
@@ -35,7 +35,10 @@
         }
 
         public double GetAltitude(double azimuth) {
+            if (double.IsNaN(azimuth) || double.IsInfinity(azimuth)) { return GetMaxAltitude(); }
             if (azimuth < 0 || azimuth > 359) { azimuth = Utility.CoreUtil.EuclidianModulus(azimuth, 360); }
+            // EuclidianModulus(-1e-14, 360) rounds to exactly 360, past the last knot, where Interpolate1D returns 0
+            if (azimuth >= 360) { azimuth -= 360; }
             return Accord.Math.Tools.Interpolate1D(azimuth, azimuths, altitudes, 0, 0);
         }
 
@@ -78,7 +81,7 @@
         /// Creates an instance of the custom horizon object to calculate horizon altitude based on a given azimuth out of a NINA-standard file that specifies the horizon
         /// The Horizon file must consist of a list of azimuth and alitutde pairs that are separated by a space and line breaks
         /// A minimum of two points are required to approximate the horizon
-        /// Lines starting with '#' character will be treated as comments and therefore ignored
+        /// Everything from a '#' character to the end of the line is a comment, so comments may start a line or follow a point
         /// </summary>
         /// <example>
         /// # File Example
@@ -94,16 +97,21 @@
             var horizonMap = new SortedDictionary<double, double>();
 
             string line;
-            while ((line = sr.ReadLine()?.Trim()) != null) {
-                // Lines starting with # are comments
-                if (!line.StartsWith("#") && !string.IsNullOrEmpty(line)) {
+            while ((line = sr.ReadLine()) != null) {
+                // '#' starts a comment, on its own line or after a point ("180 4 # over water")
+                var commentStart = line.IndexOf('#');
+                if (commentStart >= 0) {
+                    line = line.Substring(0, commentStart);
+                }
+                line = line.Trim();
+                if (!string.IsNullOrEmpty(line)) {
                     var columns = line.Split(new char[] { '\t', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                     if (columns.Length == 2) {
                         if (double.TryParse(columns[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var azimuth)) {
                             if (double.TryParse(columns[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var altitude)) {
                                 horizonMap[azimuth] = altitude;
                             } else {
-                                Logger.Warning($"Invalid value for altitude {columns[0]}");
+                                Logger.Warning($"Invalid value for altitude {columns[1]}");
                             }
                         } else {
                             Logger.Warning($"Invalid value for azimuth {columns[0]}");
```

## Planner semantics

`NightPlanner.Plan(eveningDate, targets, options)` covers the darkness interval that starts on that local evening, searched from local noon to the next noon. An instant counts as usable when all of these hold:

- altitude ≥ the horizon profile at that azimuth;
- altitude ≥ `MinAltitudeDeg` (default 15°);
- altitude ≤ `MaxAltitudeDeg` (default 75°, decision 5);
- the rotation limit ≥ `MinSubSeconds`, but only if that option is set. By default the planner warns and doesn't clamp (decision 4).

Each window reports:

- the meridian side;
- what opened and closed it;
- start, end and peak altitude;
- the worst max sub and when it happens;
- one recommended fixed exposure: the longest of the 5/10/20/30 s dark-library steps that fits the worst case;
- the frame-rotation span.

Flags:

| Flag | Meaning |
|---|---|
| `NeverRises` | Dec below −67.75° at this site |
| `Circumpolar` | The target never sets |
| `BlockedByHorizon` | It rises but never clears the profile and minimum altitude at any hour angle, e.g. Polaris or Dec +75 behind the northern wall |
| `NotUpInDarkness` | It clears the limits at some hour angle, but not during tonight's darkness |
| `PassesZenithKeyhole` | Transit altitude is above the maximum. Objects near Dec +22° pass overhead. The keyhole times are given, and the windows stop and restart at the 75° edges |
| `RotationLimited` | No exposure step fits somewhere in a window, e.g. Dec +5 at transit allows ~4 s |
| `NoDarkness` | The Sun never gets low enough that night |

## Not done / open

- No Moon (phase and separation), and no refraction. Altitudes are geometric.
- The site has a fixed UTC offset, which is fine for Hong Kong. A site with daylight saving would need `TimeZoneInfo`.
- Absolute times are checked against an independent script, not against a published almanac. A one-night spot check against HKO or USNO tables would close that gap.
- The horizon profile is a placeholder until it is measured. A capture tool (read `:GA#`/`:GZ#` from the mount and append a point) belongs to the LX200 and UI work.
- Engine integration still to do:
  - a `FieldRotationMaxExposure` symbol for the sequencer;
  - a MaxAltitude condition and slew guard;
  - feeding the patched horizon into AboveHorizonCondition.
- `mac/src/NINA.Mac.App/Astro/SkyMath.cs`, written in a parallel workflow, has its own GMST, alt/az, field-rotation and Sun code. One copy should be kept. This library is the one tested against Meeus and the plan tables.
