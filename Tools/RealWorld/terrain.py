"""terrain.py - Mapzen/AWS Terrarium elevation tiles: download (cached), mosaic and bilinear sampling by lon/lat."""
import io
import math
import os
from concurrent.futures import ThreadPoolExecutor

import numpy as np
import requests
from PIL import Image

CACHE = os.environ.get("RW_CACHE", "/home/claude/rw-cache")
URL = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/%d/%d/%d.png"


def _tile(z, x, y):
    p = os.path.join(CACHE, "terrarium", str(z), str(x), "%d.png" % y)
    if not os.path.exists(p):
        os.makedirs(os.path.dirname(p), exist_ok=True)
        for k in range(5):
            try:
                r = requests.get(URL % (z, x, y), timeout=60)
                r.raise_for_status()
                break
            except Exception:
                if k == 4:
                    raise
        open(p + ".tmp", "wb").write(r.content)
        os.replace(p + ".tmp", p)
    a = np.asarray(Image.open(p).convert("RGB"), dtype=np.float64)
    return a[:, :, 0] * 256 + a[:, :, 1] + a[:, :, 2] / 256 - 32768


def _merc(lon, lat, z):
    n = 2 ** z * 256
    x = (np.asarray(lon) + 180) / 360 * n
    lr = np.radians(np.asarray(lat))
    y = (1 - np.log(np.tan(lr) + 1 / np.cos(lr)) / math.pi) / 2 * n
    return x, y


class Dem:
    """Elevation mosaic covering a lon/lat box at zoom z."""

    def __init__(self, box, z=14):
        self.z = z
        x0, y1 = _merc(box[0], box[1], z)
        x1, y0 = _merc(box[2], box[3], z)
        self.tx0, self.ty0 = int(x0 // 256), int(y0 // 256)
        tx1, ty1 = int(x1 // 256), int(y1 // 256)
        jobs = [(z, tx, ty) for ty in range(self.ty0, ty1 + 1) for tx in range(self.tx0, tx1 + 1)]
        with ThreadPoolExecutor(16) as ex:
            tiles = list(ex.map(lambda j: _tile(*j), jobs))
        nx, ny = tx1 - self.tx0 + 1, ty1 - self.ty0 + 1
        self.a = np.zeros((ny * 256, nx * 256))
        for (z_, tx, ty), t in zip(jobs, tiles):
            self.a[(ty - self.ty0) * 256:(ty - self.ty0 + 1) * 256, (tx - self.tx0) * 256:(tx - self.tx0 + 1) * 256] = t

    def smooth(self, metres, lat):
        """Light Gaussian low-pass (removes pixel noise, tree-canopy and roof bumps of the surface model)."""
        from scipy.ndimage import gaussian_filter
        px = 40075016.686 * math.cos(math.radians(lat)) / 2 ** (self.z + 8)
        self.a = gaussian_filter(self.a, metres / px)

    def sample(self, lon, lat):
        x, y = _merc(lon, lat, self.z)
        # pixel centres sit at +0.5
        fx = x - self.tx0 * 256 - 0.5
        fy = y - self.ty0 * 256 - 0.5
        h, w = self.a.shape
        fx = np.clip(fx, 0, w - 1.001)
        fy = np.clip(fy, 0, h - 1.001)
        i, j = fx.astype(int), fy.astype(int)
        u, v = fx - i, fy - j
        a = self.a
        return (a[j, i] * (1 - u) + a[j, i + 1] * u) * (1 - v) + (a[j + 1, i] * (1 - u) + a[j + 1, i + 1] * u) * v
