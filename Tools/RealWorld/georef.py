"""georef.py - tie a circuit's game frame (local metres) to WGS84.

The game centreline (from TrackDump, exactly as the game builds it) is matched by ICP to the circuit's real
centreline (OpenStreetMap raceway via the bacinger/f1-circuits GeoJSON), projected to a local transverse
Mercator plane centred on the circuit. The fit is a 2D similarity transform (rotation + translation + scale, scale
reported; mirror tested). Result: game = s * R @ tm + t.
"""
import json
import os

import numpy as np
from pyproj import Transformer
from scipy.spatial import cKDTree

CACHE = os.environ.get("RW_CACHE", "/home/claude/rw-cache")
F1C_URL = "https://raw.githubusercontent.com/bacinger/f1-circuits/master/circuits/%s.geojson"


def load_outline(f1id):
    p = os.path.join(CACHE, "f1-circuits", f1id + ".geojson")
    if not os.path.exists(p):
        import requests
        os.makedirs(os.path.dirname(p), exist_ok=True)
        r = requests.get(F1C_URL % f1id, timeout=60)
        r.raise_for_status()
        open(p, "wb").write(r.content)
    d = json.load(open(p))
    g = d["features"][0]["geometry"]
    c = np.array(g["coordinates"] if g["type"] == "LineString" else g["coordinates"][0], float)
    return c[:, :2]


def densify(xy, step=0.5, closed=True):
    if closed and np.hypot(*(xy[0] - xy[-1])) > 1e-6:
        xy = np.vstack([xy, xy[:1]])
    out = []
    for a, b in zip(xy[:-1], xy[1:]):
        n = max(1, int(np.ceil(np.hypot(*(b - a)) / step)))
        u = np.arange(n)[:, None] / n
        out.append(a + (b - a) * u)
    out.append(xy[-1:])
    return np.vstack(out)


def umeyama(src, dst, scale=True):
    """dst ~ s R src + t (least squares). Returns s, R, t."""
    ms, md = src.mean(0), dst.mean(0)
    A, B = src - ms, dst - md
    C = B.T @ A / len(src)
    U, S, Vt = np.linalg.svd(C)
    D = np.eye(2)
    if np.linalg.det(U @ Vt) < 0:
        D[1, 1] = -1
    R = U @ D @ Vt
    s = (S * np.diag(D)).sum() / (A ** 2).sum() * len(src) if scale else 1.0
    t = md - s * R @ ms
    return s, R, t


class Warp:
    """Smooth local correction near the track (applied after the similarity transform): the residual between the
    game centreline and the fitted real centreline, smoothed along the lap (Gaussian, sigma_s m) and spread to the
    land around (inverse-distance weights), fading to zero from fade0 to fade1 m away from the track. It moves
    the surroundings by the few metres the game's own (smoothed, rescaled) centreline differs from the real road."""

    def __init__(self, q, D, fade0=40.0, fade1=300.0):
        self.q, self.D, self.f0, self.f1 = q, D, fade0, fade1
        self.tree = cKDTree(q)

    def disp(self, xy):
        xy = np.atleast_2d(xy)
        d, j = self.tree.query(xy, k=24, distance_upper_bound=self.f1 + 200)
        ok = np.isfinite(d)
        jj = np.where(ok, j, 0)
        w = np.where(ok, 1.0 / (d ** 2 + 900.0), 0.0)
        ws = w.sum(1)
        D = np.einsum("nk,nkc->nc", w, self.D[jj]) / np.maximum(ws, 1e-12)[:, None]
        dmin = np.where(ok[:, 0], d[:, 0], 1e9)
        u = np.clip((dmin - self.f0) / (self.f1 - self.f0), 0, 1)
        fade = 1 - u * u * (3 - 2 * u)
        return D * fade[:, None]


class Georef:
    def __init__(self, lon0, lat0, s, R, t, mirror=False):
        self.lon0, self.lat0, self.s, self.R, self.t, self.mirror = lon0, lat0, s, R, t, mirror
        self.warp = None
        self.fwd = Transformer.from_crs("EPSG:4326", self._tm(), always_xy=True)
        self.inv = Transformer.from_crs(self._tm(), "EPSG:4326", always_xy=True)

    def sim_to_game(self, xy):
        return xy + self.warp.disp(xy) if self.warp is not None else xy

    def game_to_sim(self, xy):
        if self.warp is None:
            return xy
        s = xy.copy()
        for _ in range(4):
            s = xy - self.warp.disp(s)
        return s

    def _tm(self):
        return "+proj=tmerc +lat_0=%.8f +lon_0=%.8f +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs" % (self.lat0, self.lon0)

    def tm_to_game(self, xy):
        xy = np.asarray(xy, float)
        if self.mirror:
            xy = xy * np.array([-1.0, 1.0])
        return self.s * xy @ self.R.T + self.t

    def game_to_tm(self, xy):
        xy = (np.asarray(xy, float) - self.t) @ self.R / self.s
        if self.mirror:
            xy = xy * np.array([-1.0, 1.0])
        return xy

    def ll_to_game(self, lon, lat):
        x, y = self.fwd.transform(np.asarray(lon, float), np.asarray(lat, float))
        return self.sim_to_game(self.tm_to_game(np.column_stack([x, y])))

    def game_to_ll(self, xy):
        tm = self.game_to_tm(self.game_to_sim(np.asarray(xy, float)))
        lon, lat = self.inv.transform(tm[:, 0], tm[:, 1])
        return np.column_stack([lon, lat])

    def to_dict(self):
        return dict(lon0=self.lon0, lat0=self.lat0, scale=self.s, rot_deg=float(np.degrees(np.arctan2(self.R[1, 0], self.R[0, 0]))),
                    tx=float(self.t[0]), ty=float(self.t[1]), mirror=self.mirror)


def fit(game_xy, outline_ll, allow_scale=True, trim=True, warp=True):
    """ICP of the game centreline onto the real outline. Returns (Georef, stats)."""
    lon0, lat0 = outline_ll[:, 0].mean(), outline_ll[:, 1].mean()
    g0 = Georef(lon0, lat0, 1.0, np.eye(2), np.zeros(2))
    x, y = g0.fwd.transform(outline_ll[:, 0], outline_ll[:, 1])
    Q = densify(np.column_stack([x, y]), 0.5)
    P = game_xy[::2]  # ~4-5 m spacing
    pc_, qc = P.mean(0), Q.mean(0)

    def icp(s, R, t, mirror, iters, tr):
        Qm = Q * np.array([-1.0, 1.0]) if mirror else Q
        for it in range(iters):
            Qg = s * Qm @ R.T + t
            tree = cKDTree(Qg)
            d, j = tree.query(P)
            if tr:
                lim = max(3.0 * np.median(d), 8.0)
                m = d < lim
            else:
                m = np.ones(len(d), bool)
            s, R, t = umeyama(Qm[j[m]], P[m], scale=allow_scale and it > 3)
        Qg = s * Qm @ R.T + t
        d, _ = cKDTree(Qg).query(P)
        return s, R, t, d

    best = None
    for mirror in (False, True):
        Qm = Q * np.array([-1.0, 1.0]) if mirror else Q
        qcm = Qm.mean(0)
        for ang in np.arange(0, 360, 4.0):
            a = np.radians(ang)
            R = np.array([[np.cos(a), -np.sin(a)], [np.sin(a), np.cos(a)]])
            t = pc_ - R @ qcm
            s, R, t, d = icp(1.0, R, t, mirror, 12, False)
            score = np.median(d)
            if best is None or score < best[0]:
                best = (score, s, R, t, mirror)
    _, s, R, t, mirror = best
    s, R, t, d = icp(s, R, t, mirror, 60, trim)
    geo = Georef(lon0, lat0, s, R, t, mirror)
    # final statistics on the full-resolution game centreline vs the dense outline
    Qg = geo.tm_to_game(Q)
    dd, _ = cKDTree(Qg).query(game_xy)
    stats = dict(rms=float(np.sqrt((dd ** 2).mean())), median=float(np.median(dd)), p95=float(np.percentile(dd, 95)),
                 max=float(dd.max()), scale=float(s), mirror=bool(mirror),
                 rot_deg=float(np.degrees(np.arctan2(R[1, 0], R[0, 0]))))
    if warp:
        # residual of every game point to the fitted outline, smoothed along the lap (sigma 30 m)
        tree = cKDTree(Qg)
        _, j = tree.query(game_xy)
        D = game_xy - Qg[j]
        n = len(D)
        seg = np.hypot(*np.diff(np.vstack([game_xy, game_xy[:1]]), axis=0).T).mean()
        sg = 30.0 / seg
        w = int(np.ceil(3 * sg))
        k = np.exp(-0.5 * (np.arange(-w, w + 1) / sg) ** 2)
        k /= k.sum()
        Ds = np.column_stack([np.convolve(np.r_[D[-w:, c], D[:, c], D[:w, c]], k, "valid") for c in (0, 1)])
        q = game_xy - Ds  # where those points lie before the warp
        geo.warp = Warp(q, Ds)
        Qw = geo.sim_to_game(Qg)
        dw, _ = cKDTree(Qw).query(game_xy)
        stats.update(warp_rms=float(np.sqrt((dw ** 2).mean())), warp_max_shift=float(np.hypot(*Ds.T).max()))
        Qg = Qw
    return geo, stats, Qg
