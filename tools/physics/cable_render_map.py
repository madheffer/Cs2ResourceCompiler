"""cable_render_map.py <base.vmap> <out.vmap>: a probe map for what a cable keeps only in its render model.
cable_probe_map.py's layout (each variant a copy of the base map's first CMapCable, 96 units further along x, one
prop_physics) over the render settings instead: material, secondary material, tint, texture scale / orientation /
offsets, and the shape settings with collision off. The map decompiler rebuilds CMapCable from the lump's pathNodes and
the cable's models (docs/DECOMPILE.md, Handoff: the cable probe)."""
import os, sys

sys.path.insert(0, os.path.dirname(__file__))
import cable_probe_map as cp  # noqa: E402

OFF = {"collisionEnabled": ("bool", "0")}
cp.VARIANTS = [
    dict(OFF),
    {**OFF, "materialName": ("string", "materials/cable/csgo_cable_tintable.vmat"), "tintColor": ("color", "200 40 90 255")},
    {**OFF, "materialName": ("string", "materials/cable/phonecable.vmat")},
    {**OFF, "materialName": ("string", "materials/cable/csgo_placeholder_cable.vmat"),
     "secondary_material": ("string", "materials/cable/rope_shadowdepth.vmat")},
    {**OFF, "textureScale": ("float", "1.5"), "textureRepeatsCircumference": ("float", "3")},
    {**OFF, "textureOrientation": ("int", "1"), "textureOffsetAlongPath": ("float", "0.3"), "textureOffsetCircumference": ("float", "0.6")},
    {**OFF, "radius": ("float", "3"), "numSides": ("int", "5"), "tessellationSpacing": ("float", "20")},
    {**OFF, "interpolationType": ("int", "2"), "closedLoop": ("bool", "1")},
    {**OFF, "flipFaces": ("bool", "1"), "classname": ("string", "cable_static")},
    {**OFF, "angles": ("qangle", "10 35 -20"), "radius": ("float", "6"), "numSides": ("int", "7")},
    {"materialName": ("string", "materials/cable/csgo_cable_tintable.vmat"), "tintColor": ("color", "10 250 120 255"),
     "textureScale": ("float", "2")},
]
_set = cp.pm.Element.set


def _set_prop(self, key, kind, value):
    # secondary_material is an entity property, not a CMapCable field (classname is handled by main)
    if key == "secondary_material" and self.type == "CMapCable":
        return _set(self.get("entity_properties"), key, kind, value)
    return _set(self, key, kind, value)


cp.pm.Element.set = _set_prop

if __name__ == "__main__":
    cp.main(sys.argv[1], sys.argv[2])
