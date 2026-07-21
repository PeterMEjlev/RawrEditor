"""
Decode every JPEG once, invert to scene-linear luminance EV, and cache per-scene
arrays so the radius sweep / curve fit can iterate in-memory without re-decoding.

Cache layout (scratch/cache/<stem>.npz):
  ev_n            float32 (H,W)   neutral scene EV
  clip_n          bool            neutral clipped (any channel >=253)
  ev_<S>          float32 (H,W)   slider scene EV        for S in +-25/50/75/100
  clip_<S>        bool            slider clipped
"""
import os, glob, time
import numpy as np
from PIL import Image
import rawr_transform as rt

DATA = r"C:\Users\Torsten\Desktop\Datasets\Highlights"
OUT  = os.path.join(os.path.dirname(__file__), "cache")
DRAFT = (760, 1140)
SLIDERS = [-100, -75, -50, -25, 25, 50, 75, 100]
os.makedirs(OUT, exist_ok=True)


def suffix(s): return f"h{'+' if s > 0 else '-'}{abs(s):03d}"


def load_ev(path):
    im = Image.open(path); im.draft("RGB", DRAFT); im = im.convert("RGB")
    codes = np.asarray(im, dtype=np.uint8)
    ev, clip = rt.code_to_ev_luma(codes)
    return ev.astype(np.float32), clip


def main():
    stems = sorted({os.path.basename(p)[:-len("_h000.jpg")]
                    for p in glob.glob(os.path.join(DATA, "*_h000.jpg"))})
    t0 = time.time()
    for i, stem in enumerate(stems):
        out = os.path.join(OUT, f"{stem}.npz")
        if os.path.exists(out):
            print(f"[{i+1:2d}/{len(stems)}] {stem} cached"); continue
        d = {}
        ev_n, clip_n = load_ev(os.path.join(DATA, f"{stem}_h000.jpg"))
        d["ev_n"] = ev_n; d["clip_n"] = clip_n
        for s in SLIDERS:
            ev_s, clip_s = load_ev(os.path.join(DATA, f"{stem}_{suffix(s)}.jpg"))
            d[f"ev_{s}"] = ev_s; d[f"clip_{s}"] = clip_s
        np.savez(out, **d)
        print(f"[{i+1:2d}/{len(stems)}] {stem}  {ev_n.shape}  ({time.time()-t0:5.1f}s)")
    print("done")


if __name__ == "__main__":
    main()
