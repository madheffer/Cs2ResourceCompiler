"""nav_settings_probe_map.py <base.vmap> <out.vmap> key=value...: a probe for the map's nav settings (docs/DECOMPILE.md
item 16). A copy of <base.vmap> (one with a CMapNavData, e.g. probe01.vmap) whose navData has the given settings*
fields set (bool, int and float fields by the base's own type; an array field as a comma list), nothing else changed,
so the .nav resourcecompiler writes shows which of them it was built with (Decompile.NavSettings reads them back).
"""
import os
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "coverage"))
import probe_map as pm  # noqa: E402


def main(base, out, pairs):
    work = tempfile.mkdtemp(prefix="nav_probe_")
    text_path = os.path.join(work, "base_text.vmap")
    pm.convert(base, text_path, "keyvalues2")
    text = open(text_path, encoding="utf-8").read()
    header = text.split("\n", 1)[0]
    tops = pm.parse(text)
    data = next(e for t in tops for e in pm.walk(t) if e.type == "CDmeNavData")
    kinds = {f[0]: f[1] for f in data.fields}
    for pair in pairs:
        key, value = pair.split("=", 1)
        kind = kinds.get(key)
        if kind is None:
            raise SystemExit(f"navData has no field {key}")
        data.set(key, kind, value.split(",") if kind.endswith("_array") else value)
        print(key, kind, value)
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(pm.emit(tops, header))
    pm.convert(text_out, out, "binary")
    print("wrote", out)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3:])
