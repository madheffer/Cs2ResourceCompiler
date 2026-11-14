"""runtime_prefab_probe_map.py <base.vmap> <out.vmap> [--runtime 0|1] [--entity 0|1] [--trailing] [--dump]: a probe
for prefabs loaded at runtime (docs/DECOMPILE.md item 11).

A copy of <base.vmap> (a map placing prefabs, e.g. s2c_prefabprobe.vmap) with every CMapPrefab's loadAtRuntime
and prefabRuntimeEntity set as given, nothing else changed, so resourcecompiler's package of it against the
base's shows what a runtime prefab leaves in the map. --trailing also puts an info_target (s2c_after_prefab, a
copy of the world's last entity) after the last prefab among the world's children, so its compile_source_id
shows whether the prefab's point_prefab takes a walk number. --dump prints the base's prefab elements as text
and writes nothing.
"""
import argparse
import copy
import os
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("base")
    p.add_argument("out", nargs="?")
    p.add_argument("--runtime", default="1")
    p.add_argument("--entity", default="0")
    p.add_argument("--trailing", action="store_true")
    p.add_argument("--target", help="every prefab's targetMapPath instead of its own (a map in a subfolder: targetMapName's form)")
    p.add_argument("--fixup", help="every prefab's fixupEntityNames (0 or 1)")
    p.add_argument("--dump", action="store_true")
    a = p.parse_args()
    work = tempfile.mkdtemp(prefix="rtprefab_probe_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(a.base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    prefabs = [e for t in tops for e in pm.walk(t) if e.type == "CMapPrefab"]
    if a.dump:
        for e in prefabs:
            print(e.type, [(f[0], f[2]) for f in e.fields if f[1] not in ("element_array", "DmePlugList")])
        return
    if not a.out:
        raise SystemExit("out.vmap is needed unless --dump")
    for e in prefabs:
        e.set("loadAtRuntime", "bool", a.runtime)
        e.set("prefabRuntimeEntity", "bool", a.entity)
        if a.target:
            e.set("targetMapPath", "string", a.target)
        if a.fixup is not None:
            e.set("fixupEntityNames", "bool", a.fixup)
    if a.trailing:
        everything = [e for t in tops for e in pm.walk(t)]
        world = next(t for t in tops if t.type == "CMapRootElement").get("world")
        children = world.get("children")
        donor = [c for c in children if c.type == "CMapEntity"][-1]
        e = copy.deepcopy(donor)
        node = max(int(x.get("nodeID")) for x in everything if x.get("nodeID") is not None) + 1
        serial = 0
        for x in pm.walk(e):
            if x.get("id") is not None:
                serial += 1
                x.set("id", "elementid", f"5c2cfa00-0000-4000-8000-{serial:012x}")
        e.set("nodeID", "int", str(node))
        e.set("referenceID", "uint64", "0x%016x" % (0x5c2cfa0000000000 + node))
        e.set("origin", "vector3", "128 256 32")
        props = e.get("entity_properties")
        props.set("classname", "string", "info_target")
        props.set("targetname", "string", "s2c_after_prefab")
        last = max(i for i, c in enumerate(children) if c.type == "CMapPrefab")
        children.insert(last + 1, e)
        print("info_target s2c_after_prefab node", node, "after child", last)
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, a.out, "binary")
    print("wrote", a.out, "with", len(prefabs), "prefab(s): loadAtRuntime", a.runtime, "prefabRuntimeEntity", a.entity)


if __name__ == "__main__":
    main()
