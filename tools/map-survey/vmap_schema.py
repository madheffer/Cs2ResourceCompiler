"""Every element type a .vmap holds, with each attribute set it is written with
(names, order, DMX type) and how many elements use it. This is where the decompiler's
element shapes come from: a Hammer save, not a guess (docs/DECOMPILE.md).

    python vmap_schema.py <a.vmap> [<b.vmap> ...] [--type CMapEntity]
"""
import collections
import struct
import sys

TYPE_NAMES = {1: 'element', 2: 'int', 3: 'float', 4: 'bool', 5: 'string', 6: 'binary', 7: 'time', 8: 'color',
              9: 'vector2', 10: 'vector3', 11: 'vector4', 12: 'qangle', 13: 'quaternion', 14: 'matrix',
              15: 'uint64', 16: 'uint8'}
SIZES = {2: 4, 3: 4, 4: 1, 7: 4, 8: 4, 9: 8, 10: 12, 11: 16, 12: 12, 13: 16, 14: 64, 15: 8, 16: 1}


def type_name(t):
    return TYPE_NAMES[t - 32] + '_array' if t > 32 else TYPE_NAMES[t]


def schema(path):
    data = open(path, 'rb').read()
    off = data.index(b'\0') + 1
    u32 = lambda o: struct.unpack_from('<I', data, o)[0]

    def cstr(o):
        e = data.index(b'\0', o)
        return data[o:e].decode('utf-8', 'replace'), e + 1

    def skip(o, t, inline):
        if t > 32:
            n = u32(o); o += 4
            for _ in range(n):
                o = skip(o, t - 32, True)
            return o
        if t == 5:
            return cstr(o)[1] if inline else o + 4
        if t == 6:
            return o + 4 + u32(o)
        if t == 1:
            return o + 4
        return o + SIZES[t]

    n = u32(off); off += 4
    for _ in range(n):
        c = u32(off); off += 4
        for _ in range(c):
            _, off = cstr(off); t = data[off]; off += 1; off = skip(off, t, True)
    ns = u32(off); off += 4
    strs = []
    for _ in range(ns):
        s, off = cstr(off); strs.append(s)
    ne = u32(off); off += 4
    types = []
    for _ in range(ne):
        ti = struct.unpack_from('<i', data, off)[0]; off += 24
        types.append(strs[ti])
    out = collections.OrderedDict()
    for t in types:
        c = u32(off); off += 4
        attrs = []
        for _ in range(c):
            ni = struct.unpack_from('<i', data, off)[0]; off += 4
            ty = data[off]; off += 1
            off = skip(off, ty, False)
            attrs.append(f'{strs[ni]}:{type_name(ty)}')
        out.setdefault(t, collections.Counter())[tuple(attrs)] += 1
    return out


def main():
    args = sys.argv[1:]
    only = None
    if '--type' in args:
        i = args.index('--type'); only = args[i + 1]; del args[i:i + 2]
    for path in args:
        print('==', path)
        for t, shapes in schema(path).items():
            if only and t != only:
                continue
            for attrs, count in shapes.items():
                print(f'{t} x{count}: ' + ', '.join(attrs))


if __name__ == '__main__':
    main()
