"""chart_diff.py: which lightmap charts hold the luxels where two vrad3 EXRs differ.

    python chart_diff.py <job dir> <ours dir> [--files a.exr,b.exr] [--top 40] [--json out.json]

<job dir> is Valve's vrad3 job (lightmap_info.dat, lightmap_packing_geometry.dat and Valve's EXRs);
<ours dir> holds our EXRs of the same names. Compares the half bits exactly. lightmap_info.dat is each
luxel's chart id in RunVrad3's block order (LightmapTexels.Write: 1024-luxel blocks, row-major over the
blocks and within each). Per chart: luxels, differing luxels per file, its atlas rect, flags and the world
box of its packing triangles (position at vertex offset 0), so a chart can be paired with a map mesh.
"""
import argparse
import json
import os
import struct
import sys

import numpy as np


def read_exr(path):
    """{channel: uint16 plane (h, w)} of an uncompressed half scanline EXR, one scanline per block."""
    f = np.memmap(path, np.uint8, "r")
    head = bytes(f[:1 << 16])
    i, channels, window = 8, [], None
    while head[i] != 0:
        j = head.index(b"\0", i)
        k = head.index(b"\0", j + 1)
        size = int.from_bytes(head[k + 1:k + 5], "little")
        name, value = head[i:j], head[k + 5:k + 5 + size]
        if name == b"channels":
            p = 0
            while value[p] != 0:
                q = value.index(b"\0", p)
                channels.append((value[p:q].decode(), int.from_bytes(value[q + 1:q + 5], "little")))
                p = q + 17
        elif name == b"compression" and value != b"\0":
            sys.exit(f"{path}: compressed EXR")
        elif name == b"dataWindow":
            window = struct.unpack("<4i", value)
        i = k + 5 + size
    if any(t != 1 for _, t in channels):
        sys.exit(f"{path}: half channels only")
    w, h = window[2] - window[0] + 1, window[3] - window[1] + 1
    offsets = np.frombuffer(head, np.uint64, h, i + 1).astype(np.int64)
    n = len(channels)
    rows = np.empty((h, n, w), np.uint16)
    for y in range(h):
        o = int(offsets[y]) + 8
        rows[y] = np.frombuffer(f, np.uint16, n * w, o).reshape(n, w)
    return {c: rows[:, ci, :] for ci, (c, _) in enumerate(channels)}, w, h


def read_ids(path, w, h, block=1024):
    raw = np.fromfile(path, np.uint32)
    if raw.size != w * h:
        sys.exit(f"{path}: {raw.size} ids for a {w}x{h} atlas")
    block = min(block, w, h)
    ids = np.empty((h, w), np.uint32)
    at = 0
    for by in range(0, h, block):
        for bx in range(0, w, block):
            y1, x1 = min(by + block, h), min(bx + block, w)
            n = (y1 - by) * (x1 - bx)
            ids[by:y1, bx:x1] = raw[at:at + n].reshape(y1 - by, x1 - bx)
            at += n
    return ids


def read_packing(path):
    b = open(path, "rb").read()
    p = 0

    def u32(n):
        nonlocal p
        a = np.frombuffer(b, np.uint32, n, p)
        p += 4 * n
        return a

    u32(int(u32(1)[0]))
    stride, count = (int(x) for x in u32(2))
    verts = np.frombuffer(b, np.uint8, stride * count, p).reshape(count, stride)
    p += stride * count
    p += 8 * count                                       # atlas uv
    indices = u32(int(u32(1)[0]))
    nc = int(u32(1)[0])
    charts = np.frombuffer(b, np.uint32, nc * 7, p).reshape(nc, 7)
    p += nc * 28
    tri_charts = u32(indices.size // 3)
    pos = verts[:, :12].copy().view(np.float32).reshape(count, 3)
    return charts, indices.reshape(-1, 3), tri_charts, pos


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("job")
    ap.add_argument("ours")
    ap.add_argument("--files", default="irradiance.exr,directional_irradiance.exr,direct_light_shadows.exr,lightmap_chart_color.exr")
    ap.add_argument("--top", type=int, default=40)
    ap.add_argument("--json")
    a = ap.parse_args()

    charts, tris, tri_charts, pos = read_packing(os.path.join(a.job, "lightmap_packing_geometry.dat"))
    ids = None
    per_file = {}
    for name in a.files.split(","):
        valve, w, h = read_exr(os.path.join(a.job, name))
        ours, w2, h2 = read_exr(os.path.join(a.ours, name))
        if (w, h) != (w2, h2) or valve.keys() != ours.keys():
            sys.exit(f"{name}: shapes or channels differ")
        if ids is None:
            ids = read_ids(os.path.join(a.job, "lightmap_info.dat"), w, h)
        diff = np.zeros((h, w), bool)
        for c in valve:
            diff |= valve[c] != ours[c]
        hit = ids[diff]
        outside = int((hit == 0xFFFFFFFF).sum())
        counts = np.bincount(hit[hit != 0xFFFFFFFF].astype(np.int64), minlength=len(charts))
        per_file[name] = counts
        print(f"{name}: {int(diff.sum())} luxels differ in {int((counts > 0).sum())} charts, {outside} outside any chart")

    luxels = np.bincount(ids[ids != 0xFFFFFFFF].astype(np.int64), minlength=len(charts))
    # World box per chart from its triangles.
    lo = np.full((len(charts), 3), np.inf, np.float32)
    hi = np.full((len(charts), 3), -np.inf, np.float32)
    for k in range(3):
        np.minimum.at(lo, tri_charts, pos[tris[:, k]])
        np.maximum.at(hi, tri_charts, pos[tris[:, k]])
    tri_count = np.bincount(tri_charts.astype(np.int64), minlength=len(charts))

    total = sum(per_file.values())
    order = np.argsort(-total, kind="stable")
    names = list(per_file)
    print(f"\n{'chart':>6} {'luxels':>7} {'tris':>5} " + " ".join(f"{n.split('.')[0][:12]:>12}" for n in names) + "  rect  flags  world box")
    rows = []
    for c in order[:a.top]:
        if total[c] == 0:
            break
        _, index, x, y, cw, ch, flags = (int(v) for v in charts[c])
        print(f"{c:>6} {luxels[c]:>7} {tri_count[c]:>5} " + " ".join(f"{int(per_file[n][c]):>12}" for n in names)
              + f"  {x},{y} {cw}x{ch}  {flags:08x}  ({lo[c][0]:.0f},{lo[c][1]:.0f},{lo[c][2]:.0f})-({hi[c][0]:.0f},{hi[c][1]:.0f},{hi[c][2]:.0f})")
    for c in np.nonzero(total)[0]:
        _, index, x, y, cw, ch, flags = (int(v) for v in charts[c])
        rows.append({"chart": int(c), "luxels": int(luxels[c]), "tris": int(tri_count[c]),
                     "diff": {n: int(per_file[n][c]) for n in names}, "rect": [x, y, cw, ch], "flags": flags,
                     "lo": lo[c].tolist(), "hi": hi[c].tolist()})
    if a.json:
        json.dump(rows, open(a.json, "w"), indent=1)
    print(f"\ncharts with any difference: {len(rows)} of {len(charts)}")


if __name__ == "__main__":
    main()
