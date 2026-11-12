"""addon_material_specimen.py [<base.vmap>] [<out.vmap>]: s2c_addonmat, probe01 with two materials that exist only in
the s2c_rc_probe addon, for 8.b1 (compile-map compiling a map's addon content; GROUND_TRUTH 255).

The materials and images are RcReference probes whose compiled output the material compiler already matches byte
for byte (tests/Source2.Compiler.Tests/RcReference/materials): cx_full.vmat (csgo_complex, five images, a tint)
and cx_color.vmat (csgo_complex, one image). They are copied into content/csgo_addons/s2c_rc_probe/materials/
s2cmat/ (their own paths name materials/s2cmat/), and probe01's materials are swapped:

  - materials/dev/dev_measuregeneric01.vmat -> materials/s2cmat/cx_full.vmat
  - materials/dev/reflectivity_30.vmat -> materials/s2cmat/cx_color.vmat

Existing files under materials/s2cmat/ are left alone if equal and refused if different. Then compile it with
Valve's tools (tools/pipeline/compile_fresh.py s2c_rc_probe s2c_addonmat) and list what appears under
game/csgo_addons/s2c_rc_probe/materials/.
"""
import filecmp
import os
import shutil
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import probe_map as pm  # noqa: E402

ADDON = os.path.join(os.path.dirname(pm.GAME), "content", "csgo_addons", "s2c_rc_probe")
PROBES = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "tests", "Source2.Compiler.Tests",
                      "RcReference", "materials")
FILES = ["cx_full.vmat", "cx_color.vmat", "color.png", "normal.png", "rough.png", "metal.png", "ao.png"]
SWAPS = {
    "materials/dev/dev_measuregeneric01.vmat": "materials/s2cmat/cx_full.vmat",
    "materials/dev/reflectivity_30.vmat": "materials/s2cmat/cx_color.vmat",
}


def main(base, out):
    dest = os.path.join(ADDON, "materials", "s2cmat")
    os.makedirs(dest, exist_ok=True)
    for name in FILES:
        src, dst = os.path.join(PROBES, name), os.path.join(dest, name)
        if os.path.exists(dst):
            if not filecmp.cmp(src, dst, shallow=False):
                raise SystemExit(f"{dst} exists and differs from {src}")
            continue
        shutil.copy2(src, dst)
        print("copied", name)
    with tempfile.TemporaryDirectory() as tmp:
        text = os.path.join(tmp, "base.vmap")
        pm.convert(base, text, "keyvalues2")
        with open(text, encoding="utf-8") as f:
            kv2 = f.read()
        for old, new in SWAPS.items():
            count = kv2.count('"' + old + '"')
            if count == 0:
                raise SystemExit(f"{old} not in {base}")
            kv2 = kv2.replace('"' + old + '"', '"' + new + '"')
            print(f"{old} -> {new}: {count}")
        swapped = os.path.join(tmp, "swapped.vmap")
        with open(swapped, "w", encoding="utf-8") as f:
            f.write(kv2)
        pm.convert(swapped, out, "binary")
    print(f"wrote {out}")


if __name__ == "__main__":
    args = sys.argv[1:]
    maps = os.path.join(ADDON, "maps")
    main(args[0] if args else os.path.join(maps, "probe01.vmap"),
         args[1] if len(args) > 1 else os.path.join(maps, "s2c_addonmat.vmap"))
