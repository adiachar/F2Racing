# verify.py — reads a 'PPL2' pack back exactly as People.cs does and renders its bodies posed by the game's clips (from
# people.bytes and the pack's own clips), textured and team-tinted like People2.shader:
#   python3 -I verify.py <repo root> <pack .bytes> <out.png> [clip names ...]
import os, sys, struct, io
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from PIL import Image
from qskel import QSkel, fk
from raster import render, look, save


def srgb_dec(x): return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)
def srgb_enc(x): return np.where(x <= 0.0031308, 12.92 * x, 1.055 * np.power(np.clip(x, 0, None), 1 / 2.4) - 0.055)


def read_pack(path):
    d = open(path, 'rb').read(); o = 0
    def rd(fmt):
        nonlocal o; v = struct.unpack_from('<' + fmt, d, o); o += struct.calcsize('<' + fmt); return v
    assert d[:4] == b'PPL2'; o = 4; ver, nj = rd('ii')
    n, = rd('I'); jpg = d[o:o + n]; o += n; n, = rd('I'); png = d[o:o + n]; o += n
    col = Image.open(io.BytesIO(jpg)).convert('RGB'); tint = Image.open(io.BytesIO(png)).convert('L')
    nb, = rd('i'); bodies = []
    for b in range(nb):
        ln, = rd('B'); name = d[o:o + ln].decode(); o += ln
        flags, ps, h = rd('Bff')
        t = np.zeros((nj, 3)); q = np.zeros((nj, 4))
        for j in range(nj): v = rd('7f'); t[j] = v[:3]; q[j] = v[3:]
        bind = np.array([np.array(rd('16f')).reshape(4, 4) for _ in range(nj)])
        nv_, nl = rd('ii'); var = []
        for vi in range(nv_):
            row = []
            for l in range(nl):
                nv, nt = rd('ii')
                P = np.frombuffer(d, '<f4', nv * 3, o).reshape(nv, 3).astype(float); o += nv * 12
                N = np.frombuffer(d, 'i1', nv * 3, o).reshape(nv, 3) / 127.0; o += nv * 3
                UV = np.frombuffer(d, '<u2', nv * 2, o).reshape(nv, 2) / 65535.0; o += nv * 4
                R = np.frombuffer(d, 'u1', nv, o); o += nv
                BI = np.frombuffer(d, 'u1', nv * 4, o).reshape(nv, 4); o += nv * 4
                BW = np.frombuffer(d, 'u1', nv * 4, o).reshape(nv, 4) / 255.0; o += nv * 4
                I = np.frombuffer(d, '<u2', nt * 3, o).reshape(nt, 3).astype(int); o += nt * 6
                row.append(dict(P=P, N=N, UV=UV, R=R, BI=BI, BW=BW, I=I))
            var.append(row)
        bodies.append(dict(name=name, flags=flags, pscale=ps, height=h, t=t, q=q, bind=bind, var=var))
    nc, = rd('i'); clips = []
    for c in range(nc):
        ln, = rd('B'); name = d[o:o + ln].decode(); o += ln
        fps, n, loop, stride = rd('fiBf')
        Qs = np.frombuffer(d, '<i2', n * nj * 4, o).reshape(n, nj, 4) / 32767.0; o += n * nj * 8
        Ps = np.frombuffer(d, '<f4', n * 3, o).reshape(n, 3).astype(float); o += n * 12
        clips.append(dict(name=name, fps=fps, loop=loop, stride=stride, q=Qs, p=Ps))
    assert o == len(d), (o, len(d))
    return col, tint, bodies, clips


def pose_mesh(Q, body, m, clip, frame):
    t = body['t'].copy()
    if clip is None: q = body['q']
    else: q = clip['q'][frame % len(clip['q'])]; t[0] = clip['p'][frame % len(clip['p'])] * body['pscale']
    W = fk(Q.parent, t, q)
    S = np.array([W[j] @ body['bind'][j] for j in range(len(W))])
    M = np.einsum('vk,vkab->vab', m['BW'], S[m['BI']])
    P = np.einsum('vab,vb->va', M, np.c_[m['P'], np.ones(len(m['P']))])[:, :3]
    return P, W


PAL = {0: (0.05, 0.25, 0.75), 6: (0.05, 0.25, 0.75), 18: (0.05, 0.25, 0.75), 17: (0.05, 0.05, 0.06), 14: (0.95, 0.8, 0.15), 5: (0.15, 0.15, 0.18)}


def textured(col, tint, m, P, team=(0.05, 0.25, 0.75)):
    tex = np.asarray(col, float) / 255.0; tt = np.asarray(tint, float) / 255.0; H, Wd = tt.shape
    UV = m['UV']
    def tf(c, face):
        return c
    # bake the tint into a per-mesh texture copy (preview only): albedo = lerp(tex, pal * lin(tex) * 2, tint)
    pal = np.zeros((32, 3)); pal[:] = 1
    for r, c in PAL.items(): pal[r] = c
    pal[0] = pal[6] = pal[18] = team
    out = []
    # split faces by region so each region gets its own tinted texture
    for r in np.unique(m['R']):
        fsel = m['R'][m['I'][:, 0]] == r
        if not fsel.any(): continue
        lin = srgb_dec(tex); a = tt[..., None]
        al = lin * (1 - a) + np.asarray(pal[r])[None, None, :] * lin * 2 * a
        img = Image.fromarray((np.clip(srgb_enc(al), 0, 1) * 255).astype(np.uint8))
        uvp = UV.copy(); uvp[:, 1] = 1 - uvp[:, 1]
        out.append(dict(P=P, I=m['I'][fsel], uv=uvp, img=img))
    return out


def main():
    repo, pack, outp = sys.argv[1], sys.argv[2], sys.argv[3]
    want = sys.argv[4:] or ['rest', 'Idle', 'Kneel', 'Walk']
    Q = QSkel(f'{repo}/Assets/Resources/People/people.bytes')
    col, tint, bodies, clips = read_pack(pack)
    extra = {c['name']: c for c in clips}
    if len(sys.argv) > 4 and os.path.exists(os.path.join(os.path.dirname(pack), 'mixamo', 'mixamo.bytes')):
        extra.update({c['name']: c for c in read_pack(os.path.join(os.path.dirname(pack), 'mixamo', 'mixamo.bytes'))[3]})
    allc = {c['name']: c for c in Q.clips}; allc.update(extra)
    ims = []; tt = []
    only = os.environ.get('BODY')
    for b in bodies:
        if only and b['name'] != only: continue
        print(b['name'], 'pscale', round(b['pscale'], 3), 'height', b['height'], [[(len(m['P']), len(m['I'])) for m in row] for row in b['var']])
        for vi, cn in enumerate(want):
            gear = vi % 4
            m = b['var'][gear][int(os.environ.get('LOD', '0'))]
            cl = None if cn == 'rest' else allc[cn]
            P, W = pose_mesh(Q, b, m, cl, 7)
            c = np.array([0, 0.9, 0])
            eye = c + np.array([1.6, 0.25, 3.2])
            ims.append(render(textured(col, tint, m, P), look(eye, c), size=(260, 400), ortho=1.0)); tt.append(f"{b['name']} g{gear} {cn}")
    save(ims, outp, tt)


if __name__ == '__main__':
    main()
