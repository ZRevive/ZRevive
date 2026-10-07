"""Replace ROTK branding in the locale strings of OUR client copy (C:\\Games\\ZRevive\\Locale).

ROTK appended 111 strings (ids 800001-800111) to every <lang>_data.dat. 69 of them, the same English text in
all 11 languages, carry "ROTK" (68 are NAME_IDs of ROTK-made skins, items 9001-9219; "ROTK Bindings" is not
referenced by any item). These are ROTK creations, so there is no original KotK name: "ROTK" -> "ZRevive".
OVERRIDES can give single strings another text (key = locale hash, see fl_locale.string_key).

Locale format (checked against ROTK's files): <lang>_data.dat = BOM + records "hash<TAB>type<TAB>text"
separated by CRLF (text may itself contain CRLF). <lang>_data.dir = "##" header (Count, MD5Checksum = md5 of
the whole .dat, TextLength = longest text in bytes, ...) + one row "hash<TAB>offset<TAB>length<TAB>d" per
record. Both files are rebuilt together; every record other than the renamed ones stays byte-identical.

    python re\\patch_locale_rotk.py              patch (idempotent)
    python re\\patch_locale_rotk.py --dry-run    show what would change, write nothing
    python re\\patch_locale_rotk.py --restore    put the backed-up originals back

Originals go to backup\\locale_rotk\\ (manifest.json holds original + patched sha256). Files are written as a
temp file + os.replace, never in place, and only when not shared with another install (guard(), as in
patch_ui_text.py). If the game holds a file open the patch is rolled back and nothing changes; rerun it
with the game closed. The game reads the locale at start, so changes show at the next game start.

Never point this at C:\\Games\\ROTK or the Steam H1Z1 folder.
"""
import glob
import hashlib
import json
import os
import re
import shutil
import sys

LOCALE = r"C:\Games\ZRevive\Locale"
BACKUP_DIR = r"C:\Users\Gusta\ZRevive\backup\locale_rotk"
MANIFEST = os.path.join(BACKUP_DIR, "manifest.json")
BRAND = re.compile(rb"\bROTK\b", re.I)
NEW = b"ZRevive"
# Strings that need more than the brand swap: ROTK's collaboration skins carried club, sponsor, team and creator
# names; their logos are replaced by patch_logos.py, so the names become neutral ZRevive names (owner 2026-10-06).
# Keyed by string id (800001-800111 are ROTK's own strings, the same English text in all 11 languages).
# In-game brands (Royoline, Pleasant Valley) and plain descriptive names stay as they are.
NEUTRAL_NAMES = {
    800011: "Green Splash ZRevive Hoodie",       # Zevent
    800012: "Green Splash ZRevive Pants",        # Zevent
    800013: "Green Splash ZRevive T-Shirt",      # Zevent
    800014: "Gold Splash ZRevive T-Shirt",       # Ayezee
    800015: "Black ZRevive Logo T-Shirt",        # Teufnow
    800016: "Black ZRevive Logo Hoodie",         # TEUF
    800017: "Black ZRevive Logo Pants",          # TEUF
    800018: "Green Black ZRevive Backpack",      # Kick
    800019: "Orange Black ZRevive Backpack",     # Umi
    800038: "White Blue ZRevive Jersey",         # OM
    800039: "Black Red ZRevive Parachute",       # TEUF
    800040: "Black ZRevive Print T-Shirt",       # AKER
    800041: "White Tricolor ZRevive Jersey",     # Lyon
    800042: "Navy Red ZRevive Jersey",           # PSG
    800043: "Black Pink ZRevive Hoodie",         # AKER
    800044: "Black Pink ZRevive Pants",          # AKER
    800045: "Green Camo ZRevive Scrubs Shirt",   # KICK
    800046: "Green Camo ZRevive Scrubs Pants",   # KICK
    800048: "Pink Wildcard ZRevive Hoodie",      # TEUF
    800049: "Pink Signature ZRevive Hoodie",     # Guezmerr
    800051: "White ZRevive Hoodie",              # Chowh1
    800052: "White ZRevive Pants",               # Chowh1
    800053: "Frog Military Backpack",            # Uncat
    800054: "ZRevive Team Hoodie #32",           # Brawks / M8
    800055: "ZRevive Team Hoodie #7",            # Gotaga / M8
    800056: "ZRevive Team Pants",                # M8
    800057: "ZRevive Tour T-Shirt",              # M8
    800058: "ZRevive Pro League Helmet",         # M8
    800059: "ZRevive Team Backpack",             # M8
    800060: "Season 0 Winner T-Shirt",           # Havachi
    800061: "Lightning T-Shirt",                 # LAME
    800062: "Lightning Leather Pants",           # LAME
    800063: "Bike Shop T-Shirt",                 # Maszuka
    800067: "Red ZRevive Jersey",                # Bayern
    800068: "Yellow Black ZRevive Jersey",       # Dortmund
    800069: "ZRevive Team Hoodie #04",           # M8
    800070: "ZRevive Team Hoodie #10",           # Joedeceives / M8
    800071: "Black Gold ZRevive Hoodie",         # Vitality
    800072: "Black Gold ZRevive Pro Hoodie",     # Skite / Vitality
    800073: "Black Gold ZRevive Pants",          # Vitality
    800077: "Blue Red Striped ZRevive Jersey",   # Barca
    800078: "Blue Red Striped Jersey #10",       # Barca / Itachi
    800079: "Champion Edition AR-15",            # AyeZee
    800085: "Green Black ZRevive Offroader",     # Kick
    800086: "White ZRevive Parachute",           # Kick x ROTK
    800087: "Turret Satchel",                    # SPKB
    800099: "Green Visor ZRevive Helmet",        # Kick x ROTK
    800100: "Black Pink ZRevive Backpack",       # AKER
    800101: "Street Leggings",                   # J1nx
    800103: "Signature ZRevive Hoodie",          # Eryc
    800104: "Red Drip ZRevive T-Shirt",          # Umi
    800105: "Black ZRevive Eye Patch",           # Kick
}


def _overrides():
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import fl_locale
    return {fl_locale.string_key(sid): t.encode("utf-8") for sid, t in NEUTRAL_NAMES.items()}


OVERRIDES = _overrides()  # {locale hash: b"new text"}
BOM = b"\xef\xbb\xbf"

assert "ROTK" not in LOCALE.upper().replace("ZREVIVE", "")


def guard(path):
    """Refuse files shared with another install (hardlink/junction): see patch_ui_text.guard."""
    if os.stat(path).st_nlink != 1 or os.path.normcase(os.path.realpath(path)) != os.path.normcase(os.path.abspath(path)):
        raise SystemExit(f"{path} is shared with another install (hardlink/junction); not touching it.")


def sha(b):
    return hashlib.sha256(b).hexdigest()


def parse(dat, dirb):
    """-> (header lines, [(hash, offset, length, flag)] in .dir order); checks the pair is consistent."""
    lines = dirb.split(b"\r\n")
    header = [ln for ln in lines if ln.startswith(b"#")]
    rows = []
    for ln in lines:
        if ln and not ln.startswith(b"#"):
            h, off, n, flag = ln.split(b"\t")
            rows.append((h, int(off), int(n), flag))
    spans = sorted((off, n) for _, off, n, _ in rows)
    start = len(BOM) if dat.startswith(BOM) else 0
    ok = spans and spans[0][0] == start and spans[-1][0] + spans[-1][1] + 2 == len(dat) and all(
        spans[i + 1][0] == spans[i][0] + spans[i][1] + 2 for i in range(len(spans) - 1))
    if not ok:
        raise SystemExit("records are not contiguous CRLF-separated lines; unknown layout, not touching it")
    for h, off, n, _ in rows:
        if not dat.startswith(h + b"\t", off) or dat[off + n:off + n + 2] != b"\r\n":
            raise SystemExit(f"dir row {h!r} does not point at its record; not touching it")
    return header, rows


def rebuild(dat, dirb):
    """-> (new dat, new dir, [(hash, old text, new text)])."""
    header, rows = parse(dat, dirb)
    by_off = sorted(rows, key=lambda r: r[1])
    out = bytearray(BOM if dat.startswith(BOM) else b"")
    where, changes, longest = {}, [], 0
    for h, off, n, _ in by_off:
        rec = dat[off:off + n]
        hh, typ, text = rec.split(b"\t", 2)
        new = OVERRIDES.get(int(hh), BRAND.sub(NEW, text))
        if new != text:
            changes.append((int(hh), text.decode("utf-8"), new.decode("utf-8")))
            rec = hh + b"\t" + typ + b"\t" + new
        longest = max(longest, len(new))
        where[h] = (len(out), len(rec))
        out += rec + b"\r\n"
    new_dat = bytes(out)
    hdr = []
    for ln in header:
        if ln.startswith(b"## MD5Checksum:"):
            ln = b"## MD5Checksum: " + hashlib.md5(new_dat).hexdigest().upper().encode()
        elif ln.startswith(b"## TextLength:"):
            ln = b"## TextLength:\t" + str(longest).encode()
        hdr.append(ln)
    body = [b"%s\t%d\t%d\t%s" % (h, *where[h], flag) for h, _, _, flag in rows]
    new_dir = b"\r\n".join(hdr + body) + b"\r\n"
    return new_dat, new_dir, changes


def verify(old_dat, old_dir, new_dat, new_dir, changes):
    """Re-parse the result: same records in the same order, only the changed texts differ."""
    _, old_rows = parse(old_dat, old_dir)
    _, new_rows = parse(new_dat, new_dir)
    changed = {str(h).encode(): new.encode() for h, _, new in changes}
    assert [r[0] for r in old_rows] == [r[0] for r in new_rows], "record order changed"
    for (h, o, n, _), (_, o2, n2, _) in zip(old_rows, new_rows):
        a, b = old_dat[o:o + n], new_dat[o2:o2 + n2]
        if h in changed:
            assert b.split(b"\t", 2)[2] == changed[h] and a.split(b"\t", 2)[:2] == b.split(b"\t", 2)[:2], h
        else:
            assert a == b, f"record {h!r} changed unexpectedly"
    assert b"## MD5Checksum: " + hashlib.md5(new_dat).hexdigest().upper().encode() in new_dir


def load_manifest():
    return json.load(open(MANIFEST, encoding="utf-8")) if os.path.exists(MANIFEST) else {}


def save_manifest(m):
    tmp = MANIFEST + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(m, f, indent=1)
    os.replace(tmp, MANIFEST)


def replace_file(path, data):
    """temp file next to the target + os.replace (never writes into the existing file)."""
    guard(path)
    tmp = path + ".zrevive-tmp"
    with open(tmp, "xb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    try:
        os.replace(tmp, path)
    except OSError:
        os.remove(tmp)
        raise


def backup(name, data, manifest):
    dst = os.path.join(BACKUP_DIR, name)
    rec = manifest.get(name)
    if rec:  # keep the first original we saw; never overwrite it with something else
        if not os.path.exists(dst) or sha(open(dst, "rb").read()) != rec["original"]:
            raise SystemExit(f"backup {dst} is missing or not the recorded original; not touching {name}")
        if sha(data) != rec["original"]:
            raise SystemExit(f"{name} is neither the original nor patched by this script (backup kept); not touching it")
        return
    with open(dst + ".tmp", "wb") as f:
        f.write(data)
    os.replace(dst + ".tmp", dst)
    manifest[name] = {"original": sha(data), "size": len(data)}


def patch(dry):
    manifest = load_manifest()
    total = 0
    for dat_path in sorted(glob.glob(os.path.join(LOCALE, "*_data.dat"))):
        dir_path = dat_path[:-4] + ".dir"
        dn, rn = os.path.basename(dat_path), os.path.basename(dir_path)
        guard(dat_path)
        guard(dir_path)
        dat, dirb = open(dat_path, "rb").read(), open(dir_path, "rb").read()
        # already patched by an earlier version of this script? re-patch from the backed-up original, so a changed
        # OVERRIDES list is applied cleanly (and the restore still goes back to the true original)
        for name, cur in ((dn, "dat"), (rn, "dir")):
            rec = manifest.get(name)
            data = dat if cur == "dat" else dirb
            if rec and rec.get("patched") == sha(data):
                orig = open(os.path.join(BACKUP_DIR, name), "rb").read()
                if sha(orig) != rec["original"]:
                    raise SystemExit(f"backup of {name} does not match the recorded original; not touching it")
                if cur == "dat":
                    dat = orig
                else:
                    dirb = orig
        new_dat, new_dir, changes = rebuild(dat, dirb)
        cur_dat, cur_dir = open(dat_path, "rb").read(), open(dir_path, "rb").read()
        if new_dat == cur_dat and new_dir == cur_dir:
            print(f"{dn}: already patched exactly like this")
            continue
        if not changes:
            print(f"{dn}: nothing to rename (already patched)")
            continue
        verify(dat, dirb, new_dat, new_dir, changes)
        total += len(changes)
        print(f"{dn}: {len(changes)} string(s)" + (" (dry run)" if dry else ""))
        if dry:
            if dn.startswith("en_us"):
                for h, a, b in changes:
                    print(f"   {h:>10}  {a}  ->  {b}")
            continue
        backup(dn, dat, manifest)
        backup(rn, dirb, manifest)
        save_manifest(manifest)
        try:
            replace_file(dat_path, new_dat)
        except OSError as e:
            raise SystemExit(f"{dn}: can't replace ({e}); the game probably has it open. Nothing changed; rerun with the game closed.")
        try:
            replace_file(dir_path, new_dir)
        except OSError as e:
            replace_file(dat_path, open(os.path.join(BACKUP_DIR, dn), "rb").read())  # keep the pair consistent
            raise SystemExit(f"{rn}: can't replace ({e}); {dn} rolled back. Rerun with the game closed.")
        manifest[dn]["patched"], manifest[rn]["patched"] = sha(new_dat), sha(new_dir)
        save_manifest(manifest)
    print(f"done: {total} string(s) renamed" + (" (dry run, nothing written)" if dry else ""))


def restore():
    manifest = load_manifest()
    if not manifest:
        print("nothing to restore")
        return
    kept = {}
    for name, rec in manifest.items():
        path, src = os.path.join(LOCALE, name), os.path.join(BACKUP_DIR, name)
        guard(path)
        cur = sha(open(path, "rb").read())
        if cur == rec["original"]:
            print(f"{name}: already original")
            continue
        if cur != rec.get("patched"):
            print(f"{name}: not the file this script wrote (changed since?); not touching it (backup kept)")
            kept[name] = rec
            continue
        orig = open(src, "rb").read()
        if sha(orig) != rec["original"]:
            print(f"{name}: backup does not match the recorded original; not touching it")
            kept[name] = rec
            continue
        replace_file(path, orig)
        print(f"{name}: restored")
    if kept:
        save_manifest(kept)
    else:
        os.remove(MANIFEST)
        for name in manifest:
            os.remove(os.path.join(BACKUP_DIR, name))


if __name__ == "__main__":
    os.makedirs(BACKUP_DIR, exist_ok=True)
    if "--dry-run" not in sys.argv:
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        from live import pid_of  # only queries the process list; never touches the game
        if pid_of():
            raise SystemExit("our H1Z1.exe is running and has the locale open; nothing changed. "
                             "Run this again after closing the game (it applies at the next game start).")
    if "--restore" in sys.argv:
        restore()
    else:
        patch("--dry-run" in sys.argv)
