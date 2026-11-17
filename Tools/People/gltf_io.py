# Minimal glTF / GLB reading for the people converter (pygltflib + numpy): accessors, node matrices, skins, clips.
import os
import numpy as np
import pygltflib

COMP = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
NCOMP = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}


class Gltf:
    def __init__(self, path):
        self.path = path
        self.g = pygltflib.GLTF2().load(path)
        self.blobs = {}
        self.parent = {}
        for i, n in enumerate(self.g.nodes):
            for c in (n.children or []):
                self.parent[c] = i
        self.byname = {n.name: i for i, n in enumerate(self.g.nodes)}

    def buffer(self, i):
        if i not in self.blobs:
            b = self.g.buffers[i]
            if b.uri is None:
                self.blobs[i] = self.g.binary_blob()
            else:
                self.blobs[i] = open(os.path.join(os.path.dirname(self.path), b.uri), 'rb').read()
        return self.blobs[i]

    def acc(self, i):
        a = self.g.accessors[i]
        bv = self.g.bufferViews[a.bufferView]
        dt = np.dtype(COMP[a.componentType]); n = NCOMP[a.type]
        off = (bv.byteOffset or 0) + (a.byteOffset or 0)
        stride = bv.byteStride or dt.itemsize * n
        raw = self.buffer(bv.buffer)
        if stride == dt.itemsize * n:
            arr = np.frombuffer(raw, dt, a.count * n, off).reshape(a.count, n)
        else:
            arr = np.stack([np.frombuffer(raw, dt, n, off + k * stride) for k in range(a.count)])
        arr = arr.astype(np.float64) if dt != np.float32 or not a.normalized else arr.astype(np.float64)
        if a.normalized and dt != np.float32:
            arr = arr / float(np.iinfo(dt).max)
        return arr

    def local(self, i):
        n = self.g.nodes[i]
        if n.matrix:
            return np.array(n.matrix, dtype=np.float64).reshape(4, 4).T
        t = n.translation or [0, 0, 0]; r = n.rotation or [0, 0, 0, 1]; s = n.scale or [1, 1, 1]
        return trs(t, r, s)

    def world(self, i):
        m = self.local(i)
        p = self.parent.get(i)
        while p is not None:
            m = self.local(p) @ m
            p = self.parent.get(p)
        return m

    def image(self, i):
        from PIL import Image
        import io
        im = self.g.images[i]
        if im.bufferView is not None:
            bv = self.g.bufferViews[im.bufferView]
            raw = self.buffer(bv.buffer)[(bv.byteOffset or 0):(bv.byteOffset or 0) + bv.byteLength]
            return Image.open(io.BytesIO(raw)).convert('RGB')
        return Image.open(os.path.join(os.path.dirname(self.path), im.uri)).convert('RGB')


def quat_mat(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def mat_quat(m):
    t = np.trace(m)
    if t > 0:
        s = np.sqrt(t + 1.0) * 2; w = 0.25 * s
        x = (m[2, 1] - m[1, 2]) / s; y = (m[0, 2] - m[2, 0]) / s; z = (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = np.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w = (m[2, 1] - m[1, 2]) / s; x = 0.25 * s; y = (m[0, 1] + m[1, 0]) / s; z = (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = np.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w = (m[0, 2] - m[2, 0]) / s; x = (m[0, 1] + m[1, 0]) / s; y = 0.25 * s; z = (m[1, 2] + m[2, 1]) / s
    else:
        s = np.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w = (m[1, 0] - m[0, 1]) / s; x = (m[0, 2] + m[2, 0]) / s; y = (m[1, 2] + m[2, 1]) / s; z = 0.25 * s
    q = np.array([x, y, z, w]); return q / np.linalg.norm(q)


def trs(t, r, s):
    m = np.eye(4)
    m[:3, :3] = quat_mat(r) * np.array(s)[None, :]
    m[:3, 3] = t
    return m


def slerp(a, b, t):
    d = np.dot(a, b)
    if d < 0: b = -b; d = -d
    if d > 0.9995:
        q = a + (b - a) * t; return q / np.linalg.norm(q)
    th = np.arccos(d); s = np.sin(th)
    return (np.sin((1 - t) * th) * a + np.sin(t * th) * b) / s


def sample_channel(times, vals, t, rot):
    if t <= times[0]: return vals[0]
    if t >= times[-1]: return vals[-1]
    k = np.searchsorted(times, t) - 1
    u = (t - times[k]) / (times[k + 1] - times[k])
    return slerp(vals[k], vals[k + 1], u) if rot else vals[k] * (1 - u) + vals[k + 1] * u
