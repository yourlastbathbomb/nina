# native/ (not committed)

Vendor SDK archives (`zwo/`) and staged, re-signed dylibs (`stage/`) go here. Create them with `../scripts/stage-zwo.sh`. Licences: ZWO SDK is MIT-style (`stage/ZWO-LICENSE.txt`); libusb is LGPL-2.1, dynamically linked.

`../scripts/build-astrometry-natives.sh` builds `stage/libsofa.dylib` and `stage/libnovas31.dylib` (plus `stage/SOFA-LICENSE.txt`) from the repo's SOFA and NOVAS C sources; its intermediate files and `--selftest` output go to `build/astrometry/`. NOVAS also needs the JPL DE421 ephemeris: copy `JPLEPH` from nina.external to `ephemeris/JPLEPH`. `NINA.Mac.Native` copies the `.dylib` and `.txt` files in `stage/` next to each build output that references it, and `ephemeris/JPLEPH` to `External/JPLEPH`.
