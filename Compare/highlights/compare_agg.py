"""
Aggregate end-to-end comparison over all rendered scenes.
  * average dEV(base) curve: RAWR real render vs LR (are they centered the same?)
  * operator-delta RMS (baseline-cancelling): RMS of [(RAWR_s - RAWR_0) - (LR_s - LR_0)]
    per aligned pixel, in display codes -- isolates the Highlights operator from the
    neutral baseline gap.
"""
import os, glob
import numpy as np
from PIL import Image
from scipy.ndimage import uniform_filter
import rawr_transform as rt

REND=os.path.join(os.path.dirname(__file__),"renders"); LR=r"C:\Users\Torsten\Desktop\Datasets\Highlights"
DS=8; GEPS,GR=0.25,8
EDGES=np.arange(-6,4.01,0.2); CENT=0.5*(EDGES[:-1]+EDGES[1:]); NB=CENT.size
def code(ev): return rt.display_transform(0.18*2**ev)*255
def suffix(s): return "h000" if s==0 else f"h{'+' if s>0 else '-'}{abs(s):03d}"

def load(path,png):
    im=Image.open(path)
    if not png: im.draft("RGB",(im.width//DS,im.height//DS))
    a=np.asarray(im.convert("RGB"),np.uint8)
    if png:
        h,w=a.shape[:2]; h-=h%DS; w-=w%DS
        a=a[:h,:w].reshape(h//DS,DS,w//DS,DS,3).mean(axis=(1,3))
    ev,clip=rt.code_to_ev_luma(a); return ev.astype(np.float32),clip
def guided(ev):
    s=2*GR+1; m=uniform_filter(ev,s,mode="nearest"); c=uniform_filter(ev*ev,s,mode="nearest")
    v=np.maximum(c-m*m,0); a=v/(v+GEPS); b=(1-a)*m
    return uniform_filter(a,s,mode="nearest")*ev+uniform_filter(b,s,mode="nearest")
def crop(*arr):
    h=min(a.shape[0] for a in arr); w=min(a.shape[1] for a in arr); return [a[:h,:w] for a in arr]

stems=sorted({os.path.basename(p).split("_h")[0] for p in glob.glob(os.path.join(REND,"*_h000.png"))})
sliders=[-100,-50,50,100]
print(f"{len(stems)} rendered scenes: {stems}")

# aggregate curve accumulators
accR={s:[np.zeros(NB),np.zeros(NB)] for s in sliders}
accL={s:[np.zeros(NB),np.zeros(NB)] for s in sliders}
opsse={s:0.0 for s in sliders}; opN={s:0 for s in sliders}
baseR=[]
for stem in stems:
    try:
        rn,rcn=load(os.path.join(REND,f"{stem}_h000.png"),True)
        ln,lcn=load(os.path.join(LR,f"{stem}_h000.jpg"),False)
    except FileNotFoundError: continue
    Br=guided(rn); Bl=guided(ln); ir=np.clip(np.digitize(Br,EDGES)-1,0,NB-1); il=np.clip(np.digitize(Bl,EDGES)-1,0,NB-1)
    rn_c,ln_c,rcn_c,lcn_c=crop(rn,ln,rcn,lcn)
    v0=(~rcn_c)&(~lcn_c); baseR.append(np.sqrt(np.mean((code(rn_c)-code(ln_c))[v0]**2)))
    for s in sliders:
        p=os.path.join(REND,f"{stem}_{suffix(s)}.png")
        if not os.path.exists(p): continue
        rs,rcs=load(p,True); ls,lcs=load(os.path.join(LR,f"{stem}_{suffix(s)}.jpg"),False)
        # RAWR curve (own base ir)
        vr=(~rcn)&(~rcs); yr=(rs-rn)[vr]
        accR[s][0]+=np.bincount(ir[vr],yr,minlength=NB)[:NB]; accR[s][1]+=np.bincount(ir[vr],minlength=NB)[:NB]
        vl=(~lcn)&(~lcs); yl=(ls-ln)[vl]
        accL[s][0]+=np.bincount(il[vl],yl,minlength=NB)[:NB]; accL[s][1]+=np.bincount(il[vl],minlength=NB)[:NB]
        # operator-delta RMS (aligned)
        rs_c,ls_c,rn_c2,ln_c2,rcs_c,lcs_c,rcn_c2,lcn_c2=crop(rs,ls,rn,ln,rcs,lcs,rcn,lcn)
        vv=(~rcs_c)&(~lcs_c)&(~rcn_c2)&(~lcn_c2)
        dR=code(rs_c)-code(rn_c2); dL=code(ls_c)-code(ln_c2)
        e=(dR-dL)[vv]; opsse[s]+=float(np.sum(e*e)); opN[s]+=int(vv.sum())

print(f"\nneutral baseline RMS (RAWR vs LR, per scene): mean={np.mean(baseR):.1f} codes  [{np.min(baseR):.0f}..{np.max(baseR):.0f}]")
print("\naverage dEV(base) curve: RAWR real render vs LR")
print(f"{'code':>5} | " + " ".join(f"S{s:+04d} R / L" for s in sliders))
for ev in [-1.0,-0.5,0.0,0.5,1.0,1.5]:
    j=np.argmin(np.abs(CENT-ev)); cells=[]
    for s in sliders:
        r=accR[s][0][j]/max(accR[s][1][j],1); l=accL[s][0][j]/max(accL[s][1][j],1)
        cells.append(f"{r:+.2f}/{l:+.2f}")
    print(f"{code(CENT[j]):5.0f} | "+"  ".join(cells))
print("\noperator-delta RMS (baseline-cancelled, aligned), display codes:")
for s in sliders:
    print(f"  S={s:+4d}: {np.sqrt(opsse[s]/max(opN[s],1)):.2f}  (n={opN[s]/1e3:.0f}k)")
