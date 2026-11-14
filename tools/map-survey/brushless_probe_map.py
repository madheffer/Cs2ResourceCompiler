"""brushless_probe_map.py <base.vmap> <out.vmap>: which brush entities ship with no geometry.
The base map's first func_brush (s2c_pinst7's 2138, whose mesh names a material CS2 does not have, so its model
compile fails and the entity ships anyway) is copied once per entry of VARIANTS, each 128 units further along x and
named brushless<i>: with its meshes, with none, and as other brush classes with none. The decompiler writes a
brush entity whose model is not in the package with no meshes (docs/DECOMPILE.md, the RC check)."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

# (classname or None to keep it, keep the meshes)
VARIANTS = [
    (None, True),
    (None, False),
    ("trigger_multiple", False),
    ("func_button", False),
    ("func_door", False),
    ("func_breakable", False),
    ("func_water", False),
]


def main(base, out):
    work = tempfile.mkdtemp(prefix="brushless_probe_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    brush = next(e for e in everything if e.type == "CMapEntity" and e.get("entity_properties").get("classname") == "func_brush")
    world = next(e for e in everything if e.type == "CMapWorld")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    serial = 0
    origin = [float(x) for x in brush.get("origin").split()]
    for i, (cls, meshes) in enumerate(VARIANTS):
        c = copy.deepcopy(brush)
        if not meshes:
            c.set("children", "element_array", [])
        for x in pm.walk(c):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"b7e50000-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0xb7e5000000000000 + node))
        props = c.get("entity_properties")
        if cls is not None:
            props.set("classname", "string", cls)
        props.set("targetname", "string", f"brushless{i}")
        c.set("origin", "vector3", " ".join(f"{v:g}" for v in [origin[0] + 128 * (i + 1), origin[1], origin[2]]))
        world.get("children").append(c)
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, len(VARIANTS), "brush entities from node", brush.get("nodeID"))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
