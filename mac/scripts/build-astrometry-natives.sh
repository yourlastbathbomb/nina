#!/bin/bash
# Build the IAU SOFA and USNO NOVAS 3.1 C sources in this repo as arm64 macOS dylibs for
# NINA.Astrometry (SOFA.cs, NOVAS.cs) and stage them into mac/native/stage.
#
#   mac/scripts/build-astrometry-natives.sh             build and stage
#   mac/scripts/build-astrometry-natives.sh --selftest  build, stage, then run the vendors' own test
#                                                       programs against the staged dylibs and check
#                                                       that a second build is byte-identical
#
# Staged files (mac/native/stage; NINA.Mac.Native copies them into every app/test output):
#   libsofa.dylib     SOFA/SOFA/src (all functions; release from 00READ.ME), for DllImport "SOFA_<release>.dll"
#   libnovas31.dylib  NOVAS31/NOVAS31 novas, novascon, nutation, eph_manager, solsys1 (JPL ephemeris),
#                     readeph0, plus the set_racio_file shim; for DllImport "NOVAS31lib.dll"
#   SOFA-LICENSE.txt  SOFA licence terms, copied from sofa.h
# Each dylib has install name @rpath/<file>, is ad-hoc signed, depends only on libSystem, and must export
# every EntryPoint named in NINA.Astrometry/SOFA.cs and NOVAS.cs (the script fails otherwise).
#
# How the vendor sources are built (no upstream C file is edited):
# - The vendor headers say "#define EXPORT __declspec(dllexport)" unconditionally. The forced include
#   mac/src/NINA.Mac.Native/csrc/nina_mac_export.h maps __declspec(...) to default visibility, and the
#   libraries are compiled with -fvisibility=hidden. A dylib therefore exports exactly the EXPORT-marked
#   functions, like the Windows DLLs.
# - -ffp-contract=off: on arm64, clang would otherwise fuse a*b+c into FMA instructions. Those round
#   differently from the x64 DLLs NINA ships, so keeping strict IEEE evaluation keeps the results comparable.
# - NOVAS: novas.c is also compiled with csrc/novas_racio.h force-included. That routes its two
#   fopen("cio_ra.bin") calls through csrc/novas_racio.c, which also provides set_racio_file (the in-repo
#   source lacks it; NOVAS.cs imports it). By default no CIO file is used: NOVAS computes the CIO RA from
#   the equinox (presumably what NINA gets on Windows too), independent of the working directory. cio_ra.bin is not
#   shipped; see novas_racio.c for why and how one could be wired in.
# - NOVAS vendor warnings silenced after review: -Wmisleading-indentation (novas.c 3435, 7338: layout only),
#   -Wabsolute-value (novas.c cio_array abs() on long values bounded by the record count),
#   -Wunused-value (readeph0.c dummy stub).
# - The JPL ephemeris (DE421) is not built here. It is mac/native/ephemeris/JPLEPH, and NINA.Mac.Native
#   copies it to <output>/External/JPLEPH, where NOVAS.cs looks for it.
#
# Environment: MACOSX_DEPLOYMENT_TARGET (default 13.0), JOBS (default: CPU count).
set -euo pipefail
export LC_ALL=C

here="$(cd "$(dirname "$0")/.." && pwd)"
nina="$(cd "$here/.." && pwd)"
sofa_dir="$nina/SOFA/SOFA"
sofa_src="$sofa_dir/src"
novas_src="$nina/NOVAS31/NOVAS31"
astrometry="$nina/NINA.Astrometry"
csrc="$here/src/NINA.Mac.Native/csrc"
resolver="$here/src/NINA.Mac.Native/NativeLibraries.cs"
stage="$here/native/stage"
build="$here/native/build/astrometry"
jpleph="$here/native/ephemeris/JPLEPH"
grep=/usr/bin/grep

selftest=0
case "${1:-}" in
    "") ;;
    --selftest) selftest=1 ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $1 (try --help)" >&2; exit 2 ;;
esac

cc="$(xcrun --sdk macosx --find clang)"
sdk="$(xcrun --sdk macosx --show-sdk-path)"
jobs="${JOBS:-$(sysctl -n hw.ncpu)}"
common=(-arch arm64 -isysroot "$sdk" "-mmacosx-version-min=${MACOSX_DEPLOYMENT_TARGET:-13.0}")
cflags=("${common[@]}" -O2 -ffp-contract=off -fvisibility=hidden -Wall -include "$csrc/nina_mac_export.h")
novas_quiet=(-Wno-misleading-indentation -Wno-absolute-value -Wno-unused-value)
ldflags=("${common[@]}" -dynamiclib -Wl,-headerpad_max_install_names)
novas_lib_sources=(novascon nutation eph_manager solsys1 readeph0)

die() { echo "error: $*" >&2; exit 1; }

dllname() { sed -n 's/.*const string DLLNAME = "\([^"]*\)".*/\1/p' "$1" | head -1; }

# --- Consistency checks: upstream DLL names vs. sources vs. the resolver map -----------------------------
sofa_issue="$(tr -d '\r' < "$sofa_dir/00READ.ME" | sed -n 's/^SOFA-Issue: *\([0-9][0-9-]*\).*/\1/p' | head -1)"
[[ -n "$sofa_issue" ]] || die "cannot read SOFA-Issue from $sofa_dir/00READ.ME"
sofa_dll="$(dllname "$astrometry/SOFA.cs")"
novas_dll="$(dllname "$astrometry/NOVAS.cs")"
[[ "$sofa_dll" == "SOFA_${sofa_issue//-/_}.dll" ]] ||
    die "SOFA.cs imports '$sofa_dll' but $sofa_dir is release $sofa_issue"
for dll in "$sofa_dll" "$novas_dll"; do
    $grep -q "\"${dll%.dll}" "$resolver" || die "$dll is not mapped in $resolver"
done
sofa_version="${sofa_issue//-/.}"   # Mach-O current_version, e.g. 2023.10.11

# --- Build ---------------------------------------------------------------------------------------------
# build_libs OUTDIR: compile, link and ad-hoc sign libsofa.dylib and libnovas31.dylib into OUTDIR.
build_libs() {
    local out="$1" obj="$1/obj" f
    rm -rf "$out"
    mkdir -p "$obj/sofa" "$obj/novas"

    local sofa_sources=()
    for f in "$sofa_src"/*.c; do
        [[ "$(basename "$f")" == t_sofa_c.c ]] || sofa_sources+=("$f")
    done
    (cd "$obj/sofa" && printf '%s\0' "${sofa_sources[@]}" | xargs -0 -n 16 -P "$jobs" "$cc" "${cflags[@]}" -c)
    "$cc" "${ldflags[@]}" -install_name @rpath/libsofa.dylib \
        -current_version "$sofa_version" -compatibility_version "$sofa_version" \
        -o "$out/libsofa.dylib" "$obj/sofa"/*.o

    "$cc" "${cflags[@]}" "${novas_quiet[@]}" -include "$csrc/novas_racio.h" \
        -c "$novas_src/novas.c" -o "$obj/novas/novas.o"
    for f in "${novas_lib_sources[@]}"; do
        "$cc" "${cflags[@]}" "${novas_quiet[@]}" -c "$novas_src/$f.c" -o "$obj/novas/$f.o"
    done
    "$cc" "${cflags[@]}" -c "$csrc/novas_racio.c" -o "$obj/novas/novas_racio.o"
    "$cc" "${ldflags[@]}" -install_name @rpath/libnovas31.dylib \
        -current_version 3.1 -compatibility_version 3.1 \
        -o "$out/libnovas31.dylib" "$obj/novas"/*.o

    codesign --force --sign - "$out/libsofa.dylib" 2>/dev/null
    codesign --force --sign - "$out/libnovas31.dylib" 2>/dev/null
}

# check_exports DYLIB CSFILE: every EntryPoint in CSFILE must be an exported symbol of DYLIB.
check_exports() {
    local lib="$1" cs="$2" ep n=0 missing=()
    local exported
    exported="$(nm -gU "$lib" | awk '{print $3}')"
    while read -r ep; do
        n=$((n + 1))
        $grep -qx "_$ep" <<< "$exported" || missing+=("$ep")
    done < <($grep -oE 'EntryPoint *= *"[^"]+"' "$cs" | sed -E 's/.*"([^"]+)"/\1/' | sort -u)
    if (( ${#missing[@]} )); then
        die "$(basename "$lib") lacks ${#missing[@]} of the $n entry points in $(basename "$cs"): ${missing[*]}"
    fi
    echo "  $(basename "$lib"): all $n EntryPoints of $(basename "$cs") exported ($(wc -l <<< "$exported" | tr -d ' ') exports total)"
}

# install_file SRC NAME: replace stage/NAME with a new file (new inode, so a process that has the old
# dylib mapped keeps a valid image).
install_file() {
    cp "$1" "$stage/.$2.tmp"
    mv -f "$stage/.$2.tmp" "$stage/$2"
}

echo "Building SOFA $sofa_issue and NOVAS C3.1 for arm64 (clang $("$cc" -dumpversion), $(basename "$sdk"), $jobs jobs)"
build_libs "$build/lib"

echo "Checking exports against NINA.Astrometry:"
check_exports "$build/lib/libsofa.dylib" "$astrometry/SOFA.cs"
check_exports "$build/lib/libnovas31.dylib" "$astrometry/NOVAS.cs"

mkdir -p "$stage"
install_file "$build/lib/libsofa.dylib" libsofa.dylib
install_file "$build/lib/libnovas31.dylib" libnovas31.dylib
{
    echo "IAU SOFA release $sofa_issue, built from unmodified SOFA source files into libsofa.dylib"
    echo "for the N.I.N.A. macOS port (mac/scripts/build-astrometry-natives.sh). Terms, from sofa.h:"
    echo
    awk '/^\/\*-+$/ { p = 1 } p' "$sofa_src/sofa.h" | tr -d '\r'
} > "$stage/.SOFA-LICENSE.txt.tmp"
mv -f "$stage/.SOFA-LICENSE.txt.tmp" "$stage/SOFA-LICENSE.txt"

echo "Staged into $stage:"
for f in libsofa.dylib libnovas31.dylib; do
    echo "  $f: $(lipo -archs "$stage/$f"), id $(otool -D "$stage/$f" | tail -1), sha256 $(shasum -a 256 "$stage/$f" | cut -c1-16)..."
    otool -L "$stage/$f" | tail -n +3 | sed 's/^[[:space:]]*/      links /'
    codesign --verify --strict "$stage/$f" || die "codesign verification failed for $f"
done
echo "  SOFA-LICENSE.txt"

(( selftest )) || exit 0

# --- Self-tests ----------------------------------------------------------------------------------------
echo
echo "Self-tests (vendor test programs against the staged dylibs):"
t="$build/selftest"
rm -rf "$t"
mkdir -p "$t"
failures=0
fail() { echo "  FAIL: $*"; failures=$((failures + 1)); }
xcflags=("${common[@]}" -O2 -ffp-contract=off -Wall -include "$csrc/nina_mac_export.h")
link_dylib=(-L"$stage" -Wl,-rpath,"$stage")

# 1. SOFA's own validation program, linked against libsofa.dylib.
"$cc" "${xcflags[@]}" -I"$sofa_src" "$sofa_src/t_sofa_c.c" "${link_dylib[@]}" -lsofa -o "$t/t_sofa_c"
"$t/t_sofa_c" -v > "$t/t_sofa_c.out" || true
sofa_verdict="$(tail -1 "$t/t_sofa_c.out")"
echo "  t_sofa_c: $sofa_verdict ($($grep -c ' passed: ' "$t/t_sofa_c.out") checks passed, $($grep -c ' failed: ' "$t/t_sofa_c.out") failed)"
[[ "$sofa_verdict" == "t_sofa_c validation successful" ]] || {
    $grep ' failed: ' "$t/t_sofa_c.out" | head -20 | sed 's/^/    /'
    fail "t_sofa_c"
}

# NOVAS object files for the static check program (same flags as the dylib).
novas_objects() { # OUTDIR SOLSYS: novas + racio shim + constants + nutation + SOLSYS + readeph0
    local o="$1" f
    mkdir -p "$o"
    "$cc" "${xcflags[@]}" "${novas_quiet[@]}" -include "$csrc/novas_racio.h" -c "$novas_src/novas.c" -o "$o/novas.o"
    for f in novascon nutation "$2" readeph0; do
        "$cc" "${xcflags[@]}" "${novas_quiet[@]}" -Wno-unused-but-set-variable -c "$novas_src/$f.c" -o "$o/$f.o"
    done
    "$cc" "${xcflags[@]}" -c "$csrc/novas_racio.c" -o "$o/novas_racio.o"
}

# 2. checkout-stars.c: NOVAS' basic validation with solsys3 (no JPL file). solsys3 is not part of the
#    dylib, so this is a static build of the same sources with the same flags. Must match exactly.
novas_objects "$t/obj-solsys3" solsys3
"$cc" "${xcflags[@]}" -c "$novas_src/checkout-stars.c" -o "$t/obj-solsys3/checkout-stars.o"
"$cc" "${common[@]}" "$t/obj-solsys3"/*.o -o "$t/checkout-stars"
(cd "$t" && ./checkout-stars > checkout-stars.out)
if diff -u "$novas_src/checkout-stars-usno.txt" "$t/checkout-stars.out" > "$t/checkout-stars.diff"; then
    echo "  checkout-stars (solsys3, static): identical to checkout-stars-usno.txt"
else
    sed 's/^/    /' "$t/checkout-stars.diff"
    fail "checkout-stars differs from checkout-stars-usno.txt"
fi

# 3./4. checkout-stars-full.c and example.c, linked against libnovas31.dylib, reading JPLEPH from the
#    working directory as the programs are written. USNO made the expected files with DE405; we have
#    DE421, so the ephemeris banner differs and some last digits may differ. Every other number is
#    compared: angles as milliarcseconds (RA scaled by cos Dec), distances relative (limit 1e-8),
#    dates/location exactly. Limits (milliarcseconds):
#      checkout-stars-full: 0.06 = one unit in the last printed digit (1e-9 h of RA, 1e-8 deg of Dec).
#        Star places depend on the ephemeris only through the Earth's barycentric state, where
#        DE405 and DE421 agree far below that.
#      example: 10. Its Moon and Mars lines depend directly on the lunar/planetary ephemeris, and
#        LE405 -> LE421 moves the Moon by metres (several mas at 0.0027 AU). Its star, sidereal-time
#        and zenith lines do not depend on it (the C# smoke tests check those to 0.01 mas / 1e-10 h).
[[ -f "$jpleph" ]] || die "$jpleph is missing (DE421 JPLEPH)"
# compare_numeric EXPECTED ACTUAL LIMIT_MAS: print each differing line and fail beyond the limits.
compare_numeric() {
    awk -v limit="$3" '
        function abs(x) { return x < 0 ? -x : x }
        function nums(line, arr,   n, tok, i, k) {
            n = split(line, tok, /[ \t=]+/); k = 0
            for (i = 1; i <= n; i++) if (tok[i] ~ /^[-+]?[0-9]+\.[0-9]+$/) arr[++k] = tok[i] + 0
            return k
        }
        NR == FNR { expected[FNR] = $0; next }
        {
            e_line = expected[FNR]; a_line = $0
            if (a_line ~ /:$/) { title = a_line; next }          # example.c section title
            if (e_line ~ /DE[0-9]+/) next                         # ephemeris banner
            ne = nums(e_line, ev); na = nums(a_line, av)
            if (ne != na) { printf "    line %d: number of fields differs\n", FNR; bad = 1; next }
            if (a_line ~ /RA =/ || title ~ /positions:|RA & Dec/) units = "h deg AU"
            else if (title ~ /sidereal time/) units = "h h deg"
            else if (title ~ /zenith distance|Mars heliocentric/) units = "deg deg AU"
            else units = "exact exact exact"
            split(units, u, " ")
            for (i = 1; i <= ne; i++) {
                d = av[i] - ev[i]
                if (d == 0) continue
                if (u[i] == "h") {
                    mas = abs(d) * 15 * 3600000
                    if (u[i + 1] == "deg") mas *= cos(av[i + 1] * 3.14159265358979 / 180)
                } else if (u[i] == "deg") {
                    mas = abs(d) * 3600000
                } else if (u[i] == "AU") {
                    rel = abs(d / ev[i])
                    printf "    line %d field %d: %+.3g AU (relative %.2g)\n", FNR, i, d, rel
                    if (rel > 1e-8) bad = 1
                    continue
                } else {
                    printf "    line %d field %d: %+.3g, must match exactly\n", FNR, i, d
                    bad = 1
                    continue
                }
                printf "    line %d field %d: %.4f mas\n", FNR, i, mas
                if (mas > worst) worst = mas
                if (mas > limit) bad = 1
            }
        }
        END { printf "    largest angular difference %.4f mas (limit %s mas)\n", worst, limit; exit bad }
    ' "$1" "$2"
}

mkdir -p "$t/jpl"
ln -sf "$jpleph" "$t/jpl/JPLEPH"
"$cc" "${xcflags[@]}" -c "$novas_src/novascon.c" -o "$t/jpl/novascon.o"   # T0, DEG2RAD... are not exported
for prog in checkout-stars-full example; do
    "$cc" "${xcflags[@]}" "$novas_src/$prog.c" "$t/jpl/novascon.o" "${link_dylib[@]}" -lnovas31 -o "$t/jpl/$prog"
    (cd "$t/jpl" && "./$prog" > "$prog.out")
    expected="$novas_src/$prog-usno.txt"
    case "$prog" in checkout-stars-full) limit=0.06 ;; *) limit=10 ;; esac
    if diff -q "$expected" "$t/jpl/$prog.out" > /dev/null; then
        echo "  $prog (libnovas31.dylib + DE421): identical to $(basename "$expected")"
    else
        echo "  $prog (libnovas31.dylib + DE421) vs $(basename "$expected") (DE405):"
        diff "$expected" "$t/jpl/$prog.out" | $grep '^[<>]' | sed 's/^/    /' || true
        compare_numeric "$expected" "$t/jpl/$prog.out" "$limit" || fail "$prog differs from $(basename "$expected") beyond the limits"
    fi
done
echo "  checkout-mp: not run (needs solsys2, JPL's Fortran PLEPH and a minor-planet ephemeris)"

# 5. set_racio_file shim: default ignores a cio_ra.bin in the CWD, a Windows-layout file is rejected,
#    a native-layout file is used.
mkdir -p "$t/racio"
"$cc" "${xcflags[@]}" "$here/scripts/selftest/novas_racio_check.c" "${link_dylib[@]}" -lnovas31 -o "$t/racio/novas_racio_check"
(
    cd "$t/racio"
    ./novas_racio_check write-native cio_ra.bin
    ./novas_racio_check write-native native.bin
    ./novas_racio_check write-win windows.bin
    ./novas_racio_check run > default.out
    ./novas_racio_check run "$PWD/windows.bin" > windows.out
    ./novas_racio_check run "$PWD/native.bin" > native.out
)
r_default="$(cat "$t/racio/default.out")"
r_windows="$(cat "$t/racio/windows.out")"
r_native="$(cat "$t/racio/native.out")"
echo "  set_racio_file: never called, cio_ra.bin in CWD -> $r_default"
echo "  set_racio_file: Windows-layout file            -> $r_windows"
echo "  set_racio_file: native-layout file             -> $r_native"
[[ "$r_default" == error=0\ ref_sys=2\ * ]] || fail "default must ignore the CWD and use the equinox fallback (ref_sys=2)"
[[ "$r_windows" == "$r_default" ]] || fail "a Windows-layout cio_ra.bin must be rejected (same result as default)"
[[ "$r_native" == "error=0 ref_sys=1 ra_cio=0.066666666666667" ]] || fail "a native-layout cio_ra.bin must be used (ref_sys=1, 3600\"/54000)"

# 6. Reproducibility: a second build from scratch must be byte-identical to the staged dylibs.
build_libs "$build/repro"
for f in libsofa.dylib libnovas31.dylib; do
    if cmp -s "$build/repro/$f" "$stage/$f"; then
        echo "  reproducible: $f rebuilt byte-identical"
    else
        fail "$f differs between two builds"
    fi
done
rm -rf "$build/repro"

if (( failures )); then
    echo "Self-tests: $failures FAILED (outputs in $t)"
    exit 1
fi
echo "Self-tests: all passed (outputs in $t)"
