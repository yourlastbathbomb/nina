#!/usr/bin/env python3
# Copyright © 2016 - 2026 Stefan Berg and the N.I.N.A. contributors.
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
"""Helpers for package-app.sh (standard library only; /usr/bin/python3 from the Xcode tools).

  bundle_tools.py plist OUT key=value ...          write Info.plist (true/false become booleans)
  bundle_tools.py set-uuid MACHO SEED              give a thin 64-bit Mach-O a deterministic LC_UUID
  bundle_tools.py icon OUT.icns                    draw the placeholder app icon (needs sips + iconutil)
  bundle_tools.py notices DEPS.json NUGET OUT EXTRA...   third-party notices for every NuGet package shipped
  bundle_tools.py native-licenses FRAMEWORKS STAGE NINA RESOURCES
                                                   licence files + notices for every vendor dylib (and JPLEPH);
                                                   fails for a dylib without an entry in NATIVE_LICENSES
  bundle_tools.py sign [--skip PATH] PATH...       ad-hoc sign files (folders are walked), keeping entitlements
  bundle_tools.py check-entitlements REFERENCE BUNDLE_DIR
                                                   every entitled Mach-O under REFERENCE keeps them under BUNDLE_DIR
"""
import json
import math
import os
import plistlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET
import zlib


def cmd_plist(out, *pairs):
    data = {}
    for pair in pairs:
        key, _, value = pair.partition("=")
        if value in ("true", "false"):
            data[key] = value == "true"
        else:
            data[key] = value
    with open(out, "wb") as f:
        plistlib.dump(data, f, sort_keys=True)


MH_MAGIC_64 = 0xFEEDFACF
LC_UUID = 0x1B


def cmd_set_uuid(path, seed):
    """Every .NET apphost ships with the same LC_UUID; macOS (TN3179 local network privacy, crash reports)
    keys some state on it. Replace it with uuid5(seed) so this app has its own."""
    with open(path, "r+b") as f:
        data = bytearray(f.read())
        magic, _cpu, _sub, _ftype, ncmds, _size, _flags, _res = struct.unpack_from("<IiiIIIII", data, 0)
        if magic != MH_MAGIC_64:
            sys.exit(f"{path}: not a thin 64-bit Mach-O (magic {magic:#x})")
        offset = 32
        for _ in range(ncmds):
            cmd, cmdsize = struct.unpack_from("<II", data, offset)
            if cmd == LC_UUID:
                new = uuid.uuid5(uuid.NAMESPACE_URL, seed)
                data[offset + 8:offset + 24] = new.bytes
                f.seek(0)
                f.write(data)
                print(str(new).upper())
                return
            offset += cmdsize
        sys.exit(f"{path}: no LC_UUID load command")


def _png(width, height, rows):
    raw = b"".join(b"\x00" + bytes(r) for r in rows)

    def chunk(tag, payload):
        return struct.pack(">I", len(payload)) + tag + payload + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def _icon_pixels(size):
    """Placeholder icon: a dark sky squircle with a few stars and a faint red reticle (the night-vision colour)."""
    stars = [(0.30, 0.28, 0.018), (0.70, 0.22, 0.012), (0.78, 0.62, 0.016), (0.24, 0.70, 0.010), (0.55, 0.80, 0.008), (0.42, 0.18, 0.007)]
    rows = []
    for y in range(size):
        row = bytearray()
        v = (y + 0.5) / size
        for x in range(size):
            u = (x + 0.5) / size
            # squircle mask |x|^5 + |y|^5 <= r^5, antialiased over about one pixel
            dx, dy = abs(u - 0.5) / 0.45, abs(v - 0.5) / 0.45
            d = (dx ** 5 + dy ** 5) ** 0.2
            alpha = max(0.0, min(1.0, (1.0 - d) * size * 0.45 + 0.5))
            if alpha <= 0:
                row += b"\x00\x00\x00\x00"
                continue
            r2 = math.hypot(u - 0.45, v - 0.40)
            base = max(0.0, 1.0 - r2 * 1.3)
            red, green, blue = 10 + 18 * base, 14 + 26 * base, 28 + 60 * base
            ring = abs(math.hypot(u - 0.5, v - 0.5) - 0.27)
            glow = max(0.0, 1.0 - ring * size / max(2.0, size * 0.012))
            red += 150 * glow
            green += 30 * glow
            blue += 25 * glow
            for sx, sy, sr in stars:
                s = math.hypot(u - sx, v - sy) / sr
                if s < 1.5:
                    k = max(0.0, 1.0 - s / 1.5) ** 1.5
                    red, green, blue = red + (235 - red) * k, green + (238 - green) * k, blue + (245 - blue) * k
            row += bytes((int(min(255, red)), int(min(255, green)), int(min(255, blue)), int(255 * alpha)))
        rows.append(row)
    return rows


def cmd_icon(out):
    work = tempfile.mkdtemp()
    try:
        master = os.path.join(work, "master.png")
        with open(master, "wb") as f:
            f.write(_png(1024, 1024, _icon_pixels(1024)))
        iconset = os.path.join(work, "AppIcon.iconset")
        os.mkdir(iconset)
        for base in (16, 32, 128, 256, 512):
            for scale in (1, 2):
                px = base * scale
                name = f"icon_{base}x{base}{'@2x' if scale == 2 else ''}.png"
                subprocess.run(["/usr/bin/sips", "-z", str(px), str(px), master, "--out", os.path.join(iconset, name)],
                               check=True, stdout=subprocess.DEVNULL)
        subprocess.run(["/usr/bin/iconutil", "-c", "icns", iconset, "-o", out], check=True)
    finally:
        shutil.rmtree(work)


MIT_TEXT = """Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."""


def _nuspec(nuget, name, version):
    folder = os.path.join(nuget, name.lower(), version.lower())
    spec = os.path.join(folder, name.lower() + ".nuspec")
    info = {"folder": folder, "license": None, "copyright": None, "url": None, "authors": None}
    if not os.path.exists(spec):
        return info
    root = ET.parse(spec).getroot()
    for el in root.iter():
        tag = el.tag.split("}")[-1]
        if tag == "license":
            info["license"] = (el.text or "").strip()
        elif tag == "licenseUrl" and not info["license"]:
            info["license"] = (el.text or "").strip()
        elif tag == "copyright":
            info["copyright"] = (el.text or "").strip()
        elif tag == "projectUrl":
            info["url"] = (el.text or "").strip()
        elif tag == "authors":
            info["authors"] = (el.text or "").strip()
    return info


def _font_names(data):
    """(family, version, copyright, licence, licence URL) of every TrueType/OpenType font embedded in DATA, read from
    the fonts' own 'name' tables (Windows platform, UTF-16BE). Used to find fonts that NuGet assemblies embed as
    resources (Avalonia.Fonts.Inter) and whose licence differs from the package's."""
    found = []
    i = 0
    while True:
        i = data.find(b"\x00\x01\x00\x00", i)
        if i < 0:
            return found
        try:
            num_tables = struct.unpack_from(">H", data, i + 4)[0]
            tables = {}
            if 4 <= num_tables <= 64:
                for k in range(num_tables):
                    tag, _cs, off, length = struct.unpack_from(">4sIII", data, i + 12 + 16 * k)
                    if not all(32 <= c < 127 for c in tag):
                        tables = {}
                        break
                    tables[tag] = (off, length)
            if b"name" in tables and b"cmap" in tables:
                base = i + tables[b"name"][0]
                _fmt, count, string_offset = struct.unpack_from(">HHH", data, base)
                names = {}
                for r in range(count):
                    pid, _eid, _lid, nid, length, off = struct.unpack_from(">HHHHHH", data, base + 6 + 12 * r)
                    if pid == 3 and nid in (0, 1, 2, 5, 13, 14) and nid not in names:
                        start = base + string_offset + off
                        names[nid] = data[start:start + length].decode("utf-16-be", errors="replace").strip()
                if names.get(1):
                    found.append((f"{names.get(1)} {names.get(2, '')}".strip(), names.get(5, ""), names.get(0, ""),
                                  names.get(13, ""), names.get(14, "")))
        except struct.error:
            pass
        i += 4


def _package_runtime_files(deps, nuget, name, version):
    for target in deps.get("targets", {}).values():
        entry = target.get(f"{name}/{version}")
        if entry:
            for rel in entry.get("runtime", {}):
                path = os.path.join(nuget, name.lower(), version.lower(), rel)
                if os.path.isfile(path):
                    yield path


def cmd_notices(deps_json, nuget, out, *extra):
    """deps.json lists every package the published app carries. For each: licence, copyright, and the package's own
    licence/notice files when it ships them (SkiaSharp, HarfBuzzSharp, the .NET runtime pack); MIT text otherwise.
    Fonts embedded in a package's assemblies are listed with their own copyright and licence (Inter: SIL OFL 1.1).
    EXTRA arguments are "Title=path" files appended verbatim (vendor dylibs, JPLEPH; see native-licenses)."""
    deps = json.load(open(deps_json))
    packages = sorted((k.split("/")[0], k.split("/")[1]) for k, v in deps.get("libraries", {}).items()
                      if v.get("type") in ("package", "runtimepack"))
    lines = ["THIRD-PARTY NOTICES", "", "This application includes the components below. Its own source code is",
             "MPL-2.0 (see LICENSE.txt), based on N.I.N.A. - Nighttime Imaging 'N' Astronomy.", ""]
    for name, version in packages:
        lookup = name
        if name.startswith("runtimepack."):
            lookup = name[len("runtimepack."):]
        info = _nuspec(nuget, lookup, version)
        lines += ["=" * 100, f"{lookup} {version}", f"License: {info['license'] or 'see project'}"]
        if info["copyright"]:
            lines.append(info["copyright"])
        if info["url"]:
            lines.append(info["url"])
        lines.append("")
        shipped = []
        for fname in sorted(os.listdir(info["folder"])) if os.path.isdir(info["folder"]) else []:
            low = fname.lower()
            if (low.startswith("license") or "third-party-notices" in low or "thirdpartynotices" in low) and \
                    not low.endswith((".nupkg", ".sha512", ".png", ".nuspec")) and os.path.isfile(os.path.join(info["folder"], fname)):
                shipped.append(os.path.join(info["folder"], fname))
        if shipped:
            for path in shipped:
                lines += [f"--- {os.path.basename(path)} ---", open(path, encoding="utf-8", errors="replace").read().strip(), ""]
        elif (info["license"] or "").upper() == "MIT":
            lines += [MIT_TEXT, ""]
        fonts = []
        for path in _package_runtime_files(deps, nuget, name, version):
            with open(path, "rb") as f:
                fonts += [font for font in _font_names(f.read()) if font not in fonts]
        if fonts:
            lines += [f"--- Fonts embedded in {lookup} (their own licence, not the package's) ---"]
            for family, font_version, font_copyright, font_licence, font_url in fonts:
                parts = [p.strip().rstrip(".") for p in (f"{family}, {font_version}", font_copyright, font_licence) if p.strip()]
                url = f" {font_url}" if font_url and font_url not in font_licence else ""
                lines.append(". ".join(parts) + "." + url)
            lines.append("")
    for item in extra:
        title, _, path = item.partition("=")
        lines += ["=" * 100, title, "", open(path, encoding="utf-8", errors="replace").read().strip(), ""]
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"{len(packages)} packages")


class _LicenceContext:
    def __init__(self, frameworks, stage, nina, resources):
        self.frameworks, self.stage, self.nina, self.resources = frameworks, stage, nina, resources
        self.licenses = os.path.join(resources, "licenses")
        os.makedirs(self.licenses, exist_ok=True)

    def need(self, path, what):
        if not os.path.isfile(path):
            sys.exit(f"native-licenses: {what} is bundled but its licence file {path} is missing")
        return path

    def copy(self, src, name):
        dst = os.path.join(self.licenses, name)
        shutil.copyfile(src, dst)
        return dst

    def write(self, name, text):
        dst = os.path.join(self.licenses, name)
        with open(dst, "w", encoding="utf-8") as f:
            f.write(text.strip() + "\n")
        return dst


def _text_section(path):
    result = subprocess.run(["/usr/bin/otool", "-X", "-s", "__TEXT", "__text", path], capture_output=True)
    return result.stdout if result.returncode == 0 else None


def _libusb_keg():
    """The Homebrew libusb keg mac/scripts/stage-zwo.sh copies libusb from (realpath, e.g. .../Cellar/libusb/1.0.29)."""
    candidates = []
    try:
        prefix = subprocess.run(["brew", "--prefix", "libusb"], capture_output=True, text=True).stdout.strip()
        if prefix:
            candidates.append(prefix)
    except OSError:
        pass
    candidates += ["/opt/homebrew/opt/libusb", "/usr/local/opt/libusb"]
    for c in candidates:
        if os.path.isfile(os.path.join(c, "COPYING")):
            return os.path.realpath(c)
    return None


def _licence_zwo(ctx, name):
    ctx.copy(ctx.need(os.path.join(ctx.stage, "ZWO-LICENSE.txt"), name), "ZWO-ASI-SDK-LICENSE.txt")
    return f"ZWO ASI Camera SDK ({name}), MIT-style licence", os.path.join(ctx.licenses, "ZWO-ASI-SDK-LICENSE.txt")


def _licence_libusb(ctx, name):
    keg = _libusb_keg()
    if keg is None:
        sys.exit(f"native-licenses: {name} is bundled but libusb's licence (Homebrew libusb COPYING) was not found")
    ctx.copy(os.path.join(keg, "COPYING"), "libusb-LGPL-2.1.txt")
    holders = []
    if os.path.isfile(os.path.join(keg, "AUTHORS")):
        ctx.copy(os.path.join(keg, "AUTHORS"), "libusb-AUTHORS.txt")
        holders = [line.strip() for line in open(os.path.join(keg, "AUTHORS"), encoding="utf-8", errors="replace")
                   if line.startswith("Copyright")]
    version = os.path.basename(keg)
    bundled, brewed = _text_section(os.path.join(ctx.frameworks, name)), _text_section(os.path.join(keg, "lib", name))
    if bundled is not None and bundled == brewed:
        what = f"libusb {version}"
    else:
        print(f"WARNING: {name} does not match Homebrew libusb {version}; its version is not stated (re-run mac/scripts/stage-zwo.sh)",
              file=sys.stderr)
        what = "libusb 1.0"
    text = f"""{name} (Contents/Frameworks) is {what} as built by Homebrew, licensed under the GNU Lesser General Public
License v2.1 (libusb-LGPL-2.1.txt). It is dynamically linked and unmodified apart from its install name and its ad-hoc
signature; you may replace it with your own build of libusb 1.0 (same file name) and re-sign the bundle.
Source: https://github.com/libusb/libusb{f' (tag v{version})' if what != 'libusb 1.0' else ''}

Copyright holders (from libusb's AUTHORS file, libusb-AUTHORS.txt, which also lists the other contributors):
""" + "\n".join("  " + h for h in holders)
    return f"libusb ({name}), LGPL-2.1, dynamically linked", ctx.write("libusb-NOTICE.txt", text)


def _licence_sofa(ctx, name):
    licence = ctx.need(os.path.join(ctx.stage, "SOFA-LICENSE.txt"), name)
    ctx.copy(licence, "SOFA-LICENSE.txt")
    terms = open(licence, encoding="utf-8", errors="replace").read().strip()
    release = re.search(r"SOFA release (\d{4}-\d{2}-\d{2})", terms)
    if not release:
        readme = os.path.join(ctx.nina, "SOFA", "SOFA", "00READ.ME")
        release = re.search(r"SOFA-Issue: *(\d{4}-\d{2}-\d{2})", open(readme, encoding="latin-1").read()) if os.path.isfile(readme) else None
    text = f"""{name} (Contents/Frameworks) is compiled from the IAU SOFA (Standards of Fundamental Astronomy) C library,
release {release.group(1) if release else "(unknown)"}. As clause 3(a) of the SOFA Software License requires, this application
(i) uses routines and computations derived by its developers from software provided by SOFA under license to them; and
(ii) does not itself constitute software provided by and/or endorsed by SOFA.
SOFA software is copyright the Standards of Fundamental Astronomy Board of the International Astronomical Union
(www.iausofa.org). The licence terms follow; they are also in SOFA-LICENSE.txt.

{terms}"""
    return f"IAU SOFA ({name}), SOFA Software License", ctx.write("SOFA-NOTICE.txt", text)


def _licence_novas(ctx, name):
    ctx.copy(ctx.need(os.path.join(ctx.nina, "NOVAS31", "NOVAS31", "README.txt"), name), "NOVAS-C3.1-README.txt")
    text = f"""{name} (Contents/Frameworks) is compiled from the Naval Observatory Vector Astrometry Software (NOVAS),
C Edition, Version 3.1, by the Astronomical Applications Department of the U.S. Naval Observatory (the NOVAS31 sources in
the N.I.N.A. repository), plus this fork's MPL-2.0 shim novas_racio.c. NOVAS has no licensing requirements; its README
(NOVAS-C3.1-README.txt, section IV) asks applications that use it to acknowledge the Astronomical Applications
Department of the U.S. Naval Observatory, which this notice does.
Reference: Bangert, J., Puatua, W., Kaplan, G., Bartlett, J., Harris, W., Fredericks, A., & Monet, A. 2011,
User's Guide to NOVAS Version C3.1 (Washington, DC: USNO)."""
    return f"USNO NOVAS C3.1 ({name}), no licensing requirements, acknowledgement", ctx.write("NOVAS-NOTICE.txt", text)


def _licence_jpleph(ctx, path):
    with open(path, "rb") as f:
        header = f.read(252).decode("ascii", errors="replace")
    titles = [header[k:k + 84].strip() for k in (0, 84, 168)]
    if not titles[0].startswith("JPL"):
        sys.exit(f"native-licenses: {path} does not start with a JPL ephemeris title ({titles[0]!r})")
    de = re.search(r"DE(\d+)", titles[0])
    reference = ("\nReference: Folkner, W. M., Williams, J. G. & Boggs, D. H. 2009, The Planetary and Lunar Ephemeris DE 421,"
                 "\nIPN Progress Report 42-178 (Jet Propulsion Laboratory).") if de and de.group(1) == "421" else ""
    text = f"""Resources/JPLEPH is the JPL planetary and lunar ephemeris{f" DE{de.group(1)}" if de else ""}, in the binary form NOVAS reads.
It comes from N.I.N.A.'s nina.external repository; upstream N.I.N.A. ships the same file as External/JPLEPH.
File header: {titles[0]}; {titles[1]}; {titles[2]}
The JPL ephemerides are produced by the Jet Propulsion Laboratory, California Institute of Technology, for NASA
(https://ssd.jpl.nasa.gov/planets/eph_export.html).{reference}"""
    return f"JPL ephemeris{f' DE{de.group(1)}' if de else ''} (Resources/JPLEPH), attribution", ctx.write("JPLEPH-NOTICE.txt", text)


# Every vendor dylib that may be bundled in Contents/Frameworks needs an entry here: package-app.sh copies every
# *.dylib from mac/native/stage, and native-licenses refuses to continue for one it does not know.
NATIVE_LICENSES = {
    "libASICamera2.dylib": _licence_zwo,
    "libusb-1.0.0.dylib": _licence_libusb,
    "libsofa.dylib": _licence_sofa,
    "libnovas31.dylib": _licence_novas,
}


def cmd_native_licenses(frameworks, stage, nina, resources):
    """Copies the licence of every dylib in FRAMEWORKS (and of RESOURCES/JPLEPH) into RESOURCES/licenses and prints one
    "Title=notice file" line each, for the notices command. Exits non-zero for a dylib with no NATIVE_LICENSES entry."""
    ctx = _LicenceContext(frameworks, stage, nina, resources)
    names = sorted(n for n in os.listdir(frameworks) if n.endswith(".dylib") and not n.startswith("._"))
    unknown = [n for n in names if n not in NATIVE_LICENSES]
    if unknown:
        sys.exit(f"native-licenses: no licence entry for {', '.join(unknown)}; add one to NATIVE_LICENSES in "
                 f"{os.path.abspath(__file__)} before bundling it")
    for n in names:
        title, path = NATIVE_LICENSES[n](ctx, n)
        print(f"{title}={path}")
    jpleph = os.path.join(resources, "JPLEPH")
    if os.path.isfile(jpleph):
        title, path = _licence_jpleph(ctx, jpleph)
        print(f"{title}={path}")


def _is_macho(path):
    with open(path, "rb") as f:
        return f.read(4) in (b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe", b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca")


def _entitlements(path):
    result = subprocess.run(["/usr/bin/codesign", "-d", "--entitlements", "-", "--xml", path], capture_output=True)
    return plistlib.loads(result.stdout) if result.returncode == 0 and result.stdout.strip() else None


def _walk(paths):
    for p in paths:
        if os.path.isdir(p):
            for root, dirs, names in os.walk(p):
                dirs.sort()
                for n in sorted(names):
                    yield os.path.join(root, n)
        else:
            yield p


def cmd_sign(*args):
    """Ad-hoc signs files, keeping the entitlements a file already has. The .NET runtime's createdump carries
    com.apple.security.cs.debugger (and two more); a plain re-sign drops them and createdump then cannot read the
    crashed process (task_for_pid fails), so no crash dump. Hardened runtime stays off (see package-app.sh)."""
    skip, targets = set(), []
    it = iter(args)
    for a in it:
        if a == "--skip":
            skip.add(os.path.realpath(next(it)))
        else:
            targets.append(a)
    macho = entitled = other = 0
    for f in _walk(targets):
        if os.path.islink(f) or not os.path.isfile(f) or os.path.realpath(f) in skip:
            continue
        is_macho = _is_macho(f)
        before = _entitlements(f) if is_macho else None
        result = subprocess.run(["/usr/bin/codesign", "--force", "--sign", "-", "--timestamp=none",
                                 "--preserve-metadata=entitlements", f], capture_output=True, text=True)
        if result.returncode != 0:
            sys.exit(f"codesign {f}: {result.stderr.strip()}")
        if before is not None:
            if _entitlements(f) != before:
                sys.exit(f"sign: {f} lost its entitlements {sorted(before)}")
            entitled += 1
        if is_macho:
            macho += 1
        else:
            other += 1
    print(f"{macho} Mach-O ({entitled} with entitlements kept) + {other} other files")


def cmd_check_entitlements(reference, bundle_dir):
    """Fails when a Mach-O that has entitlements under REFERENCE (the publish folder) has different ones (or is missing)
    at the same relative path under BUNDLE_DIR (Contents/MacOS)."""
    problems = checked = 0
    for f in _walk([reference]):
        if os.path.islink(f) or not _is_macho(f):
            continue
        want = _entitlements(f)
        if want is None:
            continue
        checked += 1
        twin = os.path.join(bundle_dir, os.path.relpath(f, reference))
        have = _entitlements(twin) if os.path.isfile(twin) else None
        if have != want:
            print(f"entitlements lost: {os.path.relpath(f, reference)} has {sorted(have or {})}, expected {sorted(want)}")
            problems += 1
    print(f"{checked} entitled Mach-O checked")
    if problems:
        sys.exit(1)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    commands = {"plist": cmd_plist, "set-uuid": cmd_set_uuid, "icon": cmd_icon, "notices": cmd_notices,
                "native-licenses": cmd_native_licenses, "sign": cmd_sign, "check-entitlements": cmd_check_entitlements}
    command = commands.get(sys.argv[1]) or sys.exit(__doc__)
    command(*sys.argv[2:])
