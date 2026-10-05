#!/bin/bash
# Copyright © 2016 - 2026 Stefan Berg and the N.I.N.A. contributors. MPL-2.0: http://mozilla.org/MPL/2.0/
#
# Build the macOS app bundle: self-contained osx-arm64 publish -> "<AppDisplayName>.app" (Nightglass.app) in
# mac/artifacts/, vendor dylibs in Contents/Frameworks, data in Contents/Resources, ad-hoc signed inside-out, then
# verified without spctl (an ad-hoc build is never Gatekeeper-approved) and smoke-tested from CWD "/": headlessly
# (--smoke-test), then for real (--gui-smoke: the main window shows for a few seconds and the app quits by itself).
#
#   mac/packaging/package-app.sh [--skip-smoke] [--skip-gui-smoke] [--zip]
#
#   --skip-gui-smoke  skip only the on-screen check (no GUI session, display asleep, or nobody should see a window)
#   --skip-smoke      skip both smoke tests
#
# Needs: the user-local .NET 10 SDK (mac/dotnet), Xcode command line tools (codesign, install_name_tool, otool,
# lipo, sips, iconutil, plutil, python3). Reads mac/native/stage and mac/native/ephemeris; never writes there.
set -euo pipefail

skip_smoke=0
skip_gui_smoke=0
make_zip=0
for arg in "$@"; do
    case "$arg" in
        --skip-smoke) skip_smoke=1; skip_gui_smoke=1 ;;
        --skip-gui-smoke) skip_gui_smoke=1 ;;
        --zip) make_zip=1 ;;
        *) echo "usage: $0 [--skip-smoke] [--skip-gui-smoke] [--zip]" >&2; exit 2 ;;
    esac
done

here="$(cd "$(dirname "$0")" && pwd)"
mac="$(cd "$here/.." && pwd)"
nina="$(cd "$mac/.." && pwd)"
dotnet="$mac/dotnet"
proj="$mac/src/NINA.Mac.App/NINA.Mac.App.csproj"
artifacts="$mac/artifacts"
tools="$here/bundle_tools.py"
stage="$mac/native/stage"
ephemeris="$mac/native/ephemeris/JPLEPH"
nuget="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
export AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

step() { printf '\n== %s\n' "$*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

for tool in codesign install_name_tool otool lipo sips iconutil plutil python3 dwarfdump; do
    command -v "$tool" >/dev/null || fail "$tool not found (install the Xcode command line tools)"
done

step "App identity (from NINA.Mac.App.csproj)"
props_json="$("$dotnet" msbuild "$proj" -nologo -p:Configuration=Release \
    -getProperty:AppDisplayName -getProperty:AppShortName -getProperty:AppExecutableName \
    -getProperty:AppBundleId -getProperty:Version -getProperty:AppMinimumSystemVersion -getProperty:NinaBaseVersion)"
prop() { python3 -c 'import json,sys; print(json.loads(sys.argv[1])["Properties"][sys.argv[2]])' "$props_json" "$1"; }
display="$(prop AppDisplayName)"
short="$(prop AppShortName)"
exe="$(prop AppExecutableName)"
bundle_id="$(prop AppBundleId)"
version="$(prop Version)"
min_os="$(prop AppMinimumSystemVersion)"
nina_base="$(prop NinaBaseVersion)"
echo "  $display ($short) $version, $bundle_id, macOS >= $min_os, based on N.I.N.A. $nina_base"
# Same rule as CheckAppIdentity in the csproj and AppInfo.ContainsNina: letters only, any case
for name in "$display" "$short" "$exe" "$bundle_id"; do
    [[ "$(printf '%s' "$name" | LC_ALL=C tr -cd 'A-Za-z' | tr '[:lower:]' '[:upper:]')" != *NINA* ]] ||
        fail "'$name' contains NINA (spaces and punctuation ignored); the fork needs its own name"
done

step "Publish (Release, osx-arm64, self-contained)"
publish="$artifacts/publish/osx-arm64"
rm -rf "$publish"
"$dotnet" publish "$proj" -c Release -r osx-arm64 --self-contained true -o "$publish" \
    -p:DebugType=embedded -p:GenerateDocumentationFile=false -nologo -v:minimal
[[ -x "$publish/NINA.Mac.App" ]] || fail "publish produced no apphost"

step "Assemble $display.app"
app="$artifacts/$display.app"
contents="$app/Contents"
rm -rf "$app"
mkdir -p "$contents/MacOS" "$contents/Frameworks" "$contents/Resources/licenses"
cp -R "$publish/." "$contents/MacOS/"
mv "$contents/MacOS/NINA.Mac.App" "$contents/MacOS/$exe"
find "$contents/MacOS" -name '*.pdb' -delete

# Vendor dylibs (ZWO SDK, libusb; later libsofa/libnovas): flat in Contents/Frameworks
shopt -s nullglob
vendor=("$stage"/*.dylib)
shopt -u nullglob
if [[ ${#vendor[@]} -eq 0 ]]; then
    echo "  WARNING: no dylibs in $stage (run mac/scripts/stage-zwo.sh); the camera will not load"
fi
for lib in "${vendor[@]}"; do
    cp "$lib" "$contents/Frameworks/"
done
for lib in "$contents/Frameworks"/*.dylib; do
    [[ -e "$lib" ]] || continue
    name="$(basename "$lib")"
    chmod u+w "$lib"
    install_name_tool -id "@rpath/$name" "$lib" 2>/dev/null
    # References to sibling vendor libs must resolve inside Frameworks
    while read -r dep; do
        base="$(basename "$dep")"
        if [[ -f "$contents/Frameworks/$base" && "$dep" != "@loader_path/$base" && "$dep" != "@rpath/$base" ]]; then
            install_name_tool -change "$dep" "@loader_path/$base" "$lib" 2>/dev/null
        fi
    done < <(otool -L "$lib" | tail -n +2 | awk '{print $1}')
    if otool -L "$lib" | tail -n +3 | awk '{print $1}' | grep -q '^@rpath/' && ! otool -l "$lib" | grep -A2 LC_RPATH | grep -q '@loader_path$'; then
        install_name_tool -add_rpath @loader_path "$lib" 2>/dev/null
    fi
    echo "  Frameworks/$name: $(lipo -archs "$lib"); $(otool -L "$lib" | tail -n +2 | awk '{print $1}' | grep -v '^/usr/lib\|^/System' | tr '\n' ' ')"
done

# arm64 only (MAC_PORT_PLAN.md: no Rosetta): thin universal NuGet natives (SkiaSharp, HarfBuzzSharp, AvaloniaNative)
thinned=0
while IFS= read -r -d '' f; do
    if file -b "$f" | grep -q 'universal binary' && lipo -archs "$f" | grep -qw arm64; then
        lipo -thin arm64 "$f" -output "$f.thin" && mv "$f.thin" "$f"
        thinned=$((thinned + 1))
    fi
done < <(find "$contents/MacOS" "$contents/Frameworks" -type f -name '*.dylib' -print0)
echo "  thinned $thinned universal dylib(s) to arm64"

# Main executable: find Frameworks through @rpath, and get a UUID of its own (all .NET apphosts share one)
main="$contents/MacOS/$exe"
if ! otool -l "$main" | grep -A2 LC_RPATH | grep -q '@executable_path/../Frameworks'; then
    install_name_tool -add_rpath @executable_path/../Frameworks "$main" 2>/dev/null
fi
template_uuid="$(dwarfdump --uuid "$main" | awk '{print $2}')"
new_uuid="$(python3 "$tools" set-uuid "$main" "$bundle_id/$version")"
echo "  LC_UUID $template_uuid (shared .NET apphost) -> $new_uuid"

# Data and licences
if [[ -f "$ephemeris" ]]; then
    cp "$ephemeris" "$contents/Resources/JPLEPH"
else
    echo "  WARNING: $ephemeris missing; JPLEPH not bundled"
fi
cp "$nina/LICENSE.txt" "$contents/Resources/LICENSE.txt"
# Every vendor dylib in Frameworks (and JPLEPH) needs an entry in bundle_tools.py NATIVE_LICENSES: it copies the
# licence into Resources/licenses and writes the notice (SOFA clause 3(a) statement, NOVAS/JPL acknowledgements,
# libusb version and copyright holders). An unknown dylib stops the build instead of shipping without a notice.
native_notices="$(python3 "$tools" native-licenses "$contents/Frameworks" "$stage" "$nina" "$contents/Resources")" ||
    fail "licence notices for the vendor libraries (see above)"
extra_notices=()
while IFS= read -r line; do
    if [[ -n "$line" ]]; then
        extra_notices+=("$line")
        echo "  licence: ${line%%=*}"
    fi
done <<< "$native_notices"
python3 "$tools" notices "$publish/NINA.Mac.App.deps.json" "$nuget" "$contents/Resources/THIRD-PARTY-NOTICES.txt" ${extra_notices[@]+"${extra_notices[@]}"} \
    | sed 's/^/  notices: /'
python3 "$tools" icon "$contents/Resources/AppIcon.icns"

python3 "$tools" plist "$contents/Info.plist" \
    CFBundleDevelopmentRegion=en \
    "CFBundleDisplayName=$display" \
    "CFBundleExecutable=$exe" \
    CFBundleIconFile=AppIcon \
    "CFBundleIdentifier=$bundle_id" \
    CFBundleInfoDictionaryVersion=6.0 \
    "CFBundleName=$short" \
    CFBundlePackageType=APPL \
    "CFBundleShortVersionString=$version" \
    "CFBundleVersion=$version" \
    LSApplicationCategoryType=public.app-category.utilities \
    "LSMinimumSystemVersion=$min_os" \
    NSHighResolutionCapable=true \
    NSSupportsAutomaticGraphicsSwitching=true \
    "NSHumanReadableCopyright=Based on N.I.N.A. $nina_base, © 2016-2026 Stefan Berg and the N.I.N.A. contributors. MPL-2.0."
plutil -lint "$contents/Info.plist" >/dev/null || fail "Info.plist is invalid"
printf 'APPL????' > "$contents/PkgInfo"
xattr -cr "$app"

step "Ad-hoc sign, inside-out"
# Hardened runtime is off on purpose: ad-hoc signatures have no team ID, so library validation would reject the
# vendor dylibs, and CoreCLR would need allow-jit. Distribution builds need a Developer ID (see README.md).
# codesign treats every file in Contents/MacOS as nested code, and .NET keeps its managed assemblies, deps.json and
# runtimeconfig.json next to the apphost (it finds them relative to the executable). So every file there is signed
# individually, as Avalonia's macOS deployment guide does; non-Mach-O files carry the signature in extended
# attributes (copy the bundle with ditto or cp -p, not tools that drop xattrs).
# Entitlements a file already has are kept: the runtime's createdump needs com.apple.security.cs.debugger to write
# crash dumps (a plain re-sign drops it and createdump fails in task_for_pid).
signed="$(python3 "$tools" sign --skip "$main" "$contents/Frameworks" "$contents/MacOS")" || fail "signing nested code"
codesign --force --sign - --timestamp=none --identifier "$bundle_id" "$app" 2>/dev/null || fail "codesign the bundle"
echo "  $signed in MacOS/Frameworks, then the bundle (identifier $bundle_id)"

step "Verify (no spctl: Gatekeeper never approves ad-hoc builds)"
verify_out="$(codesign --verify --deep --strict --verbose=2 "$app" 2>&1)" || { echo "$verify_out"; fail "codesign --verify --deep --strict"; }
echo "$verify_out" | tail -2 | sed 's/^/  /'
codesign -dv "$app" 2>&1 | grep -E '^(Identifier|Format|Signature)' | sed 's/^/  /'
problems=0
while IFS= read -r -d '' f; do
    file -b "$f" | grep -q 'Mach-O' || continue
    rel="${f#"$contents/"}"
    archs="$(lipo -archs "$f" 2>/dev/null || echo '?')"
    if [[ "$archs" != "arm64" ]]; then
        echo "  BAD arch $archs: $rel"; problems=$((problems + 1))
    fi
    codesign --verify --strict "$f" 2>/dev/null || { echo "  BAD signature: $rel"; problems=$((problems + 1)); }
    # Every non-system dependency must resolve inside the bundle (a dylib's own install-name id is not a dependency)
    own_id="$(otool -D "$f" 2>/dev/null | tail -n +2 | head -1)"
    while read -r dep; do
        [[ -z "$dep" || "$dep" == "$own_id" ]] && continue
        case "$dep" in
            /usr/lib/*|/System/*) ;;
            @rpath/*|@loader_path/*|@executable_path/*)
                base="$(basename "$dep")"
                [[ -f "$contents/Frameworks/$base" || -f "$contents/MacOS/$base" || -f "$(dirname "$f")/$base" ]] || { echo "  UNRESOLVED $dep in $rel"; problems=$((problems + 1)); } ;;
            *) echo "  ABSOLUTE dependency $dep in $rel"; problems=$((problems + 1)) ;;
        esac
    done < <(otool -L "$f" | grep -v ':$' | sed -E 's/^[[:space:]]+//; s/ \(compatibility version.*$//')
done < <(find "$contents" -type f -print0)
[[ "$(dwarfdump --uuid "$main" | awk '{print $2}')" == "$new_uuid" ]] || { echo "  LC_UUID not applied"; problems=$((problems + 1)); }
[[ -z "$(xattr -r "$app" 2>/dev/null | grep com.apple.quarantine || true)" ]] || { echo "  quarantine attribute present"; problems=$((problems + 1)); }
for required in Info.plist MacOS/"$exe" Resources/LICENSE.txt Resources/THIRD-PARTY-NOTICES.txt Resources/AppIcon.icns; do
    [[ -e "$contents/$required" ]] || { echo "  MISSING $required"; problems=$((problems + 1)); }
done
# A notice for every vendor dylib and for JPLEPH
for lib in "$contents/Frameworks"/*.dylib; do
    [[ -e "$lib" ]] || continue
    grep -qF "($(basename "$lib"))" "$contents/Resources/THIRD-PARTY-NOTICES.txt" ||
        { echo "  NO NOTICE for Frameworks/$(basename "$lib")"; problems=$((problems + 1)); }
done
if [[ -f "$contents/Resources/JPLEPH" ]]; then
    grep -qF "(Resources/JPLEPH)" "$contents/Resources/THIRD-PARTY-NOTICES.txt" || { echo "  NO NOTICE for JPLEPH"; problems=$((problems + 1)); }
fi
# Entitlements of the published Mach-O files (createdump) survived signing
entitlements="$(python3 "$tools" check-entitlements "$publish" "$contents/MacOS")" || { echo "$entitlements" | sed 's/^/  /'; problems=$((problems + 1)); }
[[ $problems -eq 0 ]] || fail "$problems bundle problem(s)"
echo "  all Mach-O arm64 and signed; dependencies resolve inside the bundle; Info.plist valid; notices complete; ${entitlements:-entitlements checked}"
echo "  spctl (informational): $(spctl --assess --type execute "$app" 2>&1 || true)"

if [[ $skip_smoke -eq 0 ]]; then
    step "Smoke test: \"$main\" --smoke-test (CWD /)"
    start=$(python3 -c 'import time; print(time.time())')
    set +e
    (cd / && "$main" --smoke-test) | sed 's/^/  /'
    status=${PIPESTATUS[0]}
    set -e
    end=$(python3 -c 'import time; print(time.time())')
    [[ $status -eq 0 ]] || fail "smoke test exited $status"
    python3 -c "import sys; print(f'  smoke test wall time (process start to exit): {(float(sys.argv[2]) - float(sys.argv[1])) * 1000:.0f} ms')" "$start" "$end"
fi

if [[ $skip_gui_smoke -eq 0 ]]; then
    # The headless smoke test cannot show that a real launch works (Avalonia.Headless brings its own text shaper, and
    # with the display asleep the native platform cannot start). This is a real launch: Avalonia.Native, the real App
    # and services, the main window on screen (without taking focus) for a few seconds, every page rendered, then the
    # app quits by itself. It needs a logged-in GUI session with the display awake. perl's alarm is a backstop for the
    # app's own 45 s watchdog.
    step "GUI smoke test: \"$main\" --gui-smoke (CWD /; the main window shows for a few seconds)"
    set +e
    (cd / && /usr/bin/perl -e 'alarm 90; exec {$ARGV[0]} @ARGV or die "exec: $!"' "$main" --gui-smoke) | sed 's/^/  /'
    status=${PIPESTATUS[0]}
    set -e
    [[ $status -eq 0 ]] || fail "GUI smoke test exited $status (it needs a logged-in session with the display awake; --skip-gui-smoke skips it)"
fi

if [[ $make_zip -eq 1 ]]; then
    step "Zip"
    zip="$artifacts/$short-$version-arm64.zip"
    rm -f "$zip"
    # --sequesterRsrc stores extended attributes (which hold the managed files' signatures) under __MACOSX/ instead
    # of as "._" files next to them, so even a plain unzip leaves no AppleDouble files inside the bundle. Only Finder
    # or ditto -x -k restore those attributes, and with them a valid signature.
    ditto -c -k --sequesterRsrc --keepParent "$app" "$zip"
    stray="$(unzip -Z1 "$zip" | grep -v '^__MACOSX/' | grep -E '(^|/)\._' || true)"
    [[ -z "$stray" ]] || { echo "$stray" | head -5; fail "AppleDouble entries inside the bundle in $zip"; }
    unzipped="$(mktemp -d)"
    ditto -x -k "$zip" "$unzipped"
    codesign --verify --deep --strict "$unzipped/$display.app" 2>&1 || { rm -rf "$unzipped"; fail "the zip does not extract (ditto -x -k) to a validly signed app"; }
    rm -rf "$unzipped"
    echo "  $zip ($(du -h "$zip" | cut -f1)); no AppleDouble files in the bundle; ditto -x -k extraction verifies"
fi

step "Done"
echo "  $app"
echo "  size: $(du -sh "$app" | cut -f1) (MacOS $(du -sh "$contents/MacOS" | cut -f1), Frameworks $(du -sh "$contents/Frameworks" | cut -f1), Resources $(du -sh "$contents/Resources" | cut -f1))"
