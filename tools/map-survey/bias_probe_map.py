"""bias_probe_map.py <base.vmap> <ze_hold_em_p.vmap> <out.vmap>: how lightmapScaleBias shows in a lit compile.

The decompiler needs each world face's lightmapScaleBias: it groups the faces into physics pieces
(docs/DECOMPILE.md, "What the pieces need"), and ze_hold_em_p's fixture build shows no lightmap density to read
it from. Into the base map (probe01) go:
  - copies of its first dev_measuregeneric01 box, one per bias of BIASES, every face at that bias, 256 units
    apart along y (the density rule: ChartMeshes scales a face set's charts by 2^bias);
  - one box whose faces alternate bias 0 and 1 (one mesh, two face sets);
  - ze_hold_em_p's node 409 (subdivided, painted, three materials, faces at bias 0 and 1), moved to y 4096,
    so the decompile's whole case is in a map that compiles lit.
New element ids, node ids and reference ids throughout. Compile it lit with Valve's resourcecompiler (CS2
closed), then read the lightmap texcoords (DECOMPILE_LMDENSITY) against the biases set here."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

BIASES = [-2, -1, 0, 1, 2, 3]
# The box is 512 units along y: s2c_biasprobe (10-30) used 256 and its boxes overlapped; s2c_biasprobe2 uses
# BIAS_PROBE_SPACING=640 BIAS_PROBE_ZE_Y=8192.
SPACING = int(os.environ.get("BIAS_PROBE_SPACING", "256"))
ZE_Y = os.environ.get("BIAS_PROBE_ZE_Y", "4096")  # "none": leave node 409 out
# BIAS_PROBE_ROW=1 (s2c_biasprobe4): the boxes' vertices scaled 0.25 x 0.25, in a row along x 160 apart at y 0, z 128, all
# inside probe01's toolslightmapres volume 79 (x -208..1248, y -928..912, z 0..256) and clear of its geometry;
# probes 1 and 2 crossed volumes 79 and 93, which set the base density.
ROW = os.environ.get("BIAS_PROBE_ROW") == "1"


def load(path):
    work = tempfile.mkdtemp(prefix="bias_probe_")
    text_path = os.path.join(work, "text.vmap")
    pm.convert(path, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    return text.split("\n", 1)[0], pm.parse(text)


def everything(tops):
    return [e for t in tops for e in pm.walk(t)]


def stream(mesh, array, name):
    data = mesh.get("meshData").get(array)
    return next(s for s in data.get("streams") if isinstance(s, pm.Element) and s.get("standardAttributeName") == name)


class Ids:
    def __init__(self, node):
        self.node, self.serial = node, 0

    def renew(self, root):
        for x in pm.walk(root):
            if x.get("id") is not None:
                self.serial += 1
                x.set("id", "elementid", f"b1a50000-0000-4000-8000-{self.serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(self.node))
                self.node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0xb1a5000000000000 + self.node))


def main(base, ze, out):
    header, tops = load(base)
    _, ze_tops = load(ze)
    nodes = everything(tops)
    world = next(e for e in nodes if e.type == "CMapWorld")
    box = next(e for e in nodes if e.type == "CMapMesh"
               and any("dev_measuregeneric01" in m for m in e.get("meshData").get("materials")))
    ids = Ids(max(int(e.get("nodeID")) for e in nodes if e.get("nodeID") is not None) + 1)
    origin = [float(x) for x in box.get("origin").split()]
    made = []

    def place(c, dy):
        if ROW:
            k = dy // SPACING - 1
            c.set("origin", "vector3", f"{-112 + 160 * k:g} 0 128")
            # resourcecompiler ignores a mesh's scales (s2c_biasprobe3, 10-30): scale the vertices.
            s = stream(c, "vertexData", "position")
            s.set("data", s.fields[[f[0] for f in s.fields].index("data")][1],
                  [" ".join(f"{float(v) * m:g}" for v, m in zip(p.split(), (0.25, 0.25, 1))) for p in s.get("data")])
        else:
            c.set("origin", "vector3", " ".join(f"{v:g}" for v in [origin[0], origin[1] + dy, origin[2]]))
        world.get("children").append(c)

    for i, bias in enumerate(BIASES):
        c = copy.deepcopy(box)
        ids.renew(c)
        s = stream(c, "faceData", "lightmapScaleBias")
        s.set("data", "int_array", [str(bias)] * len(s.get("data")))
        place(c, SPACING * (i + 1))
        made.append((c.get("nodeID"), f"all faces {bias}"))
    c = copy.deepcopy(box)
    ids.renew(c)
    s = stream(c, "faceData", "lightmapScaleBias")
    s.set("data", "int_array", [str(k % 2) for k in range(len(s.get("data")))])
    place(c, SPACING * (len(BIASES) + 1))
    made.append((c.get("nodeID"), "faces alternating 0, 1"))

    source = next(e for e in everything(ze_tops) if e.type == "CMapMesh" and e.get("nodeID") == "409")
    if ZE_Y == "none":
        source = None
    if source is not None:
      c = copy.deepcopy(source)
      ids.renew(c)
      o = [float(x) for x in source.get("origin").split()]
      c.set("origin", "vector3", " ".join(f"{v:g}" for v in [o[0], float(ZE_Y), o[2]]))
      world.get("children").append(c)
      biases = stream(c, "faceData", "lightmapScaleBias").get("data")
      made.append((c.get("nodeID"), f"ze_hold_em_p node 409: biases {sorted(set(biases))}, {biases.count('0')} at 0, {biases.count('1')} at 1"))

    work = tempfile.mkdtemp(prefix="bias_probe_out_")
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out)
    for node, what in made:
        print(f"  node {node}: {what}")


if __name__ == "__main__":
    main(*sys.argv[1:4])
