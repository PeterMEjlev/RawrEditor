"""
Faithful Python port of RAWR's output transform (BasicTone.DisplayCurve composed
with BasicTone.LightroomMatch) and its numerical inverse.

The pipeline maps scene-linear (normalised so 1.0 == sensor saturation) -> display
[0,1] -> *255. LocalHighlights operates upstream in scene-linear luminance EV about
middle grey, so to recover the EV a given LR JPEG pixel corresponds to we invert this
transform per channel, then take Rec.709 luminance in linear and log2 it about midgray.

Ported verbatim from src/Rawr.Develop/BasicTone.cs.
"""
import numpy as np

MIDDLE_GRAY  = 0.18
MIDTONE_LIFT = 0.70
BASE_CONTRAST = 0.18
TANH_SLOPE   = 2.0

# LrMatchLut, verbatim from BasicTone.cs lines 194-203 (65 control points).
LR_MATCH_LUT = np.array([
    0.000000, 0.004541, 0.009081, 0.013622, 0.018163, 0.022703, 0.027244, 0.031784,
    0.042006, 0.057890, 0.070311, 0.083424, 0.098665, 0.118914, 0.141383, 0.164969,
    0.189962, 0.216275, 0.243335, 0.270840, 0.296823, 0.323598, 0.350717, 0.377512,
    0.405154, 0.433715, 0.458124, 0.485849, 0.511346, 0.535160, 0.559946, 0.583656,
    0.607689, 0.631176, 0.654909, 0.678365, 0.700916, 0.722202, 0.743124, 0.764952,
    0.784913, 0.804290, 0.830618, 0.851641, 0.866516, 0.881291, 0.892721, 0.903191,
    0.912870, 0.921874, 0.930127, 0.940565, 0.952813, 0.957635, 0.961707, 0.966748,
    0.971063, 0.974976, 0.978781, 0.982341, 0.984641, 0.986756, 0.990113, 0.995056,
    1.000000,
], dtype=np.float64)


def display_curve(lin):
    """DisplayCurve: sRGB OETF -> midtone-lift power -> gentle base S. lin,out in [0,1]."""
    lin = np.clip(lin, 0.0, 1.0)
    srgb = np.where(lin <= 0.0031308, 12.92 * lin, 1.055 * np.power(lin, 1.0 / 2.4) - 0.055)
    p = np.power(srgb, MIDTONE_LIFT)
    tt = p * 2.0 - 1.0
    tanh_norm = np.tanh(TANH_SLOPE)
    s_curve = (np.tanh(tt * TANH_SLOPE) / tanh_norm + 1.0) * 0.5
    p = p * (1.0 - BASE_CONTRAST) + s_curve * BASE_CONTRAST
    return np.clip(p, 0.0, 1.0)


def lightroom_match(v):
    """LightroomMatch: piecewise-linear 65-pt LUT over input [0,1], fixed at 0 and 1."""
    v = np.clip(v, 0.0, 1.0)
    last = LR_MATCH_LUT.size - 1        # 64
    s = v * last
    i = np.floor(s).astype(np.int64)
    i = np.clip(i, 0, last - 1)
    f = s - i
    return LR_MATCH_LUT[i] * (1.0 - f) + LR_MATCH_LUT[i + 1] * f


def display_transform(lin):
    """Full output transform, scene-linear [0,1] -> display [0,1]."""
    return lightroom_match(display_curve(lin))


# ---- Numerical inverse: display [0,1] -> scene-linear [0,1] --------------------
# Both stages are monotonic increasing, so the composition is invertible on [0,1].
_LIN_GRID = np.linspace(0.0, 1.0, 1_000_001)
_DISP_GRID = display_transform(_LIN_GRID)
# Guard strict monotonicity for np.interp (ties at the clipped top are harmless).
_DISP_GRID = np.maximum.accumulate(_DISP_GRID)


def display_to_linear(disp):
    """Invert the output transform. disp in [0,1] (i.e. code/255)."""
    return np.interp(disp, _DISP_GRID, _LIN_GRID)


def code_to_ev_luma(rgb_code):
    """(...,3) uint8/float sRGB-display codes 0..255 -> scene-linear luminance EV
    about middle grey, matching LocalHighlights' I space. Returns (..., ) EV and a
    boolean 'clipped' mask (any channel at/near 255 => highlight info lost)."""
    disp = np.asarray(rgb_code, dtype=np.float64) / 255.0
    lin = display_to_linear(disp)                      # per channel
    y = 0.2126 * lin[..., 0] + 0.7152 * lin[..., 1] + 0.0722 * lin[..., 2]
    y = np.maximum(y, 1e-9)
    ev = np.log2(y / MIDDLE_GRAY)
    clipped = np.any(np.asarray(rgb_code) >= 253, axis=-1)
    return ev, clipped


if __name__ == "__main__":
    # Self-checks against values reasoned from BasicTone.cs.
    # DisplayCurve(1)=1, LightroomMatch(1)=1  => transform(1)=1.
    assert abs(display_transform(np.array([1.0]))[0] - 1.0) < 1e-9
    assert abs(display_transform(np.array([0.0]))[0] - 0.0) < 1e-9
    # Monotonic.
    xs = np.linspace(0, 1, 10001)
    ys = display_transform(xs)
    assert np.all(np.diff(ys) >= -1e-12), "transform not monotonic"
    # Round trip.
    d = np.linspace(0, 1, 5001)
    lin = display_to_linear(d)
    d2 = display_transform(lin)
    err = np.max(np.abs(d - d2))
    print(f"inverse round-trip max err (display units): {err:.3e}  ({err*255:.4f} codes)")
    # Middle grey scene-linear 0.18 -> what display code?
    mg = display_transform(np.array([0.18]))[0]
    print(f"scene middle grey 0.18 -> display {mg:.4f} = {mg*255:.1f}/255")
    # EV of a few codes (neutral grey rgb=(v,v,v)).
    for c in [64, 128, 190, 220, 245, 252]:
        ev, clip = code_to_ev_luma(np.array([[c, c, c]]))
        print(f"  code {c:3d} -> EV {ev[0]:+.3f}  clipped={clip[0]}")
    print("self-check OK")
