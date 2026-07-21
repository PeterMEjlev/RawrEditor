"""
Measure LR's Shadows response in scene-linear EV, binned by the engine's regional base
(EdgeAwareLuma self-guided filter, radius = min(w,h)/16). Reports the dEV(base) curve per
direction, the detail-layer slope, slider-linearity, +/- symmetry, and the current
ApplyShadowsV3 operator for comparison.
"""
import os, glob, sys
import numpy as np
from scipy.ndimage import uniform_filter
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))
import rawr_transform as rt

CACHE = os.path.join(os.path.dirname(__file__), "cache")
STEMS = sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(CACHE, "*.npz")))
SLIDERS = [-100, -75, -50, -25, 25, 50, 75, 100]
GEPS = 0.25; DS = 2
BIN_LO, BIN_HI, BW = -9.0, 2.0, 0.2
EDGES = np.arange(BIN_LO, BIN_HI+BW, BW); CENT = 0.5*(EDGES[:-1]+EDGES[1:]); NB = CENT.size

def down(a, f):
    h,w=a.shape; h-=h%f; w-=w%f; return a[:h,:w].reshape(h//f,f,w//f,f).mean(axis=(1,3))
def downc(a,f):
    h,w=a.shape; h-=h%f; w-=w%f; return a[:h,:w].reshape(h//f,f,w//f,f).any(axis=(1,3))
def region_radius(w,h): return max(8, min(w,h)//16)   # EdgeAwareLuma.RegionRadius
def guided(ev, r):
    s=2*r+1; m=uniform_filter(ev,s,mode="nearest"); c=uniform_filter(ev*ev,s,mode="nearest")
    v=np.maximum(c-m*m,0); a=v/(v+GEPS); b=(1-a)*m
    return uniform_filter(a,s,mode="nearest")*ev+uniform_filter(b,s,mode="nearest")
def code(ev): return rt.display_transform(0.18*2**ev)*255
def idxof(B): return np.clip(np.digitize(B,EDGES)-1,0,NB-1)

# current operator (ApplyShadowsV3): dEV = (sh/100)*3.5*(1 - smoothstep(-4.5,0.5,base))
def smoothstep(e0,e1,x):
    u=np.clip((x-e0)/(e1-e0),0,1); return u*u*(3-2*u)
def cur_amp(base): return 3.5*(1-smoothstep(-4.5,0.5,base))

def load_scene(stem):
    z=np.load(os.path.join(CACHE,stem+".npz"))
    ev_n=down(z["ev_n"].astype(np.float32),DS); clip_n=downc(z["clip_n"],DS)
    d={"ev_n":ev_n,"clip_n":clip_n}
    for s in SLIDERS:
        d[f"ev_{s}"]=down(z[f"ev_{s}"].astype(np.float32),DS); d[f"clip_{s}"]=downc(z[f"clip_{s}"],DS)
    return d

def main():
    print(f"{len(STEMS)} scenes")
    # accumulate per-direction LS fit of (dEV/k) ~ A + G*D per base bin
    acc={d:{k:np.zeros(NB) for k in "n Sx Sy Sxx Sxy".split()} for d in (-1,1)}
    # per-slider mean dEV for linearity
    sl_sy={s:np.zeros(NB) for s in SLIDERS}; sl_n={s:np.zeros(NB) for s in SLIDERS}
    sse_cur={s:0.0 for s in SLIDERS}; cnt={s:0 for s in SLIDERS}
    for stem in STEMS:
        sc=load_scene(stem); ev_n=sc["ev_n"]; cn=sc["clip_n"]
        R=region_radius(*ev_n.shape[::-1]); B=guided(ev_n,R); D=ev_n-B; ii=idxof(B)
        for s in SLIDERS:
            k=abs(s)/100.0; dd=-1 if s<0 else 1
            cs=sc[f"clip_{s}"]; valid=(~cn)&(~cs)
            y=((sc[f"ev_{s}"]-ev_n)/k)[valid]; x=D[valid]; jj=ii[valid]
            a=acc[dd]
            a["n"]+=np.bincount(jj,minlength=NB)[:NB]; a["Sx"]+=np.bincount(jj,x,minlength=NB)[:NB]
            a["Sy"]+=np.bincount(jj,y,minlength=NB)[:NB]; a["Sxx"]+=np.bincount(jj,x*x,minlength=NB)[:NB]
            a["Sxy"]+=np.bincount(jj,x*y,minlength=NB)[:NB]
            sl_sy[s]+=np.bincount(jj,(sc[f"ev_{s}"]-ev_n)[valid],minlength=NB)[:NB]
            sl_n[s]+=np.bincount(jj,minlength=NB)[:NB]
            # current op dEV predicted from base
            cur=(s/100.0)*cur_amp(B)
            e=((s/100.0)*cur_amp(B) - (sc[f"ev_{s}"]-ev_n))  # in EV; convert to code below
            cp=code(ev_n+cur); cs_code=code(sc[f"ev_{s}"])
            m=valid
            sse_cur[s]+=float(np.sum(((cp-cs_code)[m])**2)); cnt[s]+=int(m.sum())
    # curves
    def solve(d):
        a=acc[d]; n=a["n"]; det=n*a["Sxx"]-a["Sx"]**2; safe=(n>300)&(np.abs(det)>1e-9)
        with np.errstate(invalid="ignore",divide="ignore"):
            A=(a["Sy"]*a["Sxx"]-a["Sx"]*a["Sxy"])/det; G=(n*a["Sxy"]-a["Sx"]*a["Sy"])/det; meanA=a["Sy"]/n
        return np.where(safe,A,meanA), np.where(safe,G,np.nan), n
    An,Gn,nn=solve(-1); Ap,Gp,npo=solve(1)
    np.savez("shadows_measured.npz", cent=CENT, A_neg=An,A_pos=Ap,G_neg=Gn,G_pos=Gp,n_neg=nn,n_pos=npo,
             **{f"sy_{s}":sl_sy[s] for s in SLIDERS}, **{f"n_{s}":sl_n[s] for s in SLIDERS})

    print("\n base_EV code |  A_neg(deepen) A_pos(lift) | G_neg G_pos | current_amp | n_pos(M)")
    for ev in np.arange(-8,1.01,0.5):
        j=np.argmin(np.abs(CENT-ev))
        print(f" {CENT[j]:+5.2f} {code(CENT[j]):4.0f} | {An[j]:+11.2f} {Ap[j]:+9.2f} | "
              f"{Gn[j]:+5.2f} {Gp[j]:+5.2f} | {cur_amp(CENT[j]):8.2f} | {npo[j]/1e6:.2f}")
    print("\nslider-linearity (mean dEV / (|s|/100)) at a few bases:")
    for ev in [-6,-4,-2,-0.5]:
        j=np.argmin(np.abs(CENT-ev))
        cells=[f"{s:+4d}:{(sl_sy[s][j]/max(sl_n[s][j],1))/(abs(s)/100):+.2f}" for s in SLIDERS]
        print(f"  base {CENT[j]:+.1f} ({code(CENT[j]):.0f}): "+" ".join(cells))
    print("\ncurrent-op RMS (display codes) per slider:")
    for s in SLIDERS:
        print(f"  S={s:+4d}: {np.sqrt(sse_cur[s]/max(cnt[s],1)):.2f}  (n={cnt[s]/1e6:.1f}M)")

if __name__ == "__main__":
    main()
