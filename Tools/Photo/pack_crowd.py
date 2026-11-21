#!/usr/bin/env python3
"""Seated crowd sprites for the grandstand texture (TracksideArt.Crowd) from Adi's asset pack (RacingSim_Assets.zip,
extras/crowd/seated: renders of Microsoft Rocketbox avatars, MIT licence, see Docs/ASSETS.md).
   python3 pack_crowd.py <pack assets/ dir>
Writes Assets/Resources/Textures/crowd_people.bytes: PNG RGBA 1024 x 128 = two rows of up to 32 cells of 32 x 64 px
(one seat of the crowd texture): top row arms down, bottom row arms up; empty cells are fully transparent."""
import os, sys, glob
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "Assets", "Resources", "Textures", "crowd_people.bytes")
fs = sorted(glob.glob(os.path.join(sys.argv[1], "extras", "crowd", "seated", "*.png")))
down, up = [], []
for f in fs:
    im = Image.open(f).convert("RGBA")
    a = np.asarray(im)[..., 3]
    rows = np.where(a.max(1) > 128)[0]
    cols = np.where(a[: rows[0] + 30].max(0) > 128)[0]
    # arms up: the topmost opaque rows are two narrow columns (hands) well above the head
    top = rows[0]
    (up if top < 90 else down).append(im)
def cell(im):
    # the person fills the seat: an 88 x 176 window (bottom = the feet, centred) -> 32 x 64
    a = np.asarray(im)[..., 3]; bot = np.where(a.max(1) > 128)[0][-1] + 2
    im = im.crop((20, bot - 176, 108, bot))
    # premultiplied downscale (no dark fringes), then back to straight alpha
    p = np.asarray(im).astype(np.float32) / 255
    p[..., :3] *= p[..., 3:]
    s = np.asarray(Image.fromarray((p * 255 + 0.5).astype(np.uint8), "RGBA").resize((32, 64), Image.LANCZOS)).astype(np.float32) / 255
    a = s[..., 3:]
    s[..., :3] = np.where(a > 1e-3, s[..., :3] / np.maximum(a, 1e-3), 0)
    return (np.clip(s, 0, 1) * 255 + 0.5).astype(np.uint8)
sheet = np.zeros((128, 1024, 4), np.uint8)
for r, lst in enumerate((down, up)):
    for i, im in enumerate(lst[:32]):
        sheet[r * 64:(r + 1) * 64, i * 32:(i + 1) * 32] = cell(im)
Image.fromarray(sheet, "RGBA").save(OUT, "PNG", optimize=True)
Image.fromarray(sheet, "RGBA").save("/tmp/ra/crowd_people.png")
print("arms down", len(down), "arms up", len(up), os.path.getsize(OUT))
