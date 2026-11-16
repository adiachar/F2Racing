#!/usr/bin/env python3
"""
Offline model pipeline for F2Racing: Poly Haven (CC0) photoscans -> mobile-sized meshes + ONE shared 2048x2048 atlas.

    python3 Tools/Models/download.py /tmp/ph_models      # gltf + 1k diffuse / alpha maps
    python3 Tools/Models/build_models.py /tmp/ph_models  # writes Assets/Resources/Models/{models,atlas}.bytes + preview

What it does, per model
  * trees / shrubs: the photoscan leaves are millions of tiny alpha cards. They are clustered (k-means) into LEAF CLUMPS,
    one camera-facing-outward quad per clump textured with a clump tile painted from the model's own leaf texture.
    Wood (trunk + branches) is welded and quadric-decimated, with its own bark strip in the atlas.
      LOD0 <= 2.5k tris (wood ~500 + clumps), LOD1 <= 600 tris, LOD2 = two crossed impostor cards (4 tris) rendered
      offline from LOD0 (numpy rasteriser, front + side view, albedo x ambient occlusion only: the game's baked light
      is applied on top).
  * props (street lamps): welded, decimated to <= 500 tris, texture resized into the atlas.
  * vertex colour (ao) = ambient occlusion byte (inner clumps darker); normals of leaves are bent outwards / upwards
    from the crown centre so that the baked lighting reads a crown as a rounded volume.

ATLAS (2048 x 2048)
    y   0..767   clump tiles 256x256, 3 variants per species (8 per row)
    y 768..1023  impostor cards 192x256, two per tree (front, side)
    y 1024..1279 bark strips 128x256 (x = 128*tree), lamp tiles 256x256 (x = 640 + 256*lamp)
  atlas.bytes  = "F2AT" | u32 jpgLen | RGB jpeg | u32 pngLen | 8-bit alpha png   (colour dilated into transparent texels)

models.bytes (little endian)
    "F2MD" u16 version(=1) u16 nModels
    per model: u8 nameLen, name utf8, u8 kind (0 tree, 1 shrub, 2 prop), u8 nLods, u16 0,
               f32 height, f32 radius, f32 leaf mean colour linear rgb (0 for props),
               f32 bboxMin[3], f32 bboxSize[3]
      per LOD: u32 nVerts, u32 nTris,
               nVerts x { i16 x,y,z   (position = bboxMin + (q + 32768) / 65535 * bboxSize),
                          i8 nx,ny,nz (/127), u8 ao, u16 u,v (/65535, Unity convention: v up) },
               nTris x 3 x u16 indices (front faces clockwise: Unity winding)
  Coordinates are Unity's: the glTF x axis is negated on export (right-handed -> left-handed), metres, y up, the
  trunk base centred at the origin.
"""
import glob, io, json, math, os, struct, sys
import numpy as np
from PIL import Image
import trimesh, fast_simplification
from scipy import ndimage
from scipy.cluster.vq import kmeans2

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
SRC = sys.argv[1] if len(sys.argv) > 1 else "/tmp/ph_models"
OUT = os.path.join(REPO, "Assets", "Resources", "Models")
PREVIEW = os.path.join(REPO, "Docs", "env-previews", "models.jpg")
AT = 2048
rng = np.random.default_rng(1234)

# name: kind, wood tris (LOD0, LOD1), clumps (LOD0, LOD1), sprite size relative to the 256 tile, clump quad scale
SPEC = {
    "tree_small_02":  dict(kind=0, w=(520, 110), k=(880, 230), sprite=0.10, qs=3.9),
    "jacaranda_tree": dict(kind=0, w=(560, 120), k=(860, 230), sprite=0.30, qs=5.0, flat=True),
    "island_tree_01": dict(kind=0, w=(520, 110), k=(880, 230), sprite=0.13, qs=3.9),
    "island_tree_02": dict(kind=0, w=(520, 110), k=(880, 230), sprite=0.13, qs=3.9),
    "island_tree_03": dict(kind=0, w=(500, 110), k=(880, 230), sprite=0.13, qs=3.9),
    "shrub_02":       dict(kind=1, w=(0, 0), k=(110, 0), sprite=0.24, qs=6.0),
    "shrub_03":       dict(kind=1, w=(0, 0), k=(90, 0), sprite=0.24, qs=6.0),
    "street_lamp_01": dict(kind=2, w=(480, 0), k=(0, 0)),
    "street_lamp_02": dict(kind=2, w=(420, 0), k=(0, 0)),
}
TREES = [n for n, s in SPEC.items() if s["kind"] == 0]
SPECIES = [n for n, s in SPEC.items() if s["kind"] in (0, 1)]
LAMPS = [n for n, s in SPEC.items() if s["kind"] == 2]
CLUMP_VARIANTS = 3


# ------------------------------------------------------------------------------------------------ atlas
class Atlas:
    def __init__(self):
        self.px = np.zeros((AT, AT, 4), np.uint8)

    def put(self, img, x, y):
        a = np.asarray(img.convert("RGBA"))
        self.px[y:y + a.shape[0], x:x + a.shape[1]] = a

    def get(self, x, y, w, h):
        return self.px[y:y + h, x:x + w]


def clump_rect(sp, v):
    i = sp * CLUMP_VARIANTS + v
    return (i % 8) * 256, (i // 8) * 256, 256, 256


def card_rect(tree, view):
    return (tree * 2 + view) * 192, 768, 192, 256


def bark_rect(tree):
    return tree * 128, 1024, 128, 256


def lamp_rect(l):
    return 640 + l * 256, 1024, 256, 256


def uv_of(rect, fx, fy, inset=1.5):
    """pixel-fraction (fx, fy from the top-left of the rect) -> Unity atlas uv"""
    x, y, w, h = rect
    px = x + inset + fx * (w - 2 * inset)
    py = y + inset + fy * (h - 2 * inset)
    return px / AT, 1.0 - py / AT


# ------------------------------------------------------------------------------------------------ loading
def load_alpha(path):
    a = np.array(Image.open(path))
    if a.ndim == 3:
        a = a[..., 0]
    if a.dtype == np.uint16:
        a = (a / 257.0).astype(np.uint8)
    return a


def load_model(mid):
    sc = trimesh.load(glob.glob(f"{SRC}/{mid}/{mid}.gltf")[0], force="scene")
    wood, leaf, bulb, uvs_w = [], [], [], []
    for node in sc.graph.nodes_geometry:
        T, gname = sc.graph[node]
        g = sc.geometry[gname].copy()
        g.apply_transform(T)
        mat = (getattr(g.visual, "material", None).name or "").lower() if hasattr(g.visual, "material") else ""
        uv = np.array(g.visual.uv) if g.visual.kind == "texture" and g.visual.uv is not None else np.zeros((len(g.vertices), 2))
        item = (np.asarray(g.vertices, np.float64), np.asarray(g.faces, np.int64), uv)
        if "glass" in mat:
            continue
        if "bulb" in mat:
            bulb.append(item)
        elif "leaves" in mat or (SPEC[mid]["kind"] == 1):
            leaf.append(item)
        else:
            wood.append(item)
    return wood, leaf, bulb


def cat(items):
    if not items:
        return np.zeros((0, 3)), np.zeros((0, 3), np.int64), np.zeros((0, 2))
    off, V, F, U = 0, [], [], []
    for v, f, u in items:
        V.append(v); F.append(f + off); U.append(u); off += len(v)
    return np.vstack(V), np.vstack(F), np.vstack(U)


def weld(V, F, U=None, q=1e-4):
    key = np.round(V / q).astype(np.int64)
    _, idx, inv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    inv = inv.reshape(-1)
    V2 = V[idx]; F2 = inv[F]
    ok = (F2[:, 0] != F2[:, 1]) & (F2[:, 1] != F2[:, 2]) & (F2[:, 0] != F2[:, 2])
    return V2, F2[ok], (U[idx] if U is not None else None)


def decimate(V, F, target):
    """photoscan wood is thousands of loose pieces (trunk, limbs, twigs). Rank the pieces by size, give the big ones a
    share of the triangle budget and quadric-simplify each one on its own (a simplifier cannot join separate pieces,
    and a grid merge eats thin trunks); the small ones are dropped."""
    from scipy.sparse import coo_matrix
    from scipy.sparse.csgraph import connected_components
    n = len(V)
    e = np.vstack([F[:, [0, 1]], F[:, [1, 2]]])
    nc, lab = connected_components(coo_matrix((np.ones(len(e)), (e[:, 0], e[:, 1])), shape=(n, n)), directed=False)
    fl = lab[F[:, 0]]
    order = np.argsort(fl, kind="stable"); fs = F[order]; fls = fl[order]
    starts = np.searchsorted(fls, np.arange(nc)); ends = np.searchsorted(fls, np.arange(nc), side="right")
    comps = []
    for c in range(nc):
        if ends[c] - starts[c] < 24: continue
        ff = fs[starts[c]:ends[c]]
        vv = V[ff.reshape(-1)]
        diag = float(np.linalg.norm(vv.max(0) - vv.min(0)))
        comps.append((diag * math.sqrt(len(ff)), c, ff))
    comps.sort(key=lambda t: -t[0])
    budget = int(target); outV, outF, off = [], [], 0
    for rank, (_, c, ff) in enumerate(comps):
        if budget < 8: break
        want = int(budget * 0.45) if rank == 0 else int(min(budget, max(12, min(70, len(ff) * 0.06))))
        want = max(8, min(want, budget))
        u, inv = np.unique(ff.reshape(-1), return_inverse=True)
        v2, f2 = V[u], inv.reshape(-1, 3)
        if len(f2) > want:
            try:
                v3, f3 = fast_simplification.simplify(v2.astype(np.float32), f2.astype(np.int32), target_count=want, agg=7)
                v2, f2 = v3.astype(np.float64), f3.astype(np.int64)
            except Exception:
                pass
        if len(f2) > want * 1.6 or len(f2) == 0:
            continue
        budget -= len(f2)
        outV.append(v2); outF.append(f2 + off); off += len(v2)
    if not outV:
        return V[:3], F[:1]
    return np.vstack(outV), np.vstack(outF)


def vnormals(V, F):
    m = trimesh.Trimesh(V, F, process=False)
    n = np.array(m.vertex_normals)
    n[~np.isfinite(n).all(1)] = (0, 1, 0)
    return n


# ------------------------------------------------------------------------------------------------ clump tiles
def sprites_from(rgb, alpha):
    mask = alpha > 128
    lab, n = ndimage.label(mask, structure=np.ones((3, 3)))
    out = []
    for i, sl in enumerate(ndimage.find_objects(lab)):
        h, w = sl[0].stop - sl[0].start, sl[1].stop - sl[1].start
        m = lab[sl] == i + 1
        if m.sum() < 220:
            continue
        if max(h, w) > 520:  # a big connected mass: cut it in windows
            for y0 in range(0, h, 300):
                for x0 in range(0, w, 300):
                    mm = m[y0:y0 + 300, x0:x0 + 300]
                    if mm.sum() > 4000:
                        out.append((rgb[sl][y0:y0 + 300, x0:x0 + 300], mm))
            continue
        out.append((rgb[sl], m))
    return out


def prep_sprite(rgb, m, bright):
    m2 = ndimage.binary_erosion(m, iterations=1)
    if m2.sum() < 60:
        m2 = m
    idx = ndimage.distance_transform_edt(~m2, return_distances=False, return_indices=True)
    filled = rgb[idx[0], idx[1]].astype(np.float32) * bright
    a = (ndimage.gaussian_filter(m2.astype(np.float32), 0.6) * 255).astype(np.uint8)
    img = np.dstack([np.clip(filled, 0, 255).astype(np.uint8), a])
    return Image.fromarray(img, "RGBA")


def make_clump_tile(sprites, rel, T=256, seed=0):
    r = np.random.default_rng(seed)
    tile = Image.new("RGBA", (T, T), (0, 0, 0, 0))
    R = 0.40 * T
    order = []
    for _ in range(int(60 + 3.2 / (rel * rel) * 0.5)):
        ang = r.uniform(0, 2 * math.pi); rad = math.sqrt(r.uniform(0, 1)) * 0.78
        order.append((rad, ang))
    order.sort(key=lambda t: t[0])  # inner first (dark), outer last (light, on top)
    for k, (rad, ang) in enumerate(order):
        rgb, m = sprites[int(r.integers(len(sprites)))]
        cx, cy = T / 2 + math.cos(ang) * rad * R, T / 2 + math.sin(ang) * rad * R
        up_bias = 0.5 - cy / T  # brighter towards the top
        bright = float(np.clip(0.52 + 0.5 * rad + 0.35 * up_bias + r.uniform(-0.08, 0.08), 0.35, 1.25))
        sp = prep_sprite(rgb, m, bright)
        size = max(10, int(T * rel * r.uniform(0.7, 1.15)))
        sc = size / max(sp.size)
        sp = sp.resize((max(2, int(sp.size[0] * sc)), max(2, int(sp.size[1] * sc))), Image.LANCZOS)
        sp = sp.rotate(r.uniform(0, 360), expand=True, resample=Image.BICUBIC)
        w, h = sp.size
        x, y = int(cx - w / 2), int(cy - h / 2)
        x = int(np.clip(x, 3, T - 3 - w)); y = int(np.clip(y, 3, T - 3 - h))
        tile.alpha_composite(sp, (x, y))
    # fill gaps: sparse sprites (fronds, thin shrub leaves) leave holes; add more where the core is still empty
    yy0, xx0 = np.mgrid[0:T, 0:T]
    core = np.hypot(xx0 - T / 2, yy0 - T / 2) < 0.34 * T
    for it in range(260):
        al = np.asarray(tile)[..., 3]
        gaps = np.argwhere(core & (al < 40))
        if len(gaps) < 0.07 * core.sum(): break
        gy, gx = gaps[int(r.integers(len(gaps)))]
        rgb, m = sprites[int(r.integers(len(sprites)))]
        sp = prep_sprite(rgb, m, float(r.uniform(0.6, 1.0)))
        sc = max(10, int(T * rel * r.uniform(0.7, 1.15))) / max(sp.size)
        sp = sp.resize((max(2, int(sp.size[0] * sc)), max(2, int(sp.size[1] * sc))), Image.LANCZOS).rotate(r.uniform(0, 360), expand=True, resample=Image.BICUBIC)
        w, h = sp.size
        tile.alpha_composite(sp, (int(np.clip(gx - w / 2, 3, T - 3 - w)), int(np.clip(gy - h / 2, 3, T - 3 - h))))
    # soft vignette on the rim so the cut-out reads as a clump, not a square
    a = np.asarray(tile).astype(np.float32)
    yy, xx = np.mgrid[0:T, 0:T]
    d = np.hypot(xx - T / 2 + 0.5, yy - T / 2 + 0.5) / (T / 2)
    a[..., 3] *= np.clip((1.0 - d) * 9.0, 0, 1)
    return Image.fromarray(a.astype(np.uint8), "RGBA")


def tile_mean_linear(img):
    a = np.asarray(img).astype(np.float64)
    w = a[..., 3:4] / 255.0
    c = ((a[..., :3] / 255.0) ** 2.2 * w).sum((0, 1)) / max(w.sum(), 1e-6)
    return c


# ------------------------------------------------------------------------------------------------ geometry builders
def quad_geo(c, out, size, roll, rect_uv, ao):
    """one leaf-clump quad centred on c, facing along out. returns 4 verts, 4 uv, normals"""
    out = out / (np.linalg.norm(out) + 1e-9)
    ref = np.array([0, 1, 0]) if abs(out[1]) < 0.95 else np.array([1, 0, 0])
    u = np.cross(ref, out); u /= np.linalg.norm(u)
    v = np.cross(out, u)
    cr, sr = math.cos(roll), math.sin(roll)
    u2, v2 = u * cr + v * sr, -u * sr + v * cr
    h = size / 2
    P = [c - u2 * h - v2 * h, c + u2 * h - v2 * h, c + u2 * h + v2 * h, c - u2 * h + v2 * h]
    return P


def kmeans(P, K, seed, iters=10):
    """Lloyd's k-means with a k-d tree assignment (scipy's kmeans2 is far too slow for 1500 clusters)"""
    from scipy.spatial import cKDTree
    r = np.random.default_rng(seed)
    C = P[r.choice(len(P), K, replace=False)].copy()
    for _ in range(iters):
        lab = cKDTree(C).query(P)[1]
        cnt = np.bincount(lab, minlength=K).astype(np.float64)
        for d in range(3):
            C[:, d] = np.where(cnt > 0, np.bincount(lab, weights=P[:, d], minlength=K) / np.maximum(cnt, 1), C[:, d])
    return C, cKDTree(C).query(P)[1]


def build_clumps(leaf_pts, K, qs, sp_idx, crown_c, height, shrub=False, seed=0, flat=False):
    r = np.random.default_rng(seed)
    n = len(leaf_pts)
    samp = leaf_pts[r.choice(n, min(n, 150000), replace=False)]
    Kc = int(K * (1.0 if shrub else 1.7))
    cent, lab = kmeans(samp, Kc, int(seed) + 7)
    ok = np.bincount(lab, minlength=Kc) > 3
    rms = np.zeros(Kc)
    for k in range(Kc):
        if ok[k]:
            d = samp[lab == k] - cent[k]
            rms[k] = math.sqrt((d ** 2).sum(1).mean())
    # exposure: radius against the crown's largest radius in that direction
    rel = samp - crown_c
    rr = np.linalg.norm(rel, axis=1) + 1e-6
    az = ((np.arctan2(rel[:, 2], rel[:, 0]) / (2 * math.pi) + 0.5) * 24).astype(int) % 24
    el = np.clip(((rel[:, 1] / rr) * 0.5 + 0.5) * 8, 0, 7).astype(int)
    rmax = np.zeros((24, 8))
    np.maximum.at(rmax, (az, el), rr)
    rmax = np.maximum(rmax, np.percentile(rr, 60))
    cr = cent - crown_c
    crr = np.linalg.norm(cr, axis=1) + 1e-6
    caz = ((np.arctan2(cr[:, 2], cr[:, 0]) / (2 * math.pi) + 0.5) * 24).astype(int) % 24
    cel = np.clip(((cr[:, 1] / crr) * 0.5 + 0.5) * 8, 0, 7).astype(int)
    depth = np.clip(crr / rmax[caz, cel], 0, 1.2)
    if shrub or flat: depth = np.clip(depth, 0.55, 1.2) + r.uniform(0, 0.1, len(depth))  # thin canopies: no inside to hide
    sel = np.argsort(-depth)
    sel = [k for k in sel if ok[k]][:K]
    med = np.median(rms[sel])
    verts, uvs, norms, aos, tris = [], [], [], [], []
    for k in sel:
        c = cent[k]
        out = c - crown_c
        out = out / (np.linalg.norm(out) + 1e-9)
        face = out + np.array([0, 0.3, 0]) + r.normal(0, 0.18, 3)
        size = float(np.clip(rms[k], 0.6 * med, 1.8 * med)) * qs
        P = quad_geo(c, face, size, r.uniform(-0.8, 0.8), None, 1.0)
        ao = float(0.45 + 0.55 * np.clip((depth[k] - 0.35) / 0.65, 0, 1) ** 0.8)
        if shrub:
            ao *= 0.8 + 0.2 * np.clip(c[1] / max(height, 1e-3), 0, 1)
        else:
            ao *= 0.88 + 0.12 * np.clip(c[1] / max(height, 1e-3), 0, 1)
        ns = out * 0.8 + np.array([0, 0.55, 0])
        ns /= np.linalg.norm(ns)
        var = int(r.integers(CLUMP_VARIANTS)); rect = clump_rect(sp_idx, var)
        flip = r.random() < 0.5
        base = len(verts)
        corner_uv = [(0, 1), (1, 1), (1, 0), (0, 0)]
        for ci, p in enumerate(P):
            fx, fy = corner_uv[ci]
            if flip: fx = 1 - fx
            uvs.append(uv_of(rect, fx, fy, 0.0))
            verts.append(p); norms.append(ns); aos.append(ao)
        tris += [(base, base + 2, base + 1), (base, base + 3, base + 2)]
    return np.array(verts), np.array(uvs), np.array(norms), np.array(aos), np.array(tris, np.int64)


def wood_geo(V, F, tree_idx, height):
    """bark uv: u = |azimuth| about the trunk axis (mirrored: no seam), v = height; dark moss at the foot"""
    rect = bark_rect(tree_idx)
    az = np.arctan2(V[:, 2], V[:, 0])
    fx = np.clip(np.abs(az) / math.pi, 0, 1)
    fy = 1.0 - np.clip(V[:, 1] / max(height, 1e-3), 0, 1)
    uv = np.array([uv_of(rect, a, b, 2.0) for a, b in zip(fx, fy)])
    n = vnormals(V, F)
    ao = 0.78 + 0.22 * np.clip(V[:, 1] / 1.5, 0, 1)
    return V, uv, n, ao, F


def merge_parts(parts):
    V, U, N, A, F, off = [], [], [], [], [], 0
    for v, u, n, a, f in parts:
        if len(v) == 0: continue
        V.append(v); U.append(u); N.append(n); A.append(a); F.append(f + off); off += len(v)
    return np.vstack(V), np.vstack(U), np.vstack(N), np.concatenate(A), np.vstack(F)


# ------------------------------------------------------------------------------------------------ rasteriser
def rasterize(V, U, N, A, F, atlas_f, W, H, view_az, lit, xr, yr, ss=2, bg=None):
    """orthographic render. view_az degrees about y. xr = (xmin, xmax), yr = (ymin, ymax) of the frame in view space.
    returns (H, W, 4) float (premultiplied-free rgb, alpha 0/1 coverage after ss downsampling)."""
    a = math.radians(view_az); ca, sa = math.cos(a), math.sin(a)
    R = np.array([[ca, 0, sa], [0, 1, 0], [-sa, 0, ca]])
    P = V @ R.T
    Nn = N @ R.T
    Ws, Hs = W * ss, H * ss
    sx = Ws / (xr[1] - xr[0]); sy = Hs / (yr[1] - yr[0])
    X = (P[:, 0] - xr[0]) * sx
    Y = (yr[1] - P[:, 1]) * sy
    Z = -P[:, 2]  # larger = nearer when looking along -z... we look from +z towards -z: nearer has larger z
    Z = P[:, 2]
    img = np.zeros((Hs, Ws, 4), np.float32)
    zb = np.full((Hs, Ws), -1e9, np.float32)
    L = np.array([-0.45, 0.75, 0.5]); L /= np.linalg.norm(L)
    AH, AW = atlas_f.shape[:2]
    for f in F:
        i0, i1, i2 = f
        x0, y0, x1, y1, x2, y2 = X[i0], Y[i0], X[i1], Y[i1], X[i2], Y[i2]
        xmin = int(max(0, math.floor(min(x0, x1, x2)))); xmax = int(min(Ws - 1, math.ceil(max(x0, x1, x2))))
        ymin = int(max(0, math.floor(min(y0, y1, y2)))); ymax = int(min(Hs - 1, math.ceil(max(y0, y1, y2))))
        if xmin > xmax or ymin > ymax: continue
        den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
        if abs(den) < 1e-9: continue
        gx, gy = np.meshgrid(np.arange(xmin, xmax + 1) + 0.5, np.arange(ymin, ymax + 1) + 0.5)
        w0 = ((y1 - y2) * (gx - x2) + (x2 - x1) * (gy - y2)) / den
        w1 = ((y2 - y0) * (gx - x2) + (x0 - x2) * (gy - y2)) / den
        w2 = 1 - w0 - w1
        m = (w0 >= -1e-4) & (w1 >= -1e-4) & (w2 >= -1e-4)
        if not m.any(): continue
        z = w0 * Z[i0] + w1 * Z[i1] + w2 * Z[i2]
        zsub = zb[ymin:ymax + 1, xmin:xmax + 1]
        m &= z > zsub
        if not m.any(): continue
        u = w0 * U[i0, 0] + w1 * U[i1, 0] + w2 * U[i2, 0]
        v = w0 * U[i0, 1] + w1 * U[i1, 1] + w2 * U[i2, 1]
        tx = np.clip(u * AW - 0.5, 0, AW - 1.001); ty = np.clip((1 - v) * AH - 0.5, 0, AH - 1.001)
        ix, iy = tx.astype(int), ty.astype(int); fx, fy = tx - ix, ty - iy
        def S(dx, dy): return atlas_f[iy + dy, ix + dx]
        c = (S(0, 0) * ((1 - fx) * (1 - fy))[..., None] + S(1, 0) * (fx * (1 - fy))[..., None]
             + S(0, 1) * ((1 - fx) * fy)[..., None] + S(1, 1) * (fx * fy)[..., None])
        m &= c[..., 3] > 0.5
        if not m.any(): continue
        ao = w0 * A[i0] + w1 * A[i1] + w2 * A[i2]
        rgb = c[..., :3] * ao[..., None]
        if lit:
            n = w0[..., None] * Nn[i0] + w1[..., None] * Nn[i1] + w2[..., None] * Nn[i2]
            n /= np.linalg.norm(n, axis=2, keepdims=True) + 1e-9
            nl = np.maximum(0, n @ (R @ L))
            rgb = rgb * (0.42 + 0.78 * nl)[..., None]
        sub = img[ymin:ymax + 1, xmin:xmax + 1]
        sub[m, :3] = rgb[m]; sub[m, 3] = 1.0
        zsub[m] = z[m]
    # downsample premultiplied
    a = img[..., 3:4]
    pm = (img[..., :3] * a).reshape(H, ss, W, ss, 3).sum((1, 3))
    asum = a.reshape(H, ss, W, ss, 1).sum((1, 3))
    rgb = pm / np.maximum(asum, 1e-6)
    return np.dstack([rgb, asum[..., 0] / (ss * ss)])


# ------------------------------------------------------------------------------------------------ main build
def srgb_img(path, size=None):
    im = Image.open(path).convert("RGB")
    return im.resize(size, Image.LANCZOS) if size else im


def export(V, N):
    V = V.copy(); N = N.copy()
    V[:, 0] *= -1; N[:, 0] *= -1  # glTF (right-handed) -> Unity (left-handed): mirror x, keep the winding
    return V, N


def main():
    atlas = Atlas()
    models = {}
    report = []
    sp_index = {n: i for i, n in enumerate(SPECIES)}
    tree_index = {n: i for i, n in enumerate(TREES)}

    for mid, sp in SPEC.items():
        print("==", mid, flush=True)
        wood, leaf, bulb = load_model(mid)
        Vw, Fw, Uw = cat(wood)
        Vl, Fl, _ = cat(leaf)
        Vb, Fb, Ub = cat(bulb)
        kind = sp["kind"]
        info = dict(kind=kind, lods=[])
        tex = f"{SRC}/{mid}/textures"

        if kind == 2:  # ---- prop
            Vw, Fw, Uw = weld(Vw, Fw, Uw)
            # shift so the base centre (lowest points) is the origin
            low = Vw[:, 1] < Vw[:, 1].min() + 0.05
            sh = np.array([Vw[low, 0].mean(), Vw[:, 1].min(), Vw[low, 2].mean()])
            Vw = Vw - sh
            if len(Vb): Vb = Vb - sh
            Vd, Fd = decimate(Vw, Fw, sp["w"][0])
            # uvs of decimated verts: nearest source vertex
            from scipy.spatial import cKDTree
            idx = cKDTree(Vw).query(Vd)[1]
            Ud = Uw[idx]
            li = LAMPS.index(mid); rect = lamp_rect(li)
            img = srgb_img(glob.glob(f"{tex}/{mid}_diff_1k.jpg")[0], (256, 256)).convert("RGBA")
            arr = np.array(img); arr[240:256, 240:256] = (255, 238, 196, 255); img = Image.fromarray(arr)
            atlas.put(img, rect[0], rect[1])
            uv = np.array([uv_of(rect, u_ % 1.0, 1.0 - (v_ % 1.0), 1.5) for u_, v_ in Ud])
            n = vnormals(Vd, Fd)
            ao = np.ones(len(Vd)) * 0.95
            parts = [(Vd, uv, n, ao, Fd)]
            if len(Vb):  # the bulb: constant warm white (the bright corner of the tile)
                Vb2, Fb2, _ = weld(Vb, Fb, Ub)
                Vb2, Fb2 = decimate(Vb2, Fb2, 24)
                parts.append((Vb2, np.tile(np.array(uv_of(rect, 0.97, 0.97, 0.0)), (len(Vb2), 1)), vnormals(Vb2, Fb2), np.ones(len(Vb2)), Fb2))
            V, U, N, A, F = merge_parts(parts)
            lods = [(V, U, N, A, F)]
            height = float(V[:, 1].max()); radius = float(np.hypot(V[:, 0], V[:, 2]).max())
            leafc = (0, 0, 0)
        else:  # ---- tree / shrub
            si = sp_index[mid]
            # normalise: trunk base centre at the origin
            if kind == 0:
                Vw_, Fw_, _ = weld(Vw, Fw)
                low = Vw_[:, 1] < Vw_[:, 1].min() + 0.4
                cx, cz, y0 = Vw_[low, 0].mean(), Vw_[low, 2].mean(), Vw_[:, 1].min()
            else:
                cx, cz, y0 = (Vl[:, 0].min() + Vl[:, 0].max()) / 2, (Vl[:, 2].min() + Vl[:, 2].max()) / 2, Vl[:, 1].min()
            for arr in (Vw, Vl):
                if len(arr): arr[:, 0] -= cx; arr[:, 2] -= cz; arr[:, 1] -= y0
            # ---- clump tiles from the model's own leaf texture
            if kind == 0:
                dif = np.array(Image.open(f"{tex}/{mid}_leaves_diff_1k.jpg").convert("RGB"))
                alp = load_alpha(f"{tex}/{mid}_leaves_alpha_1k.png")
            else:
                dif = np.array(Image.open(f"{tex}/{mid}_diff_1k.jpg").convert("RGB"))
                alp = load_alpha(f"{tex}/{mid}_alpha_1k.png")
            sprites = sprites_from(dif, alp)
            print("   sprites", len(sprites), flush=True)
            tiles = []
            for v in range(CLUMP_VARIANTS):
                t = make_clump_tile(sprites, sp["sprite"], 256, seed=sp_index[mid] * 31 + v)
                x, y, _, _ = clump_rect(si, v)
                atlas.put(t, x, y); tiles.append(t)
            leafc = tuple(float(c) for c in tile_mean_linear(tiles[0]))
            leaf_h = Vl[:, 1].max()
            # leaf points = triangle centroids
            pts = Vl[Fl].mean(1)
            crown_c = pts.mean(0)
            height = float(max(Vl[:, 1].max(), Vw[:, 1].max() if len(Vw) else 0))
            lods = []
            if kind == 0:
                # bark strip
                tri = tree_index[mid]
                bf = glob.glob(f"{tex}/{mid}_trunk_diff_1k.jpg") or glob.glob(f"{tex}/{mid}_diff_1k.jpg")
                bim = Image.open(bf[0]).convert("RGB").crop((384, 64, 640, 960)).resize((128, 256), Image.LANCZOS)
                atlas.put(bim, *bark_rect(tri)[:2])
                Vw_w, Fw_w, _ = weld(Vw, Fw)
                for lod in (0, 1):
                    Vd, Fd = decimate(Vw_w, Fw_w, sp["w"][lod])
                    wp = wood_geo(Vd, Fd, tri, height)
                    cp = build_clumps(pts, sp["k"][lod], sp["qs"] * (1.0 if lod == 0 else 1.55), si, crown_c, height, False, seed=11 + lod, flat=sp.get("flat", False))
                    lods.append(merge_parts([wp, cp]))
                    print(f"   LOD{lod}: wood {len(Fd)} tris, clumps {len(cp[4])} tris, total {len(lods[-1][4])}", flush=True)
            else:
                cp = build_clumps(pts, sp["k"][0], sp["qs"], si, crown_c, height, True, seed=5)
                lods.append(merge_parts([cp]))
                print(f"   LOD0: {len(lods[-1][4])} tris", flush=True)
            radius = float(np.hypot(Vl[:, 0], Vl[:, 2]).max()) if kind == 0 else float(np.hypot(lods[0][0][:, 0], lods[0][0][:, 2]).max())
            radius = float(min(radius, np.percentile(np.hypot(lods[0][0][:, 0], lods[0][0][:, 2]), 98) * 1.05))
        info.update(lods=lods, height=height, radius=radius, leaf=leafc)
        models[mid] = info

    # ---------------------------------------------------------------- impostors (needs the tiles in the atlas)
    atlas_f = atlas.px.astype(np.float32) / 255.0
    atlas_f[..., :3] = np.where(atlas_f[..., 3:4] > 0, atlas_f[..., :3], 0)
    # colour dilation first, so the rasteriser's bilinear taps never see black
    dil = dilate_atlas(atlas.px)
    atlas_f = dil.astype(np.float32) / 255.0
    atlas_f[..., :3] = atlas_f[..., :3]  # (sRGB values, kept as is: the card is composited in the same space)
    for mid in TREES:
        m = models[mid]; V, U, N, A, F = m["lods"][0]
        tri = tree_index[mid]
        R = m["radius"] * 1.04; H = m["height"] * 1.01
        cards = []
        for view, az in enumerate((0, 90)):
            x, y, w, h = card_rect(tri, view)
            img = rasterize(V, U, N, A, F, atlas_f, w, h, az, False, (-R, R), (0, H), ss=2)
            # mean over the trunk's x gives a tiny gutter: keep as is
            rgba = np.dstack([np.clip(img[..., :3] * 255, 0, 255), np.clip(img[..., 3:4] * 255, 0, 255)]).astype(np.uint8)
            atlas.put(Image.fromarray(rgba, "RGBA"), x, y)
            cards.append(card_rect(tri, view))
        # LOD2: two crossed cards
        Vc, Uc, Nc, Ac, Fc = [], [], [], [], []
        for view, (cx_, cz_) in enumerate(((1, 0), (0, 1))):
            rect = card_rect(tri, view)
            # view 0 (az 0) looks along -z: card spans x; view 1 (az 90) spans z
            ax = np.array([1.0, 0, 0]) if view == 0 else np.array([0, 0, 1.0])
            base = len(Vc)
            corners = [(-R, 0), (R, 0), (R, H), (-R, H)]
            for ci, (dx, dy) in enumerate(corners):
                Vc.append(ax * dx + np.array([0, dy, 0]))
                fx = 0.0 if dx < 0 else 1.0; fy = 1.0 if dy == 0 else 0.0
                Uc.append(uv_of(rect, fx, fy, 0.0))
                side = ax * (-0.6 if dx < 0 else 0.6)
                up = 0.35 if dy == 0 else 0.8
                n = side + np.array([0, up, 0]); Nc.append(n / np.linalg.norm(n))
                Ac.append(0.9 if dy == 0 else 1.0)
            Fc += [(base, base + 2, base + 1), (base, base + 3, base + 2)]
        m["lods"].append((np.array(Vc), np.array(Uc), np.array(Nc), np.array(Ac), np.array(Fc, np.int64)))
        print("impostor", mid, flush=True)

    # ---------------------------------------------------------------- write files
    os.makedirs(OUT, exist_ok=True)
    atlas.px = dilate_atlas(atlas.px, keep_alpha=True)
    write_atlas(atlas.px, os.path.join(OUT, "atlas.bytes"))
    write_models(models, os.path.join(OUT, "models.bytes"))
    preview(models, atlas.px)
    for mid, m in models.items():
        report.append(f"{mid}: " + ", ".join(f"{len(l[0])}v/{len(l[4])}t" for l in m["lods"]) + f"  h={m['height']:.1f} r={m['radius']:.1f}")
    print("\n".join(report))
    for f in ("models.bytes", "atlas.bytes"):
        print(f, os.path.getsize(os.path.join(OUT, f)), "bytes")


def dilate_atlas(px, keep_alpha=True):
    """fill transparent texels with the colour of the nearest opaque one (no dark fringes in mips / bilinear taps)"""
    a = px[..., 3] > 8
    if not a.any(): return px
    idx = ndimage.distance_transform_edt(~a, return_distances=False, return_indices=True)
    out = px.copy()
    out[..., :3] = px[idx[0], idx[1], :3]
    if not keep_alpha: out[..., 3] = 255
    return out


def write_atlas(px, path):
    rgb = Image.fromarray(px[..., :3], "RGB")
    b1 = io.BytesIO(); rgb.save(b1, "JPEG", quality=88, subsampling=0, optimize=True)
    al = Image.fromarray(px[..., 3], "L")
    b2 = io.BytesIO(); al.save(b2, "PNG", optimize=True)
    with open(path, "wb") as f:
        f.write(b"F2AT"); f.write(struct.pack("<I", len(b1.getvalue()))); f.write(b1.getvalue())
        f.write(struct.pack("<I", len(b2.getvalue()))); f.write(b2.getvalue())


def write_models(models, path):
    out = io.BytesIO()
    out.write(b"F2MD"); out.write(struct.pack("<HH", 1, len(models)))
    for mid, m in models.items():
        lods = []
        for (V, U, N, A, F) in m["lods"]:
            Vx, Nx = export(V, N)
            lods.append((Vx, U, Nx, A, F))
        allv = np.vstack([l[0] for l in lods])
        bmin = allv.min(0); bsize = np.maximum(allv.max(0) - bmin, 1e-4)
        nb = mid.encode()
        out.write(struct.pack("<B", len(nb))); out.write(nb)
        out.write(struct.pack("<BBH", m["kind"], len(lods), 0))
        out.write(struct.pack("<ff3f", m["height"], m["radius"], *m["leaf"]))
        out.write(struct.pack("<3f3f", *bmin, *bsize))
        for (V, U, N, A, F) in lods:
            out.write(struct.pack("<II", len(V), len(F)))
            q = np.clip(np.round((V - bmin) / bsize * 65535 - 32768), -32768, 32767).astype("<i2")
            nn = N / (np.linalg.norm(N, axis=1, keepdims=True) + 1e-9)
            n8 = np.clip(np.round(nn * 127), -127, 127).astype(np.int8)
            ao = np.clip(np.round(A * 255), 0, 255).astype(np.uint8)
            uu = np.clip(np.round(U * 65535), 0, 65535).astype("<u2")
            rec = np.zeros(len(V), dtype=[("p", "<i2", 3), ("n", "i1", 3), ("a", "u1"), ("uv", "<u2", 2)])
            rec["p"] = q; rec["n"] = n8; rec["a"] = ao; rec["uv"] = uu
            out.write(rec.tobytes())
            out.write(F.astype("<u2").tobytes())
    open(path, "wb").write(out.getvalue())


def preview(models, px):
    """contact sheet: every tree LOD0 | LOD1 | impostor (lit, over a sky gradient), then shrubs and lamps"""
    atlas_f = px.astype(np.float32) / 255.0
    cols = []
    cw, ch = 300, 400
    names = list(models.keys())
    sheet = Image.new("RGB", (cw * 3, ch * (len(TREES) + 1)), (150, 190, 230))
    row = 0
    for mid in TREES:
        m = models[mid]
        for li in range(3):
            V, U, N, A, F = m["lods"][li]
            Vv, Nv = V.copy(), N.copy()
            R = m["radius"] * 1.05; H = m["height"] * 1.02
            asp = cw / ch
            span = max(2 * R, H * asp)
            img = rasterize(Vv, U, Nv, A, F, atlas_f, cw, ch, 30 if li < 2 else 30, True, (-span / 2, span / 2), (-(span / asp - H) / 2 * 0 - 0.0, span / asp), ss=2)
            rgb = np.clip(img[..., :3], 0, 1) * 255
            al = img[..., 3:4]
            sky = np.linspace(0, 1, ch)[:, None, None]
            bg = (np.array([120, 165, 225]) * (1 - sky) + np.array([200, 220, 240]) * sky) * np.ones((ch, cw, 3))
            comp = bg * (1 - al) + rgb * al
            tile = Image.fromarray(np.clip(comp, 0, 255).astype(np.uint8), "RGB")
            sheet.paste(tile, (li * cw, row * ch))
        row += 1
    for li, mid in enumerate(("shrub_02", "shrub_03", "street_lamp_01")):  # last row: two shrubs and a lamp, LOD0
        m = models[mid]; V, U, N, A, F = m["lods"][0]
        R = max(m["radius"], m["height"] * 0.5) * 1.1; asp = cw / ch; span = max(2 * R, m["height"] * 1.05 * asp)
        img = rasterize(V, U, N, A, F, atlas_f, cw, ch, 30, True, (-span / 2, span / 2), (-0.05, span / asp - 0.05), ss=2)
        al = img[..., 3:4]; sky = np.linspace(0, 1, ch)[:, None, None]
        bg = (np.array([120, 165, 225]) * (1 - sky) + np.array([200, 220, 240]) * sky) * np.ones((ch, cw, 3))
        comp = bg * (1 - al) + np.clip(img[..., :3], 0, 1) * 255 * al
        sheet.paste(Image.fromarray(np.clip(comp, 0, 255).astype(np.uint8), "RGB"), (li * cw, row * ch))
    os.makedirs(os.path.dirname(PREVIEW), exist_ok=True)
    sheet.save(PREVIEW, quality=88)
    print("preview", PREVIEW)


if __name__ == "__main__":
    main()
