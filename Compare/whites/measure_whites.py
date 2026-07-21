"""
Measure LR's Whites response. Whites v4 is GLOBAL (per-pixel luminance EV); test that
against LR by comparing two predictors of the actual per-pixel ΔEV:
  (a) global  — binned by the pixel's own neutral EV
  (b) regional — binned by the guided-filter regional base (EdgeAwareLuma radius)
Lower residual RMS = the truer description. Also reports the ΔEV(ev) curve per slider,
slider-response (Whites is geometric, not linear), and the current ApplyWhitesV4 RMS.
"""
import os, glob, sys
import numpy as np
from scipy.ndimage import uniform_filter
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))
import rawr_transform as rt

CACHE=os.path.join(os.path.dirname(__file__),"cache")
STEMS=sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(CACHE,"*.npz")))
SLIDERS=[-100,-75,-50,-25,25,50,75,100]; GEPS=0.25; DS=2
BIN_LO,BIN_HI,BW=-2.0,4.0,0.15
EDGES=np.arange(BIN_LO,BIN_HI+BW,BW); CENT=0.5*(EDGES[:-1]+EDGES[1:]); NB=CENT.size
def down(a,f):
    h,w=a.shape;h-=h%f;w-=w%f;return a[:h,:w].reshape(h//f,f,w//f,f).mean(axis=(1,3))
def downc(a,f):
    h,w=a.shape;h-=h%f;w-=w%f;return a[:h,:w].reshape(h//f,f,w//f,f).any(axis=(1,3))
def rr(w,h): return max(8,min(w,h)//16)
def guided(ev,r):
    s=2*r+1;m=uniform_filter(ev,s,mode="nearest");c=uniform_filter(ev*ev,s,mode="nearest")
    v=np.maximum(c-m*m,0);a=v/(v+GEPS);b=(1-a)*m
    return uniform_filter(a,s,mode="nearest")*ev+uniform_filter(b,s,mode="nearest")
def code(ev): return rt.display_transform(0.18*2**ev)*255
def binidx(x): return np.clip(np.digitize(x,EDGES)-1,0,NB-1)
# current ApplyWhitesV4
def softknee(d,slope,width):
    width=max(width,1e-6);return np.where(d<=0,d,slope*d+(1-slope)*width*(1-np.exp(-np.clip(d,0,None)/width)))
def cur_dev(ev,whites):
    d=ev-0.1;k=abs(whites)/100.0;slope=(0.05 if whites<0 else 2.2)**k
    nd=np.where(d<=0,d,softknee(d,slope,0.42));return (0.1+nd)-ev

def load_scene(stem):
    z=np.load(os.path.join(CACHE,stem+".npz"))
    d={"ev_n":down(z["ev_n"].astype(np.float32),DS),"clip_n":downc(z["clip_n"],DS)}
    for s in SLIDERS: d[f"ev_{s}"]=down(z[f"ev_{s}"].astype(np.float32),DS);d[f"clip_{s}"]=downc(z[f"clip_{s}"],DS)
    return d

def main():
    print(f"{len(STEMS)} scenes")
    # mean-curve accumulators for global(ev) and regional(base)
    gS={s:np.zeros(NB) for s in SLIDERS}; gN={s:np.zeros(NB) for s in SLIDERS}
    rS={s:np.zeros(NB) for s in SLIDERS}; rN={s:np.zeros(NB) for s in SLIDERS}
    scenes=[]
    for stem in STEMS:
        sc=load_scene(stem); scenes.append(sc); ev_n=sc["ev_n"]; cn=sc["clip_n"]
        R=rr(*ev_n.shape[::-1]); base=guided(ev_n,R); sc["_base"]=base
        gi=binidx(ev_n); ri=binidx(base)
        for s in SLIDERS:
            valid=(~cn)&(~sc[f"clip_{s}"]); y=(sc[f"ev_{s}"]-ev_n)
            gS[s]+=np.bincount(gi[valid],y[valid],minlength=NB)[:NB]; gN[s]+=np.bincount(gi[valid],minlength=NB)[:NB]
            rS[s]+=np.bincount(ri[valid],y[valid],minlength=NB)[:NB]; rN[s]+=np.bincount(ri[valid],minlength=NB)[:NB]
    gC={s:gS[s]/np.maximum(gN[s],1) for s in SLIDERS}; rC={s:rS[s]/np.maximum(rN[s],1) for s in SLIDERS}
    # residual RMS of each model (in EV) + current op RMS (codes)
    sse_g={s:0. for s in SLIDERS};sse_r={s:0. for s in SLIDERS};sse_c={s:0. for s in SLIDERS};n={s:0 for s in SLIDERS}
    for sc in scenes:
        ev_n=sc["ev_n"];cn=sc["clip_n"];base=sc["_base"];gi=binidx(ev_n);ri=binidx(base)
        for s in SLIDERS:
            valid=(~cn)&(~sc[f"clip_{s}"]);y=(sc[f"ev_{s}"]-ev_n)
            pg=gC[s][gi];pr=rC[s][ri]
            sse_g[s]+=float(np.sum(((pg-y)[valid])**2));sse_r[s]+=float(np.sum(((pr-y)[valid])**2))
            cs=code(sc[f"ev_{s}"]);cp=code(ev_n+cur_dev(ev_n,s))
            sse_c[s]+=float(np.sum(((cp-cs)[valid])**2));n[s]+=int(valid.sum())
    print("\nGLOBAL vs REGIONAL model residual (EV RMS; lower=truer description):")
    print(f"{'S':>5} | global | region")
    for s in SLIDERS: print(f"{s:+5d} | {np.sqrt(sse_g[s]/max(n[s],1)):.3f}  | {np.sqrt(sse_r[s]/max(n[s],1)):.3f}")
    tg=np.sqrt(sum(sse_g.values())/sum(n.values()));tr=np.sqrt(sum(sse_r.values())/sum(n.values()))
    print(f"  ALL | {tg:.3f}  | {tr:.3f}")
    print("\nΔEV(per-pixel ev) curve per slider, and current-op:")
    print(f"{'code':>5} | "+" ".join(f"S{s:+04d}" for s in SLIDERS)+" | cur+100 cur-100")
    for ev in np.arange(-0.5,3.01,0.3):
        j=np.argmin(np.abs(CENT-ev))
        cells=" ".join(f"{gC[s][j]:+5.2f}" for s in SLIDERS)
        print(f"{code(CENT[j]):5.0f} | {cells} | {cur_dev(CENT[j],100):+.2f}  {cur_dev(CENT[j],-100):+.2f}")
    print("\ncurrent-op RMS (codes) per slider:")
    for s in SLIDERS: print(f"  S={s:+4d}: {np.sqrt(sse_c[s]/max(n[s],1)):.2f} (n={n[s]/1e6:.1f}M)")
    np.savez("whites_measured.npz",cent=CENT,**{f"g_{s}":gC[s] for s in SLIDERS},**{f"gn_{s}":gN[s] for s in SLIDERS})

if __name__=="__main__": main()
