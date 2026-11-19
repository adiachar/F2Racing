#!/usr/bin/env python3
"""
Monza park species and park furniture, ADDED to the existing model library (models.bytes + atlas.bytes) without the
Poly Haven sources (build_models.py needs them; this script only appends, so it can run where Poly Haven is not
reachable). Same formats, same clump / LOD / impostor treatment as build_models.py (its functions are reused).

    python3 -I Tools/Models/build_park.py <textures dir> <kit dir>

<textures dir>: leaf cut-outs oak.png, pine.png, ash.png (EZ-Tree demo textures, MIT, github.com/dgreenheck/ez-tree)
                and ambientCG bark colour maps Bark001/004/006/014_1K-JPG_Color.jpg (CC0).
<kit dir>:      kit-cidade-lowpoly (CC0, github.com/kayquer/kit-cidade-lowpoly) models/ folder: banco_praca.glb,
                lixeira.glb (flat-colour materials; recoloured to Monza's park furniture).

Tree skeletons are grown here (our own recursive generator, numpy): no third-party code runs.
  park_oak     Quercus robur (farnia): short trunk, a few heavy crooked limbs, wide irregular crown, ~19 m
  park_plane   London plane (platano, the park's avenues): tall straight trunk, mottled bark, high broad dome, ~26 m
               (no plane-leaf cut-out: its clumps reuse island_tree_01's broad-leaf tiles)
  park_robinia Robinia / ash (robinia, frassino: the woods' secondary trees): slender, open, pinnate leaves, ~16 m
  park_pine    Scots pine (pino silvestre): long bare orange-grey trunk, flat tufted crown, ~21 m
Props: park_bench (cast-iron frame, wooden slats), park_bin (dark green steel bin on a post).

ATLAS: only texels whose alpha is 0 in the current atlas are used:
    tiles 21-23 (row 2, x 1280..2047)      clump tiles
    y 1280..1535, x 0..511                  bark strips (4 x 128)          -> wood (0.25 < v < 0.5)
    y 1280..1535, x 512..767                prop palette (8x8 swatches)    -> wood
    y 1536..1791, x 0..2047                 8 clump tiles                  -> leaves (v <= 0.25)
    y 1792..2047, x 0..1535                 8 impostor cards 192x256       -> leaves
ModelLib.IsWood: 0.2505 <= v < 0.5 (bark strips, lamps, props); v >= 0.5 or v < 0.2505 leaves.
"""
import io, math, os, struct, sys, glob
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.argv = [sys.argv[0], "/nonexistent"] + sys.argv[1:]  # build_models reads argv[1] as its source folder
import build_models as bm  # noqa: E402

TEX = sys.argv[2] if len(sys.argv) > 2 else "/tmp/dl_tex"
KIT = sys.argv[3] if len(sys.argv) > 3 else "/tmp/dl_kit/repo/models"
OUT = bm.OUT
AT = bm.AT

# --------------------------------------------------------------------------------------------- atlas slots
OLD_SPECIES = 7  # tree_small_02, jacaranda, island 1-3, shrub_02, shrub_03 (tiles 0..20)
CLUMP_SLOTS = [((21 + i) % 8 * 256, (21 + i) // 8 * 256) for i in range(3)] + [(i * 256, 1536) for i in range(8)]
CARD_SLOTS = [(i * 192, 1792) for i in range(8)]
BARK_SLOTS = [(i * 128, 1280) for i in range(4)]
PAL_X, PAL_Y = 512, 1280

NEW = {
    #             leaf png, bark, sprite rel, clump quad scale, clumps (LOD0, LOD1), wood tris (LOD0, LOD1)
    "park_oak":     dict(leaf="oak", bark="Bark001", sprite=0.26, qs=3.6, k=(820, 220), w=(520, 110)),
    "park_plane":   dict(leaf=None, borrow="island_tree_01", bark="Bark006", qs=3.9, k=(840, 220), w=(480, 100)),
    "park_robinia": dict(leaf="ash", bark="Bark004", sprite=0.30, qs=3.3, k=(700, 200), w=(420, 90)),
    "park_pine":    dict(leaf="pine", bark="Bark014", sprite=0.30, qs=3.0, k=(620, 180), w=(380, 90)),
}

# --------------------------------------------------------------------------------------------- tree growth
P = {
    # trunk length, leader (fraction of the trunk that carries limbs), limbs, their angle (deg), length, level-2 count,
    # angle, length, gnarl (rad per section), up pull, leaf blob radius, leaves per twig, bare fraction of each twig
    "park_oak":     dict(H=12.0, r0=0.45, start=0.30, n1=8, a1=60, l1=9.0, n2=6, a2=50, l2=4.2, n3=5, a3=45, l3=1.8, gn=0.17, up=0.04, lr=0.9, lp=60, bare=0.2, droop=0.0),
    "park_plane":   dict(H=22.0, r0=0.50, start=0.34, n1=10, a1=46, l1=9.0, n2=6, a2=45, l2=4.2, n3=4, a3=45, l3=1.8, gn=0.08, up=0.05, lr=0.9, lp=60, bare=0.2, droop=0.0),
    "park_robinia": dict(H=14.0, r0=0.28, start=0.38, n1=8, a1=38, l1=6.0, n2=5, a2=42, l2=3.2, n3=4, a3=40, l3=1.5, gn=0.13, up=0.05, lr=0.85, lp=50, bare=0.25, droop=0.0),
    "park_pine":    dict(H=19.0, r0=0.32, start=0.70, n1=12, a1=84, l1=5.6, n2=5, a2=50, l2=2.4, n3=3, a3=35, l3=1.0, gn=0.12, up=0.035, lr=0.75, lp=55, bare=0.3, droop=0.0),
}


def rot(v, axis, ang):
    axis = axis / (np.linalg.norm(axis) + 1e-9)
    return v * math.cos(ang) + np.cross(axis, v) * math.sin(ang) + axis * np.dot(axis, v) * (1 - math.cos(ang))


def perp(d):
    a = np.array([0, 1.0, 0]) if abs(d[1]) < 0.9 else np.array([1.0, 0, 0])
    p = np.cross(d, a); return p / np.linalg.norm(p)


def grow(name, seed):
    """returns wood tubes [(points (n,3), radii (n,))] and leaf points (m,3)"""
    p = P[name]; r = np.random.default_rng(seed)
    tubes, leaves = [], []

    def branch(o, d, L, rad, level, sections):
        pts, rs = [o.copy()], [rad]
        seg = L / sections; cur = o.copy(); dd = d.copy()
        for s in range(sections):
            dd = rot(dd, perp(dd), r.normal(0, p["gn"] * (0.3 if level == 0 else 1.0)))
            dd = dd + np.array([0, p["up"] * (1.0 if level < 2 else 0.5), 0]); dd /= np.linalg.norm(dd)
            cur = cur + dd * seg; pts.append(cur.copy())
            rs.append(rad * (1 - 0.82 * (s + 1) / sections))
        pts = np.array(pts); rs = np.array(rs)
        if level <= 1 or (level == 2 and rad > 0.03):
            tubes.append((pts, rs))
        if level == 0:
            n, ang, ln = p["n1"], p["a1"], p["l1"]
        elif level == 1:
            n, ang, ln = p["n2"], p["a2"], p["l2"]
        elif level == 2:
            n, ang, ln = p["n3"], p["a3"], p["l3"]
        else:
            # twig: the leaves
            for t in np.linspace(p["bare"], 1.0, 3):
                c = pts[0] + (pts[-1] - pts[0]) * t
                k = p["lp"] // 3
                q = r.normal(0, 1, (k, 3)); q *= (p["lr"] * r.uniform(0.3, 1.0, (k, 1))) / (np.linalg.norm(q, axis=1, keepdims=True) + 1e-9)
                q[:, 1] *= 0.6
                leaves.append(c + q)
            return
        az0 = r.uniform(0, 2 * math.pi)
        for i in range(n):
            t = p["start"] + (1 - p["start"]) * (i + r.uniform(0.1, 0.9)) / n if level == 0 else r.uniform(0.25, 1.0)
            idx = min(len(pts) - 2, int(t * (len(pts) - 1)))
            base = pts[idx] + (pts[idx + 1] - pts[idx]) * (t * (len(pts) - 1) - idx)
            axis = rot(perp(d), d, az0 + i * 2.39996 + r.normal(0, 0.3))
            nd = rot(d, axis, math.radians(ang + r.normal(0, 8)))
            nd[1] += -p["droop"]; nd /= np.linalg.norm(nd)
            scale = (1.15 - 0.55 * t) if level == 0 else (1.2 - 0.5 * t)
            if name == "park_pine" and level == 0: scale = (1.25 - 0.9 * (t - p["start"]) / (1 - p["start"]))
            branch(base, nd, ln * scale * r.uniform(0.8, 1.2), rs[idx] * (0.55 if level == 0 else 0.5), level + 1, 6 if level == 0 else 4 if level == 1 else 2)

    branch(np.zeros(3), np.array([0, 1.0, 0]), p["H"], p["r0"], 0, 10)
    return tubes, np.vstack(leaves)


def tube_mesh(tubes, segs=(9, 5, 3)):
    V, F, off = [], [], 0
    for pts, rs in tubes:
        n = len(pts); sg = segs[0] if rs[0] > 0.2 else segs[1] if rs[0] > 0.06 else segs[2]
        d = np.gradient(pts, axis=0); d /= np.linalg.norm(d, axis=1, keepdims=True) + 1e-9
        u = perp(d[0])
        ring = []
        for i in range(n):
            u = u - d[i] * np.dot(u, d[i]); u /= np.linalg.norm(u) + 1e-9
            w = np.cross(d[i], u)
            for k in range(sg):
                a = 2 * math.pi * k / sg
                ring.append(pts[i] + (u * math.cos(a) + w * math.sin(a)) * max(rs[i], 0.01))
        V.append(np.array(ring))
        for i in range(n - 1):
            for k in range(sg):
                a = off + i * sg + k; b = off + i * sg + (k + 1) % sg; c = a + sg; e = b + sg
                F += [(a, c, b), (b, c, e)]
        off += n * sg
    return np.vstack(V), np.array(F, np.int64)


# --------------------------------------------------------------------------------------------- old files
def read_atlas(path):
    d = open(path, "rb").read()
    n = struct.unpack("<I", d[4:8])[0]; jpg = d[8:8 + n]
    m = struct.unpack("<I", d[8 + n:12 + n])[0]; png = d[12 + n:12 + n + m]
    c = np.array(Image.open(io.BytesIO(jpg)).convert("RGB")); a = np.array(Image.open(io.BytesIO(png)).convert("L"))
    return np.dstack([c, a]).astype(np.uint8)


def read_models_raw(path):
    """[(name, raw bytes of the whole model record)] of an existing models.bytes"""
    d = open(path, "rb").read(); assert d[:4] == b"F2MD"
    cnt = struct.unpack("<H", d[6:8])[0]; o = 8; out = []
    for _ in range(cnt):
        s = o
        nl = d[o]; name = d[o + 1:o + 1 + nl].decode(); o += 1 + nl
        kind, nlods, _ = struct.unpack("<BBH", d[o:o + 4]); o += 4
        o += 4 * 5 + 4 * 6
        for _ in range(nlods):
            nv, nt = struct.unpack("<II", d[o:o + 8]); o += 8
            o += nv * 14 + nt * 6
        out.append((name, d[s:o]))
    return out


# --------------------------------------------------------------------------------------------- props (flat colours)
PALETTE = {}


def swatch(rgb):
    key = tuple(int(c) for c in rgb[:3])
    if key not in PALETTE:
        i = len(PALETTE); PALETTE[key] = (PAL_X + (i % 32) * 8, PAL_Y + (i // 32) * 8)
    x, y = PALETTE[key]
    return ((x + 4) / AT, 1.0 - (y + 4) / AT)


RECOLOUR = {
    "park_bench": {(11, 13, 18): (34, 38, 34), (100, 45, 13): (112, 74, 44)},  # cast iron, varnished slats
    "park_bin": {(255, 82, 7): (40, 66, 44), (159, 154, 141): (120, 118, 110), (41, 50, 61): (36, 40, 38), (11, 13, 18): (30, 32, 30)},
}
PROPS = {"park_bench": ("banco_praca", 1.75), "park_bin": ("lixeira", 0.95)}  # source, real width / height (m)


def load_prop(mid):
    import trimesh
    src, size = PROPS[mid]
    f = glob.glob(os.path.join(KIT, "**", src + ".glb"), recursive=True)[0]
    sc = trimesh.load(f, force="scene")
    V, U, N, A, F, off = [], [], [], [], [], 0
    for node in sc.graph.nodes_geometry:
        T, gname = sc.graph[node]
        g = sc.geometry[gname].copy(); g.apply_transform(T)
        col = tuple(int(c) for c in g.visual.material.baseColorFactor[:3])
        col = RECOLOUR[mid].get(col, col)
        uv = swatch(col)
        v = np.asarray(g.vertices, np.float64); f = np.asarray(g.faces, np.int64)
        # flat shading look: unweld per face so the bake sees the hard edges
        vv = v[f].reshape(-1, 3); ff = np.arange(len(vv)).reshape(-1, 3)
        fn = np.cross(vv[1::3] - vv[0::3], vv[2::3] - vv[0::3]); fn /= np.linalg.norm(fn, axis=1, keepdims=True) + 1e-9
        V.append(vv); F.append(ff + off); N.append(np.repeat(fn, 3, axis=0)); U.append(np.tile(uv, (len(vv), 1)))
        A.append(np.full(len(vv), 0.95)); off += len(vv)
    V = np.vstack(V); F = np.vstack(F); N = np.vstack(N); U = np.vstack(U); A = np.concatenate(A)
    # weld corners that share position, face normal and colour (flat shading kept)
    key = np.round(np.hstack([V * 1000, N * 100, U * 4096])).astype(np.int64)
    _, idx, inv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    inv = inv.reshape(-1); V, N, U, A, F = V[idx], N[idx], U[idx], A[idx], inv[F]
    lo = V.min(0); hi = V.max(0)
    V = V - np.array([(lo[0] + hi[0]) / 2, lo[1], (lo[2] + hi[2]) / 2])
    s = size / (hi[0] - lo[0] if mid == "park_bench" else hi[1] - lo[1]); V *= s
    return V, U, N, A, F


# --------------------------------------------------------------------------------------------- main
def main():
    px = read_atlas(os.path.join(OUT, "atlas.bytes"))
    old = read_models_raw(os.path.join(OUT, "models.bytes"))
    old = [(n, b) for n, b in old if not n.startswith("park_")]
    free = px[..., 3] == 0
    for (x, y) in CLUMP_SLOTS: assert free[y + 8:y + 248, x + 8:x + 248].all(), ("clump slot used", x, y)
    for (x, y) in CARD_SLOTS: assert free[y:y + 256, x:x + 192].all(), ("card slot used", x, y)
    for (x, y) in BARK_SLOTS: assert free[y:y + 256, x:x + 128].all(), ("bark slot used", x, y)
    atlas = bm.Atlas(); atlas.px = px.copy()
    # where the old clump tiles' colour is (plane borrows them)
    old_sp = ["tree_small_02", "jacaranda_tree", "island_tree_01", "island_tree_02", "island_tree_03", "shrub_02", "shrub_03"]
    names = list(NEW.keys())
    sp_index = {n: OLD_SPECIES + i for i, n in enumerate(names)}
    slot_of = {}
    si = 0
    for n in names:
        if NEW[n].get("borrow"): continue
        for v in range(3): slot_of[(sp_index[n], v)] = CLUMP_SLOTS[si]; si += 1
    orig_clump = bm.clump_rect

    def clump_rect(sp, v):
        if sp < OLD_SPECIES: return orig_clump(sp, v)
        x, y = slot_of[(sp, v)]; return x, y, 256, 256
    bm.clump_rect = clump_rect
    bm.card_rect = lambda tree, view: (CARD_SLOTS[tree * 2 + view][0], CARD_SLOTS[tree * 2 + view][1], 192, 256)
    bm.bark_rect = lambda tree: (BARK_SLOTS[tree][0], BARK_SLOTS[tree][1], 128, 256)

    models = {}
    for ti, mid in enumerate(names):
        sp = NEW[mid]; print("==", mid, flush=True)
        tubes, pts = grow(mid, 1000 + ti * 77)
        Vw, Fw = tube_mesh(tubes)
        print("   wood", len(Fw), "tris,", len(pts), "leaf points", flush=True)
        if sp.get("borrow"):
            spi = old_sp.index(sp["borrow"])
            tiles = [Image.fromarray(atlas.px[orig_clump(spi, 0)[1]:orig_clump(spi, 0)[1] + 256, orig_clump(spi, 0)[0]:orig_clump(spi, 0)[0] + 256], "RGBA")]
        else:
            spi = sp_index[mid]
            im = Image.open(os.path.join(TEX, sp["leaf"] + ".png")).convert("RGBA")
            a = np.array(im)
            sprites = bm.sprites_from(a[..., :3], a[..., 3])
            print("   sprites", len(sprites), flush=True)
            tiles = []
            for v in range(3):
                t = bm.make_clump_tile(sprites, sp["sprite"], 256, seed=spi * 31 + v)
                x, y, _, _ = clump_rect(spi, v); atlas.put(t, x, y); tiles.append(t)
        leafc = tuple(float(c) for c in bm.tile_mean_linear(tiles[0]))
        bim = Image.open(os.path.join(TEX, sp["bark"] + "_1K-JPG_Color.jpg")).convert("RGB")
        bw, bh = bim.size
        bim = bim.crop((bw // 4, 0, bw // 4 + bw // 4, bh)).resize((128, 256), Image.LANCZOS)
        atlas.put(bim, *bm.bark_rect(ti)[:2])
        height = float(max(pts[:, 1].max(), Vw[:, 1].max()))
        crown_c = pts.mean(0)
        lods = []
        for lod in (0, 1):
            Vd, Fd = bm.decimate(Vw, Fw, sp["w"][lod])
            wp = bm.wood_geo(Vd, Fd, ti, height)
            cp = bm.build_clumps(pts, sp["k"][lod], sp["qs"] * (1.0 if lod == 0 else 1.55), spi, crown_c, height, False, seed=11 + lod)
            lods.append(bm.merge_parts([wp, cp]))
            print(f"   LOD{lod}: wood {len(Fd)} tris, clumps {len(cp[4])} tris, total {len(lods[-1][4])}", flush=True)
        radius = float(np.hypot(pts[:, 0], pts[:, 2]).max())
        radius = float(min(radius, np.percentile(np.hypot(lods[0][0][:, 0], lods[0][0][:, 2]), 98) * 1.05))
        models[mid] = dict(kind=0, lods=lods, height=height, radius=radius, leaf=leafc, ti=ti)

    # impostors
    dil = bm.dilate_atlas(atlas.px)
    atlas_f = dil.astype(np.float32) / 255.0
    for mid in names:
        m = models[mid]; V, U, N, A, F = m["lods"][0]; ti = m["ti"]
        R = m["radius"] * 1.04; H = m["height"] * 1.01
        for view, az in enumerate((0, 90)):
            x, y, w, h = bm.card_rect(ti, view)
            img = bm.rasterize(V, U, N, A, F, atlas_f, w, h, az, False, (-R, R), (0, H), ss=2)
            rgba = np.dstack([np.clip(img[..., :3] * 255, 0, 255), np.clip(img[..., 3:4] * 255, 0, 255)]).astype(np.uint8)
            atlas.put(Image.fromarray(rgba, "RGBA"), x, y)
        Vc, Uc, Nc, Ac, Fc = [], [], [], [], []
        for view in range(2):
            rect = bm.card_rect(ti, view)
            ax = np.array([1.0, 0, 0]) if view == 0 else np.array([0, 0, 1.0])
            base = len(Vc)
            for dx, dy in [(-R, 0), (R, 0), (R, H), (-R, H)]:
                Vc.append(ax * dx + np.array([0, dy, 0]))
                Uc.append(bm.uv_of(rect, 0.0 if dx < 0 else 1.0, 1.0 if dy == 0 else 0.0, 0.0))
                n = ax * (-0.6 if dx < 0 else 0.6) + np.array([0, 0.35 if dy == 0 else 0.8, 0]); Nc.append(n / np.linalg.norm(n))
                Ac.append(0.9 if dy == 0 else 1.0)
            Fc += [(base, base + 2, base + 1), (base, base + 3, base + 2)]
        m["lods"].append((np.array(Vc), np.array(Uc), np.array(Nc), np.array(Ac), np.array(Fc, np.int64)))
        print("impostor", mid, flush=True)

    # props
    for mid in PROPS:
        V, U, N, A, F = load_prop(mid)
        models[mid] = dict(kind=2, lods=[(V, U, N, A, F)], height=float(V[:, 1].max()), radius=float(np.hypot(V[:, 0], V[:, 2]).max()), leaf=(0, 0, 0))
        print(mid, len(F), "tris", flush=True)
    for (r, g, b), (x, y) in PALETTE.items():
        atlas.px[y:y + 8, x:x + 8] = (r, g, b, 255)

    # write: the old records unchanged, then the new ones
    new_alpha = atlas.px[..., 3].copy()
    out_px = bm.dilate_atlas(atlas.px, keep_alpha=True)
    out_px[..., 3] = new_alpha
    # keep the old texels exactly as decoded (no colour drift from the dilation on old regions)
    oldmask = px[..., 3] > 0
    out_px[oldmask] = px[oldmask]
    # the old dilated colour in the free space was only padding: our dilation replaces it
    bm.write_atlas(out_px, os.path.join(OUT, "atlas.bytes"))
    tmp = os.path.join(OUT, "_new.bytes")
    bm.write_models(models, tmp)
    nd = open(tmp, "rb").read(); os.remove(tmp)
    with open(os.path.join(OUT, "models.bytes"), "wb") as f:
        f.write(b"F2MD"); f.write(struct.pack("<HH", 1, len(old) + len(models)))
        for _, b in old: f.write(b)
        f.write(nd[8:])
    for mid, m in models.items():
        print(f"{mid}: " + ", ".join(f"{len(l[0])}v/{len(l[4])}t" for l in m["lods"]) + f"  h={m['height']:.1f} r={m['radius']:.1f}")
    for fn in ("models.bytes", "atlas.bytes"):
        print(fn, os.path.getsize(os.path.join(OUT, fn)), "bytes")
    # preview sheet
    prev = os.path.join(bm.REPO, "Docs", "env-previews", "park_models.jpg")
    af = out_px.astype(np.float32) / 255.0
    cw, ch = 300, 400
    sheet = Image.new("RGB", (cw * 3, ch * (len(names) + 1)), (150, 190, 230))
    for row, mid in enumerate(list(names) + ["props"]):
        for li in range(3):
            if mid == "props":
                if li > 1: continue
                pm = models[list(PROPS)[li]]; V, U, N, A, F = pm["lods"][0]; H = pm["height"] * 1.3; R = max(pm["radius"], H / 2) * 1.2
            else:
                pm = models[mid]; V, U, N, A, F = pm["lods"][li]; H = pm["height"] * 1.02; R = pm["radius"] * 1.05
            asp = cw / ch; span = max(2 * R, H * asp)
            img = bm.rasterize(V, U, N, A, F, af, cw, ch, 30, True, (-span / 2, span / 2), (0, span / asp), ss=2)
            al = img[..., 3:4]; sky = np.linspace(0, 1, ch)[:, None, None]
            bg = (np.array([120, 165, 225]) * (1 - sky) + np.array([200, 220, 240]) * sky) * np.ones((ch, cw, 3))
            comp = bg * (1 - al) + np.clip(img[..., :3], 0, 1) * 255 * al
            sheet.paste(Image.fromarray(np.clip(comp, 0, 255).astype(np.uint8), "RGB"), (li * cw, row * ch))
    os.makedirs(os.path.dirname(prev), exist_ok=True)
    sheet.save(prev, quality=85); print("preview", prev)


if __name__ == "__main__":
    main()
