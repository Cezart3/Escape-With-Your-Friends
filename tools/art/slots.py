"""Slot machine symbols, modelled from code (#252).

    blender -b --factory-startup -P tools/art/slots.py -- <absolute path>/Assets/_Project/Art/Casino/Models

Writes SlotSymbols.fbx (one mesh per symbol, named `Sym_<Look>` after SlotFactory's looks),
SlotCabinets.fbx (per game `Cab_<Kind>` and its three bulb groups `Cab_<Kind>_Bulbs<n>`) and
Textures/Symbols.png, a 32-column ramp sheet everything is painted from: one material for the slots. No folder: a preview grid in
the open Blender.

A symbol is a handful of parts - spheres, lathes, extruded outlines, tubes - each painted from one ramp
column, dark at its foot and light at its top, so every fruit and gem has the candy gradient a slot
reel wants. Parts that make the silhouette get an inverted-hull outline: a copy pushed out along its
normals and turned inside out, in near-black, which the back-face culling draws as a rim. Round
things get a white gloss blob up and to the left, where the casino's lights would catch them.

Everything faces -Y here, which is a cabinet's +Z in Unity: the seven reads the right way round.
"""

import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import palms as P  # noqa: E402

TAU = math.tau

# ------------------------------------------------------------------------------------- textures

COLUMNS = 32
RAMPS = [
    ("RED", ["4A0008", "C8101E", "FF6A5A"]),
    ("GOLD", ["5A3404", "D89A18", "FFF2A8"]),
    ("LIME", ["2A5208", "7CC22A", "E0F88A"]),
    ("BROWN", ["24120A", "6A4022", "B0845A"]),
    ("CREAM", ["A8A090", "F2EEE2", "FFFFFF"]),
    ("ORANGE", ["7A2604", "F07A14", "FFD070"]),
    ("PINK", ["8A2238", "F0607A", "FFC0C8"]),
    ("YELLOW", ["7A5A04", "F2C81C", "FFF68A"]),
    ("LEAF", ["0E300E", "2E8A2E", "9AE06A"]),
    ("MELON", ["7A0614", "F03848", "FFA8B0"]),
    ("DARK", ["050508", "16161E", "34343E"]),
    ("OBSIDIAN", ["08060E", "2A2640", "7A74A8"]),
    ("JADE", ["063020", "22A060", "B0F8D0"]),
    ("AMBER", ["5A2002", "E8861A", "FFE090"]),
    ("RUBY", ["3A000C", "D0103A", "FF9AB8"]),
    ("PEARL", ["8A8A9A", "E6E2EE", "FFFFFF"]),
    ("TEAL", ["06343E", "2A9AAA", "A8F0F8"]),
    ("LAVA", ["5A0600", "FF4A00", "FFF09A"]),
    ("STONE", ["181414", "463632", "8E6E5E"]),
    ("PURPLE", ["220838", "8A30B0", "E0B0F8"]),
    ("BLUE", ["061444", "2A5AD0", "A8C8FF"]),
    ("SKY", ["22404E", "7AB0C8", "E0F6FF"]),
    ("CORAL", ["7A3E44", "F0A0A8", "FFE8E8"]),
    ("SKIN", ["6A3E22", "E0A070", "FFE0BE"]),
    ("STRAW", ["6A4E12", "D8B050", "FFF4B0"]),
    ("WHITE", ["F0F0F0", "FFFFFF", "FFFFFF"]),
    ("OUTLINE", ["000000", "08040C", "120A16"]),
    ("METAL", ["30303A", "9A9AAA", "F6F6FF"]),
    ("MAGENTA", ["5A0634", "E0208A", "FFA0D8"]),
    ("KIWI", ["30500A", "8AC030", "E8FCA0"]),
    ("SEA", ["06342A", "1A8A6A", "88ECCC"]),
    ("NAVY", ["040A24", "1A2A6A", "4A6AB0"]),
]
COL = {name: i for i, (name, _) in enumerate(RAMPS)}


def paint_texture(folder):
    os.makedirs(folder, exist_ok=True)
    stops = [[(0.0, a), (0.55, b), (1.0, c)] for _, (a, b, c) in RAMPS]

    def pixel(x, v):
        return P.ramp(stops[min(x // P.CELL, COLUMNS - 1)], v)

    path = os.path.join(folder, "Symbols.png")
    P.write_png(path, P.CELL * COLUMNS, P.TEX_H, pixel)
    return path


# -------------------------------------------------------------------------------------- builder

class Sym:
    """One symbol under construction: a bmesh whose faces carry a ramp column, a part and a flag."""

    def __init__(self):
        self.bm = bmesh.new()
        self.col = self.bm.faces.layers.int.new("col")
        self.part = self.bm.faces.layers.int.new("part")
        self.parts = 0
        self.outlined = set()

    def begin(self):
        return set(self.bm.faces)

    def end(self, before, col, smooth, outline=True):
        faces = [f for f in self.bm.faces if f not in before]
        p = self.parts
        self.parts += 1
        for f in faces:
            f[self.col] = COL[col]
            f[self.part] = p
            f.smooth = smooth
        if outline:
            self.outlined.add(p)
        return faces


def sphere(s, c, r, col, segs=12, rings=8, smooth=True, outline=True, rot=None, bumps=0.0, seed=0):
    before = s.begin()
    ret = bmesh.ops.create_uvsphere(s.bm, u_segments=segs, v_segments=rings, radius=1.0)
    rng = random.Random(seed)
    rm = rot or Matrix.Identity(3)
    rr = r if isinstance(r, (tuple, list, Vector)) else (r, r, r)
    for v in ret["verts"]:
        k = 1 + (rng.uniform(-bumps, bumps) if bumps else 0)
        v.co = rm @ Vector((v.co.x * rr[0] * k, v.co.y * rr[1] * k, v.co.z * rr[2] * k)) + Vector(c)
    return s.end(before, col, smooth, outline)


def lathe(s, profile, col, segs=12, c=(0, 0, 0), axis="z", smooth=True, outline=True, rot=None, twist=0.0):
    """Revolve [(radius, height)] round an axis. Radius 0 at an end closes it with a point."""
    before = s.begin()
    rm = rot or Matrix.Identity(3)
    c = Vector(c)

    def place(x, y, h):
        # Along y a negative height points at the viewer.
        p = Vector((x, y, h)) if axis == "z" else Vector((x, h, y)) if axis == "y" else Vector((h, x, y))
        return rm @ p + c

    rings = []
    for i, (r, h) in enumerate(profile):
        if r < 1e-6:
            rings.append([s.bm.verts.new(place(0, 0, h))])
        else:
            off = twist * i
            rings.append([s.bm.verts.new(place(r * math.cos(k * TAU / segs + off), r * math.sin(k * TAU / segs + off), h))
                          for k in range(segs)])
    for a, b in zip(rings, rings[1:]):
        if len(a) == 1 and len(b) == 1:
            continue
        for k in range(segs):
            j = (k + 1) % segs
            if len(a) == 1:
                s.bm.faces.new((a[0], b[j], b[k]))
            elif len(b) == 1:
                s.bm.faces.new((a[k], a[j], b[0]))
            else:
                s.bm.faces.new((a[k], a[j], b[j], b[k]))
    faces = s.end(before, col, smooth, outline)
    bmesh.ops.recalc_face_normals(s.bm, faces=faces)
    return faces


def extrude(s, poly, depth, col, y=0.0, bevel=0.0, dome=0.0, smooth=False, outline=True, rot=None, c=(0, 0, 0)):
    """A 2-D outline [(x, z)] in the picture plane, given `depth` toward the back. `dome` pushes the
    front's middle toward the viewer (a faceted star or gem)."""
    before = s.begin()
    rm = rot or Matrix.Identity(3)
    c = Vector(c)

    def place(x, yy, z):
        return rm @ Vector((x, yy, z)) + c

    front = [s.bm.verts.new(place(x, y - depth / 2, z)) for x, z in poly]
    back = [s.bm.verts.new(place(x, y + depth / 2, z)) for x, z in poly]
    n = len(poly)
    for k in range(n):
        j = (k + 1) % n
        s.bm.faces.new((front[k], front[j], back[j], back[k]))
    if dome:
        cx = sum(x for x, _ in poly) / n
        cz = sum(z for _, z in poly) / n
        tip = s.bm.verts.new(place(cx, y - depth / 2 - dome, cz))
        for k in range(n):
            s.bm.faces.new((front[k], tip, front[(k + 1) % n]))
        tail = s.bm.verts.new(place(cx, y + depth / 2 + dome * 0.4, cz))
        for k in range(n):
            s.bm.faces.new((back[(k + 1) % n], tail, back[k]))
    else:
        s.bm.faces.new(front[::-1])
        s.bm.faces.new(back)
    faces = [f for f in s.bm.faces if f not in before]
    bmesh.ops.recalc_face_normals(s.bm, faces=faces)
    if bevel and not dome:
        rim = [e for e in s.bm.edges if all(f in faces for f in e.link_faces) and len(e.link_faces) == 2
               and abs(e.link_faces[0].normal.dot(e.link_faces[1].normal)) < 0.5]
        bmesh.ops.bevel(s.bm, geom=rim, offset=bevel, segments=2, profile=0.5, affect="EDGES")
    bmesh.ops.triangulate(s.bm, faces=[f for f in s.bm.faces if f not in before and len(f.verts) > 4])
    return s.end(before, col, smooth, outline)


def tube(s, pts, radii, col, sides=6, outline=True, smooth=True, cap=True):
    before = s.begin()
    pts = [Vector(p) for p in pts]
    if not isinstance(radii, (list, tuple)):
        radii = [radii] * len(pts)
    n = len(pts)
    rings = []
    ref = Vector((0, -1, 0))
    for i, p in enumerate(pts):
        t = (pts[min(i + 1, n - 1)] - pts[max(i - 1, 0)]).normalized()
        a = ref - t * ref.dot(t)
        if a.length < 1e-3:
            a = Vector((1, 0, 0)) - t * t.x
        a.normalize()
        b = t.cross(a)
        rings.append([s.bm.verts.new(p + (a * math.cos(k * TAU / sides) + b * math.sin(k * TAU / sides)) * radii[i])
                      for k in range(sides)])
    for ra, rb in zip(rings, rings[1:]):
        for k in range(sides):
            j = (k + 1) % sides
            s.bm.faces.new((ra[k], ra[j], rb[j], rb[k]))
    if cap:
        s.bm.faces.new(rings[0][::-1])
        s.bm.faces.new(rings[-1])
    faces = s.end(before, col, smooth, outline)
    bmesh.ops.recalc_face_normals(s.bm, faces=faces)
    return faces


def gloss(s, c, r=0.07, squash=0.45):
    """The white catch-light: a flattened blob just proud of the surface, no outline."""
    sphere(s, c, (r, r * squash, r * 0.7), "WHITE", segs=8, rings=5, outline=False)


def eye(s, c, r=0.06):
    sphere(s, c, (r, r * 0.5, r * 1.15), "DARK", segs=8, rings=5, outline=False)
    sphere(s, (c[0] - r * 0.3, c[1] - r * 0.45, c[2] + r * 0.4), (r * 0.32, r * 0.2, r * 0.32), "WHITE",
           segs=6, rings=4, outline=False)


def rotz(deg):
    return Matrix.Rotation(math.radians(deg), 3, "Z")


def rotx(deg):
    return Matrix.Rotation(math.radians(deg), 3, "X")


def roty(deg):
    return Matrix.Rotation(math.radians(deg), 3, "Y")


def star(n, ro, ri, rot=90.0):
    pts = []
    for k in range(n * 2):
        a = math.radians(rot) + k * math.pi / n
        r = ro if k % 2 == 0 else ri
        pts.append((r * math.cos(a), r * math.sin(a)))
    return pts


def ellipse(rx, rz, n=16, c=(0, 0)):
    return [(c[0] + rx * math.cos(k * TAU / n), c[1] + rz * math.sin(k * TAU / n)) for k in range(n)]


def leaf_poly(length, width, n=5):
    """A leaf pointing up +z from the origin."""
    left = [(-width * math.sin(math.pi * t) ** 0.8, length * t) for t in (i / n for i in range(1, n))]
    return [(0, 0)] + [(-x, z) for x, z in left] + [(0, length)] + left[::-1]


def leaf(s, base, length, width, deg, col="LEAF", depth=0.04, tilt=0.0):
    poly = [(x, z) for x, z in leaf_poly(length, width)]
    extrude(s, poly, depth, col, rot=rotx(tilt) @ roty(deg), c=base, outline=True)


# --------------------------------------------------------------------------------------- symbols

def lime(s):
    sphere(s, (0, 0, 0), (0.42, 0.36, 0.4), "LIME", segs=14, rings=9)
    sphere(s, (0.4, 0, 0.05), (0.06, 0.06, 0.05), "LIME", segs=6, rings=4)
    leaf(s, (-0.05, 0, 0.36), 0.32, 0.12, -40)
    gloss(s, (-0.18, -0.33, 0.17))


def coconut(s):
    sphere(s, (0, 0.08, 0), (0.44, 0.3, 0.44), "BROWN", segs=14, rings=9, bumps=0.04, seed=3)
    lathe(s, [(0.0, -0.25), (0.36, -0.25), (0.4, -0.2)], "CREAM", segs=16, axis="y", smooth=False)
    lathe(s, [(0.0, -0.27), (0.26, -0.27)], "STRAW", segs=16, axis="y", outline=False)
    gloss(s, (-0.12, -0.29, 0.12), 0.05)


def mango(s):
    sphere(s, (0, 0, -0.02), (0.36, 0.3, 0.46), "ORANGE", segs=14, rings=9, rot=roty(-28))
    sphere(s, (-0.1, -0.24, -0.2), (0.2, 0.08, 0.2), "RED", segs=10, rings=6, outline=False)
    tube(s, [(0.14, 0, 0.36), (0.2, 0, 0.47)], 0.03, "BROWN")
    leaf(s, (0.2, 0, 0.45), 0.3, 0.12, -70)
    gloss(s, (-0.2, -0.27, 0.15))


def papaya(s):
    sphere(s, (0, 0.06, 0), (0.32, 0.26, 0.5), "ORANGE", segs=14, rings=9)
    extrude(s, ellipse(0.27, 0.44, 18), 0.04, "PINK", y=-0.2, smooth=False)
    extrude(s, ellipse(0.11, 0.24, 12, (0, -0.04)), 0.02, "DARK", y=-0.23, outline=False)
    rng = random.Random(5)
    for _ in range(9):
        sphere(s, (rng.uniform(-0.07, 0.07), -0.25, rng.uniform(-0.24, 0.16)), 0.035, "OBSIDIAN", segs=6, rings=4,
               outline=False)


def pineapple(s, col="YELLOW"):
    profile = [(0.0, -0.46), (0.22, -0.43), (0.33, -0.28), (0.36, -0.08), (0.32, 0.1), (0.2, 0.2), (0.0, 0.22)]
    lathe(s, profile, col, segs=8, smooth=False, twist=math.pi / 8)
    for k in range(7):
        a = -60 + k * 20
        leaf(s, (0, 0, 0.16), 0.24 + 0.12 * (1 - abs(a) / 60), 0.07, a)
    gloss(s, (-0.16, -0.3, 0.02), 0.05)


def melon(s):
    arc = [(0.48 * math.cos(math.radians(a)), 0.1 + 0.48 * math.sin(math.radians(a))) for a in range(200, 341, 10)]
    extrude(s, [(x * 1.07, z * 1.07 + 0.007) for x, z in arc], 0.2, "SEA", y=0.04)
    extrude(s, [(x * 0.98, z * 0.98 + 0.002) for x, z in arc], 0.21, "CREAM", y=0.02, outline=False)
    extrude(s, [(x * 0.9, z * 0.9 + 0.01) for x, z in arc], 0.22, "MELON", outline=False)
    for x, z in ((-0.2, -0.05), (0.0, -0.18), (0.2, -0.05), (-0.08, 0.02), (0.1, 0.02)):
        sphere(s, (x, -0.12, z), (0.03, 0.02, 0.05), "DARK", segs=6, rings=4, outline=False)


def seven(s, col="RED", rim="GOLD"):
    poly = [(-0.42, 0.5), (0.44, 0.5), (0.44, 0.36), (0.04, -0.5), (-0.2, -0.5), (0.18, 0.3), (-0.3, 0.3),
            (-0.3, 0.36), (-0.42, 0.36)]
    extrude(s, poly, 0.2, col, bevel=0.035)
    extrude(s, [(x * 1.12, z * 1.08 - 0.01) for x, z in poly], 0.14, rim, y=0.07, outline=True)
    gloss(s, (-0.28, -0.13, 0.44), 0.05, 0.3)


def star_sym(s, col="GOLD", ro=0.5, ri=0.22, depth=0.16, dome=0.12):
    extrude(s, star(5, ro, ri), depth, col, dome=dome)
    gloss(s, (-0.12, -0.2, 0.18), 0.05, 0.4)


# ----- volcano

def gem(s, profile, col, segs=8, rot=None):
    lathe(s, profile, col, segs=segs, axis="y", smooth=False, rot=rot)
    gloss(s, (-0.12, -0.3, 0.14), 0.05, 0.4)


def obsidian(s):
    poly = [(0.0, 0.5), (0.22, 0.22), (0.26, -0.2), (0.06, -0.5), (-0.2, -0.36), (-0.26, 0.06)]
    extrude(s, poly, 0.12, "OBSIDIAN", dome=0.16)
    gloss(s, (-0.08, -0.18, 0.2), 0.04, 0.3)


def jade(s):
    sphere(s, (0, 0, 0), (0.42, 0.22, 0.34), "JADE", segs=10, rings=6, smooth=False)
    extrude(s, ellipse(0.46, 0.38, 14), 0.08, "GOLD", y=0.12)
    gloss(s, (-0.16, -0.2, 0.14))


def amber(s):
    gem(s, [(0.0, -0.18), (0.26, -0.12), (0.32, 0.0), (0.26, 0.12), (0.0, 0.18)], "AMBER", 8,
        rot=Matrix.Diagonal((1.0, 1.0, 1.55)))
    sphere(s, (0.02, -0.05, -0.04), (0.06, 0.05, 0.1), "BROWN", segs=6, rings=4, outline=False)


def ruby(s):
    lathe(s, [(0.0, -0.36), (0.44, 0.08), (0.42, 0.14), (0.24, 0.26), (0.0, 0.26)], "RUBY", segs=8, smooth=False)
    gloss(s, (-0.14, -0.3, 0.12), 0.05, 0.4)


def pearl(s):
    sphere(s, (0, 0.1, -0.26), (0.46, 0.22, 0.14), "CORAL", segs=12, rings=6)
    sphere(s, (0, -0.02, 0.04), 0.3, "PEARL", segs=14, rings=9)
    gloss(s, (-0.11, -0.27, 0.17), 0.07)


def drum(s):
    rt = rotx(-18)
    lathe(s, [(0.0, -0.4), (0.3, -0.4), (0.36, -0.1), (0.34, 0.2), (0.32, 0.3)], "BROWN", segs=12, rot=rt,
          smooth=False)
    lathe(s, [(0.34, 0.3), (0.0, 0.33)], "CREAM", segs=12, rot=rt, smooth=False)
    for h in (-0.3, 0.2):
        lathe(s, [(0.37, h - 0.04), (0.39, h), (0.37, h + 0.04)], "GOLD", segs=12, rot=rt, outline=False)
    for k in range(6):
        a = k * TAU / 6
        tube(s, [rt @ Vector((0.36 * math.cos(a), 0.36 * math.sin(a), -0.28)),
                 rt @ Vector((0.35 * math.cos(a + 0.5), 0.35 * math.sin(a + 0.5), 0.18))], 0.018, "RED",
             sides=4, outline=False)


def mask(s):
    poly = [(-0.34, 0.5), (0.34, 0.5), (0.4, 0.2), (0.32, -0.2), (0.16, -0.46), (0.0, -0.52), (-0.16, -0.46),
            (-0.32, -0.2), (-0.4, 0.2)]
    extrude(s, poly, 0.14, "TEAL", bevel=0.03)
    for x in (-0.15, 0.15):
        extrude(s, ellipse(0.1, 0.07, 10, (x, 0.18)), 0.06, "DARK", y=-0.08, outline=False)
        extrude(s, [(x - 0.14, 0.3), (x + 0.14, 0.3), (x + 0.12, 0.36), (x - 0.12, 0.36)], 0.08, "GOLD", y=-0.08,
                outline=False)
    extrude(s, [(-0.2, -0.2), (0.2, -0.2), (0.14, -0.32), (-0.14, -0.32)], 0.06, "DARK", y=-0.08, outline=False)
    for x in (-0.12, -0.04, 0.04, 0.12):
        extrude(s, [(x - 0.03, -0.2), (x + 0.03, -0.2), (x, -0.27)], 0.08, "CREAM", y=-0.09, outline=False)
    extrude(s, [(-0.05, 0.12), (0.05, 0.12), (0.07, -0.08), (-0.07, -0.08)], 0.1, "TEAL", y=-0.1, outline=False)


def idol(s):
    head = [(-0.3, 0.5), (0.3, 0.5), (0.36, 0.38), (0.36, -0.06), (0.26, -0.14), (-0.26, -0.14), (-0.36, -0.06),
            (-0.36, 0.38)]
    extrude(s, head, 0.3, "BROWN", bevel=0.03)
    extrude(s, [(-0.22, -0.14), (0.22, -0.14), (0.26, -0.5), (-0.26, -0.5)], 0.26, "BROWN", y=0.02, bevel=0.02)
    for x in (-0.14, 0.14):
        extrude(s, ellipse(0.09, 0.09, 10, (x, 0.24)), 0.06, "GOLD", y=-0.16, outline=False)
        extrude(s, ellipse(0.045, 0.045, 8, (x, 0.24)), 0.04, "DARK", y=-0.19, outline=False)
    extrude(s, [(-0.2, 0.02), (0.2, 0.02), (0.16, -0.08), (-0.16, -0.08)], 0.06, "DARK", y=-0.16, outline=False)
    extrude(s, [(-0.3, -0.24), (0.3, -0.24), (0.3, -0.3), (-0.3, -0.3)], 0.08, "GOLD", y=-0.13, outline=False)


def crown(s, col="GOLD"):
    poly = [(-0.46, -0.36), (0.46, -0.36), (0.46, 0.1), (0.46, 0.34), (0.26, 0.04), (0.0, 0.44), (-0.26, 0.04),
            (-0.46, 0.34), (-0.46, 0.1)]
    extrude(s, poly, 0.22, col, bevel=0.03)
    extrude(s, [(-0.48, -0.2), (0.48, -0.2), (0.48, -0.36), (-0.48, -0.36)], 0.26, col, y=0.0, outline=False)
    for x, z, c in ((0.0, 0.44, "RUBY"), (-0.46, 0.34, "JADE"), (0.46, 0.34, "JADE")):
        sphere(s, (x, -0.04, z), 0.07, c, segs=8, rings=5)
    for x, c in ((-0.26, "RUBY"), (0.0, "BLUE"), (0.26, "RUBY")):
        extrude(s, [(x, -0.2), (x + 0.07, -0.28), (x, -0.36), (x - 0.07, -0.28)], 0.06, c, y=-0.14, outline=False)
    gloss(s, (-0.3, -0.14, 0.1), 0.05, 0.3)


def volcano(s):
    lathe(s, [(0.0, -0.46), (0.5, -0.46), (0.36, -0.2), (0.22, 0.12), (0.16, 0.22), (0.1, 0.17), (0.0, 0.16)],
          "STONE", segs=10, smooth=False)
    lathe(s, [(0.13, 0.19), (0.0, 0.2)], "LAVA", segs=10, outline=False)
    for a, length in ((-110, 0.5), (-70, 0.42), (-95, 0.3)):
        r = math.radians(a)
        d = Vector((math.cos(r), math.sin(r), 0))
        pts = [Vector((0, 0, 0.2)) + d * 0.12, Vector((0, 0, 0.0)) + d * 0.26, Vector((0, 0, 0.2 - length)) + d * 0.4]
        tube(s, pts, [0.05, 0.045, 0.03], "LAVA", sides=5, outline=False)
    for k, (x, z, r) in enumerate(((0.0, 0.34, 0.12), (0.1, 0.44, 0.1), (-0.06, 0.5, 0.08))):
        sphere(s, (x, 0.05, z), r, "METAL", segs=8, rings=5)


def lava_orb(s):
    sphere(s, (0, 0, 0), 0.44, "LAVA", segs=14, rings=9)
    rng = random.Random(9)
    for k in range(7):
        a, e = rng.uniform(0, TAU), rng.uniform(-1.0, 1.0)
        n = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
        if n.y < -0.5 and abs(n.z) < 0.4:
            continue
        sphere(s, n * 0.42, (0.16, 0.16, 0.08), "STONE", segs=6, rings=4, outline=False,
               rot=n.to_track_quat("Z", "Y").to_matrix())
    gloss(s, (-0.16, -0.36, 0.18), 0.07)


# ----- reef

def kelp(s):
    for k, (x, h, lean) in enumerate(((-0.18, 0.95, -0.12), (0.02, 1.0, 0.04), (0.2, 0.85, 0.14))):
        pts = []
        for i in range(9):
            t = i / 8
            pts.append((x + lean * t + 0.07 * math.sin(t * 9 + k), -0.5 + h * t))
        left = [(px - 0.07 * (1 - t * 0.5), pz) for (px, pz), t in zip(pts, (i / 8 for i in range(9)))]
        right = [(px + 0.07 * (1 - t * 0.5), pz) for (px, pz), t in zip(pts, (i / 8 for i in range(9)))]
        extrude(s, left + right[::-1], 0.05, "SEA" if k != 1 else "LEAF", y=0.03 * k)


def shell(s, col="CORAL"):
    n = 9
    poly = [(0.0, -0.46)]
    for k in range(n + 1):
        a = math.radians(200 - k * 220 / n) + math.pi / 2 - math.radians(10)
        poly.append((0.48 * math.cos(math.radians(160) - k * math.radians(140) / n),
                     -0.1 + 0.52 * math.sin(math.radians(160) - k * math.radians(140) / n)))
    extrude(s, poly[::-1], 0.14, col, dome=0.12)
    for k in range(1, n):
        a = math.radians(160) - k * math.radians(140) / n
        tube(s, [(0.0, -0.12, -0.38), (0.42 * math.cos(a), -0.12, -0.1 + 0.46 * math.sin(a))], 0.022, "CREAM",
             sides=4, outline=False)
    extrude(s, [(-0.12, -0.42), (0.12, -0.42), (0.16, -0.52), (-0.16, -0.52)], 0.12, col)


def starfish(s, col="ORANGE"):
    extrude(s, star(5, 0.5, 0.21, rot=90 + 6), 0.16, col, dome=0.1)
    for k in range(5):
        a = math.radians(96 + k * 72)
        for t in (0.18, 0.3, 0.4):
            sphere(s, (t * math.cos(a), -0.12 - 0.06 * (0.4 - t), t * math.sin(a)), 0.03, "CREAM", segs=6, rings=4,
                   outline=False)


def urchin(s):
    sphere(s, (0, 0, -0.05), 0.28, "PURPLE", segs=12, rings=8)
    for k in range(26):
        z = 1 - 2 * (k + 0.5) / 26
        r = math.sqrt(1 - z * z)
        a = k * 2.4
        d = Vector((r * math.cos(a), r * math.sin(a), z))
        lathe(s, [(0.05, 0.0), (0.0, 0.26)], "PURPLE", segs=4, smooth=False, outline=False,
              rot=d.to_track_quat("Z", "Y").to_matrix(), c=Vector((0, 0, -0.05)) + d * 0.24)
    gloss(s, (-0.1, -0.25, 0.08), 0.05)


def fish(s, body, col, belly=None, stripes=None, bill=0.0, sail=False, spots=None):
    """A fish swimming left, toward the reels' start."""
    rx, ry, rz = body
    sphere(s, (0, 0, 0), (rx, ry, rz), col, segs=14, rings=8)
    if belly:
        sphere(s, (0.02, -0.02, -rz * 0.35), (rx * 0.85, ry * 0.9, rz * 0.55), belly, segs=12, rings=6,
               outline=False)
    extrude(s, [(rx * 0.85, 0), (rx + 0.28, rz * 0.9), (rx + 0.22, 0), (rx + 0.28, -rz * 0.9)], 0.04, col)
    if sail:
        extrude(s, [(-rx * 0.5, rz * 0.8), (-rx * 0.2, rz * 2.2), (rx * 0.5, rz * 1.4), (rx * 0.6, rz * 0.7)], 0.03,
                col)
    else:
        extrude(s, [(-rx * 0.3, rz * 0.85), (0.0, rz * 1.5), (rx * 0.45, rz * 0.8)], 0.03, col)
    extrude(s, [(-rx * 0.1, -rz * 0.6), (rx * 0.15, -rz * 1.25), (rx * 0.35, -rz * 0.55)], 0.03, col)
    if bill:
        lathe(s, [(0.03, 0.0), (0.0, bill)], "METAL" if col != "GOLD" else "GOLD", segs=5,
              rot=roty(-90), c=(-rx * 0.95, 0, 0.02))
    for k, x in enumerate(stripes or ()):
        sphere(s, (x, 0, 0), (0.04, ry * 1.04, rz * 0.98 * math.sqrt(max(0.05, 1 - (x / rx) ** 2))), "CREAM",
               segs=10, rings=6, outline=False)
    rng = random.Random(4)
    for _ in range(spots or 0):
        sphere(s, (rng.uniform(-rx * 0.6, rx * 0.6), -ry * 0.92, rng.uniform(-rz * 0.4, rz * 0.6)), 0.03, "DARK",
               segs=6, rings=4, outline=False)
    eye(s, (-rx * 0.62, -ry * 0.72, rz * 0.25), 0.055)
    gloss(s, (-rx * 0.2, -ry * 0.95, rz * 0.55), 0.05)


def puffer(s):
    sphere(s, (0, 0, 0), (0.36, 0.32, 0.34), "YELLOW", segs=14, rings=9)
    sphere(s, (0, -0.03, -0.12), (0.3, 0.27, 0.2), "CREAM", segs=12, rings=6, outline=False)
    for k in range(18):
        z = 1 - 2 * (k + 0.5) / 18
        r = math.sqrt(1 - z * z)
        a = k * 2.4
        d = Vector((r * math.cos(a), r * math.sin(a) * 0.9, z))
        if d.y < -0.7:
            continue
        lathe(s, [(0.035, 0.0), (0.0, 0.1)], "STRAW", segs=4, smooth=False, outline=False,
              rot=d.to_track_quat("Z", "Y").to_matrix(), c=d * 0.33)
    extrude(s, [(0.3, 0), (0.48, 0.14), (0.44, 0), (0.48, -0.14)], 0.04, "YELLOW")
    eye(s, (-0.16, -0.27, 0.1), 0.07)
    sphere(s, (-0.33, -0.08, -0.04), (0.04, 0.04, 0.03), "ORANGE", segs=6, rings=4, outline=False)


def clownfish(s):
    fish(s, (0.36, 0.17, 0.24), "ORANGE", stripes=(-0.14, 0.08, 0.3))


def octopus(s):
    sphere(s, (0, 0, 0.12), (0.3, 0.26, 0.3), "MAGENTA", segs=14, rings=9)
    for k in range(8):
        a = math.radians(-160 + k * 140 / 7)
        x0 = 0.2 * math.cos(a)
        pts = [(x0, -0.05, -0.08)]
        for i in range(1, 6):
            t = i / 5
            pts.append((x0 + 0.42 * math.cos(a) * t + 0.06 * math.sin(t * 7 + k), -0.08 - 0.06 * t,
                        -0.1 - 0.32 * t + 0.12 * t * t))
        tube(s, pts, [0.07, 0.06, 0.05, 0.04, 0.03, 0.015], "MAGENTA", sides=4)
    eye(s, (-0.11, -0.22, 0.16), 0.07)
    eye(s, (0.11, -0.22, 0.16), 0.07)
    gloss(s, (-0.12, -0.2, 0.33), 0.06)


def chest(s):
    extrude(s, [(-0.46, -0.4), (0.46, -0.4), (0.46, 0.06), (-0.46, 0.06)], 0.46, "BROWN", bevel=0.02)
    lathe(s, [(0.0, -0.46), (0.23, -0.46), (0.23, 0.46), (0.0, 0.46)], "BROWN", segs=10, axis="x", smooth=False,
          c=(0, 0, 0.06), rot=Matrix.Diagonal((1.0, 1.0, 1.0)))
    for x in (-0.34, 0.34):
        extrude(s, [(x - 0.05, -0.4), (x + 0.05, -0.4), (x + 0.05, 0.3), (x - 0.05, 0.3)], 0.5, "GOLD",
                outline=False)
    extrude(s, [(-0.08, -0.06), (0.08, -0.06), (0.08, 0.14), (-0.08, 0.14)], 0.06, "GOLD", y=-0.24)
    for x, z in ((-0.18, 0.3), (0.0, 0.33), (0.17, 0.29), (0.08, 0.36)):
        lathe(s, [(0.0, 0.0), (0.08, 0.0), (0.08, 0.025), (0.0, 0.025)], "GOLD", segs=8, axis="y",
              c=(x, -0.18, z), smooth=False, outline=False)


# ----- fruit tumble

def berry(s):
    for x, z, r in ((-0.17, -0.12, 0.24), (0.17, -0.12, 0.24), (0.0, 0.14, 0.24)):
        sphere(s, (x, 0, z), r, "RUBY", segs=10, rings=6)
        gloss(s, (x - 0.08, -0.21, z + 0.09), 0.045)
    leaf(s, (0.0, 0.05, 0.3), 0.22, 0.1, -30)
    leaf(s, (0.0, 0.05, 0.3), 0.22, 0.1, 30)


def lychee(s):
    sphere(s, (0, 0, 0), 0.42, "PINK", segs=10, rings=7, smooth=False, bumps=0.06, seed=12)
    tube(s, [(0, 0, 0.4), (0.04, 0, 0.5)], 0.03, "BROWN")
    leaf(s, (0.04, 0, 0.48), 0.24, 0.1, -60)


def kiwi(s):
    lathe(s, [(0.0, 0.1), (0.47, 0.1), (0.5, 0.0), (0.47, -0.1), (0.0, -0.1)], "BROWN", segs=18, axis="y")
    lathe(s, [(0.0, -0.11), (0.42, -0.11)], "KIWI", segs=18, axis="y", outline=False)
    lathe(s, [(0.0, -0.12), (0.12, -0.12)], "CREAM", segs=12, axis="y", outline=False)
    for k in range(12):
        a = k * TAU / 12
        sphere(s, (0.2 * math.cos(a), -0.125, 0.2 * math.sin(a)), (0.025, 0.012, 0.04), "DARK", segs=6, rings=4,
               outline=False, rot=roty(-math.degrees(a) + 90))


def starfruit(s):
    extrude(s, star(5, 0.48, 0.3, rot=90), 0.3, "YELLOW", dome=0.1)
    extrude(s, star(5, 0.12, 0.08, rot=90), 0.04, "CREAM", y=-0.2, outline=False)


def guava(s):
    lathe(s, [(0.0, -0.42), (0.3, -0.36), (0.4, -0.12), (0.34, 0.12), (0.2, 0.3), (0.08, 0.38), (0.0, 0.38)],
          "LIME", segs=14)
    sphere(s, (0.14, -0.3, -0.12), (0.14, 0.06, 0.14), "PINK", segs=8, rings=5, outline=False)
    lathe(s, [(0.0, -0.44), (0.07, -0.42), (0.0, -0.4)], "BROWN", segs=6, outline=False)
    leaf(s, (0.05, 0, 0.36), 0.26, 0.11, -55)
    gloss(s, (-0.2, -0.3, 0.06), 0.05)


def banana(s):
    for k, (dz, dy) in enumerate(((0.0, 0.0), (-0.12, -0.08), (0.12, 0.06))):
        pts = []
        for i in range(7):
            t = i / 6
            a = math.radians(200 + t * 120)
            pts.append((0.46 * math.cos(a) * 1.05, dy, 0.36 + 0.46 * math.sin(a) + dz))
        tube(s, pts, [0.05, 0.09, 0.11, 0.11, 0.1, 0.08, 0.04], "YELLOW", sides=6)
        sphere(s, pts[-1], 0.04, "BROWN", segs=6, rings=4, outline=False)
    tube(s, [(-0.46, 0.0, 0.2), (-0.5, 0.0, 0.34)], 0.06, "LEAF")


def dragonfruit(s):
    sphere(s, (0, 0, 0), (0.34, 0.3, 0.44), "MAGENTA", segs=12, rings=8)
    for k in range(10):
        a = k * 2.4
        z = -0.3 + 0.07 * k
        r = 0.34 * math.sqrt(max(0.1, 1 - (z / 0.44) ** 2))
        if math.sin(a) > 0.6:
            continue
        base = (r * math.cos(a), r * math.sin(a), z)
        leaf(s, base, 0.18, 0.06, -math.degrees(math.atan2(base[0], 0.4)) * 0.8, col="KIWI", tilt=-25)
    leaf(s, (0, 0, 0.42), 0.18, 0.06, 0, col="KIWI")


def passionfruit(s):
    sphere(s, (0, 0.08, 0), (0.42, 0.3, 0.42), "PURPLE", segs=14, rings=9)
    lathe(s, [(0.0, -0.22), (0.36, -0.22)], "YELLOW", segs=16, axis="y", outline=False)
    rng = random.Random(7)
    for _ in range(14):
        a, r = rng.uniform(0, TAU), 0.28 * math.sqrt(rng.random())
        sphere(s, (r * math.cos(a), -0.235, r * math.sin(a)), 0.03, "DARK", segs=6, rings=4, outline=False)


def sun(s):
    extrude(s, star(12, 0.5, 0.36, rot=90), 0.08, "ORANGE", y=0.06)
    lathe(s, [(0.0, -0.1), (0.3, -0.06), (0.36, 0.0), (0.0, 0.0)], "YELLOW", segs=16, axis="y", smooth=True)
    for x in (-0.11, 0.11):
        sphere(s, (x, -0.15, 0.08), (0.04, 0.02, 0.06), "DARK", segs=6, rings=4, outline=False)
    tube(s, [(-0.14, -0.13, -0.06), (-0.07, -0.15, -0.13), (0.07, -0.15, -0.13), (0.14, -0.13, -0.06)], 0.022,
         "DARK", sides=4, outline=False)
    gloss(s, (-0.16, -0.12, 0.18), 0.05)


def coconut_bomb(s):
    sphere(s, (0, 0, -0.06), 0.38, "BROWN", segs=14, rings=9, bumps=0.03, seed=6)
    for x, z in ((-0.1, 0.06), (0.1, 0.06), (0.0, -0.08)):
        sphere(s, (x, -0.35, z), (0.045, 0.02, 0.045), "DARK", segs=6, rings=4, outline=False)
    lathe(s, [(0.12, 0.28), (0.12, 0.36), (0.0, 0.37)], "METAL", segs=8)
    tube(s, [(0, 0, 0.36), (0.06, 0, 0.45), (0.16, 0, 0.47)], 0.025, "STRAW", sides=4)
    extrude(s, star(6, 0.12, 0.05), 0.04, "YELLOW", c=(0.18, -0.02, 0.48), outline=False)
    gloss(s, (-0.16, -0.3, 0.1), 0.06)


# ----- lagoon

def conch(s):
    rt = roty(-35)
    profile = [(0.0, 0.52), (0.07, 0.42), (0.12, 0.34), (0.2, 0.22), (0.3, 0.1), (0.37, 0.02), (0.33, -0.14),
               (0.18, -0.32), (0.07, -0.46), (0.0, -0.5)]
    lathe(s, profile, "STRAW", segs=10, smooth=False, rot=rt, twist=0.3)
    for h, r in ((0.34, 0.125), (0.22, 0.205)):
        lathe(s, [(r, h - 0.02), (r + 0.015, h), (r, h + 0.02)], "BROWN", segs=10, rot=rt, outline=False)
    for k in range(7):
        a = k * TAU / 7
        d = Vector((math.cos(a), math.sin(a), 0.25)).normalized()
        if d.y < -0.6:
            continue
        lathe(s, [(0.05, 0.0), (0.0, 0.12)], "STRAW", segs=5, smooth=False, outline=False,
              rot=rt @ d.to_track_quat("Z", "Y").to_matrix(), c=rt @ Vector((0.34 * math.cos(a), 0.34 * math.sin(a), 0.04)))
    extrude(s, ellipse(0.13, 0.28, 12, (0.08, -0.1)), 0.06, "PINK", y=-0.3, rot=rt, outline=False)
    gloss(s, (-0.06, -0.3, 0.2), 0.05)


def crab(s):
    sphere(s, (0, 0, -0.08), (0.34, 0.22, 0.2), "RED", segs=14, rings=8)
    for side in (-1, 1):
        tube(s, [(0.28 * side, -0.05, -0.04), (0.38 * side, -0.08, 0.12), (0.34 * side, -0.1, 0.24)], 0.04, "RED",
             sides=5)
        extrude(s, [(0, 0), (0.1, 0.06), (0.14, 0.18), (0.04, 0.12), (0.02, 0.2), (-0.06, 0.12)], 0.1, "RED",
                c=(0.32 * side, -0.1, 0.24), rot=roty(-15 * side) @ Matrix.Diagonal((side, 1, 1)))
        for k in range(3):
            tube(s, [(0.24 * side, 0.0, -0.14 - 0.03 * k), (0.42 * side, 0.02, -0.22 - 0.04 * k),
                     (0.48 * side, 0.02, -0.38 - 0.02 * k)], 0.022, "RED", sides=4)
        tube(s, [(0.1 * side, -0.12, 0.06), (0.12 * side, -0.14, 0.2)], 0.018, "RED", sides=4, outline=False)
        eye(s, (0.12 * side, -0.16, 0.22), 0.05)
    tube(s, [(-0.07, -0.22, -0.12), (0.0, -0.23, -0.15), (0.07, -0.22, -0.12)], 0.015, "DARK", sides=4,
         outline=False)


def bobber(s):
    lathe(s, [(0.0, -0.06), (0.3, -0.04), (0.34, 0.02), (0.0, 0.02)], "WHITE", segs=14)
    lathe(s, [(0.0, -0.46), (0.2, -0.38), (0.32, -0.16), (0.34, -0.04), (0.0, -0.04)][::-1][::-1], "CREAM",
          segs=14)
    lathe(s, [(0.0, 0.02), (0.34, 0.02), (0.3, 0.2), (0.18, 0.32), (0.0, 0.36)], "RED", segs=14)
    tube(s, [(0, 0, 0.34), (0, 0, 0.52)], 0.03, "METAL", sides=4)
    gloss(s, (-0.14, -0.27, 0.18), 0.06)


def tackle(s):
    extrude(s, [(-0.46, -0.36), (0.46, -0.36), (0.46, 0.14), (-0.46, 0.14)], 0.42, "SEA", bevel=0.03)
    extrude(s, [(-0.48, 0.14), (0.48, 0.14), (0.44, 0.24), (-0.44, 0.24)], 0.44, "SEA", bevel=0.01)
    tube(s, [(-0.18, 0, 0.24), (-0.16, 0, 0.4), (0.16, 0, 0.4), (0.18, 0, 0.24)], 0.03, "DARK", sides=5)
    for x in (-0.28, 0.28):
        extrude(s, [(x - 0.06, 0.04), (x + 0.06, 0.04), (x + 0.06, 0.2), (x - 0.06, 0.2)], 0.06, "GOLD", y=-0.22,
                outline=False)
    sphere(s, (0.0, -0.22, -0.14), (0.1, 0.05, 0.06), "ORANGE", segs=8, rings=5, outline=False)


def rod(s):
    tube(s, [(-0.42, 0, -0.46), (0.0, 0, 0.0), (0.44, 0, 0.48)], [0.045, 0.03, 0.012], "BROWN", sides=5)
    tube(s, [(-0.42, 0, -0.46), (-0.26, 0, -0.28)], 0.06, "DARK", sides=6)
    lathe(s, [(0.0, -0.06), (0.12, -0.06), (0.12, 0.06), (0.0, 0.06)], "METAL", segs=10, axis="y",
          c=(-0.18, -0.06, -0.14))
    tube(s, [(0.44, 0, 0.48), (0.46, -0.02, 0.0), (0.44, -0.02, -0.2)], 0.008, "CREAM", sides=3, outline=False)
    hook(s, scale=0.35, c=(0.44, -0.02, -0.3))


def boat(s):
    hull = [(-0.5, 0.1), (0.5, 0.1), (0.4, -0.12), (0.26, -0.26), (-0.26, -0.26), (-0.4, -0.12)]
    extrude(s, hull, 0.44, "BLUE", bevel=0.03)
    extrude(s, [(-0.5, 0.1), (0.5, 0.1), (0.5, 0.04), (-0.5, 0.04)], 0.46, "CREAM", outline=False)
    extrude(s, [(-0.3, 0.1), (0.3, 0.1), (0.3, 0.16), (-0.3, 0.16)], 0.4, "BROWN", y=0.02)
    tube(s, [(-0.1, -0.24, 0.12), (0.34, -0.26, 0.42)], 0.022, "STRAW", sides=4)
    extrude(s, [(0, 0), (0.06, 0.12), (0, 0.24), (-0.06, 0.12)], 0.02, "STRAW", c=(0.36, -0.27, 0.42),
            rot=roty(-50))


def castaway(s):
    sphere(s, (0, 0, -0.02), (0.32, 0.28, 0.34), "SKIN", segs=14, rings=9)
    sphere(s, (0, -0.04, -0.2), (0.3, 0.26, 0.24), "BROWN", segs=12, rings=7, bumps=0.05, seed=2)
    lathe(s, [(0.0, 0.16), (0.5, 0.18), (0.48, 0.22), (0.28, 0.22), (0.26, 0.4), (0.0, 0.42)], "STRAW", segs=14,
          smooth=False, rot=rotx(-8))
    lathe(s, [(0.27, 0.22), (0.27, 0.27)], "RED", segs=14, outline=False, rot=rotx(-8))
    eye(s, (-0.1, -0.27, 0.06), 0.045)
    eye(s, (0.1, -0.27, 0.06), 0.045)
    sphere(s, (0, -0.31, -0.02), (0.05, 0.04, 0.05), "SKIN", segs=6, rings=4, outline=False)
    tube(s, [(-0.08, -0.29, -0.12), (0.0, -0.31, -0.15), (0.08, -0.29, -0.12)], 0.015, "DARK", sides=4,
         outline=False)


def hook(s, scale=1.0, c=(0, 0, 0)):
    c = Vector(c)
    bend = [(-0.18 + 0.18 * math.cos(math.radians(a)), -0.12 + 0.2 * math.sin(math.radians(a)))
            for a in range(0, -181, -30)]
    pts = [c + Vector((x, 0, z)) * scale for x, z in [(0.0, 0.42)] + bend + [(-0.36, 0.02)]]
    tube(s, pts, 0.065 * scale, "GOLD", sides=6)
    extrude(s, [(-0.36, 0.14), (-0.27, -0.02), (-0.36, -0.04)], 0.1, "GOLD", c=c,
            rot=Matrix.Diagonal((scale, scale, scale)))
    lathe(s, [(0.1, -0.03), (0.1, 0.03)], "GOLD", segs=8, axis="y", c=c + Vector((0, 0, 0.47)) * scale,
          rot=Matrix.Diagonal((scale, scale, scale)))


def hook_sym(s):
    hook(s)
    gloss(s, (-0.04, -0.06, 0.2), 0.04, 0.3)


# ----- extras

def disc(col):
    def make(s):
        lathe(s, [(0.0, -0.05), (0.5, -0.05), (0.5, 0.05), (0.0, 0.05)], col, segs=16, axis="y", smooth=False,
              outline=False)
        lathe(s, [(0.36, -0.06), (0.4, -0.06)], "WHITE", segs=16, axis="y", outline=False)
    return make


def card(col, emblem, ink):
    def make(s):
        extrude(s, [(-0.35, -0.5), (0.35, -0.5), (0.35, 0.5), (-0.35, 0.5)], 0.06, col, bevel=0.02, outline=False)
        if emblem == "diamond":
            extrude(s, [(0, 0.3), (0.2, 0), (0, -0.3), (-0.2, 0)], 0.02, ink, y=-0.04, outline=False)
        elif emblem == "heart":
            pts = [(0.0, -0.28)] + [(0.13 + 0.13 * math.cos(a), 0.06 + 0.13 * math.sin(a))
                                    for a in (math.radians(d) for d in range(-40, 181, 30))] + \
                  [(-0.13 + 0.13 * math.cos(a), 0.06 + 0.13 * math.sin(a))
                   for a in (math.radians(d) for d in range(0, 221, 30))]
            extrude(s, pts, 0.02, ink, y=-0.04, outline=False)
        else:
            pts = [(0.0, 0.3)] + [(-0.13 + 0.13 * math.cos(a), -0.04 + 0.13 * math.sin(a))
                                  for a in (math.radians(d) for d in range(140, 361, 30))] + \
                  [(0.13 + 0.13 * math.cos(a), -0.04 + 0.13 * math.sin(a))
                   for a in (math.radians(d) for d in range(180, 401, 30))]
            extrude(s, pts[::-1], 0.02, ink, y=-0.04, outline=False)
            extrude(s, [(-0.03, -0.1), (0.03, -0.1), (0.08, -0.3), (-0.08, -0.3)], 0.02, ink, y=-0.04, outline=False)
    return make


def slab(col):
    def make(s):
        extrude(s, [(-0.5, -0.5), (0.5, -0.5), (0.5, 0.5), (-0.5, 0.5)], 0.1, col, outline=False)
    return make


SYMBOLS = [
    ("Lime", lime), ("Coconut", coconut), ("Mango", mango), ("Papaya", papaya), ("Pineapple", pineapple),
    ("Melon", melon), ("Seven", seven), ("Star", star_sym),
    ("Obsidian", obsidian), ("Jade", jade), ("Amber", amber), ("Ruby", ruby), ("Pearl", pearl), ("Drum", drum),
    ("Mask", mask), ("Idol", idol), ("Crown", crown), ("Volcano", volcano), ("LavaOrb", lava_orb),
    ("Kelp", kelp), ("Shell", shell), ("Starfish", starfish), ("Urchin", urchin), ("Puffer", puffer),
    ("Clownfish", clownfish), ("Octopus", octopus), ("Chest", chest),
    ("FruitBerry", berry), ("FruitLychee", lychee), ("FruitKiwi", kiwi), ("FruitStarfruit", starfruit),
    ("FruitGuava", guava), ("FruitBanana", banana), ("FruitDragonfruit", dragonfruit),
    ("FruitPassionfruit", passionfruit), ("FruitGoldenPineapple", lambda s: pineapple(s, "GOLD")),
    ("FruitSun", sun), ("FruitCoconutBomb", coconut_bomb),
    ("LagoonShell", conch), ("LagoonStarfish", lambda s: starfish(s, "RED")), ("LagoonCrab", crab),
    ("LagoonBobber", bobber), ("LagoonTackle", tackle), ("LagoonRod", rod), ("LagoonBoat", boat),
    ("LagoonMinnow", lambda s: fish(s, (0.3, 0.1, 0.13), "SKY", belly="CREAM")),
    ("LagoonSnapper", lambda s: fish(s, (0.34, 0.14, 0.2), "MELON", belly="PINK")),
    ("LagoonGrouper", lambda s: fish(s, (0.36, 0.2, 0.25), "LEAF", belly="LIME", spots=8)),
    ("LagoonMarlin", lambda s: fish(s, (0.34, 0.12, 0.16), "BLUE", belly="SKY", bill=0.22, sail=True)),
    ("LagoonGoldenMarlin", lambda s: fish(s, (0.34, 0.12, 0.16), "GOLD", belly="YELLOW", bill=0.22, sail=True)),
    ("LagoonCastaway", castaway), ("LagoonHook", hook_sym),
    ("SpotMarked", disc("TEAL")), ("SpotHot", disc("MAGENTA")),
    ("CardBack", card("BLUE", "diamond", "GOLD")), ("CardRed", card("RED", "heart", "CREAM")),
    ("CardBlack", card("DARK", "spade", "CREAM")), ("Screen", slab("NAVY")),
]

# ------------------------------------------------------------------------------------- cabinets
#
# One cabinet per game, standing on its origin with its front to -Y: a plinth, a base, the button
# deck, a body with a bezel round the reel window, pillars, and a topper in the game's theme. The
# measurements are SlotFactory's: the deck's top at 0.94 where the buttons sit, the window centred
# at 1.5 and as tall as the game's grid, the back no deeper than 0.33 (the wall is 10 cm behind).
# Bulbs are their own three objects, every third bulb in each, for the cabinet to chase.

WINDOW = {"Sevens": 0.54, "Lagoon": 0.54, "Volcano": 0.7, "Fruit": 0.7, "Reef": 0.84}
WIDTH = 0.84
BACK = 0.33
FACE = -0.12     # the body's front, where the screen's glass sits
BULB = 0.024


def box(s, lo, hi, col, bevel=0.0, outline=True):
    before = s.begin()
    ret = bmesh.ops.create_cube(s.bm, size=1.0)
    lo, hi = Vector(lo), Vector(hi)
    centre, size = (lo + hi) / 2, hi - lo
    for v in ret["verts"]:
        v.co = Vector((v.co.x * size.x, v.co.y * size.y, v.co.z * size.z)) + centre
    if bevel:
        faces = [f for f in s.bm.faces if f not in before]
        edges = list({e for f in faces for e in f.edges})
        bmesh.ops.bevel(s.bm, geom=edges, offset=bevel, segments=2, profile=0.5, affect="EDGES")
    return s.end(before, col, False, outline)


def put(s, make, scale, at, rot=None):
    """A symbol builder's parts, scaled, turned and moved onto the cabinet."""
    before = set(s.bm.verts)
    make(s)
    rm = rot or Matrix.Identity(3)
    for v in s.bm.verts:
        if v not in before:
            v.co = rm @ (v.co * scale) + Vector(at)


def bezel(s, h, col, inner="DARK", depth=0.14):
    """The frame round the window, and the dark reveal inside it the reels sit in."""
    w, z0, z1 = WIDTH / 2, 1.5 - h / 2, 1.5 + h / 2
    t, y0, y1 = 0.075, FACE - depth, FACE
    for lo, hi in (((-w - t, y0, z1), (w + t, y1, z1 + t)), ((-w - t, y0, z0 - t), (w + t, y1, z0)),
                   ((-w - t, y0, z0), (-w, y1, z1)), ((w, y0, z0), (w + t, y1, z1))):
        box(s, lo, hi, col, bevel=0.012)
    for lo, hi in (((-w, y0 + 0.01, z1 - 0.012), (w, y1, z1)), ((-w, y0 + 0.01, z0), (w, y1, z0 + 0.012)),
                   ((-w, y0 + 0.01, z0), (-w + 0.012, y1, z1)), ((w - 0.012, y0 + 0.01, z0), (w, y1, z1))):
        box(s, lo, hi, inner, outline=False)


def carcass(s, main, trim, h, bulbs, deck="DARK"):
    """What every cabinet shares: plinth, base, deck, body, bezel, pillars and their bulbs."""
    box(s, (-0.5, -0.33, 0.0), (0.5, BACK, 0.07), "DARK", bevel=0.01)
    box(s, (-0.48, -0.31, 0.07), (0.48, BACK - 0.01, 0.88), main, bevel=0.025)
    box(s, (-0.42, -0.325, 0.16), (0.42, -0.2, 0.8), trim, bevel=0.02)
    box(s, (-0.5, -0.38, 0.88), (0.5, 0.1, 0.94), deck, bevel=0.012)
    box(s, (-0.5, -0.385, 0.865), (0.5, -0.36, 0.89), trim, outline=False)
    box(s, (-0.47, FACE, 0.94), (0.47, BACK, 2.12), main, bevel=0.03)
    bezel(s, h, trim)
    for side in (-1, 1):
        x = side * 0.5
        lathe(s, [(0.0, 0.92), (0.055, 0.92), (0.055, 2.12), (0.0, 2.12)], trim, segs=10, c=(x, FACE - 0.02, 0),
              smooth=True)
        for k in range(11):
            bulbs.append(Vector((x - side * 0.0, FACE - 0.08, 1.0 + k * 0.106)))


def arch_bulbs(bulbs, centre, r, n, a0=10, a1=170, y=-0.2):
    for k in range(n):
        a = math.radians(a0 + (a1 - a0) * k / (n - 1))
        bulbs.append(Vector((centre[0] + r * math.cos(a), y, centre[1] + r * math.sin(a))))


def cab_sevens(s, bulbs):
    carcass(s, "RED", "GOLD", WINDOW["Sevens"], bulbs)
    # The marquee: a gold arch behind a red one, bulbs round its rim, the seven in the middle.
    arc = [(0.52 * math.cos(math.radians(a)), 2.1 + 0.46 * math.sin(math.radians(a))) for a in range(0, 181, 12)]
    extrude(s, [(x * 1.08, 2.1 + (z - 2.1) * 1.1) for x, z in arc], 0.2, "GOLD", y=0.12)
    extrude(s, arc, 0.26, "RED", y=0.06, bevel=0.015)
    arch_bulbs(bulbs, (0.0, 2.1), 0.46, 13, 8, 172, y=-0.08)
    put(s, seven, 0.36, (0.0, -0.1, 2.34))
    for x in (-0.3, 0.3):
        put(s, star_sym, 0.13, (x, -0.08, 2.22))
    # Paylines: little arrows either side of the window.
    for side in (-1, 1):
        for z in (1.38, 1.5, 1.62):
            extrude(s, [(0, 0.03), (side * 0.05, 0), (0, -0.03)], 0.02, "YELLOW", c=(side * 0.425, FACE - 0.15, z),
                    outline=False)
    put(s, seven, 0.4, (0.0, -0.33, 0.5))


def cab_volcano(s, bulbs):
    carcass(s, "STONE", "OBSIDIAN", WINDOW["Volcano"], bulbs, deck="OBSIDIAN")
    put(s, volcano, 0.65, (0.0, 0.0, 2.43))
    # Lava running down the body's front corners and through the base panel.
    rng = random.Random(11)
    for side in (-1, 1):
        pts = [(side * (0.44 - 0.03 * rng.random()), FACE - 0.005, 2.1 - k * 0.12) for k in range(10)]
        tube(s, pts, 0.014, "LAVA", sides=4, outline=False)
    for k in range(5):
        x0 = -0.35 + k * 0.17
        tube(s, [(x0, -0.33, 0.78), (x0 + 0.05, -0.33, 0.55), (x0 - 0.03, -0.33, 0.3)], 0.012, "LAVA", sides=4,
             outline=False)
    put(s, lava_orb, 0.3, (0.0, -0.33, 0.48))
    for k in range(9):
        bulbs.append(Vector((-0.4 + 0.1 * k, FACE - 0.03, 2.15)))


def cab_reef(s, bulbs):
    carcass(s, "TEAL", "METAL", WINDOW["Reef"], bulbs, deck="NAVY")
    # A diver's porthole: a brass ring with bolts, the starfish behind its glass.
    lathe(s, [(0.2, -0.06), (0.26, -0.06), (0.26, 0.06), (0.2, 0.06)], "GOLD", segs=20, axis="y",
          c=(0.0, -0.02, 2.42), smooth=True)
    lathe(s, [(0.0, -0.02), (0.2, -0.02)], "SEA", segs=20, axis="y", c=(0.0, -0.02, 2.42), outline=False)
    for k in range(10):
        a = k * TAU / 10
        sphere(s, (0.23 * math.cos(a), -0.09, 2.42 + 0.23 * math.sin(a)), 0.016, "METAL", segs=6, rings=4,
               outline=False)
    box(s, (-0.06, -0.06, 2.1), (0.06, 0.06, 2.18), "GOLD")
    put(s, starfish, 0.24, (0.0, -0.06, 2.42))
    arch_bulbs(bulbs, (0.0, 2.42), 0.31, 12, -30, 210, y=-0.04)
    # Coral up the sides, shells on the base.
    for side in (-1, 1):
        root = Vector((side * 0.5, -0.1, 2.12))
        for k, (dx, dz) in enumerate(((0.08, 0.22), (0.0, 0.32), (-0.06, 0.18))):
            tip = root + Vector((side * dx, 0, dz))
            tube(s, [root, root.lerp(tip, 0.5) + Vector((side * 0.03, 0, 0)), tip], [0.035, 0.026, 0.015], "CORAL",
                 sides=5)
    put(s, shell, 0.32, (0.0, -0.33, 0.5))


def cab_fruit(s, bulbs):
    carcass(s, "PINK", "CREAM", WINDOW["Fruit"], bulbs, deck="MAGENTA")
    for x in (-0.32, -0.16, 0.0, 0.16, 0.32):
        box(s, (x - 0.035, FACE - 0.004, 0.94), (x + 0.035, FACE, 2.12), "CORAL", outline=False)
    # The fruit stand: a striped awning and a heap of the reels' own fruit.
    for k in range(6):
        x0 = -0.5 + k / 6
        extrude(s, [(x0, 2.12), (x0 + 1 / 6, 2.12), (x0 + 1 / 6, 2.24), (x0 + 1 / 12, 2.19), (x0, 2.24)], 0.4,
                "RED" if k % 2 == 0 else "CREAM", y=-0.1, outline=k == 0)
    put(s, lambda q: pineapple(q, "GOLD"), 0.4, (0.0, 0.0, 2.48))
    put(s, banana, 0.28, (-0.27, -0.05, 2.38))
    put(s, berry, 0.24, (0.28, -0.05, 2.36))
    put(s, sun, 0.38, (0.0, -0.33, 0.5))
    arch_bulbs(bulbs, (0.0, 2.12), 0.48, 11, 5, 175, y=-0.16)


def cab_lagoon(s, bulbs):
    carcass(s, "SEA", "STRAW", WINDOW["Lagoon"], bulbs, deck="BROWN")
    # A beach hut: bamboo poles with their nodes, a thatched roof, a marlin over the door.
    for x in (-0.5, 0.5):
        for k in range(5):
            lathe(s, [(0.06, 0.94 + k * 0.24), (0.068, 0.95 + k * 0.24), (0.06, 0.96 + k * 0.24)], "BROWN",
                  segs=10, c=(x, FACE - 0.02, 0), outline=False)
    lathe(s, [(0.0, 2.62), (0.5, 2.2), (0.78, 2.04), (0.74, 2.0), (0.0, 2.1)], "STRAW", segs=4, smooth=False,
          rot=Matrix.Diagonal((1.0, 0.62, 1.0)) @ rotz(45), c=(0.0, -0.12, 0.0))
    for k in range(9):
        x = -0.6 + k * 0.15
        tube(s, [(x, -0.48, 2.05), (x + 0.02, -0.5, 1.96)], 0.018, "STRAW", sides=3, outline=False)
    put(s, lambda q: fish(q, (0.34, 0.12, 0.16), "BLUE", belly="SKY", bill=0.22, sail=True), 0.5,
        (0.0, -0.32, 2.3))
    put(s, bobber, 0.3, (0.0, -0.33, 0.5))
    for k in range(10):
        t = k / 9
        bulbs.append(Vector((-0.62 + 1.24 * t, -0.49, 1.98 - 0.06 * math.sin(math.pi * t))))


CABINETS = [("Sevens", cab_sevens), ("Volcano", cab_volcano), ("Reef", cab_reef), ("Fruit", cab_fruit),
            ("Lagoon", cab_lagoon)]

# --------------------------------------------------------------------------------------- driver

OUTLINE_WIDTH = 0.022


def finish(s, name, material, fit=True, outline=OUTLINE_WIDTH):
    bm = s.bm
    bm.normal_update()
    if fit:
        # A symbol: centred, the larger of width and height one unit (SlotFactory scales by the cell).
        xs = [v.co.x for v in bm.verts]
        ys = [v.co.y for v in bm.verts]
        zs = [v.co.z for v in bm.verts]
        centre = Vector(((max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2, (max(zs) + min(zs)) / 2))
        k = 1.0 / max(max(xs) - min(xs), max(zs) - min(zs))
        for v in bm.verts:
            v.co = (v.co - centre) * k
        bm.normal_update()

    # Paint before the outline: v up each part's height, a little lighter where it faces the viewer.
    uv = bm.loops.layers.uv.new("UVMap")
    span = {}
    for f in bm.faces:
        p = f[s.part]
        z = [v.co.z for v in f.verts]
        lo, hi = span.get(p, (1e9, -1e9))
        span[p] = (min(lo, *z), max(hi, *z))
    for f in bm.faces:
        lo, hi = span[f[s.part]]
        col = f[s.col]
        for loop in f.loops:
            h = (loop.vert.co.z - lo) / max(hi - lo, 1e-4)
            v = 0.12 + 0.72 * h + 0.14 * max(0.0, -f.normal.y)
            loop[uv].uv = P.uv_on(COLUMNS, col, 0.5, min(max(v, 0.0), 1.0))

    # The rim: outlined parts copied, pushed out, turned inside out, painted near-black.
    faces = [f for f in bm.faces if f[s.part] in s.outlined]
    if faces:
        ret = bmesh.ops.duplicate(bm, geom=faces)
        shell = [g for g in ret["geom"] if isinstance(g, bmesh.types.BMFace)]
        verts = {v for f in shell for v in f.verts}
        bm.normal_update()
        normals = {v: v.normal.copy() for v in verts}
        for v in verts:
            v.co += normals[v] * outline
        bmesh.ops.reverse_faces(bm, faces=shell)
        for f in shell:
            f.smooth = True
            f[s.col] = COL["OUTLINE"]
            for loop in f.loops:
                loop[uv].uv = P.uv_on(COLUMNS, COL["OUTLINE"], 0.5, 0.5)

    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(material)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def material(texture):
    mat = bpy.data.materials.new("Symbols")
    mat.use_nodes = True
    mat.use_backface_culling = True
    nodes = mat.node_tree.nodes
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 0.5
    image = bpy.data.images.load(texture, check_existing=False)
    image.name = "Symbols"
    tex = nodes.new("ShaderNodeTexImage")
    tex.image = image
    mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    return mat


def main(out_dir=None, only=None):
    P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "slots")
    mat = material(paint_texture(folder))
    objects = []
    for i, (name, make) in enumerate(SYMBOLS):
        if only and name not in only:
            continue
        s = Sym()
        make(s)
        obj = finish(s, "Sym_" + name, mat)
        tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        print(f"[slots] {obj.name}: {tris} tris")
        objects.append(obj)
        if out_dir is None:
            obj.location = ((len(objects) - 1) % 10 * 1.3, 0, -((len(objects) - 1) // 10) * 1.3)

    cabinets = []
    for k, (name, make) in enumerate(CABINETS):
        if only and "Cab_" + name not in only:
            continue
        s = Sym()
        bulbs = []
        make(s, bulbs)
        parts = [finish(s, "Cab_" + name, mat, fit=False, outline=0.01)]
        for g in range(3):
            b = Sym()
            for i, at in enumerate(bulbs):
                if i % 3 == g:
                    sphere(b, at, BULB, "CREAM", segs=8, rings=5, outline=False)
            parts.append(finish(b, f"Cab_{name}_Bulbs{g}", mat, fit=False))
        tris = sum(len(p.vertices) - 2 for o in parts for p in o.data.polygons)
        print(f"[slots] Cab_{name}: {tris} tris, {len(bulbs)} bulbs")
        cabinets.extend(parts)
        if out_dir is None:
            for o in parts:
                o.location = (k * 1.4, 0, -10.5)

    if out_dir is None:
        return objects + cabinets

    for file, group in (("SlotSymbols.fbx", objects), ("SlotCabinets.fbx", cabinets)):
        bpy.ops.object.select_all(action="DESELECT")
        for obj in group:
            obj.select_set(True)
        bpy.context.view_layer.objects.active = group[0]
        bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, file), use_selection=True,
                                 apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z",
                                 axis_up="Y", mesh_smooth_type="FACE", bake_space_transform=True,
                                 path_mode="RELATIVE")
    print(f"[slots] exported {len(objects)} symbols and {len(cabinets)} cabinet parts to {out_dir}")
    return objects + cabinets


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
