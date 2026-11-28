# driver_code.py — EXPERIMENT (branch claude/driver-code): the race driver built entirely by code, no textures.
#
# Smooth lofted tubes (elliptical rings along the limb / spine chains, radii measured once from Adi's suited driver so the
# proportions match), a code-made helmet (egg shell, visor band, brow band, centre stripe), gloves, boots, a neck, all
# skinned to the same game skeleton as every People2 body (src.body_skeleton), so every clip / IK / pit animation plays
# unchanged. Colour only by per-vertex palette REGION (People2.shader: _Pal[region], same regions as today's driver) over a
# 64 x 64 atlas whose single texel colour is "tinted, neutral grey": no albedo texture at all.
# Seams / zip / belt / cuffs / collar are rings or columns of other regions; side stripes are a sector of the ring.
# Meant to be ported to C# (it is only rings, ellipses and a weight ramp) so it can be generated at load; this prototype
# builds it offline into the same pack format so the game and Tools/Shots can show it side by side with driver_new.
import numpy as np
from p2io import Model
from retarget import Src, MIX
import build as B
import driver_new as DN

SRC = DN.SRC
# joint indices of the game skeleton (People.cs J*)
PELVIS, SP1, SP2, SP3, NECK, HEAD, CLAVL, UPL, LOL, HANDL, CLAVR, UPR, LOR, HANDR, THL, CALFL, FOOTL, BALLL, THR, CALFR, FOOTR, BALLR = range(22)
R_SUIT, R_STRIPE, R_GLOVE, R_BOOT, R_BELT, R_NECK, R_HELM, R_TOP, R_VISOR = B.RSuit, B.RStripe, B.RGlove, B.RBoot, B.RBelt, B.RNeck, B.RHelmet, B.RHelmetTop, B.RVisor

# per LOD: sectors round a ring, stations (torso, leg, arm, boot, hand), helmet rows x cols
LODS = [dict(C=36, st=(18, 14, 12, 8, 7), hr=24, hc=48),
        dict(C=20, st=(10, 8, 7, 5, 4), hr=12, hc=24),
        dict(C=12, st=(6, 5, 4, 3, 3), hr=8, hc=14),
        dict(C=8, st=(4, 3, 3, 2, 2), hr=5, hc=8)]


class Mesh:
    def __init__(self): self.P, self.KW, self.R, self.I, self.C = [], [], [], [], []   # C: per-vertex ring centre (for winding)
    def n(self): return sum(len(p) for p in self.P)


def gs(a, hu, hv):
    """an orthonormal (u, v) frame perpendicular to axis a from hints"""
    u = hu - a * (hu @ a); u /= np.linalg.norm(u) + 1e-9
    v = np.cross(a, u); v /= np.linalg.norm(v) + 1e-9
    if v @ hv < 0: v = -v
    return u, v


def measure(pts, c, a, u, v, delta, rmax, lo=3, hi=97):
    """cross-section of the reference surface points around centre c (axis a): (cu, cv, rx, ry)"""
    rel = pts - c; t = rel @ a; pu = rel @ u; pv = rel @ v
    sel = (np.abs(t) < delta) & (np.hypot(pu, pv) < rmax)
    if sel.sum() < 6: return 0.0, 0.0, None, None
    a0, a1 = np.percentile(pu[sel], [lo, hi]); b0, b1 = np.percentile(pv[sel], [lo, hi])
    return (a0 + a1) / 2, (b0 + b1) / 2, max((a1 - a0) / 2, 0.012), max((b1 - b0) / 2, 0.012)


def polyline_stations(path, n):
    """n points along a polyline (equal arc length): (points, tangents, segment index, local t)"""
    path = np.asarray(path, float); seg = np.linalg.norm(np.diff(path, axis=0), axis=1); cum = np.r_[0, np.cumsum(seg)]
    out = []
    for k in range(n):
        s = cum[-1] * k / (n - 1); i = min(np.searchsorted(cum, s, side='right') - 1, len(seg) - 1)
        t = (s - cum[i]) / max(seg[i], 1e-9); p = path[i] * (1 - t) + path[i + 1] * t
        out.append((p, (path[i + 1] - path[i]) / max(seg[i], 1e-9), i, t))
    return out


def blend_w(bones, i, t, ramp=0.22):
    """weights over the game joints for a station at local t in segment i (bones[i] owns it; ramps into the neighbours)"""
    w = np.zeros(22); w[bones[i]] = 1.0
    if t < ramp and i > 0: k = 0.5 * (1 - t / ramp); w[bones[i]] -= k; w[bones[i - 1]] += k
    if t > 1 - ramp and i + 1 < len(bones): k = 0.5 * (1 - (1 - t) / ramp); w[bones[i]] -= k; w[bones[i + 1]] += k
    return w


def loft(mesh, path, bones, pts, hu, hv, C, n, delta, rmax, region_fn, scale=1.0, cap0=False, cap1=False, lift=0.0, minr=0.0, ends=None):
    """a tube along `path` (joint positions) measured from the reference points; region_fn(ring k of n, angle, side) -> region"""
    st = polyline_stations(path, n)
    P = []; KW = []; Rg = []; Cn = []
    ring_c = []
    for k, (c, a, i, t) in enumerate(st):
        u, v = gs(a, np.asarray(hu, float), np.asarray(hv, float))
        cu, cv, rx, ry = measure(pts, c, a, u, v, delta, rmax)
        if rx is None:  # no surface there: reuse the neighbour's size
            rx, ry, cu, cv = (ring_c[-1][2], ring_c[-1][3], ring_c[-1][0], ring_c[-1][1]) if ring_c else (0.05, 0.05, 0.0, 0.0)
        rx = max(rx * scale, minr); ry = max(ry * scale, minr)
        ring_c.append((cu, cv, rx, ry, u, v, c, a, i, t))
    # smooth the radii a little along the chain (the measurement is noisy at joints)
    arr = np.array([[r[0], r[1], r[2], r[3]] for r in ring_c])
    for _ in range(2):
        arr[1:-1] = 0.25 * arr[:-2] + 0.5 * arr[1:-1] + 0.25 * arr[2:]
    for k, r in enumerate(ring_c):
        cu, cv, rx, ry = arr[k]; u, v, c, a, i, t = r[4:]
        w = blend_w(bones, i, t)
        for j in range(C):
            th = 2 * np.pi * j / C; ca, sa = np.cos(th), np.sin(th)
            P.append(c + u * (cu + rx * ca) + v * (cv + ry * sa)); KW.append(w); Cn.append(c + u * cu + v * cv)
            Rg.append(region_fn(k, n, th, c))
    P = np.array(P); KW = np.array(KW); Rg = np.array(Rg, np.uint8); Cn = np.array(Cn)
    I = []
    for k in range(n - 1):
        for j in range(C):
            a0, a1 = k * C + j, k * C + (j + 1) % C; b0, b1 = (k + 1) * C + j, (k + 1) * C + (j + 1) % C
            I += [(a0, b0, b1), (a0, b1, a1)]
    I = np.array(I)
    base = mesh.n()
    for cap, ring in ((cap0, 0), (cap1, n - 1)):
        if not cap: continue
        ci = len(P); cen = Cn[ring * C]; P = np.vstack([P, cen]); KW = np.vstack([KW, KW[ring * C]]); Rg = np.r_[Rg, Rg[ring * C + 0]]; Cn = np.vstack([Cn, cen])
        for j in range(C): I = np.vstack([I, (ci, ring * C + j, ring * C + (j + 1) % C)])
    mesh.P.append(P); mesh.KW.append(KW); mesh.R.append(Rg); mesh.C.append(Cn); mesh.I.append(I + base)
    return mesh


def helmet(mesh, rows, cols, c0, ry_up, ry_dn, rz_f, rz_b, rx):
    """the egg shell on a grid with its poles at the temples (borders of the design run along grid lines)"""
    rs = np.sin(np.linspace(-np.pi / 2, np.pi / 2, rows)); ang = np.radians(np.linspace(-90, 270, cols, endpoint=False))
    P = []; Rg = []
    for r, s in enumerate(rs):
        rho = np.sqrt(max(0.0, 1 - s * s))
        for a in ang:
            ux, uy, uz = s, rho * np.sin(a), rho * np.cos(a)
            ry = ry_up if uy > 0 else ry_dn; rz = rz_f if uz > 0 else rz_b
            chin = np.clip(-uy * 2.5, 0, 1) * np.clip(uz * 2, 0, 1) * (1 - s * s); nape = np.clip(-uy * 2, 0, 1) * np.clip(-uz * 2, 0, 1)
            P.append(c0 + np.array([ux * rx * (1 - 0.08 * nape), uy * ry, uz * rz * (1 + 0.06 * chin)]))
    P = np.array(P)
    def reg(r, c):
        s = abs(rs[r]); a = np.degrees(ang[c % cols])
        a = (a + 180) % 360 - 180
        if s < 0.62 and -17 < a < 17: return R_VISOR
        if s < 0.72 and 17 <= a < 31: return R_TOP      # brow band over the visor
        if s < 0.22 and 31 <= a < 190: return R_TOP                                                   # centre stripe over the top
        if -90 <= a < -35 and abs(rs[r]) < 0.78 and a > -75: return R_TOP                              # a chin band
        return R_HELM
    Rg = np.array([reg(r, c) for r in range(rows) for c in range(cols)], np.uint8)
    I = []
    for r in range(rows - 1):
        for c in range(cols):
            c1 = (c + 1) % cols; a0, a1, b0, b1 = r * cols + c, r * cols + c1, (r + 1) * cols + c, (r + 1) * cols + c1
            I += [(a0, b0, b1), (a0, b1, a1)]
    I = np.array(I); base = mesh.n()
    KW = np.zeros((len(P), 22)); KW[:, HEAD] = 1
    cen = np.tile(c0, (len(P), 1))
    mesh.P.append(P); mesh.KW.append(KW); mesh.R.append(Rg); mesh.C.append(cen); mesh.I.append(I + base)


def finish(mesh):
    """orient faces outward (away from their ring centre) for the Unity winding, smooth normals, one mesh dict"""
    P = np.vstack(mesh.P); KW = np.vstack(mesh.KW); R = np.concatenate(mesh.R); Cn = np.vstack(mesh.C); I = np.vstack(mesh.I)
    fn = np.cross(P[I[:, 1]] - P[I[:, 0]], P[I[:, 2]] - P[I[:, 0]])
    out = P[I].mean(1) - Cn[I[:, 0]]
    flip = np.einsum('ij,ij->i', fn, out) < 0
    I[flip] = I[flip][:, [0, 2, 1]]
    good = np.linalg.norm(np.cross(P[I[:, 1]] - P[I[:, 0]], P[I[:, 2]] - P[I[:, 0]]), axis=1) > 1e-10
    I = I[good]
    N = B.vnormals(P, I)
    UV = np.tile([0.5, 0.5], (len(P), 1))
    return dict(P=P, N=N, UV=UV, KW=KW, R=R, A=R.copy(), I=I, FK=np.array(['dsuit'] * len(I)), IH=np.zeros((0, 3), int))


def build(repo, A, Q, rect=None):
    B.KIND_REG.update({'dsuit': B.RSuit})
    M = Model(f'{repo}/{SRC}/driver_suited_m.glb'); src = Src(M, 1.0); src.repose(Q)
    byname = {}
    for d in M.prims: byname.setdefault(d['name'], d)
    ref = {k: DN.Part(byname[k + '_LOD0'], src).P for k in ('Suit', 'Boots', 'Gloves')}
    Jp = lambda j: src.P2[src.idx[MIX[j]]]
    body_pts = np.vstack([ref['Suit'], ref['Boots'], ref['Gloves']])
    # a tiny atlas: every texel tinted, neutral
    A.rgb[:] = 0.5; A.cov[:] = 1; A.tint[:] = 1.0; A.acc[:] = 0.0
    A.tiles['solid'] = (0, 0, A.w, A.h, 0, 0, 1, 1)
    lods = []
    for L in LODS:
        C = L['C']; nt, nl, na, nb, nh = L['st']; m = Mesh()
        up = np.array([0, 1.0, 0]); fwd = np.array([0, 0, 1.0]); side = np.array([1.0, 0, 0])
        # ---- torso: pelvis -> neck base; stripes on both flanks below the chest, belt ring, zip column, collar
        hips = Jp(PELVIS); torso = [hips + [0, -0.16, 0], Jp(SP1), Jp(SP2), Jp(SP3), Jp(NECK)]
        def torso_reg(k, n, th, c):
            y = c[1]
            if abs(y - 1.06) < 0.02: return R_STRIPE
            if y > 1.50: return R_NECK
            if abs(np.cos(th)) > 0.80 and y < 1.40: return R_STRIPE    # flank stripes
            return R_SUIT
        loft(m, torso, [PELVIS, SP1, SP2, SP3], np.vstack([p for p in [body_pts[(np.abs(body_pts[:, 0]) < 0.23)]]]), side, fwd, C, nt, 0.03, 0.30,
             torso_reg, cap0=True, cap1=False)
        # ---- legs and boots, arms and gloves (left = joint index L, right = R)
        for thigh, calf, foot, ball, sg in ((THL, CALFL, FOOTL, BALLL, -1), (THR, CALFR, FOOTR, BALLR, 1)):
            leg = [Jp(thigh), Jp(calf), Jp(foot)]
            def leg_reg(k, n, th, c, sg=sg):
                if k >= n - 2: return R_BOOT
                if np.cos(th) * -sg > 0.78 and 0.15 < k / (n - 1) < 1.0 and c[1] > 0.12: return R_STRIPE     # outer side stripe
                return R_SUIT
            # (x of the left joints is negative in Unity space; the outward side is -sign(x))
            xs = np.sign(Jp(thigh)[0]) or sg
            def leg_reg2(k, n, th, c, xs=xs):
                if k >= n - 1: return R_BOOT
                if np.cos(th) * xs > 0.8 and k > 0: return R_STRIPE
                return R_SUIT
            loft(m, leg, [thigh, calf], body_pts, side, fwd, C, nl, 0.03, 0.15, leg_reg2)
            toe = Jp(ball) + (Jp(ball) - Jp(foot)) * 0.45
            boot = [Jp(foot) + [0, 0.07, 0], Jp(foot), Jp(ball), toe]
            loft(m, boot, [foot, foot, ball], ref['Boots'], side, up, max(C * 2 // 3, 6), nb, 0.04, 0.14, lambda k, n, th, c: R_BOOT, cap1=True, scale=1.0)
        for up_j, lo_j, hand_j, clav in ((UPL, LOL, HANDL, CLAVL), (UPR, LOR, HANDR, CLAVR)):
            xs = np.sign(Jp(up_j)[0])
            arm = [Jp(up_j) - (Jp(lo_j) - Jp(up_j)) * 0.12, Jp(up_j), Jp(lo_j), Jp(hand_j)]
            def arm_reg(k, n, th, c, xs=xs):
                if k >= n - 1: return R_GLOVE                                  # cuff
                if np.cos(th) > 0.80 and 0.2 < k / (n - 1) < 0.85: return R_STRIPE   # sleeve top stripe
                return R_SUIT
            loft(m, arm, [SP3, up_j, lo_j], body_pts, up, fwd, C, na, 0.03, 0.12, arm_reg)
            tip = Jp(hand_j) + (Jp(hand_j) - Jp(lo_j)) / np.linalg.norm(Jp(hand_j) - Jp(lo_j)) * 0.17
            hand = [Jp(hand_j) - (Jp(hand_j) - Jp(lo_j)) * 0.12, Jp(hand_j), tip]
            loft(m, hand, [hand_j, hand_j], ref['Gloves'], up, fwd, max(C * 2 // 3, 6), nh, 0.03, 0.09, lambda k, n, th, c: R_GLOVE, cap1=True)
        # ---- neck (dark balaclava) and the helmet
        loft(m, [Jp(NECK) + [0, 0.0, 0], Jp(HEAD) + [0, 0.03, 0.01]], [NECK], body_pts, side, fwd, max(C // 2, 6), 3, 0.05, 0.075, lambda k, n, th, c: R_NECK)
        helmet(m, L['hr'], L['hc'], np.array([0.0, 1.655, 0.04]), 0.15, 0.145, 0.168, 0.168, 0.148)
        lods.append(finish(m))
    # per-vertex KW normalised
    for l in lods: l['KW'] = l['KW'] / np.maximum(l['KW'].sum(1, keepdims=True), 1e-9)
    height = float(lods[0]['P'][:, 1].max() - lods[0]['P'][:, 1].min())
    return src, lods, height
