# convert.py — builds Assets/Resources/People/people.bytes from Quaternius' CC0 models (see CREDITS.txt):
#   python3 -I convert.py <src dir (fetch.sh output)> <out .bytes> [preview.png]
# * bodies: Universal Base Characters "Superhero" male / female (12.5k tris each), decimated to ~1.2k (LOD0) and ~0.3k
#   (LOD1) triangles, skin weights reduced to a 22-joint skeleton (fingers -> hand, curled into a relaxed grip first),
#   inflated a little into loose overalls. Every face gets a REGION (overall body, flank panels, yoke, sleeves, gloves,
#   boots, face, hair, helmet, visor, headset...) - the game colours the regions per team / person (People.cs).
# * head gear built here and fitted to each head: full-face crew helmet with visor and top stripe, engineer headset with
#   boom mic, team cap; head faces hidden inside the helmet are dropped. 4 variants x 2 bodies x 2 LODs.
# * clips: Universal Animation Library (same skeleton), sampled at 24 fps on the 22 joints (Unity space).
# Format (little endian), read by People.cs:
#   'PPL1', nj, parent[nj] (i32), nBody
#   per body: pelvisScale f32, rest local T (3 f32) + Q (4 f32) per joint, bindpose 16 f32 per joint (row-major),
#             nVar, per variant per LOD: nv, nt (i32), pos (3 f32), normal (3 i8), region (u8), bone idx (4 u8),
#             weight (4 u8), indices (3 u16 per tri)
#   nClip, per clip: name (u8 len + ascii), fps f32, frames i32, loop u8, quats (frames x nj x 4 i16), pelvis (frames x 3 f32)
import os, sys, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import fast_simplification as fs
from gltf_io import Gltf, trs, quat_mat
from rig import KEEP, PARENT, NJ, S, Clips, rest_skeleton, keep_of, fk

# ---------------------------------------------------------------- regions (People.cs: Reg*)
RSuit, RSide, RYoke, RSleeve, RForearm, RGlove, RThigh, RStripe, RShin, RBoot, RBelt, RNeck, RFace, RHair, \
    RHelmet, RHelmetTop, RVisor, RHeadset, RCap, RCapBrim, RKnee = range(21)

VARIANTS = ['helmet', 'headset', 'cap', 'bare']
LOD_TRIS = [1180, 300]
INFLATE = 0.011


def load_body(path, U):
    G = Gltf(path); g = G.g
    mi = max(range(len(g.meshes)), key=lambda i: G.g.accessors[g.meshes[i].primitives[0].indices].count)
    prim = g.meshes[mi].primitives[0]; sk = g.skins[0]
    P = G.acc(prim.attributes.POSITION); N = G.acc(prim.attributes.NORMAL)
    J = G.acc(prim.attributes.JOINTS_0).astype(int); Wt = G.acc(prim.attributes.WEIGHTS_0)
    I = G.acc(prim.indices).astype(int).reshape(-1, 3)
    ibm = G.acc(sk.inverseBindMatrices).reshape(-1, 4, 4).transpose(0, 2, 1)
    # rest pose with the fingers curled as in the idle clip (a relaxed grip)
    idle_q = {}
    for nm in [n.name for n in g.nodes]:
        if any(nm.startswith(f) for f in ('index', 'middle', 'ring', 'pinky', 'thumb')) and nm in U.G.byname:
            idle_q[nm] = U.local('Idle_Loop', 0.0, U.G.byname[nm])
    def local(i):
        n = g.nodes[i]
        if n.name in idle_q:
            L = G.local(i).copy(); r = idle_q[n.name][:3, :3]; L[:3, :3] = r / np.linalg.norm(r, axis=0) * np.linalg.norm(L[:3, :3], axis=0); return L
        return G.local(i)
    def world(i):
        m = local(i); p = G.parent.get(i)
        while p is not None: m = local(p) @ m; p = G.parent.get(p)
        return m
    skin = np.array([world(j) @ ibm[k] for k, j in enumerate(sk.joints)])
    M = np.einsum('vk,vkab->vab', Wt, skin[J])
    Pw = np.einsum('vab,vb->va', M, np.c_[P, np.ones(len(P))])[:, :3]
    Nw = np.einsum('vab,vb->va', M[:, :3, :3], N); Nw /= np.linalg.norm(Nw, axis=1, keepdims=True) + 1e-9
    # kept-joint weights
    kmap = np.array([keep_of(G, j) for j in sk.joints])
    KW = np.zeros((len(P), NJ))
    for c in range(4): np.add.at(KW, (np.arange(len(P)), kmap[J[:, c]]), Wt[:, c])
    KW /= KW.sum(1, keepdims=True)
    # to Unity space
    Pw[:, 0] *= -1; Nw[:, 0] *= -1; I = I[:, [0, 2, 1]]
    t, q, Wr = rest_skeleton(G)
    return dict(P=Pw, N=Nw, KW=KW, I=I, t=t, q=q, W=Wr)


def weld(P, I, *attrs):
    key = np.round(P / 1e-5).astype(np.int64)
    _, first, inv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    inv = inv.reshape(-1)
    return P[first], inv[I], [a[first] for a in attrs]


def decimate(P, I, target):
    red = 1.0 - target / len(I)
    p2, f2, coll = fs.simplify(P, I, target_reduction=red, agg=5, return_collapses=True)
    p3, f3, mp = fs.replay_simplification(P.astype(np.float32), I.astype(np.int32), coll)
    return p3.astype(np.float64), f3.astype(int), mp


def vnormals(P, I):
    n = np.zeros_like(P); fn = np.cross(P[I[:, 1]] - P[I[:, 0]], P[I[:, 2]] - P[I[:, 0]])
    for c in range(3): np.add.at(n, I[:, c], fn)
    return n / (np.linalg.norm(n, axis=1, keepdims=True) + 1e-12)


def classify(c, n, w, B):
    """region of a face: centroid c, normal n, kept-joint weights w; B = body landmarks"""
    j = int(np.argmax(w))
    nm = KEEP[j]
    if nm == 'Head':
        hc = B['hc']; r = B['hr']
        rel = (c - hc) / r
        face = rel[2] > 0.15 and rel[1] < 0.35 and abs(rel[0]) < 0.75
        return RFace if face else RHair
    if nm == 'neck_01':
        return RNeck
    if nm.startswith('hand'): return RGlove
    if nm.startswith('lowerarm'):
        return RGlove if abs(c[0]) > B['wrist'] - 0.035 else RForearm
    if nm.startswith('upperarm'): return RSleeve
    if nm.startswith('foot') or nm.startswith('ball'): return RBoot
    side = np.sign(c[0]) * n[0] > 0.75
    leg = np.sign(c[0]) * n[0] > 0.9
    if nm.startswith('calf'):
        if c[1] < B['ankle'] + 0.11: return RBoot
        if abs(c[1] - B['knee']) < 0.075 and n[2] > 0.35: return RKnee
        return RStripe if leg else RShin
    if nm.startswith('thigh'):
        return RStripe if leg else RThigh
    # torso
    if abs(c[1] - B['belt']) < 0.032: return RBelt
    if c[1] > B['yoke'] or (nm.startswith('clavicle') and c[1] > B['yoke'] - 0.06): return RYoke
    if side and c[1] < B['yoke'] - 0.02 and c[1] > B['belt'] - 0.12: return RSide
    return RSuit


def landmarks(b):
    W = b['W']; pos = {nm: W[j][:3, 3] for j, nm in enumerate(KEEP)}
    hv = b['P'][b['KW'][:, KEEP.index('Head')] > 0.5]
    lo, hi = hv.min(0), hv.max(0)
    return dict(hc=(lo + hi) / 2, hr=(hi - lo) / 2, hlo=lo, hhi=hi, wrist=abs(pos['hand_l'][0]), ankle=pos['foot_l'][1],
                knee=pos['calf_l'][1], belt=pos['pelvis'][1] + 0.1, yoke=pos['spine_03'][1] + 0.1, head=pos['Head'])


# ---------------------------------------------------------------- head gear (Unity space; all on the Head joint)
class Geo:
    def __init__(self): self.P = []; self.R = []; self.F = []; self.N = []

    def tri(self, a, b, c, reg, out=None):
        a, b, c = map(np.asarray, (a, b, c))
        n = np.cross(b - a, c - a)
        if out is not None and np.dot(n, out) < 0: b, c = c, b; n = -n
        k = len(self.P); self.P += [a, b, c]; nn = n / (np.linalg.norm(n) + 1e-12); self.N += [nn, nn, nn]; self.R += [reg] * 3
        self.F.append([k, k + 1, k + 2])

    def quad(self, a, b, c, d, reg, out=None): self.tri(a, b, c, reg, out); self.tri(a, c, d, reg, out)


def smooth_shell(rows, cols, ring, regfn, out_c):
    """a smooth shell from a grid of points ring(i, j) (i rows top -> bottom, j around, wrapping); smooth normals"""
    g = Geo()
    pts = np.array([[ring(i, j) for j in range(cols)] for i in range(rows)])
    nrm = np.zeros_like(pts)
    for i in range(rows):
        for j in range(cols):
            d = pts[i, j] - out_c; nrm[i, j] = d / np.linalg.norm(d)
    for i in range(rows - 1):
        for j in range(cols):
            j2 = (j + 1) % cols
            reg = regfn(i, j)
            quad = [(i, j), (i, j2), (i + 1, j2), (i + 1, j)]
            for tri in ([0, 1, 2], [0, 2, 3]):
                a, b, c = [quad[t] for t in tri]
                if np.linalg.norm(pts[a] - pts[b]) < 1e-6 and tri == [0, 1, 2]:
                    pass
                k = len(g.P)
                for v in (a, b, c): g.P.append(pts[v]); g.N.append(nrm[v]); g.R.append(reg)
                n = np.cross(pts[b] - pts[a], pts[c] - pts[a])
                if np.dot(n, pts[a] - out_c) < 0: g.F.append([k, k + 2, k + 1])
                else: g.F.append([k, k + 1, k + 2])
    return g


def helmet(B, lod):
    hc = B['hc'] + np.array([0, 0.012, 0.0]); r = B['hr'] * np.array([1.2, 1.12, 1.16]) + 0.018
    cols = 14 if lod == 0 else 7; rows = 8 if lod == 0 else 4
    lat = np.linspace(90, -62, rows) if lod == 0 else np.array([90, 35, -15, -62])
    def ring(i, j):
        if lat[i] >= 89.9: return hc + np.array([0, r[1], 0])
        a = np.radians(lat[i]); az = 2 * np.pi * j / cols
        x, z = np.sin(az), np.cos(az); y = np.sin(a); ca = np.cos(a)
        # the back of the shell reaches lower (over the neck), the chin bar is a little narrower
        p = hc + np.array([x * ca * r[0], y * r[1], z * ca * r[2]])
        if lat[i] < -20: p[2] = hc[2] + z * ca * r[2] * (0.9 if z > 0 else 1.0)
        return p
    def reg(i, j):
        az = 360.0 * (j + 0.5) / cols; front = az < 58 or az > 302
        la = (lat[i] + lat[i + 1]) / 2
        if front and -22 < la < 22: return RVisor
        if la > 30 and (az < 25 or az > 335 or 155 < az < 205): return RHelmetTop
        return RHelmet
    return smooth_shell(rows, cols, ring, reg, hc)


def cap(B, lod):
    hc = B['hc'] + np.array([0, 0.0, -0.005]); r = B['hr'] * np.array([1.06, 1.0, 1.06]) + 0.008
    cols = 12 if lod == 0 else 6; lat = [90, 55, 25, 5] if lod == 0 else [90, 30, 5]
    def ring(i, j):
        if lat[i] >= 89.9: return hc + np.array([0, r[1] * 1.02, 0])
        a = np.radians(lat[i]); az = 2 * np.pi * j / cols
        return hc + np.array([np.sin(az) * np.cos(a) * r[0], np.sin(a) * r[1], np.cos(az) * np.cos(a) * r[2]])
    g = smooth_shell(len(lat), cols, ring, lambda i, j: RCap, hc)
    # brim: a flat half disc in front
    y = hc[1] + np.sin(np.radians(5)) * r[1] - 0.004; n = 5 if lod == 0 else 2
    for k in range(n):
        a0 = np.radians(-70 + 140 * k / n); a1 = np.radians(-70 + 140 * (k + 1) / n)
        p0 = hc + np.array([np.sin(a0) * r[0], 0, np.cos(a0) * r[2]]); p1 = hc + np.array([np.sin(a1) * r[0], 0, np.cos(a1) * r[2]])
        q0 = hc + np.array([np.sin(a0) * r[0] * 1.15, 0, np.cos(a0) * r[2] * 1.15 + 0.07 * np.cos(a0)]); q1 = hc + np.array([np.sin(a1) * r[0] * 1.15, 0, np.cos(a1) * r[2] * 1.15 + 0.07 * np.cos(a1)])
        for v in (p0, p1, q0, q1): v[1] = y
        q0[1] -= 0.012; q1[1] -= 0.012
        g.quad(p0, p1, q1, q0, RCapBrim, np.array([0, 1, 0])); g.quad(p0, q0, q1, p1, RCapBrim, np.array([0, -1, 0]))
    return g


def box_seg(g, a, b, w, h, reg, side_hint):
    a, b = np.asarray(a), np.asarray(b); d = b - a; d /= np.linalg.norm(d)
    u = np.cross(d, side_hint); u /= np.linalg.norm(u); v = np.cross(u, d)
    cs = [(-1, -1), (1, -1), (1, 1), (-1, 1)]
    A = [a + u * x * w + v * y * h for x, y in cs]; Bp = [b + u * x * w + v * y * h for x, y in cs]
    for k in range(4):
        k2 = (k + 1) % 4; mid = (A[k] + A[k2]) / 2 - a
        g.quad(A[k], A[k2], Bp[k2], Bp[k], reg, mid)


def cyl_x(g, c, r, length, n, reg, sgn):
    """a short cylinder along x (ear cup), the outer cap facing sgn * x"""
    pts0 = [c + np.array([0, np.cos(2 * np.pi * k / n) * r, np.sin(2 * np.pi * k / n) * r]) for k in range(n)]
    pts1 = [p + np.array([sgn * length, 0, 0]) for p in pts0]
    for k in range(n):
        k2 = (k + 1) % n; g.quad(pts0[k], pts0[k2], pts1[k2], pts1[k], reg, (pts0[k] + pts0[k2]) / 2 - c)
    cc = c + np.array([sgn * length, 0, 0])
    for k in range(n): g.tri(cc, pts1[k], pts1[(k + 1) % n], reg, np.array([sgn, 0, 0]))


def headset(B, lod):
    g = Geo(); hc = B['hc']; r = B['hr']
    n = 8 if lod == 0 else 4
    ey = hc[1] - r[1] * 0.12; ez = hc[2] - r[2] * 0.08
    for s in (-1, 1):
        cyl_x(g, np.array([s * (r[0] + 0.004), ey, ez]), 0.048, 0.03, n, RHeadset, s)
    # band over the top
    m = 7 if lod == 0 else 3; prev = None
    for k in range(m + 1):
        a = np.pi * k / m; p = np.array([np.cos(a) * (r[0] + 0.022), ey + np.sin(a) * (r[1] + 0.018 + hc[1] - ey), ez])
        if prev is not None: box_seg(g, prev, p, 0.016, 0.006, RHeadset, np.array([0, 0, 1.0]))
        prev = p
    if lod == 0:  # boom mic from the left cup to the corner of the mouth
        a = np.array([-(r[0] + 0.02), ey - 0.02, ez + 0.02]); b = np.array([-r[0] * 0.55, hc[1] - r[1] * 0.62, hc[2] + r[2] * 0.95])
        box_seg(g, a, b, 0.006, 0.006, RHeadset, np.array([0, 1.0, 0]))
        box_seg(g, b, b + np.array([0.025, 0, 0.012]), 0.011, 0.011, RHeadset, np.array([0, 1.0, 0]))
    return g


# ---------------------------------------------------------------- one body -> variants x LODs
def build_body(b):
    B = landmarks(b)
    Pw, Iw, (Nw, KWw) = weld(b['P'], b['I'], b['N'], b['KW'])
    out = []
    for lod, tgt in enumerate(LOD_TRIS):
        P, F, mp = decimate(Pw, Iw, tgt)
        # attributes of a new vertex: the source vertex (of those collapsed into it) nearest to it
        best = np.full(len(P), -1); bd = np.full(len(P), 1e9)
        d = np.linalg.norm(Pw - P[mp], axis=1)
        for i in range(len(Pw)):
            k = mp[i]
            if d[i] < bd[k]: bd[k] = d[i]; best[k] = i
        KW = KWw[best]
        N = vnormals(P, F)
        P = P + N * INFLATE * (1.0 - KW[:, KEEP.index('Head')] - KW[:, KEEP.index('neck_01')])[:, None]
        N = vnormals(P, F)
        fc = P[F].mean(1); fn = np.cross(P[F[:, 1]] - P[F[:, 0]], P[F[:, 2]] - P[F[:, 0]]); fn /= np.linalg.norm(fn, axis=1, keepdims=True) + 1e-12
        fw = KW[F].mean(1)
        freg = np.array([classify(fc[i], fn[i], fw[i], B) for i in range(len(F))])
        var = []
        for v in VARIANTS:
            keepf = np.ones(len(F), bool)
            if v == 'helmet':
                hc = B['hc'] + np.array([0, 0.012, 0]); r = B['hr'] * np.array([1.2, 1.12, 1.16]) + 0.012
                inside = (((P[F] - hc) / r) ** 2).sum(2).max(1) < 1.0
                keepf &= ~inside
            gear = {'helmet': helmet, 'headset': headset, 'cap': cap}.get(v)
            g = gear(B, lod) if gear else Geo()
            # split vertices per region (crisp panel edges)
            verts = {}; vp, vn, vr, vw, tris = [], [], [], [], []
            for i in np.nonzero(keepf)[0]:
                t3 = []
                for c in F[i]:
                    key = (c, freg[i])
                    if key not in verts:
                        verts[key] = len(vp); vp.append(P[c]); vn.append(N[c]); vr.append(freg[i]); vw.append(KW[c])
                    t3.append(verts[key])
                tris.append(t3)
            hw = np.zeros(NJ); hw[KEEP.index('Head')] = 1
            base = len(vp)
            for k in range(len(g.P)): vp.append(g.P[k]); vn.append(g.N[k]); vr.append(g.R[k]); vw.append(hw)
            for f in g.F: tris.append([base + f[0], base + f[1], base + f[2]])
            var.append((np.array(vp), np.array(vn), np.array(vr), np.array(vw), np.array(tris)))
        out.append(var)
    return out


def quant_weights(W):
    idx = np.argsort(-W, axis=1)[:, :4]; w = np.take_along_axis(W, idx, 1)
    w = w / w.sum(1, keepdims=True); q = np.floor(w * 255 + 0.5).astype(int)
    q[:, 0] += 255 - q.sum(1)
    return idx.astype(np.uint8), np.clip(q, 0, 255).astype(np.uint8)


# ---------------------------------------------------------------- clips
CLIPS = [  # name, source clip, t0, t1 (None = whole), loop, ping-pong
    ('Idle', 'Idle_Loop', None, None, 1, 0), ('Walk', 'Walk_Loop', None, None, 1, 0), ('Jog', 'Jog_Fwd_Loop', None, None, 1, 0),
    ('Carry', 'Walk_Carry_Loop', None, None, 1, 0), ('Kneel', 'Fixing_Kneeling', 1.25, 4.2, 1, 1), ('Crouch', 'Crouch_Idle_Loop', None, None, 1, 0),
    ('Push', 'Push_Loop', None, None, 1, 0), ('Headset', 'Idle_TalkingPhone_Loop', None, None, 1, 0), ('FoldArms', 'Idle_FoldArms_Loop', None, None, 1, 0),
    ('Talk', 'Idle_Talking_Loop', None, None, 1, 0), ('Sit', 'Sitting_Idle_Loop', None, None, 1, 0), ('SitTalk', 'Sitting_Talking_Loop', None, None, 1, 0),
    ('Celebrate', 'Dance_Loop', None, None, 1, 0), ('Yes', 'Yes', None, None, 0, 0), ('Interact', 'Interact', None, None, 1, 0),
    ('Sprint', 'Sprint_Loop', None, None, 1, 0),
]
FPS = 24.0


def bake_clips(U1, U2):
    res = []
    for name, src, t0, t1, loop, pp in CLIPS:
        C = U1 if src in U1.anims else U2
        dur = C.anims[src][1]; a = t0 or 0.0; b = t1 or dur
        n = max(2, int(round((b - a) * FPS)) + (0 if loop else 1))
        ts = [a + (b - a) * k / (n if loop else n - 1) for k in range(n)]
        if pp: ts = ts + ts[-2:0:-1]
        Q = []; T = []
        for t in ts:
            q, pel = C.pose(src, t); Q.append(q); T.append(pel)
        Q = np.array(Q); T = np.array(T)
        for j in range(NJ):  # hemisphere continuity
            for f in range(1, len(Q)):
                if np.dot(Q[f, j], Q[f - 1, j]) < 0: Q[f, j] = -Q[f, j]
        res.append((name, loop, Q, T))
    return res


def main():
    src, outp = sys.argv[1], sys.argv[2]
    U1 = Clips(Gltf(f'{src}/moorfall/animations/UAL1.glb')); U2 = Clips(Gltf(f'{src}/moorfall/animations/UAL2.glb'))
    ual_t, _, _ = rest_skeleton(U1.G)
    bodies = []
    for nm in ['Superhero_Male', 'Superhero_Female']:
        b = load_body(f'{src}/moorfall/characters/{nm}.glb', U1)
        b['lods'] = build_body(b); b['pscale'] = b['t'][0][1] / ual_t[0][1]
        bodies.append(b)
        for lod in range(2):
            print(nm, 'lod', lod, [(len(v[0]), len(v[4])) for v in b['lods'][lod]])
    clips = bake_clips(U1, U2)
    o = bytearray(b'PPL1'); o += struct.pack('<i', NJ); o += struct.pack(f'<{NJ}i', *PARENT); o += struct.pack('<i', len(bodies))
    for b in bodies:
        o += struct.pack('<f', b['pscale'])
        for j in range(NJ): o += struct.pack('<7f', *b['t'][j], *b['q'][j])
        for j in range(NJ): o += struct.pack('<16f', *np.linalg.inv(b['W'][j]).reshape(-1))
        o += struct.pack('<i', len(VARIANTS))
        for vi in range(len(VARIANTS)):
            for lod in range(2):
                P, N, R, W, F = b['lods'][lod][vi]
                bi, bw = quant_weights(W)
                o += struct.pack('<ii', len(P), len(F))
                o += P.astype('<f4').tobytes()
                o += np.clip(np.round(N * 127), -127, 127).astype(np.int8).tobytes()
                o += R.astype(np.uint8).tobytes(); o += bi.tobytes(); o += bw.tobytes()
                o += F.astype('<u2').tobytes()
    o += struct.pack('<i', len(clips))
    for name, loop, Q, T in clips:
        o += struct.pack('<B', len(name)) + name.encode(); o += struct.pack('<fiB', FPS, len(Q), loop)
        o += np.clip(np.round(Q * 32767), -32767, 32767).astype('<i2').tobytes(); o += T.astype('<f4').tobytes()
    open(outp, 'wb').write(o)
    print('wrote', outp, len(o), 'bytes')
    if len(sys.argv) > 3: preview(bodies, sys.argv[3])


def preview(bodies, path):
    import matplotlib; matplotlib.use('Agg'); import matplotlib.pyplot as plt
    from matplotlib.collections import PolyCollection
    pal = {RSuit: (0.8, 0.1, 0.1), RSide: (0.95, 0.95, 0.95), RYoke: (0.1, 0.1, 0.12), RSleeve: (0.8, 0.1, 0.1), RForearm: (0.8, 0.1, 0.1),
           RGlove: (0.1, 0.1, 0.1), RThigh: (0.8, 0.1, 0.1), RStripe: (0.95, 0.95, 0.95), RShin: (0.5, 0.06, 0.06), RBoot: (0.05, 0.05, 0.05),
           RBelt: (0.1, 0.1, 0.1), RNeck: (0.8, 0.6, 0.5), RFace: (0.85, 0.65, 0.52), RHair: (0.2, 0.13, 0.08), RHelmet: (0.8, 0.1, 0.1),
           RHelmetTop: (0.95, 0.95, 0.95), RVisor: (0.05, 0.07, 0.12), RHeadset: (0.05, 0.05, 0.05), RCap: (0.8, 0.1, 0.1), RCapBrim: (0.1, 0.1, 0.1), RKnee: (0.2, 0.2, 0.22)}
    fig, axs = plt.subplots(2, 8, figsize=(24, 7))
    for bi, b in enumerate(bodies):
        for vi in range(4):
            for lod in range(2):
                ax = axs[bi, vi * 2 + lod]
                P, N, R, W, F = b['lods'][lod][vi]
                for view, off in ((0, 0.0), (1, 1.1)):
                    if view == 0: X = P[:, 0]; Z = P[:, 2]
                    else: X = P[:, 2] + off; Z = -P[:, 0]
                    polys = []; cols = []
                    fc = P[F].mean(1); order = np.argsort(Z[F].mean(1))
                    for i in order:
                        f = F[i]; n = np.cross(P[f[1]] - P[f[0]], P[f[2]] - P[f[0]])
                        nz = n[2] if view == 0 else -n[0]
                        if nz <= 0: continue
                        sh = 0.45 + 0.55 * max(0, (n / np.linalg.norm(n)) @ np.array([0.3, 0.5, 0.8] if view == 0 else [0.8, 0.5, 0.3]))
                        polys.append(np.c_[X[f], P[f, 1]]); cols.append(np.clip(np.array(pal.get(int(R[f[0]]), (1, 0, 1))) * sh, 0, 1))
                    ax.add_collection(PolyCollection(polys, facecolors=cols, edgecolors='none'))
                ax.set_xlim(-0.6, 1.7); ax.set_ylim(0, 1.95); ax.set_aspect('equal'); ax.axis('off'); ax.set_title(f'{VARIANTS[vi]} lod{lod} {len(F)}t', fontsize=8)
    plt.tight_layout(); plt.savefig(path, dpi=70)


if __name__ == '__main__':
    main()
