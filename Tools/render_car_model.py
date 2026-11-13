# Software renderer for car .bytes files (z-buffer), to see the model the way the game camera does.
# usage: render.py file.bytes out.png view [cull] [ao]   (ao: shade with the baked vertex AO of F2M2 files)
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
        nm,mid,P,F=part[:4]; C=part[4] if len(part)>4 else None
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
            if cull and not facing[k]: colr=np.array((1,0,1))
            else:
                colr=base*sh[k]
                if C is not None:   # baked AO (front or back face), brake-disc glow (extra = glow colour)
                    ao=C[F[k],0 if facing[k] else 2].mean(); colr=colr*(0.25+0.75*ao)
                    if extra is not None and C[F[k],1].mean()>0.5: colr=np.array(extra)
            sub[m]=z[m]; img[mny:mxy+1,mnx:mxx+1][m]=colr
    return (np.clip(img,0,1)*255).astype(np.uint8)
if __name__=='__main__':
    parts=load(sys.argv[1], colours='ao' in sys.argv); cull=len(sys.argv)>4 and sys.argv[4]=='cull'
    views=sys.argv[3].split(',')
    ims=[render(parts,v,cull) for v in views]
    Image.fromarray(np.concatenate(ims,0) if len(ims)<3 else np.concatenate([np.concatenate(ims[i:i+2],1) for i in range(0,len(ims)-len(ims)%2,2)],0)).save(sys.argv[2])
