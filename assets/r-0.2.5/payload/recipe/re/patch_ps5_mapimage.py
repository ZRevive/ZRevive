"""Show the Pre-Season 5 (Aug 2017) world map for Z2 in OUR client copy (C:\\Games\\ZRevive) only.

Facts (traced 2026-10-06):
  * The Aug-2017 PS5 client's MapImageZ2.gfx (Assets_235.pack of depot 433851 manifest 6373368576374184611)
    draws img8973010328128190133.dds (1024x1024 DXT5, borders with the A-J / 1-10 grid). That texture is
    still in our packs (assets_x64_0 / assets_x64_1, the copies ROTK kept), byte-identical, so nothing from
    the 2017 download is needed here.
  * Our 2019 UI: MapWindow.gfx loads MapImage_Z2_8k.swf (4 tiles 4096x4096: img6438.. top-left,
    img9729.. top-right, img2799.. bottom-left, img10849.. bottom-right) and MapImage_<zone>.swf;
    MapImage_Z2.gfx draws img7491.. (2048, no grid border), MapImageZ2.gfx img5036.. (2048, grid border),
    MapImage_overlay_Z2.gfx img4832.. (2048, roads/labels/Combat Update icons on transparent).
  * All of these cover the same square: the 2019 plain map matches the bordered one with crop (0, 0, 1),
    also against the PS5 image (re-checked by edge correlation, ps5man\\mapcal.py).

What it does: every copy of those textures (in every pack that has the name) gets new content of the
SAME size and format (DXT5, original 128-byte DDS header kept):
    img5036.., img7491..     = PS5 map upscaled 2x (Lanczos)
    the four 8k tiles         = the matching PS5 quadrant upscaled 8x
    img4832.. (overlay)       = fully transparent, so no 2019 roads/icons are drawn over the PS5 map
The movies themselves are not changed. Same size and format means the movies' image sizes still fit.

Safety (same scheme as patch_ps5_map.py / patch_hide_rides.py):
  * never C:\\Games\\ROTK or anything under steamapps; refuses while our H1Z1.exe runs (checked again right
    before each os.replace);
  * every pack: st_nlink == 1 and realpath == path; full backup to C:\\Users\\Gusta\\ZRevive\\backup\\
    (sha256-checked) first, then a temp file next to the pack, verified, then os.replace;
  * original bytes stay; new blobs + a new map are appended, only the header and the touched map rows change.

    python patch_ps5_mapimage.py              # dry run: build + verify every pack in a temp folder,
                                              # writes preview PNGs to %TEMP%\\zr_ps5_mapimage_preview
    python patch_ps5_mapimage.py --apply      # game closed
    python patch_ps5_mapimage.py --restore    # game closed: put the backed-up packs back
"""
import hashlib
import io
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
import patch_ps5_map as base  # guard(), game_running(), sha256()  # noqa: E402

try:
    from PIL import Image
except ImportError:  # pragma: no cover
    raise SystemExit("needs Pillow 11+ (pip install pillow): it encodes the DXT5 textures")

import zr_install as ZI  # noqa: E402

ROOT = ZI.resolve_root()
ASSETS = ZI.assets_dir(ROOT)
BACKUP_DIR = ZI.BACKUP_DIR
RECORD = os.path.join(BACKUP_DIR, "ps5_mapimage.json" if os.path.normcase(ROOT) ==
                      os.path.normcase(ZI.DEFAULT_ROOT) else
                      f"ps5_mapimage.{os.path.basename(ROOT).lower()}.json")
PS5_MAP = "img8973010328128190133.dds"
# Where the source map may also be found once it has been fetched from the 2017 depot
# (patch_ps5_map.py --extract writes it here).
PS5_MAP_REF = os.path.join(ZI.REPO, "ref", "ps5-2017", PS5_MAP)
OVERLAY_ONLY = "--overlay-only" in sys.argv
FULL = ["img5036265743661002417.dds", "img7491279441200264424.dds"]
TILES = {"img6438417686747091404.dds": (0, 0), "img9729741037893190798.dds": (1, 0),
         "img2799690159767973169.dds": (0, 1), "img10849394225470804997.dds": (1, 1)}
OVERLAY = "img4832946094318338635.dds"
TARGETS = FULL + list(TILES) + [OVERLAY]


def packs_with(names):
    out = {}
    for fn in sorted(os.listdir(ASSETS)):
        if not fn.endswith(".pack2"):
            continue
        p = Pack2(os.path.join(ASSETS, fn))
        hit = [n for n in names if name_hash(n) in p.entries]
        p.f.close()
        if hit:
            out[os.path.join(ASSETS, fn)] = hit
    return out


def read_any(name):
    """Read `name` from whatever pack of THIS install holds it; for the PS5 source map, fall back to
    the 2017 depot extraction in ref\\ps5-2017\\."""
    for path in packs_with([name]):
        p = Pack2(path)
        d = p.read(name)
        p.f.close()
        return d
    if name == PS5_MAP and os.path.exists(PS5_MAP_REF):
        return open(PS5_MAP_REF, "rb").read()
    raise SystemExit(f"{name} is not in any pack of {ROOT}")


def have_source():
    """Is the 2017 source map available at all for this install?"""
    return bool(packs_with([PS5_MAP])) or os.path.exists(PS5_MAP_REF)


def dds_info(d):
    if d[:4] != b"DDS " or d[84:88] != b"DXT5":
        raise SystemExit("expected a DXT5 DDS")
    h, w = struct.unpack_from("<II", d, 12)
    return w, h


def encode_like(orig, im):
    """DXT5 payload for im (resized to orig's size) behind orig's own 128-byte header."""
    w, h = dds_info(orig)
    if im.size != (w, h):
        im = im.resize((w, h), Image.LANCZOS)
    b = io.BytesIO()
    im.convert("RGBA").save(b, "DDS", pixel_format="DXT5")
    d = b.getvalue()
    if len(d) != len(orig):
        raise SystemExit(f"encoded size {len(d)} != original {len(orig)}")
    return orig[:128] + d[128:]


def build_textures():
    if OVERLAY_ONLY:
        # Only the fully transparent 2019 overlay, which is ours and needs no source texture: it stops
        # the 2019 roads / labels / Combat Update icons being drawn over a PS5-layout Z2. The map art
        # itself stays the stock 2019 art. This is the only half of this patch that can run on a stock
        # Z1 Battle Royale baseline, because PS5_MAP is absent there (see the module docstring).
        ov = read_any(OVERLAY)
        return {OVERLAY: encode_like(ov, Image.new("RGBA", dds_info(ov), (0, 0, 0, 0)))}, None
    ps5 = read_any(PS5_MAP)
    if dds_info(ps5) != (1024, 1024):
        raise SystemExit(f"{PS5_MAP}: not the 1024x1024 PS5 map")
    src = Image.open(io.BytesIO(ps5)).convert("RGBA")
    out = {}
    for n in FULL:
        out[n] = encode_like(read_any(n), src)
    q = src.size[0] // 2
    for n, (cx, cy) in TILES.items():
        out[n] = encode_like(read_any(n), src.crop((cx * q, cy * q, cx * q + q, cy * q + q)))
    ov = read_any(OVERLAY)
    out[OVERLAY] = encode_like(ov, Image.new("RGBA", dds_info(ov), (0, 0, 0, 0)))
    return out, hashlib.sha256(ps5).hexdigest()


def stored(data, zf):
    if zf in (1, 0x11):
        return b"\xA1\xB2\xC3\xD4" + struct.pack(">I", len(data)) + zlib.compress(data, 9)
    return data


def build_pack(src, dst, new):
    """Append new contents for the names in `new` (name -> bytes) to a copy of src at dst."""
    p = Pack2(src)
    size = os.path.getsize(src)
    if p.map_off + 32 * p.count != size or p.length != size:
        raise SystemExit(f"{src}: map is not at the end of the file, refusing")
    p.f.seek(p.map_off)
    mapping = bytearray(p.f.read(32 * p.count))
    rows = {struct.unpack_from("<Q", mapping, 32 * i)[0]: i for i in range(p.count)}
    blobs, off = [], size
    for n, data in new.items():
        h = name_hash(n)
        zf = p.entries[h][2]
        blob = stored(data, zf)
        struct.pack_into("<QQQII", mapping, 32 * rows[h], h, off, len(blob), zf, zlib.crc32(blob))
        blobs.append(blob)
        off += len(blob)
    p.f.close()
    header = struct.pack("<4sIQQ", b"PAK\x01", p.count, off + len(mapping), off)
    with open(src, "rb") as s, open(dst, "wb") as d:
        shutil.copyfileobj(s, d, 1 << 22)
        for b in blobs:
            d.write(b)
        d.write(mapping)
        d.seek(0)
        d.write(header)
        d.flush()
        os.fsync(d.fileno())


def verify_pack(path, original, new):
    a, b = Pack2(original), Pack2(path)
    assert b.count == a.count and os.path.getsize(path) == b.length == b.map_off + 32 * b.count
    want = {name_hash(n) for n in new}
    assert {k for k in a.entries if a.entries[k] != b.entries[k]} == want
    for n, data in new.items():
        off, ln, zf, crc = b.entries[name_hash(n)]
        b.f.seek(off)
        assert zlib.crc32(b.f.read(ln)) == crc
        got = b.read(n)
        assert got == data, n
        Image.open(io.BytesIO(got)).load()
    with open(original, "rb") as fa, open(path, "rb") as fb:
        fa.seek(24)
        fb.seek(24)
        left = a.length - 24
        while left:
            k = min(left, 1 << 22)
            assert fa.read(k) == fb.read(k), "original bytes changed"
            left -= k
    a.f.close()
    b.f.close()


def plan():
    if not OVERLAY_ONLY and not have_source():
        raise SystemExit(
            f"{PS5_MAP} (the 2017 source map) is in no pack of {ROOT} and not at {PS5_MAP_REF}.\n"
            "A stock Z1 Battle Royale install does not contain it - only a ROTK-derived tree does.\n"
            "Either fetch it from the 2017 depot (see re\\patch_ps5_map.py for the DepotDownloader\n"
            "command, then --extract), or run this with --overlay-only to apply just the transparent\n"
            "2019 overlay, which needs no source texture.")
    tex, ps5sha = build_textures()
    want = list(tex)
    where = packs_with(want)
    for path in where:
        base.guard(path)
    missing = set(want) - {n for v in where.values() for n in v}
    if missing:
        raise SystemExit(f"not found in any pack of {ROOT}: {sorted(missing)}")
    return tex, where, ps5sha


def dry_run():
    tex, where, ps5sha = plan()
    prev = os.path.join(tempfile.gettempdir(), "zr_ps5_mapimage_preview")
    os.makedirs(prev, exist_ok=True)
    for n, d in tex.items():
        im = Image.open(io.BytesIO(d))
        im.resize((512, 512)).save(os.path.join(prev, n.replace(".dds", ".png")))
    with tempfile.TemporaryDirectory() as td:
        for path, names in where.items():
            out = os.path.join(td, os.path.basename(path))
            build_pack(path, out, {n: tex[n] for n in names})
            verify_pack(out, path, {n: tex[n] for n in names})
            os.remove(out)
            print(f"{os.path.basename(path):20} {len(names)} texture(s) {names}: verified")
    print(f"PS5 map source: {PS5_MAP} sha256 {ps5sha}" if ps5sha else
          "overlay-only mode: no 2017 source texture used, the stock 2019 map art is kept")
    print(f"previews (512 px) in {prev}")
    print(f"dry run only: nothing in {ROOT} was written. Close the game, then --apply.")


def apply():
    if base.game_running():
        raise SystemExit("our H1Z1.exe is running: close the game first, nothing was written")
    if os.path.exists(RECORD):
        raise SystemExit(f"{RECORD} exists: already applied (--restore first)")
    tex, where, ps5sha = plan()
    os.makedirs(BACKUP_DIR, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    rec = {"at": stamp, "ps5MapSha256": ps5sha, "packs": {}}
    for path, names in where.items():
        backup = os.path.join(BACKUP_DIR, f"{os.path.basename(path)}.pre-ps5-mapimage-{stamp}")
        shutil.copyfile(path, backup)
        digest = base.sha256(path)
        if base.sha256(backup) != digest:
            raise SystemExit(f"backup of {path} differs, stopping")
        rec["packs"][path] = {"backup": backup, "originalSha256": digest, "names": names}
    json.dump(rec, open(RECORD, "w"), indent=1)  # written before any pack changes, so --restore always knows
    for path, names in where.items():
        tmp = path + ".zrtmp"
        new = {n: tex[n] for n in names}
        try:
            build_pack(path, tmp, new)
            verify_pack(tmp, path, new)
            base.guard(path)
            if base.game_running():
                raise SystemExit("the game was started meanwhile: stopping (packs done so far: see --restore)")
            os.replace(tmp, path)
        finally:
            if os.path.exists(tmp):
                os.remove(tmp)
        base.guard(path)
        rec["packs"][path]["patchedSha256"] = base.sha256(path)
        json.dump(rec, open(RECORD, "w"), indent=1)
        print(f"patched {os.path.basename(path)}: {names}")
    print(f"done; backups and record in {BACKUP_DIR} ({os.path.basename(RECORD)})")


def restore():
    if base.game_running():
        raise SystemExit("our H1Z1.exe is running: close the game first")
    if not os.path.exists(RECORD):
        raise SystemExit("not applied, nothing to restore")
    rec = json.load(open(RECORD))
    for path, r in rec["packs"].items():
        base.guard(path)
        cur = base.sha256(path)
        if cur == r["originalSha256"]:
            print(f"{os.path.basename(path)}: already original")
            continue
        if cur != r.get("patchedSha256"):
            raise SystemExit(f"{path} is not the file this script patched: not touching it (record kept)")
        if base.sha256(r["backup"]) != r["originalSha256"]:
            raise SystemExit(f"backup {r['backup']} does not match its hash: not restoring")
    for path, r in rec["packs"].items():
        if base.sha256(path) == r["originalSha256"]:
            continue
        tmp = path + ".zrtmp"
        shutil.copyfile(r["backup"], tmp)
        if base.game_running():
            os.remove(tmp)
            raise SystemExit("the game was started meanwhile: stopping")
        os.replace(tmp, path)
        base.guard(path)
        print(f"restored {os.path.basename(path)}")
    os.remove(RECORD)


if __name__ == "__main__":
    if "--apply" in sys.argv:
        apply()
    elif "--restore" in sys.argv:
        restore()
    else:
        dry_run()
