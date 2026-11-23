# build.py — People2: realistic people for the game, converted offline into the runtime format People.cs reads ('PPL2').
#
#   python3 -I build.py <work dir with the Mixamo GLBs (fbx2glb.py output) or ''> <assets-in dir> <repo root> [--preview DIR]
#                       [--only public,people3,mixamo]
#   1. Mixamo FBX -> GLB (Blender 4, apt `blender` + `python3-numpy`), one per file, named as SPECS / CLIPS expect
#      (Remy.glb, Ch08_nonPBR.glb, Ch13_nonPBR.glb, Idle.glb, Walking.glb, Talking.glb, Talking_1.glb, Clapping.glb,
#      Sitting_Clap.glb, Waving.glb, Standing_W_Briefcase_Idle.glb):
#        blender -b --factory-startup -P fbx2glb.py -- in.fbx WORK/name.glb
#   2. <assets-in dir> holds the two Sketchfab downloads (DRIVER / AVATAR folders below: scene.gltf, textures/): only
#      their helmet and cap are used now (the crew's head gear).
#   3. pip: pygltflib numpy pillow opencv-python-headless scipy trimesh pymeshlab (apt libopengl0); then this script;
#      verify.py renders the packs back (as People.cs reads them) posed by the game's clips.
#
# Packs:
#  * PUBLIC  Assets/Resources/People2/people2.bytes + people2_atlas.png (CC0): the pack drivers from Art/People3
#      (people3.py): "Driver" = driver_01 (the player), "Driver2" = driver_02; 4 LODs (19k hero / 4k / 1.5k / 0.5k).
#  * PUBLIC  people3.bytes / people3p.bytes + _atlas.png (MIT, Microsoft Rocketbox, Art/People3; people3.py): the race
#      crew (engineers + crew-helmet variant, marshals) and the podium people (officials, photographer, presenter, fans).
#  * PRIVATE Assets/Resources/People2/mixamo/mixamo.bytes (Mixamo: Adobe licence, NOT for the public repo; .gitignore):
#      - crew bodies Ch08 (hoodie, joggers), Ch13 (top, trousers), Remy (t-shirt, shorts), clothes tinted per team;
#      - mocap clips Idle, Walk, Talk, Talk2, Clap, SitClap, Wave, Briefcase retargeted to the game skeleton.
#      After a rebuild copy it to the private assets repo (staging: /home/claude/private-assets-staging/People2/mixamo/).
# Every body is bound to the game's 22-joint skeleton with its rest ORIENTATIONS (retarget.py), so all the old clips,
# the IK and the pit-stop choreography drive it unchanged. Gear variants per body (People.G*; a body may have fewer:
# the game clamps the index). Textures: one atlas per pack: PNG next to the pack (imported by Unity) or JPEG colour +
# PNG alpha inside it. Alpha (v2): 0.5 * tint + 0.5 * accent (People2.shader), hair-card coverage in card islands.
#
# PPL2 format (little endian):
#   'PPL2', version i32 (2; v1 = no nth / accent arrays, alpha = tint amount), nj i32 (22)
#   atlas: jpgLen u32, jpg, pngLen u32, png (8-bit grey alpha, see above; rgb under the tint = a detail grey);
#          both 0: the atlas is <pack>_atlas.png next to the pack (RGBA)
#   nBody; per body: name (u8 len + ascii), flags u8 (1 female, 2 driver), pscale f32, height f32,
#          rest local T (3 f32) + Q (4 f32) per joint, bindpose 16 f32 per joint (row-major), nVar i32 (4: People.G*),
#          nLod i32 (3 or 4); per variant per LOD: nv, nt, nth i32 (hair-card tris, the 2nd sub-mesh), pos 3 f32, normal 3 i8,
#          uv 2 u16, region u8, accent region u8, bone idx 4 u8, weight 4 u8, indices 3 u16 per tri (nt, then nth)
#   nClip; per clip: name (u8 len + ascii), fps f32, frames i32, loop u8, stride f32 (m per cycle, 0 = none),
#          quats (frames x nj x 4 i16), pelvis (frames x 3 f32)
import os, sys, struct, io
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import cv2
from PIL import Image
from p2io import Model, strip
from qskel import QSkel, KEEP, fk, quat_mat
from retarget import Src, MIX, skin_repose, weights22, bake_clip, rot_between
import texfix

# regions (People.cs R*)
RSuit, RSide, RYoke, RSleeve, RForearm, RGlove, RThigh, RStripe, RShin, RBoot, RBelt, RNeck, RFace, RHair, \
    RHelmet, RHelmetTop, RVisor, RHeadset, RCap, RCapBrim, RKnee = range(21)
KIND_REG = {'body': RSuit, 'cards': RHair, 'dsuit': RSuit, 'dhelmet': RHelmet, 'dvisor': RVisor, 'skin': RFace, 'top': RSuit, 'bottom': RThigh, 'shoes': RBoot, 'hair': RHair, 'eyes': RFace, 'cap': RCap,
            'helmet': RHelmet, 'visor': RVisor, 'headset': RHeadset, 'suit': RSuit, 'gloves': RGlove, 'hans': RBelt, 'shell': RHair}
ACC_REG = {'dsuit': RStripe, 'body': RStripe, 'dhelmet': RHelmetTop}  # accent region (People2 v2: tint mask 128) per kind
TINT_KINDS = {'top', 'bottom', 'cap', 'headset'}          # tinted wherever they cover
RED_KINDS = {'suit', 'gloves', 'helmet'}                  # tinted where the texture is red (the source's livery colour)
GEARS = ['helmet', 'headset', 'cap', 'bare']
ATLAS = 1024
PAD = 3


def srgb_enc(x): return np.where(x <= 0.0031308, 12.92 * x, 1.055 * np.power(np.clip(x, 0, None), 1 / 2.4) - 0.055)
def srgb_dec(x): return np.where(x <= 0.04045, x / 12.92, np.power((x + 0.055) / 1.055, 2.4))


# ---------------------------------------------------------------- atlas
class Atlas:
    """one texture atlas (w x h): colour, tint amount (0..1), accent share of the tint (0..1, People2 v2), alpha-clip
    coverage of hair cards (cutm: texels of card islands, cut: their coverage)"""
    def __init__(self, size=ATLAS, h=None):
        self.w = size; self.h = h or size; self.n = size
        self.rgb = np.zeros((self.h, self.w, 3), np.float32); self.cov = np.zeros((self.h, self.w), np.uint8)
        self.tint = np.zeros((self.h, self.w), np.float32); self.acc = np.zeros((self.h, self.w), np.float32)
        self.cutm = np.zeros((self.h, self.w), np.uint8); self.cut = np.zeros((self.h, self.w), np.float32); self.tiles = {}

    def place(self, key, img, rect, uvs=None, solid=None):
        """img (PIL) -> rect (x, y, w, h) in atlas pixels; crops the image to the UVs used (uvs: list of (n,2) arrays)"""
        x, y, w, h = rect
        if solid is not None:
            self.rgb[y:y + h, x:x + w] = np.asarray(solid, np.float32)[:3]; self.cov[y:y + h, x:x + w] = 1
            self.tiles[key] = (x, y, w, h, 0.0, 0.0, 1.0, 1.0); return
        a = np.asarray(img.convert('RGB'), np.float32) / 255.0; ih, iw = a.shape[:2]
        u0, v0, u1, v1 = 0.0, 0.0, 1.0, 1.0
        if uvs is not None and len(uvs):
            U = np.concatenate(uvs); U = np.clip(U, 0, 1)
            u0, v0 = U.min(0) - 4.0 / iw; u1, v1 = U.max(0) + 4.0 / ih
            u0, v0 = max(u0, 0.0), max(v0, 0.0); u1, v1 = min(u1, 1.0), min(v1, 1.0)
        crop = a[int(v0 * ih):max(int(np.ceil(v1 * ih)), int(v0 * ih) + 1), int(u0 * iw):max(int(np.ceil(u1 * iw)), int(u0 * iw) + 1)]
        u0, u1 = int(u0 * iw) / iw, max(int(np.ceil(u1 * iw)), int(u0 * iw) + 1) / iw
        v0, v1 = int(v0 * ih) / ih, max(int(np.ceil(v1 * ih)), int(v0 * ih) + 1) / ih
        tw, th = w - 2 * PAD, h - 2 * PAD
        r = cv2.resize(crop, (tw, th), interpolation=cv2.INTER_AREA if crop.shape[1] > tw else cv2.INTER_LINEAR)
        self.rgb[y + PAD:y + PAD + th, x + PAD:x + PAD + tw] = r; self.cov[y + PAD:y + PAD + th, x + PAD:x + PAD + tw] = 1
        self.tiles[key] = (x + PAD, y + PAD, tw, th, u0, v0, u1, v1)

    def tile_img(self, key, arr, interp=cv2.INTER_AREA):
        """a per-texel map of a whole source image (H x W, float) resized to the tile (uncropped tiles only)"""
        x, y, w, h, *_ = self.tiles[key]
        return cv2.resize(arr.astype(np.float32), (w, h), interpolation=interp), (x, y, w, h)

    def map(self, key, uv):
        """source uv (glTF, v down) -> atlas uv (glTF orientation, 0..1)"""
        x, y, w, h, u0, v0, u1, v1 = self.tiles[key]
        uv = np.clip(uv, 0, 1)
        px = x + (uv[:, 0] - u0) / max(u1 - u0, 1e-6) * w; py = y + (uv[:, 1] - v0) / max(v1 - v0, 1e-6) * h
        return np.c_[px / self.w, py / self.h]

    def paint_tint(self, uv_tris, kinds, red=None):
        """tint mask from the faces of tinted kinds (rasterised in atlas pixels); red: per face bool, tint only red texels"""
        m = np.zeros((self.h, self.w), np.uint8); mr = np.zeros((self.h, self.w), np.uint8)
        sc = np.array([self.w, self.h]) * 4
        for t, k in zip(uv_tris, kinds):
            if k in TINT_KINDS or k in RED_KINDS:
                pts = np.round(t * sc).astype(np.int32)
                cv2.fillPoly(mr if k in RED_KINDS else m, [pts], 255, lineType=cv2.LINE_8, shift=2)
        m = cv2.dilate(m, np.ones((3, 3), np.uint8)); mr = cv2.dilate(mr, np.ones((3, 3), np.uint8))
        self.tint = np.maximum(self.tint, m / 255.0)
        if mr.any():
            c = self.rgb; r, g, b = c[..., 0], c[..., 1], c[..., 2]
            mx = np.maximum(g, b)
            redness = np.clip((r / (mx + 0.03) - 1.6) / 0.8, 0, 1) * np.clip((r - mx) / 0.06, 0, 1)
            redness = cv2.GaussianBlur(redness.astype(np.float32), (3, 3), 0)
            self.tint = np.maximum(self.tint, redness * (mr / 255.0))

    def finish(self):
        """detail grey under the tint (normalised per connected tinted area), padding by dilation; -> (jpg, png) bytes.
        Alpha (People2 v2): 0.5 * tint + 0.5 * accent (People2.shader: amount = saturate(2a), accent = saturate(2a - 1));
        in hair-card islands the cards' coverage (alpha clip at 0.5, People2Hair.shader)."""
        lin = srgb_dec(self.rgb); lum = 0.2126 * lin[..., 0] + 0.7152 * lin[..., 1] + 0.0722 * lin[..., 2]
        msk = (self.tint > 0.5).astype(np.uint8)
        n, lab = cv2.connectedComponents(msk, connectivity=4)
        det = np.zeros_like(lum)
        for i in range(1, n):
            sel = lab == i; mean = max(float(np.median(lum[sel])), 0.01)
            det[sel] = np.clip(lum[sel] / mean, 0.35, 1.7)
        # partly tinted texels (edges, the red blend): the detail of their own brightness against the area round them
        soft = (self.tint > 0.01) & (msk == 0)
        if soft.any():
            blur = cv2.GaussianBlur(lum.astype(np.float32), (15, 15), 0)
            det[soft] = np.clip(lum[soft] / np.maximum(blur[soft], 0.01), 0.35, 1.7)
        t = self.tint[..., None]
        g = srgb_enc(0.5 * det)[..., None]
        out = self.rgb * (1 - t) + np.repeat(g, 3, 2) * t
        out = np.where(t > 0.5, np.repeat(g, 3, 2), out)  # (fully tinted: only the grey; the shader recolours)
        # dilate colours into the empty texels (mip-map bleeding)
        cov = self.cov.copy(); img = out.copy()
        for _ in range(12):
            if cov.all(): break
            k = np.ones((3, 3), np.float32)
            s = cv2.filter2D(img * cov[..., None], -1, k, borderType=cv2.BORDER_CONSTANT)
            c = cv2.filter2D(cov.astype(np.float32), -1, k, borderType=cv2.BORDER_CONSTANT)
            new = (cov == 0) & (c > 0)
            img[new] = s[new] / c[new][:, None]; cov[new] = 1
        img[cov == 0] = 0.5
        alpha = 0.5 * self.tint + 0.5 * np.minimum(self.acc, self.tint)
        if self.cutm.any():
            # hair cards: colour under the transparent texels = the coverage-weighted hair colour around them (no fringe
            # when the mips blur the clip edge), alpha = coverage
            cm = self.cutm > 0; a = self.cut * cm
            num = cv2.GaussianBlur(img * a[..., None], (0, 0), 6); den = cv2.GaussianBlur(a, (0, 0), 6)[..., None]
            fill = num / np.maximum(den, 1e-4)
            w_ = np.clip(a * 1.6, 0, 1)[..., None]
            img = np.where(cm[..., None], img * w_ + fill * (1 - w_), img)
            alpha = np.where(cm, self.cut, alpha)
        b = io.BytesIO(); Image.fromarray(np.clip(img * 255 + 0.5, 0, 255).astype(np.uint8)).save(b, 'JPEG', quality=88, optimize=True)
        pa = np.clip(alpha * 255 + 0.5, 0, 255).astype(np.uint8)
        pb = io.BytesIO(); Image.fromarray(pa, 'L').save(pb, 'PNG', optimize=True)
        self.final = img; self.alpha = alpha
        self.rgba = np.dstack([np.clip(img * 255 + 0.5, 0, 255).astype(np.uint8), pa])
        return b.getvalue(), pb.getvalue()


# ---------------------------------------------------------------- geometry helpers
def weld(P, I=None):
    key = np.round(P / 1e-5).astype(np.int64)
    _, first, inv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    return first, inv.reshape(-1)


def vnormals(P, I):
    n = np.zeros_like(P); fn = np.cross(P[I[:, 1]] - P[I[:, 0]], P[I[:, 2]] - P[I[:, 0]])  # (outward for the Unity winding: clockwise front, x mirrored)
    for c in range(3): np.add.at(n, I[:, c], fn)
    return n / (np.linalg.norm(n, axis=1, keepdims=True) + 1e-12)


def decimate(P, I, UVw, target, tmp='/tmp/p2_dec', keep_border=True, optimal=True):
    """quadric edge collapse keeping UV seams (pymeshlab, via OBJ); P/I position-welded, UVw per corner (F,3,2)"""
    import pymeshlab
    os.makedirs(tmp, exist_ok=True)
    if len(I) <= target:
        return P, I, UVw
    Image.new('RGB', (4, 4)).save(f'{tmp}/t.png'); open(f'{tmp}/m.mtl', 'w').write('newmtl a\nmap_Kd t.png\n')
    U = UVw.reshape(-1, 2)
    with open(f'{tmp}/m.obj', 'w') as f:
        f.write('mtllib m.mtl\nusemtl a\n')
        f.write(''.join('v %.6f %.6f %.6f\n' % tuple(p) for p in P))
        f.write(''.join('vt %.6f %.6f\n' % (u[0], 1 - u[1]) for u in U))
        f.write(''.join('f %d/%d %d/%d %d/%d\n' % (I[k, 0] + 1, 3 * k + 1, I[k, 1] + 1, 3 * k + 2, I[k, 2] + 1, 3 * k + 3) for k in range(len(I))))
    ms = pymeshlab.MeshSet(); ms.load_new_mesh(f'{tmp}/m.obj')
    ms.meshing_decimation_quadric_edge_collapse_with_texture(targetfacenum=int(target), qualitythr=0.4, extratcoordw=1.0, preserveboundary=keep_border,
                                                            boundaryweight=1.5, optimalplacement=optimal, preservenormal=True, planarquadric=True)
    m = ms.current_mesh()
    P2 = m.vertex_matrix().copy(); I2 = m.face_matrix().astype(int).copy(); W2 = m.wedge_tex_coord_matrix().reshape(-1, 3, 2).copy()
    W2[..., 1] = 1 - W2[..., 1]
    used = np.zeros(len(P2), bool); used[I2.reshape(-1)] = True
    remap = np.cumsum(used) - 1
    return P2[used], remap[I2], W2


def closest_on_tris(Psrc, Isrc, Q):
    """closest points of Q on the triangle soup (Psrc, Isrc): triangle index and barycentrics"""
    import trimesh
    tm = trimesh.Trimesh(Psrc, Isrc, process=False)
    cp, dist, tid = trimesh.proximity.closest_point(tm, Q)
    bc = trimesh.triangles.points_to_barycentric(tm.triangles[tid], cp)
    return tid, np.clip(bc, 0, 1), dist


# ---------------------------------------------------------------- a source part list -> one re-posed, atlas-mapped mesh
class Part:
    """one piece of a body before decimation: re-posed positions, 22-joint weights, atlas uv per corner, kind"""
    def __init__(self, P, I, UVc, KW, kind, tile):
        self.P, self.I, self.UVc, self.KW, self.kind, self.tile = P, I, UVc, KW, kind, tile


def hair_shell(skin_parts, hair, atlas, tile, offset, rng, face_ok=True):
    """the hair-card hair / beard of a Mixamo character -> a thin shell over the scalp / jaw faces they cover
    (face_ok False: none on the face below the brow, so a fringe does not become a mask)"""
    import trimesh
    tm = trimesh.Trimesh(hair[0], hair[1], process=False); out = []
    for sp in skin_parts:
        _, dv, _ = trimesh.proximity.closest_point(tm, sp.P)
        head = sp.KW[sp.I].mean(1)[:, [4, 5]].sum(1) > 0.5
        sel = (dv[sp.I] < rng).all(1) & head
        if not face_ok and head.any():
            hp = sp.P[np.unique(sp.I[head])]; lo, hi = np.percentile(hp, 2, 0), np.percentile(hp, 98, 0)
            c = sp.P[sp.I].mean(1); hy = lo[1] + 0.68 * (hi[1] - lo[1]); hz = (lo[2] + hi[2]) / 2
            sel &= (c[:, 1] > hy) | (c[:, 2] < hz - 0.01)
        if not sel.any(): continue
        I = sp.I[sel]; vs = np.unique(I); remap = -np.ones(len(sp.P), int); remap[vs] = np.arange(len(vs))
        P = sp.P[vs]; N = vnormals(sp.P, sp.I)[vs]
        P = P + N * offset
        # uv: cylindrical round the head (u = angle, v = height) into the swatch tile
        cx, cz = P[:, 0].mean(), P[:, 2].mean()
        u = (np.arctan2(P[:, 0] - cx, P[:, 2] - cz) / (2 * np.pi) + 0.5) * 3 % 1.0
        v = (P[:, 1] - P[:, 1].min()) / max(np.ptp(P[:, 1]), 1e-3)
        uv = atlas.map(tile, np.c_[u, v] * 0.96 + 0.02)
        Ii = remap[I]
        out.append(Part(P, Ii, uv[Ii], sp.KW[vs], 'shell', tile))
    return out


def hair_swatch(img_rgba):
    a = np.asarray(img_rgba, np.float32) / 255.0
    sel = a[..., 3] > 0.8
    col = a[..., :3][sel].mean(0) if sel.any() else np.array([0.12, 0.09, 0.07])
    rs = np.random.RandomState(7); n = rs.rand(64, 64).astype(np.float32)
    n = cv2.GaussianBlur(n, (1, 15), 0); n = (n - n.mean()) / (n.std() + 1e-6)
    t = np.clip(col[None, None, :] * (1.0 + 0.22 * n[..., None]), 0, 1)
    return Image.fromarray((t * 255).astype(np.uint8))


# ---------------------------------------------------------------- bodies
def load_skinned_parts(M, src, spec, atlas, layout, Q):
    """the prims of a rigged source model -> re-posed Parts (spec['parts']: mesh name -> kind)"""
    parts = []; hairP = []; hair_img = None
    # tiles: one per material image, cropped to the UVs the kept prims use
    uvs = {}
    for d in M.prims:
        k = spec['parts'].get(d['name'], 'drop')
        if k in ('drop', 'hair_cards') or d['img'] is None and k != 'skin': continue
        uvs.setdefault(d['mat'], []).append(d['uv'][np.unique(d['I'])])
    for mat, rect in layout.items():
        if mat.startswith('_'): continue
        d = next(x for x in M.prims if x['mat'] == mat)
        img = d['img'] if d['img'] is not None and d['img'].size[0] > 4 else None
        if img is not None and d.get('imgname'): img = texfix.clean(img, d['imgname'])
        if img is None:
            col = np.asarray(d['img'].convert('RGB'), np.float32).reshape(-1, 3).mean(0) / 255.0 if d['img'] is not None else np.array(d['color'][:3])
            atlas.place((spec['name'], mat), None, rect, solid=col)
        else: atlas.place((spec['name'], mat), img, rect, uvs.get(mat))
    for d in M.prims:
        k = spec['parts'].get(d['name'], 'drop')
        if k == 'drop': continue
        P = d['P'] * src.scale
        P2, N2 = skin_repose(src, P, d['N'], d['J'], d['W'])
        KW = weights22(src, d['J'], d['W'])
        if k == 'hair_cards':
            hairP.append((P2, d['I'])); hair_img = d['img']; continue
        uv = atlas.map((spec['name'], d['mat']), d['uv'])
        parts.append(Part(P2, d['I'], uv[d['I']], KW, k, (spec['name'], d['mat'])))
    hide_skin(parts)
    if hairP:
        key = (spec['name'], '_hair')
        atlas.place(key, hair_swatch(hair_img), layout['_hair'])
        skin = [p for p in parts if p.kind == 'skin']
        HP = []; HI = []; base = 0
        for P_, I_ in hairP: HP.append(P_); HI.append(I_ + base); base += len(P_)
        parts += hair_shell(skin, (np.concatenate(HP), np.concatenate(HI)), atlas, key, spec.get('hair_offset', 0.005), spec.get('hair_range', 0.015), spec.get('hair_face', True))
    return parts


def hide_skin(parts):
    """drop the skin faces under the clothes (they poke through once both are decimated, and cost triangles)"""
    cloth = [p for p in parts if p.kind in ('top', 'bottom', 'shoes')]
    if not cloth: return
    import trimesh
    CP = []; CI = []; base = 0
    for c in cloth: CP.append(c.P); CI.append(c.I + base); base += len(c.P)
    CP = np.concatenate(CP); CI = np.concatenate(CI)
    fn = np.cross(CP[CI[:, 1]] - CP[CI[:, 0]], CP[CI[:, 2]] - CP[CI[:, 0]]); fn /= np.linalg.norm(fn, axis=1, keepdims=True) + 1e-12
    tm = trimesh.Trimesh(CP, CI, process=False)
    from trimesh.ray.ray_triangle import RayMeshIntersector
    ray = RayMeshIntersector(tm)
    for p in parts:
        if p.kind != 'skin': continue
        N = vnormals(p.P, p.I)
        ext = p.KW[:, [4, 5, 9, 13]].sum(1) > 0.5  # (neck, head and hands stay)
        # a skin vertex is hidden when the cloth is right above it along its normal (and also a little to each side)
        hid = ~ext
        for tilt in (np.zeros(3), np.array([0.35, 0, 0]), np.array([-0.35, 0, 0]), np.array([0, 0.35, 0]), np.array([0, -0.35, 0])):
            d = N + tilt; d /= np.linalg.norm(d, axis=1, keepdims=True) + 1e-12
            cand = np.nonzero(hid)[0]
            if not len(cand): break
            loc, ri, ti = ray.intersects_location(p.P[cand] + d[cand] * 0.0005, d[cand], multiple_hits=False)
            hit = np.zeros(len(cand), bool)
            if len(ri):
                dist = np.linalg.norm(loc - p.P[cand][ri], axis=1); hit[ri[dist < 0.07]] = True
            h2 = hid.copy(); h2[cand] = hit; hid = h2
        keep = ~hid[p.I].all(1)
        print('    hidden skin faces', int((~keep).sum()), 'of', len(p.I))
        p.I = p.I[keep]; p.UVc = p.UVc[keep]


def merge(parts):
    """parts -> one position-welded mesh per part (welded within a part only), corner uvs, per-vertex weights, kinds"""
    P = []; I = []; UV = []; KW = []; kind = []; base = 0
    for p in parts:
        first, inv = weld(p.P)
        P.append(p.P[first]); KW.append(p.KW[first]); I.append(inv[p.I] + base); UV.append(p.UVc); kind += [p.kind] * len(p.I)
        base += len(first)
    return np.concatenate(P), np.concatenate(I), np.concatenate(UV), np.concatenate(KW), np.array(kind)


def lod_mesh(P, I, UVc, KW, kind, target):
    """decimate the merged body and carry the weights / kinds over (closest point on the source, same kind)"""
    P2, I2, UV2 = decimate(P, I, UVc, target, keep_border=target > 2000)
    # per new face: kind of the closest source face with the closest uv (the atlas tile tells the part apart)
    c2 = P2[I2].mean(1); uvc2 = UV2.mean(1)
    from scipy.spatial import cKDTree
    c1 = P[I].mean(1); uvc1 = UVc.mean(1)
    kd = cKDTree(np.c_[c1, uvc1 * 0.5]); _, fi = kd.query(np.c_[c2, uvc2 * 0.5])
    kind2 = kind[fi]
    if len(I2) > target * 1.25: print('    (decimation stopped at', len(I2), 'of', target, 'tris)')
    # per new vertex: weights from the closest point on the source faces of the same kind
    KW2 = np.zeros((len(P2), 22)); vk = np.empty(len(P2), object)
    for f in range(len(I2)):
        for v in I2[f]: vk[v] = kind2[f]
    for k in np.unique(kind):
        sel = np.nonzero(vk == k)[0]
        if not len(sel): continue
        fs = np.nonzero(kind == k)[0]
        tid, bc, _ = closest_on_tris(P, I[fs], P2[sel])
        tri = I[fs][tid]
        KW2[sel] = (KW[tri] * bc[:, :, None]).sum(1)
    KW2 /= np.maximum(KW2.sum(1, keepdims=True), 1e-9)
    return P2, I2, UV2, KW2, kind2


# ---------------------------------------------------------------- gear (head): fitted from the source head box onto a body's
def head_box(P, KW, sel=None):
    m = KW[:, 5] > 0.6
    if sel is not None: m &= sel
    Hp = P[m]; lo, hi = np.percentile(Hp, 1, 0), np.percentile(Hp, 99, 0)
    return (lo + hi) / 2, (hi - lo)


class Gear:
    """a head-gear mesh in its source head's frame: positions relative to that head box, corner uvs, kinds"""
    def __init__(self, P, I, UVc, kinds, hc, hs):
        self.P, self.I, self.UVc, self.kinds, self.hc, self.hs = P, I, UVc, kinds, hc, hs

    def fit(self, hc, hs, grow=1.0, lift=0.0):
        s = np.clip(hs / self.hs, 0.85, 1.2); s = np.array([s[0], (s[0] + s[1]) / 2, s[2]]) * grow
        return hc + (self.P - self.hc) * s + np.array([0, lift, 0])


def gear_parts(name, P, I, UVc, kinds, hc, hs, target):
    if target and len(I) > target:
        first, inv = weld(P)
        Pw = P[first]; Iw = inv[I]
        P2, I2, UV2 = decimate(Pw, Iw, UVc, target)
        from scipy.spatial import cKDTree
        kd = cKDTree(np.c_[P[I].mean(1), UVc.mean(1)]); _, fi = kd.query(np.c_[P2[I2].mean(1), UV2.mean(1)])
        return Gear(P2, I2, UV2, kinds[fi], hc, hs)
    return Gear(P, I, UVc, kinds, hc, hs)


def headset_geo(hc, hs, uv_dark):
    """a simple headset (two ear cups, band, boom mic) round a head box (Unity space)"""
    P = []; I = []
    def box(a, b, w, h):
        a, b = np.asarray(a, float), np.asarray(b, float); d = b - a; d /= np.linalg.norm(d)
        up = np.array([0, 0, 1.0]) if abs(d[2]) < 0.9 else np.array([0, 1.0, 0])
        u = np.cross(d, up); u /= np.linalg.norm(u); v = np.cross(u, d)
        k = len(P)
        for e in (a, b):
            for x, y in ((-1, -1), (1, -1), (1, 1), (-1, 1)): P.append(e + u * x * w + v * y * h)
        for q in range(4):
            q2 = (q + 1) % 4; I.extend([[k + q, k + q2, k + 4 + q2], [k + q, k + 4 + q2, k + 4 + q]])
        I.extend([[k, k + 2, k + 1], [k, k + 3, k + 2], [k + 4, k + 5, k + 6], [k + 4, k + 6, k + 7]])
    rx = hs[0] / 2; ry = hs[1] / 2; ey = hc[1] - ry * 0.1; ez = hc[2] - hs[2] * 0.04
    for s in (-1, 1):
        box([s * (rx + 0.002) + hc[0], ey, ez], [s * (rx + 0.03) + hc[0], ey, ez], 0.04, 0.045)
    prev = None
    for k in range(8):
        a = np.pi * k / 7; p = np.array([hc[0] + np.cos(a) * (rx + 0.02), ey + np.sin(a) * (hc[1] + ry + 0.012 - ey), ez])
        if prev is not None: box(prev, p, 0.012, 0.005)
        prev = p
    a = np.array([hc[0] - (rx + 0.022), ey - 0.025, ez + 0.02]); b = np.array([hc[0] - rx * 0.45, hc[1] - ry * 0.62, hc[2] + hs[2] * 0.5])
    box(a, b, 0.005, 0.005); box(b, b + np.array([0.02, 0, 0.01]), 0.01, 0.01)
    P = np.array(P); I = np.array(I)
    # outward winding (clockwise front in Unity: flip faces whose normal points inwards)
    fn = np.cross(P[I[:, 1]] - P[I[:, 0]], P[I[:, 2]] - P[I[:, 0]]); out = P[I].mean(1) - hc
    flip = (fn * out).sum(1) < 0
    I[flip] = I[flip][:, [0, 2, 1]]
    UVc = np.tile(uv_dark, (len(I), 3, 1))
    return P, I, UVc


def compose(body, gear_kind, gears, B):
    """one variant: the body faces (minus those inside the gear) + the gear fitted to the head; Head-weighted gear verts"""
    P, I, UV, KW, kind = body
    hc, hs = B['hc'], B['hs']
    keep = np.ones(len(I), bool); extra = []
    if gear_kind == 'helmet' and 'helmet' in gears:
        g = gears['helmet']; Pg = g.fit(hc, hs, B.get('helmet_grow', 1.0), B.get('helmet_lift', 0.0))
        # drop the head faces inside the helmet (an ellipsoid a little inside the shell), hair and caps
        gc = (Pg.min(0) + Pg.max(0)) / 2; gr = (Pg.max(0) - Pg.min(0)) / 2 * 0.95
        inside = ((((P[I] - gc) / gr) ** 2).sum(2) < 1.0).all(1)
        keep &= ~inside; keep &= ~np.isin(kind, ['shell', 'hair', 'cap'])
        extra.append((Pg, g.I, g.UVc, g.kinds))
    elif gear_kind in ('cap', 'headset'):
        if not B.get('own_cap') and 'cap' in gears:
            g = gears['cap']; Pg = g.fit(hc, hs, B.get('cap_grow', 1.0), B.get('cap_lift', 0.0))
            gc = (Pg.min(0) + Pg.max(0)) / 2; gr = (Pg.max(0) - Pg.min(0)) / 2 * 0.92
            inside = ((((P[I] - gc) / gr) ** 2).sum(2) < 1.0).all(1) & ((P[I][:, :, 1] > hc[1] + hs[1] * 0.05).all(1))
            keep &= ~inside
            # loose hair (Remy) under the crown: gone, the rest (sides, back, neck) stays below the cap
            fc = P[I].mean(1); crown = (fc[:, 1] > gc[1] - gr[1] * 0.35) & ((((fc - gc)[:, [0, 2]] / (gr[[0, 2]] * 1.12)) ** 2).sum(1) < 1.0)
            fringe = (fc[:, 1] > gc[1] - gr[1] * 1.0) & (fc[:, 2] > hc[2]) & (np.abs(fc[:, 0] - gc[0]) < gr[0] * 0.8)
            keep &= ~((crown | fringe) & (kind == 'hair'))
            extra.append((Pg, g.I, g.UVc, g.kinds))
        if gear_kind == 'headset':
            Ph, Ih, UVh = headset_geo(hc, hs * np.array([1.08, 1.0, 1.0]) + np.array([0.012, 0, 0]), B['uv_dark'])
            extra.append((Ph, Ih, UVh, np.array(['headset'] * len(Ih))))
    elif gear_kind == 'bare' and B.get('own_cap'):
        pass
    Pv = [P]; Iv = [I[keep]]; UVv = [UV[keep]]; KWv = [KW]; kv = [kind[keep]]; base = len(P)
    hw = np.zeros(22); hw[5] = 1.0
    for Pg, Ig, UVg, kg in extra:
        Pv.append(Pg); Iv.append(Ig + base); UVv.append(UVg); KWv.append(np.tile(hw, (len(Pg), 1))); kv.append(kg); base += len(Pg)
    P = np.concatenate(Pv); I = np.concatenate(Iv); UV = np.concatenate(UVv); KW = np.concatenate(KWv); kind = np.concatenate(kv)
    used = np.zeros(len(P), bool); used[I.reshape(-1)] = True; remap = np.cumsum(used) - 1
    return P[used], remap[I], UV, KW[used], kind


def finalize(P, I, UV, KW, kind):
    """split vertices per (position vertex, uv) for Unity; smooth normals over the position vertices"""
    N = vnormals(P, I)
    key = np.c_[I.reshape(-1), np.round(UV.reshape(-1, 2) * 65535).astype(np.int64)]
    _, first, inv = np.unique(key, axis=0, return_index=True, return_inverse=True); inv = inv.reshape(-1)
    pv = I.reshape(-1)[first]
    reg = np.zeros(len(first), np.uint8)
    fk_ = np.repeat(kind, 3)[first]
    for k, r in KIND_REG.items(): reg[fk_ == k] = r
    acc = reg.copy()
    for k, r in ACC_REG.items(): acc[fk_ == k] = r
    return dict(P=P[pv], N=N[pv], UV=UV.reshape(-1, 2)[first], KW=KW[pv], R=reg, A=acc, I=inv.reshape(-1, 3), FK=np.asarray(kind), IH=np.zeros((0, 3), int))


def quant_weights(W):
    idx = np.argsort(-W, axis=1)[:, :4]; w = np.take_along_axis(W, idx, 1)
    w = w / np.maximum(w.sum(1, keepdims=True), 1e-9); q = np.floor(w * 255 + 0.5).astype(int)
    q[:, 0] += 255 - q.sum(1)
    return idx.astype(np.uint8), np.clip(q, 0, 255).astype(np.uint8)


# ---------------------------------------------------------------- the driver (tomkart): rigid parts, joints from the parts
DRIVER_PARTS = {  # mesh -> kind, bone (MIX index) or None (torso / by rule)
    'Object_0': ('drop', 5), 'Object_1': ('helmet', 5), 'Object_2': ('helmet', 5), 'Object_3': ('visor', 5), 'Object_4': ('helmet', 5),
    'Object_12': ('hans', 3), 'Object_11': ('suit', 'chest'), 'Object_13': ('suit', 'belly'), 'Object_20': ('suit', 'pelvis'),
}


def build_driver_src(M, height):
    """joint positions from the rigid parts (left = -x in Unity space) and per-vertex weights; returns a synthetic Src"""
    prims = M.prims
    allP = np.concatenate([d['P'] for d in prims]); lo = allP.min(0); hi = allP.max(0); s = height / (hi[1] - lo[1])
    off = np.array([-(lo[0] + hi[0]) / 2, -lo[1], 0.0])
    for d in prims: d['P'] = (d['P'] + off) * s
    allP = np.concatenate([d['P'] for d in prims]); zc = (allP[:, 2].min() + allP[:, 2].max()) / 2
    byn = {d['name']: d for d in prims}
    def bb(n): p = byn[n]['P']; return p.min(0), p.max(0), (p.min(0) + p.max(0)) / 2
    def side(n): return np.sign(bb(n)[2][0])  # -1 left
    def ends(n):
        """the two ends of a limb part along its main axis (centre +- half extent)"""
        p = byn[n]['P']; c = p.mean(0); u, sv, vt = np.linalg.svd(p - c, full_matrices=False); ax = vt[0]
        t = (p - c) @ ax; a = c + ax * t.min(); b = c + ax * t.max()
        return a, b
    L = {}
    # legs: thigh 16/19, shin 15/18, foot 14/17; arms: upper 7/10, fore 6/9, glove 5/8
    for n_th, n_sh, n_ft in (('Object_16', 'Object_15', 'Object_14'), ('Object_19', 'Object_18', 'Object_17')):
        sd = 'Left' if side(n_th) < 0 else 'Right'
        a, b = ends(n_th); top, bot = (a, b) if a[1] > b[1] else (b, a)
        a2, b2 = ends(n_sh); top2, bot2 = (a2, b2) if a2[1] > b2[1] else (b2, a2)
        lo3, hi3, c3 = bb(n_ft)
        L[sd + 'UpLeg'] = top - np.array([0, 0.03, 0]); L[sd + 'Leg'] = (bot + top2) / 2
        L[sd + 'Foot'] = np.array([c3[0], lo3[1] + 0.085 * height / 1.78, lo3[2] + 0.06]); L[sd + 'ToeBase'] = np.array([c3[0], lo3[1] + 0.02, hi3[2] - 0.05])
        L[sd + 'UpLeg'][2] = L[sd + 'Leg'][2] = (L[sd + 'UpLeg'][2] + L[sd + 'Leg'][2]) / 2
    for n_up, n_fo, n_gl in (('Object_7', 'Object_6', 'Object_5'), ('Object_10', 'Object_9', 'Object_8')):
        sd = 'Left' if side(n_up) < 0 else 'Right'
        a, b = ends(n_up); sh, el = (a, b) if abs(a[0]) < abs(b[0]) else (b, a)
        a2, b2 = ends(n_fo); el2, wr = (a2, b2) if abs(a2[0]) < abs(b2[0]) else (b2, a2)
        g = byn[n_gl]['P']; gc = g.mean(0); u_, s_, vt = np.linalg.svd(g - gc, full_matrices=False); ax = vt[0]
        if ax @ (gc - wr) < 0: ax = -ax
        tmax = ((g - gc) @ ax).max(); tip = gc + ax * tmax
        L[sd + 'Arm'] = sh + (el - sh) * 0.08; L[sd + 'ForeArm'] = (el + el2) / 2; L[sd + 'Hand'] = wr + (gc - wr) * 0.25
        L[sd + 'HandMiddle1'] = L[sd + 'Hand'] + (tip - L[sd + 'Hand']) * 0.55
        L[sd + 'Shoulder'] = np.array([L[sd + 'Arm'][0] * 0.25, L[sd + 'Arm'][1] + 0.01, L[sd + 'Arm'][2] + 0.01])
    hips = (L['LeftUpLeg'] + L['RightUpLeg']) / 2 + np.array([0, 0.05, 0])
    lo_h, hi_h, c_h = bb('Object_2')
    head = np.array([0.0, lo_h[1] + 0.32 * (hi_h[1] - lo_h[1]), c_h[2] - 0.02])
    neck = np.array([0.0, (L['LeftArm'][1] + head[1]) / 2 - 0.02, (hips[2] + head[2]) / 2])
    sh_y = L['LeftArm'][1]
    sp2 = np.array([0.0, sh_y - 0.17, hips[2] + 0.01]); sp1 = np.array([0.0, (hips[1] + sp2[1]) / 2 + 0.02, hips[2] + 0.005])
    sp0 = np.array([0.0, hips[1] + 0.10, hips[2]])
    L.update(Hips=hips, Spine=sp0, Spine1=sp1, Spine2=sp2, Neck=neck, Head=head)
    names = MIX + ['LeftHandMiddle1', 'RightHandMiddle1']
    parent = [-1, 0, 1, 2, 3, 4, 3, 6, 7, 8, 3, 10, 11, 12, 0, 14, 15, 16, 0, 18, 19, 20, 9, 13]
    pos = np.array([L[n] for n in names])
    # weights: rigid parts, blended over ~6 cm at each joint (each side half), the torso by height along the spine
    def seg_w(Pv, j, jc, jp):
        """weights of part vertices bound to joint j (segment j -> jc), blending with the parent jp near the joint"""
        W = np.zeros((len(Pv), len(names)))
        a = pos[j]; b = pos[jc] if jc is not None else a + (a - pos[parent[j]])
        d = b - a; t = ((Pv - a) @ d) / (d @ d); L_ = np.linalg.norm(d)
        bl = np.clip(0.5 - t * L_ / 0.06, 0, 0.5) if jp is not None else np.zeros(len(Pv))
        W[:, j] = 1 - bl
        if jp is not None: W[:, jp] += bl
        # the distal end blends half into the child
        if jc is not None:
            bc = np.clip(0.5 - (1 - t) * L_ / 0.06, 0, 0.5); W[:, j] -= bc; W[:, jc] += bc
        return W
    ni = {n: i for i, n in enumerate(names)}
    parts = []
    for d in prims:
        nm = d['name']; Pv = d['P']
        if nm in DRIVER_PARTS:
            kind, bone = DRIVER_PARTS[nm]
            W = np.zeros((len(Pv), len(names)))
            if bone in ('chest', 'belly', 'pelvis'):
                # by height along the spine; the chest's outer top blends into the upper arms, the pelvis' sides into the thighs
                ys = np.array([pos[0][1], pos[1][1], pos[2][1], pos[3][1]])
                for vi, p in enumerate(Pv):
                    y = p[1]
                    if y <= ys[0]: W[vi, 0] = 1
                    elif y >= ys[3]: W[vi, 3] = 1
                    else:
                        k = np.searchsorted(ys, y) - 1; u = (y - ys[k]) / (ys[k + 1] - ys[k]); W[vi, k] = 1 - u; W[vi, k + 1] = u
                    if bone == 'chest':
                        for sd, ja in (('Left', 7), ('Right', 11)):
                            sh = pos[ja]; dx = (p[0] - sh[0] * 0.55) / (sh[0] * 0.45)
                            if dx > 0 and p[1] > sh[1] - 0.14:
                                w = np.clip(dx, 0, 1) * np.clip((p[1] - (sh[1] - 0.14)) / 0.1, 0, 1) * 0.5
                                W[vi] *= (1 - w); W[vi, ja] += w * 0.6; W[vi, ja - 1] += w * 0.4
                    if bone == 'pelvis':
                        for ja in (14, 18):
                            th = pos[ja]
                            if np.sign(p[0]) == np.sign(th[0]) and p[1] < th[1] + 0.02 and abs(p[0]) > abs(th[0]) * 0.35:
                                w = np.clip((th[1] + 0.02 - p[1]) / 0.12, 0, 0.6); W[vi] *= (1 - w); W[vi, ja] += w
            else:
                W[:, bone] = 1
        else:
            sd = 'Left' if np.sign(Pv.mean(0)[0]) < 0 else 'Right'; k = {'Object_16': 'UpLeg', 'Object_19': 'UpLeg', 'Object_15': 'Leg', 'Object_18': 'Leg',
                                                                        'Object_14': 'Foot', 'Object_17': 'Foot', 'Object_7': 'Arm', 'Object_10': 'Arm',
                                                                        'Object_6': 'ForeArm', 'Object_9': 'ForeArm', 'Object_5': 'Hand', 'Object_8': 'Hand'}[nm]
            kind = 'gloves' if k == 'Hand' else 'suit'
            j = ni[sd + k]; ch = {'UpLeg': 'Leg', 'Leg': 'Foot', 'Foot': None, 'Arm': 'ForeArm', 'ForeArm': 'Hand', 'Hand': None}[k]
            jc = ni[sd + ch] if ch else None
            jp = parent[j] if k not in ('UpLeg',) else 0
            W = seg_w(Pv, j, jc, jp if k != 'Foot' else ni[sd + 'Leg'])
            if k == 'Foot':  # the toe end of the boot to the ball joint
                tb = ni[sd + 'ToeBase']; w = np.clip((Pv[:, 2] - pos[tb][2] + 0.03) / 0.06, 0, 1)
                W *= (1 - w)[:, None]; W[:, tb] += w
        W = np.clip(W, 0, None); W /= np.maximum(W.sum(1, keepdims=True), 1e-9)
        d['kind'] = kind; d['W22'] = W
    return SynthSrc(names, parent, pos), byn


class SynthSrc(Src):
    """a Src from joint arrays (no glTF nodes): node ids = indices into names"""
    def __init__(self, names, parent, pos):
        self.M = None; self.scale = 1.0; n = len(names)
        self.nodes = list(range(n)); self.children = {i: [k for k in range(n) if parent[k] == i] for i in range(n)}
        self.idx = {nm: names.index(nm) for nm in MIX}; self.P = {i: pos[i].copy() for i in range(n)}
        self.R = {i: np.eye(3) for i in range(n)}; self.names = names; self._parent = parent

    def keep_of(self, node):
        nm = self.names[node]
        return MIX.index(nm) if nm in MIX else MIX.index(nm.replace('Middle1', ''))

    def repose(self, Q):
        class _M: pass
        self.M = _M(); self.M.byname = {nm: i for i, nm in enumerate(self.names)}
        return Src.repose(self, Q)


# ---------------------------------------------------------------- body records
def body_record(name, flags, src, Q, lods, height):
    t, q, W = src.body_skeleton(Q)
    pscale = t[0][1] / Q.ual_pelvis_y
    bind = np.array([np.linalg.inv(W[j]) for j in range(22)])
    return dict(name=name, flags=flags, pscale=pscale, height=height, t=t, q=q, W=W, bind=bind, lods=lods)


def body_variants(base_lods, gears, B):
    """base_lods: [(P, I, UV, KW, kind)] per LOD -> [variant][lod] finalized meshes (B['gear_map']: People.G* -> gear)"""
    out = []
    for gk in GEARS:
        row = []
        for l, body in enumerate(base_lods):
            g = {k: v[l] for k, v in gears.items()}
            row.append(finalize(*compose(body, B['gear_map'].get(gk, gk), g, B)))
        out.append(row)
    return out


def write_pack(path, atlas_bytes, bodies, clips, external=None):
    """PPL2 version 2. external: an RGBA atlas (uint8 array) written next to the pack as <name>_atlas.png (Unity imports
    it, ASTC on Android: no runtime decode / compression); the pack then holds empty jpg / png blocks"""
    o = bytearray(b'PPL2'); o += struct.pack('<ii', 2, 22)
    if external is not None:
        jpg, png = b'', b''
        Image.fromarray(external, 'RGBA').save(path[:-len('.bytes')] + '_atlas.png', optimize=True)
    else: jpg, png = atlas_bytes
    o += struct.pack('<I', len(jpg)) + jpg + struct.pack('<I', len(png)) + png
    o += struct.pack('<i', len(bodies))
    for b in bodies:
        o += struct.pack('<B', len(b['name'])) + b['name'].encode() + struct.pack('<Bff', b['flags'], b['pscale'], b['height'])
        for j in range(22): o += struct.pack('<7f', *b['t'][j], *b['q'][j])
        for j in range(22): o += struct.pack('<16f', *b['bind'][j].reshape(-1))
        o += struct.pack('<ii', len(b['lods']), len(b['lods'][0]))
        for var in b['lods']:
            for m in var:
                bi, bw = quant_weights(m['KW'])
                assert len(m['P']) < 65535
                IH = m.get('IH', np.zeros((0, 3), int))
                o += struct.pack('<iii', len(m['P']), len(m['I']), len(IH))
                o += m['P'].astype('<f4').tobytes()
                o += np.clip(np.round(m['N'] * 127), -127, 127).astype(np.int8).tobytes()
                uv = m['UV'].copy(); uv[:, 1] = 1 - uv[:, 1]  # (Unity: v up)
                o += np.clip(np.round(uv * 65535), 0, 65535).astype('<u2').tobytes()
                o += m['R'].astype(np.uint8).tobytes(); o += m.get('A', m['R']).astype(np.uint8).tobytes()
                o += bi.tobytes(); o += bw.tobytes()
                o += m['I'].astype('<u2').tobytes(); o += IH.astype('<u2').tobytes()
    o += struct.pack('<i', len(clips))
    for c in clips:
        o += struct.pack('<B', len(c['name'])) + c['name'].encode(); o += struct.pack('<fiBf', c['fps'], len(c['q']), c['loop'], c['stride'])
        o += np.clip(np.round(c['q'] * 32767), -32767, 32767).astype('<i2').tobytes(); o += c['p'].astype('<f4').tobytes()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, 'wb').write(o)
    print('wrote', path, len(o), 'bytes')


def walk_stride(Q, q, p):
    """m per cycle of a walk clip on the game's male body: twice the feet's fore-aft travel (relative to the pelvis)"""
    b = Q.bodies[0]; zs = []
    for f in range(len(q)):
        t = b['t'].copy(); t[0] = p[f] * b['pscale']
        W = fk(Q.parent, t, q[f]); zs.append(W[16][2, 3] - W[0][2, 3])
    zs = np.array(zs); return 2.0 * (zs.max() - zs.min())


# ---------------------------------------------------------------- the packs
DRIVER = '4eec013f-race_car_driver_-_low_poly'  # (folder names as downloaded)
AVATAR = '974cfcfd-free_avatar_for_iclone'
SPECS = {
    'Avatar': dict(file=None, height=1.84, flags=0, parts={'mesh_3': 'skin', 'Object_2': 'skin', 'Object_7': 'top', 'Object_5': 'bottom',
                   'Object_6': 'shoes', 'Object_4': 'cap', 'mesh_0': 'eyes', 'mesh_1': 'eyes', 'mesh_8': 'drop'},
                   layout={'Wolf3D_Skin': (768, 256, 256, 256), 'Wolf3D_Outfit_Top': (0, 768, 256, 256), 'Wolf3D_Outfit_Bottom': (256, 768, 256, 256),
                           'Wolf3D_Outfit_Footwear': (512, 768, 192, 192), 'Wolf3D_Headwear': (704, 768, 128, 128), 'Wolf3D_Eye': (832, 768, 64, 64),
                           'Wolf3D_Body': (896, 768, 32, 32)},
                   B=dict(own_cap=True, gear_map={'helmet': 'helmet', 'headset': 'headset', 'cap': 'cap', 'bare': 'cap'})),
    'Ch08': dict(file='Ch08_nonPBR.glb', height=1.80, flags=0, parts={'Mesh': 'skin', 'Mesh.004': 'top', 'Mesh.003': 'bottom', 'Mesh.001': 'shoes',
                 'Mesh.005': 'hair_cards', 'Mesh.006': 'hair_cards', 'Mesh.002': 'drop'},
                 layout={'Ch08_body': (0, 0, 256, 384), 'Ch08_body1': (256, 0, 256, 512), '_hair': (0, 384, 64, 64)},
                 hair_offset=0.005, hair_range=0.014, B=dict(gear_map={}, cap_lift=0.008, cap_grow=1.03)),
    'Ch13': dict(file='Ch13_nonPBR.glb', height=1.69, flags=1, parts={'Mesh.001': 'skin', 'Mesh.003': 'top', 'Mesh.002': 'bottom', 'Mesh': 'shoes',
                 'Mesh.005': 'hair_cards', 'Mesh.004': 'drop'},
                 layout={'Ch13_body': (512, 0, 512, 448), '_hair': (512, 448, 64, 64)}, hair_offset=0.008, hair_range=0.03, hair_face=False,
                 erase=[(650, 33, 670, 57), (643, 86, 671, 116)], B=dict(gear_map={}, cap_lift=0.008, cap_grow=1.03)),
    'Remy': dict(file='Remy.glb', height=1.78, flags=0, parts={'Body': 'skin', 'Tops': 'top', 'Bottoms': 'bottom', 'Shoes': 'shoes', 'Hair': 'hair',
                 'Eyes': 'eyes', 'Eyelashes': 'drop'},
                 layout={'Bodymat': (0, 512, 256, 256), 'Topmat': (256, 512, 256, 256), 'Bottommat': (256, 768, 256, 256), 'Shoesmat': (0, 768, 128, 128),
                         'Hairmat': (128, 768, 128, 128)}, B=dict(gear_map={}, cap_lift=0.012, cap_grow=1.06)),
}
LOD_TRIS = [4000, 1500, 500]  # crew / avatar: near (< ~15 m), mid (< ~45 m), far
CLIPS = [  # name, file, loop, t0, t1
    ('MIdle', 'Idle.glb', 1, None, None), ('MWalk', 'Walking.glb', 1, None, None), ('MTalk', 'Talking.glb', 1, None, None),
    ('MTalk2', 'Talking_1.glb', 1, None, None), ('MClap', 'Clapping.glb', 1, None, None), ('MSitClap', 'Sitting_Clap.glb', 1, None, None),
    ('MWave', 'Waving.glb', 1, None, None), ('MBriefcase', 'Standing_W_Briefcase_Idle.glb', 1, None, None),
]


def scaled_src(M, height, Q):
    allP = np.concatenate([d['P'] for d in M.prims]); s = height / (allP[:, 1].max() - allP[:, 1].min())
    src = Src(M, s); src.repose(Q); return src


def gear_sources(ain, atlas, rects):
    """head gear meshes placed in an atlas: tomkart's helmet, the avatar's cap; rects: helmet, glass, strip, cap, dark"""
    D = Model(f'{ain}/{DRIVER}/scene.gltf')
    allP = np.concatenate([d['P'] for d in D.prims]); lo, hi = allP.min(0), allP.max(0); s = 1.80 / (hi[1] - lo[1])
    for d in D.prims: d['P'] = (d['P'] - np.array([(lo[0] + hi[0]) / 2, lo[1], 0])) * s
    byn = {d['name']: d for d in D.prims}
    face = byn['Object_0']['P']; hc = (face.min(0) + face.max(0)) / 2; hs = face.max(0) - face.min(0)
    for nm, key in (('Object_2', 'helmet'), ('Object_3', 'glass')):
        d = byn[nm]; k = ('img', d['imgname'])
        if k not in atlas.tiles: atlas.place(k, texfix.clean(d['img'], d['imgname']), rects[key])
    P = []; I = []; UV = []; K = []; base = 0
    for nm, kind in (('Object_1', 'helmet'), ('Object_2', 'helmet'), ('Object_3', 'visor'), ('Object_4', 'helmet')):
        d = byn[nm]; uv = atlas.map(('img', d['imgname']), d['uv'])
        P.append(d['P']); I.append(d['I'] + base); UV.append(uv[d['I']]); K += [kind] * len(d['I']); base += len(d['P'])
    P = np.concatenate(P); I = np.concatenate(I); UV = np.concatenate(UV); K = np.array(K)
    helmet = [gear_parts('helmet', P, I, UV, K, hc, hs, t) for t in (None, 170, 170)]
    W = Model(f'{ain}/{AVATAR}/scene.gltf')
    allP = np.concatenate([d['P'] for d in W.prims]); s = SPECS['Avatar']['height'] / (allP[:, 1].max() - allP[:, 1].min())
    wb = {d['name']: d for d in W.prims}
    hp = wb['mesh_3']['P'] * s; hcw = (np.percentile(hp, 1, 0) + np.percentile(hp, 99, 0)) / 2; hsw = np.percentile(hp, 99, 0) - np.percentile(hp, 1, 0)
    d = wb['Object_4']; atlas.place(('gear', 'cap'), d['img'], rects['cap'], [d['uv']])
    uv = atlas.map(('gear', 'cap'), d['uv'])
    cap = [gear_parts('cap', d['P'] * s, d['I'], uv[d['I']], np.array(['cap'] * len(d['I'])), hcw, hsw, t) for t in (420, 140, 140)]
    atlas.place(('gear', 'dark'), None, rects['dark'], solid=np.array([0.09, 0.09, 0.1]))
    x, y, w, h, *_ = atlas.tiles[('gear', 'dark')]
    return {'helmet': helmet, 'cap': cap}, np.array([(x + w / 2) / atlas.w, (y + h / 2) / atlas.h])


def build_rigged(spec, M, src, atlas, Q, gears, uv_dark, lod_tris=LOD_TRIS):
    parts = load_skinned_parts(M, src, spec, atlas, spec['layout'], Q)
    for x0, y0, x1, y1 in spec.get('erase', []):  # (printed marks on the clothes: painted out)
        sub = (atlas.rgb[y0:y1, x0:x1] * 255).astype(np.uint8); med = np.median(sub.reshape(-1, 3), 0)
        m = (np.abs(sub.astype(float) - med).max(2) > 25).astype(np.uint8) * 255; m = cv2.dilate(m, np.ones((3, 3), np.uint8))
        atlas.rgb[y0:y1, x0:x1] = cv2.inpaint(sub, m, 4, cv2.INPAINT_TELEA) / 255.0
    # opaque hair-card hair (Remy) eats the budget when decimated with the body: cut it down on its own first
    for i, pt in enumerate(parts):
        if pt.kind == 'hair' and len(pt.I) > 800:
            P_, I_, UV_, KW_, k_ = lod_mesh(*merge([pt]), 800)
            parts[i] = Part(P_, I_, UV_, KW_, 'hair', pt.tile)
    P, I, UV, KW, kind = merge(parts)
    print(' ', spec['name'], 'source tris', len(I))
    lods = [lod_mesh(P, I, UV, KW, kind, t) for t in lod_tris]
    head = head_box(lods[0][0], lods[0][3], None)
    sk = lods[0][4]; Ps, Is, KWs = lods[0][0], lods[0][1], lods[0][3]
    vs = np.unique(Is[sk == 'skin']); sel = np.zeros(len(Ps), bool); sel[vs] = True
    hc, hs = head_box(Ps, KWs, sel)
    B = dict(spec['B']); B.update(hc=hc, hs=hs, uv_dark=uv_dark)
    var = body_variants(lods, gears, B)
    return lods, var, B


def driver_body(ain, atlas, Q):
    D = Model(f'{ain}/{DRIVER}/scene.gltf')
    src, byn = build_driver_src(D, 1.80)
    src.repose(Q)
    layout = {'RT_DriverSuit_baseColor.png': (0, 0, 512, 512), 'RT_Gloves_baseColor.png': (512, 0, 512, 256),
              'RT_HANS_baseColor.png': (512, 256, 128, 128), 'RT_HELMET_Glass_baseColor.png': (640, 256, 64, 64)}
    parts = []
    for d in D.prims:
        if d['kind'] == 'drop': continue
        key = ('img', d['imgname'])
        if key not in atlas.tiles: atlas.place(key, texfix.clean(d['img'], d['imgname']), layout[d['imgname']])
        n = len(d['P']); J = np.tile(np.arange(len(src.names)), (n, 1))
        P2, N2 = skin_repose(src, d['P'], d['N'], J, d['W22'])
        KW = weights22(src, J, d['W22'])
        uv = atlas.map(key, d['uv'])
        parts.append(Part(P2, d['I'], uv[d['I']], KW, d['kind'], key))
    P, I, UV, KW, kind = merge(parts)
    print('  Driver tris', len(I))
    lods = [(P, I, UV, KW, kind), lod_mesh(P, I, UV, KW, kind, 760), lod_mesh(P, I, UV, KW, kind, 400)]
    B = dict(gear_map={g: 'none' for g in GEARS}, hc=np.zeros(3), hs=np.ones(3) * 0.2, uv_dark=np.zeros(2))
    var = body_variants(lods, {}, B)
    return src, lods, var, B


def build_public(ain, repo, Q, prev):
    A = Atlas()
    gears, uv_dark = gear_sources(ain, A, {'helmet': (0, 512, 1024, 256), 'glass': (640, 256, 64, 64),
                                            'cap': (704, 768, 128, 128), 'dark': (928, 768, 32, 32)})
    dsrc, dl, dvar, dB = driver_body(ain, A, Q)
    bodies = [body_record('Driver', 2, dsrc, Q, dvar, 1.80)]
    spec = dict(SPECS['Avatar'], name='Avatar')
    W = Model(f'{ain}/{AVATAR}/scene.gltf'); wsrc = scaled_src(W, spec['height'], Q)
    wl, wvar, wB = build_rigged(spec, W, wsrc, A, Q, gears, uv_dark)
    bodies.append(body_record('Avatar', 0, wsrc, Q, wvar, spec['height']))
    paint(A, bodies)
    data = A.finish()
    write_pack(f'{repo}/Assets/Resources/People2/people2.bytes', data, bodies, [])
    return A, bodies


def paint(A, bodies):
    """tint mask from every variant's faces (their kinds)"""
    tris = []; kinds = []
    for b in bodies:
        for var in b['lods']:
            for m in var:
                tris.append(m['UV'][m['I']]); kinds.append(m['FK'])
    A.paint_tint(np.concatenate(tris), np.concatenate(kinds))


def build_private(work, ain, repo, Q, prev):
    A = Atlas()
    gears, uv_dark = gear_sources(ain, A, {'helmet': (512, 512, 512, 128), 'glass': (512, 640, 32, 32),
                                            'cap': (576, 640, 128, 128), 'dark': (704, 640, 32, 32)})
    bodies = []
    for nm in ('Ch08', 'Ch13', 'Remy'):
        spec = dict(SPECS[nm], name=nm)
        M = Model(f'{work}/{spec["file"]}'); src = scaled_src(M, spec['height'], Q)
        l, var, B = build_rigged(spec, M, src, A, Q, gears, uv_dark)
        bodies.append(body_record(nm, spec['flags'], src, Q, var, spec['height']))
    paint(A, bodies)
    data = A.finish()
    clips = []
    for name, f, loop, t0, t1 in CLIPS:
        M = Model(f'{work}/{f}'); src = Src(M, 1.0); src.repose(Q)
        q, p, dur = bake_clip(src, Q, 0, 24.0, t0, t1, bool(loop))
        stride = walk_stride(Q, q, p) if name == 'MWalk' else 0.0  # (m per cycle: twice the foot's fore-aft travel)
        print(' clip', name, len(q), 'frames', round(dur, 2), 's', 'stride', round(stride, 3))
        clips.append(dict(name=name, fps=24.0, loop=loop, q=q, p=p, stride=stride))
    write_pack(f'{repo}/Assets/Resources/People2/mixamo/mixamo.bytes', data, bodies, clips)
    return A, bodies, clips


def main():
    work, ain, repo = sys.argv[1], sys.argv[2], sys.argv[3]
    prev = sys.argv[sys.argv.index('--preview') + 1] if '--preview' in sys.argv else None
    only = sys.argv[sys.argv.index('--only') + 1].split(',') if '--only' in sys.argv else ['public', 'people3', 'mixamo']
    Q = QSkel(f'{repo}/Assets/Resources/People/people.bytes')
    import people3
    if 'public' in only:
        print('public pack (Art/People3 drivers, CC0)')
        A, bodies = people3.build_drivers(repo, Q)
        write_pack(f'{repo}/Assets/Resources/People2/people2.bytes', None, bodies, [], external=A.rgba)
        if prev: Image.fromarray(A.rgba[..., :3]).save(f'{prev}/atlas_public.jpg')
    if 'people3' in only:
        for group, fn in (('crew', 'people3'), ('podium', 'people3p')):
            print(fn, 'pack (Rocketbox, MIT):', group)
            A, bodies = people3.build_rocketbox(repo, ain, Q, group)
            write_pack(f'{repo}/Assets/Resources/People2/{fn}.bytes', None, bodies, [], external=A.rgba)
            if prev: Image.fromarray(A.rgba[..., :3]).save(f'{prev}/atlas_{fn}.jpg')
    if 'mixamo' in only and work and os.path.exists(f'{work}/Remy.glb'):
        print('private (Mixamo) pack')
        A2, b2, clips = build_private(work, ain, repo, Q, prev)
        if prev: Image.fromarray((A2.final * 255).astype(np.uint8)).save(f'{prev}/atlas_mixamo.jpg')


if __name__ == '__main__':
    main()
