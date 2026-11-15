# Software renderer for car .bytes files (z-buffer), to see the model the way the game camera does.
# usage: render.py file.bytes out.png view [cull] [ao]   (ao: shade with the baked vertex AO of F2M2 files)
#        render.py game file.bytes before|after out.png views   (the rival car as CarModel builds it: per part / merged)
#        render.py shade file.bytes old|new out.png views [W H]     (the player's car lit like CarPaint.shader: sky probe,
#                 sun, Fresnel clear coat, contact shadow on the road; old = before the RR3-style pass, new = now)
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


# ---- shaded preview: CarPaint.shader (full pass) ported to numpy, per pixel, for the player's car as CarModel builds it.
# The sky probe is a stand-in (procedural gradient + sun disc), so judge looks, not exact values. Linear light, then
# exposure + Grade.cginc's tone curve, then sRGB. Geometry: 'new' applies CarModel.FitTyre / WheelY / LowerBody.
ROOT=3.40/3.135
def lin(c): c=np.array(c,float); return np.where(c<=0.04045,c/12.92,((c+0.055)/1.055)**2.4)
def to_srgb(c): c=np.clip(c,0,1); return np.where(c<=0.0031308,c*12.92,1.055*c**(1/2.4)-0.055)
SUN_L=np.array([0.45,0.62,0.64]); SUN_L/=np.linalg.norm(SUN_L); SUN_C=lin([1,0.95,0.86])*1.25
AMB_SKY=lin([0.62,0.72,0.85]); AMB_EQ=lin([0.55,0.6,0.6]); AMB_GR=lin([0.3,0.37,0.24])
GROUND_REFL=lin([0.15,0.15,0.16])   # what the shader gets from SetGlobalColor (linear conversion)
ROAD=np.array([0.11,0.11,0.115])    # baked road radiance stand-in (linear, after the bake's sun + sky)
def sh(n):   # trilight ambient as Unity's SH roughly gives it
    y=n[...,1:2]; return np.where(y>0, AMB_EQ+(AMB_SKY-AMB_EQ)*y, AMB_EQ+(AMB_GR-AMB_EQ)*(-y))
def sky(R, mip):
    t=np.clip(R[...,1:2],0,1)
    c=np.array([0.78,0.84,0.93])+(np.array([0.17,0.3,0.62])-np.array([0.78,0.84,0.93]))*t**0.45
    sd=(R@SUN_L)[...,None]; c=c+np.clip((sd-0.9990)/0.0006,0,1)*(1-mip[...,None]/7)
    c=c+0.25*np.clip((sd-0.97)/0.03,0,1)**2
    avg=np.array([0.45,0.55,0.74]); k=np.clip(mip/7,0,1)[...,None]
    return np.clip(c*(1-k)+avg*k,0,1)
def smooth(a,b,x): t=np.clip((x-a)/(b-a),0,1); return t*t*(3-2*t)
def tone(x):
    x=np.maximum(x,0); h=np.maximum(x-0.8,0); return np.minimum(x,0.8+h/(1+h*5))

# materials of the player's car per part id (as CarModel.CreateFromAsset sets them): base (linear, what the shader
# gets), coat, gloss, metal, AO strength
def mats(new):
    if new: L=lambda *c: np.array(c,float)
    else: L=lambda *c: lin(c)
    red=lin([0.78,0.05,0.07]); white=lin([0.93,0.93,0.94])
    if new: return {0:(red,1,.95,.06,.6),1:(white,1,.93,.03,.55),2:(L(.022,.024,.028),.35,.88,.05,.75),3:(L(.45,.46,.5),.3,.85,.95,.7),
                    4:(L(.015,.015,.016),.1,.35,0,.45),6:(L(.22,.23,.245),.35,.82,.9,.7),8:(lin([1,.15,.12]),.5,.8,0,0),
                    9:(lin([.85,.08,.08]),.25,.5,0,0),10:(lin([1,.82,.18]),1,.95,.1,0),11:(L(.03,.03,.033),.5,.7,.1,.5)}
    return {0:(red,1,.92,.15,.6),1:(white,1,.9,.05,.55),2:(L(.06,.065,.075),.55,.7,.1,.75),3:(L(.55,.57,.6),.3,.8,.9,.7),
            4:(L(.085,.085,.09),.22,.38,0,.45),6:(L(.2,.21,.23),.3,.75,.85,.7),8:(lin([1,.15,.12]),.5,.8,0,0),
            9:(lin([.85,.08,.08]),.25,.5,0,0),10:(lin([1,.82,.18]),1,.93,.1,0),11:(L(.2,.21,.23),.3,.75,.85,.5)}

def fit_tyre(P,F,front):
    u=np.unique(F); r=np.hypot(P[u,1],P[u,2]); R=r.max(); rb=r.min(); Rt=(0.705 if front else 0.710)*0.5/ROOT
    if R>=Rt or rb>=R-0.02: return P
    rr=np.hypot(P[:,1],P[:,2]); s=np.where((rr>=rb)&(rr>1e-5),(rb+(rr-rb)*(Rt-rb)/(R-rb))/np.maximum(rr,1e-5),1.0)
    P=P.copy(); P[:,1]*=s; P[:,2]*=s; return P
def lower_body(P,N):
    D,Y0,Y1=0.045,0.30,0.52; P=P.copy(); N=N.copy()
    fy=np.clip((Y1-P[:,1])/(Y1-Y0),0,1); xs=np.clip((np.abs(P[:,0])-0.20)/0.36,0,1); keep=np.zeros(len(P))
    for az,ay in ((1.724,0.32),(-1.411,0.32)):
        r=np.hypot(P[:,2]-az,P[:,1]-ay); keep=np.maximum(keep,np.clip((0.31-r)/0.06,0,1)*xs)
    w=1-keep; band=(P[:,1]>Y0)&(fy>0)
    N[band,1]/=1+D/(Y1-Y0)*w[band]; N/=np.linalg.norm(N,axis=1)[:,None]+1e-12
    P[:,1]-=D*fy*w; return P,N

def shade_parts(path,new):
    out=load_raw(path)
    # normals: read separately (load_raw skips them)
    d=open(path,'rb').read(); o=4; n,=struct.unpack_from('<i',d,o); o+=4; normals=[]
    for _ in range(n):
        l,=struct.unpack_from('<i',d,o);o+=4+l; o+=16; nv,ni=struct.unpack_from('<ii',d,o);o+=8; o+=nv*12
        normals.append(np.frombuffer(d,np.float32,nv*3,o).reshape(-1,3).astype(float)); o+=nv*12+nv*4+ni*4
    res=[]
    for (nm,mid,piv,P,F,C),N in zip(out,normals):
        piv=piv.copy()
        if nm.startswith('wheel_'):
            front=nm[6]=='F'
            if new: piv[1]=(0.705 if front else 0.710)*0.5/ROOT-0.004
            if nm.endswith('_tyre'):
                if new: P=fit_tyre(P,F,front)
                pid=4
            else: pid=6
        elif nm.startswith('steer_'): P=P@rotx(float(nm[6:])).T; N=N@rotx(float(nm[6:])).T; pid=11; front=False
        else:
            pid={0:0,1:1,3:3,6:8}.get(mid,2); front=False
            if new: P,N=lower_body(P,N)
        used=np.unique(F)
        if len(used)*2<=len(P):
            mp=-np.ones(len(P),int); mp[used]=np.arange(len(used)); P,N,C,F=P[used],N[used],C[used],mp[F]
        if mid in (1,2,3,6) or nm.startswith('steer_'):
            C2=C.copy(); C2[:,0]=C[:,2]
            P=np.concatenate([P,P]); N=np.concatenate([N,-N]); F=np.concatenate([F,F[:,::-1]+len(P)//2]); C=np.concatenate([C,C2])
        res.append((P+piv,N,F,C,pid,front))
        if pid==4:
            sm=stripe_mesh(P)
            if sm is not None:
                sv,sf=sm; sn=np.zeros_like(sv); sn[:,0]=np.sign(sv[:,0])
                res.append((sv+piv,sn,sf,np.ones((len(sv),4)),9,front))
    hv,hf=sphere_mesh((0,0.72,0.05),(0.26,0.28,0.3)); hn=(hv-np.array([0,0.72,0.05]))/np.array([0.26,0.28,0.3])**2
    hn/=np.linalg.norm(hn,axis=1)[:,None]; res.append((hv,hn,hf,np.ones((len(hv),4)),10,False))
    return res

def contact_alpha(x,z,new):
    u=x/2.4+0.5; v=(z-0.3)/5.6+0.5; inside=(u>=0)&(u<=1)&(v>=0)&(v<=1)
    dx=(u-0.5)*2; dy=(v-0.5)*2; dd=np.sqrt(dx*dx*1.1+dy*dy*0.9)
    soft=np.clip(1-dd,0,1); soft=soft*soft*(3-2*soft)*(0.6 if new else 0.75)
    hw=np.where(z>1.1,0.55+(0.12-0.55)*np.clip((z-1.1)/1.5,0,1),np.where(z<-1.5,0.55+(0.3-0.55)*np.clip((-1.5-z)/0.6,0,1),0.62))
    ex=np.abs(x)-hw; ez=np.maximum(z-2.7,-2.15-z)
    out1=np.sqrt(np.maximum(ex,0)**2+np.maximum(ez,0)**2)+np.minimum(np.maximum(ex,ez),0)
    patch=np.zeros_like(x)
    if new:
        core=np.clip(1-(out1+0.08)/0.2,0,1); core=core*(2-core)*0.88
        skirt=np.clip(1-out1/0.35,0,1); skirt=skirt*skirt*0.4
        noseK=1+(0.35-1)*np.clip((z-1.2)/0.8,0,1); core*=noseK; skirt*=noseK
        for wx,wz,hx in ((0.71,1.72,0.13),(-0.71,1.72,0.13),(0.68,-1.41,0.16),(-0.68,-1.41,0.16)):
            ax=np.abs(x-wx)-hx; az=np.abs(z-wz); q=np.clip(1-np.maximum(ax/0.04,(az-0.06)/0.05),0,1)
            h=np.clip(1-np.sqrt((np.maximum(ax,0)/0.1)**2+(az/0.42)**2),0,1); patch=np.maximum(patch,np.maximum(q*0.97,h*h*0.7))
        a=1-(1-soft)*(1-core)*(1-skirt)*(1-patch); a*=250/255
    else:
        core=np.clip(1-(out1+0.12)/0.22,0,1)*0.6
        for wx,wz,hx in ((0.71,1.72,0.14),(-0.71,1.72,0.14),(0.68,-1.41,0.18),(-0.68,-1.41,0.18)):
            q=np.clip(1-np.maximum((np.abs(x-wx)-hx)/0.08,(np.abs(z-wz)-0.16)/0.1),0,1); patch=np.maximum(patch,q*q*0.8)
        a=(1-(1-soft)*(1-core)*(1-patch))*235/255
    return np.where(inside,a,0)

def render_shaded(parts,view,new,W=960,H=540):
    pos,look,fov=VIEWS[view] if isinstance(view,str) else view
    pos=np.array(pos,float); look=np.array(look,float)
    fwd=look-pos; fwd/=np.linalg.norm(fwd); right=np.cross([0,1,0],fwd); right/=np.linalg.norm(right); up=np.cross(fwd,right)
    f=1/np.tan(np.radians(fov)/2); near=0.05
    zb=np.full((H,W),1e9); tid=np.full((H,W),-1); ba=np.zeros((H,W)); bb=np.zeros((H,W))
    allP=[];allN=[];allC=[];allI=[];allFr=[];allF=[];off=0
    for P,N,F,C,pid,front in parts:
        allP.append(P);allN.append(N);allC.append(C);allI.append(np.full(len(P),pid));allFr.append(np.full(len(P),front));allF.append(F+off);off+=len(P)
    P=np.concatenate(allP);N=np.concatenate(allN);C=np.concatenate(allC);PI=np.concatenate(allI);FR=np.concatenate(allFr);F=np.concatenate(allF)
    rel=P-pos; cx=rel@right; cy=rel@up; cz=rel@fwd
    sx=(cx/np.maximum(cz,1e-6)*f*(H/W)+1)*W/2; sy=(1-cy/np.maximum(cz,1e-6)*f)*H/2
    t=P[F]; fn=np.cross(t[:,1]-t[:,0],t[:,2]-t[:,0]); facing=np.einsum('ij,ij->i',fn,pos-t[:,0])>0
    ok=(cz[F]>near).all(1)&facing
    for k in np.where(ok)[0]:
        i0,i1,i2=F[k]; x0,y0,x1,y1,x2,y2=sx[i0],sy[i0],sx[i1],sy[i1],sx[i2],sy[i2]
        mnx=int(max(0,np.floor(min(x0,x1,x2)))); mxx=int(min(W-1,np.ceil(max(x0,x1,x2))))
        mny=int(max(0,np.floor(min(y0,y1,y2)))); mxy=int(min(H-1,np.ceil(max(y0,y1,y2))))
        if mnx>mxx or mny>mxy: continue
        den=(y1-y2)*(x0-x2)+(x2-x1)*(y0-y2)
        if abs(den)<1e-9: continue
        X,Y=np.meshgrid(np.arange(mnx,mxx+1)+0.5,np.arange(mny,mxy+1)+0.5)
        a=((y1-y2)*(X-x2)+(x2-x1)*(Y-y2))/den; b=((y2-y0)*(X-x2)+(x0-x2)*(Y-y2))/den; c=1-a-b
        m=(a>=0)&(b>=0)&(c>=0)
        if not m.any(): continue
        # perspective-correct barycentrics
        wa=a/cz[i0]; wb=b/cz[i1]; wc=c/cz[i2]; ws=wa+wb+wc; z=1/ws
        sub=zb[mny:mxy+1,mnx:mxx+1]; m&=z<sub
        if not m.any(): continue
        sub[m]=z[m]; tid[mny:mxy+1,mnx:mxx+1][m]=k; ba[mny:mxy+1,mnx:mxx+1][m]=(wa/ws)[m]; bb[mny:mxy+1,mnx:mxx+1][m]=(wb/ws)[m]
    # background: sky above, road with the contact shadow below
    ys,xs=np.mgrid[0:H,0:W]+0.5
    dirs=fwd[None,None,:]+right[None,None,:]*((xs/W*2-1)/f/(H/W))[...,None]+up[None,None,:]*((1-ys/H*2)/f)[...,None]
    dirs/=np.linalg.norm(dirs,axis=2)[...,None]
    img=sky(dirs,np.zeros((H,W)))
    tg=-pos[1]/np.minimum(dirs[...,1],-1e-6); hit=dirs[...,1]<-1e-4
    gx=pos[0]+dirs[...,0]*tg; gz=pos[2]+dirs[...,2]*tg
    a=contact_alpha(gx,gz,new); road=ROAD*(1-a[...,None])
    img=np.where(hit[...,None],road,img)
    # shade the car pixels
    msk=tid>=0; T=F[tid[msk]]; A=ba[msk][:,None]; B=bb[msk][:,None]; Cc=1-A-B
    Nn=N[T[:,0]]*A+N[T[:,1]]*B+N[T[:,2]]*Cc; Nn/=np.linalg.norm(Nn,axis=1)[:,None]+1e-12
    Pp=P[T[:,0]]*A+P[T[:,1]]*B+P[T[:,2]]*Cc
    ao_v=C[T[:,0],0]*A[:,0]+C[T[:,1],0]*B[:,0]+C[T[:,2],0]*Cc[:,0]; gm=(C[T[:,0],1]*A[:,0]+C[T[:,1],1]*B[:,0]+C[T[:,2],1]*Cc[:,0])
    pid=PI[T[:,0]]; MT=mats(new)
    base=np.zeros((len(pid),3)); coatK=np.zeros(len(pid)); gloss=np.zeros(len(pid)); metal=np.zeros(len(pid)); aoK=np.zeros(len(pid))
    for k,(bc,ck,gl,me,ak) in MT.items():
        s_=pid==k; base[s_]=bc; coatK[s_]=ck; gloss[s_]=gl; metal[s_]=me; aoK[s_]=ak
    ao=1-aoK*(1-ao_v)
    V=pos-Pp; V/=np.linalg.norm(V,axis=1)[:,None]
    # per-part details: wing undersides carbon, tyre tread sheen, discs, helmet visor (approx)
    under=(pid==1)*smooth(0.25,0.6,-Nn[:,1]); cb=np.array([.022,.024,.028]) if new else np.array([.06,.065,.075])
    base=base*(1-under[:,None])+cb*under[:,None]; coatK=coatK+(( .35 if new else .6)-coatK)*under
    tread=(pid==4)*(1-smooth(0.35,0.8,np.abs(Nn[:,0]))); coatK=coatK*np.where(pid==4,1+0.7*tread,1)
    disc=(pid==6)*np.clip(gm,0,1); base=base*(1-disc[:,None])+(np.array([.06,.06,.065]) if new else np.array([.1,.1,.105]))*disc[:,None]
    L=SUN_L; nl=Nn@L; ndl=np.clip(nl,0,1)
    R=2*(Nn*V).sum(1)[:,None]*Nn-V
    if new:
        wrap=np.clip((nl+0.2)/1.2,0,1); mip=(1-gloss)*5
        diff=base*(wrap[:,None]*SUN_C+sh(Nn))*(1-metal*0.85)[:,None]*ao[:,None]
        env=np.maximum(sky(R,mip),sh(R)*0.9)
        lum=(sh(R)*0.9)@np.array([0.2126,0.7152,0.0722]); gr=np.maximum(GROUND_REFL,lum[:,None])
        lo=-0.35+0.30*gloss; env=env+(gr-env)*(1-smooth(lo,0.015,R[:,1]))[:,None]
        x=1-np.clip((Nn*V).sum(1),0,1); fz=0.05+0.95*x**4
        sp=2**(3+6*gloss); sm=np.minimum(sp*0.015,6)
    else:
        diff=base*(ndl[:,None]*SUN_C+sh(Nn))*(1-metal*0.6)[:,None]*ao[:,None]
        mip=(1-gloss)*6; env=np.maximum(sky(R,mip),sh(R)*0.9)
        env=env+(GROUND_REFL-env)*(1-smooth(-0.12,0.02,R[:,1]))[:,None]
        x=1-np.clip((Nn*V).sum(1),0,1); fz=0.04+0.96*x**5
        sp=16+(512-16)*gloss; sm=0.5+5.5*gloss
    env=env*(0.3+0.7*ao)[:,None]
    coat=np.clip(fz*coatK,0,1)
    H_=L[None,:]+V; H_/=np.linalg.norm(H_,axis=1)[:,None]; nh=np.clip((Nn*H_).sum(1),0,1)
    spec=nh**sp*ndl*sm+nh**2048*ndl*14*np.clip((gloss-0.8)*6,0,1); spec*=ao
    if new: spec*=np.where(pid==4,2.0,1.0)
    col=diff*(1-coat)[:,None]+env*(coat[:,None]+metal[:,None]*base)+spec[:,None]*SUN_C*(coatK+metal)[:,None]
    col=np.where((pid==8)[:,None],np.array([0.25,0.02,0.02])+col,col)
    img[msk]=col
    out=to_srgb(tone(img*1.06))
    return (out*255).astype(np.uint8)

if __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='game':
    # usage: render.py game file.bytes before|after out.png views
    parts=game_parts(sys.argv[2], sys.argv[3]=='after')
    print(sys.argv[3], 'renderers:', len(parts), [p[0] for p in parts] if len(parts)<12 else '')
    ims=[render(parts,v,'skip') for v in sys.argv[5].split(',')]
    Image.fromarray(np.concatenate(ims,0) if len(ims)<3 else np.concatenate([np.concatenate(ims[i:i+2],1) for i in range(0,len(ims)-len(ims)%2,2)],0)).save(sys.argv[4])
elif __name__=='__main__' and len(sys.argv)>1 and sys.argv[1]=='shade':
    # usage: render.py shade file.bytes old|new out.png views [W H]
    W,H=(int(sys.argv[6]),int(sys.argv[7])) if len(sys.argv)>7 else (960,540)
    parts=shade_parts(sys.argv[2], sys.argv[3]=='new')
    ims=[render_shaded(parts,v,sys.argv[3]=='new',W,H) for v in sys.argv[5].split(',')]
    Image.fromarray(np.concatenate(ims,0) if len(ims)<3 else np.concatenate([np.concatenate(ims[i:i+2],1) for i in range(0,len(ims)-len(ims)%2,2)],0)).save(sys.argv[4])
elif __name__=='__main__':
    parts=load(sys.argv[1], colours='ao' in sys.argv); cull=len(sys.argv)>4 and sys.argv[4]=='cull'
    views=sys.argv[3].split(',')
    ims=[render(parts,v,cull) for v in views]
    Image.fromarray(np.concatenate(ims,0) if len(ims)<3 else np.concatenate([np.concatenate(ims[i:i+2],1) for i in range(0,len(ims)-len(ims)%2,2)],0)).save(sys.argv[2])
