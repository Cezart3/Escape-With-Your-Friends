"""The four ground layers, painted from code (#245).

    python tools/art/terrain.py <absolute path>/Assets/_Project/Art/Terrain

Writes Sand.png, Grass.png, Rock.png and Dirt.png at 512, each tiling seamlessly. TerrainGenerator
keeps a layer texture that is already there, so these are what the island wears; it only generates
its flat placeholder when one is missing. Plain Python with numpy, no Blender: a texture is a
picture, not a model.

Every layer keeps the mean colour the island was balanced on and adds what was missing: the sand
gets wind ripples and grains, the grass clumps, clover and dry patches, the dirt pebbles and
cracks, and the rock is basalt, dark and cracked into plates with lichen on it. The shader adds the
big variation, at forty metres, that no tile can carry (StylizedTerrain.shader).
"""

import os
import struct
import sys
import zlib

import numpy as np

SIZE = 512


# ------------------------------------------------------------------------------------- the noise

def value(cells, seed, size=SIZE):
    """Smooth noise in [0, 1] that wraps: a random grid of cells x cells, cubic between its points."""
    rng = np.random.default_rng(seed)
    grid = rng.random((cells, cells))
    t = np.arange(size) * cells / size
    i0 = np.floor(t).astype(int) % cells
    i1 = (i0 + 1) % cells
    f = t - np.floor(t)
    f = f * f * (3 - 2 * f)
    rows = grid[i0] * (1 - f)[:, None] + grid[i1] * f[:, None]
    return rows[:, i0] * (1 - f)[None, :] + rows[:, i1] * f[None, :]


def fbm(cells, seed, octaves=4, gain=0.5):
    """Octaves of value(), each twice as fine: still wraps, as every period divides the tile."""
    out, amp, total = np.zeros((SIZE, SIZE)), 1.0, 0.0
    for o in range(octaves):
        out += value(cells * 2 ** o, seed + o * 101) * amp
        total += amp
        amp *= gain
    return out / total


def cells(n, seed, warp=0.0):
    """Wrapped Voronoi: the distance to the nearest of n random points and to the second nearest,
    in tile units. f2 - f1 is near zero along every border, which is a crack; `warp` bends the
    borders with noise, so they are not a paving."""
    rng = np.random.default_rng(seed)
    pts = rng.random((n, 2))
    y, x = np.mgrid[0:SIZE, 0:SIZE] / SIZE
    if warp:
        x = (x + (fbm(6, seed + 1, 3) - 0.5) * warp) % 1.0
        y = (y + (fbm(6, seed + 2, 3) - 0.5) * warp) % 1.0
    f1 = np.full((SIZE, SIZE), 9.0)
    f2 = np.full((SIZE, SIZE), 9.0)
    owner = np.zeros((SIZE, SIZE), int)
    for k, (px, py) in enumerate(pts):
        dx = np.abs(x - px)
        dy = np.abs(y - py)
        d = np.hypot(np.minimum(dx, 1 - dx), np.minimum(dy, 1 - dy))
        closer = d < f1
        f2 = np.where(closer, f1, np.minimum(f2, d))
        owner = np.where(closer, k, owner)
        f1 = np.where(closer, d, f1)
    return f1, f2, owner


def dots(n, r_min, r_max, seed):
    """n round blobs at random places, wrapped: a mask in [0, 1] (soft edge) and the blob's own
    random number, for giving each pebble or clover leaf its own shade."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:SIZE, 0:SIZE] / SIZE
    mask = np.zeros((SIZE, SIZE))
    tone = np.zeros((SIZE, SIZE))
    for _ in range(n):
        px, py, r, t = rng.random(), rng.random(), rng.uniform(r_min, r_max), rng.random()
        dx = np.abs(x - px)
        dy = np.abs(y - py)
        d = np.hypot(np.minimum(dx, 1 - dx), np.minimum(dy, 1 - dy)) / r
        m = np.clip((1 - d) * 3, 0, 1)
        tone = np.where(m > mask, t, tone)
        mask = np.maximum(mask, m)
    return mask, tone


def hexc(h):
    return np.array([int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)])


def mix(a, b, t):
    t = np.clip(t, 0, 1)[..., None]
    return a * (1 - t) + b * t


def keep_mean(img, mean):
    """Shift the picture so its average is the colour the island was balanced on."""
    return np.clip(img - img.reshape(-1, 3).mean(0) + mean, 0, 1)


# ------------------------------------------------------------------------------------- the layers

def sand():
    # Wind ripples: stripes a few centimetres apart, bent by noise so no two are straight, crests
    # lit and troughs shaded.
    y, x = np.mgrid[0:SIZE, 0:SIZE] / SIZE
    bend = fbm(4, 11, 3) * 2.2 + fbm(8, 12, 2) * 0.6
    ripple = np.sin(TAU * (x * 14 + y * 5 + bend))
    ripple = np.sign(ripple) * np.abs(ripple) ** 0.6
    fade = np.clip(fbm(3, 13, 2) * 1.8 - 0.35, 0, 1)
    img = np.ones((SIZE, SIZE, 3)) * hexc("D4BE8C")
    img = mix(img, hexc("E8D6A6"), (ripple * 0.5 + 0.5) * 0.55 * fade)
    img = mix(img, hexc("B89E6C"), (0.5 - ripple * 0.5) * 0.45 * fade)
    img = mix(img, hexc("C2A672"), fbm(6, 14, 4) * 0.5)
    rng = np.random.default_rng(15)
    grain = rng.random((SIZE, SIZE))
    img = mix(img, hexc("F4EAD0"), (grain > 0.985) * 0.8)
    img = mix(img, hexc("8A7452"), (grain < 0.012) * 0.6)
    shells, tone = dots(26, 0.004, 0.008, 16)
    img = mix(img, mix(np.ones_like(img) * hexc("F6EEE0"), hexc("E8B8A0"), tone), shells * 0.9)
    return keep_mean(img, np.array([0.805, 0.735, 0.55]))


def grass():
    # Clumps of light and dark blades, clover in round patches, and dry yellow where the soil is thin.
    clump = fbm(16, 21, 4, 0.6)
    blade = value(128, 22) * 0.6 + value(256, 23) * 0.4
    img = mix(np.ones((SIZE, SIZE, 3)) * hexc("3E6A28"), hexc("6E9A3A"), clump * 1.2 - 0.1)
    img = mix(img, hexc("2A4A1A"), np.clip(0.45 - blade, 0, 1) * 1.6)
    img = mix(img, hexc("8AB850"), np.clip(blade - 0.62, 0, 1) * 2.0)
    dry = np.clip((fbm(4, 24, 3) - 0.56) * 4.5, 0, 1)
    img = mix(img, hexc("A89A50"), dry * (0.25 + 0.2 * blade))
    clover, tone = dots(60, 0.01, 0.022, 25)
    leaf = clover * (value(64, 26) > 0.35)
    img = mix(img, mix(np.ones_like(img) * hexc("4E8A34"), hexc("68A844"), tone), leaf * 0.85)
    flowers, _ = dots(18, 0.003, 0.005, 27)
    img = mix(img, hexc("F4F0E0"), flowers)
    return keep_mean(img, np.array([0.285, 0.41, 0.19]))


def rock():
    # Basalt: dark, cracked into plates, each plate its own shade, with grey-green lichen and the odd
    # rust stain. The volcano is made of it.
    f1, f2, owner = cells(26, 31, warp=0.16)
    rng = np.random.default_rng(32)
    shade = rng.random(26)[owner]
    crack = np.clip(1 - (f2 - f1) * 70, 0, 1) * np.clip((value(12, 37) - 0.25) * 3, 0, 1)
    fine = fbm(32, 33, 3)
    img = mix(np.ones((SIZE, SIZE, 3)) * hexc("3C3A3A"), hexc("5E5A56"), shade * 0.8 + fine * 0.5 - 0.15)
    img = mix(img, hexc("1A1818"), crack ** 1.5 * 0.9)
    # The lit upper edge of every plate, just inside its crack.
    y = np.mgrid[0:SIZE, 0:SIZE][0] / SIZE
    streak = np.sin(TAU * (y * 9 + fbm(4, 38, 3) * 1.5)) * 0.5 + 0.5
    img = mix(img, hexc("2C2A2A"), streak ** 4 * 0.35)
    lichen = np.clip((fbm(8, 34, 4) - 0.6) * 4, 0, 1) * (0.6 + 0.4 * value(96, 35))
    img = mix(img, hexc("7A8460"), lichen * 0.45)
    rust = np.clip((fbm(5, 36, 3) - 0.62) * 4, 0, 1)
    img = mix(img, hexc("6A4430"), rust * 0.5)
    return keep_mean(img, np.array([0.36, 0.35, 0.34]))


def dirt():
    # A trodden path: brown earth, fine cracks, pebbles with a lit top and a shadow under them.
    soil = fbm(8, 41, 4)
    img = mix(np.ones((SIZE, SIZE, 3)) * hexc("5E4630"), hexc("7E6040"), soil * 1.3 - 0.15)
    f1, f2, _ = cells(70, 42)
    img = mix(img, hexc("3A2A1C"), np.clip(1 - (f2 - f1) * 140, 0, 1) * 0.28 * (value(10, 46) > 0.45))
    stones, tone = dots(55, 0.005, 0.013, 43)
    stones *= np.clip((value(96, 47) - 0.2) * 4, 0, 1)
    img = mix(img, hexc("2E2218"), np.roll(stones, (3, 2), (0, 1)) * 0.5)
    img = mix(img, mix(np.ones_like(img) * hexc("6E6256"), hexc("9A8C78"), tone), stones * 0.9)
    return keep_mean(img, np.array([0.39, 0.305, 0.215]))


TAU = 2 * np.pi
LAYERS = [("Sand", sand), ("Grass", grass), ("Rock", rock), ("Dirt", dirt)]


def write_png(path, img):
    """An 8-bit RGB png, by hand, so nothing past numpy is needed."""
    data = (np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8)
    h, w, _ = data.shape
    raw = b"".join(b"\x00" + data[y].tobytes() for y in range(h))

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    for name, paint in LAYERS:
        img = paint()
        # A tile that wraps has the same left and right edges as its neighbours: check the seams.
        assert np.abs(img[:, 0] - img[:, -1]).mean() < 0.08 and np.abs(img[0] - img[-1]).mean() < 0.08, name
        write_png(os.path.join(out_dir, f"{name}.png"), img)
        print(f"[terrain] {name}.png mean {img.reshape(-1, 3).mean(0).round(3)}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
