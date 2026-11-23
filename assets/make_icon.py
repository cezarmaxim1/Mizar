"""
Builds Mizar.ico from assets/icon-source.jpg.

The source is a JPG with a fake "transparent" checkerboard baked in (40 px squares,
grey 56 / 105, dark square at 0,0). We rebuild real transparency:
  * stars (white / light grey / gold) -> opaque, with anti-aliased edges
  * the soft drop shadow -> semi-transparent black, recovered from how much
    darker each pixel is than the checker square that should be under it
  * everything else -> fully transparent

Usage:  python -I assets/make_icon.py
Outputs: Mizar.ico (16..256 px), assets/icon.png (512 px), assets/icon-preview.png
"""
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "assets" / "icon-source.jpg"

SQUARE, DARK, LIGHT = 40, 56.0, 105.0


def blur(a, sigma):
    """Separable Gaussian blur for 2-D float arrays."""
    r = max(1, int(sigma * 3))
    x = np.arange(-r, r + 1)
    k = np.exp(-(x ** 2) / (2 * sigma ** 2))
    k /= k.sum()
    a = np.apply_along_axis(lambda v: np.convolve(np.pad(v, r, mode="edge"), k, "valid"), 1, a)
    a = np.apply_along_axis(lambda v: np.convolve(np.pad(v, r, mode="edge"), k, "valid"), 0, a)
    return a


def max_filter(a, r):
    out = a.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            out = np.maximum(out, np.roll(np.roll(a, dy, 0), dx, 1))
    return out


def main():
    rgb = np.asarray(Image.open(SRC).convert("RGB")).astype(np.float64)
    h, w, _ = rgb.shape
    L = rgb.mean(axis=2)
    sat = rgb.max(axis=2) - rgb.min(axis=2)

    # expected checker colour under every pixel (slightly blurred, like the JPEG)
    yy, xx = np.mgrid[0:h, 0:w]
    ideal = np.where(((xx // SQUARE) + (yy // SQUARE)) % 2 == 0, DARK, LIGHT)
    B = blur(ideal, 1.0)

    # ---- stars -------------------------------------------------------------
    gold = sat > 40
    white = (L > 160) & ~gold
    solid = gold | white
    band = max_filter(solid.astype(np.float64), 3) > 0   # edge zone around the stars

    star_a = np.zeros((h, w))
    star_rgb = np.zeros((h, w, 3))
    star_a[solid] = 1.0
    star_rgb[solid] = rgb[solid]

    # local "pure" star colour near edges
    Lsolid = np.where(white, L, 0.0)
    F = max_filter(Lsolid, 4)
    edge = band & ~solid
    # white/grey edges: partially covering the checker
    we = edge & (sat <= 20) & (L > B + 6) & (F > 0)
    a_w = np.clip((L - B) / np.maximum(F - B, 1), 0, 1)
    star_a[we] = a_w[we]
    star_rgb[we] = np.repeat(F[we][:, None], 3, axis=1)
    # gold edges: saturation fades out into the grey checker
    ge = edge & (sat > 20)
    a_g = np.clip((sat - 6) / 94.0, 0, 1)
    star_a[ge] = a_g[ge]
    unmix = (rgb - (1 - a_g)[..., None] * B[..., None]) / np.maximum(a_g, 0.05)[..., None]
    star_rgb[ge] = np.clip(unmix[ge], 0, 255)

    # ---- shadow ------------------------------------------------------------
    sh = np.clip(1 - L / B, 0, 1)
    sh[(star_a > 0)] = 0
    sh[sh < 0.08] = 0                      # JPEG noise / checker seams, not shadow
    # the real shadow only exists close to the stars: keep it within ~90 px of them
    m = Image.fromarray((star_a > 0).astype(np.uint8) * 255).resize((w // 8, h // 8), Image.BOX)
    from PIL import ImageFilter
    m = m.point(lambda v: 255 if v > 0 else 0).filter(ImageFilter.MaxFilter(23))
    near = np.asarray(m.resize((w, h), Image.BILINEAR)).astype(np.float64) / 255.0
    sh *= near
    # Pixels on the checker seams (JPEG mixes both greys there) give false shadow:
    # ignore them and fill them in from their neighbours (normalised convolution).
    seam = (np.minimum(xx % SQUARE, SQUARE - 1 - xx % SQUARE) < 3) | \
           (np.minimum(yy % SQUARE, SQUARE - 1 - yy % SQUARE) < 3)
    valid = (~seam).astype(np.float64)
    sh = blur(sh * valid, 3.0) / np.maximum(blur(valid, 3.0), 1e-3)

    # The measured shadow is still JPEG-noisy. A drop shadow is just the star shape,
    # offset, blurred and faded - so fit those 4 numbers to the measurement and
    # render a clean shadow with them (same look as the original, no noise).
    k = 4  # fit at 1/4 resolution for speed
    sa = star_a[::k, ::k]
    meas = sh[::k, ::k]
    fitmask = (sa == 0) & (near[::k, ::k] > 0) & (~seam[::k, ::k])
    best = None
    for sigma in (4, 6, 8, 11, 14):
        base = blur(sa, sigma / 1.0)
        for dx in range(-2, 9, 2):
            for dy in range(0, 13, 2):
                cand = np.roll(np.roll(base, dy, 0), dx, 1)
                c, m_ = cand[fitmask], meas[fitmask]
                op = float((c * m_).sum() / max((c * c).sum(), 1e-9))
                err = float(((op * c - m_) ** 2).sum())
                if best is None or err < best[0]:
                    best = (err, sigma, dx, dy, op)
    _, sigma, dx, dy, op = best
    print(f"shadow fit: blur={sigma * k}px offset=({dx * k},{dy * k})px opacity={op:.2f}")
    sh = np.clip(op * np.roll(np.roll(blur(star_a, sigma * k), dy * k, 0), dx * k, 1), 0, 1)
    sh[star_a >= 1] = 0

    # ---- composite: star over shadow ---------------------------------------
    out_a = star_a + sh * (1 - star_a)
    out_rgb = np.where(out_a[..., None] > 0,
                       star_rgb * star_a[..., None] / np.maximum(out_a, 1e-6)[..., None], 0)
    rgba = np.dstack([np.clip(out_rgb, 0, 255), np.clip(out_a * 255, 0, 255)]).astype(np.uint8)
    img = Image.fromarray(rgba, "RGBA")

    # crop to content, pad to a centred square
    ys, xs = np.where(out_a > 0.03)
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    img = img.crop((x0, y0, x1 + 1, y1 + 1))
    side = int(max(img.size) * 1.04)
    sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    sq.paste(img, ((side - img.width) // 2, (side - img.height) // 2))

    # ---- frame inside a modern Obsidian Glass squircle tile ----------------
    size, pad = 512, 24
    tile = size - pad * 2
    rad = int(tile * 0.23)

    # supersampled 4x rounded-square mask
    s4, p4, r4 = size * 4, pad * 4, rad * 4
    mask = Image.new("L", (s4, s4), 0)
    ImageDraw.Draw(mask).rounded_rectangle([p4, p4, s4 - p4 - 1, s4 - p4 - 1], radius=r4, fill=255)
    mask = mask.resize((size, size), Image.LANCZOS)

    # Obsidian Glass diagonal gradient + subtle Mizar violet glow
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float64)
    t = np.clip((yy - pad) / tile, 0, 1)
    td = np.clip(0.75 * t + 0.25 * np.clip((xx - pad) / tile, 0, 1), 0, 1)
    top_rgb, bot_rgb, glow_rgb = (38.0, 38.0, 48.0), (18.0, 18.0, 24.0), (110.0, 92.0, 235.0)
    bg = np.zeros((size, size, 4), dtype=np.float64)
    for c in range(3):
        bg[..., c] = top_rgb[c] * (1 - td) + bot_rgb[c] * td
    bg[..., 3] = 255.0

    dist = np.sqrt(((xx - size * 0.48) / (tile * 0.55)) ** 2 + ((yy - size * 0.50) / (tile * 0.55)) ** 2)
    glow = np.clip(1.0 - dist, 0, 1) ** 2.2 * 0.22
    for c in range(3):
        bg[..., c] = bg[..., c] * (1 - glow) + glow_rgb[c] * glow

    # tile drop shadow + tile body
    big = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    sh_mask = mask.filter(ImageFilter.GaussianBlur(10))
    big.paste(Image.new("RGBA", (size, size), (0, 0, 0, 75)), (0, 6), sh_mask)
    tile_img = Image.fromarray(np.clip(bg, 0, 255).astype(np.uint8), "RGBA")
    tile_img.putalpha(mask)
    big.alpha_composite(tile_img)

    # subtle top-lit glass rim
    s2 = size * 2
    rim = Image.new("RGBA", (s2, s2), (0, 0, 0, 0))
    ImageDraw.Draw(rim).rounded_rectangle(
        [pad * 2 + 2, pad * 2 + 2, s2 - pad * 2 - 3, s2 - pad * 2 - 3],
        radius=rad * 2 - 2, outline=(255, 255, 255, 48), width=4
    )
    rim_arr = np.asarray(rim.resize((size, size), Image.LANCZOS)).astype(np.float64)
    rim_arr[..., 3] *= np.clip(1.0 - 0.65 * t, 0.3, 1.0)
    big.alpha_composite(Image.fromarray(np.clip(rim_arr, 0, 255).astype(np.uint8), "RGBA"))

    # Mizar stars centred inside the tile
    st_sz = int(tile * 0.78)
    big.alpha_composite(sq.resize((st_sz, st_sz), Image.LANCZOS), ((size - st_sz) // 2 + 4, (size - st_sz) // 2))

    big.save(ROOT / "assets" / "icon.png")
    sizes = [(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
    big.save(ROOT / "Mizar.ico", format="ICO", sizes=sizes)

    # preview on dark and light backgrounds, plus small sizes
    prev = Image.new("RGBA", (1200, 640), (0, 0, 0, 0))
    for i, bg in enumerate([(32, 32, 36, 255), (243, 243, 243, 255)]):
        tile = Image.new("RGBA", (600, 640), bg)
        tile.alpha_composite(big.resize((320, 320), Image.LANCZOS), (140, 30))
        x = 60
        for s in (16, 24, 32, 48, 64, 128):
            tile.alpha_composite(big.resize((s, s), Image.LANCZOS), (x, 400 + (128 - s) // 2))
            x += s + 30
        prev.alpha_composite(tile, (i * 600, 0))
    prev.save(ROOT / "assets" / "icon-preview.png")
    print("ok", sq.size)


if __name__ == "__main__":
    main()
