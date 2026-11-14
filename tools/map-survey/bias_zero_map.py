"""bias_zero_map.py <in.vmap> <out.vmap> [node id, default 409]: the map with one mesh's faces all at lightmapScaleBias 0.

ze_hold_em_p's node 409 (faces at bias 0 and 1) charts capped in a lit compile: every one of its triangles reads the
same lightmap density (10-30), so the lightmap does not show its biases. Compiled lit beside the unchanged source,
this shows what the bias still changes there. Only the bias stream changes, but the round trip itself upgrades
the file (vmap 37 -> 40): compare with the control made by node "none", not the source. Ids stay, so compile both
under other map names of one length."""
import os, sys, tempfile

sys.path.insert(0, os.path.dirname(__file__))
import bias_probe_map as bp  # noqa: E402


def main(source, out, node="409"):
    header, tops = bp.load(source)
    # node "none": the round trip alone (dmxconvert through keyvalues2, format 37 -> 40), the control.
    before = []
    if node != "none":
        mesh = next(e for e in bp.everything(tops) if e.type == "CMapMesh" and e.get("nodeID") == node)
        s = bp.stream(mesh, "faceData", "lightmapScaleBias")
        before = s.get("data")
        s.set("data", "int_array", ["0"] * len(before))
    work = tempfile.mkdtemp(prefix="bias_zero_")
    text_out = os.path.join(work, "out_text.vmap")
    open(text_out, "w", encoding="utf-8").write(bp.pm.emit(tops, header))
    bp.pm.convert(text_out, out, "binary")
    print("wrote", out, f"node {node}: {before.count('0')} faces at 0, {before.count('1')} at 1 -> all {len(before)} at 0")


if __name__ == "__main__":
    main(*sys.argv[1:4])
