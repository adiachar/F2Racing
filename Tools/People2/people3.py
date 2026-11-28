# people3.py — Adi's asset pack people (Art/People3) -> People2 bodies (used by build.py):
#  * the pack race drivers driver_01 / driver_02 (CC0, MakeHuman base: seamed suit, gloves, racing boots, closed helmet
#    with dark visor; 19k tris; tint mask _tint.png: white = team colour, 128 = accent) -> "Driver" / "Driver2" in the
#    public pack people2.bytes, 4 LODs (hero = the full mesh for menus / walk-to-car / podium, then ~4k / 1.5k / 0.5k);
#  * the Microsoft Rocketbox people (MIT, Art/People3/LICENSE_rocketbox.md): engineers (team shirt from _tint.png, own
#    headset; + a crew-helmet variant for the stop crew), fans, marshals, officials, photographer, TV presenter ->
#    people3.bytes, 3 LODs (~5k / 1.5k / 0.55k + hair cards on the first two).
# Both are already rigged with Mixamo bone names (A-pose, metres, feet at 0, facing +z): re-posed and bound to the game's
# 22-joint skeleton by retarget.py exactly like the Mixamo crew (every game clip, the IK and the pit choreography drive
# them). Rocketbox hair is alpha-clip cards: they become a second sub-mesh drawn with People2Hair.shader (alpha clip),
# their coverage in the atlas alpha of the card islands; the rest of the atlas alpha is the tint (People2 v2 encoding).
import os
import numpy as np
import cv2
from PIL import Image
from p2io import Model
from retarget import Src, skin_repose, weights22
import build as B

P3 = 'Art/People3'
DRIVER_LODS = [None, 4000, 1500, 500]
P3_LODS = [5000, 1500, 550]
CARD_BUDGET = [1200, 300, 0]  # hair-card triangles kept per LOD (largest first)

# Rocketbox people: folder -> (game name, female, role); role: 'eng', 'fan', 'marshal', 'official', 'photo', 'presenter'
ROCKETBOX = [('engineer_01', 'Eng1', 0, 'eng'), ('engineer_02', 'Eng2', 0, 'eng'), ('engineer_03', 'Eng3', 1, 'eng'), ('engineer_04', 'Eng4', 0, 'eng'),
             ('marshal_01', 'Marshal1', 0, 'marshal'), ('marshal_02', 'Marshal2', 1, 'marshal'), ('marshal_03', 'Marshal3', 0, 'marshal'),
             ('official_01', 'Official1', 0, 'official'), ('official_02', 'Official2', 1, 'official'),
             ('photographer_01', 'Photographer', 0, 'photo'), ('tv_presenter_01', 'Presenter', 1, 'presenter')] + \
            [('fan_%02d' % i, 'Fan%d' % i, 1 if i <= 7 else 0, 'fan') for i in range(1, 17)]


def face_alpha(img, uv, I):
    """min texture alpha of a face at its centre and three inner points"""
    a = np.asarray(img)
    if a.shape[2] < 4: return np.ones(len(I))
    H, W = a.shape[:2]; out = np.ones(len(I))
    for b in ([1 / 3, 1 / 3, 1 / 3], [.6, .2, .2], [.2, .6, .2], [.2, .2, .6]):
        u = (uv[I] * np.array(b)[None, :, None]).sum(1)
        x = np.clip((u[:, 0] % 1) * W, 0, W - 1).astype(int); y = np.clip((u[:, 1] % 1) * H, 0, H - 1).astype(int)
        out = np.minimum(out, a[y, x, 3] / 255.0)
    return out


def face_sample(arr, uv, I):
    """an image (H x W [x C], float) at the face centres"""
    H, W = arr.shape[:2]; u = uv[I].mean(1)
    x = np.clip((u[:, 0] % 1) * W, 0, W - 1).astype(int); y = np.clip((u[:, 1] % 1) * H, 0, H - 1).astype(int)
    return arr[y, x]


def load(repo, folder, atlas, rect, Q, driver=False):
    """one pack person -> (src, body mesh (P, I, UVc, KW, kind) welded, cards (P, I, UVc, KW) or None)"""
    M = Model(f'{repo}/{P3}/{folder}/{folder}.glb'); pr = M.prims[0]
    src = Src(M, 1.0); src.repose(Q)
    P2, _ = skin_repose(src, pr['P'], pr['N'], pr['J'], pr['W'])
    KW = weights22(src, pr['J'], pr['W'])
    img = pr['img']; I = pr['I']; uv = pr['uv']
    key = (folder, 'tex'); atlas.place(key, img, rect)
    uvc = atlas.map(key, uv)[I]
    x, y, w, h, *_ = atlas.tiles[key]
    # tint mask -> atlas tint amount / accent coverage
    tp = f'{repo}/{P3}/{folder}/{folder}_tint.png'
    m = None
    if os.path.exists(tp):
        m = np.asarray(Image.open(tp).convert('L'), np.float32) / 255.0
        if m.shape[:2] != (img.size[1], img.size[0]): m = cv2.resize(m, img.size, interpolation=cv2.INTER_AREA)
        if driver:
            tint = (m > 0.25).astype(np.float32); acc = ((m > 0.25) & (m < 0.75)).astype(np.float32)
        else:
            # (the pack's engineer masks also catch bits of the jeans: keep the light-grey texels the pack painted for the tint)
            rgb = np.asarray(img.convert('RGB'), np.float32) / 255.0
            mx, mn = rgb.max(2), rgb.min(2); sat = (mx - mn) / np.maximum(mx, 1e-3)
            grey = np.clip((0.22 - sat) / 0.08, 0, 1) * np.clip((mx - 0.3) / 0.1, 0, 1)
            tint = m * cv2.GaussianBlur(grey, (5, 5), 0); acc = np.zeros_like(m)
            # (... and specks at the trousers' waist, the top of the Rocketbox layout: shirts start lower)
            n, lab, st, _ = cv2.connectedComponentsWithStats((tint > 0.25).astype(np.uint8), connectivity=8)
            for c in range(1, n):
                if st[c, 1] + st[c, 3] < 0.13 * tint.shape[0]: tint[cv2.dilate((lab == c).astype(np.uint8), np.ones((5, 5), np.uint8)) > 0] = 0
        tint, acc = grow_out(tint, acc, uv, I, img.size)
        atlas.tint[y:y + h, x:x + w] = np.maximum(atlas.tint[y:y + h, x:x + w], cv2.resize(tint, (w, h), interpolation=cv2.INTER_AREA))
        atlas.acc[y:y + h, x:x + w] = np.maximum(atlas.acc[y:y + h, x:x + w], cv2.resize(acc, (w, h), interpolation=cv2.INTER_AREA))
    # faces: hair cards (Rocketbox: alpha-clip texels), driver helmet / visor / suit
    fa = face_alpha(img, uv, I)
    cards = fa < 0.9 if not driver else np.zeros(len(I), bool)
    kind = np.array(['body'] * len(I), dtype=object)
    if driver:
        head = KW[I][:, :, 5].mean(1) > 0.5
        rgb = np.asarray(img.convert('RGB'), np.float32) / 255.0
        lum = face_sample(rgb, uv, I) @ np.array([0.2126, 0.7152, 0.0722])
        tf = face_sample(m, uv, I) if m is not None else np.zeros(len(I))
        kind[:] = 'dsuit'; kind[head] = 'dhelmet'; kind[head & (lum < 0.12) & (tf < 0.1)] = 'dvisor'
    # card islands: their texels take the coverage (alpha clip), not the tint
    if cards.any():
        mk = np.zeros((atlas.h, atlas.w), np.uint8); sc = np.array([atlas.w, atlas.h]) * 4
        for t in uvc[cards]: cv2.fillPoly(mk, [np.round(t * sc).astype(np.int32)], 255, lineType=cv2.LINE_8, shift=2)
        mk = cv2.dilate(mk, np.ones((5, 5), np.uint8))
        al = np.asarray(img, np.float32)[..., 3] / 255.0
        ra = cv2.resize(al, (w, h), interpolation=cv2.INTER_AREA)
        sub = mk[y:y + h, x:x + w] > 0
        atlas.cutm[y:y + h, x:x + w][sub] = 1; atlas.cut[y:y + h, x:x + w][sub] = ra[sub]
        atlas.tint[y:y + h, x:x + w][sub] = 0; atlas.acc[y:y + h, x:x + w][sub] = 0
    first, inv = B.weld(P2)
    body = (P2[first], inv[I[~cards]], uvc[~cards], KW[first], kind[~cards].astype(str))
    crd = None
    if cards.any():
        Ic = I[cards]; vs = np.unique(Ic); remap = -np.ones(len(P2), int); remap[vs] = np.arange(len(vs))
        crd = (P2[vs], remap[Ic], uvc[cards], KW[vs])
    return src, body, crd


def grow_out(tint, acc, uv, I, size):
    """the masks grown into the texels no face covers (a few px round the UV islands), so the downscaled atlas keeps
    full tint up to an island's edge (no untinted specks on tiny islands / at seams); covered texels keep their value"""
    W, H = size; cov = np.zeros((H, W), np.uint8)
    for t in uv[I]: cv2.fillPoly(cov, [np.round(t * np.array([W, H]) * 4).astype(np.int32)], 255, lineType=cv2.LINE_8, shift=2)
    free = cov == 0; k = np.ones((3, 3), np.uint8)
    t, a = tint.copy(), acc.copy()
    for _ in range(max(4, W // 256)):
        t = np.where(free, np.maximum(t, cv2.dilate(t, k)), t); a = np.where(free, np.maximum(a, cv2.dilate(a, k)), a)
    return t, a


def compact(P, I, UV, KW, kind):
    used = np.zeros(len(P), bool); used[I.reshape(-1)] = True; remap = np.cumsum(used) - 1
    return P[used], remap[I], UV, KW[used], kind


def card_lod(crd, budget):
    """the largest hair cards up to `budget` triangles (None: none)"""
    if crd is None or budget <= 0: return None
    P, I, UV, KW = crd
    if len(I) > budget:
        area = np.linalg.norm(np.cross(P[I[:, 1]] - P[I[:, 0]], P[I[:, 2]] - P[I[:, 0]]), axis=1)
        keep = np.sort(np.argsort(-area)[:budget]); I = I[keep]; UV = UV[keep]
    used = np.zeros(len(P), bool); used[I.reshape(-1)] = True; remap = np.cumsum(used) - 1
    return P[used], remap[I], UV, KW[used]


def with_cards(f, crd):
    """a finalized body mesh + hair cards as the second sub-mesh (IH: indices into the joined vertex arrays)"""
    if crd is None: return f
    P, I, UV, KW = crd
    c = B.finalize(P, I, UV, KW, np.array(['cards'] * len(I)))
    # cards are single-sided in the source: People2Hair draws both sides (Cull Off), so the normals face the camera there
    n0 = len(f['P'])
    out = dict(f)
    for k in ('P', 'N', 'UV', 'KW', 'R', 'A'): out[k] = np.concatenate([f[k], c[k]])
    out['IH'] = c['I'] + n0
    return out


def headless(l, hc, hs):
    """the head faces above the jaw line (face, hair, own headset and boom mic) dropped: the full-face helmet covers them"""
    P, I, UV, KW, kind = l
    c = P[I].mean(1); hw = KW[I][:, :, 5].mean(1)
    keep = ~((hw > 0.5) & (c[:, 1] > hc[1] - 0.3 * hs[1]))
    return compact(P, I[keep], UV[keep], KW, kind[keep])


def lod_exact(P, I, UV, KW, kind, target):
    """decimation that keeps vertices where they were (no optimal placement), so every LOD vertex IS a source vertex and
    takes its weights as they are (closest-point weights mixed up the inner thighs, which touch in the rest pose)"""
    P2, I2, UV2 = B.decimate(P, I, UV, target, keep_border=target > 2000, optimal=False)
    from scipy.spatial import cKDTree
    d, vi = cKDTree(P).query(P2)
    if d.max() > 1e-4: print('    (moved vertices:', int((d > 1e-4).sum()), ')')
    kd = cKDTree(np.c_[P[I].mean(1), UV.mean(1) * 0.5]); _, fi = kd.query(np.c_[P2[I2].mean(1), UV2.mean(1) * 0.5])
    return P2, I2, UV2, KW[vi], kind[fi]


def lods_of(body, targets):
    P, I, UV, KW, kind = body
    out = []
    for t in targets:
        if t is None or len(I) <= t: out.append(compact(P, I, UV, KW, kind))
        else: out.append(lod_exact(*compact(P, I, UV, KW, kind), t))
    return out


def build_drivers(repo, Q):
    """public pack bodies: Driver (Adi's new suited driver + helmet, driver_new.py) and Driver2 (driver_02);
    atlas 2048 x 1536 (Driver 1024^2 suit + tiles below, Driver2 1024^2 at the right)"""
    import driver_new
    A = B.Atlas(2048, 1536); bodies = []
    src, lods, h = driver_new.build(repo, A, Q, (0, 0, 1024, 1024))
    print('  Driver (driver_new) LOD tris', [len(m['I']) for m in lods], 'verts', [len(m['P']) for m in lods])
    bodies.append(B.body_record('Driver', 2, src, Q, [lods], h))
    src, body, _ = load(repo, 'driver_02', A, (1024, 0, 1024, 1024), Q, driver=True)
    print('  Driver2 source tris', len(body[1]))
    var = [[B.finalize(*l) for l in lods_of(body, DRIVER_LODS)]]
    bodies.append(B.body_record('Driver2', 2, src, Q, var, float(body[0][:, 1].max() - body[0][:, 1].min())))
    print('   LOD tris', [len(m['I']) for m in var[0]], 'verts', [len(m['P']) for m in var[0]])
    A.finish()
    return A, bodies


def build_rocketbox(repo, ain, Q, group):
    """group 'crew' (people3.bytes, loaded with the other packs: engineers + marshals + the crew helmet; LOD0 ~4k like the
    crew before) or 'podium' (people3p.bytes, loaded only for the podium ceremony: officials, photographer, presenter,
    fans; LOD0 ~5k). Atlas 2048 x 1024: 512^2 per person (fans 256^2)"""
    A = B.Atlas(2048, 1024)
    gears, uv_dark = (None, None)
    if group == 'crew':
        gears, uv_dark = B.gear_sources(ain, A, {'helmet': (1536, 512, 512, 128), 'glass': (1536, 640, 32, 32),
                                                 'cap': (1600, 640, 128, 128), 'dark': (1728, 640, 32, 32)})
    targets = [4000, 1500, 550] if group == 'crew' else P3_LODS
    bodies = []; slot = 0; fan = 0
    for folder, name, female, role in ROCKETBOX:
        if (role in ('eng', 'marshal')) != (group == 'crew'): continue
        if role == 'fan':
            s = 4 + fan // 4; k = fan % 4; fan += 1
            rect = ((s % 4) * 512 + (k % 2) * 256, (s // 4) * 512 + (k // 2) * 256, 256, 256)
        else:
            rect = ((slot % 4) * 512, (slot // 4) * 512, 512, 512); slot += 1
        src, body, crd = load(repo, folder, A, rect, Q)
        lods = lods_of(body, targets)
        own = [with_cards(B.finalize(*l), card_lod(crd, CARD_BUDGET[i] if group != 'crew' else min(CARD_BUDGET[i], 400))) for i, l in enumerate(lods)]
        var = [own]
        if role == 'eng':
            # the stop crew's variant: the crew helmet (tomkart's, fitted to the head box) instead of hair and headset
            Ps, Is, KWs = lods[0][0], lods[0][1], lods[0][3]
            hc, hs = B.head_box(Ps, KWs, None)
            Bd = dict(hc=hc, hs=hs, uv_dark=uv_dark, helmet_grow=1.06, helmet_lift=0.0)
            hel = [B.finalize(*B.compose(headless(l, hc, hs), 'helmet', {'helmet': gears['helmet'][i]}, Bd)) for i, l in enumerate(lods)]
            var = [hel, own]
        h = float(body[0][:, 1].max() - body[0][:, 1].min())
        bodies.append(B.body_record(name, female, src, Q, var, h))
        print(' ', name, role, 'tris', [[len(m['I']) + len(m['IH']) for m in v] for v in var], 'cards', 0 if crd is None else len(crd[1]))
    if group == 'crew': B.paint(A, bodies)  # (the crew helmet's red stripe -> tint)
    A.finish()
    return A, bodies
