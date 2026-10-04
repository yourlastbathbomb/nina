# mac/packaging: build the .app

```bash
mac/packaging/package-app.sh            # -> mac/artifacts/<AppDisplayName>.app (gitignored)
mac/packaging/package-app.sh --zip      # also mac/artifacts/<AppShortName>-<version>-arm64.zip (ditto keeps the signatures)
mac/packaging/package-app.sh --skip-smoke
```

What `package-app.sh` does:

1. Reads the identity from `NINA.Mac.App.csproj` (`AppDisplayName`, `AppShortName`, `AppExecutableName`, `AppBundleId`, `Version`, `AppMinimumSystemVersion`, `NinaBaseVersion`). It refuses a display name containing "NINA".
2. Runs `dotnet publish -c Release -r osx-arm64 --self-contained` into `mac/artifacts/publish/osx-arm64`.
3. Assembles the bundle:

   | Location | Contents |
   |---|---|
   | `Contents/MacOS/` | The apphost, renamed to `AppExecutableName`, plus the .NET runtime and managed assemblies. .NET finds them next to the executable |
   | `Contents/Frameworks/` | Every `*.dylib` from `mac/native/stage`, read-only: today ZWO `libASICamera2`, `libusb`, `libsofa` and `libnovas31`. Install ids are set to `@rpath/<name>` and sibling references to `@loader_path/<name>`. The executable gets `LC_RPATH @executable_path/../Frameworks` |
   | `Contents/Resources/` | `JPLEPH` (from `mac/native/ephemeris`), `LICENSE.txt` (MPL-2.0), `THIRD-PARTY-NOTICES.txt` (every NuGet package in `deps.json`, the ZWO SDK and libusb), `licenses/` (ZWO MIT-style, libusb LGPL-2.1 + notice), `AppIcon.icns` (placeholder drawn by `bundle_tools.py`) |
   | `Contents/` | `Info.plist`, written with plistlib and checked with `plutil -lint` |

4. Thins the universal NuGet natives (SkiaSharp, HarfBuzzSharp, AvaloniaNative) to arm64.
5. Gives the executable its own `LC_UUID`. Every .NET apphost ships with the same one (`F4FCC140-…`), which TN3179 warns about for local-network privacy.
6. Signs ad hoc, inside-out: every file in `Frameworks` and `MacOS`, then the bundle, with identifier `AppBundleId`. Managed `.dll` and `.json` files must be signed too, because codesign treats all of `Contents/MacOS` as code. Their signatures live in extended attributes, so copy the app with `ditto` or `cp -p`.
7. Verifies the bundle: `codesign --verify --deep --strict`; every Mach-O is arm64 and signed; every non-system dependency resolves inside the bundle; no quarantine attribute; the required files exist. `spctl` is printed for information only, since it always rejects ad-hoc builds.
8. Runs `Contents/MacOS/<exe> --smoke-test` from CWD `/` (as Finder does) and reports its wall time.

Requirements: `mac/dotnet` (.NET 10 SDK) and the Xcode command line tools (codesign, install_name_tool, otool, lipo, sips, iconutil, plutil, dwarfdump, /usr/bin/python3). The libusb licence is taken from Homebrew's `libusb/COPYING`.

Not done (personal build): Developer ID signing, hardened runtime, entitlements (`com.apple.security.cs.allow-jit` would be required) and notarization. MAC_PORT_PLAN.md §2 only needs these for distribution.
