"""
Decode every Shadows JPEG once, invert to scene-linear luminance EV, cache per scene.
Tracks black-crush clipping (any channel <=2) — the shadow analog of the 255 highlight
clip — and top clipping (>=253), so the fit can exclude tones it cannot measure.
Cache dir: shadows/cache/<stem>.npz  with ev_n/clip_n and ev_<S>/clip_<S>.
"""
import os, glob, time, sys
import numpy as np
from PIL import Image
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))  # reuse rawr_transform
import rawr_transform as rt

DATA = r"C:\Users\Torsten\Desktop\Datasets\Shadows"
OUT  = os.path.join(os.path.dirname(__file__), "cache")
DRAFT = (760, 1140)
SLIDERS = [-100, -75, -50, -25, 25, 50, 75, 100]
os.makedirs(OUT, exist_ok=True)

def suffix(s): return f"shadows{'+' if s > 0 else '-'}{abs(s):03d}"

def load(path):
    im = Image.open(path); im.draft("RGB", DRAFT); im = im.convert("RGB")
    a = np.asarray(im, np.uint8)
    ev, _ = rt.code_to_ev_luma(a)
    clip = np.any(a <= 2, axis=-1) | np.any(a >= 253, axis=-1)   # crushed OR blown
    return ev.astype(np.float32), clip

def main():
    stems = sorted({os.path.basename(p)[:-len("_shadows000.jpg")]
                    for p in glob.glob(os.path.join(DATA, "*_shadows000.jpg"))})
    t0 = time.time()
    for i, stem in enumerate(stems):
        out = os.path.join(OUT, f"{stem}.npz")
        if os.path.exists(out): print(f"[{i+1}/{len(stems)}] {stem} cached"); continue
        d = {}
        d["ev_n"], d["clip_n"] = load(os.path.join(DATA, f"{stem}_shadows000.jpg"))
        for s in SLIDERS:
            d[f"ev_{s}"], d[f"clip_{s}"] = load(os.path.join(DATA, f"{stem}_{suffix(s)}.jpg"))
        np.savez(out, **d)
        print(f"[{i+1}/{len(stems)}] {stem} {d['ev_n'].shape} ({time.time()-t0:.0f}s)")
    print("done")

if __name__ == "__main__":
    main()
