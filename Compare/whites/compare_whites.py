"""End-to-end: RAWR real Whites render vs LR. Baseline + operator-delta RMS + dEV(per-pixel ev) curves.
Whites is global, so the transfer is binned by the pixel's own EV (not a regional base)."""
import os, glob, sys
import numpy as np
from PIL import Image
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))
import rawr_transform as rt
REND=os.path.join(os.path.dirname(os.path.dirname(__file__)),"renders_whites")
LR=r"C:\Users\Torsten\Desktop\Datasets\Whites"; DS=8
EDGES=np.arange(-3,4.01,0.25); CENT=0.5*(EDGES[:-1]+EDGES[1:]); NB=CENT.size
def code(ev): return rt.display_transform(0.18*2**ev)*255
def suffix(s): return "whites000" if s==0 else f"whites{'+' if s>0 else '-'}{abs(s):03d}"
def load(path,png):
    im=Image.open(path)
    if not png: im.draft("RGB",(im.width//DS,im.height//DS))
    a=np.asarray(im.convert("RGB"),np.uint8)
    if png:
        h,w=a.shape[:2];h-=h%DS;w-=w%DS;a=a[:h,:w].reshape(h//DS,DS,w//DS,DS,3).mean(axis=(1,3))
    ev,_=rt.code_to_ev_luma(a);clip=np.any(a<=2,axis=-1)|np.any(a>=253,axis=-1);return ev.astype(np.float32),clip
def crop(*arr):
    h=min(a.shape[0] for a in arr);w=min(a.shape[1] for a in arr);return [a[:h,:w] for a in arr]
stems=sorted({os.path.basename(p).split("_whites000")[0] for p in glob.glob(os.path.join(REND,"*_whites000.png"))})
sliders=[-100,-50,50,100]
print(f"{len(stems)} scenes")
accR={s:[np.zeros(NB),np.zeros(NB)] for s in sliders};accL={s:[np.zeros(NB),np.zeros(NB)] for s in sliders}
opsse={s:0. for s in sliders};opN={s:0 for s in sliders};base=[]
for stem in stems:
    try:
        rn,rcn=load(os.path.join(REND,f"{stem}_whites000.png"),True);ln,lcn=load(os.path.join(LR,f"{stem}_whites000.jpg"),False)
    except FileNotFoundError: continue
    ir=np.clip(np.digitize(rn,EDGES)-1,0,NB-1);il=np.clip(np.digitize(ln,EDGES)-1,0,NB-1)
    a=crop(rn,ln,rcn,lcn);v0=(~a[2])&(~a[3]);base.append(np.sqrt(np.mean((code(a[0])-code(a[1]))[v0]**2)))
    for s in sliders:
        p=os.path.join(REND,f"{stem}_{suffix(s)}.png")
        if not os.path.exists(p): continue
        rs,rcs=load(p,True);ls,lcs=load(os.path.join(LR,f"{stem}_{suffix(s)}.jpg"),False)
        vr=(~rcn)&(~rcs);accR[s][0]+=np.bincount(ir[vr],(rs-rn)[vr],minlength=NB)[:NB];accR[s][1]+=np.bincount(ir[vr],minlength=NB)[:NB]
        vl=(~lcn)&(~lcs);accL[s][0]+=np.bincount(il[vl],(ls-ln)[vl],minlength=NB)[:NB];accL[s][1]+=np.bincount(il[vl],minlength=NB)[:NB]
        c=crop(rs,ls,rn,ln,rcs,lcs,rcn,lcn);vv=(~c[4])&(~c[5])&(~c[6])&(~c[7])
        e=((code(c[0])-code(c[2]))-(code(c[1])-code(c[3])))[vv];opsse[s]+=float(np.sum(e*e));opN[s]+=int(vv.sum())
print(f"\nneutral baseline RMS: mean={np.mean(base):.1f} codes")
print("\naverage dEV(per-pixel ev): RAWR / LR")
print(f"{'code':>5} | "+" ".join(f"S{s:+04d}" for s in sliders))
for ev in [-1.5,-1,-0.5,0,0.5,1,1.5]:
    j=np.argmin(np.abs(CENT-ev));cells=[]
    for s in sliders:
        r=accR[s][0][j]/max(accR[s][1][j],1);l=accL[s][0][j]/max(accL[s][1][j],1);cells.append(f"{r:+.2f}/{l:+.2f}")
    print(f"{code(CENT[j]):5.0f} | "+"  ".join(cells))
print("\noperator-delta RMS (baseline-cancelled, codes):")
for s in sliders: print(f"  S={s:+4d}: {np.sqrt(opsse[s]/max(opN[s],1)):.2f}")
