"""variable_probe_map.py <prefab probe.vmap> <its prefab map.vmap> <out dir>: where map variables land in a compile.

Every Hammer source has a CMapVariableSet (root mapVariables) and every node variableTargetKeys/variableNames, but
none of the 232 sources on this machine fills one (docs/DECOMPILE.md, "Scripts and map links"). This writes two
maps into <out dir>:
  - s2c_varinner.vmap, the prefab map with two variables, relayname ("def_relay") and prio ("0"), the first
    counter-terrorist spawn's targetname bound to relayname and the first terrorist spawn's priority to prio;
  - s2c_varprobe.vmap, the prefab probe with its prefab pointed at s2c_varinner, overriding both
    (variableOverrideNames/Values: "over_relay", "5"), a second placement of it 512 units along x with no
    override, and a variable of its own, mapvar ("map_level"), bound to its first counter-terrorist spawn's
    targetname.
CMapVariableSet's fields are resourcecompiler's (the element's attribute list, 180fcf110 in 1.41.9.3) as Hammer
writes them (string_array x5, m_ChoiceGroups element_array, variableAndChoiceOrder int_array). The type name
"string" and variableAndChoiceOrder = the variables' indices are not from a Hammer save: open the maps in Hammer
and re-save them before trusting either. Compile both with Valve's resourcecompiler (CS2 closed) and read the
entity lump."""
import copy, os, re, sys, uuid

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402


def load(path, work):
    text_path = os.path.join(work, os.path.basename(path) + ".txt")
    pm.convert(path, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    return text.split("\n", 1)[0], pm.parse(text)


def save(header, tops, path, work):
    text_path = os.path.join(work, os.path.basename(path) + ".out.txt")
    open(text_path, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_path, path, "binary")


def nodes(tops):
    return [e for t in tops for e in pm.walk(t)]


def classname(e):
    p = e.get("entity_properties")
    return p.get("classname") if isinstance(p, pm.Element) else None


def first(tops, cls):
    return next(e for e in nodes(tops) if e.type == "CMapEntity" and classname(e) == cls)


def bind(node, pairs):
    node.set("variableTargetKeys", "string_array", [k for k, _ in pairs])
    node.set("variableNames", "string_array", [v for _, v in pairs])


def variables(tops, defs):
    root = next(t for t in tops if t.type == "CMapRootElement")
    vs = root.get("mapVariables")
    vs.fields = [f for f in vs.fields if f[0] == "id"]
    vs.set("variableNames", "string_array", [n for n, _ in defs])
    vs.set("variableValues", "string_array", [v for _, v in defs])
    vs.set("variableTypeNames", "string_array", ["string"] * len(defs))
    vs.set("variableTypeParameters", "string_array", [""] * len(defs))
    vs.set("variableGroupNames", "string_array", [""] * len(defs))
    vs.set("m_ChoiceGroups", "element_array", [])
    vs.set("variableAndChoiceOrder", "int_array", [str(i) for i in range(len(defs))])


def fresh_ids(e, node_id, ref):
    """New element ids under e; e itself takes node_id and ref."""
    for x in pm.walk(e):
        for f in x.fields:
            if f[0] == "id" and f[1] == "elementid":
                f[2] = str(uuid.uuid4())
    e.set("nodeID", "int", str(node_id))
    e.set("referenceID", "uint64", ref)


def main(probe, inner, out_dir):
    work = os.path.join(out_dir, "_varprobe_work")
    os.makedirs(work, exist_ok=True)

    header, tops = load(inner, work)
    variables(tops, [("relayname", "def_relay"), ("prio", "0")])
    bind(first(tops, "info_player_counterterrorist"), [("targetname", "relayname")])
    bind(first(tops, "info_player_terrorist"), [("priority", "prio")])
    save(header, tops, os.path.join(out_dir, "s2c_varinner.vmap"), work)

    header, tops = load(probe, work)
    variables(tops, [("mapvar", "map_level")])
    bind(first(tops, "info_player_counterterrorist"), [("targetname", "mapvar")])
    prefab = next(e for e in nodes(tops) if e.type == "CMapPrefab")
    prefab.set("targetMapPath", "string", "maps/s2c_varinner.vmap")
    second = copy.deepcopy(prefab)
    prefab.set("variableOverrideNames", "string_array", ["relayname", "prio"])
    prefab.set("variableOverrideValues", "string_array", ["over_relay", "5"])
    top_id = max(int(m) for m in re.findall(r"\d+", " ".join(e.get("nodeID") or "0" for e in nodes(tops))))
    fresh_ids(second, top_id + 1, "0x5c2cf00000000002")
    x, y, z = (float(v) for v in second.get("origin").split())
    second.set("origin", "vector3", f"{x + 512:g} {y:g} {z:g}")
    world = next(t for t in tops if t.type == "CMapRootElement").get("world")
    parent = next(e for e in nodes(tops) if isinstance(e.get("children"), list) and prefab in e.get("children"))
    kids = parent.get("children")
    kids.insert(kids.index(prefab) + 1, second)
    assert world is not None
    save(header, tops, os.path.join(out_dir, "s2c_varprobe.vmap"), work)
    print("wrote s2c_varinner.vmap, s2c_varprobe.vmap in", out_dir)


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(*sys.argv[1:])
