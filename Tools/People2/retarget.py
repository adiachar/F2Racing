# retarget.py — Mixamo-named skeletons (Mixamo characters and animations, Ready Player Me / Wolf3D avatars) onto the
# game's 22-joint skeleton (qskel.py).
#  * re-pose: the source's limbs are turned (minimal rotations, parents first) so every limb bone points the way the
#    game skeleton's does at rest (its T-pose); the torso keeps its own shape. The mesh follows by linear blend skinning.
#  * bind: the 22 joints sit where the re-posed source joints are, with the game skeleton's rest ORIENTATIONS, so the
#    game's clips (local rotations) drive the new body exactly as they drive the old ones.
#  * clips: a Mixamo animation becomes 22 local rotations per frame in the game's convention (world deltas from the
#    re-posed rest applied to the game skeleton's rest), plus the pelvis position scaled to the game's clip scale.
import numpy as np
from p2io import to_unity, strip, quat_mat, mat_quat, sample_channel
from qskel import KEEP

MIX = ['Hips', 'Spine', 'Spine1', 'Spine2', 'Neck', 'Head',
       'LeftShoulder', 'LeftArm', 'LeftForeArm', 'LeftHand', 'RightShoulder', 'RightArm', 'RightForeArm', 'RightHand',
       'LeftUpLeg', 'LeftLeg', 'LeftFoot', 'LeftToeBase', 'RightUpLeg', 'RightLeg', 'RightFoot', 'RightToeBase']
# limb joints re-posed to the game's directions: joint -> (source child, game joint, game child)
LIMB = [(6, 'LeftArm', 7), (7, 'LeftForeArm', 8), (8, 'LeftHand', 9), (9, 'LeftHandMiddle1', None),
        (10, 'RightArm', 11), (11, 'RightForeArm', 12), (12, 'RightHand', 13), (13, 'RightHandMiddle1', None),
        (14, 'LeftLeg', 15), (15, 'LeftFoot', 16), (16, 'LeftToeBase', 17),
        (18, 'RightLeg', 19), (19, 'RightFoot', 20), (20, 'RightToeBase', 21)]


def nrm_rot(m):
    r = m[:3, :3].copy(); return r / np.linalg.norm(r, axis=0)


def rot_between(a, b):
    a = a / np.linalg.norm(a); b = b / np.linalg.norm(b); v = np.cross(a, b); c = float(a @ b)
    if c < -0.9999:
        ax = np.cross(a, [1, 0, 0]) if abs(a[0]) < 0.9 else np.cross(a, [0, 1, 0]); ax /= np.linalg.norm(ax)
        return 2 * np.outer(ax, ax) - np.eye(3)
    K = np.array([[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]])
    return np.eye(3) + K + K @ K / (1 + c)


class Src:
    """a Mixamo-named skeleton in a glTF: nodes by stripped name, rest world positions / rotations (Unity space)"""
    def __init__(self, model, scale=1.0):
        self.M = model; G = model.G
        self.nodes = [i for i, n in enumerate(G.g.nodes)]
        self.children = {i: list(G.g.nodes[i].children or []) for i in self.nodes}
        self.idx = {k: model.byname[k] for k in MIX}
        self.scale = scale
        self.Wr = {i: to_unity(G.world(i)) for i in self.nodes}
        self.P = {i: self.Wr[i][:3, 3] * scale for i in self.nodes}
        self.R = {i: nrm_rot(self.Wr[i]) for i in self.nodes}

    def subtree(self, i):
        out = [i]; k = 0
        while k < len(out): out += self.children[out[k]]; k += 1
        return out

    def repose(self, Q):
        """rotations D[node] (world) turning the rest pose into the game's limb directions; new positions P2"""
        Qw = Q.bodies[0]['W']; Qp = Qw[:, :3, 3]
        D = {i: np.eye(3) for i in self.nodes}; P2 = dict(self.P)
        for j, child_name, gchild in LIMB:
            node = self.idx[MIX[j]]; ch = self.M.byname.get(child_name)
            if ch is None: continue
            dm = P2[ch] - P2[node]
            if gchild is None: dq = Qp[j] - Qp[Q.parent[j]]  # (the hand: along the forearm)
            else: dq = Qp[gchild] - Qp[j]
            Rt = rot_between(dm, dq); piv = P2[node].copy()
            for s in self.subtree(node):
                P2[s] = piv + Rt @ (P2[s] - piv); D[s] = Rt @ D[s]
        self.D = D; self.P2 = P2
        return D, P2

    def body_skeleton(self, Q):
        """the 22 game joints at the re-posed positions with the game's rest orientations: local t, q, world W"""
        Qb = Q.bodies[0]; Rq = Qb['W'][:, :3, :3]
        X = np.array([self.P2[self.idx[MIX[j]]] for j in range(22)])
        t = np.zeros((22, 3))
        for j in range(22):
            p = Q.parent[j]
            t[j] = X[j] if p < 0 else Rq[p].T @ (X[j] - X[p])
        q = Qb['q'].copy()
        from qskel import fk
        W = fk(Q.parent, t, q)
        return t, q, W

    def keep_of(self, node):
        """the game joint a source node's weights go to (fingers -> hand, eyes / head top -> head, toe ends -> ball)"""
        G = self.M.G; inv = {v: k for k, v in self.idx.items()}
        n = node
        while n is not None:
            nm = strip(G.g.nodes[n].name or '')
            if nm in MIX: return MIX.index(nm)
            n = G.parent.get(n)
        return 0


def skin_repose(src, P, N, J, Wt):
    """vertices (rest, Unity, scaled) skinned with the re-pose: per node v' = P2 + D (v - P)"""
    out = np.zeros_like(P); on = np.zeros_like(N)
    for k in range(J.shape[1]):
        w = Wt[:, k]
        for node in np.unique(J[:, k]):
            sel = (J[:, k] == node) & (w > 0)
            if not sel.any(): continue
            D = src.D[node]; p0 = src.P[node]; p2 = src.P2[node]
            out[sel] += w[sel, None] * (p2 + (P[sel] - p0) @ D.T)
            on[sel] += w[sel, None] * (N[sel] @ D.T)
    s = Wt.sum(1, keepdims=True); out /= np.maximum(s, 1e-9)
    on /= np.linalg.norm(on, axis=1, keepdims=True) + 1e-12
    return out, on


def weights22(src, J, Wt):
    KW = np.zeros((len(J), 22)); km = {}
    for k in range(J.shape[1]):
        for node in np.unique(J[:, k]):
            if node not in km: km[node] = src.keep_of(node)
            sel = J[:, k] == node
            KW[sel, km[node]] += Wt[sel, k]
    KW /= np.maximum(KW.sum(1, keepdims=True), 1e-9)
    return KW


# ---------------------------------------------------------------- clips
def anim_channels(G, a):
    ch = {}
    for c in a.channels:
        s = a.samplers[c.sampler]
        ch.setdefault(c.target.node, {})[c.target.path] = (G.acc(s.input)[:, 0], G.acc(s.output))
    dur = max(G.acc(s.input)[-1, 0] for s in a.samplers)
    return ch, dur


def local_at(G, ch, node, t):
    from p2io import trs
    n = G.g.nodes[node]
    T = list(n.translation or [0, 0, 0]); R = list(n.rotation or [0, 0, 0, 1]); Sc = list(n.scale or [1, 1, 1])
    c = ch.get(node, {})
    if 'translation' in c: T = sample_channel(*c['translation'], t, False)
    if 'rotation' in c: R = sample_channel(*c['rotation'], t, True)
    if 'scale' in c: Sc = sample_channel(*c['scale'], t, False)
    return trs(T, R, Sc)


def bake_clip(src, Q, anim_index=0, fps=24.0, t0=None, t1=None, loop=True):
    """a Mixamo animation on the source skeleton -> (q frames x 22 x 4 Unity local quats, pelvis frames x 3) in the game's
    convention. src must be re-posed (src.repose(Q)) first."""
    G = src.M.G; a = G.g.animations[anim_index]; ch, dur = anim_channels(G, a)
    a0 = t0 or 0.0; a1 = t1 if t1 is not None else dur
    n = max(2, int(round((a1 - a0) * fps)) + (0 if loop else 1))
    ts = [a0 + (a1 - a0) * k / (n if loop else n - 1) for k in range(n)]
    Rq = Q.bodies[0]['W'][:, :3, :3]
    hips = src.idx['Hips']; k = Q.ual_pelvis_y / src.P[hips][1]
    order = []  # nodes from the root down (for world matrices)
    roots = [i for i in src.nodes if G.parent.get(i) is None]
    for r in roots: order += src.subtree(r)
    Qs = np.zeros((n, 22, 4)); Ps = np.zeros((n, 3))
    for f, t in enumerate(ts):
        Wm = {}
        for i in order:
            L = local_at(G, ch, i, t); p = G.parent.get(i)
            Wm[i] = L if p is None else Wm[p] @ L
        Rw = np.zeros((22, 3, 3))
        for j in range(22):
            node = src.idx[MIX[j]]
            Ra = nrm_rot(to_unity(Wm[node]))
            delta = Ra @ (src.D[node] @ src.R[node]).T
            Rw[j] = delta @ Rq[j]
        for j in range(22):
            p = Q.parent[j]
            L = Rw[j] if p < 0 else Rw[p].T @ Rw[j]
            Qs[f, j] = mat_quat(L)
        Ps[f] = to_unity(Wm[hips])[:3, 3] * src.scale * k
    for j in range(22):
        for f in range(1, n):
            if np.dot(Qs[f, j], Qs[f - 1, j]) < 0: Qs[f, j] = -Qs[f, j]
    return Qs, Ps, dur
