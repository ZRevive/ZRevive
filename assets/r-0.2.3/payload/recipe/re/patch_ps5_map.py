"""Put Z2 "The Arena" back to its Pre-Season 5 (Aug 2017) layout in OUR client copy (C:\\Games\\ZRevive) only.

Source: Z2.zone of the Aug-2017 PS5 client (depot 433851, manifest 6373368576374184611), downloaded once
into C:\\Users\\Gusta\\ZRevive\\ref\\ps5-2017\\depot\\ (stays local, never shipped). `--extract` copies the
Z2.zone entry out of the old v1 .pack files there into ref\\ps5-2017\\Z2.zone. The download needs a Steam
account that has app 433850 (anonymous login is refused); the owner runs, in ref\\ps5-2017\\DepotDownloader:
    .\\DepotDownloader.exe -app 433850 -depot 433851 -manifest 6373368576374184611 -filelist files.txt
        -dir ..\\depot -username <steam account name> -remember-password
and types the password / Steam Guard code into DepotDownloader itself (never into a script or chat).

What changes (objects section of Z2.zone only; terrain, flora, ecos, lights and decals stay 2019):
  RESTORE  Coy's Car Salvage, Ponyvale                     (in PS5, gone in 2019)
  REVERT   CWP Water Treatment, East Valley Shopping Center, CWP Utilities 62, Sunny Pines rework
           (Combat Update, 2018): back to whatever PS5 had there
Inside each region (circle in x/z) the 2019 placements that PS5 does not have are dropped and the PS5
placements that 2019 does not have are added. A placement "matches" when actor, instance id and position
agree (0.05 m). `--whole-map` does that for the whole map instead (exact PS5 placements everywhere).

ZONE v5 (2017) -> v7 (2019), objects section (re-derived and checked on 199,000 placements both files share):
  object   cstr actor | f32 renderDist | [v7: u32, 0] | u32 n | n * instance
  instance f32x4 pos | f32x4 rot | f32x4 scale | u32 id | u8 | [v7: u32] | f32 lod | 3 lists | u32 0 |
           list | 5 bytes
  v7's new u32 is 0x01000000 ("interior" flag, bytes 00 00 00 01) where v5 had u8 = 1 (v7 then has u8 0);
  2019 also set it on many indoor props, so a PS5 placement gets it when most 2019 placements of that actor
  have it. Decals (2017 section 7) are not carried over.

Safety (same scheme as patch_hide_rides.py / patch_ui_text.py):
  * never C:\\Games\\ROTK or anything under steamapps; refuses while an H1Z1.exe of our copy runs (or one
    whose path cannot be read);
  * the pack must have st_nlink == 1 and realpath == path (no hardlink/junction into another install);
  * full backup to C:\\Users\\Gusta\\ZRevive\\backup\\ first, then a temp file next to the pack, read back
    and verified, then os.replace (never written in place);
  * original bytes stay as they are: the new Z2.zone blob and a new map are appended; only the header
    and the Z2.zone map row change.

    python patch_ps5_map.py --extract            # PS5 Z2.zone out of ref\\ps5-2017\\depot (read-only there)
    python patch_ps5_map.py                      # dry run: build + verify in a temp folder, print the report
    python patch_ps5_map.py --dry-run --whole-map
    python patch_ps5_map.py --apply              # game closed: back up, patch via temp + os.replace
    python patch_ps5_map.py --restore            # game closed: put the backed-up pack back
  --source PATH  (dry run only) use another v5/v7 Z2.zone as the source, e.g. the Mar-2017 one, for testing
"""
import collections
import glob
import hashlib
import json
import math
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import zlib

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pack2 import Pack2, name_hash  # noqa: E402
import zr_install as ZI  # noqa: E402

ROOT = ZI.resolve_root()
ASSETS = ZI.assets_dir(ROOT)
ENTRY = "Z2.zone"
# Which pack holds Z2.zone is looked up by name in THIS install, not hardcoded. It is Z2_x64_12 in
# both a stock Z1BR install and a ROTK-derived one (both 360,565,600 bytes before patching), but the
# lookup keeps that a verified fact rather than an assumption.
PACK = ZI.find_pack(ROOT, ENTRY)
GAME_EXE = os.path.join(ROOT, "H1Z1.exe")
REF = os.path.join(ZI.REPO, "ref", "ps5-2017")
DEPOT = os.path.join(REF, "depot")
SOURCE = os.path.join(REF, "Z2.zone")
BACKUP_DIR = ZI.BACKUP_DIR
RECORD = os.path.join(BACKUP_DIR, "ps5_map.json" if os.path.normcase(ROOT) ==
                      os.path.normcase(ZI.DEFAULT_ROOT) else
                      f"ps5_map.{os.path.basename(ROOT).lower()}.json")
FORBIDDEN = ZI.FORBIDDEN

# Entries --extract pulls out of the 2017 depot packs. MAPIMAGE_SRC is the 1024x1024 bordered PS5
# world map that patch_ps5_mapimage.py upscales. It is present in a ROTK-derived tree (ROTK kept a
# copy in assets_x64_0/1) but ABSENT from a stock Z1 Battle Royale install - verified 2026-10-07 by
# hashing every entry of all 74,003 names in stock's pack2 set. So on a stock baseline the map image
# patch has no local source and must take it from this same depot download.
MAPIMAGE_SRC = "img8973010328128190133.dds"
EXTRACT_NAMES = ("z2.zone", "z2areas.xml", "lighting_z2.txt", "mapimagez2.gfx", MAPIMAGE_SRC.lower())
INTERIOR = 0x01000000
TOL = 0.05

# name, centre x, centre z, radius (m): the 2019 Z2Areas.xml loot box (centre, half diagonal + margin);
# Ponyvale has no area, its centre is the PonyVale road sign and the radius takes in the 2017 town block.
# Check them against the "biggest whole-map differences" list of a dry run with the PS5 source.
RESTORE = [
    ("Coy's Car Salvage", -212.0, -1020.0, 240.0),        # Loot.CoysCarSalvage.1
    ("Ponyvale", -1836.0, -802.0, 450.0),
]
REVERT = [
    ("CWP Water Treatment", -134.0, -1671.0, 200.0),      # LootDistro.CWPWaterTreatmentAuthority
    ("East Valley Shopping Center", -1056.0, 2935.0, 280.0),  # LootDistro.EastValleyShoppingCenter
    ("CWP Utilities 62", 1531.0, 194.0, 200.0),           # LootDistro.CWPUtilitiesCompound62
    ("Sunny Pines rework", 1477.0, 1262.0, 250.0),        # Loot.SunnyPinesWarehouses
]
REGIONS = RESTORE + REVERT


# ---------------------------------------------------------------- safety

def guard(path):
    low = os.path.normcase(os.path.abspath(path))
    if any(f in low for f in FORBIDDEN):
        raise SystemExit(f"{path}: not our copy, refusing")
    if os.stat(path).st_nlink != 1 or os.path.normcase(os.path.realpath(path)) != low:
        raise SystemExit(f"{path} is shared with another install (hardlink/junction); not touching it")


def game_running():
    """True if an H1Z1.exe of our copy runs, or an H1Z1.exe whose image path cannot be read."""
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq H1Z1.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True).stdout
    pids = [int(l.strip('"').split('","')[1]) for l in out.splitlines() if l.lower().startswith('"h1z1.exe"')]
    if not pids:
        return False
    try:
        import live
        for pid in pids:
            img = live.image_path(pid)
            if not img or os.path.normcase(img) == os.path.normcase(GAME_EXE):
                return True
        return False
    except Exception:
        return True


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 22), b""):
            h.update(chunk)
    return h.hexdigest()


# ---------------------------------------------------------------- zone file

class Zone:
    """ZONE v5/v7. Keeps every byte outside the objects section as it is."""

    def __init__(self, data):
        if data[:4] != b"ZONE":
            raise ValueError("not a ZONE file")
        self.data = data
        self.ver = struct.unpack_from("<I", data, 4)[0]
        n = struct.unpack_from("<I", data, 8)[0]
        self.offs = list(struct.unpack_from("<%dI" % n, data, 12))
        if self.ver not in (5, 7) or n != 7:
            raise ValueError(f"ZONE v{self.ver} with {n} sections: unknown layout")
        self.objects = self._objects()

    def _objects(self):
        d, o, v7 = self.data, self.offs[3], self.ver >= 7
        (cnt,) = struct.unpack_from("<I", d, o)
        o += 4
        objs = []
        for _ in range(cnt):
            e = d.index(b"\0", o)
            name = d[o:e].decode("latin-1")
            o = e + 1
            (rd,) = struct.unpack_from("<f", d, o)
            o += 4
            ox = 0
            if v7:
                (ox,) = struct.unpack_from("<I", d, o)
                o += 4
            (ni,) = struct.unpack_from("<I", d, o)
            o += 4
            insts = []
            for _ in range(ni):
                s = o
                o += 48 + 4 + 1 + (4 if v7 else 0) + 4
                for size in (8, 8, 0, 20):
                    (k,) = struct.unpack_from("<I", d, o)
                    if size == 0 and k:
                        raise ValueError("unexpected non-empty list")
                    o += 4 + k * size
                o += 5
                insts.append(d[s:o])
            objs.append({"name": name, "rd": rd, "ox": ox, "insts": insts})
        if o != self.offs[4]:
            raise ValueError(f"objects end at {o}, lights start at {self.offs[4]}")
        return objs

    @staticmethod
    def pos(raw):
        return struct.unpack_from("<3f", raw, 0)

    @staticmethod
    def iid(raw):
        return struct.unpack_from("<I", raw, 48)[0]

    def interior(self, raw):
        return (raw[52] == 1) if self.ver < 7 else struct.unpack_from("<I", raw, 53)[0] == INTERIOR

    def build_v7(self, objects):
        """New v7 file: this zone's bytes with the objects section replaced."""
        if self.ver != 7:
            raise ValueError("only a v7 zone can be the base")
        out = bytearray()
        out += struct.pack("<I", len(objects))
        for ob in objects:
            out += ob["name"].encode("latin-1") + b"\0" + struct.pack("<fII", ob["rd"], ob["ox"], len(ob["insts"]))
            for raw in ob["insts"]:
                out += raw
        head = bytearray(self.data[:self.offs[3]])
        delta = len(out) - (self.offs[4] - self.offs[3])
        for i in range(4, 7):
            struct.pack_into("<I", head, 12 + 4 * i, self.offs[i] + delta)
        return bytes(head) + bytes(out) + self.data[self.offs[4]:]


def v5_to_v7(raw, interior):
    """v5 instance -> v7: u8 -> 0 + u32 flag (see module doc)."""
    flag = INTERIOR if (raw[52] == 1 or interior) else 0
    return raw[:52] + b"\0" + struct.pack("<I", flag) + raw[53:]


# ---------------------------------------------------------------- source

def pack1_entries(path):
    """Old ForgeLight .pack (v1): chunks of [u32be next, u32be count, count*(u32be len, name, u32be off, len, crc)]."""
    with open(path, "rb") as f:
        size = os.fstat(f.fileno()).st_size
        pos = 0
        while True:
            f.seek(pos)
            nxt, cnt = struct.unpack(">II", f.read(8))
            for _ in range(cnt):
                (nl,) = struct.unpack(">I", f.read(4))
                name = f.read(nl).decode("latin-1")
                off, ln, crc = struct.unpack(">III", f.read(12))
                yield name, off, ln, crc
            if nxt == 0 or nxt >= size:
                break
            pos = nxt


def extract():
    packs = sorted(glob.glob(os.path.join(DEPOT, "**", "*.pack"), recursive=True))
    if not packs:
        raise SystemExit(f"no .pack under {DEPOT}: download Assets_146.pack first (see the top of this script)")
    found = []
    for p in packs:
        for name, off, ln, crc in pack1_entries(p):
            if name.lower() in EXTRACT_NAMES:
                with open(p, "rb") as f:
                    f.seek(off)
                    d = f.read(ln)
                if zlib.crc32(d) != crc:
                    print(f"  warning: crc mismatch for {name} in {os.path.basename(p)}")
                out = os.path.join(REF, name)
                with open(out + ".tmp", "wb") as f:
                    f.write(d)
                os.replace(out + ".tmp", out)
                found.append(name)
                print(f"{name}: {len(d)} bytes from {os.path.basename(p)} -> {out}  sha256 {hashlib.sha256(d).hexdigest()}")
    if not any(n.lower() == "z2.zone" for n in found):
        raise SystemExit("Z2.zone is not in the downloaded pack(s): it lives in another Assets_*.pack of that manifest")


# ---------------------------------------------------------------- merge

def in_region(p, regions):
    x, _, z = p
    for name, cx, cz, r in regions:
        if (x - cx) ** 2 + (z - cz) ** 2 <= r * r:
            return name
    return None


def index_assets():
    hashes = set()
    for p in glob.glob(os.path.join(ASSETS, "*.pack2")):
        pk = Pack2(p)
        hashes.update(pk.entries)
        pk.f.close()
    return hashes


NAME_RE = re.compile(rb"([A-Za-z0-9_\-\.]{3,100}\.(?:adr|dme|dds|mrn|fxd|cdt|dma|apx|dmv))(?![A-Za-z0-9])", re.I)


def missing_models(actors):
    """Actors (and the files their .adr/.dme name) that our 2019 packs do not have."""
    packs = [Pack2(p) for p in glob.glob(os.path.join(ASSETS, "*.pack2"))]
    have = set()
    for pk in packs:
        have.update(pk.entries)

    def read(n):
        h = name_hash(n)
        for pk in packs:
            if h in pk.entries:
                return pk.read_hash(h)
        return None

    out, seen = {}, set()
    for actor in sorted(actors):
        miss, todo = [], [actor]
        while todo:
            n = todo.pop()
            if n.lower() in seen and n != actor:
                continue
            seen.add(n.lower())
            if name_hash(n) not in have:
                miss.append(n)
                continue
            if n.lower().endswith((".adr", ".dme")):
                d = read(n) or b""
                todo += [m.group(1).decode("latin-1") for m in NAME_RE.finditer(d)
                         if m.group(1).decode("latin-1").lower() not in seen]
        if miss:
            out[actor] = miss
    for pk in packs:
        pk.f.close()
    return out


def merge(base, src, regions, whole_map):
    """base: 2019 v7 Zone; src: PS5 Zone (v5 or v7). Returns (objects, report)."""
    have_assets = index_assets()
    # interior flag majority per actor in 2019
    flagged = {ob["name"].lower(): sum(base.interior(r) for r in ob["insts"]) * 2 > len(ob["insts"])
               for ob in base.objects}
    src_by = collections.defaultdict(list)  # (actor, id) -> positions in source
    for ob in src.objects:
        for raw in ob["insts"]:
            src_by[(ob["name"].lower(), Zone.iid(raw))].append(Zone.pos(raw))
    base_by = collections.defaultdict(list)
    for ob in base.objects:
        for raw in ob["insts"]:
            base_by[(ob["name"].lower(), Zone.iid(raw))].append(Zone.pos(raw))

    def same(p, plist):
        return any(max(abs(a - b) for a, b in zip(p, q)) <= TOL for q in plist)

    region_of = (lambda p: "whole map") if whole_map else (lambda p: in_region(p, regions))
    rep = collections.defaultdict(lambda: collections.Counter())
    removed_actors = collections.defaultdict(collections.Counter)
    added_actors = collections.defaultdict(collections.Counter)
    used_ids = collections.Counter()
    objects, by_name = [], {}
    for ob in base.objects:
        keep = []
        for raw in ob["insts"]:
            p = Zone.pos(raw)
            reg = region_of(p)
            if reg and not same(p, src_by.get((ob["name"].lower(), Zone.iid(raw)), [])):
                rep[reg]["removed"] += 1
                removed_actors[reg][ob["name"]] += 1
                continue
            if reg:
                rep[reg]["kept"] += 1
            keep.append(raw)
            used_ids[Zone.iid(raw)] += 1
        nob = dict(ob, insts=keep)
        objects.append(nob)
        by_name[ob["name"].lower()] = nob
    skipped = collections.Counter()
    new_actor_rd = {}
    for ob in src.objects:
        lname = ob["name"].lower()
        for raw in ob["insts"]:
            p = Zone.pos(raw)
            reg = region_of(p)
            if not reg or same(p, base_by.get((lname, Zone.iid(raw)), [])):
                continue
            if name_hash(ob["name"]) not in have_assets:
                skipped[ob["name"]] += 1
                rep[reg]["skipped (actor not in our packs)"] += 1
                continue
            if src.ver < 7:
                raw = v5_to_v7(raw, flagged.get(lname, False))
            iid = Zone.iid(raw)
            if used_ids[iid]:
                iid = zlib.crc32(ob["name"].encode() + raw[:12])
                while used_ids[iid]:
                    iid = (iid + 0x9E3779B1) & 0xFFFFFFFF
                raw = raw[:48] + struct.pack("<I", iid) + raw[52:]
                rep[reg]["new id (clash)"] += 1
            used_ids[iid] += 1
            if lname not in by_name:
                nob = {"name": ob["name"], "rd": ob["rd"], "ox": 0, "insts": []}
                objects.append(nob)
                by_name[lname] = nob
                new_actor_rd[ob["name"]] = ob["rd"]
            by_name[lname]["insts"].append(raw)
            rep[reg]["added"] += 1
            added_actors[reg][ob["name"]] += 1
    objects = [ob for ob in objects if ob["insts"] or ob["name"].lower() in {o["name"].lower() for o in base.objects}]
    return objects, {"regions": {k: dict(v) for k, v in rep.items()},
                     "removed": {k: dict(v.most_common(8)) for k, v in removed_actors.items()},
                     "added": {k: dict(v.most_common(8)) for k, v in added_actors.items()},
                     "skipped_actors": dict(skipped), "new_actors": sorted(new_actor_rd),
                     "added_actor_names": sorted({a for v in added_actors.values() for a in v})}


def diff_clusters(base, src, cell=300.0, top=25):
    """Whole-map placement differences (structures and props), by 300 m cell, for checking the regions."""
    def keys(z):
        c = collections.Counter()
        where = {}
        for ob in z.objects:
            n = ob["name"].lower()
            if "itemspawner" in n or "lootspawn" in n:
                continue
            for raw in ob["insts"]:
                p = Zone.pos(raw)
                k = (n, round(p[0]), round(p[1]), round(p[2]))
                c[k] += 1
                where[k] = p
        return c, where
    cb, wb = keys(base)
    cs, ws = keys(src)
    cells = collections.defaultdict(lambda: [0, 0])
    for k, n in (cb - cs).items():
        p = wb[k]
        cells[(math.floor(p[0] / cell), math.floor(p[2] / cell))][0] += n
    for k, n in (cs - cb).items():
        p = ws[k]
        cells[(math.floor(p[0] / cell), math.floor(p[2] / cell))][1] += n
    rows = sorted(cells.items(), key=lambda kv: -(kv[1][0] + kv[1][1]))[:top]
    return [((cx + 0.5) * cell, (cz + 0.5) * cell, only19, onlysrc, in_region(((cx + 0.5) * cell, 0, (cz + 0.5) * cell), REGIONS))
            for (cx, cz), (only19, onlysrc) in rows]


# ---------------------------------------------------------------- pack

def stored(data):
    return b"\xA1\xB2\xC3\xD4" + struct.pack(">I", len(data)) + zlib.compress(data, 9)


def build(src_pack, dst_pack, zone_bytes):
    p = Pack2(src_pack)
    size = os.path.getsize(src_pack)
    if p.map_off + 32 * p.count != size or p.length != size:
        raise SystemExit(f"{src_pack}: map is not at the end of the file, refusing")
    p.f.seek(p.map_off)
    mapping = bytearray(p.f.read(32 * p.count))
    h = name_hash(ENTRY)
    row = next(i for i in range(p.count) if struct.unpack_from("<Q", mapping, 32 * i)[0] == h)
    _, _, zf, _ = p.entries[h]
    p.f.close()
    blob = stored(zone_bytes) if zf in (1, 0x11) else zone_bytes
    struct.pack_into("<QQQII", mapping, 32 * row, h, size, len(blob), zf, zlib.crc32(blob))
    map_off = size + len(blob)
    header = struct.pack("<4sIQQ", b"PAK\x01", p.count, map_off + len(mapping), map_off)
    with open(src_pack, "rb") as s, open(dst_pack, "wb") as d:
        shutil.copyfileobj(s, d, 1 << 22)
        d.write(blob)
        d.write(mapping)
        d.seek(0)
        d.write(header)
        d.flush()
        os.fsync(d.fileno())


def verify(path, original, zone_bytes):
    a, b = Pack2(original), Pack2(path)
    assert b.count == a.count and os.path.getsize(path) == b.length == b.map_off + 32 * b.count
    h = name_hash(ENTRY)
    off, ln, zf, crc = b.entries[h]
    b.f.seek(off)
    assert zlib.crc32(b.f.read(ln)) == crc
    assert b.read(ENTRY) == zone_bytes
    Zone(zone_bytes)  # parses end to end
    assert [k for k in a.entries if a.entries[k] != b.entries[k]] == [h]
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


# ---------------------------------------------------------------- commands

def make(source, whole_map):
    if not os.path.exists(source):
        raise SystemExit(f"{source} missing: run --extract after the PS5 download (see the top of this script)")
    rec = json.load(open(RECORD)) if os.path.exists(RECORD) else None
    pk = Pack2(PACK)
    cur = pk.read(ENTRY)
    pk.f.close()
    if rec and hashlib.sha256(cur).hexdigest() == rec.get("patchedZoneSha256"):
        raise SystemExit("our Z2.zone is already the patched one (--restore first to rebuild)")
    base = Zone(cur)
    src = Zone(open(source, "rb").read())
    objects, rep = merge(base, src, REGIONS, whole_map)
    out = base.build_v7(objects)
    rep["base_instances"] = sum(len(o["insts"]) for o in base.objects)
    rep["new_instances"] = sum(len(o["insts"]) for o in Zone(out).objects)
    rep["source"] = {"path": source, "version": src.ver, "sha256": hashlib.sha256(src.data).hexdigest()}
    in2019 = {o["name"].lower() for o in base.objects if o["insts"]}
    rep["missing_models"] = {(a + ("" if a.lower() not in in2019 else " (also placed by 2019)")): m
                             for a, m in missing_models(rep["added_actor_names"]).items()}
    rep["diff_cells"] = diff_clusters(base, src)
    return cur, out, rep


def print_report(rep, cur, out):
    print(f"source {rep['source']['path']} (ZONE v{rep['source']['version']})")
    print(f"Z2.zone {len(cur)} -> {len(out)} bytes, placements {rep['base_instances']} -> {rep['new_instances']}")
    for reg, c in rep["regions"].items():
        print(f"  {reg:30} {c}")
        if rep["removed"].get(reg):
            print(f"      removed: {rep['removed'][reg]}")
        if rep["added"].get(reg):
            print(f"      added:   {rep['added'][reg]}")
    print(f"new actors (not placed anywhere in 2019 Z2): {len(rep['new_actors'])} {rep['new_actors'][:12]}")
    print(f"actors skipped, not in our packs: {rep['skipped_actors'] or 'none'}")
    print(f"actors placed whose files are missing in our packs: {rep['missing_models'] or 'none'}")
    print("biggest whole-map differences 2019 vs source (300 m cells: x, z, only-2019, only-source, region):")
    for x, z, a, b, r in rep["diff_cells"]:
        print(f"   ({x:7.0f},{z:7.0f})  -{a:5}  +{b:5}  {r or ''}")


def dry_run(source, whole_map):
    guard(PACK)
    cur, out, rep = make(source, whole_map)
    with tempfile.TemporaryDirectory() as td:
        tmp = os.path.join(td, os.path.basename(PACK))
        build(PACK, tmp, out)
        verify(tmp, PACK, out)
    print_report(rep, cur, out)
    print("verified (temp copy): only the Z2.zone row changed, original bytes intact, new zone parses")
    print(f"dry run only: nothing in {ROOT} was written. Close the game, then --apply.")


def apply(whole_map):
    guard(PACK)
    if game_running():
        raise SystemExit("our H1Z1.exe is running: close the game first, nothing was written")
    cur, out, rep = make(SOURCE, whole_map)
    os.makedirs(BACKUP_DIR, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup = os.path.join(BACKUP_DIR, f"Z2_x64_12.pack2.pre-ps5-map-{stamp}")
    shutil.copyfile(PACK, backup)
    digest = sha256(PACK)
    if sha256(backup) != digest:
        raise SystemExit("backup copy differs from the pack, stopping (nothing was patched)")
    tmp = PACK + ".zrtmp"
    try:
        build(PACK, tmp, out)
        verify(tmp, PACK, out)
        guard(PACK)
        if game_running():
            raise SystemExit("the game was started meanwhile: stopping, the pack is unchanged")
        os.replace(tmp, PACK)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)
    guard(PACK)
    json.dump({"backup": backup, "originalSha256": digest, "patchedSha256": sha256(PACK), "at": stamp,
               "patchedZoneSha256": hashlib.sha256(out).hexdigest(), "wholeMap": whole_map,
               "report": rep}, open(RECORD, "w"), indent=1)
    print_report(rep, cur, out)
    print(f"patched {PACK}; backup {backup}; record {RECORD}")


def restore():
    guard(PACK)
    if game_running():
        raise SystemExit("our H1Z1.exe is running: close the game first")
    if not os.path.exists(RECORD):
        raise SystemExit(f"no {RECORD}: the map patch is not applied, nothing to restore")
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
    args = sys.argv[1:]
    whole = "--whole-map" in args
    if "--extract" in args:
        extract()
    elif "--apply" in args:
        if "--source" in args:
            raise SystemExit("--source is for dry runs only; --apply always uses " + SOURCE)
        apply(whole)
    elif "--restore" in args:
        restore()
    else:
        src = args[args.index("--source") + 1] if "--source" in args else SOURCE
        dry_run(src, whole)
