"""Forgelight pack2 reader (read-only).

Header : "PAK\x01" u32 count, u64 length, u64 map_offset
Map    : count * (u64 name_hash, u64 offset, u64 data_len, u32 zip_flag, u32 crc32)
Zipped : "\xA1\xB2\xC3\xD4" u32be uncompressed_size, zlib stream

Name hash = CRC-64/Jones (poly 0xAD93D23594C935A9 reflected, init/xorout ~0) of the UPPER-cased name.
"""
import struct
import zlib

_POLY = 0x95AC9329AC4BC9B5  # CRC-64/Jones (0xAD93D23594C935A9) reflected
_T = []
for i in range(256):
    c = i
    for _ in range(8):
        c = (c >> 1) ^ _POLY if c & 1 else c >> 1
    _T.append(c)


def crc64(data: bytes, init=0xFFFFFFFFFFFFFFFF, xorout=0xFFFFFFFFFFFFFFFF):
    c = init
    for b in data:
        c = _T[(c ^ b) & 0xFF] ^ (c >> 8)
    return c ^ xorout


def name_hash(name: str):
    return crc64(name.upper().encode("latin-1"))


class Pack2:
    def __init__(self, path):
        self.path = path
        self.f = open(path, "rb")
        hdr = self.f.read(24)
        magic, self.count, self.length, self.map_off = struct.unpack("<4sIQQ", hdr)
        if magic != b"PAK\x01":
            raise ValueError(f"{path}: bad magic {magic!r}")
        self.f.seek(self.map_off)
        raw = self.f.read(32 * self.count)
        self.entries = {}
        for i in range(self.count):
            h, off, ln, zf, crc = struct.unpack_from("<QQQII", raw, i * 32)
            self.entries[h] = (off, ln, zf, crc)

    def read_hash(self, h):
        off, ln, zf, crc = self.entries[h]
        self.f.seek(off)
        d = self.f.read(ln)
        if zf in (1, 0x11) or d[:4] == b"\xA1\xB2\xC3\xD4":
            if d[:4] == b"\xA1\xB2\xC3\xD4":
                size = struct.unpack(">I", d[4:8])[0]
                d = zlib.decompress(d[8:]) if size else b""
        return d

    def read(self, name):
        return self.read_hash(name_hash(name))

    def has(self, name):
        return name_hash(name) in self.entries

