r"""Add ZRevive's own cosmetic items to a STOCK client's datasheets, so CUSTOMIZE lists them.

Read re\zr_items_spec.py first: it holds the measurements, the scope (14 skins we own, NOT the
219-row ROTK gap, which cannot be authored from our data) and the provenance of every field.

Four datasheets inside one pack2 (data_x64_0 in both stock and the ROTK tree, but located by name
hash, never by index - the two files are 2,885,216 vs 61,857,968 bytes, i.e. completely different):

    ClientItemDefinitions.txt            + 28 rows  (one ACCOUNT/skin + one WORLD row per skin)
    AcctItemConversions.txt              + 14 rows  (account -> world; this is what the grid reads)
    AcctItemConversionGroupMappings.txt  + 14 rows  (conversion -> the host's own input group)
    AcctItemConversionInputItems.txt     + 14 rows  (our world item as a grinder input, for parity)

Every non-identifying column is COPIED from the player's own stock host row, so the result is
Daybreak-valid for that garment by construction. Nothing is shipped: this is a recipe.

Pairs with re\patch_locale_stock.py, which adds the 14 name strings (ids 900001..900014). Run the
locale patch too, or the grid shows blank names.

Safety, as patch_hide_rides.py: never C:\Games\ROTK or anything under steamapps; refuses while that
install's H1Z1.exe runs; the pack must have st_nlink == 1 and realpath == path; a full backup goes
to <repo>\backup\ first; the patch is built into a temp file next to the pack, read back and
verified with an independent reader, then os.replace - never written in place. Original bytes stay:
new blobs and a new map are appended and only the header plus the four changed map rows differ.

    python -B re\patch_zr_items.py --root PATH              # dry run: build + verify in a temp dir
    python -B re\patch_zr_items.py --root PATH --apply
    python -B re\patch_zr_items.py --root PATH --restore
"""
import hashlib
import json
import os
import shutil
import struct
import sys
import tempfile
import zlib

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pack2 import Pack2, name_hash  # noqa: E402
import zr_install as ZI  # noqa: E402
import zr_items_spec as SPEC  # noqa: E402

ROOT = ZI.resolve_root()
BACKUP_DIR = ZI.BACKUP_DIR
RECORD = os.path.join(BACKUP_DIR, "zr_items.json" if os.path.normcase(ROOT) ==
                      os.path.normcase(ZI.DEFAULT_ROOT) else
                      f"zr_items.{os.path.basename(ROOT).lower()}.json")

CID = "ClientItemDefinitions.txt"
CONV = "AcctItemConversions.txt"
GMAP = "AcctItemConversionGroupMappings.txt"
INPUT = "AcctItemConversionInputItems.txt"
SHEETS = [CID, CONV, GMAP, INPUT]


def _skins():
    """zr_skins.SKINS without importing PIL/numpy at module import time."""
    import zr_skins
    return zr_skins.SKINS


# --------------------------------------------------------------------------- sheet helpers

def parse(raw):
    """-> (header line, {id: row bytes}, [row bytes in file order]). Rows are '^'-separated."""
    header, rows, order = None, {}, []
    for line in raw.split(b"\r\n"):
        if line.startswith(b"#"):
            if header is None:
                header = line
            order.append(line)
        elif line:
            f = line.split(b"^")
            if f and f[0].isdigit():
                rows[int(f[0])] = line
            order.append(line)
    return header, rows, order


def columns(header):
    return header.decode("latin-1").lstrip("#*").rstrip("^").split("^")


def set_col(row, cols, name, value):
    f = row.split(b"^")
    i = cols.index(name)
    if i >= len(f):
        raise SystemExit(f"column {name} at {i} but the row has {len(f)} fields")
    f[i] = str(value).encode("latin-1")
    return b"^".join(f)


def serialise(order, extra):
    """Rebuild a sheet: the original lines in their original order, then our new rows appended."""
    return b"\r\n".join(order + extra)


# --------------------------------------------------------------------------- building the rows

def build_rows(packpath):
    """-> ({sheet: new bytes}, report). Reads the install's own rows as templates."""
    p = Pack2(packpath)
    try:
        raw = {s: p.read(s) for s in SHEETS}
    finally:
        p.f.close()

    cid_hdr, cid_rows, cid_order = parse(raw[CID])
    cv_hdr, cv_rows, cv_order = parse(raw[CONV])
    gm_hdr, gm_rows, gm_order = parse(raw[GMAP])
    ii_hdr, ii_rows, ii_order = parse(raw[INPUT])
    cid_cols = columns(cid_hdr)

    # conversions indexed by the world item they reward, and each conversion's input group
    by_reward = {}
    for k, l in cv_rows.items():
        by_reward.setdefault(int(l.split(b"^")[3]), []).append(k)
    group_of = {int(l.split(b"^")[1]): int(l.split(b"^")[2]) for l in gm_rows.values()}

    items = SPEC.items(_skins())
    next_cid = max(cid_rows) + 1          # only used to report, our ids are fixed
    next_cv, next_gm, next_ii = max(cv_rows) + 1, max(gm_rows) + 1, max(ii_rows) + 1

    new_cid, new_cv, new_gm, new_ii, report = [], [], [], [], []
    for it in items:
        host = cid_rows.get(it["host"])
        if host is None:
            raise SystemExit(f"{it['name']}: stock host item {it['host']} is not in {CID} of this "
                             f"install - this patch does not apply to this build")
        if it["world"] in cid_rows or it["acct"] in cid_rows:
            raise SystemExit(f"{it['name']}: id {it['world']}/{it['acct']} already exists in {CID} "
                             "(already patched, or a ROTK-derived client); refusing to collide")

        host_class = host.split(b"^")[cid_cols.index("ITEM_CLASS")].decode()
        # the host's own AccountRecipe partner, else a same-class stock donor
        convs = by_reward.get(it["host"], [])
        acct_src_id = int(cv_rows[convs[0]].split(b"^")[1]) if convs else \
            SPEC.ACCOUNT_DONOR_BY_CLASS.get(host_class)
        group = group_of.get(convs[0]) if convs else None
        if group is None:
            # The host has no conversion of its own, so take the input group from the donor
            # account item's conversion instead (same garment class, so the same group).
            for k, l in cv_rows.items():
                if int(l.split(b"^")[1]) == acct_src_id:
                    group = group_of.get(k)
                    break
        acct_src = cid_rows.get(acct_src_id)
        if acct_src is None or group is None:
            raise SystemExit(f"{it['name']}: no stock AccountRecipe template (tried host partner "
                             f"and class {host_class} donor) or no input group; refusing to guess")

        # WORLD row: the stock host's row with our id and our name
        w = set_col(host, cid_cols, "ID", it["world"])
        w = set_col(w, cid_cols, "NAME_ID", it["name_id"])
        new_cid.append(w)
        # ACCOUNT/skin row: the stock account template with our id and our name
        a = set_col(acct_src, cid_cols, "ID", it["acct"])
        a = set_col(a, cid_cols, "NAME_ID", it["name_id"])
        new_cid.append(a)

        # conversion: account -> world, count 1, no tint/effect, no reward set
        new_cv.append(f"{next_cv}^{it['acct']}^0^{it['world']}^0^1^0^0^".encode("latin-1"))
        new_gm.append(f"{next_gm}^{next_cv}^{group}^".encode("latin-1"))
        new_ii.append(f"{next_ii}^{group}^{it['world']}^".encode("latin-1"))
        report.append(dict(name=it["name"], acct=it["acct"], world=it["world"], host=it["host"],
                           acctTemplate=acct_src_id, itemClass=host_class, group=group,
                           conversion=next_cv, nameId=it["name_id"], stem=it["stem"]))
        next_cv += 1
        next_gm += 1
        next_ii += 1

    out = {
        CID: serialise(cid_order, new_cid),
        CONV: serialise(cv_order, new_cv),
        GMAP: serialise(gm_order, new_gm),
        INPUT: serialise(ii_order, new_ii),
    }
    # sanity: we only ever grow, and only by the rows we meant to add
    for s, n in ((CID, len(new_cid)), (CONV, len(new_cv)), (GMAP, len(new_gm)), (INPUT, len(new_ii))):
        before = len(parse(raw[s])[1])
        after = len(parse(out[s])[1])
        if after != before + n:
            raise SystemExit(f"{s}: expected {before}+{n} rows, built {after}")
    return out, report, next_cid


# --------------------------------------------------------------------------- pack writing

def stored(data, zf):
    if zf in (1, 0x11):
        return b"\xA1\xB2\xC3\xD4" + struct.pack(">I", len(data)) + zlib.compress(data, 9)
    return data


def build_pack(src, dst, new):
    """Append new contents for `new` (name -> bytes) to a copy of src at dst, with a fresh map."""
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
    count = p.count
    p.f.close()
    header = struct.pack("<4sIQQ", b"PAK\x01", count, off + len(mapping), off)
    with open(src, "rb") as s, open(dst, "wb") as d:
        shutil.copyfileobj(s, d, 1 << 22)
        for b in blobs:
            d.write(b)
        d.write(mapping)
        d.seek(0)
        d.write(header)
        d.flush()
        os.fsync(d.fileno())


def verify(dst, src, new, report):
    """Independent read-back: our rows are there, every other entry is untouched, bytes intact."""
    a, b = Pack2(src), Pack2(dst)
    try:
        assert b.count == a.count and os.path.getsize(dst) == b.length == b.map_off + 32 * b.count
        changed = {h for h in a.entries if a.entries[h] != b.entries[h]}
        assert changed == {name_hash(n) for n in new}, changed
        for n, data in new.items():
            off, ln, zf, crc = b.entries[name_hash(n)]
            b.f.seek(off)
            assert zlib.crc32(b.f.read(ln)) == crc, f"{n}: crc"
            assert b.read(n) == data, f"{n}: round-trip"
        # the new rows parse and resolve
        _, cid_rows, _ = parse(b.read(CID))
        _, cv_rows, _ = parse(b.read(CONV))
        _, gm_rows, _ = parse(b.read(GMAP))
        cols = columns(parse(b.read(CID))[0])
        convs_by_acct = {int(l.split(b"^")[1]): (k, l) for k, l in cv_rows.items()}
        groups = {int(l.split(b"^")[1]) for l in gm_rows.values()}
        for r in report:
            assert r["acct"] in cid_rows, f"account row {r['acct']} missing"
            assert r["world"] in cid_rows, f"world row {r['world']} missing"
            for which in ("acct", "world"):
                f = cid_rows[r[which]].split(b"^")
                assert int(f[cols.index("ID")]) == r[which]
                assert int(f[cols.index("NAME_ID")]) == r["nameId"]
            k, l = convs_by_acct[r["acct"]]
            assert int(l.split(b"^")[3]) == r["world"], f"conversion for {r['acct']} rewards wrong item"
            assert k in groups, f"conversion {k} has no group mapping"
        # every pre-existing row still byte-identical
        for s in SHEETS:
            _, before, _ = parse(a.read(s))
            _, after, _ = parse(b.read(s))
            for i, row in before.items():
                assert after.get(i) == row, f"{s}: pre-existing row {i} changed"
        with open(src, "rb") as fa, open(dst, "rb") as fb:
            fa.seek(24)
            fb.seek(24)
            left = a.length - 24
            while left:
                k = min(left, 1 << 22)
                assert fa.read(k) == fb.read(k), "original bytes changed"
                left -= k
    finally:
        a.f.close()
        b.f.close()
    return True


# --------------------------------------------------------------------------- commands

def locate():
    packs = {ZI.find_pack(ROOT, s) for s in SHEETS}
    if len(packs) != 1:
        raise SystemExit(f"the four datasheets are spread over {packs}; this patch assumes one pack")
    return packs.pop()


def show(report, sizes):
    print(f"{'skin':34s} {'acct':>6} {'world':>6} {'host':>6} {'acctTpl':>8} {'class':>6} {'grp':>4} {'nameId':>8}")
    for r in report:
        print(f"{r['name']:34s} {r['acct']:>6} {r['world']:>6} {r['host']:>6} "
              f"{r['acctTemplate']:>8} {r['itemClass']:>6} {r['group']:>4} {r['nameId']:>8}")
    for s, (b, a) in sizes.items():
        print(f"  {s:38s} {b} -> {a} bytes")


def dry_run():
    pack = locate()
    ZI.guard(pack, ROOT)
    new, report, _ = build_rows(pack)
    p = Pack2(pack)
    try:
        sizes = {s: (len(p.read(s)), len(new[s])) for s in SHEETS}
    finally:
        p.f.close()
    with tempfile.TemporaryDirectory() as td:
        out = os.path.join(td, os.path.basename(pack))
        build_pack(pack, out, new)
        verify(out, pack, new, report)
    show(report, sizes)
    print(f"\npack located by name: {pack}")
    print(f"verified in a temp copy: {len(report)} skins, "
          f"{2 * len(report)} item rows + {3 * len(report)} conversion rows, original bytes intact")
    print(f"dry run only: nothing in {ROOT} was written. Then --apply, and run "
          "patch_locale_stock.py for the names.")


def apply():
    pack = locate()
    ZI.guard(pack, ROOT)
    if ZI.game_running(ROOT):
        raise SystemExit(f"an H1Z1.exe of {ROOT} is running: close the game first, nothing written")
    if os.path.exists(RECORD):
        raise SystemExit(f"{RECORD} exists: already applied (--restore first)")
    new, report, _ = build_rows(pack)
    backup = ZI.backup(pack, "pre-zr-items", ROOT)
    digest = ZI.sha256(pack)
    rec = {"pack": pack, "backup": backup, "originalSha256": digest, "report": report}
    os.makedirs(BACKUP_DIR, exist_ok=True)
    json.dump(rec, open(RECORD, "w"), indent=1)   # written before the pack changes
    tmp = pack + ".zrtmp"
    try:
        build_pack(pack, tmp, new)
        verify(tmp, pack, new, report)
        ZI.guard(pack, ROOT)
        if ZI.game_running(ROOT):
            raise SystemExit("the game was started meanwhile: stopping, the pack is unchanged")
        os.replace(tmp, pack)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)
    ZI.guard(pack, ROOT)
    rec["patchedSha256"] = ZI.sha256(pack)
    json.dump(rec, open(RECORD, "w"), indent=1)
    print(f"patched {pack}: {len(report)} ZRevive skins defined "
          f"({2 * len(report)} item rows, {3 * len(report)} conversion rows)")
    print(f"backup {backup}; record {RECORD}")
    print("now run: python -B re\\patch_locale_stock.py --root " + ROOT)


def restore():
    if ZI.game_running(ROOT):
        raise SystemExit(f"an H1Z1.exe of {ROOT} is running: close the game first")
    if not os.path.exists(RECORD):
        raise SystemExit("not applied, nothing to restore")
    rec = json.load(open(RECORD))
    pack = rec["pack"]
    ZI.guard(pack, ROOT)
    cur = ZI.sha256(pack)
    if cur == rec["originalSha256"]:
        print(f"{pack}: already original")
    else:
        if cur != rec.get("patchedSha256"):
            raise SystemExit(f"{pack} is not the file this script patched; not touching it")
        if ZI.sha256(rec["backup"]) != rec["originalSha256"]:
            raise SystemExit("backup does not match its recorded hash; not restoring")
        tmp = pack + ".zrtmp"
        shutil.copyfile(rec["backup"], tmp)
        os.replace(tmp, pack)
        ZI.guard(pack, ROOT)
        print(f"restored {pack} from {rec['backup']}")
    os.remove(RECORD)


if __name__ == "__main__":
    if "--apply" in sys.argv:
        apply()
    elif "--restore" in sys.argv:
        restore()
    else:
        dry_run()
