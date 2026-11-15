"""kv3_tier0_oracle.py -- CS2's own tier0 as the judge of our KV3 reader.

    python tools/re/kv3_tier0_oracle.py <dump dir> [--tier0 <tier0.dll>] [--show N]

<dump dir> is what Kv3TierZeroDump (tests) writes: for each pair n, Valve's block (n.valve.bin) and our
re-encoding of what our reader read from it (n.ours.bin, KV3 v5 by our writer), with index.tsv naming the
type, version, file and block. tier0 loads each through its exported LoadKV3 (CUtlBuffer overload; binary
v2-v5 and the legacy VKV3 container) and saves it with SaveKV3Text_ToString. The texts are compared: equal
means our reader kept every value, type and flag tier0 reads. No game process: tier0.dll is loaded into this
one, as tools/re/exp2f_oracle.py does.

KV3ID_t is { const char *name; uint64 id0; uint64 id1 } (ConvertKV3Format compares +8 and +0x10, tier0
0923 1800e42b0); the format passed is the block's own GUID (header bytes 4..20). CUtlBuffer comes from its
exported constructor (pointer, size, flags 8 = READ_ONLY); KeyValues3 has no exported constructor, so a
null object (word 0: external storage bit 0, type 1 in bits 1-8) is handed to LoadKV3,
which fills it.
"""
import argparse
import collections
import ctypes
import os
import sys

DEFAULT_TIER0 = os.path.join(os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive"),
                             "game", "bin", "win64", "tier0.dll")

LOAD = "?LoadKV3@@YA_NPEAVKeyValues3@@PEAVCUtlString@@PEAVCUtlBuffer@@AEBUKV3ID_t@@PEBDI@Z"
SAVE = "?SaveKV3Text_ToString@@YA_NAEBUKV3ID_t@@PEBVKeyValues3@@PEAVCUtlString@@2I@Z"
BUFFER = "??0CUtlBuffer@@QEAA@PEBXHW4BufferFlags_t@0@@Z"
GROWABLE = "??0CUtlBuffer@@QEAA@HHW4BufferFlags_t@0@@Z"
SAVE_BINARY = "?SaveKV3@@YA_NAEBUKV3ID_t@@0PEBVKeyValues3@@PEAVCUtlString@@PEAVCUtlBuffer@@I@Z"
# The "binary" encoding (Kv3Formats), uncompressed: equal bytes from tier0 mean equal trees, bit for bit.
BINARY = (bytes([0x00, 0x05, 0x86, 0x1B, 0xD8, 0xF7, 0xC1, 0x40]), bytes([0xAD, 0x82, 0x75, 0xA4, 0x82, 0x67, 0xE7, 0x14]))
READ_ONLY = 8


class KV3ID(ctypes.Structure):
    _fields_ = [("name", ctypes.c_char_p), ("id0", ctypes.c_uint64), ("id1", ctypes.c_uint64)]


def load_tier0(path):
    os.add_dll_directory(os.path.dirname(path))
    dll = ctypes.WinDLL(path)
    load = getattr(dll, LOAD)
    load.restype = ctypes.c_bool
    load.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(KV3ID), ctypes.c_char_p, ctypes.c_uint]
    save = getattr(dll, SAVE)
    save.restype = ctypes.c_bool
    save.argtypes = [ctypes.POINTER(KV3ID), ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint]
    buffer = getattr(dll, BUFFER)
    buffer.restype = ctypes.c_void_p
    buffer.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
    growable = getattr(dll, GROWABLE)
    growable.restype = ctypes.c_void_p
    growable.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int]
    save_binary = getattr(dll, SAVE_BINARY)
    save_binary.restype = ctypes.c_bool
    save_binary.argtypes = [ctypes.POINTER(KV3ID), ctypes.POINTER(KV3ID), ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint]
    return load, save, buffer, growable, save_binary


def block_format(block):
    """The block's format GUID: KV3 v2+ and VKV3 both carry it in bytes 4..20 (after the magic)."""
    if len(block) < 20:
        return None
    return int.from_bytes(block[4:12], "little"), int.from_bytes(block[12:20], "little")


def as_text(fns, block, binary=False):
    load, save, buffer, growable, save_binary = fns
    fmt = block_format(block)
    if fmt is None:
        return None, "short block"
    data = ctypes.create_string_buffer(bytes(block), len(block))
    buf = ctypes.create_string_buffer(256)
    buffer(buf, data, len(block), READ_ONLY)
    kv = ctypes.create_string_buffer(256)
    # Word 0 bit 0: external storage, a KeyValues3 outside any arena. With it clear the loader looks for its
    # arena context before the object (tier0 0923 180219a50). Bits 1-8: the type, 1 = null to start.
    ctypes.c_uint64.from_buffer(kv).value = 1 | (1 << 1)
    err = ctypes.c_void_p(0)
    kid = KV3ID(b"oracle", fmt[0], fmt[1])
    if not load(kv, ctypes.byref(err), buf, ctypes.byref(kid), b"oracle", 0):
        return None, "LoadKV3: " + (ctypes.string_at(err.value).decode("utf-8", "replace") if err.value else "failed")
    if binary:
        out_buf = ctypes.create_string_buffer(256)
        growable(out_buf, 0, 0, 0)
        enc = KV3ID(b"binary", int.from_bytes(BINARY[0], "little"), int.from_bytes(BINARY[1], "little"))
        err3 = ctypes.c_void_p(0)
        if not save_binary(ctypes.byref(enc), ctypes.byref(kid), kv, ctypes.byref(err3), out_buf, 0):
            return None, "SaveKV3: " + (ctypes.string_at(err3.value).decode("utf-8", "replace") if err3.value else "failed")
        # CUtlBuffer: memory at +8, get at +0x10, put at +0x20 (LoadKV3 reads them so, tier0 0923 1801248e0).
        mem = ctypes.c_uint64.from_buffer(out_buf, 8).value
        put = ctypes.c_int32.from_buffer(out_buf, 0x20).value
        return ctypes.string_at(mem, put), None
    out = ctypes.c_void_p(0)
    err2 = ctypes.c_void_p(0)
    if not save(ctypes.byref(kid), kv, ctypes.byref(err2), ctypes.byref(out), 0):
        return None, "SaveKV3Text_ToString: " + (ctypes.string_at(err2.value).decode("utf-8", "replace") if err2.value else "failed")
    return (ctypes.string_at(out.value).decode("utf-8", "replace") if out.value else ""), None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dump")
    ap.add_argument("--tier0", default=DEFAULT_TIER0)
    ap.add_argument("--show", type=int, default=5)
    ap.add_argument("--binary", action="store_true", help="compare tier0's binary saves (bit for bit) instead of its text")
    a = ap.parse_args()
    fns = load_tier0(a.tier0)
    tally = collections.OrderedDict()
    shown = 0
    for line in open(os.path.join(a.dump, "index.tsv"), encoding="utf-8"):
        n, key, path, btype = line.rstrip("\n").split("\t")
        valve = open(os.path.join(a.dump, f"{n}.valve.bin"), "rb").read()
        ours = open(os.path.join(a.dump, f"{n}.ours.bin"), "rb").read()
        tv, ev = as_text(fns, valve, a.binary)
        to, eo = as_text(fns, ours, a.binary)
        t = tally.setdefault(key, collections.Counter())
        t["pairs"] += 1
        if ev:
            t["tier0 cannot read Valve's"] += 1
            if shown < a.show:
                shown += 1
                print(f"  {key} {path} {btype}: {ev}")
        elif eo:
            t["tier0 cannot read ours"] += 1
            if shown < a.show:
                shown += 1
                print(f"  {key} {path} {btype}: ours: {eo}")
        elif tv == to:
            t["same"] += 1
        else:
            t["differ"] += 1
            if shown < a.show:
                shown += 1
                if a.binary:
                    at = next((i for i, (x, y) in enumerate(zip(tv, to)) if x != y), min(len(tv), len(to)))
                    print(f"  {key} {path} {btype}: tier0's binary saves differ at byte {at} ({len(tv)} vs {len(to)} bytes)")
                    continue
                lv, lo = tv.splitlines(), to.splitlines()
                at = next((i for i, (x, y) in enumerate(zip(lv, lo)) if x != y), min(len(lv), len(lo)))
                print(f"  {key} {path} {btype}: first difference at line {at + 1}")
                print(f"    valve: {lv[at] if at < len(lv) else '<end>'}")
                print(f"    ours:  {lo[at] if at < len(lo) else '<end>'}")
    print(f"{'type version':<20} {'pairs':>6} {'same':>6} {'differ':>7} {'unreadable':>11}")
    for key, t in tally.items():
        print(f"{key:<20} {t['pairs']:>6} {t['same']:>6} {t['differ']:>7} {t['tier0 cannot read Valve' + chr(39) + 's'] + t['tier0 cannot read ours']:>11}")


if __name__ == "__main__":
    sys.exit(main())
