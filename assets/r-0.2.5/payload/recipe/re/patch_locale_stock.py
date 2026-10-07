"""Rebrand the locale of a STOCK Z1 Battle Royale install to ZRevive.

This replaces patch_locale_rotk.py for the stock baseline. They are not interchangeable:

    patch_locale_rotk.py  renames ROTK's OWN appended strings (ids 800001-800111, 69 of which carry
                          "ROTK"). A stock Z1BR locale does not have those ids at all - stock en_us
                          holds 8,254 records, the ROTK-derived one 14,001 - so that script finds
                          nothing to do here and must not be used on a stock install.
    this script           renames the strings a stock install actually shows: Daybreak's own product
                          branding, "King of the Kill" / "KotK" / "KOTK". Measured 2026-10-07 on
                          <stock>\\Locale\\en_us_data.dat: 40 records match, listed in SKIP/EXPECT below.

Rules, deliberately narrow so the diff stays auditable:
  * "King of the Kill" -> "ZRevive", "KotK"/"KOTK" -> "ZRevive"/"ZREVIVE" (ALL-CAPS preserved).
    Only those two tokens. Done as one regex so a string like "H1Z1: King of the Kill" collapses
    correctly, and "Team KotK - 2 Person" keeps its shape.
  * "H1Z1" is NOT touched. In this locale it is both the in-fiction virus ("Vial of H1Z1 Infected
    Blood", the immunity tooltips - 110 records) and a Daybreak trademark. Renaming it would corrupt
    item lore for no branding gain.
  * SKIP keeps Daybreak's trademark attribution intact. Removing a copyright notice is not a rebrand.

Format handling (the .dat/.dir pair, the .dir's md5-of-.dat and per-record offsets) is reused from
patch_locale_rotk.parse, which is build-independent and already verified against these files.

    python re\\patch_locale_stock.py --root PATH --dry-run    show the diff, write nothing
    python re\\patch_locale_stock.py --root PATH              patch (idempotent)
    python re\\patch_locale_stock.py --root PATH --restore    put the backed-up originals back

Originals go to <repo>\\backup\\locale_stock\\ with a manifest of original+patched sha256. Files are
written as a temp file + os.replace, never in place, and never when shared with another install.
"""
import glob
import hashlib
import json
import os
import re
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import zr_install as ZI  # noqa: E402
from patch_locale_rotk import parse  # the .dat/.dir format, build-independent  # noqa: E402

ROOT = ZI.resolve_root()
LOCALE = ZI.locale_dir(ROOT)
BACKUP_DIR = os.path.join(ZI.BACKUP_DIR, "locale_stock")
MANIFEST = os.path.join(BACKUP_DIR, "manifest.json")
BOM = b"\xef\xbb\xbf"

NEW = b"ZRevive"
NEW_UPPER = b"ZREVIVE"
BRAND = re.compile(rb"King of the Kill|\bKOTK\b|\bKotK\b", re.I)
# "H1Z1: King of the Kill" -> "ZRevive", not "H1Z1: ZRevive".
PREFIXED = re.compile(rb"\bH1Z1\s*:\s*(?=ZRevive)", re.I)

# Daybreak's trademark attribution. It names the brands as trademarks; it is not UI branding.
SKIP = {
    317392602: "(c)2016 Daybreak trademark attribution - must stay verbatim",
}

# A few records use the brand as a noun phrase ("to be the King of the Kill"), where a plain token
# swap reads wrong. Keyed by locale string hash, these texts win over the regex.
OVERRIDES = {
    631291619: b"To be the champion you must be the last one standing. "
               b"Defeat your opponents without mercy.",
}

# What a stock en_us is expected to contain, so a build that differs is reported instead of
# silently half-patched. Measured 2026-10-07.
EXPECT_EN_US = 40


def sha(b):
    return hashlib.sha256(b).hexdigest()


def rename(text, sid):
    """The brand substitution for one record's text. -> new text (bytes)."""
    if sid in SKIP:
        return text
    if sid in OVERRIDES:
        return OVERRIDES[sid]

    def sub(m):
        return NEW_UPPER if m.group(0).isupper() else NEW

    out = BRAND.sub(sub, text)
    return PREFIXED.sub(b"", out)


def additions():
    """{locale string hash: (string id, text)} for the records we ADD, not rename.

    These are ZRevive's own item names, for the cosmetic rows re\\patch_zr_items.py writes into the
    client's datasheets. Without them the CUSTOMIZE grid would list our skins with blank names,
    because their NAME_IDs (our own 900001.. band) do not exist in any stock locale.
    """
    try:
        import zr_items_spec
        import zr_skins
    except Exception as e:                       # PIL/numpy missing -> just skip the additions
        print(f"   NOTE: item name additions skipped ({e})")
        return {}
    out = {}
    for sid, text in zr_items_spec.name_strings(zr_skins.SKINS).items():
        out[fl_locale_key(sid)] = (sid, text.encode("utf-8"))
    return out


def fl_locale_key(sid):
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import fl_locale
    return fl_locale.string_key(sid)


def rebuild(dat, dirb, extra=None):
    """-> (new dat, new dir, [(id, old, new)], [(id, added text)]).

    Renames in place, then APPENDS any `extra` records the locale does not already have. New
    records go at the end of the .dat with their own .dir rows, so every existing record keeps its
    text and the pair stays consistent (the .dir's md5-of-.dat and TextLength are recomputed).
    """
    extra = {} if extra is None else extra
    header, rows = parse(dat, dirb)
    out = bytearray(BOM if dat.startswith(BOM) else b"")
    where, changes, longest = {}, [], 0
    present = set()
    for h, off, n, _ in sorted(rows, key=lambda r: r[1]):
        rec = dat[off:off + n]
        hh, typ, text = rec.split(b"\t", 2)
        present.add(int(hh))
        new = rename(text, int(hh))
        if new != text:
            changes.append((int(hh), text.decode("utf-8"), new.decode("utf-8")))
            rec = hh + b"\t" + typ + b"\t" + new
        longest = max(longest, len(new))
        where[h] = (len(out), len(rec))
        out += rec + b"\r\n"

    added, new_rows = [], []
    for key, (sid, text) in sorted(extra.items(), key=lambda kv: kv[1][0]):
        if key in present:                       # already there (re-run, or the id is in use)
            continue
        rec = str(key).encode() + b"\tucdt\t" + text
        where[str(key).encode()] = (len(out), len(rec))
        out += rec + b"\r\n"
        longest = max(longest, len(text))
        new_rows.append((str(key).encode(), b"d"))
        added.append((sid, text.decode("utf-8")))

    new_dat = bytes(out)
    hdr = []
    for ln in header:
        if ln.startswith(b"## MD5Checksum:"):
            ln = b"## MD5Checksum: " + hashlib.md5(new_dat).hexdigest().upper().encode()
        elif ln.startswith(b"## TextLength:"):
            ln = b"## TextLength:\t" + str(longest).encode()
        elif ln.startswith(b"## Count:"):
            ln = b"## Count:\t" + str(len(rows) + len(new_rows)).encode()
        hdr.append(ln)
    body = [b"%s\t%d\t%d\t%s" % (h, *where[h], flag) for h, _, _, flag in rows]
    body += [b"%s\t%d\t%d\t%s" % (h, *where[h], flag) for h, flag in new_rows]
    return new_dat, b"\r\n".join(hdr + body) + b"\r\n", changes, added


def verify(old_dat, old_dir, new_dat, new_dir, changes, added=()):
    """Re-parse the result: every original record in its original order with only the renamed texts
    differing, and any added records appended after them with exactly the text we asked for."""
    _, old_rows = parse(old_dat, old_dir)
    _, new_rows = parse(new_dat, new_dir)
    changed = {str(h).encode(): new.encode("utf-8") for h, _, new in changes}
    assert len(new_rows) == len(old_rows) + len(added), \
        f"row count {len(new_rows)} != {len(old_rows)} + {len(added)}"
    # the added records are the tail; the originals must keep their order
    tail, new_rows = (new_rows[len(old_rows):], new_rows[:len(old_rows)]) if added else ([], new_rows)
    assert [r[0] for r in old_rows] == [r[0] for r in new_rows], "record order changed"
    want_added = {}
    for sid, text in added:
        want_added[str(fl_locale_key(sid)).encode()] = text.encode("utf-8")
    for h, o2, n2, _ in tail:
        rec = new_dat[o2:o2 + n2]
        hh, _typ, text = rec.split(b"\t", 2)
        assert hh in want_added, f"unexpected added record {hh!r}"
        assert text == want_added.pop(hh), f"added record {hh!r} has the wrong text"
    assert not want_added, f"records we meant to add are missing: {list(want_added)}"
    for (h, o, n, _), (_, o2, n2, _) in zip(old_rows, new_rows):
        a, b = old_dat[o:o + n], new_dat[o2:o2 + n2]
        if h in changed:
            assert b.split(b"\t", 2)[2] == changed[h], h
            assert a.split(b"\t", 2)[:2] == b.split(b"\t", 2)[:2], h
        else:
            assert a == b, f"record {h!r} changed unexpectedly"
    assert b"## MD5Checksum: " + hashlib.md5(new_dat).hexdigest().upper().encode() in new_dir
    # every record that still carries the old brand must be one we deliberately skipped
    for h, o2, n2, _ in new_rows:
        rec = new_dat[o2:o2 + n2]
        sid, _, text = rec.split(b"\t", 2)
        if BRAND.search(text):
            assert int(sid) in SKIP, f"record {int(sid)} still carries the old brand but is not in SKIP"
    # and each skipped record must be byte-identical to the original
    old_by_id = {}
    for h, o, n, _ in old_rows:
        rec = old_dat[o:o + n]
        old_by_id[int(rec.split(b"\t", 2)[0])] = rec
    for h, o2, n2, _ in new_rows:
        rec = new_dat[o2:o2 + n2]
        sid = int(rec.split(b"\t", 2)[0])
        if sid in SKIP:
            assert rec == old_by_id[sid], f"skipped record {sid} ({SKIP[sid]}) was modified"


def load_manifest():
    return json.load(open(MANIFEST, encoding="utf-8")) if os.path.exists(MANIFEST) else {}


def save_manifest(m):
    tmp = MANIFEST + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(m, f, indent=1)
    os.replace(tmp, MANIFEST)


def backup(name, data, manifest):
    dst = os.path.join(BACKUP_DIR, name)
    rec = manifest.get(name)
    if rec:
        if not os.path.exists(dst) or sha(open(dst, "rb").read()) != rec["original"]:
            raise SystemExit(f"backup {dst} is missing or not the recorded original; not touching {name}")
        if sha(data) != rec["original"]:
            raise SystemExit(f"{name} is neither the original nor patched by this script; not touching it")
        return
    with open(dst + ".tmp", "wb") as f:
        f.write(data)
    os.replace(dst + ".tmp", dst)
    manifest[name] = {"original": sha(data), "size": len(data)}


def patch(dry):
    os.makedirs(BACKUP_DIR, exist_ok=True)
    manifest = load_manifest()
    total = 0
    # ZRevive's own item names, added to every language (one English text, as ROTK's own appended
    # strings were) so the CUSTOMIZE grid has names whatever locale the client runs in.
    extra = additions()
    if extra:
        print(f"adding {len(extra)} ZRevive item name string(s) per language "
              f"(ids {min(v[0] for v in extra.values())}..{max(v[0] for v in extra.values())})")
    files = sorted(glob.glob(os.path.join(LOCALE, "*_data.dat")))
    if not files:
        raise SystemExit(f"{LOCALE}: no *_data.dat, that is not a game Locale folder")
    for dat_path in files:
        dir_path = dat_path[:-4] + ".dir"
        dn, rn = os.path.basename(dat_path), os.path.basename(dir_path)
        ZI.guard(dat_path, ROOT)
        ZI.guard(dir_path, ROOT)
        dat, dirb = open(dat_path, "rb").read(), open(dir_path, "rb").read()
        # already patched by an earlier run? re-patch from the backed-up original, so a changed
        # rule set applies cleanly and --restore still goes back to the true original
        for name, which in ((dn, "dat"), (rn, "dir")):
            rec = manifest.get(name)
            cur = dat if which == "dat" else dirb
            if rec and rec.get("patched") == sha(cur):
                orig = open(os.path.join(BACKUP_DIR, name), "rb").read()
                if sha(orig) != rec["original"]:
                    raise SystemExit(f"backup of {name} does not match the recorded original; not touching it")
                if which == "dat":
                    dat = orig
                else:
                    dirb = orig
        new_dat, new_dir, changes, added = rebuild(dat, dirb, extra)
        if dn.startswith("en_us") and len(changes) != EXPECT_EN_US:
            print(f"   NOTE {dn}: {len(changes)} strings match, expected {EXPECT_EN_US} for the "
                  "2026-08-25 stock build - different client build?")
        cur_dat, cur_dir = open(dat_path, "rb").read(), open(dir_path, "rb").read()
        if new_dat == cur_dat and new_dir == cur_dir:
            print(f"{dn}: already patched exactly like this")
            continue
        if not changes and not added:
            print(f"{dn}: nothing to rename or add")
            continue
        verify(dat, dirb, new_dat, new_dir, changes, added)
        total += len(changes)
        print(f"{dn}: {len(changes)} renamed, {len(added)} added" + (" (dry run)" if dry else ""))
        if dry:
            if dn.startswith("en_us"):
                for h, a, b in changes:
                    print(f"   {h:>12}  {a[:70]!r}\n                 -> {b[:70]!r}")
                for sid, text in added:
                    print(f"   + {sid:>10}  {text!r}")
            continue
        backup(dn, dat, manifest)
        backup(rn, dirb, manifest)
        save_manifest(manifest)
        try:
            ZI.replace(dat_path, new_dat, ROOT)
        except OSError as e:
            raise SystemExit(f"{dn}: can't replace ({e}); the game probably has it open. Nothing changed.")
        try:
            ZI.replace(dir_path, new_dir, ROOT)
        except OSError as e:
            ZI.replace(dat_path, open(os.path.join(BACKUP_DIR, dn), "rb").read(), ROOT)
            raise SystemExit(f"{rn}: can't replace ({e}); {dn} rolled back. Rerun with the game closed.")
        manifest[dn]["patched"], manifest[rn]["patched"] = sha(new_dat), sha(new_dir)
        save_manifest(manifest)
    print(f"done: {total} string(s) renamed across {len(files)} language(s)"
          + (" (dry run, nothing written)" if dry else ""))


def restore():
    manifest = load_manifest()
    if not manifest:
        print("nothing to restore")
        return
    kept = {}
    for name, rec in manifest.items():
        path, src = os.path.join(LOCALE, name), os.path.join(BACKUP_DIR, name)
        ZI.guard(path, ROOT)
        cur = sha(open(path, "rb").read())
        if cur == rec["original"]:
            print(f"{name}: already original")
            continue
        if cur != rec.get("patched"):
            print(f"{name}: not the file this script wrote; not touching it (backup kept)")
            kept[name] = rec
            continue
        orig = open(src, "rb").read()
        if sha(orig) != rec["original"]:
            print(f"{name}: backup does not match the recorded original; not touching it")
            kept[name] = rec
            continue
        ZI.replace(path, orig, ROOT)
        print(f"{name}: restored")
    if kept:
        save_manifest(kept)
    else:
        os.remove(MANIFEST)
        for name in manifest:
            p = os.path.join(BACKUP_DIR, name)
            if os.path.exists(p):
                os.remove(p)


if __name__ == "__main__":
    if "--dry-run" not in sys.argv and ZI.game_running(ROOT):
        raise SystemExit(f"an H1Z1.exe of {ROOT} is running and has the locale open; nothing changed. "
                         "Run this again with the game closed (it applies at the next game start).")
    if "--restore" in sys.argv:
        restore()
    else:
        patch("--dry-run" in sys.argv)
