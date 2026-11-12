"""compile_model_ref.py <addon> <model.vmdl>... [--runs N] [--out dir]: Valve's .vmdl_c references.

Compiles each content-relative .vmdl of <addon> with the installed resourcecompiler and keeps
the outputs (the .vmdl_c, and any child resources it wrote that are newer than the run start)
under --out (default %TEMP%/model_refs/<addon>/run<i>/...), with RC's log beside them. With
--runs 2 or more the compile repeats from scratch (outputs deleted first) so Valve's own
run-to-run variance can be measured with cmp.

Standalone resourcecompiler touches no game process, so it may run with CS2 open (user
2026-11-02); it always runs at below-normal priority. Queue it through tools/re/heavy.py.
"""
import argparse
import ctypes
import os
import shutil
import subprocess
import sys
import time

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("models", nargs="+")
    p.add_argument("--runs", type=int, default=1)
    p.add_argument("--out")
    p.add_argument("--shallow", action="store_true", help="-fshallow: do not compile child resources")
    a = p.parse_args()
    # @file: one content-relative .vmdl per line (model_probe_corpus.py --list).
    a.models = [m for arg in a.models for m in (open(arg[1:]).read().split() if arg.startswith("@") else [arg])]
    ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)
    content = os.path.join(CS2, "content", "csgo_addons", a.addon)
    game = os.path.join(CS2, "game", "csgo_addons", a.addon)
    out = a.out or os.path.join(os.environ.get("TEMP", "."), "model_refs", a.addon)
    for run in range(a.runs):
        dest = os.path.join(out, f"run{run + 1}")
        shutil.rmtree(dest, ignore_errors=True)
        os.makedirs(dest)
        for m in a.models:
            built = os.path.join(game, m + "_c")
            if os.path.exists(built):
                os.remove(built)
        started = time.time() - 2
        cmd = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-f", "-game", os.path.join(CS2, "game", "csgo")]
        if a.shallow:
            cmd.append("-fshallow")
        for m in a.models:
            cmd += ["-i", os.path.join(content, m)]
        with open(os.path.join(dest, "rc.log"), "wb") as log:
            code = subprocess.run(cmd, cwd=BIN, stdout=log, stderr=subprocess.STDOUT).returncode
        print(f"run {run + 1}: rc exit {code}")
        for root, _, files in os.walk(game):
            for f in files:
                src = os.path.join(root, f)
                if os.path.getmtime(src) >= started:
                    rel = os.path.relpath(src, game)
                    os.makedirs(os.path.dirname(os.path.join(dest, rel)), exist_ok=True)
                    shutil.copy2(src, os.path.join(dest, rel))
                    print("  kept", rel)
    return 0


if __name__ == "__main__":
    sys.exit(main())
