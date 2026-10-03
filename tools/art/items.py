"""Every item's ground model, from code (#283).

    blender -b --factory-startup -P tools/art/items.py -- <absolute path>/Assets/_Project/Art/Casino/Models

Writes Items.fbx, painted from the slots' ramp sheet (Textures/Symbols.png, written by slots.py), so the
items wear the slots' one material. One mesh per ItemDef id, `Itm_<id>`, lying the way it rests on the
ground: its foot on the origin's floor, centred, its longest side SIZE[id] metres. ItemArtFactory puts it
on the item's world prefab and photographs it for the bag's icon; CharacterSkin holds the same model,
shrunk to a hand.

Each one is drawn at about a metre in Unity's frame (through tables.u) and scaled down at the end, so
the numbers stay readable. Weapons are WeaponFactory's, not this file's.
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

u = T.u
box = B.box
log = B.log
TAU = math.tau


# -------------------------------------------------------------------------------------- helpers

def rx(deg):
    """Unity pitch: +y toward +z for positive degrees."""
    a = math.radians(deg)
    c, s_ = math.cos(a), math.sin(a)
    return lambda p: (p[0], p[1] * c - p[2] * s_, p[1] * s_ + p[2] * c)


def at(x, y, z):
    return lambda p: (p[0] + x, p[1] + y, p[2] + z)


def shaped(s, draw, *fns):
    """Whatever draw() adds, moved through fns in Unity's frame."""
    before = set(s.bm.verts)
    draw()
    for v in s.bm.verts:
        if v not in before:
            p = B.unity(v.co)
            for fn in fns:
                p = Vector(fn(p))
            v.co = u(*p)


def ball(s, c, r, col, segs=12, rings=8, smooth=True, outline=True, bumps=0.0, seed=0):
    rx_, ry_, rz_ = r if isinstance(r, tuple) else (r, r, r)
    return S.sphere(s, u(*c), (rx_, rz_, ry_), col, segs=segs, rings=rings, smooth=smooth, outline=outline,
                    bumps=bumps, seed=seed)


def lathe(s, profile, col, c=(0, 0, 0), segs=12, smooth=True, outline=True):
    """Revolved round Unity's y through c; profile is [(radius, height above c)]."""
    return T.ulathe(s, profile, col, c, segs=segs, smooth=smooth, outline=outline)


def pipe(s, pts, r, col, sides=6, outline=True):
    return S.tube(s, [u(*p) for p in pts], r, col, sides=sides, outline=outline)


def side(s, poly, depth, col, bevel=0.0):
    """A side outline [(z, y)] given `depth` across x: a boot, a jerrycan."""
    shaped(s, lambda: S.extrude(s, poly, depth, col, bevel=bevel), B.turn_y(90))


def flat(s, poly, depth, col):
    """A top-down outline [(x, z)] lying on the ground, `depth` thick: a pelt, a feather."""
    shaped(s, lambda: S.extrude(s, [(-x, -z) for x, z in poly], depth, col), rx(-90), at(0, depth / 2, 0))


# ---------------------------------------------------------------------------------------- food

def meat(cooked):
    def make(s):
        flesh = "BROWN" if cooked else "MELON"
        ball(s, (0, 0.22, -0.12), (0.3, 0.22, 0.38), flesh, bumps=0.05, seed=1)
        ball(s, (0.1, 0.24, -0.3), (0.18, 0.16, 0.2), flesh, outline=False, bumps=0.05, seed=2)
        if cooked:
            for z in (-0.36, -0.18, 0.0):
                y = 0.22 + 0.22 * math.sqrt(max(0.0, 1 - ((z + 0.12) / 0.38) ** 2)) - 0.012
                ball(s, (0, y, z), (0.22, 0.025, 0.03), "DARK", segs=8, rings=4, outline=False)
        else:
            ball(s, (0, 0.21, 0.17), (0.2, 0.16, 0.08), "CREAM", outline=False)
        log(s, (0, 0.2, 0.12), (0, 0.17, 0.5), 0.07, "CREAM", sides=8, outline=True)
        for x in (-0.06, 0.06):
            ball(s, (x, 0.17, 0.53), 0.075, "CREAM", segs=8, rings=6)
    return make


def fish(cooked):
    def make(s):
        col, belly = ("AMBER", "STRAW") if cooked else ("SKY", "CREAM")
        shaped(s, lambda: S.fish(s, (0.5, 0.16, 0.24), col, belly=belly), rx(-90), B.turn_y(90))
        if cooked:
            log(s, (0, 0, -0.95), (0, 0, 0.85), 0.025, "BROWN", sides=5)
            for z in (-0.22, 0.0, 0.22):
                ball(s, (0, 0.14, z), (0.2, 0.025, 0.03), "DARK", segs=8, rings=4, outline=False)
    return make


def coconut(s):
    shaped(s, lambda: S.coconut(s), rx(-90), at(-0.38, 0.32, 0))
    ball(s, (0.42, 0.42, 0.05), 0.42, "BROWN", segs=14, rings=9, bumps=0.06, seed=5)
    for k in range(3):
        a = k * TAU / 3
        ball(s, (0.42 + 0.09 * math.cos(a), 0.83, 0.05 + 0.09 * math.sin(a)), 0.045, "DARK", segs=6, rings=4,
             outline=False)


def bottle(s, glass, h=1.0):
    lathe(s, [(0.0, 0.0), (0.16, 0.0), (0.17, 0.04), (0.17, 0.5 * h), (0.14, 0.6 * h), (0.06, 0.72 * h),
              (0.055, 0.9 * h), (0.068, 0.92 * h), (0.068, 0.95 * h), (0.0, 0.95 * h)], glass, segs=14)
    lathe(s, [(0.0, 0.93 * h), (0.05, 0.93 * h), (0.05, 1.02 * h), (0.0, 1.02 * h)], "BROWN", segs=8)


def empty_bottle(s):
    bottle(s, "SKY")
    S.gloss(s, u(-0.08, 0.38, 0.14), 0.05)


def water_bottle(s):
    """A canteen in a cloth cover, on its strap."""
    disc = [(0.0, -0.13), (0.3, -0.13), (0.37, -0.07), (0.39, 0.0), (0.37, 0.07), (0.3, 0.13), (0.0, 0.13)]
    shaped(s, lambda: lathe(s, disc, "TEAL", segs=18), rx(90), at(0, 0.39, 0))
    shaped(s, lambda: lathe(s, [(0.393, -0.035), (0.397, 0.0), (0.393, 0.035)], "BROWN", segs=18, outline=False),
           rx(90), at(0, 0.39, 0))
    lathe(s, [(0.0, 0.0), (0.07, 0.0), (0.07, 0.12), (0.0, 0.12)], "METAL", c=(0, 0.76, 0), segs=8)
    lathe(s, [(0.0, 0.0), (0.09, 0.0), (0.09, 0.07), (0.0, 0.09)], "BLUE", c=(0, 0.88, 0), segs=8)
    arc = [(0.43 * math.cos(math.radians(a)), 0.39 + 0.43 * math.sin(math.radians(a)), 0) for a in range(-30, 211, 15)]
    pipe(s, arc, 0.025, "BROWN", sides=4, outline=False)


def grog(s):
    lathe(s, [(0.0, 0.0), (0.21, 0.0), (0.23, 0.05), (0.23, 0.42), (0.17, 0.52), (0.07, 0.6), (0.06, 0.8),
              (0.072, 0.82), (0.072, 0.85), (0.0, 0.85)], "AMBER", segs=14)
    lathe(s, [(0.234, 0.14), (0.234, 0.36)], "CREAM", segs=14, outline=False)
    B.sign_text(s, "XXX", 0.13, (0, 0.25, 0.236), (0, 1), "RED", depth=0.02)
    lathe(s, [(0.0, 0.83), (0.055, 0.83), (0.055, 0.93), (0.0, 0.93)], "BROWN", segs=8)
    lathe(s, [(0.075, 0.76), (0.08, 0.84), (0.062, 0.9), (0.0, 0.9)], "RED", segs=8, outline=False)
    S.gloss(s, u(-0.1, 0.3, 0.17), 0.05)


# --------------------------------------------------------------------------------------- loot

def flint(s):
    ball(s, (0, 0.28, 0), (0.5, 0.3, 0.4), "OBSIDIAN", segs=7, rings=5, smooth=False, bumps=0.15, seed=7)
    ball(s, (0.42, 0.12, 0.3), (0.2, 0.1, 0.16), "STONE", segs=6, rings=4, smooth=False, bumps=0.15, seed=8)


def pearl(s):
    """An open clam with its pearl."""
    ball(s, (0, 0.1, 0), (0.45, 0.11, 0.4), "CORAL", segs=14, rings=7)
    for k in range(-3, 4):
        a = k * 0.32
        log(s, (0, 0.13, -0.36), (0.42 * math.sin(a), 0.17, -0.36 + 0.75 * math.cos(a)), 0.012, "PINK", sides=4)
    shaped(s, lambda: ball(s, (0, 0.0, 0.0), (0.45, 0.09, 0.4), "CORAL", segs=14, rings=7),
           at(0, 0.08, 0.38), rx(-58), at(0, 0.12, -0.38))
    ball(s, (0, 0.27, 0.02), 0.17, "PEARL", segs=14, rings=9)
    S.gloss(s, u(-0.05, 0.38, 0.1), 0.05)


def antler(s):
    beam = [(0, 0.06, -0.45), (0.06, 0.1, -0.15), (0.1, 0.16, 0.12), (0.06, 0.24, 0.42)]
    pipe(s, beam, 0.075, "CREAM", sides=7)
    ball(s, (0, 0.06, -0.47), (0.09, 0.08, 0.06), "STRAW", segs=8, rings=5)
    for base, tip in (((0.06, 0.1, -0.15), (-0.18, 0.18, -0.02)), ((0.1, 0.16, 0.12), (0.34, 0.24, 0.24)),
                      ((0.08, 0.2, 0.28), (-0.12, 0.32, 0.46))):
        mid = tuple((a + b) / 2 + (0.0 if i != 1 else 0.05) for i, (a, b) in enumerate(zip(base, tip)))
        S.tube(s, [u(*base), u(*mid), u(*tip)], [0.06, 0.045, 0.018], "CREAM", sides=6)


def fang(s):
    pts = [(0, 0.1 + 0.25 * math.sin(math.pi * t * 0.5), -0.4 + 0.8 * t) for t in (k / 7 for k in range(8))]
    S.tube(s, [u(*p) for p in pts], [0.12 * (1 - t) + 0.008 for t in (k / 7 for k in range(8))], "CREAM", sides=8)
    ball(s, (0, 0.1, -0.4), (0.12, 0.12, 0.05), "STRAW", segs=8, rings=5)


def pelt(body, spots):
    def make(s):
        def r(a):
            k = 1.0
            for leg in (40, 140, 220, 320):
                d = (math.degrees(a) - leg + 180) % 360 - 180
                k += 0.75 * math.exp(-(d / 9) ** 2)
            d = (math.degrees(a) - 180 + 180) % 360 - 180
            k += 0.7 * math.exp(-(d / 5) ** 2)
            d = (math.degrees(a) + 180) % 360 - 180
            k += 0.15 * math.exp(-(d / 25) ** 2)
            return k
        poly = [(0.32 * r(a) * math.sin(a), 0.5 * r(a) * math.cos(a)) for a in (k * TAU / 64 for k in range(64))]
        flat(s, poly, 0.04, body)
        rng = random.Random(11)
        for _ in range(11 if spots else 5):
            x, z = rng.uniform(-0.22, 0.22), rng.uniform(-0.38, 0.38)
            if spots:
                ball(s, (x, 0.04, z), (0.065, 0.012, 0.065), "DARK", segs=8, rings=4, outline=False)
                ball(s, (x, 0.045, z), (0.038, 0.012, 0.038), "ORANGE", segs=6, rings=3, outline=False)
            else:
                ball(s, (x, 0.04, z), (0.1, 0.01, 0.08), "STONE", segs=8, rings=4, outline=False)
    return make


def feather(s):
    vane = [(0.16 * math.sin(math.pi * t) ** 0.7 * (1 if i < 9 else -1), t) for i, t in
            enumerate([k / 8 for k in range(9)] + [k / 8 for k in range(8, -1, -1)])]
    flat(s, [(x, z * 0.9 - 0.4) for x, z in vane[:9] + vane[9:]], 0.02, "TEAL")
    tip = [(0.13 * math.sin(math.pi * t) ** 0.6 * (1 if i < 7 else -1), t) for i, t in
           enumerate([k / 6 for k in range(7)] + [k / 6 for k in range(6, -1, -1)])]
    shaped(s, lambda: flat(s, [(x, z * 0.35 + 0.18) for x, z in tip], 0.02, "RED"), at(0, 0.004, 0))
    log(s, (0, 0.02, -0.55), (0, 0.02, 0.52), 0.012, "CREAM", sides=4)


def scrap_metal(s):
    shaped(s, lambda: box(s, (-0.3, 0, -0.35), (0.3, 0.03, 0.35), "METAL", outline=True), B.turn_z(12),
           at(-0.05, 0.08, 0))
    shaped(s, lambda: box(s, (-0.2, 0, -0.25), (0.2, 0.03, 0.25), "METAL", outline=True), B.turn_z(-35),
           B.turn_y(25), at(0.3, 0.12, 0.1))
    for x, z in ((-0.15, 0.1), (0.1, -0.2)):
        ball(s, (x, 0.15, z), (0.08, 0.01, 0.06), "AMBER", segs=8, rings=4, outline=False)
    log(s, (-0.35, 0.06, 0.3), (0.3, 0.06, 0.45), 0.06, "STONE", sides=8, outline=True)
    shaped(s, lambda: S.extrude(s, S.star(8, 0.2, 0.15, rot=0), 0.06, "AMBER"), rx(-90), at(0.15, 0.22, -0.25))
    for x, z in ((-0.25, -0.25), (0.25, -0.1), (-0.1, 0.25)):
        ball(s, (x, 0.12, z), 0.035, "DARK", segs=6, rings=4, outline=False)


def armour_kit(s):
    """A breastplate on its back: neck and arm holes, a ridge down the middle, rivets and buckled straps."""
    plate = [(-0.16, 0.5), (-0.42, 0.44), (-0.37, 0.18), (-0.31, 0.0), (-0.34, -0.38), (-0.2, -0.5), (0.2, -0.5),
             (0.34, -0.38), (0.31, 0.0), (0.37, 0.18), (0.42, 0.44), (0.16, 0.5), (0.08, 0.36), (-0.08, 0.36)]
    flat(s, plate, 0.1, "METAL")
    ball(s, (0, 0.1, -0.04), (0.28, 0.08, 0.4), "METAL", segs=14, rings=6)
    log(s, (0, 0.17, -0.42), (0, 0.17, 0.32), 0.022, "METAL", sides=6)
    for x in (-0.3, 0.3):
        box(s, (x - 0.06, 0.1, 0.3), (x + 0.06, 0.13, 0.62), "BROWN", outline=True)
        box(s, (x - 0.045, 0.13, 0.54), (x + 0.045, 0.15, 0.6), "GOLD")
    for k in range(10):
        a = k * TAU / 10
        ball(s, (0.3 * math.sin(a), 0.105, -0.06 + 0.4 * math.cos(a)), 0.022, "GOLD", segs=6, rings=4, outline=False)


def cloth(s):
    def roll():
        lathe(s, [(0.0, 0.0), (0.18, 0.0), (0.18, 0.8), (0.0, 0.8)], "CORAL", segs=14, smooth=True)
        for h in (0.2, 0.52):
            lathe(s, [(0.183, h), (0.183, h + 0.08)], "CREAM", segs=14, outline=False)
        for h in (0.08, 0.7):
            lathe(s, [(0.19, h), (0.19, h + 0.02)], "BROWN", segs=14, outline=False)
    shaped(s, roll, B.turn_z(90), B.turn_y(90), at(0, 0.18, -0.4))
    box(s, (-0.16, 0.0, 0.0), (0.16, 0.012, 0.45), "CORAL", outline=True)
    box(s, (-0.16, 0.012, 0.1), (0.16, 0.016, 0.16), "CREAM")


def bandage(s):
    shaped(s, lambda: lathe(s, [(0.0, -0.15), (0.3, -0.15), (0.3, 0.15), (0.0, 0.15)], "CREAM", segs=16),
           B.turn_z(90), at(0, 0.3, 0))
    box(s, (-0.14, 0.0, 0.0), (0.14, 0.014, 0.75), "CREAM", outline=True)
    box(s, (-0.08, 0.014, 0.47), (0.08, 0.024, 0.53), "RED")
    box(s, (-0.03, 0.014, 0.42), (0.03, 0.024, 0.58), "RED")


def torch(s):
    log(s, (0, 0.05, -0.6), (0, 0.07, 0.35), 0.045, "BROWN", sides=7, outline=True)
    ball(s, (0, 0.09, 0.5), (0.1, 0.1, 0.18), "STRAW", segs=10, rings=6, bumps=0.08, seed=4)
    ball(s, (0, 0.09, 0.37), (0.106, 0.106, 0.03), "DARK", segs=10, rings=4, outline=False)
    ball(s, (0, 0.1, 0.62), (0.075, 0.075, 0.07), "DARK", segs=8, rings=5, outline=False)


def boot(s):
    profile = [(-0.32, 0.06), (0.42, 0.06), (0.52, 0.12), (0.5, 0.22), (0.32, 0.3), (0.06, 0.36),
               (0.0, 0.78), (-0.3, 0.8), (-0.33, 0.4)]
    side(s, profile, 0.32, "BROWN", bevel=0.02)
    box(s, (-0.17, 0.0, -0.35), (0.17, 0.07, 0.54), "DARK", outline=True)
    box(s, (-0.165, 0.68, -0.32), (0.165, 0.78, 0.01), "AMBER")
    for k in range(4):
        y = 0.4 + 0.1 * k
        z = 0.05 - 0.013 * k
        box(s, (-0.1, y, z), (0.1, y + 0.025, z + 0.03), "CREAM")


def rope(s):
    turns = 3.2
    n = int(turns * 16)
    pts = []
    for k in range(n + 1):
        a = k * TAU / 16
        r = 0.36 - 0.02 * (k / 16)
        pts.append((r * math.cos(a), 0.06 + 0.1 * k / 16, r * math.sin(a)))
    a = n * TAU / 16
    pts.append((pts[-1][0] + 0.3 * -math.sin(a), 0.06, pts[-1][2] + 0.3 * math.cos(a)))
    S.tube(s, [u(*p) for p in pts], 0.06, "STRAW", sides=6)
    ball(s, pts[-1], 0.07, "STRAW", segs=6, rings=4, outline=False)


def plank(s):
    box(s, (-0.09, 0.0, -0.5), (0.09, 0.05, 0.5), "BROWN", outline=True)
    shaped(s, lambda: box(s, (-0.09, 0.0, -0.5), (0.09, 0.05, 0.5), "STRAW", outline=True), B.turn_y(14),
           at(0.04, 0.05, 0))
    for z in (-0.44, 0.44):
        for x in (-0.04, 0.04):
            ball(s, (x, 0.052, z), (0.012, 0.004, 0.012), "METAL", segs=6, rings=3, outline=False)


def boat_part(s):
    """A piece of hull: painted strakes on two ribs."""
    for k, a in enumerate(range(-60, 61, 24)):
        r = math.radians(a)
        x, y = 0.5 * math.sin(r), 0.5 - 0.5 * math.cos(r) + 0.04
        col = "RED" if abs(a) == 60 else "TEAL" if k % 2 else "SEA"
        shaped(s, lambda c=col: box(s, (-0.065, -0.018, -0.6), (0.065, 0.018, 0.6), c, outline=True),
               B.turn_z(a), at(x, y, 0))
    for z in (-0.38, 0.32):
        arc = [(0.47 * math.sin(math.radians(a)), 0.5 - 0.47 * math.cos(math.radians(a)) + 0.04, z)
               for a in range(-66, 67, 12)]
        pipe(s, arc, 0.035, "BROWN", sides=5)
    for z in (-0.5, 0.5):
        ball(s, (0.0, 0.03, z), 0.03, "METAL", segs=6, rings=4, outline=False)


# --------------------------------------------------------------------------------------- kits

def engine_kit(s):
    for x in (-0.3, 0.3):
        box(s, (x - 0.05, 0.0, -0.4), (x + 0.05, 0.06, 0.4), "BROWN", outline=True)
    for z in (-0.32, -0.1, 0.12, 0.34):
        box(s, (-0.38, 0.06, z - 0.08), (0.38, 0.1, z + 0.08), "STRAW", outline=True)
    T.ubox(s, (-0.25, 0.1, -0.28), (0.25, 0.48, 0.28), "DARK", bevel=0.03)
    for k in range(5):
        y = 0.5 + k * 0.045
        box(s, (-0.22, y, -0.25), (0.22, y + 0.02, 0.25), "METAL")
    for z in (-0.12, 0.12):
        lathe(s, [(0.0, 0.0), (0.03, 0.0), (0.03, 0.1), (0.015, 0.14), (0.0, 0.14)], "CREAM", c=(0, 0.72, z), segs=6)
    shaped(s, lambda: lathe(s, [(0.0, 0.0), (0.16, 0.0), (0.16, 0.06), (0.0, 0.06)], "METAL", segs=14),
           B.turn_z(90), at(-0.25, 0.3, 0.05))
    pipe(s, [(0.25, 0.3, -0.15), (0.38, 0.3, -0.15), (0.42, 0.2, -0.3), (0.42, 0.14, -0.45)], 0.04, "STONE")
    lathe(s, [(0.0, 0.0), (0.06, 0.0), (0.06, 0.06), (0.0, 0.07)], "RED", c=(0.12, 0.48, 0.18), segs=8)


def tank_kit(s):
    body = [(0.0, 0.0), (0.18, 0.02), (0.26, 0.08), (0.28, 0.16), (0.28, 0.74), (0.26, 0.82), (0.18, 0.88),
            (0.0, 0.9)]
    shaped(s, lambda: lathe(s, body, "RED", segs=16), B.turn_z(90), B.turn_y(90), at(0, 0.36, -0.45))
    for z in (-0.22, 0.22):
        shaped(s, lambda: lathe(s, [(0.285, -0.03), (0.285, 0.03)], "METAL", segs=16, outline=False), rx(90),
               at(0, 0.36, z))
        box(s, (-0.24, 0.0, z - 0.06), (0.24, 0.14, z + 0.06), "BROWN", outline=True)
    lathe(s, [(0.0, 0.0), (0.06, 0.0), (0.06, 0.06), (0.08, 0.06), (0.08, 0.1), (0.0, 0.1)], "METAL",
          c=(0, 0.62, -0.1), segs=8)
    ball(s, (0.0, 0.64, 0.12), (0.07, 0.02, 0.07), "CREAM", segs=10, rings=4)
    box(s, (-0.005, 0.66, 0.12), (0.04, 0.67, 0.13), "DARK")


def fuel(s):
    """A jerrycan."""
    T.ubox(s, (-0.2, 0.0, -0.4), (0.2, 1.0, 0.4), "RED", bevel=0.05)
    for x in (-0.205, 0.205):
        for a in (40, -40):
            shaped(s, lambda: box(s, (-0.012, -0.03, -0.42), (0.012, 0.03, 0.42), "RUBY"), rx(a), at(x, 0.5, 0))
    for z in (-0.35, -0.12, 0.1):
        box(s, (-0.04, 1.0, z - 0.03), (0.04, 1.12, z + 0.03), "RED")
    box(s, (-0.045, 1.1, -0.39), (0.045, 1.16, 0.14), "RED", outline=True)
    lathe(s, [(0.0, 0.0), (0.08, 0.0), (0.08, 0.14), (0.1, 0.14), (0.1, 0.2), (0.0, 0.2)], "METAL",
          c=(0, 0.98, 0.28), segs=10)
    box(s, (-0.21, 0.42, 0.18), (0.21, 0.62, 0.32), "YELLOW")


def tyre_kit(s):
    tread = [(0.27, 0.03), (0.44, 0.0), (0.5, 0.07), (0.5, 0.23), (0.44, 0.3), (0.27, 0.27), (0.25, 0.15),
             (0.27, 0.03)]
    lathe(s, tread, "DARK", segs=22)
    for k in range(22):
        shaped(s, lambda: box(s, (0.49, 0.06, -0.035), (0.53, 0.24, 0.035), "DARK"), B.turn_y(k * 360 / 22 + 8))
    lathe(s, [(0.0, 0.2), (0.1, 0.2), (0.16, 0.16), (0.27, 0.16), (0.27, 0.05)], "METAL", segs=16, outline=False)
    for k in range(5):
        a = k * TAU / 5
        ball(s, (0.08 * math.sin(a), 0.205, 0.08 * math.cos(a)), 0.018, "DARK", segs=6, rings=3, outline=False)
    lathe(s, [(0.0, 0.2), (0.045, 0.2), (0.04, 0.24), (0.0, 0.25)], "METAL", segs=8)


def fishing_rod(s):
    n = 10
    pts = [(0, 0.04 + 0.01 * t, -0.9 + 1.9 * t) for t in (k / n for k in range(n + 1))]
    S.tube(s, [u(*p) for p in pts], [0.035 - 0.022 * k / n for k in range(n + 1)], "STRAW", sides=6)
    for k in range(1, n, 2):
        y, z = pts[k][1], pts[k][2]
        ball(s, (0, y, z), (0.037 - 0.022 * k / n, 0.037 - 0.022 * k / n, 0.012), "BROWN", segs=6, rings=3,
             outline=False)
    log(s, (0, 0.04, -0.92), (0, 0.042, -0.6), 0.045, "DARK", sides=6)
    shaped(s, lambda: lathe(s, [(0.0, 0.0), (0.09, 0.0), (0.09, 0.05), (0.0, 0.05)], "METAL", segs=12),
           B.turn_z(90), at(0.06, 0.1, -0.5))
    pipe(s, [(0, 0.05, 1.0), (0.02, 0.06, 1.08), (0.04, 0.06, 1.14)], 0.004, "CREAM", sides=3, outline=False)
    ball(s, (0.04, 0.06, 1.18), 0.045, "RED", segs=8, rings=5)
    ball(s, (0.04, 0.03, 1.18), (0.047, 0.03, 0.047), "WHITE", segs=8, rings=4, outline=False)


def ammo(round_col, tip_col, r, length, label, case="CREAM", shells=False):
    def make(s):
        pitch = r * 2.4
        w, d, h = pitch * 5 / 2 + 0.03, pitch + 0.03, length * 0.6
        T.ubox(s, (-w, 0.0, -d), (w, h, d), case, bevel=0.01)
        box(s, (-w - 0.002, h * 0.25, -d - 0.002), (w + 0.002, h * 0.7, d + 0.002), "RED" if case == "CREAM" else "CREAM")
        B.sign_text(s, label, h * 0.32, (0, h * 0.47, d + 0.004), (0, 1), "CREAM" if case == "CREAM" else "DARK",
                    depth=0.01)
        for row in (-0.5, 0.5):
            for i in range(5):
                c = (-w + 0.03 + pitch * (i + 0.5), h * 0.3, row * pitch)
                if shells:
                    lathe(s, [(0.0, 0.0), (r, 0.0), (r, length * 0.25), (0.0, length * 0.25)], "GOLD", c=c, segs=8,
                          outline=False)
                    lathe(s, [(0.0, length * 0.25), (r * 0.95, length * 0.25), (r * 0.95, length), (0.0, length)],
                          round_col, c=c, segs=8, outline=False)
                else:
                    lathe(s, [(0.0, 0.0), (r, 0.0), (r, length * 0.62), (0.0, length * 0.62)], round_col, c=c, segs=6,
                          outline=False)
                    lathe(s, [(0.0, length * 0.62), (r * 0.95, length * 0.62), (r * 0.75, length * 0.86),
                              (0.0, length)], tip_col, c=c, segs=6, outline=False)
    return make


# ---------------------------------------------------------------------------------------- export

# id -> (maker, longest side in metres)
ITEMS = {
    "meat_raw": (meat(False), 0.28),
    "meat_cooked": (meat(True), 0.28),
    "fish_raw": (fish(False), 0.38),
    "fish_cooked": (fish(True), 0.36),
    "coconut": (coconut, 0.32),
    "empty_bottle": (empty_bottle, 0.28),
    "water_bottle": (water_bottle, 0.3),
    "grog": (grog, 0.3),
    "flint": (flint, 0.16),
    "pearl": (pearl, 0.1),
    "antler": (antler, 0.4),
    "fang": (fang, 0.1),
    "hide": (pelt("BROWN", False), 0.6),
    "jaguar_pelt": (pelt("YELLOW", True), 0.7),
    "feather": (feather, 0.22),
    "scrap_metal": (scrap_metal, 0.4),
    "armour_kit": (armour_kit, 0.55),
    "cloth": (cloth, 0.45),
    "bandage": (bandage, 0.14),
    "torch": (torch, 0.5),
    "boot": (boot, 0.3),
    "rope": (rope, 0.35),
    "plank": (plank, 0.9),
    "boat_part": (boat_part, 1.2),
    "engine_kit": (engine_kit, 0.6),
    "tank_kit": (tank_kit, 0.6),
    "fuel": (fuel, 0.4),
    "tyre_kit": (tyre_kit, 0.6),
    "fishing_rod": (fishing_rod, 1.0),
    "pistol_ammo": (ammo("GOLD", "AMBER", 0.05, 0.3, "9MM"), 0.1),
    "rifle_ammo": (ammo("GOLD", "AMBER", 0.045, 0.55, "7.62", case="SEA"), 0.12),
    "shotgun_shell": (ammo("RED", "RED", 0.07, 0.4, "12G", case="NAVY", shells=True), 0.12),
}


def fit(s, size):
    """Centred on x and z, standing on y 0, its longest side `size` metres."""
    pts = [B.unity(v.co) for v in s.bm.verts]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    k = size / max(hi - lo)
    mid = Vector(((lo.x + hi.x) / 2, lo.y, (lo.z + hi.z) / 2))
    for v in s.bm.verts:
        v.co = u(*((B.unity(v.co) - mid) * k))
    return k


def main(out_dir=None, only=None):
    S.P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "slots")
    mat = S.material(S.paint_texture(folder))
    objects = []
    for i, (name, (make, size)) in enumerate(ITEMS.items()):
        if only and name not in only:
            continue
        s = S.Sym()
        make(s)
        fit(s, size)
        obj = S.finish(s, "Itm_" + name, mat, fit=False, outline=max(0.0025, 0.012 * size))
        tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        print(f"[items] {obj.name}: {tris} tris")
        if out_dir is None:
            obj.location = (-(i % 8) * 1.2, (i // 8) * 1.2, 0)
        objects.append(obj)

    if out_dir is None:
        return objects

    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "Items.fbx"), use_selection=True,
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z",
                             axis_up="Y", mesh_smooth_type="FACE", bake_space_transform=True, path_mode="RELATIVE")
    print(f"[items] exported {len(objects)} meshes to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
