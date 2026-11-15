#!/usr/bin/env python3
"""build_world.py - real surroundings of each circuit -> Assets/Resources/World/<Name>.bytes (format F2W1 v1).

Usage:  python3 -I Tools/RealWorld/build_world.py [Name ...]      (default: all circuits, Monza first)
        add --preview to also write Tools/RealWorld/preview_<Name>.png

Pipeline per circuit (see README.md):
  1. game centreline exactly as the game builds it (TrackDump, C#, compiles Assets/Scripts/Physics)
  2. georeference: similarity ICP of the game centreline onto the real OSM raceway (f1-circuits GeoJSON)
  3. Overture Maps (buildings, base: land_use / land_cover / land / water / infrastructure, transportation) by
     HTTP range reads of only the row groups whose bbox statistics intersect the circuit's box
  4. Terrarium DEM -> terrain grid in the game's vertical frame (matched to the track heights)
  5. everything transformed to Unity world X/Z (= track x/y), filtered, simplified, written as F2W1 binary
"""
import json
import os
import re
import struct
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import numpy as np  # noqa: E402
import pyarrow.compute as pc  # noqa: E402
import shapely  # noqa: E402
from scipy.spatial import cKDTree  # noqa: E402

import georef  # noqa: E402
import overture as ov  # noqa: E402
import terrain  # noqa: E402

REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
CACHE = ov.CACHE
OUT_DIR = os.path.join(REPO, "Assets", "Resources", "World")
TRACKS = os.path.join(CACHE, "tracks")

# Name: game circuit id, f1-circuits id, terrain cell (m), extra
CIRCUITS = {
    "Monza": dict(id="monza", f1="it-1922", cell=20),
    "Spa": dict(id="spa", f1="be-1925", cell=20),
    "Silverstone": dict(id="silverstone", f1="gb-1948", cell=20),
    "Suzuka": dict(id="suzuka", f1="jp-1962", cell=20),
    "Austin": dict(id="austin", f1="us-2012", cell=20),
    "Budapest": dict(id="budapest", f1="hu-1986", cell=20),
    "Catalunya": dict(id="catalunya", f1="es-1991", cell=20),
    "Spielberg": dict(id="spielberg", f1="at-1969", cell=20),
    "Zandvoort": dict(id="zandvoort", f1="nl-1948", cell=20),
    "Monaco": dict(id="monaco", f1="mc-1929", cell=10, tunnel=(1560, 1880), tree_h=8.0, max_mb=5.0),
}

MARGIN = 2000.0        # m around the track bbox: terrain grid, and the cut-off for everything else
MAX_DIST = 2000.0      # m from the track centreline
LEVEL_H = 3.2          # m per storey

# ------------------------------------------------------------------ helpers


def log(*a):
    print(*a, flush=True)


def load_track(gid):
    p = os.path.join(TRACKS, gid + ".csv")
    if not os.path.exists(p):
        log("  running TrackDump (C#) ...")
        subprocess.run(["dotnet", "run", "-c", "Release", "--project", os.path.join(HERE, "TrackDump"), "--", TRACKS], check=True)
    head = open(p).readline()
    meta = dict(kv.split("=") for kv in head[1:].split())
    d = np.loadtxt(p, delimiter=",", skiprows=2)
    return d, {k: (float(v) if k != "id" else v) for k, v in meta.items()}


def signed_area(c):
    x, y = c[:, 0], c[:, 1]
    return 0.5 * (np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))


def ring_ccw(poly, tol):
    """Exterior ring of a polygon, simplified, counter-clockwise (x right, z up), no repeated last point."""
    if tol > 0:
        poly = shapely.simplify(poly, tol, preserve_topology=True)
    if poly is None or poly.is_empty or poly.geom_type != "Polygon":
        return None
    c = np.asarray(poly.exterior.coords)[:-1, :2]
    if len(c) < 3:
        return None
    if signed_area(c) < 0:
        c = c[::-1]
    return c[:65535]


def parse_color(s):
    if s is None or (isinstance(s, float) and np.isnan(s)):
        return (0, 0, 0, 0)
    try:
        import matplotlib.colors as mc
        s = s.strip()
        if re.fullmatch(r"[0-9a-fA-F]{6}", s):
            s = "#" + s
        r, g, b, _ = mc.to_rgba(s.lower().replace(" ", ""))
        return (int(r * 255 + .5), int(g * 255 + .5), int(b * 255 + .5), 255)
    except Exception:
        return (0, 0, 0, 0)


def geoms_to_game(wkb, geo):
    g = shapely.from_wkb(wkb)
    return shapely.transform(g, lambda xy: geo.ll_to_game(xy[:, 0], xy[:, 1]))


def flag_whole(flags, name):
    """True if a road/rail flag list sets `name` over the whole segment (or over most of it)."""
    if flags is None:
        return False
    for f in flags:
        vals = f.get("values")
        if vals is not None and name in list(vals):
            b = f.get("between")
            if b is None or len(b) < 2 or (b[1] - b[0]) > 0.5:
                return True
    return False


def level_below(rules):
    if rules is None:
        return False
    for r in rules:
        if (r.get("value") or 0) < 0:
            b = r.get("between")
            if b is None or len(b) < 2 or (b[1] - b[0]) > 0.5:
                return True
    return False


def tag(src, key):
    if src is None:
        return None
    if isinstance(src, dict):
        return src.get(key)
    for k, v in src:
        if k == key:
            return v
    return None


def to_float(s):
    try:
        m = re.match(r"\s*([0-9]+(?:[.,][0-9]+)?)", str(s))
        return float(m.group(1).replace(",", ".")) if m else None
    except Exception:
        return None

# ------------------------------------------------------------------ classification

ROOF = {"flat": 0, "gabled": 1, "hipped": 2, "half_hipped": 2, "side_hipped": 2, "dome": 3, "onion": 3, "pyramidal": 4}

KIND_CLASS = {
    4: {"grandstand", "stadium", "sports_hall", "sports_centre", "pavilion", "riding_hall", "bleachers"},
    8: {"hotel"},
    7: {"garage", "garages", "shed", "carport", "hut", "cabin", "roof", "kiosk", "toilets", "shelter", "bunker",
        "storage_tank", "allotment_house", "boathouse", "outbuilding", "container", "transformer_tower", "service"},
    6: {"church", "chapel", "cathedral", "mosque", "temple", "synagogue", "shrine", "monastery", "religious", "presbytery", "wayside_shrine"},
    5: {"civic", "public", "school", "university", "college", "kindergarten", "hospital", "government", "train_station",
        "transportation", "fire_station", "museum", "library", "townhall", "police", "military", "post_office", "clinic", "castle", "palace"},
    2: {"commercial", "office", "retail", "supermarket", "kiosk_shop", "bank", "restaurant", "mall", "shop"},
    3: {"industrial", "warehouse", "factory", "manufacture", "barn", "farm_auxiliary", "greenhouse", "stable", "cowshed",
        "silo", "hangar", "digester", "agricultural", "sty", "slurry_tank"},
    1: {"residential", "house", "detached", "semidetached_house", "semi_detached", "terrace", "bungalow", "dormitory",
        "farm", "static_caravan", "apartments", "villa"},
}
KIND_SUBTYPE = {"residential": 1, "commercial": 2, "industrial": 3, "agricultural": 3, "civic": 5, "education": 5,
                "medical": 5, "transportation": 5, "military": 5, "religious": 6, "entertainment": 4, "outbuilding": 7, "service": 7}
KIND_HEIGHT = {0: 6.0, 1: 7.0, 2: 10.0, 3: 8.0, 4: 12.0, 5: 11.0, 6: 14.0, 7: 3.0, 8: 25.0}


def _height_source(sources):
    """'osm' / 'ml' / None: which dataset gave the building its height."""
    if sources is None:
        return None
    ds = [x.get("dataset") for x in sources if "height" in (x.get("property") or "")]
    if not ds:
        ds = [x.get("dataset") for x in sources if (x.get("property") or "") == ""]
    if any(d and "ML" in d for d in ds):
        return "ml"
    return "osm" if ds else None


def building_kind(sub, cls, h):
    k = None
    if isinstance(cls, str):
        for kind, s in KIND_CLASS.items():
            if cls in s:
                k = kind
                break
    if k is None and isinstance(sub, str):
        k = KIND_SUBTYPE.get(sub)
    if k is None:
        k = 0
    if k == 1 and h is not None and h >= 22:
        k = 8  # apartment tower
    return k


# AREA kinds: 0 grass,1 forest,2 farmland,3 residential,4 commercial/industrial,5 water,6 sand,7 paved,8 park,9 rock,
#             10 wetland, 11 motorsport/run-off paved
LANDUSE_SUB = {"agriculture": 2, "aquaculture": 5, "campground": 0, "cemetery": 8, "construction": 9, "developed": 4,
               "education": 3, "entertainment": 4, "golf": 0, "grass": 0, "horticulture": 8, "landfill": 9, "managed": 0,
               "medical": 3, "military": 4, "park": 8, "pedestrian": 7, "recreation": 0, "religious": 3, "residential": 3,
               "resource_extraction": 9, "transportation": 4}
LANDUSE_CLASS = {"meadow": 0, "pasture": 0, "grass": 0, "village_green": 0, "allotments": 2, "plant_nursery": 2, "orchard": 2,
                 "vineyard": 2, "farmyard": 2, "greenhouse_horticulture": 2, "bunker": 6, "sand": 6, "beach_resort": 6,
                 "water_hazard": 5, "track": 7, "parking": 7, "plaza": 7, "pedestrian": 7, "garages": 7, "playground": 8,
                 "garden": 8, "park": 8, "dog_park": 8, "flowerbed": 8, "stadium": 4, "marina": 7, "airfield": 0,
                 "aerodrome": 0, "quarry": 9, "brownfield": 9, "greenfield": 0, "forest": 1, "wood": 1, "railway": 4,
                 "raceway": 11, "motorsport": 11}
LAND_CLASS = {"forest": 1, "wood": 1, "grass": 0, "grassland": 0, "meadow": 0, "scrub": 0, "shrub": 0, "heath": 0,
              "shrubbery": 8, "sand": 6, "beach": 6, "dune": 6, "bare_rock": 9, "rock": 9, "scree": 9, "stone": 9,
              "shingle": 6, "glacier": 9, "cliff": 9, "wetland": 10, "marsh": 10, "swamp": 10, "bog": 10, "reedbed": 10, "mud": 10}
COVER_SUB = {"forest": 1, "crop": 2, "grass": 0, "shrub": 0, "barren": 9, "wetland": 10, "mangrove": 10, "moss": 0,
             "snow": 9, "urban": 3}
WATER_LINE_W = {"river": 12.0, "canal": 8.0, "stream": 3.0}

# LINE kinds: 0 motorway/trunk,1 primary,2 secondary/tertiary,3 residential/service,4 path/footway,5 railway
ROAD_KIND = {"motorway": 0, "trunk": 0, "primary": 1, "secondary": 2, "tertiary": 2, "residential": 3, "service": 3,
             "unclassified": 3, "living_street": 3, "unknown": 3, "pedestrian": 4, "footway": 4, "path": 4, "steps": 4,
             "cycleway": 4, "track": 4, "bridleway": 4, "sidewalk": 4, "crosswalk": 4}
ROAD_W = {"motorway": 22.0, "trunk": 16.0, "primary": 11.0, "secondary": 8.5, "tertiary": 7.0, "residential": 6.0,
          "service": 4.0, "unclassified": 5.5, "living_street": 5.0, "unknown": 5.0, "pedestrian": 4.0, "footway": 2.0,
          "path": 1.8, "steps": 2.0, "cycleway": 2.5, "track": 3.0, "bridleway": 2.0, "sidewalk": 2.0, "crosswalk": 3.0}

# ------------------------------------------------------------------ the build


class Ctx:
    pass


def build(name, cfg, preview=False):
    t0 = time.time()
    log("== %s" % name)
    c = Ctx()
    c.name, c.cfg = name, cfg
    trk, meta = load_track(cfg["id"])
    c.trk, c.meta = trk, meta
    P = trk[:, :2]
    H = trk[:, 2]
    hw = meta["width"] / 2
    c.hw = hw
    ds = meta["ds"]

    # 1. georeference
    outline = georef.load_outline(cfg["f1"])
    geo, st, outline_game = georef.fit(P, outline)
    c.geo, c.fit, c.outline = geo, st, outline_game
    log("  fit: RMS %.2f m, median %.2f, p95 %.2f, max %.2f, scale %.4f, rot %.2f deg, mirror %s; after local warp RMS %.2f (max shift %.1f m)" % (
        st["rms"], st["median"], st["p95"], st["max"], st["scale"], st["rot_deg"], st["mirror"], st.get("warp_rms", -1), st.get("warp_max_shift", 0)))

    # 2. region
    x0, z0 = P.min(0) - MARGIN
    x1, z1 = P.max(0) + MARGIN
    c.region = (x0, z0, x1, z1)
    corners = np.array([[x0, z0], [x1, z0], [x1, z1], [x0, z1]])
    ll = geo.game_to_ll(corners)
    box = (ll[:, 0].min() - 0.002, ll[:, 1].min() - 0.002, ll[:, 0].max() + 0.002, ll[:, 1].max() + 0.002)
    c.box = box
    rbox = shapely.box(x0, z0, x1, z1)
    tree_trk = cKDTree(P)
    line = shapely.LinearRing(P)
    corridor = shapely.buffer(line, hw + 4.0, quad_segs=4)
    tunnel = None
    if "tunnel" in cfg:
        s = np.arange(len(P)) * ds
        m = (s >= cfg["tunnel"][0]) & (s <= cfg["tunnel"][1])
        idx = np.where(m)[0]
        tunnel = shapely.buffer(shapely.LineString(P[idx]), hw + 4.0, quad_segs=4)
        open_idx = np.where(~m)[0]
        # corridor without the tunnel stretch (two pieces if it wraps; here it does not)
        corridor = shapely.buffer(shapely.LineString(np.vstack([P[open_idx[open_idx > idx[-1]]], P[open_idx[open_idx < idx[0]]]])), hw + 4.0, quad_segs=4)
    shapely.prepare(corridor)

    def near_track(xy, r):
        d, _ = tree_trk.query(xy, distance_upper_bound=r + 10)
        return d

    name_key = "%s_%s" % (name, cfg["id"])

    # 3. data
    cols_b = ["id", "geometry", "bbox", "height", "min_height", "num_floors", "min_floor", "roof_shape", "class", "subtype",
              "roof_color", "facade_color", "is_underground", "sources"]
    tb = ov.fetch("buildings", "building", box, name_key, columns=cols_b)
    tlu = ov.fetch("base", "land_use", box, name_key, columns=["id", "geometry", "bbox", "subtype", "class", "source_tags"])
    tlc = ov.fetch("base", "land_cover", box, name_key, columns=["id", "geometry", "bbox", "subtype", "cartography"])
    tland = ov.fetch("base", "land", box, name_key, columns=["id", "geometry", "bbox", "subtype", "class", "source_tags"])
    twat = ov.fetch("base", "water", box, name_key, columns=["id", "geometry", "bbox", "subtype", "class", "is_salt"])
    tinf = ov.fetch("base", "infrastructure", box, name_key, columns=["id", "geometry", "bbox", "subtype", "class"])
    tseg = ov.fetch("transportation", "segment", box, name_key,
                    columns=["id", "geometry", "bbox", "subtype", "class", "width_rules", "road_flags", "rail_flags", "level_rules"])

    # 4. terrain
    c.terr = build_terrain(c, P, H, hw, tree_trk, cfg, twat, geo)

    # 5. buildings
    c.bldg = build_buildings(c, tb, geo, corridor, tunnel, tree_trk, rbox)
    # 6. areas
    c.areas = build_areas(c, tlc, tland, tlu, tinf, twat, geo, rbox)
    # 7. lines
    c.lines = build_lines(c, tseg, geo, line, hw, rbox, tree_trk)
    # 8. trees
    c.trees = build_trees(c, tland, geo, rbox, tree_trk, hw, cfg)

    path = write_file(c)
    c.size = os.path.getsize(path)
    log("  wrote %s: %.2f MB (%d buildings, %d areas, %d lines, %d trees) in %.0f s" % (
        os.path.relpath(path, REPO), c.size / 1e6, len(c.bldg), len(c.areas), len(c.lines), len(c.trees), time.time() - t0))
    if preview:
        import preview as pv
        pv.render(c, os.path.join(HERE, "preview_%s.png" % name))
    return c

# ------------------------------------------------------------------ terrain


def build_terrain(c, P, H, hw, tree_trk, cfg, twat, geo):
    x0, z0, x1, z1 = c.region
    cell = float(cfg["cell"])
    nx = int(np.ceil((x1 - x0) / cell)) + 1
    nz = int(np.ceil((z1 - z0) / cell)) + 1
    dem = terrain.Dem(c.box, z=14 if cell <= 10 else 13)
    dem.smooth(cfg.get("dem_smooth", 25.0), (c.box[1] + c.box[3]) / 2)
    gx = x0 + np.arange(nx) * cell
    gz = z0 + np.arange(nz) * cell
    XX, ZZ = np.meshgrid(gx, gz)  # z outer, x inner
    G = np.column_stack([XX.ravel(), ZZ.ravel()])
    llg = geo.game_to_ll(G)
    raw = dem.sample(llg[:, 0], llg[:, 1])
    llt = geo.game_to_ll(P)
    dt = dem.sample(llt[:, 0], llt[:, 1])
    offset = float(np.median(H - dt))           # game height = DEM + offset (on average along the lap)
    sea = offset + 0.0                           # sea level in the game frame
    base = np.maximum(raw, -8.0) + offset        # sea bed at most 8 m below sea level
    resid = H - (dt + offset)
    # spread the track's residual (game profile vs DEM) into the land around it, fading out 60 .. 500 m
    d, j = tree_trk.query(G, k=24, distance_upper_bound=700)
    valid = np.isfinite(d)
    jj = np.where(valid, j, 0)
    w = np.where(valid, 1.0 / (d ** 2 + 400.0), 0.0)
    ws = w.sum(1)
    R = np.where(ws > 0, (w * resid[jj]).sum(1) / np.maximum(ws, 1e-12), 0.0)
    dmin = np.where(valid[:, 0], d[:, 0], 1e9)
    u = np.clip((dmin - 60.0) / 440.0, 0, 1)
    fade = 1 - u * u * (3 - 2 * u)
    hgt = base + R * fade
    # on and next to the track: exactly the track height (the lowest level where two parts of the lap pass close)
    near = dmin < hw + 60.0
    idxn = np.where(near)[0]
    hn = np.empty(len(idxn))
    for k, gi in enumerate(idxn):
        ids = tree_trk.query_ball_point(G[gi], dmin[gi] + 6.0)
        hn[k] = H[ids].min() if ids else H[j[gi, 0]]
    u2 = np.clip((dmin[idxn] - (hw + 8.0)) / 52.0, 0, 1)
    b2 = u2 * u2 * (3 - 2 * u2)
    hgt[idxn] = hn * (1 - b2) + hgt[idxn] * b2
    hgt = hgt.reshape(nz, nx).astype(np.float32)
    # check: bilinear terrain under the centreline vs the track height
    fx = (P[:, 0] - x0) / cell
    fz = (P[:, 1] - z0) / cell
    i, k = fx.astype(int), fz.astype(int)
    a, b = fx - i, fz - k
    tH = (hgt[k, i] * (1 - a) + hgt[k, i + 1] * a) * (1 - b) + (hgt[k + 1, i] * (1 - a) + hgt[k + 1, i + 1] * a) * b
    err = tH - H
    c.terr_check = dict(rms=float(np.sqrt((err ** 2).mean())), max=float(np.abs(err).max()), offset=offset,
                        dem_vs_game_rms=float(np.sqrt((resid ** 2).mean())), sea=sea)
    log("  terrain %dx%d @ %.0f m; offset %.1f m; DEM vs game profile RMS %.1f m; centreline check RMS %.2f max %.2f m" % (
        nx, nz, cell, offset, c.terr_check["dem_vs_game_rms"], c.terr_check["rms"], c.terr_check["max"]))
    return dict(nx=nx, nz=nz, x0=float(x0), z0=float(z0), cell=cell, h=hgt)

# ------------------------------------------------------------------ buildings


def build_buildings(c, tb, geo, corridor, tunnel, tree_trk, rbox):
    if tb is None:
        return []
    df = tb.to_pandas()
    df = df[~(df.is_underground.fillna(False).astype(bool))].reset_index(drop=True)
    g = geoms_to_game(df.geometry.values, geo)
    parts, owner = shapely.get_parts(g, return_index=True)
    polys = np.array([p for p in parts])
    ok = shapely.get_type_id(polys) == 3
    polys, owner = polys[ok], owner[ok]
    area = shapely.area(polys)
    cen = shapely.get_coordinates(shapely.centroid(polys))
    dist, _ = tree_trk.query(cen)
    keep = (area >= 15.0) & (dist <= MAX_DIST) & shapely.within(polys, rbox)
    hit = shapely.intersects(corridor, polys)
    keep &= ~hit
    polys, owner, area, cen = polys[keep], owner[keep], area[keep], cen[keep]
    under = shapely.intersects(tunnel, polys) if tunnel is not None else np.zeros(len(polys), bool)
    a = df.iloc[owner].reset_index(drop=True)
    h = a.height.astype(float).values.copy()
    fl = a.num_floors.astype(float).values
    mh = a.min_height.astype(float).values.copy()
    mf = a.min_floor.astype(float).values
    # heights from Microsoft's ML footprints are estimates from imagery: below 2.5 m they are noise -> treat as missing
    ml_h = np.array([_height_source(sv) == "ml" for sv in a.sources.values])
    h[ml_h & (h < 2.5)] = np.nan
    known = np.isfinite(h) & (h > 0)
    hf = ~known & np.isfinite(fl) & (fl > 0)
    h[hf] = fl[hf] * LEVEL_H
    kinds = np.array([building_kind(s, k, (hh if np.isfinite(hh) else None)) for s, k, hh in zip(a.subtype.values, a["class"].values, h)])
    # grandstands / stadium stands: ML heights see only the roof edge; without a mapped height use 8-12 m
    gs = (kinds == 4) & ~(known & ~ml_h) & ~hf
    h[gs] = np.where(area[gs] >= 300, np.maximum(np.nan_to_num(h[gs]), 12.0), np.maximum(np.nan_to_num(h[gs]), 8.0))
    # still missing: garages / sheds 3 m; otherwise the median of the measured heights round about (150 m),
    # kept to a sensible range for the footprint; with no measured neighbours, the kind's default
    have = np.isfinite(h) & (h > 0)
    est = ~have
    nb_med = np.full(len(h), np.nan)
    if have.sum() >= 5 and est.any():
        kt = cKDTree(cen[have])
        hv = h[have]
        nbrs = kt.query_ball_point(cen[est], 150.0)
        nb_med[est] = [np.median(hv[n]) if len(n) >= 5 else np.nan for n in nbrs]
    for i in np.where(est)[0]:
        k = kinds[i]
        if k == 7:
            v = 3.0
        elif np.isfinite(nb_med[i]):
            v = nb_med[i]
        else:
            v = KIND_HEIGHT[k]
        if area[i] < 40:
            v = min(v, 4.0)
        elif area[i] < 100:
            v = min(v, 8.0)
        h[i] = float(np.clip(v, 3.0, 45.0))
    kinds = np.array([building_kind(s, k, hh) for s, k, hh in zip(a.subtype.values, a["class"].values, h)])
    mh = np.where(np.isfinite(mh), mh, np.where(np.isfinite(mf), mf * LEVEL_H, 0.0))
    mh = np.clip(mh, 0, None)
    mh[under] = np.maximum(mh[under], 7.0)   # Monaco: the hotel over the tunnel
    h = np.maximum(h, mh + 2.5)
    h = np.maximum(h, 2.0)
    out = []
    for i in range(len(polys)):
        ring = ring_ccw(polys[i], 0.5)
        if ring is None:
            continue
        rs = a.roof_shape.values[i]
        if isinstance(rs, str):
            roof = ROOF.get(rs, 5)
        else:
            # no roof shape mapped: small low houses / sheds get a pitched roof, the rest flat (an estimate)
            roof = 1 if (kinds[i] in (0, 1, 7) and area[i] < 250 and h[i] <= 12 and c.name != "Monaco") else 0
        col = parse_color(a.facade_color.values[i])
        if col[3] == 0:
            col = parse_color(a.roof_color.values[i])
        out.append((float(h[i]), float(mh[i]), roof, int(kinds[i]), col, ring))
    c.bstats = dict(total=len(out), measured=int(known.sum()), from_levels=int(hf.sum()), estimated=int(est.sum()))
    log("  buildings: %d (height measured %d, from levels %d, estimated %d)" % (len(out), known.sum(), hf.sum(), est.sum()))
    return out

# ------------------------------------------------------------------ areas


def build_areas(c, tlc, tland, tlu, tinf, twat, geo, rbox):
    groups = {0: [], 1: [], 2: []}   # 0 land cover (background), 1 land use / natural / paving, 2 water

    def add(df, kinds, grp, tol, min_area=30.0):
        if df is None or not len(df):
            return
        g = geoms_to_game(df.geometry.values, geo)
        g = shapely.intersection(g, rbox)
        for gi, k in zip(g, kinds):
            if k is None or gi is None or gi.is_empty:
                continue
            for p in shapely.get_parts(gi):
                if p.geom_type != "Polygon" or p.area < min_area:
                    continue
                r = ring_ccw(shapely.Polygon(p.exterior), tol)
                if r is not None:
                    groups[2 if k == 5 else grp].append((p.area, int(k), r))

    if tlc is not None:
        d = tlc.to_pandas()
        mz = np.array([cc["max_zoom"] if cc is not None else 0 for cc in d.cartography])
        d = d[mz == mz.max()]
        add(d, [COVER_SUB.get(s) for s in d.subtype], 0, 3.0, 400.0)
    if tland is not None:
        d = tland.to_pandas()
        d = d[d.subtype != "tree"]
        add(d, [LAND_CLASS.get(k) for k in d["class"]], 1, 1.0)
    if tlu is not None:
        d = tlu.to_pandas()
        ks = []
        for s, k, tg in zip(d.subtype, d["class"], d.source_tags):
            sport = str(tag(tg, "sport") or "")
            if "motor" in sport or "karting" in sport or tag(tg, "highway") == "raceway":
                ks.append(11)
            elif s in ("protected", "winter_sports"):
                ks.append(None)
            elif k in LANDUSE_CLASS:
                ks.append(LANDUSE_CLASS[k])
            else:
                ks.append(LANDUSE_SUB.get(s))
        add(d, ks, 1, 1.0)
    if tinf is not None:
        d = tinf.to_pandas()
        d = d[d["class"].isin(["parking", "apron", "runway", "taxiway", "helipad"])]
        add(d, [7] * len(d), 1, 1.0, 60.0)
    if twat is not None:
        d = twat.to_pandas()
        gt = shapely.get_type_id(shapely.from_wkb(d.geometry.values))
        poly = d[(gt == 3) | (gt == 6)]
        add(poly, [5] * len(poly), 2, 1.0, 15.0)
        ln = d[((gt == 1) | (gt == 5)) & d.subtype.isin(list(WATER_LINE_W))]
        if len(ln):
            g = geoms_to_game(ln.geometry.values, geo)
            for gi, s in zip(g, ln.subtype):
                b = shapely.intersection(shapely.buffer(gi, WATER_LINE_W[s] / 2, quad_segs=2), rbox)
                for p in shapely.get_parts(b):
                    if p.geom_type == "Polygon" and p.area >= 15:
                        r = ring_ccw(shapely.Polygon(p.exterior), 1.0)
                        if r is not None:
                            groups[2].append((p.area, 5, r))
    out = []
    for grp in (0, 1, 2):
        out += [(k, r) for a, k, r in sorted(groups[grp], key=lambda e: -e[0])]
    cnt = np.bincount([k for k, _ in out], minlength=12) if out else []
    log("  areas: %d (by kind %s)" % (len(out), [int(x) for x in cnt]))
    return out

# ------------------------------------------------------------------ lines


def build_lines(c, tseg, geo, line, hw, rbox, tree_trk):
    if tseg is None:
        return []
    d = tseg.to_pandas()
    keep = []
    for sub, cls, rf, lf, lv in zip(d.subtype, d["class"], d.road_flags, d.rail_flags, d.level_rules):
        if sub == "water":
            keep.append(False)
            continue
        flags = rf if sub == "road" else lf
        keep.append(not (flag_whole(flags, "is_tunnel") or level_below(lv)))
    d = d[np.array(keep, bool)]
    g = geoms_to_game(d.geometry.values, geo)
    cut = shapely.buffer(line, hw + 3.0, quad_segs=4)
    g = shapely.difference(shapely.intersection(g, rbox), cut)
    out = []
    for gi, sub, cls, wr in zip(g, d.subtype, d["class"], d.width_rules):
        if gi is None or gi.is_empty:
            continue
        if sub == "rail":
            kind, w = 5, 3.5
        else:
            kind = ROAD_KIND.get(cls, 3)
            w = ROAD_W.get(cls, 5.0)
            if wr is not None and len(wr):
                v = wr[0].get("value")
                if v and 1.0 <= v <= 60:
                    w = float(v)
        for p in shapely.get_parts(gi):
            if p.geom_type != "LineString" or p.length < 5.0:
                continue
            p = shapely.simplify(p, 0.5)
            pts = np.asarray(p.coords)[:, :2]
            dd, _ = tree_trk.query(pts.mean(0))
            if dd > MAX_DIST + 300:
                continue
            for k0 in range(0, len(pts) - 1, 65534):
                out.append((kind, w, pts[k0:k0 + 65535]))
    cnt = np.bincount([k for k, _, _ in out], minlength=6) if out else []
    log("  lines: %d (by kind %s)" % (len(out), [int(x) for x in cnt]))
    return out

# ------------------------------------------------------------------ trees


def build_trees(c, tland, geo, rbox, tree_trk, hw, cfg):
    if tland is None:
        return []
    d = tland.to_pandas()
    d = d[d.subtype == "tree"]
    if not len(d):
        return []
    g = geoms_to_game(d.geometry.values, geo)
    pts, hs = [], []
    hdef = cfg.get("tree_h", 10.0)
    for gi, tg in zip(g, d.source_tags):
        hh = to_float(tag(tg, "height"))
        hh = hh if hh and 1.5 <= hh <= 45 else hdef
        if gi.geom_type == "Point":
            pts.append((gi.x, gi.y)); hs.append(hh)
        elif gi.geom_type in ("LineString", "MultiLineString"):
            for p in shapely.get_parts(gi):
                n = max(1, int(p.length / 7.0))
                for q in shapely.line_interpolate_point(p, np.linspace(0, p.length, n + 1)):
                    pts.append((q.x, q.y)); hs.append(hh)
    if not pts:
        return []
    pts = np.array(pts)
    hs = np.array(hs)
    dist, _ = tree_trk.query(pts)
    m = (dist > hw + 5.0) & (dist <= MAX_DIST) & shapely.contains_xy(rbox, pts[:, 0], pts[:, 1])
    if c.bldg:
        bp = [shapely.Polygon(r) for *_, r in c.bldg]
        st = shapely.STRtree(bp)
        inside = st.query(shapely.points(pts), predicate="within")
        m[np.unique(inside[0])] = False
    out = [(float(x), float(z), float(h)) for (x, z), h in zip(pts[m], hs[m])]
    log("  trees: %d" % len(out))
    return out

# ------------------------------------------------------------------ writer


def write_file(c):
    secs = []
    t = c.terr
    b = bytearray(struct.pack("<iifff", t["nx"], t["nz"], t["x0"], t["z0"], t["cell"]))
    b += t["h"].astype("<f4").tobytes()
    secs.append((b"TERR", b))
    b = bytearray(struct.pack("<i", len(c.bldg)))
    for h, mh, roof, kind, col, ring in c.bldg:
        b += struct.pack("<ffBB4BH", h, mh, roof, kind, *col, len(ring))
        b += ring.astype("<f4").tobytes()
    secs.append((b"BLDG", b))
    b = bytearray(struct.pack("<i", len(c.areas)))
    for kind, ring in c.areas:
        b += struct.pack("<BH", kind, len(ring))
        b += ring.astype("<f4").tobytes()
    secs.append((b"AREA", b))
    b = bytearray(struct.pack("<i", len(c.lines)))
    for kind, w, pts in c.lines:
        b += struct.pack("<BfH", kind, w, len(pts))
        b += pts.astype("<f4").tobytes()
    secs.append((b"LINE", b))
    b = bytearray(struct.pack("<i", len(c.trees)))
    if c.trees:
        b += np.array(c.trees, dtype="<f4").tobytes()
    secs.append((b"TREE", b))
    # META (extra section, plain UTF-8 "key=value" lines; readers that do not know it skip it by its length)
    g = c.geo.to_dict()
    meta = {
        "circuit": c.name, "game_id": c.cfg["id"], "lon0": "%.8f" % g["lon0"], "lat0": "%.8f" % g["lat0"],
        "projection": "tmerc(lon0,lat0) WGS84 -> game = scale * R(rot) * tm + (tx, ty)",
        "scale": "%.6f" % g["scale"], "rot_deg": "%.5f" % g["rot_deg"], "tx": "%.3f" % g["tx"], "ty": "%.3f" % g["ty"],
        "mirror": int(g["mirror"]), "fit_rms_m": "%.2f" % c.fit["rms"], "fit_p95_m": "%.2f" % c.fit["p95"], "warp_rms_m": "%.2f" % c.fit.get("warp_rms", -1),
        "warp": "near the track (<300 m) a smooth local shift of up to %.1f m makes the real roads follow the game centreline" % c.fit.get("warp_max_shift", 0),
        "height_offset_m": "%.3f" % c.terr_check["offset"], "sea_level_y": "%.3f" % c.terr_check["sea"],
        "overture_release": ov.RELEASE,
        "attribution": "(c) OpenStreetMap contributors (ODbL), Overture Maps Foundation, Mapzen/AWS Terrain Tiles",
    }
    secs.append((b"META", "".join("%s=%s\n" % kv for kv in meta.items()).encode()))
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, c.name + ".bytes")
    with open(path + ".tmp", "wb") as f:
        f.write(b"F2W1" + struct.pack("<ii", 1, len(secs)))
        for tg, pl in secs:
            f.write(tg + struct.pack("<i", len(pl)) + bytes(pl))
    os.replace(path + ".tmp", path)
    c.sec_sizes = {tg.decode(): len(pl) for tg, pl in secs}
    log("  sections: " + ", ".join("%s %.0f KB" % (k, v / 1024) for k, v in c.sec_sizes.items()))
    return path


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    preview = "--preview" in sys.argv
    names = args or list(CIRCUITS)
    results = {}
    rp = os.path.join(HERE, "results.json")
    if os.path.exists(rp):
        results = json.load(open(rp))
    for n in names:
        c = build(n, CIRCUITS[n], preview)
        results[n] = dict(fit=c.fit, georef=c.geo.to_dict(), terrain=c.terr_check, buildings=c.bstats,
                          areas=len(c.areas), lines=len(c.lines), trees=len(c.trees), bytes=c.size, sections=c.sec_sizes)
        json.dump(results, open(rp, "w"), indent=1, default=float)


if __name__ == "__main__":
    main()
