#!/usr/bin/env python3
"""check_world.py - independent reader / validator of Assets/Resources/World/<Name>.bytes (F2W1 v1).

Usage: python3 -I Tools/RealWorld/check_world.py [file.bytes ...]   (default: every file in Assets/Resources/World)
Checks: magic, version, section framing, counts, rings (>= 3 points, counter-clockwise, no repeated last point),
finite values, kinds in range, everything inside the terrain grid, and terrain height vs the game track height.
"""
import glob
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))


def read(path):
    b = open(path, "rb").read()
    assert b[:4] == b"F2W1", "bad magic"
    ver, n = struct.unpack_from("<ii", b, 4)
    assert ver == 1
    off, secs = 12, {}
    for _ in range(n):
        tag = b[off:off + 4].decode()
        ln = struct.unpack_from("<i", b, off + 4)[0]
        secs[tag] = b[off + 8:off + 8 + ln]
        off += 8 + ln
    assert off == len(b), "trailing bytes"
    out = {}
    s = secs["TERR"]
    nx, nz, x0, z0, cell = struct.unpack_from("<iifff", s, 0)
    h = np.frombuffer(s, "<f4", nx * nz, 20).reshape(nz, nx)
    assert len(s) == 20 + 4 * nx * nz
    out["TERR"] = dict(nx=nx, nz=nz, x0=x0, z0=z0, cell=cell, h=h)

    def ring(s, o):
        n = struct.unpack_from("<H", s, o)[0]
        pts = np.frombuffer(s, "<f4", 2 * n, o + 2).reshape(n, 2)
        return pts, o + 2 + 8 * n

    s = secs["BLDG"]
    n = struct.unpack_from("<i", s, 0)[0]
    o, bl = 4, []
    for _ in range(n):
        hh, mh, roof, kind, r, g, bb, a = struct.unpack_from("<ffBB4B", s, o)
        pts, o = ring(s, o + 14)
        bl.append((hh, mh, roof, kind, (r, g, bb, a), pts))
    assert o == len(s)
    out["BLDG"] = bl
    s = secs["AREA"]
    n = struct.unpack_from("<i", s, 0)[0]
    o, ar = 4, []
    for _ in range(n):
        kind = s[o]
        pts, o = ring(s, o + 1)
        ar.append((kind, pts))
    assert o == len(s)
    out["AREA"] = ar
    s = secs["LINE"]
    n = struct.unpack_from("<i", s, 0)[0]
    o, li = 4, []
    for _ in range(n):
        kind = s[o]
        w = struct.unpack_from("<f", s, o + 1)[0]
        pts, o = ring(s, o + 5)
        li.append((kind, w, pts))
    assert o == len(s)
    out["LINE"] = li
    s = secs["TREE"]
    n = struct.unpack_from("<i", s, 0)[0]
    out["TREE"] = np.frombuffer(s, "<f4", 3 * n, 4).reshape(n, 3)
    assert len(s) == 4 + 12 * n
    if "META" in secs:
        out["META"] = dict(l.split("=", 1) for l in secs["META"].decode().splitlines() if "=" in l)
    return out, os.path.getsize(path)


def sarea(p):
    return 0.5 * (np.dot(p[:, 0], np.roll(p[:, 1], -1)) - np.dot(p[:, 1], np.roll(p[:, 0], -1)))


def check(path):
    w, size = read(path)
    t = w["TERR"]
    x1 = t["x0"] + (t["nx"] - 1) * t["cell"]
    z1 = t["z0"] + (t["nz"] - 1) * t["cell"]
    inside = lambda p: (p[:, 0] >= t["x0"] - 1).all() and (p[:, 0] <= x1 + 1).all() and (p[:, 1] >= t["z0"] - 1).all() and (p[:, 1] <= z1 + 1).all()
    errs = []
    assert np.isfinite(t["h"]).all()
    for i, (hh, mh, roof, kind, col, p) in enumerate(w["BLDG"]):
        if len(p) < 3 or sarea(p) <= 0 or (p[0] == p[-1]).all() or not np.isfinite(p).all() or roof > 5 or kind > 8 or not (hh > mh >= 0) or not inside(p):
            errs.append(("BLDG", i))
    for i, (kind, p) in enumerate(w["AREA"]):
        if len(p) < 3 or sarea(p) <= 0 or (p[0] == p[-1]).all() or kind > 11 or not inside(p):
            errs.append(("AREA", i))
    for i, (kind, wd, p) in enumerate(w["LINE"]):
        if len(p) < 2 or kind > 5 or not (wd > 0) or not inside(p):
            errs.append(("LINE", i))
    water_after = [k for k, _ in w["AREA"]]
    wi = [i for i, k in enumerate(water_after) if k == 5]
    order_ok = not wi or all(k == 5 for k in water_after[wi[0]:])
    name = os.path.basename(path)[:-6]
    msg = "%-12s %5.2f MB  TERR %dx%d@%gm  BLDG %6d  AREA %5d  LINE %5d  TREE %6d  water-last %s  errors %d" % (
        name, size / 1e6, t["nx"], t["nz"], t["cell"], len(w["BLDG"]), len(w["AREA"]), len(w["LINE"]), len(w["TREE"]), order_ok, len(errs))
    # terrain under the game centreline
    tid = (w.get("META") or {}).get("game_id")
    tp = os.path.join(os.environ.get("RW_CACHE", "/home/claude/rw-cache"), "tracks", "%s.csv" % tid)
    if tid and os.path.exists(tp):
        d = np.loadtxt(tp, delimiter=",", skiprows=2)
        fx = (d[:, 0] - t["x0"]) / t["cell"]
        fz = (d[:, 1] - t["z0"]) / t["cell"]
        i, k = fx.astype(int), fz.astype(int)
        a, b = fx - i, fz - k
        H = t["h"]
        th = (H[k, i] * (1 - a) + H[k, i + 1] * a) * (1 - b) + (H[k + 1, i] * (1 - a) + H[k + 1, i + 1] * a) * b
        e = th - d[:, 2]
        msg += "  terrain-vs-track RMS %.2f max %.2f m" % (np.sqrt((e ** 2).mean()), np.abs(e).max())
    print(msg)
    return not errs


if __name__ == "__main__":
    files = sys.argv[1:] or sorted(glob.glob(os.path.join(REPO, "Assets", "Resources", "World", "*.bytes")))
    ok = all([check(f) for f in files])
    sys.exit(0 if ok else 1)
