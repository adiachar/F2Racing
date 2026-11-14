# Software renderer for car .bytes files (z-buffer), to see the model the way the game camera does.
# usage: render.py file.bytes out.png view [cull] [ao]   (ao: shade with the baked vertex AO of F2M2 files)
#        render.py game file.bytes before|after out.png views   (the rival car as CarModel builds it: per part / merged)
#   view: name from VIEWS; cull: "cull" = one-sided like a normal game shader (back faces magenta = would be invisible)
import struct, sys, numpy as np
from PIL import Image
COL = {0:(0.80,0.06,0.08),1:(0.93,0.93,0.93),2:(0.16,0.16,0.19),3:(0.6,0.6,0.66),4:(0.07,0.07,0.07),5:(0.38,0.38,0.42),6:(1,0.3,0.2),7:(0.2,0.5,1.0),8:(0.9,0.8,0.2),9:(0.3,0.8,0.4)}
VIEWS = {
 'front34': ((2.6,1.5,4.2),(0,0.45,0.4),38), 'rear34': ((-2.6,1.6,-4.2),(0,0.45,0.0),38), 'side': ((5.5,0.9,0.4),(0,0.45,0.4),30),
 'top': ((0.01,7.5,0.4),(0,0,0.4),36), 'cockpit': ((0,0.775,0.22),(0,0.68,30),70), 'halo': ((0,1.02,0.12),(0,0.55,30),66),
 'chase': ((0,2.0,-6.2),(0,0.7,6),62), 'wheelFL': ((2.2,0.5,3.0),(0.75,0.33,1.72),22), 'low': ((1.4,0.25,4.6),(0,0.4,1.0),40),
}
def load(path, colours=False):
    d=open(path,'rb').read(); o=4; n,=struct.unpack_from('<i',d,o); o+=4; parts=[]; v2=d[:4]==b'F2M2'
    for _ in range(n):
        l,=struct.unpack_from('<i',d,o);o+=4;nm=d[o:o+l].decode();o+=l
        mid,px,py,pz=struct.unpack_from('<i3f',d,o);o+=16
        nv,ni=struct.unpack_from('<ii',d,o);o+=8
        P=np.frombuffer(d,np.float32,nv*3,o).reshape(-1,3)+np.array([px,py,pz],np.float32);o+=nv*12; o+=nv*12
        C=None
        if v2: C=np.frombuffer(d,np.uint8,nv*4,o).reshape(-1,4)/255.0; o+=nv*4
        F=np.frombuffer(d,np.int32,ni,o).reshape(-1,3);o+=ni*4
        parts.append((nm,mid,P,F,C) if colours else (nm,mid,P,F))
    return parts
def render(parts, view, cull=False, W=960, H=540, extra=None):
    pos,look,fov = VIEWS[view] if isinstance(view,str) else view
    pos=np.array(pos,float); look=np.array(look,float)
    fwd=look-pos; fwd/=np.linalg.norm(fwd); right=np.cross([0,1,0],fwd); right/=np.linalg.norm(right); up=np.cross(fwd,right)
    f=1/np.tan(np.radians(fov)/2); near=0.05
    img=np.zeros((H,W,3)); img[:]=(0.55,0.68,0.85); img[H//2:]=(0.30,0.30,0.32); zb=np.full((H,W),1e9)
    L=np.array([0.35,0.8,0.45]); L/=np.linalg.norm(L)
    for part in parts:
        nm,mid,P,F=part[:4]; C=part[4] if len(part)>4 else None; TC=part[5] if len(part)>5 else None
        rel=P-pos; cx=rel@right; cy=rel@up; cz=rel@fwd
        t=P[F]; fn=np.cross(t[:,1]-t[:,0],t[:,2]-t[:,0]); ln=np.linalg.norm(fn,axis=1); fn/=(ln[:,None]+1e-12)
        # Unity: a face is visible from the side cross(v1-v0, v2-v0) points to (same formula as RecalculateNormals)
        facing=np.einsum('ij,ij->i',fn,pos-t[:,0])>0
        sh=0.30+0.70*np.abs(fn@L)
        ok=(cz[F]>near).all(1)&(ln>1e-12)
        sx=(cx/np.maximum(cz,1e-6)*f*(H/W)+1)*W/2; sy=(1-cy/np.maximum(cz,1e-6)*f)*H/2
        base=np.array(COL.get(mid,(1,0,1)))
        for k in np.where(ok)[0]:
            i0,i1,i2=F[k]; x0,y0,x1,y1,x2,y2=sx[i0],sy[i0],sx[i1],sy[i1],sx[i2],sy[i2]
            mnx=int(max(0,np.floor(min(x0,x1,x2)))); mxx=int(min(W-1,np.ceil(max(x0,x1,x2))))
            mny=int(max(0,np.floor(min(y0,y1,y2)))); mxy=int(min(H-1,np.ceil(max(y0,y1,y2))))
            if mnx>mxx or mny>mxy: continue
            den=(y1-y2)*(x0-x2)+(x2-x1)*(y0-y2)
            if abs(den)<1e-9: continue
            xs=np.arange(mnx,mxx+1)+0.5; ys=np.arange(mny,mxy+1)+0.5; X,Y=np.meshgrid(xs,ys)
            a=((y1-y2)*(X-x2)+(x2-x1)*(Y-y2))/den; b=((y2-y0)*(X-x2)+(x0-x2)*(Y-y2))/den; c=1-a-b
            m=(a>=0)&(b>=0)&(c>=0)
            if not m.any(): continue
            z=1/(a/cz[i0]+b/cz[i1]+c/cz[i2])
            sub=zb[mny:mxy+1,mnx:mxx+1]; m&=z<sub
            if not m.any(): continue
            if cull=='skip' and not facing[k]: continue
            if cull and cull!='skip' and not facing[k]: colr=np.array((1,0,1))
            elif TC is not None: colr=TC[k]*sh[k]   # game-like parts (game_parts): per-triangle colour, AO already in
            else:
                colr=base*sh[k]
                if C is not None:   # baked AO (front or back face), brake-disc glow (extra = glow colour)
                    ao=C[F[k],0 if facing[k] else 2].mean(); colr=colr*(0.25+0.75*ao)
                    if extra is not None and C[F[k],1].mean()>0.5: colr=np.array(extra)
            sub[m]=z[m]; img[mny:mxy+1,mnx:mxx+1][m]=colr
    return (np.clip(img,0,1)*255).astype(np.uint8)
# ---- game-like build of a .bytes file, the way CarModel.cs puts it on screen (rest pose, default red/white paint):
#  before = the old per-part rival (one object per part, helmet sphere, tyre stripes on car_lod only)
#  after  = CarModel.CreateMergedRival: parts merged, part ID per vertex (alpha), AO strength baked in, the
#           wheels / steering-only meshes of car_lod or everything in one mesh for car_far
# Both are drawn one-sided (back faces skipped) with the two-sided parts twinned, as in the game.
STRIPE=(0.85,0.08,0.08); HELMET=(1,0.82,0.18)
ID_COL={0:COL[0],1:COL[1],2:COL[2],3:COL[3],4:COL[4],5:COL[4],6:COL[5],7:COL[5],8:COL[6],9:STRIPE,10:HELMET,11:COL[5]}
def load_raw(path):
    d=open(path,'rb').read(); o=4; n,=struct.unpack_from('<i',d,o); o+=4; out=[]
    for _ in range(n):
        l,=struct.unpack_from('<i',d,o);o+=4;nm=d[o:o+l].decode();o+=l
        mid,px,py,pz=struct.unpack_from('<i3f',d,o);o+=16; nv,ni=struct.unpack_from('<ii',d,o);o+=8
        P=np.frombuffer(d,np.float32,nv*3,o).reshape(-1,3).astype(float);o+=nv*12
        N=np.frombuffer(d,np.float32,nv*3,o).reshape(-1,3).astype(float);o+=nv*12
        C=np.frombuffer(d,np.uint8,nv*4,o).reshape(-1,4).astype(float)/255;o+=nv*4
        F=np.frombuffer(d,np.int32,ni,o).reshape(-1,3).copy();o+=ni*4
        out.append((nm,mid,np.array([px,py,pz]),P,F,C))
    return out
def twin(P,F,C):
    C2=C.copy(); C2[:,0]=C[:,2]
    return np.concatenate([P,P]),np.concatenate([F,F[:,::-1]+len(P)]),np.concatenate([C,C2])
def rotx(a):
    a=np.radians(a); return np.array([[1,0,0],[0,np.cos(a),-np.sin(a)],[0,np.sin(a),np.cos(a)]])
def sphere_mesh(c,sc,lat=16,lon=24):
    v=[];f=[]
    for i in range(lat+1):
        th=np.pi*i/lat
        for j in range(lon+1): ph=2*np.pi*j/lon; v.append([0.5*np.sin(th)*np.cos(ph),0.5*np.cos(th),0.5*np.sin(th)*np.sin(ph)])
    for i in range(lat):
        for j in range(lon): a=i*(lon+1)+j; b=a+lon+1; f+= [[a,a+1,b],[a+1,b+1,b]]
    v=np.array(v)*np.array(sc)+np.array(c); f=np.array(f)
    cen=v[f].mean(1); nn=np.cross(v[f[:,1]]-v[f[:,0]],v[f[:,2]]-v[f[:,0]])
    flip=np.einsum('ij,ij->i',nn,cen-np.array(c))<0; f[flip]=f[flip][:,::-1]   # outward
    return v,f
def stripe_mesh(P):
    rr=np.hypot(P[:,1],P[:,2]); R=rr.max(); r0,r1=0.79*R,0.86*R; sel=(rr>r0)&(rr<r1)
    if not sel.any(): return None
    xo,xi=P[sel,0].max(),P[sel,0].min(); v=[];f=[]; S=48
    for side,x in ((0,xo+0.003),(1,xi-0.003)):
        b=len(v)
        for k in range(S+1):
            a=2*np.pi*k/S; v+= [[x,np.cos(a)*r0,np.sin(a)*r0],[x,np.cos(a)*r1,np.sin(a)*r1]]
        for k in range(S):
            q=b+2*k; f+= ([[q,q+1,q+2],[q+2,q+1,q+3]] if side==0 else [[q,q+2,q+1],[q+2,q+3,q+1]])
    return np.array(v),np.array(f)
AOK={0:0.6,1:0.55,2:0.75,3:0.7,6:0.0}
def game_parts(path, merged):
    far='far' in path; parts=[]; acc={}
    def put(key,P,F,colour,ao,idv):
        a=acc.setdefault(key,[[],[],[],[],0]); a[0].append(P); a[1].append(F+a[4]); a[2].append(np.broadcast_to(colour,(len(F),3))*ao[F].mean(1)[:,None]); a[3].append(np.full(len(P),idv)); a[4]+=len(P)
    for nm,mid,piv,P,F,C in load_raw(path):
        used=np.unique(F)
        if len(used)*2<=len(P):   # Compact
            mp=-np.ones(len(P),int); mp[used]=np.arange(len(used)); P,C,F=P[used],C[used],mp[F]
        if mid in (1,2,3,6) or nm.startswith('steer_'): P,F,C=twin(P,F,C)
        if nm.startswith('wheel_'):
            key=nm[6:8]; front=key[0]=='F'; tyre=nm.endswith('_tyre'); stat=nm.endswith('_hubstatic')
            idv=(4 if front else 5) if tyre else (6 if front else 7); k=0.45 if tyre else 0.7
            col=ID_COL[idv]; slot='body' if far or (stat and not front) else ('stat'+key if stat else 'spin'+key)
            sm=stripe_mesh(P) if tyre and not far else None
            Pw=P+piv
        elif nm.startswith('steer_'):
            Pw=P@rotx(float(nm[6:])).T+piv; idv=11; k=0.5; col=COL[5]; slot='body'; sm=None
        else:
            Pw=P+piv; idv={0:0,1:1,3:3,6:8}.get(mid,2); k=AOK.get(mid,0.75); col=COL[mid]; slot='body'; sm=None
        ao=1-k*(1-C[:,0])
        if merged: put(slot,Pw,F,ID_COL[idv],ao,idv)
        else: parts.append((nm,mid,Pw,F,None,np.broadcast_to(col,(len(F),3))*ao[F].mean(1)[:,None]))
        if sm is not None:
            sv,sf=sm
            if merged: put(slot,sv+piv,sf,ID_COL[9],np.ones(len(sv)),9)
            else: parts.append(('stripe',7,sv+piv,sf,None,np.broadcast_to(STRIPE,(len(sf),3))))
    hv,hf=sphere_mesh((0,0.72,0.05),(0.26,0.28,0.3))
    if merged: put('body',hv,hf,HELMET,np.ones(len(hv)),10)
    else: parts.append(('helmet',0,hv,hf,None,np.broadcast_to(HELMET,(len(hf),3))))
    if merged:
        for key,(Ps,Fs,TCs,IDs,nv) in acc.items(): parts.append((key,0,np.concatenate(Ps),np.concatenate(Fs),None,np.concatenate(TCs)))
    return parts

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='game':
    # usage: render.py game file.bytes before|after out.png views
    parts=game_parts(sys.argv[2], sys.argv[3]=='after')
    print(sys.argv[3], 'renderers:', len(parts), [p[0] for p in parts] if len(parts)<12 else '')
    ims=[render(parts,v,'skip') for v in sys.argv[5].split(',')]
    Image.fromarray(np.concatenate(ims,0) if len(ims)<3 else np.concatenate([np.concatenate(ims[i:i+2],1) for i in range(0,len(ims)-len(ims)%2,2)],0)).save(sys.argv[4])
elif __name__=='__main__':
    parts=load(sys.argv[1], colours='ao' in sys.argv); cull=len(sys.argv)>4 and sys.argv[4]=='cull'
    views=sys.argv[3].split(',')
    ims=[render(parts,v,cull) for v in views]
    Image.fromarray(np.concatenate(ims,0) if len(ims)<3 else np.concatenate([np.concatenate(ims[i:i+2],1) for i in range(0,len(ims)-len(ims)%2,2)],0)).save(sys.argv[2])
