# Skeleton / clip helpers for the people converter: the reduced 22-joint skeleton in Unity space (left-handed, x flipped),
# clip sampling from the Universal Animation Library glTFs (Quaternius, CC0), forward kinematics.
import numpy as np
from gltf_io import Gltf, quat_mat, mat_quat, trs, sample_channel

KEEP = ['pelvis', 'spine_01', 'spine_02', 'spine_03', 'neck_01', 'Head',
        'clavicle_l', 'upperarm_l', 'lowerarm_l', 'hand_l', 'clavicle_r', 'upperarm_r', 'lowerarm_r', 'hand_r',
        'thigh_l', 'calf_l', 'foot_l', 'ball_l', 'thigh_r', 'calf_r', 'foot_r', 'ball_r']
PARENT = [-1, 0, 1, 2, 3, 4, 3, 6, 7, 8, 3, 10, 11, 12, 0, 14, 15, 16, 0, 18, 19, 20]
NJ = len(KEEP)
S = np.diag([-1.0, 1.0, 1.0, 1.0])  # glTF (right-handed) -> Unity (left-handed): flip x


def to_unity(m):
    return S @ m @ S


def keep_of(G, node):
    """the kept joint a source node's weights go to (fingers -> hand, leaves -> their parent, root -> pelvis)"""
    while node is not None:
        nm = G.g.nodes[node].name
        if nm in KEEP:
            return KEEP.index(nm)
        if nm == 'root':
            return 0
        node = G.parent.get(node)
    return 0


class Clips:
    """the animations of one glTF: local TRS of every node at time t"""

    def __init__(self, G):
        self.G = G
        self.anims = {}
        for a in G.g.animations:
            ch = {}
            for c in a.channels:
                s = a.samplers[c.sampler]
                ch.setdefault(c.target.node, {})[c.target.path] = (G.acc(s.input)[:, 0], G.acc(s.output))
            dur = max(G.acc(s.input)[-1, 0] for s in a.samplers)
            self.anims[a.name] = (ch, dur)

    def local(self, name, t, node):
        G = self.G; n = G.g.nodes[node]
        T = list(n.translation or [0, 0, 0]); R = list(n.rotation or [0, 0, 0, 1]); Sc = list(n.scale or [1, 1, 1])
        ch = self.anims[name][0].get(node, {})
        if 'translation' in ch: T = sample_channel(*ch['translation'], t, False)
        if 'rotation' in ch:
            R = sample_channel(*ch['rotation'], t, True)
        if 'scale' in ch: Sc = sample_channel(*ch['scale'], t, False)
        return trs(T, R, Sc)

    def world(self, name, t, node):
        G = self.G; m = self.local(name, t, node); p = G.parent.get(node)
        while p is not None:
            m = self.local(name, t, p) @ m; p = G.parent.get(p)
        return m

    def pose(self, name, t):
        """kept joints: local rotations (Unity quats x,y,z,w) and the pelvis's model-space translation (Unity)"""
        G = self.G
        q = np.zeros((NJ, 4)); pel = None
        for j, nm in enumerate(KEEP):
            node = G.byname[nm]
            if j == 0:
                W = to_unity(self.world(name, t, node))
                q[0] = mat_quat(W[:3, :3] / np.linalg.norm(W[:3, :3], axis=0)); pel = W[:3, 3].copy()
            else:
                L = to_unity(self.local(name, t, node))
                q[j] = mat_quat(L[:3, :3] / np.linalg.norm(L[:3, :3], axis=0))
        return q, pel


def rest_skeleton(G):
    """kept joints of a character in its node rest pose, Unity space: local translations (pelvis: model space), local
    rotations, world matrices"""
    t = np.zeros((NJ, 3)); q = np.zeros((NJ, 4)); W = np.zeros((NJ, 4, 4))
    for j, nm in enumerate(KEEP):
        node = G.byname[nm]
        M = to_unity(G.world(node) if j == 0 else G.local(node))
        r = M[:3, :3] / np.linalg.norm(M[:3, :3], axis=0)
        t[j] = M[:3, 3]; q[j] = mat_quat(r)
    W = fk(t, q)
    return t, q, W


def fk(t, q):
    W = np.zeros((NJ, 4, 4))
    for j in range(NJ):
        L = np.eye(4); L[:3, :3] = quat_mat(q[j]); L[:3, 3] = t[j]
        W[j] = L if PARENT[j] < 0 else W[PARENT[j]] @ L
    return W
