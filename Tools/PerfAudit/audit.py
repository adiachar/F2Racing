#!/usr/bin/env python3
"""Tools/PerfAudit/audit.py - render statistics of the real game, per quality tier and lap position, headless.

Drives Tools/Shots/shoot.py (WebGL build, headless Chrome) with the shot-mode parameter stats=1: the game then shows
its debug overlay (PerfStats.cs) and logs one "PERF_STATS s=<m> cam=<name> key=value ..." console line per shot.
This script collects those lines for every tier and prints one markdown table (and writes perf_audit.json).

  python3 Tools/PerfAudit/audit.py --out /tmp/perf                      # Monza, s = 0,1450,2500,5150, Low/Medium/High
  python3 Tools/PerfAudit/audit.py --track Spa --s 0,2000 --cam chase,cockpit --build ~/Downloads/F2Racing-WebGL.zip

Columns: world renderers drawn / triangles (after the sector sets) and "before" (frustum only, what Unity alone draws);
card overdraw in screens (alpha-tested foliage and fences); distinct materials and shader variants; memory of all
textures (runtime-generated part in brackets) and meshes; the engine's draw / SetPass counters when the player has them.
Note: SwiftShader fps are meaningless; the counts are what matters. Counts follow the window size (w, h).
"""
import argparse, json, os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SHOOT = os.path.join(HERE, "..", "Shots", "shoot.py")
LINE = re.compile(r"PERF_STATS (s=\S+) (cam=\S+) (.*)")


def parse(text):
    rows = []
    for m in LINE.finditer(text):
        kv = {"s": m.group(1)[2:], "cam": m.group(2)[4:]}
        # "key=value" pairs; groups are separated by two spaces and keep their first word as a prefix
        for group in m.group(3).split("  "):
            words = group.split()
            if not words:
                continue
            prefix = words[0] if "=" not in words[0] else ""
            for w in words:
                if "=" in w:
                    k, v = w.split("=", 1)
                    kv[(prefix + "." if prefix else "") + k] = v
        rows.append(kv)
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--track", default="Monza")
    ap.add_argument("--s", default="0,1450,2500,5150")
    ap.add_argument("--cam", default="chase")
    ap.add_argument("--quality", default="Low,Medium,High")
    ap.add_argument("--cars", default="6")
    ap.add_argument("--build", default=None)
    ap.add_argument("--out", default="/tmp/perf_audit")
    ap.add_argument("--parse", default=None, help="only parse an existing console.log")
    a = ap.parse_args()
    if a.parse:
        rows = parse(open(a.parse, encoding="utf-8", errors="replace").read())
        for r in rows:
            r["tier"] = "?"
    else:
        rows = []
        for q in a.quality.split(","):
            out = os.path.join(a.out, q)
            cmd = [sys.executable, "-I", SHOOT, "--out", out, "--quality", q, "--s", a.s, "--cam", a.cam,
                   "--param", "stats=1", "--cars", a.cars, "--track", a.track]
            if a.build:
                cmd += ["--build", a.build]
            print("$", " ".join(cmd), flush=True)
            subprocess.run(cmd, check=False)
            log = os.path.join(out, "console.log")
            if os.path.exists(log):
                for r in parse(open(log, encoding="utf-8", errors="replace").read()):
                    r["tier"] = q
                    rows.append(r)
    os.makedirs(a.out, exist_ok=True)
    json.dump(rows, open(os.path.join(a.out, "perf_audit.json"), "w"), indent=1)
    cols = [("tier", "tier"), ("s", "s"), ("cam", "cam"), ("world.drawn", "world drawn"), ("world.tris", "world tris"),
            ("before.drawn", "before"), ("before.tris", "before tris"), ("other.drawn", "other"), ("other.tris", "other tris"),
            ("cards.now", "cards"), ("cards.before", "cards before"), ("mats", "mats"), ("variants", "variants"),
            ("hidden", "hidden"), ("tex", "tex"), ("mesh", "mesh"), ("gpu.draws", "draws"), ("gpu.setpass", "setpass")]
    print("| " + " | ".join(c[1] for c in cols) + " |")
    print("|" + "---|" * len(cols))
    for r in rows:
        print("| " + " | ".join(str(r.get(k, "")) for k, _ in cols) + " |")
    if not rows:
        print("no PERF_STATS lines found (build without PerfStats.cs, or the run failed)", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
