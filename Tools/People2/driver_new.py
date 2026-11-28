# driver_new.py — the game's race driver from Adi's new models (Art/People3/driver_new, 28 Nov 2026):
#   driver_suited_m.glb  CC0 body, Mixamo bone names (52 bones, A-pose), one mesh per part with its own _LOD0..3
#                        (Suit, Boots, Gloves, Body_Suited = head + neck skin, Hands, Hair_Cap, Eyes, Brows), white textures
#   helmet_base.glb      unrigged helmet in the same world frame: shell, visor, visor frame, carbon trim, HANS, padding
# -> the "Driver" body of the public pack people2.bytes (people3.build_drivers). Same game skeleton / retarget as every
# People2 body, so People.Rig, the clips and the IK drive it unchanged. The file's own LODs are used (no decimation of
# the body); only the helmet group (it has one LOD) is decimated for the far LODs.
#  * kept: Suit, Boots, Gloves (LOD 0..3), the neck of Body_Suited (a dark balaclava: the closed helmet hides the face),
#    the helmet group. Dropped (never visible): Hands (inside the gloves), Hair_Cap, Eyes, Brows, helmet padding, tear-offs.
#  * colour: every white texel of suit / gloves / boots / helmet shell is team-tinted (atlas alpha, People2 v2 encoding);
#    the palette regions as before: suit RSuit, accent (side seam stripes, sleeve tops) RStripe, gloves RGlove, boots RBoot,
#    shell RHelmet, helmet top stripe + chin + visor frame RHelmetTop (the team colour), visor RVisor, carbon RBelt, neck RNeck.
#  * the helmet rides on the Head joint. No HANS collar (not worn walking round the garage / on the podium).
#  * the file's Normal + ORM maps are baked into the atlas colour as cavity / AO shading (the People2 shader has no normal map).
import numpy as np
import cv2
from PIL import Image
from p2io import Model
from retarget import Src, skin_repose, weights22, MIX
import build as B

SRC = 'Art/People3/driver_new'
HEAD, SPINE3 = 5, 3
NECK_Y = 1.55            # Body_Suited faces below this keep (neck); above is the face / skull inside the helmet
HELMET_LODS = [None, 900, 400, 150]
HELMET_SCALE = 1.08


def srgb_dec(x): return np.where(x <= 0.04045, x / 12.92, np.power((x + 0.055) / 1.055, 2.4))


def raster_attr(A, uvc, attr, mask_out=None):
    """rasterise per-corner attributes (F,3,k) of faces with atlas uvs (F,3,2; 0..1, v down) into atlas-sized (h,w,k);
    returns (values, covered)"""
    H, W = A.h, A.w; k = attr.shape[2]
    out = np.zeros((H, W, k), np.float32); cov = np.zeros((H, W), bool)
    px = uvc * np.array([W, H])
    for t in range(len(px)):
        xs, ys = px[t, :, 0], px[t, :, 1]
        x0, x1 = max(int(np.floor(xs.min())), 0), min(int(np.ceil(xs.max())), W - 1)
        y0, y1 = max(int(np.floor(ys.min())), 0), min(int(np.ceil(ys.max())), H - 1)
        if x0 > x1 or y0 > y1: continue
        den = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
        if abs(den) < 1e-9: continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        w0 = ((ys[1] - ys[2]) * (gx - xs[2]) + (xs[2] - xs[1]) * (gy - ys[2])) / den
        w1 = ((ys[2] - ys[0]) * (gx - xs[2]) + (xs[0] - xs[2]) * (gy - ys[2])) / den
        w2 = 1 - w0 - w1
        ins = (w0 >= -0.02) & (w1 >= -0.02) & (w2 >= -0.02)
        if not ins.any(): continue
        v = w0[..., None] * attr[t, 0] + w1[..., None] * attr[t, 1] + w2[..., None] * attr[t, 2]
        sub = out[y0:y1 + 1, x0:x1 + 1]; sub[ins] = v[ins]; cov[y0:y1 + 1, x0:x1 + 1] |= ins
    return out, cov


def mat_image(M, mat_name, slot):
    """a material's normal / occlusion image (PIL, RGB) by material name; slot 'normal' | 'orm'"""
    import io, os
    g = M.G.g
    for m in g.materials:
        if m.name != mat_name: continue
        if slot == 'normal': tx = m.normalTexture
        else: tx = m.occlusionTexture or (m.pbrMetallicRoughness.metallicRoughnessTexture if m.pbrMetallicRoughness else None)
        if tx is None: return None
        im = g.images[g.textures[tx.index].source]
        bv = g.bufferViews[im.bufferView]; raw = M.G.buffer(bv.buffer)[(bv.byteOffset or 0):(bv.byteOffset or 0) + bv.byteLength]
        return Image.open(io.BytesIO(raw)).convert('RGB')
    return None


def bake_relief(A, key, M, d, k_cav=0.22, k_ao=0.55):
    tx, ty, tw, th, *_ = A.tiles[key]
    nrm = mat_image(M, d['mat'], 'normal'); orm = mat_image(M, d['mat'], 'orm')
    shade = np.ones((th, tw), np.float32)
    if nrm is not None:
        n = np.asarray(nrm.resize((tw, th), Image.LANCZOS), np.float32) / 255.0 * 2 - 1
        slope = np.clip(np.sqrt(n[..., 0] ** 2 + n[..., 1] ** 2), 0, 1)       # 0 = flat, 1 = steep (a seam / fold)
        blur = cv2.GaussianBlur(slope, (0, 0), 3.0)
        cav = np.clip(slope - blur, 0, None)                                      # the narrow creases, not the broad curves
        shade *= 1.0 - np.clip(k_cav * cav * 3.0, 0, 0.45)
    if orm is not None:
        ao = np.asarray(orm.resize((tw, th), Image.LANCZOS), np.float32)[..., 0] / 255.0
        shade *= 1.0 - k_ao * (1.0 - ao) * 0.6
    A.rgb[ty:ty + th, tx:tx + tw] *= shade[..., None]
    print('   relief', key, 'mean shade', round(float(shade.mean()), 3), 'min', round(float(shade.min()), 2))


def smooth(x, a, b): return np.clip((x - a) / (b - a), 0, 1)


def grow(arr, cov, n=6):
    """a mask grown into the texels no face covers (a few px round each island: mips / bilinear keep clean edges)"""
    a = arr.copy(); free = ~cov; k = np.ones((3, 3), np.uint8)
    for _ in range(n): a = np.where(free, np.maximum(a, cv2.dilate(a, k)), a)
    return a


def lum_of(img_rgb):
    lin = srgb_dec(np.asarray(img_rgb, np.float32) / 255.0)
    return 0.2126 * lin[..., 0] + 0.7152 * lin[..., 1] + 0.0722 * lin[..., 2]


# ---------------------------------------------------------------- sponsor stickers (made-up brands) projected on the suit
BRANDS = [  # name, background, text, glyph
    ('ZORVEX', (250, 204, 21), (20, 20, 24), 'bolt'), ('KALDRIX', (245, 245, 245), (200, 30, 40), 'tri'),
    ('MYRELLO', (16, 28, 64), (245, 245, 245), 'bars'), ('QUINTAVO', (245, 245, 245), (22, 24, 30), 'ring'),
    ('FLUXORA', (245, 120, 30), (255, 255, 255), 'dot'), ('VANTRYX', (226, 232, 240), (30, 70, 160), 'bars'),
    ('GLORVEN', (22, 160, 110), (255, 255, 255), 'tri'), ('DRAXIO', (14, 14, 18), (245, 245, 245), 'bolt'),
    ('NELVARA', (20, 150, 170), (255, 255, 255), 'ring'), ('SKORVIK', (236, 72, 120), (255, 255, 255), 'dot')]
FONT = '/usr/share/fonts/truetype/freefont/FreeSansBold.ttf'


def sticker(brand, w_px, h_px):
    """a neat flat sticker (RGBA, 4x supersampled): rounded plate, a small glyph, the name in bold caps"""
    from PIL import ImageDraw, ImageFont
    name, bg, fg, glyph = brand; S = 4; W, H = w_px * S, h_px * S
    im = Image.new('RGBA', (W, H), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    d.rounded_rectangle((0, 0, W - 1, H - 1), radius=int(H * 0.22), fill=bg + (255,))
    d.rounded_rectangle((int(H * 0.06), int(H * 0.06), W - 1 - int(H * 0.06), H - 1 - int(H * 0.06)), radius=int(H * 0.17), outline=fg + (255,), width=max(2, int(H * 0.04)))
    g = int(H * 0.46); gx, gy = int(H * 0.32), H // 2
    if glyph == 'bolt': d.polygon([(gx, gy - g), (gx - g // 2, gy + g // 8), (gx - g // 10, gy + g // 8), (gx - g // 3, gy + g), (gx + g // 2, gy - g // 8), (gx + g // 12, gy - g // 8)], fill=fg + (255,))
    elif glyph == 'tri': d.polygon([(gx, gy - g), (gx + g, gy + g), (gx - g, gy + g)], fill=fg + (255,))
    elif glyph == 'ring': d.ellipse((gx - g, gy - g, gx + g, gy + g), outline=fg + (255,), width=max(3, g // 3))
    elif glyph == 'dot': d.ellipse((gx - g, gy - g, gx + g, gy + g), fill=fg + (255,))
    else:
        for i in range(3): d.rectangle((gx - g, gy - g + i * (g * 2 // 3), gx + g, gy - g + i * (g * 2 // 3) + g // 3), fill=fg + (255,))
    x0 = int(H * 0.32 + g + H * 0.14); size = int(H * 0.5)
    f = ImageFont.truetype(FONT, size)
    while f.getlength(name) > W - x0 - H * 0.2 and size > 8: size -= 2; f = ImageFont.truetype(FONT, size)
    d.text((x0, H // 2), name, font=f, fill=fg + (255,), anchor='lm')
    return im.resize((w_px, h_px), Image.LANCZOS)


# (brand index, approx centre (x, y, z) on the suit, outward direction, width m, height m, 'up' hint)
DECALS = [
    (0, (0.075, 1.30, 0.10), (0, 0.2, 1), 0.105, 0.034, (0, 1, 0)),   # left chest
    (1, (-0.075, 1.30, 0.10), (0, 0.2, 1), 0.095, 0.032, (0, 1, 0)),  # right chest
    (2, (0.0, 1.05, 0.12), (0, 0, 1), 0.10, 0.03, (0, 1, 0)),         # belly
    (3, (0.10, 0.80, 0.09), (0, 0, 1), 0.10, 0.03, (0, 1, 0)),        # left thigh front
    (4, (-0.10, 0.62, 0.09), (0, 0, 1), 0.085, 0.028, (0, 1, 0)),     # right thigh front (lower)
    (5, (0.30, 1.46, 0.0), (0, 1, 0.1), 0.10, 0.03, (1, 0, 0)),       # left upper arm (top)
    (6, (-0.30, 1.46, 0.0), (0, 1, 0.1), 0.095, 0.03, (1, 0, 0)),     # right upper arm (top)
    (7, (0.0, 1.38, -0.09), (0, 0.1, -1), 0.16, 0.05, (0, 1, 0)),     # back, upper
    (8, (0.08, 1.10, -0.09), (0, 0, -1), 0.09, 0.028, (0, 1, 0)),     # back, lower left
    (9, (-0.10, 0.48, 0.06), (0, 0, 1), 0.08, 0.026, (0, 1, 0)),      # right shin front
    (3, (0.12, 0.42, 0.06), (0, 0, 1), 0.075, 0.025, (0, 1, 0)),      # left shin front
]


def paint_decals(A, s0, key='suit'):
    """project the stickers onto the suit's atlas tile through the surface position map: each texel knows its 3D point,
    so a decal is a plane patch (centre, normal, up) sampled where the surface lies within it"""
    uvc = A.map(key, s0.uv)[s0.I]
    v, cov = raster_attr(A, uvc, np.concatenate([s0.P[s0.I], s0.N[s0.I]], axis=2))
    p = v[..., :3]; n = v[..., 3:] / (np.linalg.norm(v[..., 3:], axis=2, keepdims=True) + 1e-6)
    tx, ty, tw, th, *_ = A.tiles[key]
    for bi, c, dr, w, h, up in DECALS:
        w *= 1.7; h *= 1.7
        c = np.array(c, float); dr = np.array(dr, float); dr /= np.linalg.norm(dr)
        # snap the centre onto the surface: the suit vertices near it facing the decal's way
        vs = s0.P; ns = s0.N
        near = (np.linalg.norm(vs - c, axis=1) < 0.06) & (ns @ dr > 0.4)
        if near.sum() < 3: print('  decal', BRANDS[bi][0], 'no surface at', c); continue
        c = vs[near].mean(0); nm = ns[near].mean(0); nm /= np.linalg.norm(nm)
        right = np.cross(nm, np.array(up, float)); right /= np.linalg.norm(right) + 1e-9; upv = np.cross(right, nm)
        # (mirror: the model is mirrored in x for Unity, so a left-side sticker keeps reading left to right as seen from outside)
        rel = p - c
        u = rel @ right; vv = rel @ upv; dn = rel @ nm
        inside = cov & (np.abs(u) < w / 2) & (np.abs(vv) < h / 2) & (np.abs(dn) < 0.03) & ((n @ nm) > 0.55)
        if not inside.any(): print('  decal', BRANDS[bi][0], 'covers no texels'); continue
        spx = int(max(64, w / 0.0035)); sh = int(max(24, spx * h / w))
        img = np.asarray(sticker(BRANDS[bi], spx, sh), np.float32) / 255.0
        su = (u / w + 0.5) * (spx - 1); sv = (0.5 - vv / h) * (sh - 1)
        samp = cv2.remap(img, su.astype(np.float32), sv.astype(np.float32), cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=(0, 0, 0, 0))
        a = samp[..., 3] * inside
        sl = (slice(ty, ty + th), slice(tx, tx + tw))
        A.rgb[sl] = A.rgb[sl] * (1 - a[sl][..., None]) + samp[..., :3][sl] * a[sl][..., None]
        A.tint[sl] *= (1 - a[sl]); A.acc[sl] *= (1 - a[sl])
        print('   decal', BRANDS[bi][0], 'texels', int((a > 0.5).sum()))


class Part:
    """one prim brought to the game: re-posed positions / normals, weights, atlas uvs"""
    def __init__(self, d, src, joint=None):
        self.name = d['name']; self.I = d['I']; self.uv = d['uv']; self.img = d['img']
        if d['J'] is not None:
            self.P, self.N = skin_repose(src, d['P'], d['N'], d['J'], d['W'])
            self.KW = weights22(src, d['J'], d['W'])
        else:  # rigid: all on one game joint (the Mixamo node of that joint)
            node = src.idx[MIX[joint]]
            J = np.full((len(d['P']), 1), node); W = np.ones((len(d['P']), 1))
            self.P, self.N = skin_repose(src, d['P'], d['N'], J, W)
            self.KW = np.zeros((len(d['P']), 22)); self.KW[:, joint] = 1.0

    def select(self, keep_faces):
        self.I = self.I[keep_faces]
        return self


def finalize_n(parts, kinds):
    """vertex arrays straight from the glb vertices (their own normals: hard edges stay hard): parts = list of
    (Part, atlas uv per vertex (n,2), kind) -> mesh dict as build.finalize makes"""
    P, N, UV, KW, R, Acc, I, FK = [], [], [], [], [], [], [], []
    base = 0
    for part, uv, kind in parts:
        used = np.unique(part.I.reshape(-1)); remap = -np.ones(len(part.P), int); remap[used] = np.arange(len(used))
        P.append(part.P[used]); N.append(part.N[used]); UV.append(uv[used]); KW.append(part.KW[used])
        r = np.full(len(used), B.KIND_REG[kind], np.uint8); a = np.full(len(used), B.ACC_REG.get(kind, B.KIND_REG[kind]), np.uint8)
        R.append(r); Acc.append(a); I.append(remap[part.I] + base); FK += [kind] * len(part.I); base += len(used)
    return dict(P=np.concatenate(P), N=np.concatenate(N), UV=np.concatenate(UV), KW=np.concatenate(KW), R=np.concatenate(R),
                A=np.concatenate(Acc), I=np.concatenate(I), FK=np.array(FK), IH=np.zeros((0, 3), int))


def build(repo, A, Q, rect):
    """the new driver into atlas A (the driver's area: rect = (x, y, w, h), 1024^2 of the atlas = suit; gloves / boots /
    shell / solid tiles below it); returns (src, [mesh dict per LOD], height)"""
    # new kinds -> palette regions
    B.KIND_REG.update({'dneck': B.RNeck, 'dtrim': B.RBelt, 'dglove': B.RGlove, 'dboot': B.RBoot, 'dframe': B.RHelmetTop})
    B.ACC_REG['dsuit'] = B.RStripe; B.ACC_REG['dhelmet'] = B.RHelmetTop; B.ACC_REG['dframe'] = B.RHelmetTop
    M = Model(f'{repo}/{SRC}/driver_suited_m.glb'); src = Src(M, 1.0); src.repose(Q)
    Mh = Model(f'{repo}/{SRC}/helmet_base.glb')
    byname = {}
    for d in M.prims: byname.setdefault(d['name'], d)
    # ---- parts per LOD
    def lod(name, k): return byname[f'{name}_LOD{k}']
    suit = [Part(lod('Suit', k), src) for k in range(4)]
    boots = [Part(lod('Boots', k), src) for k in range(4)]
    gloves = [Part(lod('Gloves', k), src) for k in range(4)]
    neck = []
    for k in range(4):
        p = Part(lod('Body_Suited', k), src)
        c = p.P[p.I].mean(1)
        neck.append(p.select(c[:, 1] < NECK_Y))
    hp = {}
    for d in Mh.prims:
        key = d['name'] + ':' + d['mat']
        hp[key] = d
    shell = Part(hp['Helmet_Shell:Helmet_Shell'], src, HEAD)
    frame = Part(hp['Helmet_Visor:Helmet_Shell'], src, HEAD)
    visor = Part(hp['Helmet_Visor:Helmet_Visor'], src, HEAD)
    trim = Part(hp['Helmet_Trim:Helmet_Trim'], src, HEAD)
    seal = Part(hp['Helmet_VisorSeal:Helmet_Rubber'], src, HEAD)
    hans = Part(hp['HANS:HANS_Carbon'], src, SPINE3)
    tether = Part(hp['HANS:HANS_Tether'], src, SPINE3)
    # the helmet reads small over the narrow shoulders: 8% bigger about its centre (the model's own proportions otherwise)
    hc = np.array([0.0, 1.62, 0.04])
    for hpart in (shell, frame, visor, trim, seal): hpart.P = hc + (hpart.P - hc) * HELMET_SCALE
    # ---- atlas tiles (atlas = 2048 x 1024: the suit 1024^2 at rect, gloves / boots / shell / solid tiles to its right)
    x, y, w, h = rect
    A.place('suit', suit[0].img, (x, y, 1024, 1024))
    A.place('gloves', gloves[0].img, (x + 1024, y, 512, 512))
    A.place('boots', boots[0].img, (x + 1536, y, 512, 512))
    A.place('shell', shell.img, (x + 1024, y + 512, 512, 512))
    sol = {'dark': (x + 1536, y + 512, 32, 32), 'visor': (x + 1568, y + 512, 32, 32), 'frame': (x + 1600, y + 512, 32, 32),
           'neck': (x + 1632, y + 512, 32, 32)}
    for k, r in sol.items(): A.place(k, None, r, solid=(0.5, 0.5, 0.5) if k in ('frame', 'neck') else (0.03, 0.03, 0.035) if k != 'visor' else (0.02, 0.025, 0.04))

    def tile_uv(key, n): rx, ry, rw, rh = A.tiles[key][:4]; return np.tile([(rx + rw / 2) / A.w, (ry + rh / 2) / A.h], (n, 1))
    # ---- seams / quilting: cavity from the normal map + the ORM occlusion, multiplied into the colour (no shader cost)
    for key, nm in (('suit', 'Suit'), ('gloves', 'Gloves'), ('boots', 'Boots')):
        bake_relief(A, key, M, byname[f'{nm}_LOD0'])
    # ---- tint / accent masks from the textures and from the surface itself (stripes by position / normal)
    for key, part, thr, reg in (('suit', suit[0], 0.45, None), ('gloves', gloves[0], 0.55, None), ('boots', boots[0], 0.45, None)):
        tx, ty, tw, th, *_ = A.tiles[key]
        lum = lum_of(part.img.convert('RGB').resize((tw, th), Image.LANCZOS))
        A.tint[ty:ty + th, tx:tx + tw] = smooth(lum, thr, thr + 0.2)
    tx, ty, tw, th, *_ = A.tiles['shell']; A.tint[ty:ty + th, tx:tx + tw] = 1.0
    for k in ('frame', 'neck', 'dark'):
        rx, ry, rw, rh, *_ = A.tiles[k]; A.tint[ry:ry + rh, rx:rx + rw] = 1.0
    rx, ry, rw, rh, *_ = A.tiles['frame']; A.acc[ry:ry + rh, rx:rx + rw] = 1.0
    # accent: suit side-seam stripes (outer sides of torso and legs) and the tops of the sleeves
    s0 = suit[0]; uvc = A.map('suit', s0.uv)[s0.I]  # (F,3,2)
    at = np.concatenate([s0.P[s0.I], s0.N[s0.I]], axis=2)  # position + normal per corner
    v, cov = raster_attr(A, uvc, at)
    p, n = v[..., :3], v[..., 3:]
    nn = n / (np.linalg.norm(n, axis=2, keepdims=True) + 1e-6)
    out = nn[..., 0] * np.sign(p[..., 0] + 1e-6)   # how far the surface faces outward (sideways)
    side = smooth(out, 0.50, 0.57) * (1 - smooth(out, 0.74, 0.81)) * smooth(np.abs(p[..., 0]), 0.045, 0.06) * smooth(p[..., 1], 0.12, 0.2) * smooth(1.40 - p[..., 1], 0.0, 0.05)
    sleeve = smooth(nn[..., 1], 0.82, 0.9) * smooth(np.abs(p[..., 0]), 0.2, 0.24) * smooth(p[..., 1], 1.0, 1.05) * smooth(np.abs(p[..., 0]), 0.0, 0.02)
    # (the stripes are 1-2 cm wide bands: by the normal on the legs / torso, the top of the arm on the sleeves)
    acc = np.maximum(side, sleeve) * cov
    sx, sy, sw, sh, *_ = A.tiles['suit']
    acc = grow(acc.astype(np.float32), cov, 5)
    A.acc = np.maximum(A.acc, acc * (A.tint > 0.5))
    # accent on the shell: centre stripe over the top + the chin
    uvh = A.map('shell', shell.uv)[shell.I]
    vh, covh = raster_attr(A, uvh, np.concatenate([shell.P[shell.I], shell.N[shell.I]], axis=2))
    ph = vh[..., :3]
    stripe = smooth(0.026 - np.abs(ph[..., 0]), 0.0, 0.01) * smooth(ph[..., 1], 1.706, 1.726)
    chin = smooth(1.578 - ph[..., 1], 0.0, 0.01) * smooth(ph[..., 2], 0.02, 0.04)
    accs = grow((np.maximum(stripe, chin) * covh).astype(np.float32), covh, 5)
    tx, ty, tw, th, *_ = A.tiles['shell']
    m = np.zeros((A.h, A.w), np.float32); m[ty:ty + th, tx:tx + tw] = accs[ty:ty + th, tx:tx + tw]
    A.acc = np.maximum(A.acc, m)
    paint_decals(A, suit[0])
    # ---- helmet group as faces with atlas uvs
    def hgroup():
        rows = []
        rows.append((shell, A.map('shell', shell.uv), 'dhelmet'))
        rows.append((frame, tile_uv('frame', len(frame.P)), 'dframe'))
        rows.append((visor, tile_uv('visor', len(visor.P)), 'dvisor'))
        rows.append((trim, tile_uv('dark', len(trim.P)), 'dtrim'))
        rows.append((seal, tile_uv('dark', len(seal.P)), 'dtrim'))
        return rows
    H = hgroup()
    lods = []
    for k in range(4):
        parts = [(suit[k], A.map('suit', suit[k].uv), 'dsuit'), (boots[k], A.map('boots', boots[k].uv), 'dboot'),
                 (gloves[k], A.map('gloves', gloves[k].uv), 'dglove'), (neck[k], tile_uv('neck', len(neck[k].P)), 'dneck')]
        if HELMET_LODS[k] is None:
            parts += H
            m = finalize_n(parts, None)
        else:
            m = finalize_n(parts, None)
            hm = decimated_helmet(H, HELMET_LODS[k])
            m = concat(m, hm)
        lods.append(m)
    allp = np.concatenate([lods[0]['P']])
    height = float(allp[:, 1].max() - allp[:, 1].min())
    return src, lods, height


def decimated_helmet(H, target):
    """the helmet group welded and decimated (pymeshlab, vertices kept): smooth-normal mesh like the other LODs"""
    Pall, Iall, UVall, KWall, kind = [], [], [], [], []; base = 0
    for part, uv, k in H:
        Pall.append(part.P); UVall.append(uv[part.I]); KWall.append(part.KW); Iall.append(part.I + base); kind += [k] * len(part.I); base += len(part.P)
    P = np.concatenate(Pall); I = np.concatenate(Iall); UV = np.concatenate(UVall); KW = np.concatenate(KWall); kind = np.array(kind)
    first, inv = B.weld(P)
    P, I, KW = P[first], inv[I], KW[first]
    import people3
    P2, I2, UV2, KW2, kind2 = people3.lod_exact(P, I, UV, KW, kind, target)
    return B.finalize(P2, I2, UV2, KW2, kind2)


def concat(a, b):
    n0 = len(a['P']); out = {}
    for k in ('P', 'N', 'UV', 'KW', 'R', 'A'): out[k] = np.concatenate([a[k], b[k]])
    out['I'] = np.concatenate([a['I'], b['I'] + n0]); out['FK'] = np.concatenate([a['FK'], b['FK']]); out['IH'] = np.zeros((0, 3), int)
    return out
