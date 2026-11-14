"""prefab_slot_probe_map.py <base.vmap> <prefab_donor.vmap> <out.vmap>: whether a prefab map's hollow nodes take
slots in its prefab's block (MapInstances.PrefabSlots; slot_probe_map.py measured an instance's block).
Two prefab maps are written under maps/prefabs/<out>/: p0 holds one light, p5 the light and five empty groups. The
base map's world gets [prefab of p0, instance A, prefab of p5, instance B] (A and B copies of the base map's first
instance, the prefabs copies of the donor's first CMapPrefab): B's copies start past A's block plus p5's block, so
B - A - (A's block) is p5's block less p0's."""
import copy, os, sys, tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402


def load(path):
    text_path = os.path.join(tempfile.mkdtemp(prefix="prefab_slot_"), "in.vmap")
    pm.convert(path, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    return text.split("\n", 1)[0], pm.parse(text)


def write(tops, header, out):
    text_out = os.path.join(tempfile.mkdtemp(prefix="prefab_slot_"), "out.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")


def main(base, donor, out):
    name = os.path.splitext(os.path.basename(out))[0]
    maps = os.path.dirname(out)
    serial = 0

    def fresh(e, ids):
        nonlocal serial
        e = copy.deepcopy(e)
        for x in pm.walk(e):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"9f5b0000-0000-4000-8000-{serial:012x}")
            if x.get("nodeID") is not None:
                x.set("nodeID", "int", str(next(ids)))
        return e

    import itertools
    paths = []
    for label, empties in (("p0", 0), ("p5", 5)):
        header, tops = load(base)
        everything = [e for t in tops for e in pm.walk(t)]
        world = next(e for e in everything if e.type == "CMapWorld")
        group = next(e for e in everything if e.type == "CMapGroup")
        light = next(c for c in group.get("children") if c.get("entity_properties").get("classname") == "light_omni2")
        ids = itertools.count(2)
        lit = fresh(light, ids)
        lit.get("entity_properties").set("targetname", "string", f"{label}_light")
        empty = []
        for _ in range(empties):
            g = fresh(group, ids)
            g.set("children", "element_array", [])
            empty.append(g)
        world.set("children", "element_array", [lit] + empty)
        rel = f"maps/prefabs/{name}/{label}.vmap"
        os.makedirs(os.path.join(maps, "prefabs", name), exist_ok=True)
        write(tops, header, os.path.join(maps, "prefabs", name, label + ".vmap"))
        paths.append(rel)

    header, tops = load(base)
    everything = [e for t in tops for e in pm.walk(t)]
    world = next(e for e in everything if e.type == "CMapWorld")
    instance = next(e for e in everything if e.type == "CMapInstance")
    prefab = next(e for t in load(donor)[1] for e in pm.walk(t) if e.type == "CMapPrefab")
    ids = itertools.count(max(int(e.get("nodeID")) for e in everything if e.get("nodeID") is not None) + 1)
    origin = [float(x) for x in instance.get("origin").split()]
    for k, rel in enumerate(paths):
        p = fresh(prefab, ids)
        p.set("targetMapPath", "string", rel)
        p.set("origin", "vector3", f"0 {512 * (k + 1)} 0")
        p.set("angles", "qangle", "0 0 0")
        p.set("children", "element_array", [])
        inst = fresh(instance, ids)
        inst.set("origin", "vector3", " ".join(f"{v:g}" for v in [origin[0], origin[1] + 512 * (k + 1), origin[2]]))
        world.get("children").extend([p, inst])
    write(tops, header, out)
    print("wrote", out, "and", ", ".join(paths))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
