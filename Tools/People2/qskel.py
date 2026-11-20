# qskel.py — reads the game's existing people.bytes (Tools/People/convert.py, 'PPL1'): the 22-joint skeleton (Unity space)
# every People rig uses, the Quaternius bodies' rest poses and the clips. People2 bodies are bound to this skeleton with
# the same rest joint ORIENTATIONS (only bone lengths differ), so every existing clip, IK and pose code works on them.
import struct
import numpy as np

KEEP = ['pelvis', 'spine_01', 'spine_02', 'spine_03', 'neck_01', 'Head',
        'clavicle_l', 'upperarm_l', 'lowerarm_l', 'hand_l', 'clavicle_r', 'upperarm_r', 'lowerarm_r', 'hand_r',
        'thigh_l', 'calf_l', 'foot_l', 'ball_l', 'thigh_r', 'calf_r', 'foot_r', 'ball_r']


def quat_mat(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def fk(parent, t, q):
    n = len(parent); W = np.zeros((n, 4, 4))
    for j in range(n):
        L = np.eye(4); L[:3, :3] = quat_mat(q[j]); L[:3, 3] = t[j]
        W[j] = L if parent[j] < 0 else W[parent[j]] @ L
    return W


class QSkel:
    def __init__(self, path):
        d = open(path, 'rb').read(); o = 0
        def rd(fmt):
            nonlocal o; v = struct.unpack_from('<' + fmt, d, o); o += struct.calcsize('<' + fmt); return v
        assert d[:4] == b'PPL1'; o = 4
        nj, = rd('i'); self.nj = nj; self.parent = list(rd(f'{nj}i'))
        nb, = rd('i'); self.bodies = []
        for b in range(nb):
            ps, = rd('f'); t = np.zeros((nj, 3)); q = np.zeros((nj, 4))
            for j in range(nj): v = rd('7f'); t[j] = v[:3]; q[j] = v[3:]
            bind = np.array([np.array(rd('16f')).reshape(4, 4) for _ in range(nj)])
            nvar, = rd('i')
            for vi in range(nvar):
                for l in range(2):
                    nv, nt = rd('ii'); o += nv * 12 + nv * 3 + nv + nv * 8 + nt * 6
            self.bodies.append(dict(pscale=ps, t=t, q=q, bind=bind, W=fk(self.parent, t, q)))
        nc, = rd('i'); self.clips = []
        for c in range(nc):
            ln, = rd('B'); name = d[o:o + ln].decode(); o += ln
            fps, n, loop = rd('fiB')
            Q = np.frombuffer(d, '<i2', n * nj * 4, o).reshape(n, nj, 4) / 32767.0; o += n * nj * 8
            P = np.frombuffer(d, '<f4', n * 3, o).reshape(n, 3).astype(float); o += n * 12
            self.clips.append(dict(name=name, fps=fps, loop=loop, q=Q, p=P))
        m = self.bodies[0]
        self.ual_pelvis_y = m['t'][0][1] / m['pscale']

    def pose_world(self, body, clip, frame):
        b = self.bodies[body]; c = self.clips[clip]
        t = b['t'].copy(); t[0] = c['p'][frame] * b['pscale']
        return fk(self.parent, t, c['q'][frame])
