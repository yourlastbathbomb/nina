# NINA.Mac.Platform: macOS services for a laptop at the scope

This is a class library, P/Invoke only (IOKit, CoreFoundation, libobjc), with no UI or engine dependencies.

| Type | What | How it is verified |
|---|---|---|
| `PowerAssertion` | One IOKit assertion (`IOPMAssertionCreateWithName` / `IOPMAssertionRelease`): `PreventUserIdleSystemSleep` or `PreventUserIdleDisplaySleep` | Tests read `pmset -g assertions` and `IOPMCopyAssertionsByProcess`: listed under this pid while held, gone after `Dispose` |
| `ProcessActivity` | `-[NSProcessInfo beginActivityWithOptions:reason:]` / `endActivity:` through `objc_msgSend`. Prevents App Nap | `NSActivityUserInitiated` shows in `pmset` as a `PreventUserIdleSystemSleep` named after the reason. The App-Nap-only variant (`…AllowingIdleSystemSleep`) is checked for begin/end only, because macOS has no unprivileged way to read App Nap state |
| `MacKeepAwake` | Session keep-awake combining the above. Idempotent; adjusts when options change | `pmset` before, during and after, including a display-only release |
| `MacPowerSource` | `IOPSCopyPowerSourcesInfo`: AC/battery, %, charging, time to empty | Compared with `pmset -g batt` |
| `ResourcePaths` | Locates read-only data and dylibs from `AppContext.BaseDirectory`, never from the current directory. Bundle: `Contents/Resources`, `Contents/Frameworks`, then `Contents/MacOS`. Build output: next to the assemblies | Fake bundles in a temp folder, with the current directory set to `/`; the packaged app cross-checks against `NSBundle.mainBundle` |
| `UserDataPaths` | Images in `~/Astro/<app>`, settings in `~/Library/Application Support/<app>`, logs in `~/Library/Logs/<app>`, caches in `~/Library/Caches/<bundle id>`. Warns when images would land in iCloud-synced folders | Unit tests with a fake home |
| `SerialPorts` | `/dev/cu.*`, with USB adapters (`cu.usbserial-*`, `cu.usbmodem*`) first | Fake `/dev` |
| `MacBundle` | `NSBundle.mainBundle` path, resource path and identifier | Test host and packaged app |

Limits: closing the lid on battery still sleeps the Mac (macOS offers no API for that). `kIOPMAssertionTypePreventSystemSleep` only works on AC and is not used.

Tests live in `mac/tests/NINA.Mac.App.Test/Platform/`. They take real, short-lived assertions on this Mac. Build with `mac/dotnet build mac/src/NINA.Mac.Platform/NINA.Mac.Platform.csproj`.
