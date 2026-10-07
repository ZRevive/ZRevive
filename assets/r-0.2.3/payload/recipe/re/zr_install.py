"""Where the client we patch lives, and how to find things in it without assuming a build.

Every re\\patch_*.py used to hardcode C:\\Games\\ZRevive and, worse, hardcode WHICH pack2 a target
entry lives in (data_x64_0 for the vehicle tables, ui_x64_0 for UIRoot.gfx, ...). Both assumptions
are build-specific: ZRevive's old base was ROTK's client, where pack membership and pack NUMBERING
differ from a stock Z1 Battle Royale install (stock's assets_x64_0/1 appear in ROTK's tree
renumbered as 15/16). Measured 2026-10-07.

So this module gives the patch scripts two things:

  root resolution   --root PATH, else %ZR_GAME_ROOT%, else the default install. One flag, every script.
  find_pack(name)   which pack2 in THIS install holds an entry, looked up by CRC-64/Jones name hash
                    across every *.pack2 in the folder. No pack index is ever assumed.

Safety is centralised here too, because it has to hold for every script:
  * FORBIDDEN refuses C:\\Games\\ROTK (the owner plays it) and anything under steamapps (his real
    Steam install) as a WRITE target - both are read-only sources only, via open_source().
  * a write target must have st_nlink == 1 and realpath == path, so we never write through a
    hardlink or junction into another install (C:\\Games\\ZRevive was hardlinked to ROTK until
    2026-10-06; see re\\unlink_game_copy.py).
  * replace() writes a temp file next to the target and os.replace()s it in. Never in place.
  * game_running() refuses while an H1Z1.exe of the target install is up.
"""
import glob
import hashlib
import os
import shutil
import subprocess
import sys
import time

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pack2 import Pack2, name_hash  # noqa: E402

DEFAULT_ROOT = r"C:\Games\ZRevive"
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BACKUP_DIR = os.path.join(REPO, "backup")

# Write targets under any of these are refused outright. ROTK is the owner's own game; steamapps is
# his real Steam install and the read-only stock baseline.
FORBIDDEN = ("\\games\\rotk", "\\steamapps\\", ".rotk-assets")


# --------------------------------------------------------------------------- root resolution

def resolve_root(argv=None):
    """--root PATH | %ZR_GAME_ROOT% | DEFAULT_ROOT -> an absolute game root. Must contain H1Z1.exe."""
    argv = list(sys.argv if argv is None else argv)
    root = None
    for i, a in enumerate(argv):
        if a == "--root" and i + 1 < len(argv):
            root = argv[i + 1]
            break
        if a.startswith("--root="):
            root = a.split("=", 1)[1]
            break
    root = root or os.environ.get("ZR_GAME_ROOT") or DEFAULT_ROOT
    root = os.path.abspath(root)
    if not os.path.isfile(os.path.join(root, "H1Z1.exe")):
        raise SystemExit(f"{root}: no H1Z1.exe here, that is not a game root")
    return root


def assets_dir(root):
    return os.path.join(root, "Resources", "Assets")


def locale_dir(root):
    return os.path.join(root, "Locale")


# --------------------------------------------------------------------------- safety

def guard(path, root=None):
    """Refuse to WRITE path: forbidden location, or shared with another install."""
    low = os.path.normcase(os.path.abspath(path))
    if any(f in low for f in FORBIDDEN):
        raise SystemExit(f"{path}: read-only location (ROTK / the owner's Steam install), refusing to write")
    if root and not low.startswith(os.path.normcase(os.path.abspath(root)) + os.sep):
        raise SystemExit(f"{path} is outside the game root {root}, refusing")
    if os.path.exists(path):
        st = os.stat(path)
        if st.st_nlink != 1:
            raise SystemExit(f"{path} has {st.st_nlink} hard links (shared with another install); not touching it")
        if os.path.normcase(os.path.realpath(path)) != low:
            raise SystemExit(f"{path} is a link to {os.path.realpath(path)}; not touching it")
    d = os.path.dirname(path)
    if os.path.normcase(os.path.realpath(d)) != os.path.normcase(os.path.abspath(d)):
        raise SystemExit(f"{d} resolves to {os.path.realpath(d)} (junction/symlink); not touching it")


def assert_readonly_source(path):
    """A source we only ever READ. Having this explicit keeps the stock install out of write paths."""
    if not os.path.exists(path):
        raise SystemExit(f"{path}: source does not exist")
    return path


def game_running(root=None):
    """True if an H1Z1.exe of `root` runs - or any H1Z1.exe whose image path we cannot read."""
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq H1Z1.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True).stdout
    pids = [int(l.strip('"').split('","')[1]) for l in out.splitlines() if l.lower().startswith('"h1z1.exe"')]
    if not pids:
        return False
    if root is None:
        return True
    want = os.path.normcase(os.path.join(os.path.abspath(root), "H1Z1.exe"))
    try:
        import live
        for pid in pids:
            img = live.image_path(pid)
            if not img or os.path.normcase(img) == want:
                return True
        return False
    except Exception:
        return True          # cannot tell -> assume it is ours


def sha256(path, limit=None):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        left = os.path.getsize(path) if limit is None else limit
        while left:
            b = f.read(min(left, 1 << 22))
            if not b:
                break
            h.update(b)
            left -= len(b)
    return h.hexdigest()


def replace(path, data, root=None):
    """Write data to path via a temp file next to it + os.replace. Never writes into path itself."""
    guard(path, root)
    tmp = path + ".zrevive-tmp"
    if os.path.exists(tmp):
        os.remove(tmp)
    with open(tmp, "xb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    try:
        os.replace(tmp, path)
    except OSError:
        os.remove(tmp)
        raise


def backup(path, tag, root=None):
    """Copy path to <repo>\\backup\\<name>.<tag>-<timestamp>, sha256-checked. -> the backup path."""
    os.makedirs(BACKUP_DIR, exist_ok=True)
    dst = os.path.join(BACKUP_DIR, f"{os.path.basename(path)}.{tag}-{time.strftime('%Y%m%d-%H%M%S')}")
    shutil.copyfile(path, dst)
    if sha256(dst) != sha256(path):
        raise SystemExit(f"backup {dst} differs from {path}; stopping before anything is patched")
    return dst


# --------------------------------------------------------------------------- finding things by name

_INDEX = {}


def pack_index(root, refresh=False):
    """{name_hash: [pack2 path, ...]} for every *.pack2 in the install. Cached per root."""
    key = os.path.normcase(assets_dir(root))
    if refresh or key not in _INDEX:
        idx = {}
        for p in sorted(glob.glob(os.path.join(assets_dir(root), "*.pack2"))):
            pk = Pack2(p)
            try:
                for h in pk.entries:
                    idx.setdefault(h, []).append(p)
            finally:
                pk.f.close()
        _INDEX[key] = idx
    return _INDEX[key]


def find_packs(root, name):
    """Every pack2 in this install holding `name`, by hash. [] when absent. Never assumes an index."""
    return list(pack_index(root).get(name_hash(name), ()))


def find_pack(root, name, required=True):
    """The single pack2 holding `name`. Refuses ambiguity rather than guessing."""
    hits = find_packs(root, name)
    if not hits:
        if required:
            raise SystemExit(f"{name} is not in any pack2 of {root} - this patch does not apply to this build")
        return None
    if len(hits) > 1:
        raise SystemExit(f"{name} is in {len(hits)} packs ({', '.join(os.path.basename(h) for h in hits)}); "
                         "pick one explicitly rather than guessing")
    return hits[0]


def read_entry(root, name):
    """Decompressed bytes of `name` from wherever it lives in this install."""
    p = find_pack(root, name)
    pk = Pack2(p)
    try:
        return pk.read(name)
    finally:
        pk.f.close()


def have(root, *names):
    """{name: [pack basenames]} - what of `names` this install actually has. For status reporting."""
    return {n: [os.path.basename(p) for p in find_packs(root, n)] for n in names}
