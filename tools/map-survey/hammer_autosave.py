"""hammer_autosave.py [--force-all] <map>...: Hammer's own save of .vmap files, from inside cubehost (Steam closed).

Each map (content/csgo_addons/<addon>/maps/<map>.vmap) is copied to s2c_rs_<map>.vmap, opened in Hammer with
open_asset, and hammer_force_autosave writes the open documents to content/_toolsautosave/hammer/...; the new
autosave files are copied to maps/s2c_rs_<map>_saved.vmap and their autosave files removed (only the ones this run
wrote). Hammer skips a document whose byte +0x11 is set even when forced (hammer_autosave.js); --force-all clears it
for the write (10-30: a decompile loads with it set unless Hammer changed something on load, so only pinst7 was
written on 10-30 afternoon). Nothing reaches the desktop: no key presses, the engine runs in our own process
(tools/cubehost, never cs2.exe), with a Frida agent reporting each document.

cs2.exe -tools cannot be used for this with Steam closed ("Failed to connect with local Steam Client process"), and
key presses to Hammer's window lose to whatever the user has in front.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
sys.path.insert(0, os.path.join(REPO, "tools", "pipeline"))
from cs2_console import Console  # noqa: E402


def titles(pid):
    """The process's visible top-level window titles (a modal dialog in Hammer holds the console commands)."""
    import ctypes
    from ctypes import wintypes
    user32 = ctypes.windll.user32
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def each(h, _l):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(h):
            buf = ctypes.create_unicode_buffer(512)
            user32.GetWindowTextW(h, buf, 512)
            found.append(buf.value)
        return True
    user32.EnumWindows(each, 0)
    return found


def running():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
    return [n for n in ("steam.exe", "steamwebhelper.exe", "cs2.exe", "cubehost.exe") if n in out]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("maps", nargs="+")
    p.add_argument("--addon", default="s2c_rc_probe")
    p.add_argument("--host", default=os.path.join(REPO, "tools", "cubehost", "out", "bin", "win64", "cubehost.exe"))
    p.add_argument("--force-all", action="store_true")
    p.add_argument("--port", type=int, default=29000)
    p.add_argument("--open-seconds", type=int, default=900, help="longest wait for a map to open")
    p.add_argument("--log", default=os.path.join(os.environ.get("TEMP", "."), "hammer_autosave.jsonl"))
    a = p.parse_args()
    if r := running():
        raise SystemExit(f"close {', '.join(r)} first")
    maps_dir = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps")
    autosave_dir = os.path.join(CS2, "content", "_toolsautosave", "hammer", "csgo_addons", a.addon, "maps")
    def tree():
        found = set()
        for root, dirs, files in os.walk(autosave_dir):
            found.update(os.path.relpath(os.path.join(root, n), autosave_dir) for n in dirs + files)
        return found
    before = tree()
    keep = os.path.join(os.environ.get("TEMP", "."), "hammer_autosave", time.strftime("%Y%m%d_%H%M%S"))
    copies = []
    for m in a.maps:
        src = os.path.join(maps_dir, m + ".vmap")
        if not os.path.exists(src):
            print("missing", src)
            continue
        shutil.copy2(src, os.path.join(maps_dir, f"s2c_rs_{m}.vmap"))
        copies.append(f"s2c_rs_{m}")

    import frida
    import psutil
    log = open(a.log, "w", encoding="utf-8")
    cmd = [a.host, "--", "-insecure", "-nosteam", "-nobreakpad", "-vulkan", "-novid", "-console",
           "-addon", a.addon, "-tools", "-netconport", str(a.port)]
    # Spawned suspended, so the agent is in before Hammer's startup (its recovery prompt comes first).
    device = frida.get_local_device()
    pid = device.spawn(cmd)
    host = psutil.Process(pid)
    try:
        session = device.attach(pid)
        script = session.create_script(open(os.path.join(HERE, "hammer_autosave.js"), encoding="utf-8").read())
        script.on("message", lambda msg, _d: (log.write(json.dumps(msg.get("payload", msg)) + "\n"), log.flush()))
        script.load()
        device.resume(pid)
        console = None
        deadline = time.time() + 300
        while console is None and time.time() < deadline and host.is_running():
            try:
                console = Console(port=a.port)
            except OSError:
                time.sleep(2)
        if console is None:
            raise SystemExit("no console")
        time.sleep(30)
        for c in copies:
            console.send(f"open_asset maps/{c}.vmap")
            # A large map loads for minutes (the ze decompile); console commands wait behind it, so wait
            # until Hammer's title names the map.
            lines = console.drain(10)
            until = time.time() + a.open_seconds
            while time.time() < until and not any(c.lower() in t.lower() for t in titles(pid)):
                lines += console.drain(10)
            log.write(json.dumps({"kind": "console", "after": f"open_asset {c}", "lines": lines}) + "\n")
            print("opened", c, f"({len(lines)} console lines), windows: {titles(pid)}")
        script.exports_sync.forceall(a.force_all)
        console.send("hammer_force_autosave")
        lines = console.drain(30)
        log.write(json.dumps({"kind": "console", "after": "hammer_force_autosave", "lines": lines}) + "\n")
        print("\n".join(lines[-15:]))
        # Every file the run wrote is kept under %TEMP%/hammer_autosave/<time> (nested prefab maps are saved
        # beside the map, under prefabs/<map>/, 10-30); the top-level maps also as maps/s2c_rs_<map>_saved.vmap.
        written = sorted(w for w in tree() - before if os.path.isfile(os.path.join(autosave_dir, w)))
        for w in written:
            os.makedirs(os.path.dirname(os.path.join(keep, w)), exist_ok=True)
            shutil.copy2(os.path.join(autosave_dir, w), os.path.join(keep, w))
            print("autosave", w, os.path.getsize(os.path.join(autosave_dir, w)), "bytes")
        print("kept under", keep)
        for w in written:
            for c in copies:
                if w.startswith(c) and w.endswith(".vmap_autosave"):
                    dst = os.path.join(maps_dir, f"{c}_saved.vmap")
                    shutil.copy2(os.path.join(autosave_dir, w), dst)
                    print("saved", c, os.path.getsize(dst), "bytes")
        # Quitting with documents open waits on a save prompt (10-30): the host is killed, and the autosaves this
        # run wrote are removed so the next start shows no recovery prompt.
        console.close()
    finally:
        if host.is_running():
            host.kill()
            host.wait(30)
        log.close()
        if os.path.isdir(autosave_dir):
            for w in sorted(tree() - before, key=len, reverse=True):
                path = os.path.join(autosave_dir, w)
                os.remove(path) if os.path.isfile(path) else os.rmdir(path)
    counts = {}
    for line in open(a.log, encoding="utf-8"):
        k = json.loads(line).get("kind")
        counts[k] = counts.get(k, 0) + 1
    print("agent:", counts, "log", a.log)


if __name__ == "__main__":
    main()
