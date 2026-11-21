#!/usr/bin/env python3
"""Photo textures from Adi's asset pack (RacingSim_Assets.zip, made for this game; CC0 surfaces).
   python3 pack_photos.py <pack assets/ dir>
gravel.png  1024 RGB   gravel trap: rounded 1-3 cm stones (pack textures/gravel_trap, 2 x 2 m tile), chroma 0.7
turf.png    1024 RGBA  astroturf (pack astroturf_green, 1 x 1 m tile); A = paint roughness / aggregate for the painted
                       run-off (pack runoff_paint luminance, linear, 0.5 = mean, as EnvTex.MakeTurf's alpha)
Updates means.txt (linear mean colour per albedo) the same way as make_photos.py."""
import os, sys
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
PHOTO = os.path.join(HERE, "..", "..", "Assets", "Resources", "Photo")
src = os.path.join(sys.argv[1], "textures", "RacingSimTextures", "Textures")

def lin(s8): s = s8.astype(np.float32) / 255; return np.where(s <= 0.04045, s / 12.92, ((s + 0.055) / 1.055) ** 2.4)
def srgb8(l):
    l = np.clip(l, 0, 1); s = np.where(l <= 0.0031308, l * 12.92, 1.055 * l ** (1 / 2.4) - 0.055)
    return (s * 255 + 0.5).astype(np.uint8)
def load(name, size):
    im = Image.open(os.path.join(src, name, name + "_albedo.png")).convert("RGB")
    if im.size[0] != size: im = im.resize((size, size), Image.LANCZOS)  # (Lanczos on a seamless tile: edges wrap ok at 2:1)
    return lin(np.asarray(im))
def chroma_soft(c, k):
    y = (c * [0.2126, 0.7152, 0.0722]).sum(-1, keepdims=True)
    return y + (c - y) * k

means = {}
mp = os.path.join(PHOTO, "means.txt")
for line in open(mp):
    p = line.split()
    if len(p) == 4 and not p[0].startswith("#"): means[p[0]] = [float(x) for x in p[1:]]

g = srgb8(chroma_soft(load("gravel_trap", 1024), 0.7))
Image.fromarray(g, "RGB").save(os.path.join(PHOTO, "gravel.png"), optimize=True)
means["gravel"] = lin(g).reshape(-1, 3).mean(0).tolist()

t = srgb8(load("astroturf_green", 1024))
r = load("runoff_paint", 1024); ry = (r * [0.2126, 0.7152, 0.0722]).sum(-1)
a = np.clip(0.5 + (ry - ry.mean()) / (4 * ry.std() + 1e-6), 0, 1)
rgba = np.dstack([t, (a * 255 + 0.5).astype(np.uint8)])
Image.fromarray(rgba, "RGBA").save(os.path.join(PHOTO, "turf.png"), optimize=True)
means["turf"] = lin(t).reshape(-1, 3).mean(0).tolist()

with open(mp, "w") as f:
    f.write("# linear mean colour of each photo albedo (Tools/Photo/make_photos.py, pack_photos.py); EnvTex.PhotoMean\n")
    for k, v in means.items(): f.write("%s %.5f %.5f %.5f\n" % (k, *v))
print({k: [round(x, 4) for x in v] for k, v in means.items()})
