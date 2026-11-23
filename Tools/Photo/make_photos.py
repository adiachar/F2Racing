#!/usr/bin/env python3
"""
Photo texture pipeline (graphics account, Docs/ASSETS.md "Photo textures"): CC0 photo scans -> mobile-ready files.

    python3 Tools/Photo/make_photos.py --fetch /tmp/photo_src    # download the sources (GitHub mirrors, see SOURCES)
    python3 Tools/Photo/make_photos.py /tmp/photo_src            # write the game files

Poly Haven and ambientCG themselves are blocked on the build network, so the files come from GitHub repositories that
carry the original, unmodified CC0 files (Git LFS, fetched through media.githubusercontent.com). The licence is the
original one: CC0 1.0 (Poly Haven, ambientCG).

Outputs
  Assets/Resources/Photo/  (imported by Unity; Assets/Editor/F2TextureImport.cs sets ASTC, mips, aniso, wrap)
    grass.png         1024 RGBA  mown turf (ambientCG Grass001): colour softened to the photo's mean (chroma 0.45, as
                                 EnvTex.GroundDetail did at runtime); A = clumps (tileable fbm, 0.5 = neutral)
    gravel.png        1024 RGB   gravel trap stones (Poly Haven gravel_floor_02), chroma 0.7 around its mean
    sky_day.png       8192x2048  clear afternoon sky with fair-weather cumulus (Poly Haven kloofendal_48d_partly_cloudy
                                 _puresky, 8K, sun 47.9 deg up), upper hemisphere only: u = azimuth with the sun at u = 0,
                                 v = sin(elevation) (0 horizon .. 1 zenith: no asin in the shader, more rows near the
                                 horizon where the clouds bunch up); tone-mapped to display values (no HDR format needed)
    sky_evening_hq.png 4096x1024 golden-hour cumulus (Poly Haven kloppenheim_06_puresky, sun 8 deg), ASTC 4x4
    sky_morning_hq.png 4096x1024 clear hazy morning (Poly Haven autumn_field_puresky, sun 29 deg), ASTC 4x4
    sky_overcast.png  4096x1024  rain overcast (Poly Haven overcast_soil_puresky, no sun), ASTC 6x6
    means.txt         "name r g b" linear mean colour of each albedo (the shader gain = circuit colour / mean)
  Assets/Resources/Textures/  (JPEG / PNG bytes, decoded at load by EnvTex.LoadPhoto for the runtime-built textures)
    asphalt.bytes     1024 JPEG  Poly Haven asphalt_track (EnvTex.RoadDetail R: the road's fine grain, 3.2 m tile)
    kerbconc.bytes    1024 JPEG  Poly Haven concrete_floor_worn_001 (EnvTex.RoadDetail G: kerb concrete grain)
    leafspray.bytes   1024x512 PNG RGBA  ambientCG LeafSet002 / LeafSet005 sprays cut out (EnvTex.Foliage clusters)

All tiling sources are seamless 2048 squares; they are halved with an exact 2x2 box, which keeps them seamless.
"""
import os, sys, urllib.request
import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
PHOTO = os.path.join(ROOT, "Assets", "Resources", "Photo")
TEX = os.path.join(ROOT, "Assets", "Resources", "Textures")
MEDIA = "https://media.githubusercontent.com/media/"
SC = "kennyhassmusic/sky-city/HEAD/SourceArt/CC0/"
SOURCES = {  # local file -> (mirror path, original page)
    "grass001.jpg": ("RosaryMala/BlaseballStadiumViewer/HEAD/Assets/Textures/Grass001_2K_Color.jpg", "https://ambientcg.com/view?id=Grass001"),
    "gravel_floor_02.jpg": (SC + "polyhaven/gravel_floor_02/gravel_floor_02_diff_2k.jpg", "https://polyhaven.com/a/gravel_floor_02"),
    "asphalt_track.jpg": ("Rodrigo-AO/FS_assetto_track_creator/HEAD/presets/common/cc0/asphalt_track_diff.jpg", "https://polyhaven.com/a/asphalt_track"),
    "concrete_floor_worn_001.jpg": ("Kitjesen/MapPilot/HEAD/sim/library/sources/polyhaven/textures/concrete_floor_worn_001/2k/concrete_floor_worn_001_diff_2k.jpg", "https://polyhaven.com/a/concrete_floor_worn_001"),
    "sky.hdr": ("cdav1990/OverWatch2/HEAD/FrontEnd/public/hdr/kloofendal_48d_partly_cloudy_puresky_8k.hdr", "https://polyhaven.com/a/kloofendal_48d_partly_cloudy_puresky"),
    "sky_evening.hdr": ("Brooklyn-Dev/ray-tracing/HEAD/skyboxes/kloppenheim_06_puresky_4k.hdr", "https://polyhaven.com/a/kloppenheim_06_puresky"),
    "sky_morning.hdr": ("FionaWright/CherryPip/HEAD/Assets/Textures/Env%20Maps/autumn_field_puresky_4k.hdr", "https://polyhaven.com/a/autumn_field_puresky"),
    "sky_overcast.hdr": ("efimco/BakeForge/HEAD/res/overcast_soil_puresky_4k.hdr", "https://polyhaven.com/a/overcast_soil_puresky"),
    # wave 2: trackside (zinc rail, cast concrete wall) and the building atlas photos (512 px, were 256)
    "metal032.jpg": ("rito15/Unity-Shader-Graph-Study/HEAD/Textures/Metalic/Metal032_2K_Color.jpg", "https://ambientcg.com/view?id=Metal032"),
    "concrete_wall_008.jpg": ("i-machine-think/grasp/HEAD/GRASP/Assets/Resources/Textures/ConcreteWall/concrete_wall_008_diff_4k.jpg", "https://polyhaven.com/a/concrete_wall_008"),
    "plastered_wall.png": ("Kacper79/UKSW_Escape_Within_Reach_Gr_1/HEAD/Assets/Textures/plastered_wall_diff_2k.png", "https://polyhaven.com/a/plastered_wall"),
    "red_brick.jpg": ("mitarthpathak/Blender-Fable-5.2/HEAD/game/assets/town/textures/red_brick_diff_2k.jpg", "https://polyhaven.com/a/red_brick"),
    "clay_roof_tiles_02.jpg": ("dunkean/magna-urbis/HEAD/assets/materials/buildings/roof/polyhaven-clay_roof_tiles_02/clay_roof_tiles_02_diff_1k.jpg", "https://polyhaven.com/a/clay_roof_tiles_02"),
    "concrete_slab_wall.jpg": ("shadow-kernel/Vortex-Engine-OpenWorld-Template/HEAD/Assets/Textures/concrete_slab_wall/concrete_slab_wall_diff_2k.jpg", "https://polyhaven.com/a/concrete_slab_wall"),
    "box_profile_metal_sheet.jpg": ("Shahidul-Khan2004/UIU-Sim/HEAD/unity-client/UIU-Sim/Assets/Art/Environment/Walls/UIU%20Front/box_profile_metal_sheet_diff_2k.jpg", "https://polyhaven.com/a/box_profile_metal_sheet"),
    "corrugated_iron_02.jpg": ("Rodrigo-AO/FS_assetto_track_creator/HEAD/presets/common/cc0/corrugated_iron_02_diff.jpg", "https://polyhaven.com/a/corrugated_iron_02"),
    "painted_metal_shutter.png": ("mrxizao/gundam-hathaway/HEAD/assets/materials/adelaide_pbr/painted_metal_shutter_diff_2k.png", "https://polyhaven.com/a/painted_metal_shutter"),
    "leafset002_col.jpg": (SC + "ambientcg/LeafSet002/LeafSet002_2K-JPG_Color.jpg", "https://ambientcg.com/view?id=LeafSet002"),
    "leafset002_op.jpg": (SC + "ambientcg/LeafSet002/LeafSet002_2K-JPG_Opacity.jpg", "https://ambientcg.com/view?id=LeafSet002"),
    "leafset005_col.jpg": (SC + "ambientcg/LeafSet005/LeafSet005_2K-JPG_Color.jpg", "https://ambientcg.com/view?id=LeafSet005"),
    "leafset005_op.jpg": (SC + "ambientcg/LeafSet005/LeafSet005_2K-JPG_Opacity.jpg", "https://ambientcg.com/view?id=LeafSet005"),
}


def fetch(dst):
    os.makedirs(dst, exist_ok=True)
    for name, (path, _) in SOURCES.items():
        out = os.path.join(dst, name)
        if os.path.exists(out) and os.path.getsize(out) > 4096:
            continue
        print("fetch", name)
        urllib.request.urlretrieve(MEDIA + path, out)


def lin(srgb8):
    s = srgb8.astype(np.float32) / 255.0
    return np.where(s <= 0.04045, s / 12.92, ((s + 0.055) / 1.055) ** 2.4)


def srgb8(linv):
    l = np.clip(linv, 0.0, 1.0)
    s = np.where(l <= 0.0031308, l * 12.92, 1.055 * l ** (1 / 2.4) - 0.055)
    return (s * 255.0 + 0.5).astype(np.uint8)


def half(a):  # exact 2x2 box: a seamless tile stays seamless
    h, w = a.shape[:2]
    return a.reshape(h // 2, 2, w // 2, 2, *a.shape[2:]).mean(axis=(1, 3))


def load_lin(path, size=1024):
    im = Image.open(path)
    if im.mode.startswith("I;16") or im.mode == "I":       # 16-bit PNG
        im = Image.fromarray((np.asarray(im, np.float32) / 257.0).clip(0, 255).astype(np.uint8))
    a = lin(np.asarray(im.convert("RGB")))
    while a.shape[0] > size:
        a = half(a)
    return a


def luma(c):
    return c[..., 0] * 0.299 + c[..., 1] * 0.587 + c[..., 2] * 0.114


def chroma_soft(c, k):
    """colour relative to the mean colour with its chroma scaled by k (EnvTex.Photo.Rel), back in albedo units"""
    m = c.reshape(-1, 3).mean(axis=0)
    l = luma(c) / max(1e-4, luma(m[None, None, :])[0, 0])
    l = np.clip(l, 0.05, 4.0)[..., None]
    rr = c / np.maximum(m, 1e-4)
    return m * l * (1.0 + (rr / l - 1.0) * k), m


# tileable value noise, the same family as EnvTex.Fbm (period 1 over the tile)
def vnoise(n, cells, seed):
    rng = np.random.RandomState(seed)
    g = rng.rand(cells, cells).astype(np.float32)
    t = (np.arange(n, dtype=np.float32) + 0.5) / n * cells
    i0 = np.floor(t).astype(int) % cells
    i1 = (i0 + 1) % cells
    f = t - np.floor(t)
    f = f * f * (3 - 2 * f)
    a = g[i0][:, i0] * (1 - f)[None, :] + g[i0][:, i1] * f[None, :]
    b = g[i1][:, i0] * (1 - f)[None, :] + g[i1][:, i1] * f[None, :]
    return a * (1 - f)[:, None] + b * f[:, None]


def fbm(n, cells, octaves, seed):
    s, amp, norm = 0, 1.0, 0
    for o in range(octaves):
        s = s + amp * vnoise(n, cells, seed + o * 7)
        norm += amp
        amp *= 0.5
        cells *= 2
    return s / norm


means = {}


def grass(src):
    c = load_lin(os.path.join(src, "grass001.jpg"))
    r, m = chroma_soft(c, 0.45)
    # as GroundDetail: half the softened colour, half plain luminance detail (contrast 0.8)
    l = np.clip(luma(c) / luma(m[None, None, :])[0, 0], 0.05, 4.0)[..., None]
    k = m * (1.0 + 0.8 * (l - 1.0))
    out = np.maximum(0.05 * m, 0.5 * r + 0.5 * k)
    cl = fbm(1024, 6, 3, 301)
    cl = (cl - cl.min()) / (cl.max() - cl.min())
    x = np.clip((cl - 0.32) / 0.36, 0, 1)
    clumps = x * x * (3 - 2 * x)
    rgba = np.dstack([srgb8(out), (clumps * 255 + 0.5).astype(np.uint8)])
    Image.fromarray(rgba, "RGBA").save(os.path.join(PHOTO, "grass.png"), optimize=True)
    means["grass"] = lin(rgba[..., :3]).reshape(-1, 3).mean(axis=0)


def gravel(src):
    c = load_lin(os.path.join(src, "gravel_floor_02.jpg"))
    out, m = chroma_soft(c, 0.7)
    rgb = srgb8(out)
    Image.fromarray(rgb, "RGB").save(os.path.join(PHOTO, "gravel.png"), optimize=True)
    means["gravel"] = lin(rgb).reshape(-1, 3).mean(axis=0)


def jpeg_bytes(src, name, out, q=92, size=1024):
    c = load_lin(os.path.join(src, name), size)
    Image.fromarray(srgb8(c), "RGB").save(os.path.join(TEX, out), "JPEG", quality=q, optimize=True)


def read_hdr(path):
    import cv2  # Radiance .hdr reader
    im = cv2.imread(path, cv2.IMREAD_ANYDEPTH | cv2.IMREAD_COLOR)
    return im[:, :, ::-1].astype(np.float32)


# name, source, out, rows, exposure target (luminance of the 25-40 deg band), blue boost, sun (True: roll it to u = 0)
SKIES = [
    ("day", "sky.hdr", "sky_day.png", 2048, 0.42, 0.4, True),            # 8K: 8192 x 2048
    ("evening", "sky_evening.hdr", "sky_evening_hq.png", 1024, 0.30, 0.0, True),   # 4K: 4096 x 1024, sun 8 deg
    ("morning", "sky_morning.hdr", "sky_morning_hq.png", 1024, 0.40, 0.2, True),   # sun 29 deg
    ("overcast", "sky_overcast.hdr", "sky_overcast.png", 1024, 0.36, 0.0, False),  # no sun: the brightest part at u = 0
]


def sky(src, name="sky.hdr", outname="sky_day.png", R=2048, target=0.42, blue=0.4, sun=True):
    im = read_hdr(os.path.join(src, name))
    H, Ws, _ = im.shape
    im = im[: H // 2 + 1]                               # zenith .. horizon only
    L = im @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    import cv2
    sy, sx = np.unravel_index(np.argmax(cv2.GaussianBlur(L, (0, 0), 3)), L.shape)  # (blurred: no single hot pixel)
    del L
    print("sky: %dx%d, sun at column %d, %.1f deg up" % (Ws, H, sx, 90 - (sy + 0.5) / H * 180))
    im = np.roll(im, -sx, axis=1)                       # the sun at u = 0
    # rows: v = sin(elevation); each output row is the mean of the source rows it covers (no aliasing near the zenith)
    out = np.zeros((R, Ws, 3), np.float32)
    for r in range(R):                                  # r = 0: zenith row of the image (v = 1)
        v1, v0 = 1.0 - r / R, 1.0 - (r + 1) / R
        e1, e0 = np.degrees(np.arcsin(v1)), np.degrees(np.arcsin(v0))
        a, b = (90 - e1) / 180 * H, (90 - e0) / 180 * H  # source rows a..b (a < b)
        ia = min(int(np.floor(a)), H // 2); ib = max(min(int(np.ceil(b)), H // 2 + 1), ia + 1)
        out[r] = im[ia:ib].mean(axis=0)
    del im
    # exposure: the clear sky at 25-40 deg (its median) to the tuned on-screen blue (~125,177,243 sRGB: luminance 0.42)
    Y = np.array([0.2126, 0.7152, 0.0722], np.float32)
    Lo = out @ Y
    band = Lo[int(R * (1 - np.sin(np.radians(40)))): int(R * (1 - np.sin(np.radians(25))))]
    e = target / float(np.median(band))
    x = out * e
    del out, Lo
    lum = (x @ Y)[..., None]
    # bluer clear sky (the scan is grey-blue against the game's sunny palette); clouds keep their white
    sat = 1.0 + blue * np.clip(1.0 - (lum - 0.45) / 0.4, 0, 1)
    x = lum + (x - lum) * sat
    # soft shoulder above 0.62: cloud tops keep their modelling instead of clipping
    lum = (x @ Y)[..., None]
    k = 0.62
    lm = np.where(lum > k, k + (1 - k) * (1 - np.exp(-(lum - k) / (1 - k))), lum)
    x = x * (lm / np.maximum(lum, 1e-5))
    rgb = srgb8(x)
    Image.fromarray(rgb, "RGB").save(os.path.join(PHOTO, outname), optimize=True)
    print("sky: %dx%d, exposure %.3f, mean sRGB %s" % (rgb.shape[1], rgb.shape[0], e, rgb.reshape(-1, 3).mean(axis=0).round(1)))


def leafspray(src):
    """two rows of cut-out sprays (1024 x 512): row 0 LeafSet002 (six small-leaved sprays), row 1 LeafSet005 (a twig + 3
    leaves), each sprite in a 170 x 256 / 256 x 256 cell, colour dilated into the cut-out (no dark fringe)"""
    import cv2
    cells = []
    for name in ("leafset002", "leafset005"):
        col = np.asarray(Image.open(os.path.join(src, name + "_col.jpg")).convert("RGB"))
        op = np.asarray(Image.open(os.path.join(src, name + "_op.jpg")).convert("L"))
        # one sprite per connected piece of the cut-out (a spray, a twig with its leaves, a single leaf)
        n, lab, st, _ = cv2.connectedComponentsWithStats((op > 128).astype(np.uint8), connectivity=8)
        comps = sorted(range(1, n), key=lambda q: -st[q, cv2.CC_STAT_AREA])
        for q in comps:
            if st[q, cv2.CC_STAT_AREA] < 0.004 * op.size:
                break
            x0, y0, w0, h0 = st[q, 0], st[q, 1], st[q, 2], st[q, 3]
            a = op[y0:y0 + h0, x0:x0 + w0] * (lab[y0:y0 + h0, x0:x0 + w0] == q)
            cells.append((col[y0:y0 + h0, x0:x0 + w0], a.astype(np.uint8)))
    W, H, C = 1024, 512, 256
    out = np.zeros((H, W, 4), np.float32)
    for k, (c, a) in enumerate(cells[:8]):
        cx, cy = (k % 4) * C, (k // 4) * C
        h, w = a.shape
        s = (C - 8) / max(h, w)
        nw, nh = max(1, int(w * s)), max(1, int(h * s))
        cc = np.asarray(Image.fromarray(c).resize((nw, nh), Image.LANCZOS), np.float32) / 255.0
        aa = np.asarray(Image.fromarray(a).resize((nw, nh), Image.LANCZOS), np.float32)[..., None] / 255.0
        ox, oy = cx + (C - nw) // 2, cy + (C - nh) // 2
        out[oy:oy + nh, ox:ox + nw, :3] = cc
        out[oy:oy + nh, ox:ox + nw, 3:] = aa
    # dilate colour into the transparent texels (a few passes of a 3x3 mean of the covered ones)
    rgb, a = out[..., :3].copy(), out[..., 3].copy()
    done = a > 0.5
    for _ in range(12):
        acc = np.zeros_like(rgb); n = np.zeros(a.shape, np.float32)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                m = np.roll(np.roll(done, dy, 0), dx, 1)
                acc += np.roll(np.roll(rgb, dy, 0), dx, 1) * m[..., None]; n += m
        grow = (~done) & (n > 0)
        rgb[grow] = acc[grow] / n[grow][:, None]
        done = done | grow
    out[..., :3] = rgb
    img = (np.clip(out, 0, 1) * 255 + 0.5).astype(np.uint8)
    Image.fromarray(img, "RGBA").save(os.path.join(TEX, "leafspray.bytes"), "PNG", optimize=True)
    print("leafspray: %d sprays" % min(8, len(cells)))


def main():
    if len(sys.argv) > 2 and sys.argv[1] == "--fetch":
        fetch(sys.argv[2]); return
    src = sys.argv[1]
    os.makedirs(PHOTO, exist_ok=True)
    grass(src); gravel(src)
    jpeg_bytes(src, "asphalt_track.jpg", "asphalt.bytes")
    jpeg_bytes(src, "concrete_floor_worn_001.jpg", "kerbconc.bytes")
    # wave 2 (runtime-built atlases: TracksideArt.Atlas, EnvTex.BuildingAtlas)
    jpeg_bytes(src, "metal032.jpg", "zinc.bytes", size=512)
    jpeg_bytes(src, "concrete_wall_008.jpg", "wallconc.bytes")
    for f, o in (("plastered_wall.png", "plaster"), ("red_brick.jpg", "brick"), ("clay_roof_tiles_02.jpg", "rooftiles"),
                 ("concrete_slab_wall.jpg", "panels"), ("box_profile_metal_sheet.jpg", "cladding"),
                 ("corrugated_iron_02.jpg", "corrugated"), ("painted_metal_shutter.png", "shutter")):
        jpeg_bytes(src, f, o + ".bytes", size=512, q=90)
    leafspray(src)
    only = os.environ.get("F2_SKIES")  # e.g. F2_SKIES=evening,morning: rebuild only those
    for nm, f, outn, R, tgt, blue, sun in SKIES:
        if (only is None or nm in only.split(",")) and os.path.exists(os.path.join(src, f)):
            sky(src, f, outn, R, tgt, blue, sun)
    with open(os.path.join(PHOTO, "means.txt"), "w") as f:
        f.write("# linear mean colour of each photo albedo (Tools/Photo/make_photos.py); EnvTex.PhotoMean\n")
        for k, v in sorted(means.items()):
            f.write("%s %.5f %.5f %.5f\n" % (k, v[0], v[1], v[2]))
    print("means", {k: v.round(4).tolist() for k, v in means.items()})


if __name__ == "__main__":
    main()
