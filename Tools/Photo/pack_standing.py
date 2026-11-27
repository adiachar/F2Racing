#!/usr/bin/env python3
"""Standing spectators strip for the fence crowds (Trackside: spectators behind the catch fences) from Adi's asset pack
(RacingSim_Assets.zip, extras/crowd/standing: renders of Microsoft Rocketbox avatars, MIT licence; see
Assets/Resources/Textures/CREDITS.txt).
   python3 pack_standing.py <pack assets/ dir>
Writes Assets/Resources/Photo/crowd_standing.png: RGBA 2048 x 512, two bands 256 px tall (bottom band 0 = calm,
top band 1 = cheering: more arms up), each a seamless row of people two deep (a back row raised 14 px, a little smaller
and darker, then the front row), about 0.52 m per person: 256 px = 2.24 m (Trackside.Spectators). Alpha = people
(cut out by Baked.shader F2_PEOPLE). Premultiplied compositing, then straight alpha with the colour bled into the gaps
so the mips don't darken the edges."""
import os, sys, glob, random
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "Assets", "Resources", "Photo", "crowd_standing.png")
fs = sorted(glob.glob(os.path.join(sys.argv[1], "extras", "crowd", "standing", "*.png")))
spr = []
for f in fs:
    im = np.asarray(Image.open(f).convert("RGBA")).astype(np.float32) / 255
    a = im[..., 3]; top = np.where(a.max(1) > 0.5)[0][0]
    pm = im.copy(); pm[..., :3] *= pm[..., 3:]
    spr.append((pm, top < 50))  # (premultiplied 128 x 256, arms up)
calm = [s for s in spr if not s[1]]; cheer = [s for s in spr if s[1]]
W, BH = 2048, 256
out = np.zeros((BH * 2, W, 4), np.float32)
rng = random.Random(7)

def paste(band, pm, x, ylift, scale, dark):
    im = Image.fromarray((pm * 255).astype(np.uint8), "RGBA")
    w, h = int(128 * scale), int(256 * scale)
    im = np.asarray(im.resize((w, h), Image.LANCZOS)).astype(np.float32) / 255
    im[..., :3] *= dark
    y0 = band * BH + (BH - h) - ylift  # rows top-down in the image; band 1 = image top half
    for dx in (-W, 0, W):
        x0 = int(x) + dx - w // 2
        xa, xb = max(0, x0), min(W, x0 + w)
        if xa >= xb: continue
        sub = im[:, xa - x0:xb - x0]
        ya, yb = max(0, y0), min(out.shape[0], y0 + h)
        sub = sub[ya - y0:yb - y0]
        dst = out[ya:yb, xa:xb]
        dst[...] = sub + dst * (1 - sub[..., 3:4])

for img_band, cheerP in ((1, 0.08), (0, 0.38)):  # image top half = texture band 1 (v 0.5..1): see below
    for layer in (0, 1):  # back row, then the front row
        n = 36 if layer == 0 else 34
        for k in range(n):
            x = (k + (0.5 if layer == 0 else 0.0)) * W / n + rng.uniform(-9, 9)
            pool = cheer if (cheer and rng.random() < cheerP) else calm
            pm, _ = rng.choice(pool)
            sc = rng.uniform(0.86, 0.96) if layer == 0 else rng.uniform(0.94, 1.0)
            paste(img_band, pm, x, 14 if layer == 0 else 0, sc, 0.8 if layer == 0 else 1.0)

a = out[..., 3:4]
rgb = np.where(a > 1e-3, out[..., :3] / np.maximum(a, 1e-3), 0)
# bleed colour into the transparent texels (a few dilation passes) so mipmaps keep the people's tone at the edges
mask = a[..., 0] > 0.02
for _ in range(8):
    acc = np.zeros_like(rgb); cnt = np.zeros(mask.shape, np.float32)
    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        m2 = np.roll(mask, (dy, dx), (0, 1)); c2 = np.roll(rgb, (dy, dx), (0, 1))
        acc += c2 * m2[..., None]; cnt += m2
    fill = (~mask) & (cnt > 0)
    rgb[fill] = acc[fill] / cnt[fill][:, None]; mask = mask | fill
res = np.dstack([np.clip(rgb, 0, 1), np.clip(a, 0, 1)])
Image.fromarray((res * 255 + 0.5).astype(np.uint8), "RGBA").save(OUT, optimize=True)
print("crowd_standing.png", os.path.getsize(OUT), "calm", len(calm), "cheer", len(cheer))
