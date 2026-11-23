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
    if len(jpg) == 0:  # (v2: the atlas is <pack>_atlas.png next to it, alpha = v2 tint / card coverage)
        im = Image.open(path[:-len('.bytes')] + '_atlas.png').convert('RGBA'); col = im.convert('RGB'); tint = im.split()[3]
    else:
        col = Image.open(io.BytesIO(jpg)).convert('RGB'); tint = Image.open(io.BytesIO(png)).convert('L')
    if ver < 2: tint = tint.point(lambda v: v // 2)  # (v1: alpha = tint amount -> the v2 encoding, as People.cs)
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
                nv, nt = rd('ii'); nth = rd('i')[0] if ver >= 2 else 0
                P = np.frombuffer(d, '<f4', nv * 3, o).reshape(nv, 3).astype(float); o += nv * 12
                N = np.frombuffer(d, 'i1', nv * 3, o).reshape(nv, 3) / 127.0; o += nv * 3
                UV = np.frombuffer(d, '<u2', nv * 2, o).reshape(nv, 2) / 65535.0; o += nv * 4
                R = np.frombuffer(d, 'u1', nv, o); o += nv
                if ver >= 2: Ac = np.frombuffer(d, 'u1', nv, o); o += nv
                else: Ac = R
                BI = np.frombuffer(d, 'u1', nv * 4, o).reshape(nv, 4); o += nv * 4
                BW = np.frombuffer(d, 'u1', nv * 4, o).reshape(nv, 4) / 255.0; o += nv * 4
                I = np.frombuffer(d, '<u2', nt * 3, o).reshape(nt, 3).astype(int); o += nt * 6
                IH = np.frombuffer(d, '<u2', nth * 3, o).reshape(nth, 3).astype(int); o += nth * 6
                row.append(dict(P=P, N=N, UV=UV, R=R, A=Ac, BI=BI, BW=BW, I=I, IH=IH))
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


PAL = {0: (0.05, 0.25, 0.75), 6: (0.05, 0.25, 0.75), 18: (0.05, 0.25, 0.75), 17: (0.05, 0.05, 0.06), 14: (0.95, 0.8, 0.15), 5: (0.15, 0.15, 0.18),
       7: (0.92, 0.92, 0.93), 15: (0.05, 0.25, 0.75)}


def textured(col, tint, m, P, team=(0.05, 0.25, 0.75)):
    """as People2.shader: amount = saturate(2a), accent share = saturate(2a - 1), colour = lerp(pal[R], pal[A], share);
    hair cards (IH) alpha-clipped at 0.5 with the texture as it is"""
    tex = np.asarray(col, float) / 255.0; tt = np.asarray(tint, float) / 255.0
    UV = m['UV']
    pal = np.zeros((32, 3)); pal[:] = 1
    for r, c in PAL.items(): pal[r] = c
    pal[0] = pal[6] = pal[18] = team
    amt = np.clip(tt * 2, 0, 1)[..., None]; acc = np.clip(tt * 2 - 1, 0, 1)[..., None]
    lin = srgb_dec(tex)
    out = []
    uvp = UV.copy(); uvp[:, 1] = 1 - uvp[:, 1]
    pairs = sorted(set(zip(m['R'][m['I'][:, 0]].tolist(), m['A'][m['I'][:, 0]].tolist())))
    for r, a in pairs:
        fsel = (m['R'][m['I'][:, 0]] == r) & (m['A'][m['I'][:, 0]] == a)
        pc = np.asarray(pal[r])[None, None, :] * (1 - acc) + np.asarray(pal[a])[None, None, :] * acc
        al = lin * (1 - amt) + pc * lin * 2 * amt
        img = Image.fromarray((np.clip(srgb_enc(al), 0, 1) * 255).astype(np.uint8))
        out.append(dict(P=P, I=m['I'][fsel], uv=uvp, img=img))
    if len(m['IH']):
        out.append(dict(P=P, I=m['IH'], uv=uvp, img=col, cut=tint, both=True))
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
            m = b['var'][min(gear, len(b['var']) - 1) if 'GEAR' not in os.environ else int(os.environ['GEAR'])][min(int(os.environ.get('LOD', '0')), len(b['var'][0]) - 1)]
            cl = None if cn == 'rest' else allc[cn]
            P, W = pose_mesh(Q, b, m, cl, 7)
            c = np.array([0, 0.9, 0])
            eye = c + np.array([1.6, 0.25, 3.2])
            ims.append(render(textured(col, tint, m, P), look(eye, c), size=(260, 400), ortho=1.0)); tt.append(f"{b['name']} g{gear} {cn}")
    save(ims, outp, tt)


if __name__ == '__main__':
    main()
