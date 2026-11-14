"""slot_probe_map.py <base.vmap> <out.vmap>: which nodes take a slot in an instance's id block.
The base map's first CMapGroup and the CMapInstance of it are copied once per entry of VARIANTS, each group
holding [light A, <the variant's nodes>, light B] (copies of the group's first light_omni2, named slot<i>_a and
slot<i>_b), each instance moved 256 units further along y. resourcecompiler gives B's copy the id of A's copy
plus one plus the slots the variant's nodes take (MapInstances.Emit counts every node; the decompiler's
TemplatePlan stands empty groups in for slots that do not walk: docs/DECOMPILE.md, the RC check)."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402

# What stands between A and B: a list of ("group", [children]) / ("entity", classname, {field: (kind, value)}).
VARIANTS = [
    [],
    [("group", [])],
    [("group", [("entity", "info_target", {})])],
    [("entity", "info_target", {"editorOnly": ("bool", "1")})],
    [("group", [("group", [])])],
    [("group", []), ("group", [])],
    [("entity", "info_target", {})],
    [("group", [("entity", "info_target", {"editorOnly": ("bool", "1")})])],
]


def faceless(elements):
    """The first CMapMesh with no faces (VmapBuilder.AddSlot's). Before 10-29 evening this took the
    first mesh with no node children, which every mesh is: s2c_slotprobe3, 4 and 7's "empty mesh"
    is a mesh with faces (the decompile's first brush mesh)."""
    return next(e for e in elements if e.type == "CMapMesh" and not e.get("meshData").get("faceEdgeIndices"))


def skipped(mesh):
    """A copy of a mesh with every material toolsskip (its faces go at compile)."""
    m = copy.deepcopy(mesh)
    data = m.get("meshData")
    data.set("materials", "string_array", ["materials/tools/toolsskip.vmat"] * len(data.get("materials")))
    return m


def node_variants(tops, donor):
    """s2c_slotprobe3: the nodes the source's slots held (the group's prop_static, with and without its
    model, a world CMapMesh with its faces) and the decompile's empty CMapMesh (VmapBuilder.AddSlot,
    taken from the first group of <donor>, a decompiled .vmap)."""
    everything = [e for t in tops for e in pm.walk(t)]
    group = next(e for e in everything if e.type == "CMapGroup")
    prop = next(c for c in group.get("children") if c.get("entity_properties").get("classname") == "prop_static")
    bare = copy.deepcopy(prop)
    bare.get("entity_properties").set("model", "string", "")
    mesh = next(e for e in everything if e.type == "CMapMesh")
    text_path = os.path.join(tempfile.mkdtemp(prefix="slot_donor_"), "donor.vmap")
    pm.convert(donor, text_path, "keyvalues2")
    donated = [e for t in pm.parse(open(text_path, encoding="utf-8").read()) for e in pm.walk(t)]
    empty = faceless(donated)
    return [[("node", prop)], [("node", bare)], [("node", mesh)], [("node", empty)], [("node", empty), ("node", empty)]]


def instance_variants(donor):
    """s2c_slotprobe4: an instance of a group (in the world) between A and B: of an empty group, of a
    group holding the decompile's empty CMapMesh, of one holding an info_target; and two of one empty group."""
    text_path = os.path.join(tempfile.mkdtemp(prefix="slot_donor_"), "donor.vmap")
    pm.convert(donor, text_path, "keyvalues2")
    donated = [e for t in pm.parse(open(text_path, encoding="utf-8").read()) for e in pm.walk(t)]
    empty = faceless(donated)
    return [[("instance", [])], [("instance", [("node", empty)])], [("instance", [("entity", "info_target", {})])],
            [("instance", []), ("instance", [])]]


def main(base, out, high_empty=0, donor=None, instances=False):
    """high_empty: also put an empty CMapGroup in the world, nodeID that much past the highest
    (whether the first block's start counts it: removed at load, or only not cloned)."""
    work = tempfile.mkdtemp(prefix="slot_probe_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    everything = [e for t in tops for e in pm.walk(t)]
    group = next(e for e in everything if e.type == "CMapGroup")
    instance = next(e for e in everything if e.type == "CMapInstance" and e.get("target") == group.get("id"))
    light = next(c for c in group.get("children") if c.get("entity_properties").get("classname") == "light_omni2")
    world = next(e for e in everything if e.type == "CMapWorld")
    node = max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1
    serial = 0

    def fresh(e):
        nonlocal node, serial
        e = copy.deepcopy(e)
        for x in pm.walk(e):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"510e0000-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(node))
                node += 1
            if x.get("referenceID") is not None:
                x.set("referenceID", "uint64", "0x%016x" % (0x510e000000000000 + node))
        return e

    def named(name):
        e = fresh(light)
        e.get("entity_properties").set("targetname", "string", name)
        return e

    def build(spec):
        if spec[0] == "node":
            return fresh(spec[1])
        if spec[0] == "instance":
            target = fresh(group)
            target.set("children", "element_array", [build(s) for s in spec[1]])
            world.get("children").append(target)
            inner = fresh(instance)
            inner.set("target", "element", target.get("id"))
            inner.set("origin", "vector3", "0 0 0")
            return inner
        if spec[0] == "group":
            g = fresh(group)
            g.set("children", "element_array", [build(s) for s in spec[1]])
            return g
        e = fresh(light)
        e.get("entity_properties").set("classname", "string", spec[1])
        for key, (kind, value) in spec[2].items():
            e.set(key, kind, value)
        return e

    origin = [float(x) for x in instance.get("origin").split()]
    # s2c_slotprobe8: what 3, 4 and 7 meant to measure (a faceless mesh) and a toolsskip mesh.
    # s2c_slotprobe9: probe 8 with the toolsskip meshes faceless (its world files against 8's: does
    # a toolsskip face reach the compiled world?).
    if instances in ("faceless", "faceless_noskip"):
        text_path = os.path.join(tempfile.mkdtemp(prefix="slot_donor_"), "donor.vmap")
        pm.convert(donor, text_path, "keyvalues2")
        donated = [e for t in pm.parse(open(text_path, encoding="utf-8").read()) for e in pm.walk(t)]
        empty = ("node", faceless(donated))
        skip = empty if instances == "faceless_noskip" else ("node", skipped(next(e for e in everything if e.type == "CMapMesh")))
        variants = [[empty], [empty, empty], [skip], [("instance", [empty])], [("instance", [skip])], [("group", [empty])]]
    # s2c_slotprobe7: [A, <nodes>] with no B, each block's width read off the next block's A
    # (atixref's decompiled group 822: a light, an instance of a group holding a light_barn, an empty mesh).
    elif instances == "trailing":
        mesh = instance_variants(donor)[1][0][1][0]
        barn = [("instance", [("entity", "light_barn", {})])]
        variants = [[], [mesh], barn, barn + [mesh], [mesh, mesh], []]
    # s2c_slotprobe6: a group holding only an instance of an empty group, then a group holding that and an info_target.
    elif instances == "groupinst":
        variants = [[("group", [("instance", [])])], [("group", [("instance", []), ("entity", "info_target", {})])]]
    else:
        variants = [[], []] if instances == "toplevel" else instance_variants(donor) if instances else node_variants(tops, donor) if donor else VARIANTS
    for i, between in enumerate(variants):
        g = fresh(group)
        g.set("children", "element_array", [named(f"slot{i}_a")] + [build(s) for s in between]
              + ([] if instances == "trailing" else [named(f"slot{i}_b")]))
        inst = fresh(instance)
        inst.set("target", "element", g.get("id"))
        inst.set("origin", "vector3", " ".join(f"{v:g}" for v in [origin[0], origin[1] + 256 * (i + 1), origin[2]]))
        world.get("children").extend([g, inst])
        if instances == "toplevel":
            # s2c_slotprobe5: a top-level instance of an empty group after each variant's (does it take a block?)
            world.get("children").append(build(("instance", [])))
    if high_empty:
        g = fresh(group)
        g.set("children", "element_array", [])
        g.set("nodeID", "int", str(node + high_empty))
        world.get("children").append(g)
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out, len(variants), "instances")


if __name__ == "__main__":
    # slot_probe_map.py <base> <out> [high_empty] [donor.vmap] [instances|toplevel|groupinst|trailing|faceless|faceless_noskip]
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 0, sys.argv[4] if len(sys.argv) > 4 else None,
         sys.argv[5] if len(sys.argv) > 5 else False)
