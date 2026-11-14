"""exp2f_oracle.py <out.tsv> [count] -- call tier0's exported V_exp2f (archived tier0_20261103.dll,
loaded into this process with ctypes; no game) on edge cases plus a seeded sweep, and write
"input bits<TAB>output bits" lines (hex) for CrtMath.Exp2's tests."""
import ctypes, os, random, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths

dll = ctypes.CDLL(paths.binary("tier0_20261103.dll"))
exp2f = dll.V_exp2f
exp2f.restype = ctypes.c_float
exp2f.argtypes = [ctypes.c_float]

def bits(f):
    return struct.unpack("<I", struct.pack("<f", f))[0]

def flt(b):
    return struct.unpack("<f", struct.pack("<I", b))[0]

edge = [0x00000000, 0x80000000, 0x7f800000, 0xff800000, 0x7fc00000, 0xffc00000, 0x00000001, 0x80000001,
        bits(2147483648.0), bits(-2147483648.0), bits(1e10), bits(-1e10), bits(3.4e38), bits(-3.4e38)]
for v in [127.0, 127.49, 127.5, 127.99, 128.0, 128.5, 200.0, 228.9, 229.0, 229.5, 300.0,
          -126.0, -126.25, -126.5, -127.0, -127.5, -130.3, -140.77, -148.0, -149.0, -149.4, -149.5, -149.6,
          -150.0, -150.1, -151.0, -200.0, -228.9, -229.5, -300.0, -0.3949120044708252, -0.4092479944229126]:
    edge.append(bits(v))
rng = random.Random(1113)
count = int(sys.argv[2]) if len(sys.argv) > 2 else 20000
sweep = [bits(rng.uniform(-160.0, 140.0)) for _ in range(count)]
sweep += [bits(rng.uniform(-152.0, -125.0)) for _ in range(count)]
with open(sys.argv[1], "w") as out:
    for b in edge + sweep:
        out.write("%08x\t%08x\n" % (b, bits(exp2f(flt(b)))))
print(len(edge) + len(sweep), "values")
