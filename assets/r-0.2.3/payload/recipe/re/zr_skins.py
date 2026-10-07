r"""ZRevive's OWN skin prints: authored procedurally, no ROTK / retail artwork copied.

Why: the 104 "ZRevive" prints wired up by re\kotk_prints.py are ROTK's own full-sheet textures
(TshirtKotk_UmiChucky_DT.dds etc.) that only exist in ROTK's data_x64_0 and are ROTK's art. This module draws
our own sheets with PIL/numpy from the ZRevive logo (zrevive\portal\public\assets\zrevive-640.png, our art) plus
solid/gradient grounds, stripes, camo, drips and splatter, and encodes them as DXT5 DDS with a full mip chain in
exactly the format the client's retail _DT prints use.

FORMAT (measured on retail MotorcycleHelmet_AngryPug_01_DT.dds / HoodieDOA_01_DT.dds, re\zr_skins.py verify):
    "DDS " u32 124 | flags 0x000A1007 (CAPS|HEIGHT|WIDTH|PIXELFORMAT|MIPMAPCOUNT|LINEARSIZE)
    h, w, pitchOrLinearSize = w*h, depth 0, mipCount = log2(max(w,h)) + 1, 11 reserved u32 = 0
    pixelformat: size 32, flags 0x4 (FOURCC), "DXT5", 0,0,0,0,0 | caps1 0x00401008 (COMPLEX|TEXTURE|MIPMAP)
    caps2..reserved2 = 0; mips run down to 1x1, each level padded to a whole 4x4 block.
    512x512 -> 349680 bytes / 10 mips, 1024x1024 -> 1398256 bytes / 11 mips (byte-identical shape to retail).

UV SHEET LAYOUT per base mesh (measured by decoding a retail full-sheet print for that mesh, see SHEETS):
    TintTshirt            9525  1024  torso y 0..0.58 (front x<0.62, back x>0.62), hem band y 0.585..0.645,
                                      sleeve cuffs y 0.895..0.975, collar tabs y 0..0.10
    Hoodie_Down_Tintable  9739   512  hood x .27..57 y .01..33, back yoke x .58..86 y .01..34,
                                      pocket x .71..89 y .35..48, torso x .11..99 y .50..94 (front x<.55 / back x>.55)
    Pants_Warmups        10072  1024  front leg x 0..0.50, back leg x 0.50..1.0, y 0..0.89; side-seam stripes at
                                      x .005..03 / .495..545 / .975..1.0; hip patch ~(0.89, 0.17); cuffs y 0.89..1.0
    Backpack_Military     9622  1024  main flap x .10..52 y .15..62, top pocket x .60..98 y .02..26,
                                      front pocket x .66..96 y .32..63 (the logo patch), strap band y .70..1.0,
                                      side panel x .77..99 y .72..99
    Helmet_Motorcycle    9645    512  shell x 0..0.82 (right/bottom-right of the sheet is unused), logo ~(0.42,0.55)

STOCK-BASELINE DECISION (still open): the locale names the owner sees belong to ROTK-origin items (9205 "Red Drip
ZRevive T-Shirt", 9068 "Blue ZRevive Joggers", 9117 "ZRevive Team Backpack", and the 11 others in SKINS). Those
item definitions, their names and their CUSTOMIZE icons live in ROTK's data_x64_0, so on a client rebuilt from
stock Z1BR they do not exist at all. Two options, in order of preference:
  A. define them ourselves - 14 ClientItemDefinitions rows (FLAG_ACCOUNT_SCOPE skins + their world items), their
     NAME_ID strings in our locale and their icons. We already have the serving path: the server supplies the
     appearance rows (data\kotk\appearance\colour_overrides.json) and ItemDefinitionReply covers lobby models
     (Features\Lobby\LobbyItemModels.cs). This keeps the owner's list reading exactly as it does now.
  B. fall back to the stock host items (the `stock` column of SKINS, set ZR_SKINS_STOCK=1 in
     re\colour_overrides.py): each is a STOCK KotK cosmetic that already renders a full-sheet print on the same
     base mesh, so our art renders with zero new item data - but it appears under that stock item's own name,
     and it replaces that stock skin's look.
Until the stock baseline lands, the ROTK-origin items are the live target and nothing here needs new item data.

    python -B re\zr_skins.py preview            # PNG previews of every skin -> <scratchpad>\zr_skins\
    python -B re\zr_skins.py dds [outdir]       # write the .dds files (default <scratchpad>\zr_skins\)
    python -B re\zr_skins.py verify             # parse our DDS back and diff the header against a retail _DT
    python -B re\zr_skins.py list               # the skin table (family, host item, texture name)
"""
import io
import os
import struct
import sys
import zlib

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
sys.path.insert(0, HERE)

SCRATCH = os.environ.get("ZR_SCRATCH", r"C:\Users\Gusta\AppData\Local\Temp\claude\C--Users-Gusta"
                                       r"\1438a3c1-ed5d-46e8-aa89-df1129b04507\scratchpad")
OUTDIR = os.path.join(SCRATCH, "zr_skins")
LOGO = os.path.join(REPO, "zrevive", "portal", "public", "assets", "zrevive-640.png")

# ---------------------------------------------------------------------------------------------- brand palette
RED = (178, 26, 24)
DEEP_RED = (120, 14, 14)
BLACK = (22, 22, 24)
CHAR = (46, 46, 50)
GREY = (104, 104, 108)
BONE = (214, 210, 202)
OLIVE = (74, 78, 46)
OLIVE_D = (44, 48, 28)
NAVY = (28, 36, 72)


# ---------------------------------------------------------------------------------------------- sheet layout
class Sheet:
    def __init__(self, label, model, size, logos, bands=(), vstripes=(), panels=(), hard=False, drips=(0.5,),
                 jersey=(), panel_fill="accent"):
        self.label, self.model, self.size, self.hard = label, model, size, hard
        self.panel_fill = panel_fill    # "accent" = panels take the accent colour, "tone" = a lifted ground
                                        # shade with an accent seam (gear, where full accent panels shout)
        self.drips = drips          # normalised y where a paint run starts (just under the chest print)
        self.jersey = jersey        # [(x0, x1)] wide torso stripes for the jersey pattern
        self.logos = logos          # [(cx, cy, w)] normalised, logo fitted into a w-wide box
        self.bands = bands          # [(y0, y1)] horizontal bands (hems, racing stripes)
        self.vstripes = vstripes    # [(x0, x1)] vertical bands (side seams)
        self.panels = panels        # [(x0, y0, x1, y1)] accent panels (pockets, yokes)


SHEETS = {
    "Tshirt": Sheet("TintTshirt", 9525, 1024,
                    logos=[(0.345, 0.280, 0.230), (0.790, 0.270, 0.250)],
                    bands=[(0.585, 0.645), (0.895, 0.975), (0.000, 0.055)],
                    drips=(0.395,),
                    jersey=[(0.06, 0.13), (0.20, 0.27), (0.34, 0.41), (0.48, 0.55),
                            (0.64, 0.71), (0.78, 0.85), (0.92, 0.99)]),
    "Hoodie": Sheet("Hoodie_Down_Tintable", 9739, 512,
                    logos=[(0.300, 0.660, 0.200), (0.780, 0.660, 0.200)],
                    bands=[(0.905, 0.945)],
                    panels=[(0.27, 0.01, 0.57, 0.33), (0.58, 0.01, 0.86, 0.34), (0.71, 0.35, 0.89, 0.48)],
                    drips=(0.735,)),
    "Pants": Sheet("Pants_Warmups", 10072, 1024,
                   logos=[(0.890, 0.170, 0.110)],
                   vstripes=[(0.000, 0.030), (0.492, 0.548), (0.972, 1.000)],
                   bands=[(0.890, 1.000)]),
    "Backpack": Sheet("Backpack_Military", 9622, 1024,
                      logos=[(0.810, 0.470, 0.215), (0.880, 0.855, 0.150)],
                      panels=[(0.10, 0.15, 0.52, 0.62), (0.60, 0.02, 0.98, 0.26), (0.66, 0.32, 0.96, 0.63)],
                      bands=[(0.700, 0.716)], panel_fill="tone"),
    "Helmet": Sheet("Helmet_Motorcycle_Tintable", 9645, 512,
                    logos=[(0.420, 0.490, 0.240)],
                    bands=[(0.235, 0.300), (0.690, 0.740)], hard=True),
}


# ---------------------------------------------------------------------------------------------- the skins
# Skin = (texture stem, family, locale name, world item, stock host item, ground, accent, pattern).
#
# NAMES are the ones already in the owner's CUSTOMIZE list (our rebranded locale, re\patch_locale_rotk.py), so his
# list reads exactly as it did; only the ARTWORK is ours now instead of ROTK's.
#
# TWO ATTACH TARGETS per skin (re\colour_overrides.py uses "world" first, falls back to "stock"):
#   world = the ROTK-origin world item the name belongs to (9205 "Red Drip ZRevive T-Shirt", 9068 "Blue ZRevive
#           Joggers", 9117 "ZRevive Team Backpack", ...). These exist in the CURRENT client's data_x64_0 (ROTK's)
#           and are what the owner actually equipped locally, so this is the primary target.
#   stock = a STOCK KotK item that already renders a full-sheet MainUV print on the SAME base mesh (so the item,
#           its two gender appearance rows, the mesh and the shader group all exist in stock Z1BR data). Used when
#           the client is rebuilt on a stock baseline where the 9xxx items do not exist.
# The first three are the owner's tested trio and come first on purpose.
SKINS = [
    # stem                                family     locale name                     world stock ground    accent   pattern
    # Owner 2026-10-07, seeing it on the body: "remove the blod of this shirt" - the paint-run band read as
    # blood. Keep the charcoal ground, red trim and the Z logo; drop the drip pattern entirely.
    ("ZRevive_Tshirt_RedDrip_DT.dds",     "Tshirt",   "Red Drip ZRevive T-Shirt",      9205, 3526, CHAR,    RED,     "plain"),
    ("ZRevive_Pants_JoggersBlue_DT.dds",  "Pants",    "Blue ZRevive Joggers",          9068, 6018, NAVY,    BONE,    "solid"),
    ("ZRevive_Backpack_Team_DT.dds",      "Backpack", "ZRevive Team Backpack",         9117, 3403, BLACK,   RED,     "solid"),
    ("ZRevive_Tshirt_RedTee_DT.dds",      "Tshirt",   "Red ZRevive T-Shirt",           9064, 2181, RED,     BLACK,   "solid"),
    ("ZRevive_Tshirt_Blackout_DT.dds",    "Tshirt",   "Black ZRevive Logo T-Shirt",    9030, 2182, BLACK,   RED,     "splatter"),
    ("ZRevive_Tshirt_JerseyRed_DT.dds",   "Tshirt",   "Red ZRevive Jersey",            9133, 2053, RED,     BONE,    "jersey"),
    ("ZRevive_Hoodie_ZBlack_DT.dds",      "Hoodie",   "Black ZRevive Logo Hoodie",     9032, 2377, BLACK,   RED,     "solid"),
    ("ZRevive_Hoodie_CrimsonDrip_DT.dds", "Hoodie",   "Red Black ZRevive Hoodie",      9008, 2378, DEEP_RED, BLACK,  "drip"),
    ("ZRevive_Pants_BlackTrack_DT.dds",   "Pants",    "Black ZRevive Joggers",         9066, 3875, BLACK,   RED,     "solid"),
    ("ZRevive_Pants_Camo_DT.dds",         "Pants",    "ZRevive Team Pants",            9111, 4529, OLIVE,   OLIVE_D, "camo"),
    ("ZRevive_Backpack_Camo_DT.dds",      "Backpack", "Punk Military Backpack",        9191, 2122, OLIVE,   OLIVE_D, "camo"),
    ("ZRevive_Backpack_Crimson_DT.dds",   "Backpack", "Frog Military Backpack",        9105, 8152, BLACK,   RED,     "solid"),
    ("ZRevive_Helmet_RacerRed_DT.dds",    "Helmet",   "Red Holiday ZRevive Helmet",    9048, 2063, RED,     BONE,    "solid"),
    ("ZRevive_Helmet_Carbon_DT.dds",      "Helmet",   "Black Skull ZRevive Helmet",    9042, 2078, BLACK,   RED,     "carbon"),
]
FLAGSHIP = 3   # the owner's tested trio: the first three rows above


# ---------------------------------------------------------------------------------------------- painting
def _rng(stem):
    """Deterministic per-skin seed. NOT hash(): Python randomises str hashing per process, which would make
    every build produce different bytes (re\\zr_pack.py's verify compares a rebuild against the installed pack)."""
    return np.random.default_rng(zlib.crc32(stem.encode()))


def _fabric(size, rng, strength=10.0, weave=True):
    """Subtle woven-cloth luminance noise (so a flat ground does not read as plastic). hard-surface families
    (the helmet shell) get a much finer grain and no weave."""
    n = rng.normal(0, 1, (size, size)).astype(np.float32)
    im = Image.fromarray(np.clip(n * 64 + 128, 0, 255).astype(np.uint8), "L").filter(ImageFilter.GaussianBlur(0.8))
    a = np.asarray(im, np.float32) - 128.0
    w = 0.0
    if weave:
        w = (np.sin(np.arange(size, dtype=np.float32) * np.pi / 2)[None, :]
             + np.sin(np.arange(size, dtype=np.float32) * np.pi / 2)[:, None]) * 2.0
    return (a / 128.0 * strength) + w


def _vignette(size):
    """Soft darkening toward the sheet edges: reads as cloth folds under the client's own lighting."""
    g = np.linspace(-1, 1, size, dtype=np.float32)
    r = np.sqrt(g[None, :] ** 2 + g[:, None] ** 2) / 1.4142
    return -14.0 * np.clip(r, 0, 1) ** 2


def _camo(size, rng, cols):
    """Blobby 3-tone camo: thresholded multi-octave smoothed noise."""
    acc = np.zeros((size, size), np.float32)
    for octave, amp in ((8, 1.0), (16, 0.6), (32, 0.35)):
        n = rng.random((octave, octave)).astype(np.float32)
        acc += amp * np.asarray(Image.fromarray((n * 255).astype(np.uint8), "L")
                                .resize((size, size), Image.BICUBIC), np.float32) / 255.0
    acc = np.asarray(Image.fromarray(np.clip(acc / 1.95 * 255, 0, 255).astype(np.uint8), "L")
                     .filter(ImageFilter.GaussianBlur(size / 256.0)), np.float32) / 255.0
    out = np.zeros((size, size, 3), np.float32)
    cuts = np.quantile(acc, [0.38, 0.72])
    masks = [acc < cuts[0], (acc >= cuts[0]) & (acc < cuts[1]), acc >= cuts[1]]
    for m, c in zip(masks, cols):
        out[m] = c
    return out


def _splatter(size, rng, colour, n=320):
    """A paint/blood splatter layer: stretched, randomly oriented droplets with thrown-off specks, drawn at 4x
    and downsampled so the edges are not aliased circles."""
    ss = 3
    lay = Image.new("RGBA", (size * ss, size * ss), (0, 0, 0, 0))
    d = ImageDraw.Draw(lay, "RGBA")
    for _ in range(n):
        cx, cy = rng.random() * size * ss, rng.random() * size * ss
        r = (rng.random() ** 3.2 * 0.030 + 0.0012) * size * ss
        ar = 1.0 + rng.random() ** 2 * 2.2
        ang = rng.random() * 180.0
        blob = Image.new("L", (int(r * 2 * ar) + 4, int(r * 2) + 4), 0)
        ImageDraw.Draw(blob).ellipse([2, 2, blob.width - 3, blob.height - 3], fill=255)
        blob = blob.rotate(ang, expand=True, resample=Image.BILINEAR)
        tint = Image.new("RGBA", blob.size, colour + (255,))
        lay.paste(tint, (int(cx - blob.width / 2), int(cy - blob.height / 2)), blob)
        for _ in range(int(rng.random() * 5)):          # specks thrown off the droplet
            sr = r * (0.08 + rng.random() * 0.22)
            sx = cx + (rng.random() - 0.5) * r * 9
            sy = cy + (rng.random() - 0.5) * r * 9
            d.ellipse([sx - sr, sy - sr, sx + sr, sy + sr], fill=colour + (255,))
    return lay.resize((size, size), Image.LANCZOS)


def _drips(draw, size, rng, colour, y0, n=90):
    """A paint band with runs bleeding down out of it: the ragged source edge plus tapered runs ending in a bead.
    The band's lower edge is drawn per-column so it never reads as a ruled line."""
    band = size * 0.022
    for x in range(0, size, max(1, size // 160)):
        w = max(1, size // 160) + 1
        jag = (rng.random() ** 1.5) * band * 1.6
        draw.rectangle([x, y0 - band, x + w, y0 + jag], fill=colour + (255,))
    for _ in range(n):
        x = rng.random() * size
        w = rng.random() * size * 0.013 + size * 0.0035
        h = rng.random() ** 2.4 * size * 0.30 + size * 0.015
        y = y0 + (rng.random() - 0.5) * size * 0.02
        draw.rectangle([x, y - band, x + w, y + h], fill=colour + (255,))
        draw.ellipse([x - w * 0.35, y + h - w, x + w * 1.35, y + h + w], fill=colour + (255,))


_LOGO_CACHE = {}


def logo_rgba():
    """The ZRevive logo (our own art), trimmed to its visible pixels."""
    if "im" not in _LOGO_CACHE:
        im = Image.open(LOGO).convert("RGBA")
        bb = im.split()[3].point(lambda v: 255 if v > 8 else 0).getbbox()
        _LOGO_CACHE["im"] = im.crop(bb)
    return _LOGO_CACHE["im"]


def _place_logo(img, cx, cy, w, size):
    src = logo_rgba()
    bw = int(round(w * size))
    bh = max(1, int(round(bw * src.height / src.width)))
    lg = src.resize((bw, bh), Image.LANCZOS)
    img.alpha_composite(lg, (int(round(cx * size - bw / 2)), int(round(cy * size - bh / 2))))


def compose(stem, family, ground, accent, pattern):
    """The print sheet for one skin as an RGBA image (alpha 255 everywhere: a full-sheet print covers the whole
    UV layout, the convention the live ZRevive prints use)."""
    sh = SHEETS[family]
    size = sh.size
    rng = _rng(stem)

    if pattern == "camo":
        base = _camo(size, rng, [np.float32(accent), np.float32(ground),
                                 np.float32(tuple(min(255, int(c * 1.45) + 18) for c in ground))])
    else:
        base = np.zeros((size, size, 3), np.float32) + np.float32(ground)
    shade = (_fabric(size, rng, 3.0 if sh.hard else 10.0, not sh.hard) + _vignette(size))[..., None]
    img = Image.fromarray(np.clip(base + shade, 0, 255).astype(np.uint8), "RGB").convert("RGBA")
    d = ImageDraw.Draw(img, "RGBA")

    if pattern == "carbon":   # fine twill weave in the accent, very low alpha
        step = max(4, size // 64)
        for i in range(-size, size * 2, step):
            d.line([(i, 0), (i + size, size)], fill=accent + (26,), width=max(1, step // 3))
            d.line([(i, size), (i + size, 0)], fill=accent + (18,), width=max(1, step // 3))

    # accent panels (pockets / yokes / flaps)
    # camo must stay readable across the whole garment, so it gets no accent panels / bands / seam stripes at
    # all - only the seam outlines that mark the UV islands.
    camo = pattern == "camo"
    tone = tuple(min(255, int(c * 1.35) + 14) for c in ground)
    for (x0, y0, x1, y1) in sh.panels:
        box = [x0 * size, y0 * size, x1 * size, y1 * size]
        if not camo:
            d.rectangle(box, fill=(tone if sh.panel_fill == "tone" else accent) + (210,))
        d.rectangle(box, outline=(accent if sh.panel_fill == "tone" and not camo else BLACK) + (255,),
                    width=max(2, size // 180))
    if not camo:
        # horizontal bands (hems, cuffs, racing stripes)
        for (y0, y1) in sh.bands:
            d.rectangle([0, y0 * size, size, y1 * size], fill=accent + (255,))
        # vertical bands (trouser side seams)
        for (x0, x1) in sh.vstripes:
            d.rectangle([x0 * size, 0, x1 * size, size * (0.89 if family == "Pants" else 1.0)],
                        fill=accent + (255,))

    if pattern == "jersey":   # club-shirt vertical stripes across the torso
        for (x0, x1) in sh.jersey:
            d.rectangle([x0 * size, 0, x1 * size, size * 0.58], fill=accent + (255,))
    if pattern == "splatter":
        img.alpha_composite(_splatter(size, rng, accent))
    elif pattern == "drip":
        for y in sh.drips:
            _drips(d, size, rng, accent, y * size)

    for (cx, cy, w) in sh.logos:
        _place_logo(img, cx, cy, w, size)

    img.putalpha(255)
    return img


# ---------------------------------------------------------------------------------------------- DDS writer
DDS_FLAGS = 0x000A1007
CAPS1 = 0x00401008


def _dds_header(w, h, mips, fourcc=b"DXT5"):
    hdr = bytearray(128)
    hdr[0:4] = b"DDS "
    struct.pack_into("<7I", hdr, 4, 124, DDS_FLAGS, h, w, w * h, 0, mips)
    struct.pack_into("<I", hdr, 76, 32)          # DDS_PIXELFORMAT.dwSize
    struct.pack_into("<I", hdr, 80, 4)           # DDPF_FOURCC
    hdr[84:88] = fourcc
    struct.pack_into("<I", hdr, 108, CAPS1)
    return bytes(hdr)


def encode_dds(img, fourcc=b"DXT5"):
    """RGBA image (square, power of two) -> DXT5 DDS bytes with a full mip chain down to 1x1."""
    import logo_paint as LP     # our BCn encoder (PCA endpoints + least-squares refinement)
    w, h = img.size
    mips = max(w, h).bit_length()
    out = bytearray(_dds_header(w, h, mips, fourcc))
    lvl = img
    for _ in range(mips):
        tile = lvl
        if tile.width % 4 or tile.height % 4:    # 2x2 / 1x1 mips still occupy one whole 4x4 block
            pad = Image.new("RGBA", (4, 4))
            pad.paste(tile.resize((4, 4), Image.NEAREST))
            tile = pad
        out += LP.encode_bc(tile, fourcc.decode())
        lvl = lvl.resize((max(1, lvl.width // 2), max(1, lvl.height // 2)), Image.BOX)
    return bytes(out)


def parse_dds(data):
    f = struct.unpack_from("<7I", data, 4)
    return {"hdrSize": f[0], "flags": f[1], "height": f[2], "width": f[3], "linearSize": f[4],
            "depth": f[5], "mips": f[6], "pfSize": struct.unpack_from("<I", data, 76)[0],
            "pfFlags": struct.unpack_from("<I", data, 80)[0], "fourcc": data[84:88].decode("latin1"),
            "caps1": struct.unpack_from("<I", data, 108)[0], "bytes": len(data)}


# ---------------------------------------------------------------------------------------------- API
def textures():
    """{texture name: dds bytes} for every ZRevive skin. Consumed by re\\zr_pack.py."""
    return {s[0]: encode_dds(compose(s[0], s[1], s[5], s[6], s[7])) for s in SKINS}


def attachments():
    """[{item, stock, model, texture, name, family}] for the appearance generator (re\\colour_overrides.py).
    `item` is the ROTK-origin world item the owner's locale name belongs to; `stock` is the stock-baseline
    fallback item on the same base mesh."""
    return [{"item": w, "stock": st, "model": SHEETS[fam].model, "texture": stem, "name": name, "family": fam}
            for stem, fam, name, w, st, _g, _a, _p in SKINS]


# ---------------------------------------------------------------------------------------------- CLI
def _retail_reference(size):
    import glob
    import pack2
    want = {512: "HoodieDOA_01_DT.dds", 1024: "TshirtKotk_UmiChucky_DT.dds"}[size]
    for p in sorted(glob.glob(os.path.join(os.environ.get("ZR_GAME_ROOT", r"C:\Games\ZRevive") + r"\Resources\Assets", "*.pack2"))):
        pk = pack2.Pack2(p)
        try:
            if pk.has(want):
                return want, pk.read(want)
        finally:
            pk.f.close()
    return want, None


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "list"
    if cmd == "list":
        print(f"{'texture':38} {'family':9} {'world':6} {'stock':6} {'mesh':6} name")
        for i, (stem, fam, name, w, st, _g, _a, pat) in enumerate(SKINS):
            flag = " *" if i < FLAGSHIP else "  "
            print(f"{stem:38} {fam:9} {w:<6} {st:<6} {SHEETS[fam].model:<6} {name}{flag}[{pat}, {SHEETS[fam].size}px]")
        print(f"\n* = the owner's tested trio ({FLAGSHIP} skins)")
        return
    out = (sys.argv[2] if len(sys.argv) > 2 else OUTDIR)
    os.makedirs(out, exist_ok=True)
    if cmd == "preview":
        for stem, fam, name, _w, _st, g, a, p in SKINS:
            im = compose(stem, fam, g, a, p)
            path = os.path.join(out, stem.replace(".dds", ".png"))
            im.convert("RGB").save(path)
            print(f"{name:32} {im.size[0]}x{im.size[1]} -> {path}")
        return
    if cmd == "dds":
        for stem, fam, name, _w, _st, g, a, p in SKINS:
            data = encode_dds(compose(stem, fam, g, a, p))
            path = os.path.join(out, stem)
            with open(path, "wb") as f:
                f.write(data)
            print(f"{name:32} {parse_dds(data)} -> {path}")
        return
    if cmd == "verify":
        bad = 0
        for size in (512, 1024):
            name, ref = _retail_reference(size)
            if not ref:
                print(f"{size}: no retail reference found ({name})")
                continue
            r = parse_dds(ref)
            print(f"retail {name}: {r}")
            for stem, fam, _n, _w, _st, g, a, p in SKINS:
                if SHEETS[fam].size != size:
                    continue
                m = parse_dds(encode_dds(compose(stem, fam, g, a, p)))
                diff = {k: (m[k], r[k]) for k in m if m[k] != r[k] and k != "depth"}
                ok = not diff
                bad += not ok
                print(f"  [{'PASS' if ok else 'FAIL'}] {stem:40} {m['width']}x{m['height']} {m['fourcc']} "
                      f"mips={m['mips']} {m['bytes']}B" + (f"  DIFF {diff}" if diff else ""))
        raise SystemExit(1 if bad else 0)
    raise SystemExit(f"unknown command {cmd!r}")


if __name__ == "__main__":
    main()
