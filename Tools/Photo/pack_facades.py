#!/usr/bin/env python3
"""Building-atlas cells from Adi's asset pack facades (RacingSim_Assets.zip, CC0, made for this game).
   python3 pack_facades.py <pack assets/ dir>
Writes Assets/Resources/Textures/fac_c<cell>.bytes: PNG RGBA 256 x 256, one atlas tile (EnvTex.BuildingAtlas uses it in
place of the painted cell). The pack tile is 4 m wide x 3 m tall (two bays, one storey); the atlas tile is one storey.
rgb: sRGB colour. a: the atlas alpha: 0 = window glass (pack roughness < 40), else the tint mask (>= 14): 255 on the
plain wall, whose colour is made neutral (PlasterLin x the wall's own light and dark) so each building still takes its
colour from RealWorld's palette (Baked.shader uv3); 14 on frames, shutters, sills, doors (they keep their colours)."""
import os, sys
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "Assets", "Resources", "Textures")
SRC = os.path.join(sys.argv[1], "buildings", "facades")
N = 256
PLASTER = np.array([0.80, 0.79, 0.77])
# atlas cell -> (pack facade, tint the wall?)  (cells: EnvTex.BuildingAtlas comment)
CELLS = {0: ("facade_apartment_1", True),   # green shutters
         1: ("facade_apartment_4", True),   # brown shutters
         2: ("facade_apartment_3", True),   # roller blind
         4: ("facade_apartment_2", True),   # flats with a balcony
         6: ("facade_glass_office", False), # glass curtain wall
         7: ("facade_brick_old", False),    # arched window (brick)
         11: ("facade_garage_doors_closed", False),
         14: ("facade_concrete_industrial", True)}

def lin(s8): s = s8.astype(np.float32) / 255; return np.where(s <= 0.04045, s / 12.92, ((s + 0.055) / 1.055) ** 2.4)
def srgb8(l):
    l = np.clip(l, 0, 1); s = np.where(l <= 0.0031308, l * 12.92, 1.055 * l ** (1 / 2.4) - 0.055)
    return (s * 255 + 0.5).astype(np.uint8)
def smooth(e0, e1, x): t = np.clip((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t)

for cell, (name, tint) in CELLS.items():
    alb = Image.open(os.path.join(SRC, name + "_albedo.png")).convert("RGB").resize((N, N), Image.LANCZOS)
    rough = Image.open(os.path.join(SRC, name + "_rough.png")).convert("L").resize((N, N), Image.BILINEAR)
    c = lin(np.asarray(alb)); r = np.asarray(rough).astype(np.float32)
    glass = r < 40
    a = np.full((N, N), 14.0)
    if tint:
        q = (np.asarray(alb) // 16).reshape(-1, 3)
        keys, cnt = np.unique(q[~glass.reshape(-1)], axis=0, return_counts=True)
        mode = keys[cnt.argmax()]
        sel = np.all(q == mode, axis=1).reshape(N, N)
        wall = c[sel].mean(0)
        d = np.sqrt((((c - wall) / (wall + 0.03)) ** 2).sum(-1))
        m = 1 - smooth(0.25, 0.6, d)
        m = np.asarray(Image.fromarray((m * 255).astype(np.uint8)).filter(__import__('PIL.ImageFilter', fromlist=['x']).GaussianBlur(1.2))).astype(np.float32) / 255
        m = smooth(0.35, 0.75, m); m[glass] = 0
        wl = (wall * [0.2126, 0.7152, 0.0722]).sum()
        neutral = ((c * [0.2126, 0.7152, 0.0722]).sum(-1, keepdims=True) / wl) * PLASTER
        c = c * (1 - m[..., None]) + neutral * m[..., None]
        a = np.maximum(14, m * 255)
    a[glass] = 0
    rgba = np.dstack([srgb8(c), (a + 0.5).astype(np.uint8)])
    path = os.path.join(OUT, "fac_c%02d.bytes" % cell)
    Image.fromarray(rgba, "RGBA").save(path, "PNG", optimize=True)
    Image.fromarray(rgba, "RGBA").save("/tmp/ra/fac_c%02d.png" % cell)
    print(cell, name, os.path.getsize(path), "wall share %.2f" % (a > 128).mean(), "glass %.2f" % glass.mean())
