"""
Recalibrate ApplyWhitesV4's soft-knee/geometric-slope form to LR: fit knee, width,
MinSlope (neg), MaxSlope (pos). Then evaluate per-pixel RMS vs the current op and nothing.
"""
import os, glob, sys
import numpy as np
from scipy.optimize import least_squares
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))
import rawr_transform as rt

CACHE=os.path.join(os.path.dirname(__file__),"cache")
STEMS=sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(CACHE,"*.npz")))
SLIDERS=[-100,-75,-50,-25,25,50,75,100]; DS=2
m=np.load("whites_measured.npz"); CENT=m["cent"]
G={s:m[f"g_{s}"] for s in SLIDERS}; GN={s:m[f"gn_{s}"] for s in SLIDERS}
def code(ev): return rt.display_transform(0.18*2**ev)*255
def softknee(d,slope,width):
    width=max(width,1e-6); return np.where(d<=0,d,slope*d+(1-slope)*width*(1-np.exp(-np.clip(d,0,None)/width)))
def model_dev(ev,whites,knee,width,mins,maxs):
    k=abs(whites)/100.0; slope=(mins if whites<0 else maxs)**k
    d=ev-knee; nd=np.where(d<=0,d,softknee(d,slope,width)); return (knee+nd)-ev

# ---- fit on binned means. Reliable: exclude clipped-dominated bins.
# negative sliders reliable to code<=250 (ev<=~1.95); positive to code<=225 (ev<=~0.65).
def residuals(p):
    knee,width,mins,maxs=p; res=[]
    for s in SLIDERS:
        hi = 1.95 if s<0 else 0.75
        mask=(GN[s]>4000)&(CENT>=-1.0)&(CENT<=hi)
        w=np.sqrt(GN[s][mask])
        pred=model_dev(CENT[mask],s,knee,width,mins,maxs)
        res.append(w*(pred-G[s][mask]))
    return np.concatenate(res)
p0=[-1.0,0.5,0.6,2.0]
sol=least_squares(residuals,p0,bounds=([-2.5,0.25,0.30,1.2],[0.5,1.5,0.95,6.0]),max_nfev=20000)
knee,width,mins,maxs=sol.x
print(f"fit: knee={knee:.4f} width={width:.4f} MinSlope={mins:.4f} MaxSlope={maxs:.4f}")
# monotonicity: at every |slider|, ev -> knee+softknee(ev-knee,slope,width) must increase
gmono=True
for s in SLIDERS:
    k=abs(s)/100.0; sl=(mins if s<0 else maxs)**k; ev=np.linspace(knee-0.5,5,6001)
    ne=knee+softknee(ev-knee,sl,width)
    if not np.all(np.diff(ne)>0): gmono=False
print(f"monotonic all sliders: {gmono}")

# ---- per-pixel RMS eval (streaming) ----
def down(a,f):
    h,w=a.shape;h-=h%f;w-=w%f;return a[:h,:w].reshape(h//f,f,w//f,f).mean(axis=(1,3))
def downc(a,f):
    h,w=a.shape;h-=h%f;w-=w%f;return a[:h,:w].reshape(h//f,f,w//f,f).any(axis=(1,3))
def cur_dev(ev,whites):
    k=abs(whites)/100.0; slope=(0.05 if whites<0 else 2.2)**k
    d=ev-0.1; nd=np.where(d<=0,d,softknee(d,slope,0.42)); return (0.1+nd)-ev
sm={s:0. for s in SLIDERS};sc={s:0. for s in SLIDERS};sn={s:0. for s in SLIDERS};cn={s:0 for s in SLIDERS}
for stem in STEMS:
    z=np.load(os.path.join(CACHE,stem+".npz")); ev_n=down(z["ev_n"].astype(np.float32),DS); c0=downc(z["clip_n"],DS)
    code_n=code(ev_n)
    for s in SLIDERS:
        ev_s=down(z[f"ev_{s}"].astype(np.float32),DS); cs=downc(z[f"clip_{s}"],DS); valid=(~c0)&(~cs)
        code_s=code(ev_s)
        pm=code(ev_n+model_dev(ev_n,s,knee,width,mins,maxs)); pc=code(ev_n+cur_dev(ev_n,s))
        sm[s]+=float(np.sum(((pm-code_s)[valid])**2)); sc[s]+=float(np.sum(((pc-code_s)[valid])**2))
        sn[s]+=float(np.sum(((code_n-code_s)[valid])**2)); cn[s]+=int(valid.sum())
print("\nRMS (codes): nothing | current | NEW")
for s in SLIDERS:
    print(f"  S={s:+4d}: {np.sqrt(sn[s]/cn[s]):6.2f} | {np.sqrt(sc[s]/cn[s]):6.2f} | {np.sqrt(sm[s]/cn[s]):6.2f}")
alln=np.sqrt(sum(sn.values())/sum(cn.values()));allc=np.sqrt(sum(sc.values())/sum(cn.values()));allm=np.sqrt(sum(sm.values())/sum(cn.values()))
print(f"  {'ALL':>5}: {alln:6.2f} | {allc:6.2f} | {allm:6.2f}")
print("\nΔEV(ev): code | S-100 fit/LR | S+100 fit/LR")
for ev in np.arange(-0.6,2.01,0.3):
    j=np.argmin(np.abs(CENT-ev))
    print(f"  {code(CENT[j]):4.0f} | {model_dev(CENT[j],-100,knee,width,mins,maxs):+.2f}/{G[-100][j]:+.2f} | "
          f"{model_dev(CENT[j],100,knee,width,mins,maxs):+.2f}/{G[100][j]:+.2f}")
print(f"\n--- C# ---\nWhitesKneeEv={knee:.4f}; WhitesWidthEv={width:.4f}; WhitesMinSlope={mins:.4f}; WhitesMaxSlope={maxs:.4f};")
