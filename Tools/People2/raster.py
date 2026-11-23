# raster.py — a small software rasteriser for previews (numpy z-buffer, textured or flat, simple lambert light).
import numpy as np


def look(eye, at, up=(0, 1, 0)):
    eye, at, up = map(lambda a: np.asarray(a, float), (eye, at, up))
    f = at - eye; f /= np.linalg.norm(f); r = np.cross(up, f); r /= np.linalg.norm(r); u = np.cross(f, r)
    return eye, r, u, f


def render(tris_list, cam, size=(400, 600), ortho=None, fov=30.0, bg=(0.93, 0.93, 0.92), light=(0.4, 0.8, -0.5)):
    """tris_list: list of dict(P (n,3), I (m,3), uv (n,2) optional, img (PIL) optional, col (m,3) per-face optional).
    cam = look(); returns an HxWx3 float image"""
    W, H = size; eye, r, u, f = cam
    img = np.ones((H, W, 3)) * np.array(bg); zb = np.full((H, W), np.inf)
    L = np.asarray(light, float); L /= np.linalg.norm(L)
    for m in tris_list:
        P = m['P']; I = m['I']; rel = P - eye
        x = rel @ r; y = rel @ u; z = rel @ f
        if ortho:
            sx = W / 2 + x / ortho * H / 2; sy = H / 2 - y / ortho * H / 2
        else:
            k = (H / 2) / np.tan(np.radians(fov) / 2); zz = np.maximum(z, 1e-3)
            sx = W / 2 + x / zz * k; sy = H / 2 - y / zz * k
        tex = None; cut = None
        if m.get('img') is not None and m.get('uv') is not None:
            tex = np.asarray(m['img'].convert('RGB'), float) / 255.0; th, tw = tex.shape[:2]; UV = m['uv']
            if m.get('cut') is not None: cut = np.asarray(m['cut'], float) / 255.0
        fn = np.cross(P[I[:, 1]] - P[I[:, 0]], P[I[:, 2]] - P[I[:, 0]]); fn /= np.linalg.norm(fn, axis=1, keepdims=True) + 1e-12
        # Unity winding is clockwise when seen from the front: the face normal by left-handed cross is -fn
        shade = 0.45 + 0.55 * np.clip((np.abs(fn @ L) if m.get('both') else fn @ L), 0, 1)
        for t in range(len(I)):
            a, b, c = I[t]
            xs = np.array([sx[a], sx[b], sx[c]]); ys = np.array([sy[a], sy[b], sy[c]])
            x0 = max(int(np.floor(xs.min())), 0); x1 = min(int(np.ceil(xs.max())), W - 1)
            y0 = max(int(np.floor(ys.min())), 0); y1 = min(int(np.ceil(ys.max())), H - 1)
            if x0 > x1 or y0 > y1: continue
            den = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
            if abs(den) < 1e-9: continue
            gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
            w0 = ((ys[1] - ys[2]) * (gx - xs[2]) + (xs[2] - xs[1]) * (gy - ys[2])) / den
            w1 = ((ys[2] - ys[0]) * (gx - xs[2]) + (xs[0] - xs[2]) * (gy - ys[2])) / den
            w2 = 1 - w0 - w1
            ins = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
            if not ins.any(): continue
            zz = w0 * z[a] + w1 * z[b] + w2 * z[c]
            sub = zb[y0:y1 + 1, x0:x1 + 1]
            ok = ins & (zz < sub)
            if not ok.any(): continue
            if tex is not None:
                uu = w0 * UV[a, 0] + w1 * UV[b, 0] + w2 * UV[c, 0]; vv = w0 * UV[a, 1] + w1 * UV[b, 1] + w2 * UV[c, 1]
                px = np.clip((uu % 1.0) * tw, 0, tw - 1).astype(int); py = np.clip((vv % 1.0) * th, 0, th - 1).astype(int)
                col = tex[py, px]
                if cut is not None:
                    ok = ok & (cut[py, px] >= 0.5)
                    if not ok.any(): continue
                if m.get('tint') is not None: col = m['tint'](col, t)
            else:
                col = np.broadcast_to(np.asarray(m['col'][t] if m.get('col') is not None else (0.7, 0.7, 0.7)), gx.shape + (3,))
            sub[ok] = zz[ok]
            img[y0:y1 + 1, x0:x1 + 1][ok] = col[ok] * shade[t]
    return np.clip(img, 0, 1)


def save(images, path, titles=None):
    import matplotlib; matplotlib.use('Agg'); import matplotlib.pyplot as plt
    n = len(images); fig, axs = plt.subplots(1, n, figsize=(3.2 * n, 4.8))
    if n == 1: axs = [axs]
    for i, im in enumerate(images):
        axs[i].imshow(im); axs[i].axis('off')
        if titles: axs[i].set_title(titles[i], fontsize=8)
    plt.tight_layout(); plt.savefig(path, dpi=80); plt.close(fig)
