"""
Fit the calibrated Shadows amplitude curve (mirrored softplus rising into shadows,
tapered out of the highlights) and evaluate operator RMS vs the current ApplyShadowsV3
and do-nothing. Test whether the weak detail term helps. Print C# constants.
"""
import os, glob, sys
import numpy as np
from scipy.optimize import curve_fit
from scipy.ndimage import uniform_filter
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))
import rawr_transform as rt

CACHE=os.path.join(os.path.dirname(__file__),"cache")
STEMS=sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(CACHE,"*.npz")))
SLIDERS=[-100,-75,-50,-25,25,50,75,100]; GEPS=0.25; DS=2
m=np.load("shadows_measured.npz"); CENT=m["cent"]; nP=m["n_pos"]; nN=m["n_neg"]
Aneg=m["A_neg"]; Apos=m["A_pos"]

def softplus(x): return np.log1p(np.exp(-np.abs(x)))+np.maximum(x,0)
def down(a,f):
    h,w=a.shape; h-=h%f; w-=w%f; return a[:h,:w].reshape(h//f,f,w//f,f).mean(axis=(1,3))
def downc(a,f):
    h,w=a.shape; h-=h%f; w-=w%f; return a[:h,:w].reshape(h//f,f,w//f,f).any(axis=(1,3))
def rr(w,h): return max(8,min(w,h)//16)
def guided(ev,r):
    s=2*r+1; me=uniform_filter(ev,s,mode="nearest"); c=uniform_filter(ev*ev,s,mode="nearest")
    v=np.maximum(c-me*me,0); a=v/(v+GEPS); b=(1-a)*me
    return uniform_filter(a,s,mode="nearest")*ev+uniform_filter(b,s,mode="nearest")
def code(ev): return rt.display_transform(0.18*2**ev)*255
def smoothstep(e0,e1,x):
    u=np.clip((x-e0)/(e1-e0),0,1); return u*u*(3-2*u)
def cur_amp(base): return 3.5*(1-smoothstep(-4.5,0.5,base))

# symmetric target: average lift and |deepen| where both reliable; use lift deep down
nmin=np.minimum(nP,nN)
sym=np.where(nmin>3000, 0.5*(Apos-Aneg), Apos)   # Aneg<0
# fit mirrored softplus + floor, over the reliable range
reli=(nP>4000)&(CENT>=-6.5)&(CENT<=1.2)
B=CENT[reli]; y=sym[reli]; w=np.sqrt(nP[reli])
def form(B,f0,s,knee,W): return f0+s*W*softplus((knee-B)/W)
p0=[0.06,0.5,-0.6,0.7]
popt,_=curve_fit(form,B,y,p0=p0,sigma=1/w,maxfev=40000); f0,s,knee,W=popt
# whites guard: taper Amp to 0 across [gLo,gHi] EV so highlights stay clean
gLo,gHi=0.6,1.6
def taper(B): return np.clip((gHi-B)/(gHi-gLo),0,1)
def Amp(B): return form(B,*popt)*taper(B)
print(f"Amp fit: f0={f0:.4f} s={s:.4f} knee={knee:.4f} W={W:.4f}  guard[{gLo},{gHi}]")

# detail curve (symmetric, from G): expand on lift, compress on deepen
Gp=m["G_pos"]; Gn=m["G_neg"]
Detm=np.where(nmin>8000, 0.5*(Gp-Gn), 0.0)  # >0
# smooth-ish: fit a smoothstep bump peaking in mid-shadows
reld=(nmin>8000)&(CENT>=-4)&(CENT<=0.5)
try:
    pd,_=curve_fit(lambda B,dmax,lo,hi: dmax*smoothstep(hi,lo,B), CENT[reld], np.clip(Detm[reld],0,0.5),
                   p0=[0.2,-2.5,-0.3], maxfev=40000)
    dmax,dlo,dhi=pd
except Exception as e:
    dmax,dlo,dhi=0.15,-2.5,-0.3
def Det(B): return dmax*smoothstep(dhi,dlo,B)  # note hi<lo: rises as B drops
print(f"Det fit: dmax={dmax:.3f} dlo={dlo:.3f} dhi={dhi:.3f}")

# monotonicity of lift at +100: base -> base + Amp(base) must be increasing
g=np.linspace(-9,3,12001); mono=np.all(np.diff(g+Amp(g))>0)
print(f"lift map monotonic at +100: {mono} (min d/db {np.min(np.diff(g+Amp(g)))/(g[1]-g[0]):.3f})")

# ---- eval RMS ----
scenes=[]
for stem in STEMS:
    z=np.load(os.path.join(CACHE,stem+".npz"))
    d={"ev_n":down(z["ev_n"].astype(np.float32),DS),"clip_n":downc(z["clip_n"],DS)}
    for s_ in SLIDERS: d[f"ev_{s_}"]=down(z[f"ev_{s_}"].astype(np.float32),DS); d[f"clip_{s_}"]=downc(z[f"clip_{s_}"],DS)
    scenes.append(d)
bases=[guided(sc["ev_n"], rr(*sc["ev_n"].shape[::-1])) for sc in scenes]

def evalrms(use_detail):
    sm={s:0. for s in SLIDERS}; sc_={s:0. for s in SLIDERS}; sn={s:0. for s in SLIDERS}; cn={s:0 for s in SLIDERS}
    for sc,Bb in zip(scenes,bases):
        ev_n=sc["ev_n"]; D=ev_n-Bb; cnb=sc["clip_n"]; code_n=code(ev_n)
        for s in SLIDERS:
            valid=(~cnb)&(~sc[f"clip_{s}"]); cs=code(sc[f"ev_{s}"])
            pred=ev_n+(s/100.0)*Amp(Bb)
            if use_detail: pred=pred+(s/100.0)*Det(Bb)*D
            cp=code(pred)
            sm[s]+=float(np.sum(((cp-cs)[valid])**2))
            sc_[s]+=float(np.sum(((code(ev_n+(s/100.0)*cur_amp(Bb))-cs)[valid])**2))
            sn[s]+=float(np.sum(((code_n-cs)[valid])**2)); cn[s]+=int(valid.sum())
    def r(d):
        return {**{s:np.sqrt(d[s]/max(cn[s],1)) for s in SLIDERS},"all":np.sqrt(sum(d.values())/sum(cn.values()))}
    return r(sm),r(sc_),r(sn)

md,cd,nd=evalrms(False); md2,_,_=evalrms(True)
print("\nRMS (display codes) per slider:  nothing | current | NEW(no detail) | NEW(detail)")
for s in SLIDERS:
    print(f"  S={s:+4d}: {nd[s]:6.2f} | {cd[s]:6.2f} | {md[s]:6.2f} | {md2[s]:6.2f}")
print(f"  {'ALL':>5}: {nd['all']:6.2f} | {cd['all']:6.2f} | {md['all']:6.2f} | {md2['all']:6.2f}")
print("\nAmp table (base_ev, code, Amp_fit vs sym target):")
for ev in np.arange(-7,1.01,0.5):
    j=np.argmin(np.abs(CENT-ev)); print(f"  {CENT[j]:+5.2f} {code(CENT[j]):4.0f}: {Amp(CENT[j]):+.3f}  (target {sym[j]:+.3f})")
np.savez("shadows_params.npz", f0=f0,s=s,knee=knee,W=W,gLo=gLo,gHi=gHi,dmax=dmax,dlo=dlo,dhi=dhi)
print("\n--- C# ---")
print(f"AmpFloor={f0:.4f}; AmpSlope={s:.4f}; AmpKneeEv={knee:.4f}; AmpWidthEv={W:.4f};")
print(f"WhitesGuardLoEv={gLo}; WhitesGuardHiEv={gHi}; DetailMax={dmax:.4f}; DetailLoEv={dlo:.4f}; DetailHiEv={dhi:.4f};")
