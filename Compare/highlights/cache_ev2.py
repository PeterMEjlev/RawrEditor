"""
Cache BOTH Highlights datasets (Dataset 1 + Dataset 2) into one cache/ directory,
tagged by dataset prefix (D1_/D2_) so fit_holdout.py can fit on one and validate
on the other. Handles the two different filename conventions the two export
batches used ("_h000.jpg" vs "_highlights000.jpg").

Output: cache/<D1|D2>_<stem>.npz, same schema as cache_ev.py's per-scene cache
(ev_n, clip_n, ev_<S>, clip_<S> for S in +-25/50/75/100).
"""
import os, glob, time
import numpy as np
from PIL import Image
import rawr_transform as rt

ROOTS = {
    "D1": (r"C:\Users\Torsten\Desktop\Datasets\Dataset 1\Highlights", "h"),
    "D2": (r"C:\Users\Torsten\Desktop\Datasets\Dataset 2\Highlights", "highlights"),
}
OUT = os.path.join(os.path.dirname(__file__), "cache")
DRAFT = (760, 1140)
SLIDERS = [-100, -75, -50, -25, 25, 50, 75, 100]
os.makedirs(OUT, exist_ok=True)


def suffix(tag, s):
    return f"{tag}000" if s == 0 else f"{tag}{'+' if s > 0 else '-'}{abs(s):03d}"


def load_ev(path):
    im = Image.open(path); im.draft("RGB", DRAFT); im = im.convert("RGB")
    codes = np.asarray(im, dtype=np.uint8)
    ev, clip = rt.code_to_ev_luma(codes)
    return ev.astype(np.float32), clip


def main():
    t0 = time.time()
    new_count = 0
    for dskey, (root, tag) in ROOTS.items():
        neutral_suffix = f"_{tag}000.jpg"
        stems = sorted({os.path.basename(p)[:-len(neutral_suffix)]
                        for p in glob.glob(os.path.join(root, f"*{neutral_suffix}"))})
        print(f"[{dskey}] {len(stems)} scenes in {root}")
        for i, stem in enumerate(stems):
            key = f"{dskey}_{stem}"
            out = os.path.join(OUT, f"{key}.npz")
            if os.path.exists(out):
                print(f"  [{i+1:2d}/{len(stems)}] {key} cached"); continue
            d = {}
            ev_n, clip_n = load_ev(os.path.join(root, f"{stem}{neutral_suffix}"))
            d["ev_n"] = ev_n; d["clip_n"] = clip_n
            for s in SLIDERS:
                p = os.path.join(root, f"{stem}_{suffix(tag, s)}.jpg")
                ev_s, clip_s = load_ev(p)
                d[f"ev_{s}"] = ev_s; d[f"clip_{s}"] = clip_s
            np.savez(out, **d)
            new_count += 1
            print(f"  [{i+1:2d}/{len(stems)}] {key}  {ev_n.shape}  ({time.time()-t0:5.1f}s)")
    print(f"done, {new_count} newly cached scenes ({time.time()-t0:.0f}s total)")


if __name__ == "__main__":
    main()
