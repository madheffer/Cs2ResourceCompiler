"""model_probe_corpus.py <source addon>... --classes A,B,C [--into s2c_model_probe] [--list out.txt]: copy a model corpus.

Picks every .vmdl of the source addons (content/csgo_addons/<addon>) whose set of ModelDoc node
classes is exactly --classes (comma separated, e.g. RootNode,RenderMeshList,RenderMeshFile), and
copies into the probe addon (default s2c_model_probe) what a compile of it reads: the .vmdl, the
mesh files its filename keys name, the materials those DMX files name (mtlName), and the images
the materials name. Files already there are left alone. Writes the content-relative .vmdl list
(one per line) to --list, for compile_model_ref.py and the parity test.
"""
import argparse
import os
import re
import shutil
import sys

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
ADDONS = os.path.join(CS2, "content", "csgo_addons")


def classes(text):
    return set(re.findall(r'_class = "([^"]*)"', text))


def dmx_materials(path):
    data = open(path, "rb").read()
    return {m.decode("latin-1").replace("\\", "/") for m in re.findall(rb"materials[\\/][\x20-\x7e]+?\.vmat", data)}


def vmat_images(path):
    text = open(path, encoding="latin-1").read()
    return {m.replace("\\", "/") for m in re.findall(r'"((?:materials|models)/[^"]+\.(?:tga|psd|png|jpg|exr|pfm))"', text, re.I)}


def copy(src_root, dst_root, rel):
    src = os.path.join(src_root, rel)
    dst = os.path.join(dst_root, rel)
    if not os.path.exists(src):
        return False
    if not os.path.exists(dst):
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
    return True


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addons", nargs="+")
    p.add_argument("--classes", required=True)
    p.add_argument("--into", default="s2c_model_probe")
    p.add_argument("--list")
    p.add_argument("--every", type=int, default=1, help="take every n-th match in path order (a spread sample)")
    a = p.parse_args()
    want = set(a.classes.split(","))
    dst = os.path.join(ADDONS, a.into)
    picked = []
    seen = 0
    for addon in a.addons:
        root = os.path.join(ADDONS, addon)
        paths = sorted(os.path.join(d, f) for d, _, files in os.walk(root) for f in files if f.endswith(".vmdl"))
        for path in paths:
            text = open(path, encoding="utf-8", errors="replace").read()
            if classes(text) != want:
                continue
            seen += 1
            if (seen - 1) % a.every:
                continue
            rel = os.path.relpath(path, root).replace("\\", "/")
            meshes = [m.replace("\\", "/").lstrip("/") for m in re.findall(r'filename = "([^"]+)"', text)]
            if not all(os.path.exists(os.path.join(root, m)) for m in meshes):
                print("skip (mesh missing):", addon, rel)
                continue
            copy(root, dst, rel)
            for m in meshes:
                copy(root, dst, m)
                if m.lower().endswith(".dmx"):
                    for mat in dmx_materials(os.path.join(root, m)):
                        if copy(root, dst, mat):
                            for img in vmat_images(os.path.join(root, mat)):
                                copy(root, dst, img)
            picked.append(rel)
    print(len(picked), "models")
    if a.list:
        with open(a.list, "w") as h:
            h.write("\n".join(sorted(set(picked))) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
