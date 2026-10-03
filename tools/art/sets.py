"""The cutscenes' sets, from code (#287).

    blender -b --factory-startup -P tools/art/sets.py -- <absolute path>/Assets/_Project/Art/Casino/Models

Writes Sets.fbx, painted from the slots' ramp sheet like everything else. Every mesh is drawn in metres
in its own space, in Unity's frame through tables.u(), and CutsceneSets wears it at the origin:

- `Set_Attic`: Radu Voinea's attic, floor centred on the origin. A gable roof along x, the window in
  the +x gable (open, so the sun comes through it), the door in the -x one, a brick chimney, the
  table with the chart on it, a chair, a trunk, a shelf of jars and books, crates, a dust sheet over
  something, a hanging lantern, and Radu's photograph with his plane.
- `Set_TinBox` and `Set_TinLid`: the box the journal is found in. The box stands on y 0; the lid's
  origin is its hinge, so turning it about x opens it.
- `Set_Journal`: the flight journal, closed, lying on y 0.
- `Set_Radio`: the valve radio from the shelf, standing on y 0, its dial facing +z.
- `Set_Photo`: the photograph on the table at the end, standing on y 0, facing +z.
- `Set_Marisol`: Bogdan's fishing boat, keel on y 0, bow +z, about eleven metres.
- `Set_Reef`: the rocks she breaks on, the waterline at y 0.
- `Set_Crate`: a crate to sit on, 0.48 high, standing on y 0.
"""

import math
import os
import random
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import slots as S  # noqa: E402
import tables as T  # noqa: E402
import buildings as B  # noqa: E402
import items as I  # noqa: E402

u = T.u
box = B.box
log = B.log
ball = I.ball
TAU = math.tau

# The attic: floor 7.2 x 5.2, knee walls 0.9 high at z +-2.6, the ridge 3.1 up over z 0.
AX, AZ, KNEE, RIDGE = 3.6, 2.6, 0.9, 3.1


def roof_z(y):
    """How far from the ridge line the roof is at height y."""
    return AZ * (RIDGE - y) / (RIDGE - KNEE)


def slab(s, pts, thick, col, outline=False):
    """A flat polygon of Unity points made solid along its own normal."""
    a, b, c = (Vector(p) for p in pts[:3])
    n = (b - a).cross(c - a).normalized() * thick
    return B.prism(s, pts, [tuple(Vector(p) + n) for p in pts], col, outline)


def gable(s, x, polys, col):
    """A gable wall at x, in pieces: each a polygon of (z, y)."""
    for poly in polys:
        B.prism(s, [(x, y, z) for z, y in poly], [(x + (0.1 if x > 0 else -0.1), y, z) for z, y in poly], col)


# ------------------------------------------------------------------------------------------- attic

def attic(s):
    rng = random.Random(7)

    # Floorboards over a dark subfloor, so the gaps read.
    box(s, (-AX, -0.12, -AZ), (AX, -0.05, AZ), "DARK")
    z = -AZ
    while z < AZ - 0.01:
        w = 0.24 + rng.uniform(-0.03, 0.03)
        top = min(z + w, AZ)
        box(s, (-AX, -0.05, z + 0.012), (AX, rng.uniform(-0.004, 0.004), top), "BROWN")
        z = top

    # Knee walls: boards standing on end.
    for side in (-1, 1):
        zz = side * AZ
        box(s, (-AX, 0, min(zz, zz + side * 0.1)), (AX, KNEE, max(zz, zz + side * 0.1)), "DARK")
        x = -AX
        while x < AX - 0.01:
            w = 0.3 + rng.uniform(-0.04, 0.04)
            box(s, (x + 0.01, 0, min(zz - side * 0.03, zz)), (min(x + w, AX), KNEE, max(zz - side * 0.03, zz)),
                "BROWN")
            x += w
        box(s, (-AX, KNEE - 0.06, min(zz - side * 0.12, zz)), (AX, KNEE, max(zz - side * 0.12, zz)), "BROWN")

    # The two roof slopes: a dark sarking with boards over it on the inside, rafters and collar ties.
    for side in (-1, 1):
        quad = [(-AX - 0.1, KNEE, side * (AZ + 0.1)), (AX + 0.1, KNEE, side * (AZ + 0.1)),
                (AX + 0.1, RIDGE + 0.05, 0), (-AX - 0.1, RIDGE + 0.05, 0)]
        slab(s, quad if side > 0 else quad[::-1], 0.08, "DARK")
        t = 0.0
        while t < 0.98:
            t1 = min(t + 0.11 + rng.uniform(-0.01, 0.01), 1.0)
            y0, y1 = KNEE + (RIDGE - KNEE) * t, KNEE + (RIDGE - KNEE) * t1 - 0.012
            z0, z1 = side * roof_z(y0), side * roof_z(y1)
            board = [(-AX, y0, z0 - side * 0.02), (AX, y0, z0 - side * 0.02), (AX, y1, z1 - side * 0.02),
                     (-AX, y1, z1 - side * 0.02)]
            slab(s, board if side < 0 else board[::-1], 0.02, "BROWN")
            t = t1
    x = -AX + 0.3
    while x < AX:
        for side in (-1, 1):
            log(s, (x, KNEE - 0.05, side * (AZ - 0.12)), (x, RIDGE - 0.1, 0), 0.075, "BROWN", sides=4, outline=True)
        log(s, (x, 2.25, -roof_z(2.25) + 0.15), (x, 2.25, roof_z(2.25) - 0.15), 0.06, "BROWN", sides=4, outline=True)
        x += 1.15
    log(s, (-AX, RIDGE - 0.1, 0), (AX, RIDGE - 0.1, 0), 0.09, "BROWN", sides=4, outline=True)

    # The window gable (+x): four pieces round an open window, and its frame.
    wz, wy0, wy1 = 0.55, 1.45, 2.35
    z15, z24 = roof_z(wy0), roof_z(wy1)
    gable(s, AX, [
        [(-AZ, 0), (AZ, 0), (AZ, KNEE), (z15, wy0), (-z15, wy0), (-AZ, KNEE)],
        [(-z15, wy0), (-wz, wy0), (-wz, wy1), (-z24, wy1)],
        [(wz, wy0), (z15, wy0), (z24, wy1), (wz, wy1)],
        [(-z24, wy1), (z24, wy1), (0, RIDGE + 0.05)],
    ], "BROWN")
    fx = AX - 0.04
    for z0, z1, y0, y1 in ((-wz - 0.06, -wz, wy0, wy1), (wz, wz + 0.06, wy0, wy1),
                           (-wz - 0.06, wz + 0.06, wy0 - 0.06, wy0), (-wz - 0.06, wz + 0.06, wy1, wy1 + 0.06),
                           (-0.02, 0.02, wy0, wy1), (-wz, wz, (wy0 + wy1) / 2 - 0.02, (wy0 + wy1) / 2 + 0.02)):
        box(s, (fx - 0.06, y0, z0), (fx + 0.12, y1, z1), "CREAM", outline=True)
    box(s, (fx - 0.22, wy0 - 0.1, -wz - 0.12), (fx + 0.12, wy0 - 0.06, wz + 0.12), "CREAM", outline=True)

    # The door gable (-x): whole, with a plank door and a brass knob.
    gable(s, -AX, [[(-AZ, 0), (AZ, 0), (AZ, KNEE), (0, RIDGE + 0.05), (-AZ, KNEE)]], "BROWN")
    box(s, (-AX + 0.02, 0, -1.9), (-AX + 0.08, 1.85, -1.05), "DARK", outline=True)
    for k in range(4):
        box(s, (-AX + 0.06, 0.02, -1.86 + k * 0.2), (-AX + 0.11, 1.8, -1.68 + k * 0.2), "BROWN")
    ball(s, (-AX + 0.15, 0.95, -1.2), 0.04, "GOLD", segs=8, rings=6)

    # Radu's photograph on the door gable: sky, sand, a little bush plane.
    px, py, pz = -AX + 0.1, 1.55, 0.9
    box(s, (px, py - 0.28, pz - 0.36), (px + 0.04, py + 0.28, pz + 0.36), "BROWN", outline=True)
    box(s, (px + 0.03, py - 0.02, pz - 0.3), (px + 0.05, py + 0.22, pz + 0.3), "SKY")
    box(s, (px + 0.03, py - 0.22, pz - 0.3), (px + 0.05, py - 0.02, pz + 0.3), "STRAW")
    box(s, (px + 0.05, py + 0.02, pz - 0.14), (px + 0.06, py + 0.06, pz + 0.12), "DARK")
    box(s, (px + 0.05, py + 0.05, pz - 0.02), (px + 0.06, py + 0.07, pz + 0.02), "DARK")
    box(s, (px + 0.05, py + 0.035, pz - 0.2), (px + 0.06, py + 0.045, pz + 0.2), "DARK")
    box(s, (px + 0.05, py - 0.14, pz + 0.16), (px + 0.06, py + 0.02, pz + 0.2), "SKIN")

    # The chimney, brick, through the -z slope.
    box(s, (-1.75, 0, -1.35), (-1.15, RIDGE + 0.3, -0.85), "RED", outline=True)
    for y in [0.25 * k for k in range(1, 13)]:
        box(s, (-1.76, y, -1.36), (-1.14, y + 0.02, -0.84), "STONE")

    # A rug, the table, the chart on it, a chair.
    box(s, (-0.5, 0, -0.5), (2.1, 0.01, 1.7), "GOLD")
    box(s, (-0.42, 0.01, -0.42), (2.02, 0.016, 1.62), "RED")
    box(s, (0.05, 0.74, 0.15), (1.55, 0.8, 1.05), "BROWN", outline=True)
    box(s, (0.12, 0.62, 0.22), (1.48, 0.74, 0.98), "BROWN")
    for x in (0.12, 1.42):
        for z in (0.22, 0.92):
            box(s, (x, 0, z), (x + 0.07, 0.74, z + 0.07), "BROWN", outline=True)
    chart(s, (0.62, 0.8, 0.62))
    chair(s, (1.75, 0, 1.45))

    # The trunk, under the +z slope, its lock to the room.
    tx, tz = -2.6, 1.6
    box(s, (tx - 0.5, 0, tz - 0.3), (tx + 0.5, 0.5, tz + 0.3), "BROWN", outline=True)
    S.tube(s, [u(tx - 0.5, 0.5, tz), u(tx + 0.5, 0.5, tz)], 0.3, "BROWN", sides=10)
    for x in (tx - 0.35, tx + 0.35):
        box(s, (x - 0.03, 0, tz - 0.31), (x + 0.03, 0.5, tz + 0.31), "METAL")
    box(s, (tx - 0.06, 0.32, tz - 0.33), (tx + 0.06, 0.48, tz - 0.3), "GOLD")

    # The shelf on the -z knee wall: jars, books.
    sx0, sx1, sz0, sz1 = 0.3, 2.6, -AZ + 0.1, -AZ + 0.42
    for x in (sx0, sx1 - 0.05):
        box(s, (x, 0, sz0), (x + 0.05, 1.05, sz1), "BROWN", outline=True)
    for y in (0.02, 0.42, 0.82):
        box(s, (sx0, y, sz0), (sx1, y + 0.04, sz1), "BROWN", outline=True)
    x = sx0 + 0.1
    for col, h in (("NAVY", 0.28), ("RED", 0.31), ("LIME", 0.26), ("BROWN", 0.3), ("NAVY", 0.24), ("GOLD", 0.29)):
        box(s, (x, 0.06, sz0 + 0.05), (x + 0.06, 0.06 + h, sz1 - 0.05), col, outline=True)
        x += 0.07
    box(s, (x + 0.05, 0.06, sz0 + 0.05), (x + 0.33, 0.12, sz1 - 0.05), "RED", outline=True)
    for k, col in enumerate(("SKY", "SEA", "AMBER", "SKY", "LIME")):
        B.bottle(s, (sx0 + 1.2 + k * 0.2, 0.06, (sz0 + sz1) / 2), col, h=0.22 + 0.05 * (k % 2), r=0.055)
    for k, col in enumerate(("AMBER", "CREAM", "SEA")):
        B.bottle(s, (sx0 + 1.3 + k * 0.25, 0.46, (sz0 + sz1) / 2), col, h=0.2, r=0.07)

    # Crates in the corner by the window, a dust sheet over something tall, a pile of old suitcases.
    B.crate(s, (2.7, 0, -2.3), (3.4, 0.7, -1.6))
    B.crate(s, (2.8, 0.7, -2.2), (3.3, 1.1, -1.7))
    ball(s, (-1.0, 0.55, -1.75), (0.6, 0.6, 0.45), "CREAM", segs=10, rings=7, bumps=0.12, seed=3)
    ball(s, (-0.7, 0.35, -1.5), (0.35, 0.35, 0.3), "CREAM", segs=8, rings=6, bumps=0.15, seed=4)
    for k, (col, w, h) in enumerate((("BROWN", 0.7, 0.2), ("NAVY", 0.6, 0.18), ("RED", 0.5, 0.16))):
        y = sum((0.2, 0.18, 0.16)[:k])
        box(s, (-3.3, y, 0.2 - w / 2), (-2.85, y + h, 0.2 + w / 2), col, outline=True)
        box(s, (-3.05, y + h, 0.15), (-3.1, y + h + 0.04, 0.25), "DARK")

    # The lantern on its chain from the ridge.
    lx, ly, lz = 0.8, 2.15, 0.25
    log(s, (lx, RIDGE - 0.18, 0.0), (lx, ly + 0.28, lz), 0.012, "METAL", sides=4)
    I.lathe(s, [(0.0, 0.0), (0.11, 0.0), (0.12, 0.04), (0.0, 0.04)], "METAL", (lx, ly, lz), segs=8)
    I.lathe(s, [(0.0, 0.22), (0.12, 0.22), (0.05, 0.3), (0.0, 0.3)], "METAL", (lx, ly, lz), segs=8)
    for k in range(4):
        a = TAU * k / 4 + TAU / 8
        log(s, (lx + 0.1 * math.cos(a), ly, lz + 0.1 * math.sin(a)),
            (lx + 0.1 * math.cos(a), ly + 0.22, lz + 0.1 * math.sin(a)), 0.01, "METAL", sides=4)
    ball(s, (lx, ly + 0.11, lz), (0.08, 0.1, 0.08), "AMBER", segs=8, rings=6, outline=False)


def chart(s, at):
    """Radu's chart: a sheet with three islands and a dotted route through the reef."""
    x, y, z = at
    box(s, (x - 0.32, y, z - 0.24), (x + 0.32, y + 0.004, z + 0.24), "CREAM")
    for cx, cz, rx_, rz_, col in ((-0.17, 0.1, 0.09, 0.06, "STRAW"), (0.02, -0.08, 0.07, 0.08, "LIME"),
                                  (0.2, 0.09, 0.06, 0.05, "STONE")):
        ball(s, (x + cx, y + 0.005, z + cz), (rx_, 0.003, rz_), col, segs=10, rings=3, smooth=False, outline=False)
    ball(s, (x + 0.2, y + 0.008, z + 0.09), (0.015, 0.004, 0.015), "RED", segs=6, rings=3, outline=False)
    route = [(-0.28, 0.18), (-0.2, 0.0), (-0.08, -0.05), (0.02, 0.06), (0.12, 0.0), (0.2, 0.09)]
    for (ax, az), (bx, bz) in zip(route, route[1:]):
        for k in range(3):
            t = (k + 0.5) / 3
            px, pz = ax + (bx - ax) * t, az + (bz - az) * t
            box(s, (x + px - 0.008, y + 0.004, z + pz - 0.008), (x + px + 0.008, y + 0.006, z + pz + 0.008), "RED")
    for k in range(5):
        box(s, (x - 0.3 + 0.03 * k, y + 0.004, z - 0.22), (x - 0.29 + 0.03 * k, y + 0.006, z - 0.17), "BROWN")


def chair(s, at):
    x, y, z = at
    box(s, (x - 0.22, 0.44, z - 0.22), (x + 0.22, 0.48, z + 0.22), "BROWN", outline=True)
    for dx in (-0.2, 0.16):
        for dz in (-0.2, 0.16):
            box(s, (x + dx, 0, z + dz), (x + dx + 0.04, 0.44, z + dz + 0.04), "BROWN", outline=True)
    for dx in (-0.2, 0.16):
        box(s, (x + dx, 0.48, z + 0.16), (x + dx + 0.04, 0.95, z + 0.2), "BROWN", outline=True)
    for y in (0.62, 0.8, 0.92):
        box(s, (x - 0.2, y, z + 0.165), (x + 0.2, y + 0.04, z + 0.195), "BROWN")


# -------------------------------------------------------------------------------------- the props

def tin_box(s):
    box(s, (-0.2, 0, -0.13), (0.2, 0.12, 0.13), "TEAL", outline=True)
    box(s, (-0.19, 0.11, -0.12), (0.19, 0.121, 0.12), "DARK")
    box(s, (-0.2, 0.04, -0.131), (0.2, 0.06, 0.131), "GOLD")
    # The journal's spine and loose pages inside.
    box(s, (-0.15, 0.03, -0.09), (0.12, 0.09, 0.08), "CREAM")


def tin_lid(s):
    # Hinged on the back edge: the origin is the hinge, the lid lies forward along +z.
    box(s, (-0.205, 0, 0), (0.205, 0.03, 0.265), "TEAL", outline=True)
    box(s, (-0.12, 0.03, 0.08), (0.12, 0.034, 0.19), "GOLD")
    box(s, (-0.03, -0.02, 0.255), (0.03, 0.02, 0.27), "GOLD")


def journal(s):
    box(s, (-0.08, 0, -0.11), (0.08, 0.035, 0.11), "BROWN", outline=True)
    box(s, (-0.072, 0.004, -0.103), (0.083, 0.031, 0.103), "CREAM")
    box(s, (-0.085, 0, -0.112), (-0.07, 0.035, 0.112), "DARK")
    box(s, (0.02, 0.035, -0.11), (0.03, 0.036, 0.11), "RED")


def radio(s):
    box(s, (-0.23, 0, -0.11), (0.23, 0.26, 0.11), "BROWN", outline=True)
    S.tube(s, [u(-0.23, 0.26, 0), u(0.23, 0.26, 0)], 0.11, "BROWN", sides=12)
    box(s, (-0.19, 0.1, 0.105), (0.03, 0.3, 0.115), "CREAM")
    for k in range(6):
        box(s, (-0.18, 0.12 + k * 0.03, 0.114), (0.02, 0.13 + k * 0.03, 0.12), "DARK")
    box(s, (0.06, 0.18, 0.105), (0.2, 0.28, 0.118), "AMBER")
    box(s, (0.125, 0.19, 0.118), (0.13, 0.27, 0.122), "RED")
    for x in (0.09, 0.17):
        S.tube(s, [u(x, 0.1, 0.11), u(x, 0.1, 0.15)], 0.025, "DARK", sides=8)
    S.tube(s, [u(0.1, 0.37, -0.05), u(0.3, 0.6, -0.08)], 0.006, "METAL", sides=4)


def photo(s):
    box(s, (-0.16, 0, -0.02), (0.16, 0.24, 0.0), "BROWN", outline=True)
    box(s, (-0.14, 0.12, 0.0), (0.14, 0.22, 0.005), "SKY")
    box(s, (-0.14, 0.02, 0.0), (0.14, 0.12, 0.005), "STRAW")
    for k, col in enumerate(("RED", "LIME", "NAVY", "YELLOW", "SKIN")):
        x = -0.1 + k * 0.05
        box(s, (x - 0.012, 0.05, 0.005), (x + 0.012, 0.11, 0.009), col)
        box(s, (x - 0.009, 0.11, 0.005), (x + 0.009, 0.128, 0.009), "SKIN")
    S.tube(s, [u(0, 0.12, -0.02), u(0, 0.0, -0.12)], 0.01, "BROWN", sides=4)


# ------------------------------------------------------------------------------------------ boat

ZS = [-5.5, -5.0, -4.0, -3.0, -2.0, -1.0, 0.0, 1.0, 2.0, 3.0, 3.8, 4.5, 5.0, 5.35, 5.5]
DECK = 2.15


def beam(z):
    if z >= 0:
        return 1.7 * max(0.0, 1 - (z / 5.5) ** 2) ** 0.7 + 0.03
    return 1.7 - 0.3 * (z / 5.5) ** 2


def sheer(z):
    return 2.55 + 0.6 * (max(z, 0.0) / 5.5) ** 2 + 0.1 * (min(z, 0.0) / 5.5) ** 2


def keel(z):
    return 0.7 * (max(z - 2.0, 0.0) / 3.5) ** 2 + 0.5 * max(-z - 3.5, 0.0) / 2.0


def section(z):
    w, k, h = beam(z), keel(z), sheer(z)
    half = [(0.0, k), (0.4 * w, k + 0.2), (0.8 * w, k + 0.7), (w, max(1.5, k + 1.0)), (0.97 * w, h)]
    return [(-x, y, z) for x, y in reversed(half[1:])] + [(x, y, z) for x, y in half]


def marisol(s):
    rings = [section(z) for z in ZS]

    def paint(i, k, c):
        if c.y < 1.0:
            return "RED"
        if c.y < 1.2:
            return "WHITE"
        if c.y > sheer(c.z) - 0.18:
            return "CREAM"
        return "NAVY"
    import vehicles as V  # noqa: E402
    V.loft(s, rings, "NAVY", closed=False, caps=False, paint=paint)
    # The transom, and the inside of the bulwarks seen from the deck.
    stern = rings[0]
    B.prism(s, [(x, y, -5.52) for x, y, _ in stern], [(x, y, -5.42) for x, y, _ in stern], "NAVY", outline=True)
    plan = [(beam(z) * 0.94, z) for z in ZS[:-1]]
    outline = [(x, z) for x, z in plan] + [(-x, z) for x, z in reversed(plan)]
    B.prism(s, [(x, DECK - 0.1, z) for x, z in outline], [(x, DECK, z) for x, z in outline], "BROWN")
    for side in (-1, 1):
        rail = [(side * beam(z) * 0.97, sheer(z) + 0.02, z) for z in ZS[:-1]]
        S.tube(s, [u(*p) for p in rail], 0.05, "CREAM", sides=5, outline=True)
        for p in rail[1:-1]:
            box(s, (p[0] - 0.04, DECK, p[2] - 0.04), (p[0] + 0.04, p[1], p[2] + 0.04), "CREAM")
        B.sign_text(s, "MARISOL", 0.42, (side * (beam(-0.5) + 0.05), 1.95, -0.5), (side, 0), "CREAM", depth=0.04, res=3)
        for z in (-3.6, -2.6, 1.6):
            S.tube(s, [u(side * (beam(z) + 0.08), 1.9, z), u(side * (beam(z) + 0.08), 1.5, z)], 0.18, "DARK",
                   sides=8)
        ball(s, (side * (beam(4.6) + 0.02), sheer(4.6) - 0.15, 4.6), 0.07, "RED" if side < 0 else "LIME",
             segs=6, rings=4, outline=False)

    # The wheelhouse: cream walls, windows all round, a navy roof with a mast, a light and a horn.
    x0, x1, z0, z1, y0, y1 = -1.05, 1.05, -2.0, 0.3, DECK, DECK + 2.0
    box(s, (x0, y0, z0), (x1, y1, z1), "CREAM", outline=True)
    for k in range(3):
        xa = x0 + 0.12 + k * 0.64
        box(s, (xa, y0 + 1.15, z1), (xa + 0.55, y0 + 1.8, z1 + 0.03), "SKY")
    for side in (-1, 1):
        for k in range(2):
            za = z0 + 0.2 + k * 0.95
            box(s, (side * x1 - 0.015, y0 + 1.15, za), (side * x1 + 0.015, y0 + 1.8, za + 0.8), "SKY")
        box(s, (side * x1 - 0.02, y0, z0 + 0.15), (side * x1 + 0.02, y0 + 1.0, z0 + 0.15), "DARK")
        ring = [u(side * (x1 + 0.05), y0 + 0.75 + 0.3 * math.sin(TAU * k / 12), -0.85 + 0.3 * math.cos(TAU * k / 12))
                for k in range(13)]
        S.tube(s, ring, 0.07, "RED", sides=6, cap=False)
    box(s, (x0 - 0.18, y1, z0 - 0.18), (x1 + 0.18, y1 + 0.14, z1 + 0.3), "NAVY", outline=True)
    log(s, (0, y1 + 0.14, -1.2), (0, y1 + 2.2, -1.2), 0.05, "METAL", outline=True)
    box(s, (-0.6, y1 + 1.6, -1.25), (0.6, y1 + 1.66, -1.15), "METAL")
    ball(s, (0.6, y1 + 0.3, 0.2), (0.12, 0.12, 0.16), "CREAM", segs=8, rings=6)
    ball(s, (-0.55, y1 + 0.25, -1.6), 0.09, "AMBER", segs=6, rings=4, outline=False)

    # The foremast with its boom, the stern gantry, the net drum, rigging.
    log(s, (0, DECK, 2.8), (0, DECK + 5.6, 2.8), 0.09, "BROWN", outline=True)
    log(s, (0, DECK + 1.6, 2.8), (0, DECK + 2.7, -0.6), 0.06, "BROWN", outline=True)
    for side in (-1, 1):
        log(s, (side * 1.3, DECK, -4.9), (side * 0.15, DECK + 3.2, -4.9), 0.08, "METAL", outline=True)
    log(s, (-0.3, DECK + 3.2, -4.9), (0.3, DECK + 3.2, -4.9), 0.08, "METAL", outline=True)
    S.tube(s, [u(-0.9, DECK + 0.55, -3.7), u(0.9, DECK + 0.55, -3.7)], 0.5, "SEA", sides=12)
    for x in (-0.95, 0.95):
        S.tube(s, [u(x, DECK + 0.55, -3.7), u(x * 1.05, DECK + 0.55, -3.7)], 0.55, "METAL", sides=12)
    ball(s, (0.4, DECK + 0.3, -4.6), (0.6, 0.3, 0.4), "LIME", segs=8, rings=5, bumps=0.2, seed=9)
    for a, b in (((0, DECK + 5.5, 2.8), (0, sheer(5.4) + 0.1, 5.4)), ((0, DECK + 5.5, 2.8), (0, y1 + 2.1, -1.2)),
                 ((0, DECK + 5.4, 2.8), (1.2, sheer(2.8), 2.8)), ((0, DECK + 5.4, 2.8), (-1.2, sheer(2.8), 2.8)),
                 ((0, DECK + 3.1, -4.9), (0, y1 + 1.0, -2.0))):
        B.rope(s, [a, b], r=0.015, col="DARK")
    for k, (x, z) in enumerate(((0.9, -2.9), (-0.9, -3.0), (0.9, 1.4))):
        B.barrel(s, (x, DECK, z), 0.28, 0.75, col=("BLUE", "ORANGE", "BLUE")[k])
    B.crate(s, (-1.1, DECK, 0.7), (-0.4, DECK + 0.5, 1.4))


def reef(s):
    rng = random.Random(11)
    for cx, cz, w, h in ((0, 0, 3.0, 2.6), (2.6, 1.4, 2.0, 1.8), (-2.4, 1.0, 2.2, 2.2), (1.2, -2.2, 1.8, 1.6),
                         (-1.0, 2.6, 1.6, 1.4), (4.2, -0.6, 1.4, 1.2), (-4.0, -1.4, 1.8, 1.0)):
        B.rock(s, (cx - w / 2, -2.0, cz - w / 2), (cx + w / 2, h - 1.0, cz + w / 2), rng, out=0.5, cuts=4,
               col="STONE", moss=2.0, rnd=0.2)


def crate(s):
    B.crate(s, (-0.3, 0, -0.25), (0.3, 0.48, 0.25))


MODELS = [("Crate", crate), ("Attic", attic), ("TinBox", tin_box), ("TinLid", tin_lid), ("Journal", journal), ("Radio", radio),
          ("Photo", photo), ("Marisol", marisol), ("Reef", reef)]


def main(out_dir=None, only=None):
    S.P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "slots")
    mat = S.material(S.paint_texture(folder))
    objects = []
    for i, (name, make) in enumerate(MODELS):
        if only and name not in only:
            continue
        s = S.Sym()
        make(s)
        obj = S.finish(s, "Set_" + name, mat, fit=False, outline=0.012)
        print(f"[sets] {obj.name}: {sum(len(p.vertices) - 2 for p in obj.data.polygons)} tris")
        if out_dir is None:
            obj.location = (-(i % 4) * 12, (i // 4) * 12, 0)
        objects.append(obj)

    if out_dir is None:
        return objects

    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "Sets.fbx"), use_selection=True,
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z",
                             axis_up="Y", mesh_smooth_type="FACE", bake_space_transform=True, path_mode="RELATIVE")
    print(f"[sets] exported {len(objects)} meshes to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
