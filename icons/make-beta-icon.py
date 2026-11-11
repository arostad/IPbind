"""Generates app-beta.ico (IPbind Beta) from app.ico: the dark background is
recolored to orange per frame; every glyph, edge and alpha value is unchanged.
Run from anywhere:  python3 icons/make-beta-icon.py   (needs Pillow + numpy)"""
import os
ROOT=os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from PIL import Image
ico=Image.open(os.path.join(ROOT,'app.ico'))
B=np.array([34,49,66],float); O=np.array([211,84,0],float)
FG=[np.array([94,199,192],float), np.array([255,255,255],float)]
frames=[]
for size in sorted(ico.ico.sizes()):
    im=np.array(ico.ico.getimage(size).convert('RGBA')).astype(float)
    rgb=im[...,:3]
    best_t=None; best_err=None
    for F in FG:
        d=F-B; t=((rgb-B)@d)/(d@d)          # 0 = bg, 1 = fg along the B->F line
        t=np.clip(t,0,1)
        err=np.linalg.norm(rgb-(B+t[...,None]*d),axis=-1)
        if best_t is None: best_t,best_err=t,err
        else:
            m=err<best_err; best_t=np.where(m,t,best_t); best_err=np.where(m,err,best_err)
    bgfrac=1-best_t
    # pixels lying on the teal<->white edge contain no background
    T=FG[0]; W=FG[1]; d=W-T; u=np.clip(((rgb-T)@d)/(d@d),0,1)
    e2=np.linalg.norm(rgb-(T+u[...,None]*d),axis=-1)
    m=e2<best_err; bgfrac=np.where(m,0,bgfrac); best_err=np.where(m,e2,best_err)
    out=rgb+bgfrac[...,None]*(O-B)
    # near-transparent rim pixels carry junk RGB; give them the new background
    out=np.where((im[...,3]<16)[...,None], O, out)
    im[...,:3]=np.clip(np.round(out),0,255)
    frames.append(Image.fromarray(im.astype(np.uint8),'RGBA'))
    print(size, 'max fit err (opaque-ish)', round(best_err[im[...,3]>=16].max(),1))
frames[-1].save(os.path.join(ROOT,'app-beta.ico'),format='ICO',sizes=[f.size for f in frames],append_images=frames[:-1])
