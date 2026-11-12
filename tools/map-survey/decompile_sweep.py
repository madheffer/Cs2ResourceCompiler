"""Drive `s2c decompile-sweep` over the workshop folder until every map has a report line.

The sweep exits with 3 after a map times out (a hung thread cannot be stopped); a hard crash
(stack overflow, out of memory) leaves the map's key in <report>.current. Either way this
script records what is needed and starts the sweep again, which resumes from the report.

  python tools/map-survey/decompile_sweep.py --s2c <s2c.exe> --report <out.jsonl>
      [--workshop D:/Steam/steamapps/workshop/content/730] [--cs2 <CS2 dir>] [--timeout 900]
      [--only id,id]
  python tools/map-survey/decompile_sweep.py --summary <out.jsonl>

Run it through the heavy queue at low priority (tools/re/heavy.py); docs/DECOMPILE.md item 19.
"""
import argparse
import collections
import json
import os
import subprocess
import sys


def summary(report):
    rows = [json.loads(l) for l in open(report, encoding="utf-8") if l.strip()]
    status = collections.Counter(r["status"] for r in rows)
    print(f"{len(rows)} rows: " + ", ".join(f"{k} {v}" for k, v in status.most_common()))
    import datetime
    fmt = collections.Counter((r.get("lumpFormat", "-"), r["status"]) for r in rows if "entry" in r)
    print("lump format x status: " + ", ".join(f"{f}/{s} {n}" for (f, s), n in fmt.most_common()))
    for f in sorted({r.get("lumpFormat") for r in rows if r.get("compileTimestamp")}):
        ts = sorted(r["compileTimestamp"] for r in rows if r.get("lumpFormat") == f and r.get("compileTimestamp"))
        d = lambda t: datetime.datetime.fromtimestamp(t, datetime.timezone.utc).date()
        print(f"  {f}: compiled {d(ts[0])} .. {d(ts[-1])} ({len(ts)} maps)")
    ok = [r for r in rows if r["status"] == "ok"]
    if ok:
        secs = sorted(r["seconds"] for r in ok)
        print(f"ok: {sum(r['entities'] for r in ok):,} entities, {sum(r['placeholders'] for r in ok):,} placeholders, "
              f"median {secs[len(secs) // 2]} s, max {secs[-1]} s, total {sum(secs) / 60:.0f} min")
    errors = collections.Counter()
    examples = {}
    for r in rows:
        if r["status"] in ("error", "crash", "timeout", "badpackage"):
            frame = (r.get("at") or [""])[0]
            k = (r["status"], (r.get("error") or "").split(":")[0], frame[:120])
            errors[k] += 1
            examples.setdefault(k, r)
    if errors:
        print("\nfailures (status, exception, first frame): count, example")
        for k, n in errors.most_common():
            e = examples[k]
            print(f"  {n:4} {k[0]} {k[1]} {k[2]}\n       e.g. {e['key']}: {(e.get('error') or '')[:200]}")
    notes = collections.Counter()
    maps_with = collections.Counter()
    for r in ok:
        for g in r.get("noteGroups", []):
            notes[g["kind"]] += g["count"]
            maps_with[g["kind"]] += 1
    if notes:
        print("\nnotes (kind): total, maps")
        for k, n in notes.most_common(40):
            print(f"  {n:7,} {maps_with[k]:4}  {k[:150]}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--s2c")
    ap.add_argument("--report")
    ap.add_argument("--workshop", default="D:/Steam/steamapps/workshop/content/730")
    ap.add_argument("--cs2", default=os.environ.get("CS2_DIR", "D:/Steam/steamapps/common/Counter-Strike Global Offensive"))
    ap.add_argument("--timeout", default="900")
    ap.add_argument("--only")
    ap.add_argument("--summary")
    a = ap.parse_args()
    if a.summary:
        summary(a.summary)
        return 0
    current = a.report + ".current"
    for attempt in range(1000):
        cmd = [a.s2c, "decompile-sweep", a.workshop, "--report", a.report, "--cs2", a.cs2, "--timeout", a.timeout]
        if a.only:
            cmd += ["--only", a.only]
        code = subprocess.call(cmd)
        if code == 0:
            break
        if code == 3:
            continue  # the timeout row is written; resume
        # A hard crash: the map it was on is in .current.
        if os.path.exists(current):
            key = open(current, encoding="utf-8").read().strip()
            item, _, entry = key.partition("/")
            with open(a.report, "a", encoding="utf-8") as f:
                f.write(json.dumps({"key": key, "item": item, "entry": entry, "status": "crash", "error": f"exit code {code}"}) + "\n")
            os.remove(current)
            continue
        print(f"sweep exited {code} outside a map; stopping", file=sys.stderr)
        return code
    summary(a.report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
