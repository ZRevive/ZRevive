"""Read a running H1Z1.exe (our own client copy) to inspect live game objects.

Finds the game-client object (its run state lives at +0x37688; vtable in the image),
then the local player = [[client+0x382A0]+0x780], and prints the player's vtable and
the vfunc at +0xB8 used by CheckZoneLoaded.
"""
import ctypes
import ctypes.wintypes as wt
import os
import struct
import subprocess
import sys

import numpy as np

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
PROCESS_VM_READ = 0x10
PROCESS_QUERY_INFORMATION = 0x400
BASE, IMG_END = 0x140000000, 0x140000000 + 0x72B4000


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p), ("AllocationProtect", wt.DWORD),
                ("PartitionId", wt.WORD), ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD)]


OUR_CLIENT = r"C:\Games\ZRevive\H1Z1.exe"  # never ROTK's client (C:\Games\ROTK), even if it runs too


def image_path(pid):
    h = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION only
    if not h:
        return None
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = wt.DWORD(len(buf))
        return buf.value if k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)) else None
    finally:
        k32.CloseHandle(h)


def pid_of(name="H1Z1.exe", path=OUR_CLIENT):
    out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {name}", "/FO", "CSV", "/NH"], capture_output=True, text=True).stdout
    for line in out.splitlines():
        parts = line.strip('"').split('","')
        if parts and parts[0].lower() == name.lower():
            pid = int(parts[1])
            img = image_path(pid)
            if img and os.path.normcase(img) == os.path.normcase(path):
                return pid
    return None


class Proc:
    def __init__(self, pid):
        self.h = k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, pid)
        if not self.h:
            raise OSError(ctypes.get_last_error())

    def read(self, addr, n):
        buf = ctypes.create_string_buffer(n)
        got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)):
            return None
        return buf.raw[: got.value]

    def q(self, addr):
        d = self.read(addr, 8)
        return struct.unpack("<Q", d)[0] if d else None

    def regions(self):
        addr = 0
        mbi = MBI()
        while k32.VirtualQueryEx(self.h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            base = mbi.BaseAddress or 0
            if mbi.State == 0x1000 and mbi.Type == 0x20000 and mbi.Protect in (0x04, 0x40):  # committed, private, RW/RWX
                yield base, mbi.RegionSize
            addr = base + mbi.RegionSize
            if addr >= 0x7FFFFFFFFFFF:
                break


def find_client(p, state=22):
    for base, size in p.regions():
        if size < 0x40000:
            continue
        data = p.read(base, size)
        if not data:
            continue
        a = np.frombuffer(data[: len(data) // 4 * 4], dtype="<u4")
        for idx in np.nonzero(a == state)[0]:
            off = int(idx) * 4 - 0x37688
            if off < 0 or off % 8:
                continue
            vt = struct.unpack_from("<Q", data, off)[0]
            if BASE <= vt < IMG_END:
                yield base + off, vt


if __name__ == "__main__":
    pid = pid_of()
    if not pid:
        sys.exit("H1Z1.exe not running")
    p = Proc(pid)
    state = int(sys.argv[1]) if len(sys.argv) > 1 else 22
    for client, vt in find_client(p, state):
        world = p.q(client + 0x382A0)
        player = p.q(world + 0x780) if world else None
        print(f"client=0x{client:X} vtable=0x{vt:X} world/actorMgr=0x{world or 0:X} player=0x{player or 0:X}")
        if player:
            pvt = p.q(player)
            fn = p.q(pvt + 0xB8) if pvt else None
            flag = p.read(client + 0x376DC, 1)
            print(f"  player vtable=0x{pvt:X} vfunc+0xB8=0x{fn or 0:X} weatherSynced={flag.hex() if flag else '?'}")
