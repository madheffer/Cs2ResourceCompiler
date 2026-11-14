"""foliage_localspace_specimen.py <probe01.vmap> <out.vmap>: the OPEN 1.b1 specimen, a copy of probe01 with
baked prop_statics (non-uniform scale, so AddStaticProps bakes them) whose models draw with materials that
answer NeedsLocalSpaceVertices (0x3b7ef4e7), beside ordinary draws of the same model. AddStaticProps
(180255b90) gives the whole prop flag 8 (local space) when any of its materials answers true, else 3
(placed, texcoords scaled):
  - generic_tree_large_1: csgo_foliage with S_FOLIAGE_ANIMATION (two materials) and two other materials;
    once plain, once with skin 1;
  - sumac_poison_tree_01: every material local space;
  - wine_bottle_01_a: csgo_simple_liquid beside two other materials;
  - pipe01_str_64a: no such material (the control, flag 3).
Built through Valve's dmxconvert as probe_map.py does; FoliageLocalSpaceProbe lists the stock candidates."""
import os
import sys
import tempfile

import probe_map as pm
from nav_round_specimen import prop

PROPS = [
    ("models/cs_italy/foliage/tree/generic_tree_large_1.vmdl", (300, 300, 0), "0 0 0", "1 1 1.5", None),
    ("models/cs_italy/foliage/tree/generic_tree_large_1.vmdl", (-300, 300, 0), "0 90 0", "1.25 1 1", "1"),
    ("models/foliage/trees/sumac_poison_tree/sumac_poison_tree_01.vmdl", (700, -300, 0), "0 30 0", "1 2 1", None),
    ("models/generic/winebottle_kit_01/wine_bottle_01_a.vmdl", (200, 800, 8), "0 30 0", "1 1 2", None),
    ("models/generic/pipe01/pipe01_str_64a.vmdl", (1000, -500, 8), "0 45 30", "2 1 1", None),
]


def main(base, out):
    work = tempfile.mkdtemp(prefix="foliage_ls_")
    text_path = os.path.join(work, "base.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    root = next(t for t in tops if t.type == "CMapRootElement")
    children = root.get("world").get("children")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    added = []
    for model, origin, angles, scales, skin in PROPS:
        e = prop(model, origin, angles, scales)
        if skin is not None:
            e.get("entity_properties").fields.append(["skin", "string", skin])
        added.append(e)
    for k, e in enumerate(added):
        for x in pm.walk(e):
            if x.get("id") is not None:
                x.set("id", "elementid", f"7d7d7d7d-0000-4000-8000-{k:04x}{id(x) & 0xffffffff:08x}")
        e.set("nodeID", "int", str(node + k))
        e.set("referenceID", "uint64", "0x%016x" % (0x7d7d000000000000 + k))
    children.extend(added)
    out_text = os.path.join(work, "out.vmap")
    open(out_text, "w", encoding="utf-8", newline="\n").write(pm.emit(tops, header))
    pm.convert(out_text, out, "binary")
    print("wrote", out)


if __name__ == "__main__":
    main(*sys.argv[1:3])
