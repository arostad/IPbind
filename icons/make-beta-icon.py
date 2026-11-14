"""Generates app-beta.ico (IPbind Beta) from app.ico.

Each frame is decomposed in the original background / teal / white triangle,
then rebuilt with a dark-gray background (#2E2E30) and amber glyph (#FFC83D).
The white hub, edges, residual, and every alpha value stay unchanged.

Run from anywhere:  python3 icons/make-beta-icon.py   (needs Pillow + numpy)
"""
import os
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Original palette
B = np.array([0x22, 0x31, 0x42], float)  # #223142
T = np.array([94, 199, 192], float)      # teal nodes/lines
W = np.array([255, 255, 255], float)

# Beta palette (white is unchanged)
B2 = np.array([0x2E, 0x2E, 0x30], float)  # #2E2E30
T2 = np.array([0xFF, 0xC8, 0x3D], float)  # #FFC83D
W2 = W


def _project_segment(rgb, P, Q):
    d = Q - P
    u = np.clip(((rgb - P) @ d) / (d @ d), 0, 1)
    proj = P + u[..., None] * d
    err = np.linalg.norm(rgb - proj, axis=-1)
    return u, err


def remap(im):
    arr = np.array(im.convert('RGBA')).astype(float)
    rgb = arr[..., :3]
    alpha = arr[..., 3]
    h, w = rgb.shape[:2]
    shape = (h, w)

    dT = T - B
    dW = W - B
    A = np.stack([dT, dW], axis=1)
    AtA_inv = np.linalg.inv(A.T @ A)
    tw = np.tensordot(rgb - B, A.T, axes=([2], [1])) @ AtA_inv.T
    t_u, w_u = tw[..., 0], tw[..., 1]
    inside = (t_u >= -1e-6) & (w_u >= -1e-6) & (t_u + w_u <= 1 + 1e-6)
    t_i = np.clip(t_u, 0, 1)
    w_i = np.clip(w_u, 0, 1)
    recon_i = B + t_i[..., None] * dT + w_i[..., None] * dW
    err_i = np.linalg.norm(rgb - recon_i, axis=-1)
    err_i = np.where(inside, err_i, np.inf)

    u_bt, err_bt = _project_segment(rgb, B, T)
    u_bw, err_bw = _project_segment(rgb, B, W)
    u_tw, err_tw = _project_segment(rgb, T, W)

    errs = np.stack([err_i, err_bt, err_bw, err_tw], axis=-1)
    choice = np.argmin(errs, axis=-1)
    best_err = np.min(errs, axis=-1)

    b_w = np.zeros(shape)
    t_w = np.zeros(shape)
    w_w = np.zeros(shape)

    m = choice == 0
    t_w[m] = t_i[m]
    w_w[m] = w_i[m]
    b_w[m] = 1 - t_w[m] - w_w[m]

    m = choice == 1
    t_w[m] = u_bt[m]
    b_w[m] = 1 - t_w[m]

    m = choice == 2
    w_w[m] = u_bw[m]
    b_w[m] = 1 - w_w[m]

    m = choice == 3
    w_w[m] = u_tw[m]
    t_w[m] = 1 - w_w[m]

    b_w = np.clip(b_w, 0, 1)
    t_w = np.clip(t_w, 0, 1)
    w_w = np.clip(w_w, 0, 1)
    s = b_w + t_w + w_w
    s = np.where(s == 0, 1, s)
    b_w, t_w, w_w = b_w / s, t_w / s, w_w / s

    out = rgb + b_w[..., None] * (B2 - B) + t_w[..., None] * (T2 - T) + w_w[..., None] * (W2 - W)
    # Near-transparent rim pixels carry junk RGB; give them the new background
    out = np.where((alpha < 16)[..., None], B2, out)
    arr[..., :3] = np.clip(np.round(out), 0, 255)
    result = Image.fromarray(arr.astype(np.uint8), 'RGBA')
    opaque = alpha >= 16
    max_err = float(best_err[opaque].max()) if opaque.any() else 0.0
    return result, max_err


def qa(orig, rec, size):
    a = np.array(orig.convert('RGBA'))
    b = np.array(rec.convert('RGBA'))
    assert a.shape == b.shape, size
    assert np.array_equal(a[..., 3], b[..., 3]), 'alpha changed %s' % (size,)
    rgb_a = a[..., :3].astype(int)
    rgb_b = b[..., :3].astype(int)
    opaque = a[..., 3] >= 250
    teal_m = opaque & (np.linalg.norm(rgb_a.astype(float) - T, axis=-1) < 0.5)
    white_m = opaque & (np.linalg.norm(rgb_a.astype(float) - W, axis=-1) < 0.5)
    bg_m = opaque & (np.linalg.norm(rgb_a.astype(float) - B, axis=-1) < 0.5)
    if teal_m.any():
        delta = int(np.abs(rgb_b[teal_m] - T2).max())
        assert delta <= 1, 'teal not remapped %s delta=%s' % (size, delta)
    if white_m.any():
        delta = int(np.abs(rgb_a[white_m] - rgb_b[white_m]).max())
        assert delta <= 1, 'white shifted %s delta=%s' % (size, delta)
    if bg_m.any():
        delta = int(np.abs(rgb_b[bg_m] - B2).max())
        assert delta <= 1, 'bg not remapped %s delta=%s' % (size, delta)
    print(size, 'ok  teal=%d white=%d bg=%d' % (int(teal_m.sum()), int(white_m.sum()), int(bg_m.sum())))


def main():
    ico = Image.open(os.path.join(ROOT, 'app.ico'))
    frames = []
    for size in sorted(ico.ico.sizes()):
        src = ico.ico.getimage(size).convert('RGBA')
        rec, max_err = remap(src)
        qa(src, rec, size)
        print(size, 'max fit err (opaque-ish)', round(max_err, 1))
        frames.append(rec)
    dest = os.path.join(ROOT, 'app-beta.ico')
    frames[-1].save(dest, format='ICO', sizes=[f.size for f in frames], append_images=frames[:-1])
    print('wrote', dest, 'frames', [f.size for f in frames])


if __name__ == '__main__':
    main()
