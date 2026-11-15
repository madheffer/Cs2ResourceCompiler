"""bc_gpu_tables.py -- pack the GPU's measured BC decode (BcVendorDecodeProbe's float32 tables) for the library.

    python tools/re/bc_gpu_tables.py <probe out dir> <vendor> [<dest dir>]

The probe (tests/Source2.Compiler.Tests/BcVendorDecodeProbe.cs) decodes every BC1/BC3 colour endpoint pair
and selector per channel, and every BC4 / BC3-alpha endpoint pair and index, on the GPU, and checks the
tables on random blocks. This writes them as the library embeds them, losslessly:

  <vendor>_bc1_four.bin, _bc1_three.bin, _bc3_colour.bin   per channel R(32x32), G(64x64), B(32x32) pairs x 4
                                                             selectors, one byte each (value k/255; 0 for an
                                                             endpoint pair the mode cannot hold, never read)
  <vendor>_bc3_alpha.bin                                     256 x 256 pairs x 8 indices, one byte (k/255)
  <vendor>_bc4.bin                                           256 x 256 pairs x 8 indices, uint16 LE (k/65535)
  <vendor>_srgb.bin                                          sRGB to linear by UNORM byte, 256 float32 LE (BcSrgbDecodeProbe)

each gzipped. It refuses a table whose values are not exactly k/255 or k/65535 in float32, so nothing is
rounded on the way. Destination: src/Source2.Compiler/Texture/Decoding/Gpu.
"""
import gzip
import os
import sys

import numpy as np

src, vendor = sys.argv[1], sys.argv[2]
dest = sys.argv[3] if len(sys.argv) > 3 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..",
                                                            "src", "Source2.Compiler", "Texture", "Decoding", "Gpu")
os.makedirs(dest, exist_ok=True)


def exact(v, den, what):
    k = np.round(v.astype(np.float64) * den)
    if not np.all((k / den).astype(np.float32) == v):
        raise SystemExit(f"{what}: not every value is k/{den} in float32")
    return k


def write(name, data):
    # mtime 0 so the same tables give the same bytes.
    with open(os.path.join(dest, f"{vendor}_{name}.bin.gz"), "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", compresslevel=9, mtime=0) as f:
        f.write(data)


for name in ("bc1_four", "bc1_three", "bc3_colour"):
    a = np.fromfile(os.path.join(src, f"{vendor}_{name}.f32"), dtype="<f4")
    mask = np.isnan(a)
    k = exact(a[~mask], 255, name)
    out = np.zeros(a.size, dtype=np.uint8)
    out[~mask] = k.astype(np.uint8)
    write(name, out.tobytes())   # the decoder chooses the mode first, so it never reads a pair the mode cannot hold
    print(f"{name}: {a.size} entries, {mask.sum()} the mode cannot hold")
a = np.fromfile(os.path.join(src, f"{vendor}_bc3_alpha.f32"), dtype="<f4")
write("bc3_alpha", exact(a, 255, "bc3_alpha").astype(np.uint8).tobytes())
a = np.fromfile(os.path.join(src, f"{vendor}_bc4.f32"), dtype="<f4")
write("bc4", exact(a, 65535, "bc4").astype("<u2").tobytes())
# sRGB (BcSrgbDecodeProbe): the colour decoded in 8 bits, then this table by UNORM byte; float32 kept as measured.
srgb = os.path.join(src, f"{vendor}_srgb.f32")
if os.path.exists(srgb):
    a = np.fromfile(srgb, dtype="<f4")
    if a.size != 256 or np.isnan(a).any():
        raise SystemExit("srgb: expected 256 measured entries")
    write("srgb", a.astype("<f4").tobytes())
open(os.path.join(dest, f"{vendor}_source.txt"), "w").write(open(os.path.join(src, f"{vendor}_manifest.txt")).read())
for f in sorted(os.listdir(dest)):
    print(f, os.path.getsize(os.path.join(dest, f)))
