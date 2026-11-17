# Off-line converter: the downloaded F1 steering-wheel glTF (Sketchfab, CC BY 4.0, see CREDITS.txt) -> the game's
# wheel mesh (Assets/Resources/Wheel/wheel.bytes), its albedo atlas (atlas.bytes) and CREDITS.txt.
# usage: python3 convert.py [gltf_dir] [out_dir]          (pip: pygltflib numpy pillow fast_simplification scipy trimesh rtree)
#  * every primitive flattened through the node tree, merged into one mesh in the wheel's own frame (SteeringWheel.cs:
#    metres, x right, y up, the driver at -z), scaled to 280 mm wide, the quick-release hub's axis on the pivot
#  * decimated per part (quadric, fast_simplification) to ~13k triangles (from 328k), UVs carried through the collapse map
#  * the four base-colour textures packed 2x2 into one 1024^2 atlas (PNG); the other parts get a flat colour
#  * per vertex as WheelGeo builds it: colour rgb = base (linear) a = baked occlusion; uv0 = display UV / LED number /
#    paddle hinge; uv1 = (clear coat, gloss, metal, kind); uv2 = (paddle side, textured); uv3 = atlas UV
# File (little endian): "F2WH" u32 version (1), u32 nv, u32 nt, 4 f32 screen rect (x0 y0 x1 y1), 3 f32 left grip axis, then
# per vertex (52 B): 3f pos, 3f normal, 4B colour (rgb gamma-encoded, a = AO), 2f uv0, 4B mat (coat gloss metal *255, kind),
# 1b paddle side, 1B textured, 2B pad, 2f atlas uv; then nt*3 u16 (u32 if nv >= 65536) indices.
import sys, os, io, struct, json
import numpy as np
from PIL import Image
from pygltflib import GLTF2
import fast_simplification
from scipy.spatial import cKDTree

SRC = sys.argv[1] if len(sys.argv) > 1 else '/home/claude/wheel_in'
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(os.path.abspath(__file__)), '../../Assets/Resources/Wheel')
TARGET_TRIS = 12000  # ~8k collapsed the thumbwheels and the small buttons
DETAIL = {'maincase': 1.5, 'screen_frame': 2.2, 'buttons': 2.8, 'handle': 1.4, 'handle2': 1.2, 'rotarywheel': 1.6, 'connector2': 0.4, 'connector': 0.5, 'gray': 0.6, 'toggle_back': 0.6, 'toggle_back2': 0.6}  # share weights
WIDTH_M = 0.280            # overall width (grips) of a real F1 wheel
HUB_Y, FACE_Z = -0.004, -0.045  # the hub axis height and the display plane (as the built wheel: SteeringWheel.cs)

# material name -> (kind, coat, gloss, metal, atlas slot or None, AO hint)   kinds: 0 plain 1 carbon 2 fabric 3 screen 4 LED
KPlain, KCarbon, KFabric, KScreen, KLed = 0, 1, 2, 3, 4
MATS = {
    'maincase':            (KCarbon, 0.45, 0.86, 0.05, 0),
    'buttons':             (KPlain,  0.30, 0.66, 0.00, 1),
    'rotary_sticker':      (KPlain,  0.25, 0.55, 0.00, 2),
    'rotarywheel':         (KPlain,  0.30, 0.70, 0.55, 3),
    'handle':              (KFabric, 0.05, 0.28, 0.00, None),
    'handle2':             (KFabric, 0.05, 0.32, 0.00, None),
    'gray':                (KCarbon, 0.45, 0.84, 0.05, None),
    'screen_frame':        (KPlain,  0.70, 0.93, 0.00, None),
    'screen':              (KScreen, 1.00, 0.97, 0.00, None),
    'led_indicator_top':   (KLed,    0.80, 0.95, 0.00, None),
    'led_indicator_left':  (KPlain,  0.80, 0.95, 0.00, None),
    'led_indicator_right': (KPlain,  0.80, 0.95, 0.00, None),
    'toggle':              (KPlain,  0.30, 0.78, 0.90, None),
    'toggle2':             (KPlain,  0.30, 0.62, 0.00, None),
    'toggle_back':         (KPlain,  0.30, 0.78, 0.90, None),
    'toggle_back2':        (KPlain,  0.30, 0.62, 0.00, None),
    'connector':           (KPlain,  0.30, 0.78, 0.90, None),
    'connector2':          (KPlain,  0.35, 0.72, 0.60, None),
    'knob':                (KPlain,  0.30, 0.62, 0.65, None),
    'knob_white':          (KPlain,  0.20, 0.50, 0.00, None),
    'knob_purple':         (KPlain,  0.35, 0.72, 0.60, None),
}
SKIP = {'glass'}                      # alpha-blended cover glass: the display's own gloss stands in for it
COLOUR = {                            # linear base colours where the source's flat colour is not what we want
    'led_indicator_top': (0.01, 0.01, 0.012), 'led_indicator_left': (0.02, 0.02, 0.022), 'led_indicator_right': (0.02, 0.02, 0.022),
    'screen': (0.003, 0.003, 0.004), 'screen_frame': (0.008, 0.008, 0.010), 'knob': (0.05, 0.05, 0.055), 'toggle': (0.30, 0.30, 0.32),
    'toggle_back': (0.30, 0.30, 0.32), 'connector': (0.30, 0.30, 0.32), 'connector2': (0.45, 0.03, 0.025),
}
SLOT = 512  # atlas cells (2 x 2)
PADDLES = 'Plane.006'

# ---------------------------------------------------------------- glTF reading
g = GLTF2().load(os.path.join(SRC, 'scene.gltf'))
blob = open(os.path.join(SRC, g.buffers[0].uri), 'rb').read()
CT = {5126: np.float32, 5125: np.uint32, 5123: np.uint16, 5121: np.uint8}; NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}

def acc(i):
    a = g.accessors[i]; bv = g.bufferViews[a.bufferView]; o = (bv.byteOffset or 0) + (a.byteOffset or 0)
    n = NC[a.type]; dt = np.dtype(CT[a.componentType]); st = bv.byteStride or n * dt.itemsize
    base = np.frombuffer(blob, dt, offset=o, count=((a.count - 1) * st + n * dt.itemsize) // dt.itemsize)
    return np.array(np.lib.stride_tricks.as_strided(base, shape=(a.count, n), strides=(st, dt.itemsize)))

def node_mat(n):
    if n.matrix: return np.array(n.matrix, float).reshape(4, 4).T
    M = np.eye(4)
    if n.scale: M = np.diag(list(n.scale) + [1.0]) @ M
    if n.rotation:
        x, y, z, w = n.rotation
        R = np.eye(4); R[:3, :3] = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)], [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)], [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
        M = R @ M
    if n.translation: T = np.eye(4); T[:3, 3] = n.translation; M = T @ M
    return M

prims = []  # (material name, P world, N world, UV or None, faces)
def walk(i, parent):
    n = g.nodes[i]; M = parent @ node_mat(n)
    if n.mesh is not None:
        for p in g.meshes[n.mesh].primitives:
            mname = g.materials[p.material].name
            if mname in SKIP: continue
            P = acc(p.attributes.POSITION).astype(np.float64); P = (np.c_[P, np.ones(len(P))] @ M.T)[:, :3]
            Nn = acc(p.attributes.NORMAL).astype(np.float64) @ np.linalg.inv(M[:3, :3])  # inverse transpose
            Nn /= np.linalg.norm(Nn, axis=1, keepdims=True) + 1e-12
            UV = acc(p.attributes.TEXCOORD_0).astype(np.float64) if p.attributes.TEXCOORD_0 is not None else None
            F = acc(p.indices).reshape(-1, 3).astype(np.int64)
            prims.append(dict(mat=mname, P=P, N=Nn, UV=UV, F=F, node=g.nodes[i].name or ''))
    for c in n.children or []: walk(c, M)
for r in g.scenes[g.scene].nodes: walk(r, np.eye(4))

# ---------------------------------------------------------------- into the wheel frame
# glTF (right-handed, the screen faces +z) -> Unity's left-handed wheel frame with the driver at -z: (x, y, -z) turns
# the wheel to face the driver and changes the handedness in one go (seen from the driver it reads as in the source);
# the mirror flips the winding.
allP = np.concatenate([p['P'] for p in prims]); xmin, xmax = allP[:, 0].min(), allP[:, 0].max()
S = WIDTH_M / (xmax - xmin)
hub = next(p for p in prims if p['mat'] == 'connector')
hub_y = 0.5 * (hub['P'][:, 1].min() + hub['P'][:, 1].max())
scr = next(p for p in prims if p['mat'] == 'screen')
face_z = scr['P'][:, 2].max()
off = np.array([-(xmin + xmax) * 0.5 * S, HUB_Y - hub_y * S, FACE_Z + face_z * S])
for p in prims:
    p['P'] = p['P'] * np.array([S, S, -S]) + off
    p['N'] = p['N'] * np.array([1, 1, -1])
    p['F'] = p['F'][:, [0, 2, 1]]
print(f'scale {S:.5f} m/unit, offset {np.round(off, 4)}')

# ---------------------------------------------------------------- textures -> atlas
def tex_of(mname):
    m = next(m for m in g.materials if m.name == mname); t = m.pbrMetallicRoughness.baseColorTexture
    return None if t is None else os.path.join(SRC, g.images[g.textures[t.index].source].uri)
def base_of(mname):
    m = next(m for m in g.materials if m.name == mname); f = m.pbrMetallicRoughness.baseColorFactor or [1, 1, 1, 1]
    return np.array(f[:3])
atlas = Image.new('RGB', (2 * SLOT, 2 * SLOT), (0, 0, 0))
for mname, spec in MATS.items():
    if spec[4] is None: continue
    im = Image.open(tex_of(mname)).convert('RGB').resize((SLOT, SLOT), Image.LANCZOS)
    atlas.paste(im, ((spec[4] % 2) * SLOT, (spec[4] // 2) * SLOT))
def flat_colour(mname):
    if mname in COLOUR: return np.array(COLOUR[mname])
    t = tex_of(mname)
    if t is not None and MATS[mname][4] is None:  # a flat texture (gray): its mean, linear
        a = np.asarray(Image.open(t).convert('RGB'), float) / 255.0
        return (a ** 2.2).reshape(-1, 3).mean(0)
    return base_of(mname) if MATS[mname][4] is None else np.ones(3)

# ---------------------------------------------------------------- components (LEDs, paddles)
def components(F, nv):
    par = np.arange(nv)
    def find(a):
        while par[a] != a: par[a] = par[par[a]]; a = par[a]
        return a
    for a, b, c in F:
        ra, rb, rc = find(a), find(b), find(c); par[rb] = ra; par[find(rc)] = ra
    return np.array([find(i) for i in range(nv)])

# ---------------------------------------------------------------- per part: orient, weld, decimate
def orient(P, N, F):
    fn = np.cross(P[F[:, 1]] - P[F[:, 0]], P[F[:, 2]] - P[F[:, 0]])
    vn = N[F].sum(1); flip = np.einsum('ij,ij->i', fn, vn) < 0
    F = F.copy(); F[flip] = F[flip][:, [0, 2, 1]]
    return F

def weld(P, UV, F, tol=1e-6):
    key = np.round(P / tol).astype(np.int64)
    if UV is not None: key = np.c_[key, np.round(UV * 1e5).astype(np.int64)]
    _, first, inv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    inv = inv.ravel()
    F2 = inv[F]; keep = (F2[:, 0] != F2[:, 1]) & (F2[:, 1] != F2[:, 2]) & (F2[:, 0] != F2[:, 2])
    return P[first], (UV[first] if UV is not None else None), F2[keep]

def decimate(P, F, target):
    """quadric decimation of a position-welded mesh; returns the new points, faces and the old -> new vertex map"""
    if len(F) <= target: return P, F, np.arange(len(P))
    P32, F32 = P.astype(np.float32), F.astype(np.int32)
    _, _, coll = fast_simplification.simplify(P32, F32, target_reduction=1.0 - target / len(F), agg=6, return_collapses=True)
    P3, F3, mp = fast_simplification.replay_simplification(P32, F32, coll)
    return P3.astype(np.float64), F3.astype(np.int64), np.asarray(mp, np.int64)

def corner_uvs(P0, UV0, F0, inv_p, mp, P3, F3):
    """UVs per corner of the decimated faces. The decimation runs on positions only (UV seams would stop it), so each
    new face takes one UV island of the source (the one most of its corners came from) and each corner the UV of the
    nearest source vertex of that island that collapsed into it (or the island's nearest vertex at all)."""
    _, inv_u = np.unique(np.c_[np.round(P0 * 1e6), np.round(UV0 * 1e5)], axis=0, return_inverse=True); inv_u = inv_u.ravel()
    comp = components(inv_u[F0], inv_u.max() + 1); isl = comp[inv_u]
    nv0 = mp[inv_p]; d = np.linalg.norm(P0 - P3[nv0], axis=1)
    o = np.lexsort((d, isl, nv0)); a, b = nv0[o], isl[o]
    firsts = np.r_[True, (a[1:] != a[:-1]) | (b[1:] != b[:-1])]
    best = {}
    for vn, il, src in zip(a[firsts], b[firsts], o[firsts]): best.setdefault(int(vn), {})[int(il)] = int(src)
    trees = {}
    out = np.zeros((len(F3), 3, 2))
    for fi, f in enumerate(F3):
        cnt = {}
        for v in f:
            for il in best.get(int(v), {}): cnt[il] = cnt.get(il, 0) + 1
        il = max(cnt, key=cnt.get) if cnt else int(isl[0])
        for j, v in enumerate(f):
            src = best.get(int(v), {}).get(il)
            if src is None:
                if il not in trees: ids = np.where(isl == il)[0]; trees[il] = (cKDTree(P0[ids]), ids)
                tr, ids = trees[il]; src = ids[tr.query(P3[v])[1]]
            out[fi, j] = UV0[src]
    return out

tri_src = sum(len(p['F']) for p in prims)
w = {id(p): np.sqrt(len(p['F'])) * DETAIL.get(p['mat'], 1.0) for p in prims}
wsum = sum(w.values())
parts = []
for p in prims:
    F = orient(p['P'], p['N'], p['F'])
    tex = MATS[p['mat']][4] is not None
    key = np.round(p['P'] * 1e6).astype(np.int64)
    _, first, inv_p = np.unique(key, axis=0, return_index=True, return_inverse=True); inv_p = inv_p.ravel()
    Pw = p['P'][first]; Fw = inv_p[F]; Fw = Fw[(Fw[:, 0] != Fw[:, 1]) & (Fw[:, 1] != Fw[:, 2]) & (Fw[:, 0] != Fw[:, 2])]
    tgt = max(24, int(TARGET_TRIS * w[id(p)] / wsum))
    if p['mat'] == 'screen': tgt = 64
    P, F2, mp = decimate(Pw, Fw, tgt)
    CUV = corner_uvs(p['P'], p['UV'], F, inv_p, mp, P, F2) if tex else None
    parts.append(dict(mat=p['mat'], node=p['node'], P=P, CUV=CUV, F=F2))
    print(f"{p['node']:38s} {p['mat']:20s} {len(p['F']):6d} -> {len(F2):5d}")

# ---------------------------------------------------------------- merge with per-face attributes, auto-smooth normals
V, Fs, FA = [], [], []   # FA: per face (part index)
o = 0
for k, pt in enumerate(parts):
    V.append(pt['P']); Fs.append(pt['F'] + o); FA.append(np.full(len(pt['F']), k)); o += len(pt['P'])
Pm = np.concatenate(V); Fm = np.concatenate(Fs); FAm = np.concatenate(FA)
CUVm = np.concatenate([pt['CUV'] if pt['CUV'] is not None else np.zeros((len(pt['F']), 3, 2)) for pt in parts])
fn = np.cross(Pm[Fm[:, 1]] - Pm[Fm[:, 0]], Pm[Fm[:, 2]] - Pm[Fm[:, 0]])
fa = np.linalg.norm(fn, axis=1); fnu = fn / (fa[:, None] + 1e-20)
good = fa > 1e-14; Fm, FAm, fn, fnu, CUVm = Fm[good], FAm[good], fn[good], fnu[good], CUVm[good]
# auto-smooth: each corner averages the area-weighted normals of the faces round its vertex within 40 deg of its own face
COS = np.cos(np.radians(40))
nv = len(Pm); corner_v = Fm.ravel(); corner_f = np.repeat(np.arange(len(Fm)), 3)
order = np.argsort(corner_v, kind='stable'); cv = corner_v[order]; cf = corner_f[order]
starts = np.searchsorted(cv, np.arange(nv)); ends = np.searchsorted(cv, np.arange(nv), side='right')
cn = np.zeros((len(Fm) * 3, 3))
for vi in range(nv):
    s, e = starts[vi], ends[vi]
    if s == e: continue
    fs = cf[s:e]; nu = fnu[fs]; nw = fn[fs]
    sim = (nu @ nu.T) >= COS
    acc_n = sim.astype(float) @ nw
    acc_n /= np.linalg.norm(acc_n, axis=1, keepdims=True) + 1e-20
    # corners of these faces at this vertex
    for j, f in enumerate(fs):
        slot = np.where(Fm[f] == vi)[0]
        for sl in slot: cn[f * 3 + sl] = acc_n[j]

# ---------------------------------------------------------------- per-corner attributes
NCOR = len(Fm) * 3
c_pos = Pm[Fm.ravel()]; c_uv = CUVm.reshape(-1, 2); c_part = np.repeat(FAm, 3)
c_col = np.zeros((NCOR, 4)); c_uv0 = np.zeros((NCOR, 2)); c_mat = np.zeros((NCOR, 4)); c_pad = np.zeros(NCOR); c_tex = np.zeros(NCOR); c_auv = np.zeros((NCOR, 2))
info = {}
for k, pt in enumerate(parts):
    sel = c_part == k; kind, coat, gloss, metal, slot = MATS[pt['mat']]
    c_col[sel, :3] = flat_colour(pt['mat']); c_col[sel, 3] = 1.0
    c_mat[sel] = (coat, gloss, metal, kind)
    if slot is not None:
        u = np.clip(c_uv[sel], 0.0005, 0.9995)  # glTF uv: v down from the image top
        ox, oy = (slot % 2) * SLOT, (slot // 2) * SLOT
        c_auv[sel, 0] = (ox + u[:, 0] * SLOT) / (2 * SLOT)
        c_auv[sel, 1] = 1.0 - (oy + u[:, 1] * SLOT) / (2 * SLOT)
        c_tex[sel] = 1.0
    if pt['mat'] == 'screen':
        q = c_pos[sel]; x0, x1 = q[:, 0].min(), q[:, 0].max(); y0, y1 = q[:, 1].min(), q[:, 1].max()
        wdt = x1 - x0; yc = 0.5 * (y0 + y1)  # the 2:1 display texture across the full width, centred (clamped edges = its background)
        c_uv0[sel, 0] = (q[:, 0] - x0) / wdt; c_uv0[sel, 1] = 0.5 + (q[:, 1] - yc) / (wdt * 0.5)
        info['screen'] = [float(x0), float(y0), float(x1), float(y1), float(q[:, 2].min())]
    if pt['mat'] == 'led_indicator_top':
        F = pt['F']; comp = components(F, len(pt['P'])); roots = np.unique(comp)
        cx = np.array([pt['P'][comp == r, 0].mean() for r in roots]); rank = np.argsort(np.argsort(cx))
        n = len(roots); idx = np.round(rank * (14.0 / max(1, n - 1)))
        vidx = idx[np.searchsorted(roots, comp)]
        # corners of this part: vertex index within part = Fm - offset
        offs = sum(len(parts[j]['P']) for j in range(k))
        c_uv0[sel, 0] = vidx[Fm.ravel()[sel] - offs]
        info['leds'] = int(n)
info['tris'] = int(len(Fm)); info['src_tris'] = int(tri_src)

# paddles: the shift paddles are the 'gray' Plane.006 pieces (the upper pair behind the grips): each side turns about
# a vertical axis at its inner end

# shift paddles: the upper 'gray' pieces behind the grips (Plane.006), one per side (no triangle crosses the middle):
# each turns about a vertical axis at its inner end (uv0 = hinge x, z; uv2.x = side)
for k, pt in enumerate(parts):
    if not pt['node'].startswith(PADDLES): continue
    sel = c_part == k; q = c_pos[sel]; sx = np.sign(q[:, 0])
    tri_sx = sx.reshape(-1, 3)
    if (tri_sx.min(1) != tri_sx.max(1)).any(): print('paddles joined in the middle: no flick'); continue
    for sd in (-1, 1):
        m = sx == sd; qq = q[m]; hx = sd * np.abs(qq[:, 0]).min(); hz = qq[:, 2].mean()
        idx = np.where(sel)[0][m]; c_pad[idx] = sd; c_uv0[idx] = (hx, hz)
    info['paddles'] = [float(np.abs(q[:, 0]).min()), float(q[:, 2].mean())]
# the left grip's axis where the glove closes round it: the middle of the rubber grip's section at the hand's height
gp = next(pt for pt in parts if pt['mat'] == 'handle'); q = gp['P'][gp['P'][:, 0] < 0]
yb, yt = q[:, 1].min(), q[:, 1].max(); yh = yb + 0.52 * (yt - yb)
band = q[np.abs(q[:, 1] - yh) < 0.006]
GRIP_L = np.array([0.5 * (band[:, 0].min() + band[:, 0].max()), yh, 0.5 * (band[:, 2].min() + band[:, 2].max())])
info['grip'] = [round(float(v), 4) for v in GRIP_L]; info['grip_span'] = [float(yb), float(yt), float(band[:, 0].max() - band[:, 0].min()), float(band[:, 2].max() - band[:, 2].min())]

# ---------------------------------------------------------------- baked occlusion (hemisphere rays against the whole wheel)
def bake_ao(Pv, Nv, tris, rays=24):
    import trimesh
    mesh = trimesh.Trimesh(Pm, Fm, process=False)
    rng = np.random.default_rng(1)
    d = rng.normal(size=(rays, 3)); d /= np.linalg.norm(d, axis=1, keepdims=True)
    ao = np.ones(len(Pv))
    org = np.repeat(Pv + Nv * 4e-4, rays, 0); dirs = np.tile(d, (len(Pv), 1)); nn = np.repeat(Nv, rays, 0)
    flip = np.einsum('ij,ij->i', dirs, nn) < 0; dirs[flip] *= -1
    occ = np.zeros(len(org)); CH = 12000
    for s0 in range(0, len(org), CH):
        loc, ri, ti = mesh.ray.intersects_location(org[s0:s0 + CH], dirs[s0:s0 + CH], multiple_hits=False)
        if not len(ri): continue
        dd = np.linalg.norm(loc - org[s0 + ri], axis=1); near = dd < 0.03   # within 3 cm: occluded, nearer = darker
        occ[s0 + ri[near]] = 0.4 + 0.6 * np.clip(1 - dd[near] / 0.03, 0, 1)
    cosw = np.einsum('ij,ij->i', dirs, nn)
    o_ = (occ * cosw).reshape(-1, rays).sum(1) / cosw.reshape(-1, rays).sum(1)
    return np.clip(1 - o_ * 1.1, 0.35, 1)

# ---------------------------------------------------------------- dedupe corners into vertices
key = np.c_[np.round(c_pos * 1e5), np.round(cn * 1e3), np.round(c_uv0 * 1e4), np.round(c_mat * 100), c_pad, c_tex, np.round(c_auv * 1e5), np.round(c_col[:, :3] * 1e4)]
_, first, inv = np.unique(key, axis=0, return_index=True, return_inverse=True); inv = inv.ravel()
VP, VN, VC, VU0, VM, VPAD, VTEX, VAUV = c_pos[first], cn[first], c_col[first], c_uv0[first], c_mat[first], c_pad[first], c_tex[first], c_auv[first]
VN /= np.linalg.norm(VN, axis=1, keepdims=True) + 1e-20
T = inv.reshape(-1, 3)
try:
    VC[:, 3] = bake_ao(VP, VN, T)
except Exception as e:
    print('AO skipped:', e)
print(f'{len(VP)} vertices, {len(T)} triangles (source {tri_src}); bounds {np.round(VP.min(0), 4)} .. {np.round(VP.max(0), 4)}')

# ---------------------------------------------------------------- write
os.makedirs(OUT, exist_ok=True)
grip = GRIP_L if 'GRIP_L' in globals() else np.zeros(3)
with open(os.path.join(OUT, 'wheel.bytes'), 'wb') as f:
    f.write(b'F2WH'); f.write(struct.pack('<III', 1, len(VP), len(T)))
    f.write(struct.pack('<4f', *info['screen'][:4])); f.write(struct.pack('<3f', *grip))
    rec = np.zeros(len(VP), dtype=[('p', '<f4', 3), ('n', '<f4', 3), ('c', 'u1', 4), ('u0', '<f4', 2), ('m', 'u1', 4), ('pad', 'i1'), ('tex', 'u1'), ('z', 'u1', 2), ('a', '<f4', 2)])
    rec['p'] = VP; rec['n'] = VN
    lin = np.clip(VC[:, :3], 0, 1) ** (1 / 2.2)  # colour stored gamma-encoded in bytes (more precision in the darks), decoded at load
    rec['c'][:, :3] = np.round(lin * 255); rec['c'][:, 3] = np.round(np.clip(VC[:, 3], 0, 1) * 255)
    rec['u0'] = VU0
    rec['m'][:, :3] = np.round(np.clip(VM[:, :3], 0, 1) * 255); rec['m'][:, 3] = VM[:, 3]
    rec['pad'] = VPAD; rec['tex'] = VTEX; rec['a'] = VAUV
    f.write(rec.tobytes())
    f.write(T.astype('<u2' if len(VP) < 65536 else '<u4').tobytes())
buf = io.BytesIO(); atlas.save(buf, 'PNG', optimize=True); png = buf.getvalue()
buf = io.BytesIO(); atlas.save(buf, 'JPEG', quality=90, subsampling=0); jpg = buf.getvalue()
open(os.path.join(OUT, 'atlas.bytes'), 'wb').write(png if len(png) <= len(jpg) * 1.3 else jpg)
atlas.save('/tmp/wheel_atlas.png')
lic = open(os.path.join(SRC, 'license.txt')).read()
credit = lic[lic.index('This work is based on'):].strip()
open(os.path.join(OUT, 'CREDITS.txt'), 'w').write(
    'Steering wheel model (Assets/Resources/Wheel/wheel.bytes, atlas.bytes)\n\n' + credit + '\n\n'
    'Author: dhimasdc9 (https://sketchfab.com/dhimasdc9)\nSource: https://sketchfab.com/3d-models/mclaren-formula-1-steering-wheel-4dc0ce4effbe43d0a027aa072b437818\n'
    'License: CC BY 4.0 (http://creativecommons.org/licenses/by/4.0/)\n\n'
    'Changes: merged into one mesh, decimated (~%d of %d triangles), rescaled and re-oriented, textures packed into one atlas, '
    'materials replaced by the game\'s steering-wheel shader; the display shows the game\'s own live read-out.\n'
    'In-game credit line: "Steering wheel model by dhimasdc9 (CC BY 4.0)".\n' % (info['tris'], info['src_tris']))
json.dump(info, open('/tmp/wheel_info.json', 'w'), indent=1)
print('wrote', OUT, os.path.getsize(os.path.join(OUT, 'wheel.bytes')), 'bytes mesh,', os.path.getsize(os.path.join(OUT, 'atlas.bytes')), 'bytes atlas', info)
