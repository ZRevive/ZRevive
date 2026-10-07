"""Forgelight locale reader: en_us_data.dat lines are "hash<TAB>type<TAB>text"."""
import os
import re

LOC = os.environ.get("ZR_GAME_ROOT", r"C:\Games\ZRevive") + r"\Locale\en_us_data.dat"


def load(path=LOC):
    out = {}
    with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
        for line in f:
            line = line.rstrip("\r\n")
            parts = line.split("\t", 2)
            if len(parts) == 3 and parts[0].isdigit():
                out[int(parts[0])] = (parts[1], parts[2])
    return out


def _mix(a, b, c):
    M = 0xFFFFFFFF
    a = (a - b - c) & M; a ^= c >> 13
    b = (b - c - a) & M; b ^= (a << 8) & M
    c = (c - a - b) & M; c ^= b >> 13
    a = (a - b - c) & M; a ^= c >> 12
    b = (b - c - a) & M; b ^= (a << 16) & M
    c = (c - a - b) & M; c ^= b >> 5
    a = (a - b - c) & M; a ^= c >> 3
    b = (b - c - a) & M; b ^= (a << 10) & M
    c = (c - a - b) & M; c ^= b >> 15
    return a, b, c


def lookup2(k: bytes, initval=0):
    """Bob Jenkins lookup2 'hash()' (signed-char variant used by Forgelight)."""
    M = 0xFFFFFFFF
    length = len(k)
    a = b = 0x9E3779B9
    c = initval
    i = 0
    n = length
    sk = [x - 256 if x > 127 else x for x in k]
    while n >= 12:
        a = (a + sk[i] + (sk[i + 1] << 8) + (sk[i + 2] << 16) + (sk[i + 3] << 24)) & M
        b = (b + sk[i + 4] + (sk[i + 5] << 8) + (sk[i + 6] << 16) + (sk[i + 7] << 24)) & M
        c = (c + sk[i + 8] + (sk[i + 9] << 8) + (sk[i + 10] << 16) + (sk[i + 11] << 24)) & M
        a, b, c = _mix(a, b, c)
        i += 12
        n -= 12
    c = (c + length) & M
    t = sk[i:] + [0] * 12
    if n >= 11: c = (c + (t[10] << 24)) & M
    if n >= 10: c = (c + (t[9] << 16)) & M
    if n >= 9: c = (c + (t[8] << 8)) & M
    if n >= 8: b = (b + (t[7] << 24)) & M
    if n >= 7: b = (b + (t[6] << 16)) & M
    if n >= 6: b = (b + (t[5] << 8)) & M
    if n >= 5: b = (b + t[4]) & M
    if n >= 4: a = (a + (t[3] << 24)) & M
    if n >= 3: a = (a + (t[2] << 16)) & M
    if n >= 2: a = (a + (t[1] << 8)) & M
    if n >= 1: a = (a + t[0]) & M
    a, b, c = _mix(a, b, c)
    return c


def string_key(string_id: int):
    return lookup2(f"Global.Text.{string_id}".encode())


class Locale:
    def __init__(self, path=LOC):
        self.raw = load(path)

    def get(self, string_id):
        if not string_id:
            return None
        v = self.raw.get(string_id_key(string_id))
        if v is None:
            return None
        return clean(v[1])


def string_id_key(sid):
    return string_key(sid)


def clean(s):
    return re.sub(r"<br\s*/?>", "\n", s)
