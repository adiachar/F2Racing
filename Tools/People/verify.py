# verify.py — reads people.bytes exactly as People.cs does and renders skinned poses (sanity check of the data):
#   python3 -I verify.py <people.bytes> <out.png> [clip names...]
import sys, struct
import numpy as np


def qmat(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def read(path):
    d = open(path, 'rb').read(); o = [0]
    def u(fmt):
        v = struct.unpack_from('<' + fmt, d, o[0]); o[0] += struct.calcsize('<' + fmt); return v
    assert d[:4] == b'PPL1'; o[0] = 4
    nj, = u('i'); par = u(f'{nj}i'); nb, = u('i'); bodies = []
    for b in range(nb):
        ps, = u('f'); t = []; q = []
        for j in range(nj): v = u('7f'); t.append(v[:3]); q.append(v[3:])
        bind = [np.array(u('16f')).reshape(4, 4) for j in range(nj)]
        nv_, = u('i'); meshes = []
        for v in range(nv_):
            lods = []
            for l in range(2):
                nv, nt = u('ii')
                P = np.frombuffer(d, '<f4', nv * 3, o[0]).reshape(-1, 3); o[0] += nv * 12
                N = np.frombuffer(d, 'i1', nv * 3, o[0]).reshape(-1, 3); o[0] += nv * 3
                R = np.frombuffer(d, 'u1', nv, o[0]); o[0] += nv
                BI = np.frombuffer(d, 'u1', nv * 4, o[0]).reshape(-1, 4); o[0] += nv * 4
                BW = np.frombuffer(d, 'u1', nv * 4, o[0]).reshape(-1, 4) / 255.0; o[0] += nv * 4
                F = np.frombuffer(d, '<u2', nt * 3, o[0]).reshape(-1, 3).astype(int); o[0] += nt * 6
                lods.append((P, N, R, BI, BW, F))
            meshes.append(lods)
        bodies.append(dict(ps=ps, t=np.array(t), q=np.array(q), bind=bind, meshes=meshes))
    nc, = u('i'); clips = {}
    for c in range(nc):
        ln, = u('B'); name = d[o[0]:o[0] + ln].decode(); o[0] += ln
        fps, n, loop = u('fiB')
        Q = np.frombuffer(d, '<i2', n * nj * 4, o[0]).reshape(n, nj, 4) / 32767.0; o[0] += n * nj * 8
        T = np.frombuffer(d, '<f4', n * 3, o[0]).reshape(n, 3); o[0] += n * 12
        clips[name] = (fps, Q, T, loop)
    return par, bodies, clips


def skin(par, b, mesh, q, p):
    nj = len(par); W = [None] * nj
    for j in range(nj):
        L = np.eye(4); L[:3, :3] = qmat(q[j] / np.linalg.norm(q[j])); L[:3, 3] = b['t'][j] if j else p * b['ps']
        W[j] = L if par[j] < 0 else W[par[j]] @ L
    P, N, R, BI, BW, F = mesh
    M = np.zeros((len(P), 4, 4))
    for k in range(4): M += BW[:, k, None, None] * np.array([W[j] @ b['bind'][j] for j in range(nj)])[BI[:, k]]
    return np.einsum('vab,vb->va', M, np.c_[P, np.ones(len(P))])[:, :3]


if __name__ == '__main__':
    import matplotlib; matplotlib.use('Agg'); import matplotlib.pyplot as plt
    from matplotlib.collections import PolyCollection
    par, bodies, clips = read(sys.argv[1])
    names = sys.argv[3:] or list(clips)
    fig, axs = plt.subplots(1, len(names), figsize=(2.2 * len(names), 3.2))
    axs = np.atleast_1d(axs)
    cm = plt.get_cmap('tab20')
    for i, nm in enumerate(names):
        fps, Q, T, loop = clips[nm]; f = len(Q) // 3
        b = bodies[i % 2]; mesh = b['meshes'][i % 4][0]
        V = skin(par, b, mesh, Q[f], T[f]); F = mesh[5]; R = mesh[2]
        ax = axs[i]; polys = []; cols = []
        for fi in np.argsort(V[F][:, :, 0].mean(1)):  # side view: looking along -x (from the character's right)
            tri = F[fi]; n = np.cross(V[tri[1]] - V[tri[0]], V[tri[2]] - V[tri[0]])
            if n[0] >= 0: continue  # Unity (left-handed) winding: front faces have this sign towards a viewer at -x? keep the visible ones
            polys.append(np.c_[V[tri, 2], V[tri, 1]]); cols.append(cm(int(R[tri[0]]) % 20))
        ax.add_collection(PolyCollection(polys, facecolors=cols, edgecolors='none'))
        ax.set_xlim(-1, 1); ax.set_ylim(-0.05, 2.1); ax.set_aspect('equal'); ax.axis('off'); ax.set_title(nm, fontsize=8)
    plt.tight_layout(); plt.savefig(sys.argv[2], dpi=70)
