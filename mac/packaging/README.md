# mac/packaging: build the .app

```bash
mac/packaging/package-app.sh                  # -> mac/artifacts/Nightglass.app (<AppDisplayName>.app, gitignored)
mac/packaging/package-app.sh --zip            # also mac/artifacts/Nightglass-<version>-arm64.zip (see "The zip" below)
mac/packaging/package-app.sh --skip-gui-smoke # no window on screen (no GUI session, display asleep)
mac/packaging/package-app.sh --skip-smoke     # neither smoke test
```

What `package-app.sh` does:

1. Reads the identity from `NINA.Mac.App.csproj` (`AppDisplayName`, `AppShortName`, `AppExecutableName`, `AppBundleId`, `Version`, `AppMinimumSystemVersion`, `NinaBaseVersion`). It refuses any of the four names that contains "NINA" once spaces and punctuation are removed ("N I N A", "local.nina.mac"). The csproj's `CheckAppIdentity` target and the smoke test apply the same rule; the smoke test also checks the packaged Info.plist names and the executable.
2. Runs `dotnet publish -c Release -r osx-arm64 --self-contained` into `mac/artifacts/publish/osx-arm64`.
3. Assembles the bundle:

   | Location | Contents |
   |---|---|
   | `Contents/MacOS/` | The apphost, renamed to `AppExecutableName`, plus the .NET runtime and managed assemblies. .NET finds them next to the executable |
   | `Contents/Frameworks/` | Every `*.dylib` from `mac/native/stage`, read-only: today ZWO `libASICamera2`, `libusb`, `libsofa` and `libnovas31`. Install ids are set to `@rpath/<name>` and sibling references to `@loader_path/<name>`. The executable gets `LC_RPATH @executable_path/../Frameworks` |
   | `Contents/Resources/` | `JPLEPH` (from `mac/native/ephemeris`), `Database/` (NINA's catalogue SQL scripts, from the publish output), `LICENSE.txt` (MPL-2.0), `THIRD-PARTY-NOTICES.txt`, `licenses/`, `AppIcon.icns` (placeholder drawn by `bundle_tools.py`) |
   | Engine links | The app references the whole headless engine (`NINA.Mac.App.Engine`). NINA's NOVAS opens `<BaseDirectory>/External/JPLEPH` and the catalogue database is built from `<BaseDirectory>/Database`, with BaseDirectory = `Contents/MacOS`, so `MacOS/External/JPLEPH -> ../../Resources/JPLEPH` and `MacOS/Database -> ../Resources/Database` are relative symlinks (codesign seals them as links; `--verify --deep --strict` passes). The staged vendor dylibs and licence texts that the publish output also carries are removed from `MacOS/`: they ship once, in `Frameworks` and `Resources/licenses` |
   | `Contents/` | `Info.plist`, written with plistlib and checked with `plutil -lint` |

   Licences (`bundle_tools.py native-licenses` and `notices`):

   | Component | What ships in `Resources/licenses/` and the notices |
   |---|---|
   | Every NuGet package in `deps.json` | Licence, copyright and the package's own licence/notice files. Fonts embedded in package assemblies are listed with their own licence: Inter (Avalonia.Fonts.Inter) is SIL OFL 1.1, Roboto (in Avalonia) is Apache-2.0 |
   | `libASICamera2.dylib` | `ZWO-ASI-SDK-LICENSE.txt` (MIT-style, from `mac/native/stage`) |
   | `libusb-1.0.0.dylib` | `libusb-LGPL-2.1.txt` and `libusb-AUTHORS.txt` from the Homebrew keg; the notice states the version (only when the bundled copy's code matches that keg) and the copyright holders |
   | `libsofa.dylib` | `SOFA-LICENSE.txt` and a notice with the statement clause 3(a) of the SOFA licence requires |
   | `libnovas31.dylib` | `NOVAS-C3.1-README.txt` (NOVAS has no licensing requirements) and the USNO acknowledgement its README asks for |
   | `JPLEPH` | An attribution to JPL (DE421 per the file header), with the DE421 reference |
   | `Accord.Imaging.dll`, `NINA.Mac.ImageAnalysis.Accord.dll` | `Accord-LGPL-2.1.txt`: the LGPL-2.1 text from upstream's `NINA/3rd-party-licenses.txt` with a statement naming both assemblies (built from source, so `deps.json` does not list them as packages) |

   Every `*.dylib` copied from `mac/native/stage` needs an entry in `NATIVE_LICENSES` in `bundle_tools.py`. **Packaging stops on a dylib without one**, so a newly staged library cannot ship without its notice.

4. Thins the universal NuGet natives (SkiaSharp, HarfBuzzSharp, AvaloniaNative) to arm64.
5. Gives the executable its own `LC_UUID`. Every .NET apphost ships with the same one (`F4FCC140-…`), which TN3179 warns about for local-network privacy.
6. Signs ad hoc, inside-out (`bundle_tools.py sign`): every file in `Frameworks` and `MacOS`, then the bundle, with identifier `AppBundleId`. Managed `.dll` and `.json` files must be signed too, because codesign treats all of `Contents/MacOS` as code. Their signatures live in extended attributes, so copy the app with `ditto` or `cp -p`. Entitlements a file already has are kept (`--preserve-metadata=entitlements`): the runtime's `createdump` needs `com.apple.security.cs.debugger`, or it cannot read a crashed process and no crash dump is written.
7. Verifies the bundle: `codesign --verify --deep --strict`; the engine assemblies, `Resources/JPLEPH` and `Resources/Database` exist and the two `MacOS` links resolve; no staged dylib is duplicated in `MacOS`; every Mach-O is arm64 and signed; every non-system dependency resolves inside the bundle; no quarantine attribute; the required files exist; every Frameworks dylib and `JPLEPH` has a notice; every published Mach-O with entitlements still has them. `spctl` is printed for information only, since it always rejects ad-hoc builds.
8. Runs `Contents/MacOS/<exe> --smoke-test` from CWD `/` and reports its wall time. Among its checks:
   - The engine check composes the Real device services in a temporary folder without opening any device, and passes `EngineData.EnsureAvailable` through the bundle's links.
   - The preflight check runs the same checks as `--preflight` (no devices) against the user's settings and prints every line. Only its bundle checks (engine assemblies, engine data with a live NOVAS read, native libraries, the ZWO SDK loading) or a check that crashes fail the smoke test. A missing ASTAP on the build machine is shown, not failed. The smoke test renders every screen on Avalonia's headless platform, and in a child process (`--startup-check`) it also sets up the real Avalonia.Native platform the way a Finder launch does and lays out the main window without showing it. That catches a missing rendering or text-shaping backend (the headless platform registers its own text shaper, so headless rendering alone cannot). With every display asleep the startup check can only inspect the builder and says `partial:`.
9. Runs `Contents/MacOS/<exe> --gui-smoke` from CWD `/`: a real launch with the real services. The main window appears for about 3 seconds without taking focus; the app checks the native window, waits for compositor frames, renders every page with the real renderer, then quits by itself. Packaging fails if it does not exit 0. It needs a logged-in GUI session with the display awake; `--skip-gui-smoke` skips it.

## The zip

`--zip` builds the archive with `ditto -c -k --sequesterRsrc --keepParent`. Extended attributes, which hold the signatures of the managed files in `Contents/MacOS`, go under `__MACOSX/` instead of as `._name` files next to each file. The script checks that no AppleDouble entry sits inside the bundle and that a `ditto -x -k` extraction passes `codesign --verify --deep --strict`.

**Extract it with Finder (double-click) or `ditto -x -k`.** `unzip` and most other tools drop the extended attributes, so the managed files lose their signatures and `codesign --verify` fails. With `--sequesterRsrc` they no longer leave `._*.dylib` files in `Contents/Frameworks`, and `ResourcePaths.VendorLibraries()` skips such files anyway.

Requirements: `mac/dotnet` (.NET 10 SDK) and the Xcode command line tools (codesign, install_name_tool, otool, lipo, sips, iconutil, plutil, dwarfdump, /usr/bin/python3). The libusb licence and AUTHORS come from the Homebrew `libusb` keg; the NOVAS README from `NOVAS31/NOVAS31/README.txt` in this repository.

Tests: `mac/tests/NINA.Mac.App.Test/Packaging/PackagingToolsTests.cs` runs `bundle_tools.py` on small inputs (unknown dylib stops packaging, every staged dylib gets its notice, `sign` keeps createdump's entitlements, embedded fonts are listed).

Not done (personal build): Developer ID signing, hardened runtime, entitlements (`com.apple.security.cs.allow-jit` would be required) and notarization. MAC_PORT_PLAN.md §2 only needs these for distribution.
