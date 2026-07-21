"""
Radius sweep + curve fit for the Highlights operator, evaluated by true per-pixel
residual (display-code units) against the Lightroom exports.

All cached scenes are loaded ONCE into RAM (downsampled 2x -> ~1024 long edge) so the
sweep is pure in-memory compute. Cache is 1/4 of full-res (2048), so after the extra
2x it is 1/8; a cache-radius R here corresponds to full-res radius R*8. The live engine
uses RegionRadius = clamp(min(w,h)/16,16,64) = 64 px at full res -> cache-radius 8 here.

Model (per pixel, EV about middle grey), dir = sign(slider), k = |slider|/100:
    B  = guided_base(I; radius, eps);  D = I - B
    I' = B + k*A_dir(B) + (1 + k*G_dir(B)) * D
dEV is linear in slider (measured), so pooling every slider of a direction and
regressing (dEV/k) ~ A + G*D per base bin gives the LS-optimal A_dir, G_dir.
"""
import os, glob, time
import numpy as np
from scipy.ndimage import uniform_filter
import rawr_transform as rt

CACHE = os.path.join(os.path.dirname(__file__), "cache")
STEMS = sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(CACHE, "*.npz")))
SLIDERS = [-100, -75, -50, -25, 25, 50, 75, 100]
GUIDED_EPS = 0.25
MIDGRAY = rt.MIDDLE_GRAY
DS = 2  # extra downsample of the cache

BIN_LO, BIN_HI, BIN_W = -6.0, 4.0, 0.1
EDGES = np.arange(BIN_LO, BIN_HI + BIN_W, BIN_W)
NB = EDGES.size - 1
CENT = 0.5 * (EDGES[:-1] + EDGES[1:])


def block_down(a, f):
    h, w = a.shape; h -= h % f; w -= w % f
    return a[:h, :w].reshape(h // f, f, w // f, f).mean(axis=(1, 3))

def clip_down(a, f):
    h, w = a.shape; h -= h % f; w -= w % f
    return a[:h, :w].reshape(h // f, f, w // f, f).any(axis=(1, 3))


def load_all():
    scenes = []
    for stem in STEMS:
        z = np.load(os.path.join(CACHE, stem + ".npz"))
        d = {"ev_n": block_down(z["ev_n"].astype(np.float32), DS),
             "clip_n": clip_down(z["clip_n"], DS)}
        for s in SLIDERS:
            d[f"ev_{s}"] = block_down(z[f"ev_{s}"].astype(np.float32), DS)
            d[f"clip_{s}"] = clip_down(z[f"clip_{s}"], DS)
        scenes.append(d)
    return scenes


def guided_base(ev, r):
    size = 2 * r + 1
    mean = uniform_filter(ev, size, mode="nearest")
    corr = uniform_filter(ev * ev, size, mode="nearest")
    var = np.maximum(corr - mean * mean, 0.0)
    a = var / (var + GUIDED_EPS)
    b = (1.0 - a) * mean
    return uniform_filter(a, size, mode="nearest") * ev + uniform_filter(b, size, mode="nearest")

def ev_to_code(ev):
    return rt.display_transform(MIDGRAY * np.exp2(ev)) * 255.0

def bin_idx(B):
    return np.clip(np.digitize(B, EDGES) - 1, 0, NB - 1).astype(np.intp)


def fit_curves(scenes, bases):
    acc = {d: {k: np.zeros(NB) for k in "n Sx Sy Sxx Sxy".split()} for d in (-1, 1)}
    for sc, (B, D, idx) in zip(scenes, bases):
        clip_n = sc["clip_n"]; ev_n = sc["ev_n"]
        for s in SLIDERS:
            k = abs(s) / 100.0; d = -1 if s < 0 else 1
            valid = (~clip_n) & (~sc[f"clip_{s}"])
            y = ((sc[f"ev_{s}"] - ev_n) / k)[valid]; x = D[valid]; ii = idx[valid]
            a = acc[d]
            a["n"]   += np.bincount(ii, minlength=NB)[:NB]
            a["Sx"]  += np.bincount(ii, x, minlength=NB)[:NB]
            a["Sy"]  += np.bincount(ii, y, minlength=NB)[:NB]
            a["Sxx"] += np.bincount(ii, x * x, minlength=NB)[:NB]
            a["Sxy"] += np.bincount(ii, x * y, minlength=NB)[:NB]
    curves = {}
    for d in (-1, 1):
        a = acc[d]; n = a["n"]
        det = n * a["Sxx"] - a["Sx"] ** 2
        safe = (n > 300) & (np.abs(det) > 1e-9)
        with np.errstate(invalid="ignore", divide="ignore"):
            A = (a["Sy"] * a["Sxx"] - a["Sx"] * a["Sxy"]) / det
            G = (n * a["Sxy"] - a["Sx"] * a["Sy"]) / det
            meanA = a["Sy"] / n
        A = np.where(safe, A, meanA)
        G = np.where(safe, G, 0.0)
        curves[d] = (A, G, n.copy())
    return curves


def smooth_curve(y, n, win=5):
    w = np.where(np.isfinite(y), np.maximum(n, 1.0), 0.0)
    yv = np.where(np.isfinite(y), y, 0.0)
    k = np.ones(win)
    num = np.convolve(yv * w, k, "same"); den = np.convolve(w, k, "same")
    with np.errstate(invalid="ignore", divide="ignore"):
        out = np.where(den > 0, num / den, np.nan)
    finite = np.where(np.isfinite(out))[0]
    if finite.size:
        out[:finite[0]] = out[finite[0]]; out[finite[-1] + 1:] = out[finite[-1]]
    return out


def apply_model(B, D, s, A, G):
    k = abs(s) / 100.0; d = -1 if s < 0 else 1
    return B + k * np.interp(B, CENT, A[d]) + (1.0 + k * np.interp(B, CENT, G[d])) * D


def smoothstep(e0, e1, x):
    u = np.clip((x - e0) / (e1 - e0), 0, 1); return u * u * (3 - 2 * u)
def soft_knee(dd, slope, width):
    width = max(width, 1e-6)
    return np.where(dd <= 0, dd, slope * dd + (1 - slope) * width * (1 - np.exp(-np.clip(dd, 0, None) / width)))
def current_op(B, D, s):
    k = abs(s) / 100.0; recovering = s < 0
    amplitude = -0.68 if recovering else 0.68
    detailBoost = 0.40 if recovering else 0.0
    wB = smoothstep(-2.1, 0.6, B)
    Bo = B + amplitude * k * wB
    if recovering:
        Bo = soft_knee(Bo, 1.0 - k * (1.0 - 0.12), 0.5)
    return Bo + (1.0 + detailBoost * k * wB) * D


def rms_eval(scenes, bases, Asm, Gsm, include_current=False):
    ss = {t: {s: 0.0 for s in SLIDERS} for t in ("model", "base", "cur")}
    cnt = {s: 0 for s in SLIDERS}
    for sc, (B, D, _) in zip(scenes, bases):
        clip_n = sc["clip_n"]; code_n = ev_to_code(sc["ev_n"])
        for s in SLIDERS:
            valid = (~clip_n) & (~sc[f"clip_{s}"])
            code_s = ev_to_code(sc[f"ev_{s}"])
            code_p = ev_to_code(apply_model(B, D, s, Asm, Gsm))
            ss["model"][s] += float(np.sum(((code_p - code_s)[valid]) ** 2))
            ss["base"][s]  += float(np.sum(((code_n - code_s)[valid]) ** 2))
            cnt[s] += int(valid.sum())
            if include_current:
                code_c = ev_to_code(current_op(B, D, s))
                ss["cur"][s] += float(np.sum(((code_c - code_s)[valid]) ** 2))
    def rms(d):
        tot = sum(d[s] for s in SLIDERS); N = sum(cnt.values())
        return {**{s: np.sqrt(d[s] / max(cnt[s], 1)) for s in SLIDERS}, "all": np.sqrt(tot / max(N, 1))}
    out = {"model": rms(ss["model"]), "base": rms(ss["base"]), "cnt": cnt}
    if include_current: out["current"] = rms(ss["cur"])
    return out


def main():
    t0 = time.time()
    scenes = load_all()
    print(f"loaded {len(scenes)} scenes, shape {scenes[0]['ev_n'].shape}  ({time.time()-t0:.0f}s)")
    results = {}
    for R in [4, 6, 8, 12, 16, 24]:
        bases = [(lambda B: (B, sc["ev_n"] - B, bin_idx(B)))(guided_base(sc["ev_n"], R)) for sc in scenes]
        curves = fit_curves(scenes, bases)
        Asm = {d: smooth_curve(curves[d][0], curves[d][2]) for d in (-1, 1)}
        Gsm = {d: smooth_curve(curves[d][1], curves[d][2]) for d in (-1, 1)}
        r = rms_eval(scenes, bases, Asm, Gsm, include_current=(R == 8))
        results[R] = (curves, Asm, Gsm, r)
        line = (f"R={R:2d} (full-res~{R*8:3d}px): model={r['model']['all']:.3f}  "
                f"nothing={r['base']['all']:.3f}")
        if 'current' in r: line += f"  current-op={r['current']['all']:.3f}"
        print(line + f"   ({time.time()-t0:.0f}s)")
    best = min(results, key=lambda R: results[R][3]['model']['all'])
    curves, Asm, Gsm, r = results[best]
    np.savez("fit_result.npz", radius=best, radius_fullres=best * 8, cent=CENT,
             A_neg=Asm[-1], A_pos=Asm[1], G_neg=Gsm[-1], G_pos=Gsm[1],
             A_neg_raw=curves[-1][0], A_pos_raw=curves[1][0],
             G_neg_raw=curves[-1][1], G_pos_raw=curves[1][1],
             n_neg=curves[-1][2], n_pos=curves[1][2])
    print(f"\nBest cache-radius R={best} (full-res ~{best*8}px). Per-slider RMS model vs nothing"
          + ("  vs current" if 'current' in r else ""))
    for s in SLIDERS:
        extra = f"  {r['current'][s]:5.2f}" if 'current' in r else ""
        print(f"  S={s:+4d}: {r['model'][s]:5.2f}  {r['base'][s]:5.2f}{extra}   (n={r['cnt'][s]/1e6:.1f}M)")
    print("saved fit_result.npz")


if __name__ == "__main__":
    main()
