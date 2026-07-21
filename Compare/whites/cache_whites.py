"""Decode every Whites JPEG, invert to scene-linear EV, cache per scene (clip at both ends)."""
import os, glob, time, sys
import numpy as np
from PIL import Image
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))
import rawr_transform as rt

DATA = r"C:\Users\Torsten\Desktop\Datasets\Whites"
OUT  = os.path.join(os.path.dirname(__file__), "cache")
DRAFT = (760, 1140); SLIDERS = [-100, -75, -50, -25, 25, 50, 75, 100]
os.makedirs(OUT, exist_ok=True)
def suffix(s): return f"whites{'+' if s > 0 else '-'}{abs(s):03d}"
def load(path):
    im = Image.open(path); im.draft("RGB", DRAFT); a = np.asarray(im.convert("RGB"), np.uint8)
    ev, _ = rt.code_to_ev_luma(a); clip = np.any(a <= 2, axis=-1) | np.any(a >= 253, axis=-1)
    return ev.astype(np.float32), clip
def main():
    stems = sorted({os.path.basename(p)[:-len("_whites000.jpg")]
                    for p in glob.glob(os.path.join(DATA, "*_whites000.jpg"))})
    t0=time.time()
    for i, stem in enumerate(stems):
        out = os.path.join(OUT, f"{stem}.npz")
        if os.path.exists(out): continue
        d={}; d["ev_n"],d["clip_n"]=load(os.path.join(DATA,f"{stem}_whites000.jpg"))
        for s in SLIDERS: d[f"ev_{s}"],d[f"clip_{s}"]=load(os.path.join(DATA,f"{stem}_{suffix(s)}.jpg"))
        np.savez(out, **d); print(f"[{i+1}/{len(stems)}] {stem} {d['ev_n'].shape} ({time.time()-t0:.0f}s)", flush=True)
    print("done", flush=True)
if __name__ == "__main__": main()
