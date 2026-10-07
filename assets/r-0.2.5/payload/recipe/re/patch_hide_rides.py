"""Remove the ATV and the Racer from CUSTOMIZE -> RIDES in OUR client copy (C:\\Games\\ZRevive) only.

Why a client file and not the zone (traced in the 1.0.326 client + ROTK's UIRoot.gfx):
  * RIDES (MenuItem row 32, ShowCharacterRides) runs, in UIRoot.gfx
    UICustomizationManager.handleVehicleCustomizationClicked -> CustomizationQuerys.getVehicleSlotsQuery:
        SELECT a.VehicleId as slotId, a.IconId, a.Name, a.Description, a.StaticView FROM VehicleSkinVehicles a
    with no WHERE: every row of the UI table VehicleSkinVehicles is a vehicle tab.
  * That UI table (client services +0x2A8, schema 0x14142EF60) is filled from the client's static
    VehicleSkinVehicles.txt (data_x64_0.pack2; VehicleSkinVehiclesLoader). No requirement column, no
    MenuItem row per vehicle, nothing from the server: the only vehicle-skin packets carry selections
    (SetVehicleSkinManager unpacker 0x140E3D850 = map vehicleId -> map modPoint -> item, handled by
    0x1411A6F50), not vehicle definitions.
  * Hiding the skins with ItemDefinitionReply FLAG_HIDE_ON_CLIENT would not even empty the tab:
    getVehicleSkinsQuery LEFT JOINs ItemDefinitions without "NOT ItemId IS NULL".
So the tabs only go away when the rows are gone from VehicleSkinVehicles.txt. VehicleSkinMods.txt rows of
the same vehicles go too, so no mod refers to a vehicle the client no longer knows.

    VehicleSkinVehicles.txt  drop VEHICLE_ID 5 (ATV, kotkappearancevehiclesatv) and 21 (Racer)
    VehicleSkinMods.txt      drop the rows with VEHICLE_ID 5 or 21

Safety:
  * never C:\\Games\\ROTK / the Steam H1Z1 folder; refuses while our H1Z1.exe runs;
  * the pack must have st_nlink == 1 and realpath == path (no hardlink/junction into another install);
  * full backup to C:\\Users\\Gusta\\ZRevive\\backup\\ first, then a temp file next to the pack,
    read back + verified, then os.replace (never written in place);
  * original bytes stay as they are: new blobs and a new map are appended, only the header and the
    two map rows change (same scheme as patch_ui_text.py, which the client accepts).

    python patch_hide_rides.py              # dry run: build + verify into a temp folder, touch nothing
    python patch_hide_rides.py --apply      # game closed: back up, patch via temp + os.replace
    python patch_hide_rides.py --restore    # game closed: put the backed-up pack back (temp + os.replace)
"""
import hashlib
import json
import os
import shutil
import struct
import sys
import tempfile
import time
import zlib

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pack2 import Pack2, name_hash  # noqa: E402
import zr_install as ZI  # noqa: E402

HIDE = {6: "ARV", 21: "Racer"}  # owner 2026-10-06: "disable ARV and racer cars"
TABLES = {"VehicleSkinVehicles.txt": 0, "VehicleSkinMods.txt": 1}  # file -> column of VEHICLE_ID
KEEP_TABS = {1: "Offroader", 2: "Pickup Truck", 4: "Sedan", 5: "ATV", 13: "Parachute"}

ROOT = ZI.resolve_root()
BACKUP_DIR = ZI.BACKUP_DIR
# The record is per install: patching two different installs (C:\Games\ZRevive and a stock-baseline
# build) must not have the second --apply overwrite the first one's restore information.
RECORD = os.path.join(BACKUP_DIR, "hide_rides.json" if os.path.normcase(ROOT) ==
                      os.path.normcase(ZI.DEFAULT_ROOT) else
                      f"hide_rides.{os.path.basename(ROOT).lower()}.json")

# Which pack holds the two UI tables is looked up by name hash in THIS install, never hardcoded.
# It happens to be data_x64_0 in both a stock Z1BR install and a ROTK-derived one, but those two
# data_x64_0 files are completely different (2,885,216 vs 61,857,968 bytes), so the pack must be
# found and re-read per install rather than assumed. Verified 2026-10-07: stock's
# VehicleSkinVehicles.txt is 434 bytes / 7 rows and does contain VEHICLE_IDs 6 and 21.
_packs = {ZI.find_pack(ROOT, n) for n in TABLES}
if len(_packs) != 1:
    raise SystemExit(f"the two vehicle tables are in different packs ({_packs}); this patch assumes one pack")
PACK = _packs.pop()

guard = ZI.guard


def game_running():
    return ZI.game_running(ROOT)


def filter_rows(name, raw):
    col = TABLES[name]
    out, dropped = [], []
    for line in raw.split(b"\r\n"):
        f = line.split(b"^")
        if not line.startswith(b"#") and len(f) > col and f[col].isdigit() and int(f[col]) in HIDE:
            dropped.append(line.decode("latin-1"))
            continue
        out.append(line)
    return b"\r\n".join(out), dropped


def stored(data):
    """pack2 zipped entry: A1B2C3D4 | u32 BE size | zlib (as the pack's own entries)."""
    return b"\xA1\xB2\xC3\xD4" + struct.pack(">I", len(data)) + zlib.compress(data, 9)


def build(src_path, dst_path):
    """Write the patched pack to dst_path (src untouched). Returns a report dict."""
    p = Pack2(src_path)
    size = os.path.getsize(src_path)
    if p.map_off + 32 * p.count != size or p.length != size:
        raise SystemExit(f"{src_path}: map is not at the end of the file, refusing")
    p.f.seek(p.map_off)
    mapping = bytearray(p.f.read(32 * p.count))
    rows = {struct.unpack_from("<Q", mapping, 32 * i)[0]: i for i in range(p.count)}
    blobs, report = [], {}
    for name in TABLES:
        h = name_hash(name)
        raw = p.read(name)
        new, dropped = filter_rows(name, raw)
        if not dropped:
            raise SystemExit(f"{name}: nothing to drop (already patched?)")
        blob = stored(new)
        off = size + sum(len(b) for b in blobs)
        _, _, zf, _ = p.entries[h]
        struct.pack_into("<QQQII", mapping, 32 * rows[h], h, off, len(blob), zf, zlib.crc32(blob))
        blobs.append(blob)
        report[name] = {"dropped": dropped, "before": len(raw), "after": len(new)}
    p.f.close()
    map_off = size + sum(len(b) for b in blobs)
    total = map_off + len(mapping)
    header = struct.pack("<4sIQQ", b"PAK\x01", p.count, total, map_off)
    with open(src_path, "rb") as src, open(dst_path, "wb") as dst:
        shutil.copyfileobj(src, dst, 1 << 22)
        for b in blobs:
            dst.write(b)
        dst.write(mapping)
        dst.seek(0)
        dst.write(header)
        dst.flush()
        os.fsync(dst.fileno())
    return report


def verify(path, original):
    """Read the patched pack back with an independent reader: tabs, mods, every other entry unchanged."""
    a, b = Pack2(original), Pack2(path)
    assert b.count == a.count and os.path.getsize(path) == b.length == b.map_off + 32 * b.count
    vsv = b.read("VehicleSkinVehicles.txt").decode("latin-1").split("\r\n")
    ids = {int(l.split("^")[0]) for l in vsv if l[:1].isdigit()}
    assert ids == set(KEEP_TABS), ids
    mods = b.read("VehicleSkinMods.txt").decode("latin-1").split("\r\n")
    assert all(int(l.split("^")[1]) not in HIDE for l in mods if l[:1].isdigit())
    for name in TABLES:
        off, ln, zf, crc = b.entries[name_hash(name)]
        b.f.seek(off)
        assert zlib.crc32(b.f.read(ln)) == crc
    changed = [h for h in a.entries if a.entries[h] != b.entries[h]]
    assert sorted(changed) == sorted(name_hash(n) for n in TABLES), changed
    with open(original, "rb") as fa, open(path, "rb") as fb:
        fa.seek(24)
        fb.seek(24)
        left = a.length - 24
        while left:
            n = min(left, 1 << 22)
            assert fa.read(n) == fb.read(n), "original bytes changed"
            left -= n
    a.f.close()
    b.f.close()
    return {"tabs": {i: KEEP_TABS[i] for i in sorted(ids)}, "mods": sum(1 for l in mods if l[:1].isdigit())}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 22), b""):
            h.update(chunk)
    return h.hexdigest()


def dry_run():
    guard(PACK)
    with tempfile.TemporaryDirectory() as td:
        out = os.path.join(td, "data_x64_0.pack2")
        rep = build(PACK, out)
        v = verify(out, PACK)
    for name, r in rep.items():
        print(f"{name}: {r['before']} -> {r['after']} bytes, dropping {len(r['dropped'])} row(s): {r['dropped'][:3]}{' ...' if len(r['dropped']) > 3 else ''}")
    print(f"verified (temp copy): RIDES tabs left {v['tabs']}, {v['mods']} mod rows, original bytes intact")
    print(f"pack located by name: {PACK}")
    print(f"dry run only: nothing in {ROOT} was written. Close the game, then --apply.")


def apply():
    guard(PACK)
    if game_running():
        raise SystemExit("our H1Z1.exe is running: close the game first, nothing was written")
    os.makedirs(BACKUP_DIR, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup = os.path.join(BACKUP_DIR, f"data_x64_0.pack2.pre-hide-rides-{stamp}")
    shutil.copyfile(PACK, backup)
    digest = sha256(PACK)
    if sha256(backup) != digest:
        raise SystemExit("backup copy differs from the pack, stopping (nothing was patched)")
    tmp = PACK + ".zrtmp"
    try:
        rep = build(PACK, tmp)
        v = verify(tmp, PACK)
        guard(PACK)
        if game_running():
            raise SystemExit("the game was started meanwhile: stopping, the pack is unchanged")
        os.replace(tmp, PACK)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)
    guard(PACK)
    json.dump({"backup": backup, "originalSha256": digest, "patchedSha256": sha256(PACK), "at": stamp,
               "report": rep}, open(RECORD, "w"), indent=1)
    print(f"patched {PACK}: RIDES tabs now {v['tabs']}; backup {backup}; record {RECORD}")


def restore():
    guard(PACK)
    if game_running():
        raise SystemExit("our H1Z1.exe is running: close the game first")
    rec = json.load(open(RECORD))
    if sha256(PACK) != rec["patchedSha256"]:
        raise SystemExit("the pack is not the one this script patched (updated since?): not touching it")
    if sha256(rec["backup"]) != rec["originalSha256"]:
        raise SystemExit("backup does not match its recorded hash, not restoring")
    tmp = PACK + ".zrtmp"
    shutil.copyfile(rec["backup"], tmp)
    os.replace(tmp, PACK)
    guard(PACK)
    os.remove(RECORD)
    print(f"restored {PACK} from {rec['backup']}")


if __name__ == "__main__":
    if "--apply" in sys.argv:
        apply()
    elif "--restore" in sys.argv:
        restore()
    else:
        dry_run()
