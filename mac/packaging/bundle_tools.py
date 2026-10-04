#!/usr/bin/env python3
# Copyright © 2016 - 2026 Stefan Berg and the N.I.N.A. contributors.
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
"""Helpers for package-app.sh (standard library only; /usr/bin/python3 from the Xcode tools).

  bundle_tools.py plist OUT key=value ...          write Info.plist (true/false become booleans)
  bundle_tools.py set-uuid MACHO SEED              give a thin 64-bit Mach-O a deterministic LC_UUID
  bundle_tools.py icon OUT.icns                    draw the placeholder app icon (needs sips + iconutil)
  bundle_tools.py notices DEPS.json NUGET OUT EXTRA...   third-party notices for every NuGet package shipped
"""
import json
import math
import os
import plistlib
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


def cmd_notices(deps_json, nuget, out, *extra):
    """deps.json lists every package the published app carries. For each: licence, copyright, and the package's own
    licence/notice files when it ships them (SkiaSharp, HarfBuzzSharp, the .NET runtime pack); MIT text otherwise.
    EXTRA arguments are "Title=path" files appended verbatim (ZWO SDK, libusb)."""
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
    for item in extra:
        title, _, path = item.partition("=")
        lines += ["=" * 100, title, "", open(path, encoding="utf-8", errors="replace").read().strip(), ""]
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"{len(packages)} packages")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    commands = {"plist": cmd_plist, "set-uuid": cmd_set_uuid, "icon": cmd_icon, "notices": cmd_notices}
    command = commands.get(sys.argv[1]) or sys.exit(__doc__)
    command(*sys.argv[2:])
