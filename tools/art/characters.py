"""The people, modelled and rigged from code (#76, #77).

    blender -b --factory-startup -P tools/art/characters.py -- <absolute path>/Assets/_Project/Art/Characters

Writes one FBX per body, `Body_<Kind>_<Name>.fbx` (Kind is Player, Native, Castaway or Barman), and
Textures/Characters.png, a ramp sheet of its own: skins, hair and cloth want colours the slots' sheet
has no room for. No folder: every body side by side in the open Blender.

A body is a skinned mesh on a Mixamo-named skeleton in T-pose, which Unity maps to a Humanoid avatar
by name, so the animation library retargets onto it. The joints sit where the player's ragdoll has
them (hips 0.95, legs at x 0.11 from 0.90, knees 0.48, shoulders 1.36 at x 0.20, neck 1.42), so
PlayerPrefabBuilder fits a body unscaled. Faces +z in Unity; the character's left is -x.

Every part is drawn on a list of bones and each vertex is weighted to the nearest of them by inverse
distance to the bone (to the sixth power, three at most): a sleeve bends at the elbow, a thigh blends
into the pelvis, and a hat, a hand or a shoe has one bone and does not bend at all. The outline is the
same inverted hull as everywhere else, weighted like what it outlines.
"""

import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import palms as P  # noqa: E402
import slots as S  # noqa: E402
import buildings as B  # noqa: E402

TAU = math.tau


def u(x, y, z):
    return Vector((-x, -z, y))


unity = B.unity

# ------------------------------------------------------------------------------------- the sheet

COLUMNS = 32
RAMPS = [
    ("SKIN_PALE", ["8A5A44", "F2C4A0", "FFEADA"]),
    ("SKIN_TAN", ["6A3E22", "D9965E", "FFD8B0"]),
    ("SKIN_BROWN", ["3E2214", "A8683E", "E0A878"]),
    ("SKIN_DARK", ["1E100A", "6A3E24", "B07A52"]),
    ("SUNBURN", ["7A2A1E", "F08A70", "FFD0C0"]),
    ("BLOND", ["6A4A10", "E8C050", "FFF2A8"]),
    ("BROWN_HAIR", ["1E1008", "5A3418", "9A6A3A"]),
    ("BLACK_HAIR", ["050508", "1A1A24", "4A4A5A"]),
    ("GINGER", ["5A1804", "D0601A", "FFB070"]),
    ("GREY_HAIR", ["5A5A60", "B8B8C0", "F4F4F8"]),
    ("WHITE", ["C8C8D0", "F8F8FC", "FFFFFF"]),
    ("CREAM", ["A8A090", "F2EEE2", "FFFFFF"]),
    ("OUTLINE", ["000000", "08040C", "120A16"]),
    ("DARK", ["050508", "16161E", "34343E"]),
    ("RED", ["4A0008", "C8101E", "FF6A5A"]),
    ("ORANGE", ["7A2604", "F07A14", "FFD070"]),
    ("YELLOW", ["7A5A04", "F2C81C", "FFF68A"]),
    ("GREEN", ["0E3A0E", "2EA040", "A0F070"]),
    ("TEAL", ["06343E", "2A9AAA", "A8F0F8"]),
    ("BLUE", ["061444", "2A5AD0", "A8C8FF"]),
    ("NAVY", ["040A24", "1A2A6A", "4A6AB0"]),
    ("OCHRE", ["4A1A08", "A8401A", "E0885A"]),
    ("PINK", ["8A2238", "F0607A", "FFC0C8"]),
    ("KHAKI", ["5A4A28", "C0A870", "F0E4B8"]),
    ("DENIM", ["0E1A34", "3A5A8A", "90B0D8"]),
    ("LEATHER", ["24120A", "6A4022", "B0845A"]),
    ("GOLD", ["5A3404", "D89A18", "FFF2A8"]),
    ("BONE", ["6A6450", "E8E0C8", "FFFFF4"]),
    ("STRAW", ["6A4E12", "D8B050", "FFF4B0"]),
    ("LEAF", ["0E300E", "2E8A2E", "9AE06A"]),
    ("MOUTH", ["2A0004", "7A1020", "D04050"]),
    ("WOOD", ["2A1408", "8A5A2E", "D0A060"]),
]
COL = {name: i for i, (name, _) in enumerate(RAMPS)}
assert len(RAMPS) == COLUMNS


def paint_texture(folder):
    os.makedirs(folder, exist_ok=True)
    stops = [[(0.0, a), (0.55, b), (1.0, c)] for _, (a, b, c) in RAMPS]
    path = os.path.join(folder, "Characters.png")
    P.write_png(path, P.CELL * COLUMNS, P.TEX_H, lambda x, v: P.ramp(stops[min(x // P.CELL, COLUMNS - 1)], v))
    return path


def material(texture):
    mat = bpy.data.materials.new("Characters")
    mat.use_nodes = True
    mat.use_backface_culling = True
    nodes = mat.node_tree.nodes
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 0.6
    image = bpy.data.images.load(texture, check_existing=False)
    image.name = "Characters"
    tex = nodes.new("ShaderNodeTexImage")
    tex.image = image
    mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    return mat


# ---------------------------------------------------------------------------------- the skeleton

BONES = ["Hips", "Spine", "Spine1", "Spine2", "Neck", "Head",
         "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
         "RightShoulder", "RightArm", "RightForeArm", "RightHand",
         "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase",
         "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase"]
PARENT = {"Spine": "Hips", "Spine1": "Spine", "Spine2": "Spine1", "Neck": "Spine2", "Head": "Neck"}
for _side in ("Left", "Right"):
    PARENT.update({_side + "Shoulder": "Spine2", _side + "Arm": _side + "Shoulder",
                   _side + "ForeArm": _side + "Arm", _side + "Hand": _side + "ForeArm",
                   _side + "UpLeg": "Hips", _side + "Leg": _side + "UpLeg",
                   _side + "Foot": _side + "Leg", _side + "ToeBase": _side + "Foot"})


class Frame:
    """Where the joints are, in Unity metres. The ragdoll's numbers by default; a body changes its
    girth, its head and its arms' thickness, not where it bends."""

    def __init__(self, girth=1.0, head=0.165, arms=1.0, legs=1.0, shoulders=0.20):
        self.g, self.hr, self.ga, self.gl = girth, head, arms, legs
        self.hip, self.leg_y, self.leg_x, self.knee, self.ankle = 0.95, 0.90, 0.11, 0.48, 0.085
        self.sh_y, self.sh_x, self.elbow_x, self.wrist_x = 1.36, shoulders, 0.46, 0.70
        self.neck, self.neck_len = 1.42, 0.06
        self.head_c = Vector((0, self.neck + self.neck_len + head * 0.92, 0.015))

    def bones(self):
        h, n = self.hip, self.neck
        step = (n - h - 0.1) / 3
        top = self.head_c.y + self.hr
        b = {
            "Hips": ((0, h, 0), (0, h + 0.1, 0)),
            "Spine": ((0, h + 0.1, 0), (0, h + 0.1 + step, 0)),
            "Spine1": ((0, h + 0.1 + step, 0), (0, h + 0.1 + 2 * step, 0)),
            "Spine2": ((0, h + 0.1 + 2 * step, 0), (0, n, 0)),
            "Neck": ((0, n, 0), (0, n + self.neck_len, 0)),
            "Head": ((0, n + self.neck_len, 0), (0, top, 0)),
        }
        for side, k in (("Left", -1), ("Right", 1)):
            y = self.sh_y
            b[side + "Shoulder"] = ((k * 0.03, y, 0), (k * self.sh_x, y, 0))
            b[side + "Arm"] = ((k * self.sh_x, y, 0), (k * self.elbow_x, y, -0.012))
            b[side + "ForeArm"] = ((k * self.elbow_x, y, -0.012), (k * self.wrist_x, y, 0))
            b[side + "Hand"] = ((k * self.wrist_x, y, 0), (k * (self.wrist_x + 0.1), y, 0))
            x = k * self.leg_x
            b[side + "UpLeg"] = ((x, self.leg_y, 0), (x, self.knee, 0.015))
            b[side + "Leg"] = ((x, self.knee, 0.015), (x, self.ankle, 0))
            b[side + "Foot"] = ((x, self.ankle, 0), (x, 0.025, 0.11))
            b[side + "ToeBase"] = ((x, 0.025, 0.11), (x, 0.025, 0.2))
        return {k: (Vector(a), Vector(c)) for k, (a, c) in b.items()}


# ------------------------------------------------------------------------------------- the figure

class Fig(S.Sym):
    """A Sym on our own sheet whose every part also remembers the bones it may be weighted to."""

    def __init__(self):
        super().__init__()
        self.bones = ["Hips"]
        self.rig = {}

    def on(self, *bones):
        self.bones = list(bones)
        return self

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
        self.rig[p] = list(self.bones)
        return faces


def ball(s, c, r, col, segs=10, rings=7, outline=True, bumps=0.0, seed=0):
    rx_, ry_, rz_ = r if isinstance(r, tuple) else (r, r, r)
    return S.sphere(s, u(*c), (rx_, rz_, ry_), col, segs=segs, rings=rings, outline=outline, bumps=bumps, seed=seed)


def basis(d):
    """Two axes across a direction: the first horizontal (across), the second the other way."""
    d = d.normalized()
    ref = Vector((0, 1, 0)) if abs(d.y) < 0.9 else Vector((0, 0, 1))
    a = d.cross(ref).normalized()
    return a, a.cross(d).normalized()


def loft(s, rings, col, paint=None, caps=(True, True), outline=True, smooth=True):
    """Rings of Unity points joined in order; paint(i) may give the band after ring i its own colour."""
    before = s.begin()
    vs = [[s.bm.verts.new(u(*p)) for p in ring] for ring in rings]
    n = len(rings[0])
    bands = []
    for i in range(len(vs) - 1):
        bands.append([s.bm.faces.new((vs[i][k], vs[i][(k + 1) % n], vs[i + 1][(k + 1) % n], vs[i + 1][k]))
                      for k in range(n)])
    if caps[0]:
        s.bm.faces.new(vs[0][::-1])
    if caps[1]:
        s.bm.faces.new(vs[-1])
    faces = s.end(before, col, smooth, outline)
    bmesh.ops.recalc_face_normals(s.bm, faces=faces)
    if paint:
        for i, band in enumerate(bands):
            c = paint(i)
            if c:
                for f in band:
                    f[s.col] = COL[c]
    return bands


def tube(s, pts, radii, col, sides=8, paint=None, caps=(True, True), outline=True):
    """A limb through Unity points. `radii` is one per point (a list) or one for all; each is a number or
    (across, the other way), as basis() has them."""
    pts = [Vector(p) for p in pts]
    rings = []
    for i, p in enumerate(pts):
        a, b = basis(pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)])
        r = radii[i] if isinstance(radii, list) else radii
        ra, rb = r if isinstance(r, tuple) else (r, r)
        rings.append([p + a * math.cos(k * TAU / sides) * ra + b * math.sin(k * TAU / sides) * rb
                      for k in range(sides)])
    return loft(s, rings, col, paint=paint, caps=caps, outline=outline)


def ellipse_rings(rows, n=14):
    """[(y, half width, half depth, forward)] as horizontal rings."""
    return [[Vector((rx * math.cos(k * TAU / n), y, dz + rz * math.sin(k * TAU / n))) for k in range(n)]
            for y, rx, rz, dz in rows]


def mirror(fn):
    for k in (-1, 1):
        fn(k)


# --------------------------------------------------------------------------------------- the body

def torso(s, f, top, bottom, waist=0.93, belly=0.0, chest=1.0, paint=None, n=14):
    """Pelvis to the base of the neck. `top` above the waist, `bottom` below; paint(y) overrides."""
    g = f.g
    rows = [(0.80, 0.13 * g, 0.10 * g, 0.0), (0.88, 0.165 * g, 0.115 * g, 0.0), (0.97, 0.16 * g, 0.115 * g, 0.01),
            (1.06, 0.155 * g + belly, 0.115 * g + belly, 0.02 + belly * 0.8),
            (1.15, 0.165 * g + belly * 0.5, 0.12 * g + belly * 0.4, 0.02 + belly * 0.3),
            (1.25, 0.185 * g * chest, 0.12 * g, 0.02), (1.32, 0.19 * g * chest, 0.11 * g, 0.01),
            (1.38, 0.15 * chest, 0.09, 0.0), (1.425, 0.065, 0.06, 0.0)]
    s.on("Hips", "Spine", "Spine1", "Spine2", "LeftShoulder", "RightShoulder", "LeftUpLeg", "RightUpLeg")
    # Rings every 3 cm, so a stripe or a waistband lands where it is asked for.
    fine = []
    for a, b in zip(rows, rows[1:]):
        k = max(1, round((b[0] - a[0]) / 0.03))
        fine += [tuple(x + (y - x) * i / k for x, y in zip(a, b)) for i in range(k)]
    fine.append(rows[-1])

    def band(i):
        y = (fine[i][0] + fine[i + 1][0]) / 2
        c = paint(y) if paint else None
        return c or (top if y > waist else bottom)
    loft(s, ellipse_rings(fine, n), top, paint=band)
    return rows


def neck(s, f, skin):
    s.on("Spine2", "Neck", "Head")
    tube(s, [(0, f.neck - 0.05, 0), (0, f.neck + f.neck_len + 0.05, 0.01)], [0.06, 0.055], skin, sides=8)


def arm(s, f, k, skin, sleeve=None, sleeve_to=0.34, cuff=None, hand=None):
    """One arm out along x (k = -1 left, 1 right): a sleeve to |x| = sleeve_to, then skin, then a mitten."""
    ga, y = f.ga, f.sh_y
    xs = [0.10, f.sh_x, 0.30, 0.38, f.elbow_x, 0.56, 0.64, f.wrist_x + 0.01]
    rs = [0.07, 0.068, 0.062, 0.056, 0.05, 0.05, 0.044, 0.038]
    pts, radii, cols = [], [], []
    for i, (x, r) in enumerate(zip(xs, rs)):
        pts.append((k * x, y, -0.006 if 0.3 < x < 0.6 else 0))
        radii.append(r * ga)
    if sleeve:
        # The hem: the sleeve's end ring and the skin's first at the same x, the skin a little inside.
        j = next(i for i, x in enumerate(xs) if x > sleeve_to)
        pts.insert(j, (k * sleeve_to, y, 0))
        radii.insert(j, (rs[j - 1] * 0.5 + rs[j] * 0.5) * ga + 0.012)
        pts.insert(j + 1, (k * (sleeve_to + 0.002), y, 0))
        radii.insert(j + 1, (rs[j - 1] * 0.5 + rs[j] * 0.5) * ga * 0.9)
        cols = [sleeve if i < j else (skin if i > j else sleeve) for i in range(len(pts) - 1)]
    s.on("Spine2", k < 0 and "LeftShoulder" or "RightShoulder", k < 0 and "LeftArm" or "RightArm",
         k < 0 and "LeftForeArm" or "RightForeArm")
    tube(s, pts, radii, skin, paint=(lambda i: cols[i]) if cols else None)
    side = "Left" if k < 0 else "Right"
    s.on("Spine2", side + "Shoulder", side + "Arm")
    ball(s, (k * (f.sh_x - 0.01), y + 0.005, 0), 0.074 * ga, sleeve or skin, segs=10, rings=7)
    if cuff:
        s.on(side + "ForeArm")
        tube(s, [(k * (f.wrist_x - 0.06), y, 0), (k * (f.wrist_x - 0.015), y, 0)], 0.05 * ga, cuff, sides=8)
    mitten(s, f, k, hand or skin)


def mitten(s, f, k, skin):
    side = "Left" if k < 0 else "Right"
    s.on(side + "Hand")
    x = k * (f.wrist_x + 0.055)
    ball(s, (x, f.sh_y - 0.005, 0.0), (0.065, 0.038, 0.058), skin, segs=10, rings=6)
    # Four fat fingers in one lump, and a thumb forward: big cartoon hands.
    ball(s, (x + k * 0.06, f.sh_y - 0.014, 0.0), (0.045, 0.03, 0.056), skin, segs=8, rings=5)
    tube(s, [(x - k * 0.015, f.sh_y - 0.008, 0.04), (x + k * 0.02, f.sh_y - 0.01, 0.088)], [0.024, 0.02], skin,
         sides=6)


def leg(s, f, k, skin, pants, pants_to=0.6, hem=True):
    """One leg straight down; trousers or shorts to y = pants_to (0 = to the ankle)."""
    gl = f.gl * f.g ** 0.5
    x = k * f.leg_x
    ys = [f.leg_y + 0.03, 0.8, 0.68, 0.56, f.knee, 0.38, 0.27, 0.17, f.ankle + 0.02]
    rs = [0.095, 0.09, 0.08, 0.07, 0.062, 0.066, 0.06, 0.05, 0.045]
    ys[-1] = 0.055  # into the foot, which hangs off the ankle below it
    pts = [(x, y, 0.015 if 0.4 < y < 0.6 else 0.0) for y in ys]
    radii = [r * gl for r in rs]
    cols = None
    if pants_to > f.ankle + 0.03:
        j = next(i for i, y in enumerate(ys) if y < pants_to)
        r = (rs[j - 1] * 0.5 + rs[j] * 0.5) * gl
        if hem:
            pts.insert(j, (x, pants_to, 0.01))
            radii.insert(j, r + 0.014)
            pts.insert(j + 1, (x, pants_to - 0.002, 0.01))
            radii.insert(j + 1, r * 0.92)
            cols = [pants if i <= j else skin for i in range(len(pts) - 1)]
        else:
            cols = [pants if i < j else skin for i in range(len(pts) - 1)]
    elif pants_to > 0:
        cols = [pants] * (len(pts) - 1)
    side = "Left" if k < 0 else "Right"
    s.on("Hips", side + "UpLeg", side + "Leg")
    tube(s, pts, radii, skin, sides=9, paint=(lambda i: cols[i]) if cols else None)


def shoe(s, f, k, kind, col="WHITE", trim="RED", skin="SKIN_TAN"):
    side = "Left" if k < 0 else "Right"
    s.on(side + "Foot")
    x = k * f.leg_x
    if kind == "sneaker":
        ball(s, (x, 0.06, 0.05), (0.06, 0.05, 0.12), col, segs=10, rings=6)
        tube(s, [(x, 0.02, -0.06), (x, 0.02, 0.17)], [(0.062, 0.022), (0.06, 0.02)], "WHITE", sides=8)
        tube(s, [(x + k * 0.058, 0.07, -0.02), (x + k * 0.062, 0.05, 0.07)], 0.012, trim, sides=4, outline=False)
        ball(s, (x, 0.1, 0.07), (0.03, 0.012, 0.035), col, segs=6, rings=4, outline=False)
    elif kind == "sandal":
        ball(s, (x, 0.045, 0.05), (0.05, 0.04, 0.11), skin, segs=10, rings=6)
        tube(s, [(x, 0.012, -0.06), (x, 0.012, 0.17)], [(0.058, 0.012), (0.056, 0.012)], col, sides=8)
        tube(s, [(x - 0.052, 0.04, 0.06), (x, 0.08, 0.07), (x + 0.052, 0.04, 0.06)], 0.012, trim, sides=4)
    elif kind == "barefoot":
        ball(s, (x, 0.045, 0.05), (0.052, 0.042, 0.115), skin, segs=10, rings=6)
        for t in range(4):
            ball(s, (x - k * (0.035 - t * 0.022), 0.03, 0.155 - t * 0.008), 0.018 - t * 0.002, skin, segs=6,
                 rings=4, outline=False)
    elif kind == "boot":
        tube(s, [(x, 0.0, 0.0), (x, 0.24, 0.0)], [0.062, 0.058], col, sides=9)
        ball(s, (x, 0.06, 0.06), (0.062, 0.055, 0.115), col, segs=10, rings=6)
        tube(s, [(x, 0.015, -0.07), (x, 0.015, 0.17)], [(0.066, 0.02), (0.064, 0.02)], trim, sides=8)
    else:  # shoe
        ball(s, (x, 0.055, 0.05), (0.056, 0.045, 0.115), col, segs=10, rings=6)
        tube(s, [(x, 0.012, -0.06), (x, 0.012, 0.16)], [(0.058, 0.014), (0.056, 0.014)], trim, sides=8)


# --------------------------------------------------------------------------------------- the head

def head(s, f, skin, eyes="round", nose="ball", brows="BROWN_HAIR", mouth="smile", ears=True, cheeks=0.0,
         nose_col=None, iris="DARK"):
    s.on("Head")
    c, r = f.head_c, f.hr
    ball(s, c, (r * 0.96, r, r * 0.98), skin, segs=14, rings=10)
    if cheeks:
        ball(s, (c.x, c.y - r * 0.42, c.z + r * 0.18), (r * 0.82, r * 0.5, r * 0.72), skin, segs=12, rings=7)
    if ears:
        mirror(lambda k: ball(s, (k * r * 0.95, c.y - 0.01, c.z - 0.01), (0.022, 0.045, 0.032), skin, segs=8,
                              rings=5))
    front = c.z + r * 0.9
    if nose == "ball":
        ball(s, (0, c.y - 0.03, front + 0.02), (0.034, 0.03, 0.03), nose_col or skin, segs=8, rings=6)
    elif nose == "long":
        tube(s, [(0, c.y + 0.01, front - 0.01), (0, c.y - 0.04, front + 0.045)], [0.02, 0.026], nose_col or skin,
             sides=6)
    if eyes == "round":
        def eye(k):
            ball(s, (k * 0.058, c.y + 0.025, front - 0.018), (0.042, 0.05, 0.03), "WHITE", segs=10, rings=6)
            ball(s, (k * 0.052, c.y + 0.02, front + 0.008), (0.018, 0.022, 0.01), iris, segs=8, rings=5,
                 outline=False)
            ball(s, (k * 0.046, c.y + 0.032, front + 0.016), 0.006, "WHITE", segs=4, rings=3, outline=False)
        mirror(eye)
    elif eyes == "sleepy":
        def eye(k):
            ball(s, (k * 0.058, c.y + 0.02, front - 0.02), (0.04, 0.035, 0.028), "WHITE", segs=10, rings=6)
            ball(s, (k * 0.054, c.y + 0.015, front + 0.004), (0.016, 0.016, 0.01), iris, segs=8, rings=5,
                 outline=False)
            ball(s, (k * 0.058, c.y + 0.04, front - 0.01), (0.046, 0.022, 0.032), skin, segs=10, rings=5,
                 outline=False)
        mirror(eye)
    if brows:
        mirror(lambda k: tube(s, [(k * 0.03, c.y + 0.078, front - 0.012), (k * 0.09, c.y + 0.07, front - 0.03)],
                              [0.012, 0.009], brows, sides=5, outline=False))
    if mouth == "smile":
        tube(s, [(-0.05, c.y - 0.075, front - 0.022), (-0.02, c.y - 0.09, front - 0.01), (0.02, c.y - 0.09, front - 0.01),
                 (0.05, c.y - 0.075, front - 0.022)], 0.011, "MOUTH", sides=5, outline=False)
    elif mouth == "grin":
        ball(s, (0, c.y - 0.085, front - 0.03), (0.055, 0.025, 0.02), "MOUTH", segs=10, rings=5, outline=False)
        ball(s, (0, c.y - 0.075, front - 0.022), (0.045, 0.01, 0.015), "WHITE", segs=8, rings=4, outline=False)
    elif mouth == "o":
        ball(s, (0, c.y - 0.085, front - 0.025), (0.02, 0.024, 0.015), "MOUTH", segs=8, rings=5, outline=False)


def hair_cap(s, f, col, low=-0.45, fringe=0.42, bumps=0.0, seed=0, puff=1.0):
    """Hair over the skull, a little proud of it: the face cut away under a fringe at `fringe` of the
    radius, the back down to `low`."""
    s.on("Head")
    c, r = f.head_c, f.hr
    faces = ball(s, (c.x, c.y + r * 0.05, c.z - r * 0.04), (r * 1.04 * puff, r * 1.0 * puff, r * 1.03 * puff), col,
                 segs=14, rings=10, bumps=bumps, seed=seed)
    dead = []
    for face in faces:
        p = unity(face.calc_center_median()) - c
        if p.y < low * r or (p.z > 0.0 and p.y < fringe * r):
            dead.append(face)
    bmesh.ops.delete(s.bm, geom=dead, context="FACES")


def spikes(s, f, col, n=9, seed=1):
    s.on("Head")
    c, r = f.head_c, f.hr
    rng = random.Random(seed)
    for i in range(n):
        a = TAU * i / n + rng.uniform(-0.2, 0.2)
        tilt = rng.uniform(0.3, 0.75)
        base = Vector((math.sin(a) * r * 0.55 * tilt, c.y + r * 0.75, c.z + math.cos(a) * r * 0.55 * tilt - 0.02))
        tip = base + Vector((math.sin(a) * 0.08 * tilt, rng.uniform(0.07, 0.11), math.cos(a) * 0.06 * tilt - 0.03))
        tube(s, [base, tip], [0.045, 0.004], col, sides=5, caps=(False, False))


def beard(s, f, col, long=0.0, seed=3):
    s.on("Head")
    c, r = f.head_c, f.hr
    ball(s, (0, c.y - r * 0.55 - long * 0.4, c.z + r * 0.45), (r * 0.85, r * 0.5 + long * 0.5, r * 0.6), col,
         segs=12, rings=7, bumps=0.08, seed=seed)
    mirror(lambda k: ball(s, (k * r * 0.72, c.y - r * 0.2, c.z + r * 0.25), (r * 0.25, r * 0.45, r * 0.5), col,
                          segs=8, rings=6, bumps=0.06, seed=seed + k))


def moustache(s, f, col, droop=0.0):
    s.on("Head")
    c, r = f.head_c, f.hr
    front = c.z + r * 0.9
    tube(s, [(-0.075, c.y - 0.075 - droop, front - 0.04), (-0.03, c.y - 0.05, front + 0.0), (0, c.y - 0.055, front + 0.01),
             (0.03, c.y - 0.05, front + 0.0), (0.075, c.y - 0.075 - droop, front - 0.04)],
         [0.008, 0.022, 0.02, 0.022, 0.008], col, sides=6)


def shades(s, f, col="DARK", frame="DARK", y=0.025, z=0.0):
    s.on("Head")
    c, r = f.head_c, f.hr
    front = c.z + r * 0.9 + z
    mirror(lambda k: ball(s, (k * 0.058, c.y + y, front + 0.005), (0.05, 0.036, 0.014), col, segs=10, rings=5))
    tube(s, [(-0.13, c.y + y + 0.01, front - 0.06), (-0.1, c.y + y + 0.015, front - 0.005), (0.1, c.y + y + 0.015, front - 0.005),
             (0.13, c.y + y + 0.01, front - 0.06)], 0.007, frame, sides=4, outline=False)


def necklace(s, f, col, n=11, r=0.13, y=1.36, size=0.018, tooth=None, seed=5):
    s.on("Spine2")
    rng = random.Random(seed)
    for i in range(n):
        a = math.pi * (0.15 + 0.7 * i / (n - 1))
        p = (math.cos(a) * r, y - math.sin(a) * 0.09, 0.07 + math.sin(a) * 0.095)
        if tooth and i % 2 == 0:
            tube(s, [p, (p[0], p[1] - 0.05, p[2] + 0.01)], [size * 0.9, 0.003], tooth, sides=5, caps=(True, False),
                 outline=False)
        else:
            ball(s, p, size * rng.uniform(0.85, 1.15), col, segs=6, rings=4, outline=False)


def belt(s, f, col, y=0.93, buckle="GOLD"):
    s.on("Hips", "Spine")
    g = f.g
    loft(s, ellipse_rings([(y - 0.025, 0.168 * g, 0.12 * g, 0.004), (y + 0.025, 0.166 * g, 0.12 * g, 0.006)]), col)
    if buckle:
        ball(s, (0, y, 0.12 * g + 0.01), (0.035, 0.03, 0.012), buckle, segs=6, rings=4)


def collar(s, f, col):
    s.on("Spine2", "Neck")
    mirror(lambda k: tube(s, [(k * 0.01, 1.43, 0.065), (k * 0.07, 1.405, 0.05), (k * 0.1, 1.40, -0.02)],
                          [(0.035, 0.008), (0.035, 0.01), (0.025, 0.008)], col, sides=4))


def dots(s, cols, rows, n, seed, r=0.026):
    """Prints scattered over a torso's surface: a Hawaiian shirt's flowers."""
    rng = random.Random(seed)
    for i in range(n):
        y = rng.uniform(1.0, 1.3)
        a = rng.uniform(0, TAU)
        for (y0, rx0, rz0, dz0), (y1_, rx1, rz1, dz1) in zip(rows, rows[1:]):
            if y0 <= y <= y1_:
                t = (y - y0) / (y1_ - y0)
                rx, rz, dz = rx0 + (rx1 - rx0) * t, rz0 + (rz1 - rz0) * t, dz0 + (dz1 - dz0) * t
                break
        p = Vector((rx * math.cos(a), y, dz + rz * math.sin(a)))
        normal = Vector((math.cos(a) / rx, 0, math.sin(a) / rz)).normalized()
        # A lentil lying on the cloth: flat along the surface's normal.
        turn = Vector((0, 0, 1)).rotation_difference(u(*normal)).to_matrix()
        S.sphere(s, u(*(p + normal * 0.003)), (r, r, r * 0.3), cols[i % len(cols)], segs=6, rings=4, outline=False,
                 rot=turn)


# ------------------------------------------------------------------------------------------ people

def gus(s):
    """The tourist: sunburnt, a Hawaiian shirt with a camera on it, khaki shorts, sandals, a bucket hat."""
    f = Frame(girth=1.18, head=0.17, arms=1.1)
    skin = "SKIN_PALE"
    rows = torso(s, f, "ORANGE", "KHAKI", waist=0.95, belly=0.05)
    s.on("Hips", "Spine", "Spine1", "Spine2")
    dots(s, ["PINK", "YELLOW", "WHITE"], rows, 30, seed=11)
    belt(s, f, "LEATHER")
    collar(s, f, "ORANGE")
    neck(s, f, skin)
    mirror(lambda k: arm(s, f, k, skin, sleeve="ORANGE", sleeve_to=0.36))
    mirror(lambda k: leg(s, f, k, skin, "KHAKI", pants_to=0.6))
    mirror(lambda k: shoe(s, f, k, "sandal", col="LEATHER", trim="LEATHER", skin=skin))
    head(s, f, skin, nose="ball", nose_col="SUNBURN", brows="GINGER", mouth="grin", cheeks=0.3)
    moustache(s, f, "GINGER")
    # Bucket hat, sunglasses pushed up onto it, a camera on a strap.
    s.on("Head")
    c, r = f.head_c, f.hr
    ball(s, (0, c.y + r * 0.42, c.z - 0.005), (r * 1.05, r * 0.62, r * 1.05), "CREAM", segs=14, rings=8)
    tube(s, [(0, c.y + r * 0.14, c.z), (0, c.y + r * 0.3, c.z)], [r * 1.42, r * 1.06], "CREAM", sides=16)
    shades(s, f, y=r * 0.62, z=0.02)
    s.on("Spine1", "Spine2")
    tube(s, [(-0.12, 1.37, 0.09), (-0.07, 1.27, 0.17), (0.0, 1.19, 0.21)], 0.008, "DARK", sides=4, outline=False)
    tube(s, [(0.0, 1.19, 0.21), (0.07, 1.27, 0.17), (0.12, 1.37, 0.09)], 0.008, "DARK", sides=4, outline=False)
    tube(s, [(0, 1.16, 0.2), (0, 1.16, 0.25)], (0.06, 0.04), "DARK", sides=4)
    tube(s, [(0.02, 1.165, 0.245), (0.02, 1.165, 0.275)], 0.022, "DARK", sides=8)
    ball(s, (0.02, 1.165, 0.276), (0.016, 0.016, 0.005), "BLUE", segs=6, rings=4, outline=False)
    return f


def kiki(s):
    """The athlete: a tank top, running shorts, wristbands, a high ponytail and hoop earrings."""
    f = Frame(girth=0.94, head=0.16, arms=0.92, shoulders=0.195)
    skin = "SKIN_BROWN"
    torso(s, f, "TEAL", "NAVY", waist=0.97, chest=0.98,
          paint=lambda y: "WHITE" if 1.12 < y < 1.16 else None)
    neck(s, f, skin)
    # The tank top's straps: the shoulders are skin.
    mirror(lambda k: arm(s, f, k, skin, cuff="PINK"))
    mirror(lambda k: leg(s, f, k, skin, "NAVY", pants_to=0.74))
    mirror(lambda k: shoe(s, f, k, "sneaker", col="WHITE", trim="PINK"))
    head(s, f, skin, brows="BLACK_HAIR", mouth="smile")
    hair_cap(s, f, "BLACK_HAIR", puff=0.98)
    s.on("Head")
    c, r = f.head_c, f.hr
    ball(s, (0, c.y + r * 0.95, c.z - r * 0.35), (0.07, 0.06, 0.07), "BLACK_HAIR", segs=10, rings=6)
    tube(s, [(0, c.y + r * 1.0, c.z - r * 0.45), (0, c.y + r * 1.15, c.z - r * 0.95), (0, c.y + r * 0.6, c.z - r * 1.45),
             (0, c.y - r * 0.1, c.z - r * 1.35)], [0.05, 0.065, 0.05, 0.012], "BLACK_HAIR", sides=8)
    tube(s, [(0, c.y + r * 1.0, c.z - r * 0.55), (0, c.y + r * 1.03, c.z - r * 0.7)], 0.05, "PINK", sides=8)
    mirror(lambda k: tube(s, [(k * r * 0.98, c.y - 0.04, c.z), (k * r * 1.05, c.y - 0.09, c.z + 0.02),
                              (k * r * 0.98, c.y - 0.13, c.z), (k * r * 0.93, c.y - 0.09, c.z - 0.02),
                              (k * r * 0.98, c.y - 0.04, c.z)], 0.006, "GOLD", sides=4, caps=(False, False), outline=False))
    return f


def rex(s):
    """The surfer: tall, blond spikes, shades, a shell necklace, board shorts to the knee, barefoot."""
    f = Frame(girth=1.0, head=0.16, arms=1.0, shoulders=0.21)
    skin = "SKIN_TAN"
    torso(s, f, "WHITE", "BLUE", waist=0.96, chest=1.08,
          paint=lambda y: "RED" if 1.18 < y < 1.24 else None)
    neck(s, f, skin)
    mirror(lambda k: arm(s, f, k, skin, sleeve="WHITE", sleeve_to=0.32))
    mirror(lambda k: leg(s, f, k, skin, "BLUE", pants_to=0.5))
    s.on("Hips", "LeftUpLeg")
    tube(s, [(-f.leg_x - 0.085, 0.92, 0.0), (-f.leg_x - 0.08, 0.52, 0.01)], (0.012, 0.02), "YELLOW", sides=4,
         outline=False)
    s.on("Hips", "RightUpLeg")
    tube(s, [(f.leg_x + 0.085, 0.92, 0.0), (f.leg_x + 0.08, 0.52, 0.01)], (0.012, 0.02), "YELLOW", sides=4,
         outline=False)
    mirror(lambda k: shoe(s, f, k, "barefoot", skin=skin))
    head(s, f, skin, brows="BLOND", mouth="grin")
    hair_cap(s, f, "BLOND", puff=0.98)
    spikes(s, f, "BLOND", n=10, seed=7)
    shades(s, f, col="TEAL", frame="DARK")
    necklace(s, f, "BONE", n=9, r=0.1, y=1.39, size=0.015, tooth="CREAM", seed=8)
    return f


def mo(s):
    """The old captain: a striped jumper with the sleeves pushed up, a white beard, a cap and a pipe."""
    f = Frame(girth=1.1, head=0.17, arms=1.08, shoulders=0.205)
    skin = "SKIN_PALE"
    stripes = lambda y: "WHITE" if int((y - 0.95) / 0.045) % 2 else None  # noqa: E731
    torso(s, f, "NAVY", "DENIM", waist=0.93, belly=0.03, paint=lambda y: y > 0.95 and stripes(y))
    neck(s, f, skin)
    mirror(lambda k: arm(s, f, k, skin, sleeve="NAVY", sleeve_to=0.5))
    mirror(lambda k: leg(s, f, k, skin, "DENIM", pants_to=0.2, hem=False))
    mirror(lambda k: shoe(s, f, k, "boot", col="LEATHER", trim="DARK"))
    belt(s, f, "DARK", y=0.94)
    head(s, f, skin, eyes="sleepy", nose="ball", nose_col="SUNBURN", brows="GREY_HAIR", mouth=None, cheeks=0.2)
    beard(s, f, "GREY_HAIR", long=0.04)
    moustache(s, f, "GREY_HAIR", droop=0.01)
    s.on("Head")
    c, r = f.head_c, f.hr
    ball(s, (0, c.y + r * 0.55, c.z - 0.01), (r * 1.06, r * 0.5, r * 1.08), "WHITE", segs=14, rings=7)
    tube(s, [(0, c.y + r * 0.32, c.z), (0, c.y + r * 0.5, c.z)], r * 1.04, "NAVY", sides=16)
    tube(s, [(0, c.y + r * 0.34, c.z + r * 0.6), (0, c.y + r * 0.3, c.z + r * 1.3)], [(r * 0.85, 0.012), (r * 0.6, 0.01)],
         "DARK", sides=8)
    ball(s, (0, c.y + r * 0.42, c.z + r * 1.03), (0.03, 0.025, 0.01), "GOLD", segs=6, rings=4)
    front = c.z + r * 0.9
    tube(s, [(0.04, c.y - 0.08, front - 0.01), (0.09, c.y - 0.1, front + 0.06)], 0.01, "WOOD", sides=5)
    tube(s, [(0.09, c.y - 0.13, front + 0.065), (0.09, c.y - 0.075, front + 0.065)], [0.025, 0.028], "WOOD", sides=8)
    return f


# ---------------------------------------------------------------------------------------- islanders
#
# People who live here, not people dressed up: hide and rope instead of anything bought, paint that
# was smeared on by hand (white clay, red ochre, ash, mud), hair that has never seen a comb, bone and
# teeth for ornaments, and faces that do not want you here. Wild, not gory.

def daub(s, skin, fn):
    """Body paint: every face still in `skin` whose centre fn() gives a colour takes it."""
    for face in s.bm.faces:
        if face[s.col] == COL[skin]:
            c = fn(unity(face.calc_center_median()))
            if c:
                face[s.col] = COL[c]


def noise(p, k=7.0):
    """A cheap repeatable 0..1 from a position: where a hand left more paint and where less."""
    return (math.sin(p.x * 41.3 * k + p.y * 17.1 * k) * math.sin(p.z * 23.7 * k - p.y * 9.3 * k) + 1) * 0.5


def smear(s, pts, col, on, w=0.012, step=0.015, seed=0, dots=False):
    """Paint put on with a finger: flat dabs along a stroke (or at each point, for dots), each pressed
    onto the nearest face still coloured `on`, so a stroke follows the body however it curves and
    never lands on an eye or a necklace."""
    index, verts, polys = {}, [], []
    for face in s.bm.faces:
        if face[s.col] != COL[on]:
            continue
        poly = []
        for v in face.verts:
            if v not in index:
                index[v] = len(verts)
                verts.append(v.co.copy())
            poly.append(index[v])
        polys.append(poly)
    tree = BVHTree.FromPolygons(verts, polys)
    rng = random.Random(seed)
    pts = [Vector(p) for p in pts]
    path = pts
    if not dots:
        path = []
        for a, b in zip(pts, pts[1:]):
            n = max(1, round((b - a).length / step))
            path += [a.lerp(b, i / n) for i in range(n)]
        path.append(pts[-1])
    marks = []
    for p in path:
        q = u(*p)
        hit, normal, _, _ = tree.find_nearest(q)
        if hit is not None:
            marks.append((hit + (normal if normal.dot(q - hit) > 0 else -normal) * 0.002,
                          normal if normal.dot(q - hit) > 0 else -normal))

    def face(vs, normal):
        f = s.bm.faces.new(vs)
        f.normal_update()
        if f.normal.dot(normal) < 0:
            f.normal_flip()

    before = s.begin()
    if dots:
        # A fingertip: a flat disc on the skin.
        for c, normal in marks:
            a = normal.orthogonal().normalized()
            b = normal.cross(a)
            r = w * rng.uniform(0.85, 1.15)
            face([s.bm.verts.new(c + (a * math.cos(k * TAU / 7) + b * math.sin(k * TAU / 7)) * r) for k in range(7)],
                 normal)
    elif len(marks) > 1:
        # A finger drawn along: a flat ribbon, widest in the middle where it pressed hardest.
        rows = []
        for i, (c, normal) in enumerate(marks):
            along = marks[min(i + 1, len(marks) - 1)][0] - marks[max(i - 1, 0)][0]
            side = normal.cross(along).normalized()
            taper = 0.35 + 0.65 * max(0.0, math.sin(math.pi * i / (len(marks) - 1))) ** 0.5
            r = w * taper * rng.uniform(0.85, 1.1)
            rows.append((s.bm.verts.new(c + side * r), s.bm.verts.new(c - side * r), normal))
        for (a0, b0, n0), (a1, b1, n1) in zip(rows, rows[1:]):
            face([a0, b0, b1, a1], n0 + n1)
    s.end(before, col, True, outline=False)


def ring(x, y, rad, n=16, wobble=0.012, seed=0):
    """Points round an arm (out along x at height y): a band painted on by hand, not by a ruler."""
    rng = random.Random(seed)
    return [(x + rng.uniform(-wobble, wobble), y + rad * math.cos(a), rad * math.sin(a))
            for a in (i * TAU / n for i in range(n + 1))]


def loincloth(s, f, hide="LEATHER", rope="STRAW", length=0.3):
    """A rope round the hips with a hide flap hanging in front and behind."""
    g = f.g
    s.on("Hips", "Spine")
    tube(s, [Vector((0.17 * g * math.cos(k * TAU / 12), 0.95, 0.12 * g * math.sin(k * TAU / 12) + 0.005))
             for k in range(13)], 0.012, rope, sides=4, caps=(False, False), outline=False)
    s.on("Hips", "LeftUpLeg", "RightUpLeg")
    for z in (1, -1):
        front = z * (0.125 * g + 0.012)
        tube(s, [(0, 0.95, front), (0, 0.95 - length * 0.5, front + z * 0.012), (0, 0.95 - length, front + z * 0.02)],
             [(0.1, 0.012), (0.095, 0.01), (0.075, 0.008)], hide, sides=6)


def dreads(s, f, col, n=13, length=0.3, seed=1):
    """Matted locks round the back and sides of the head, hanging to the shoulders."""
    s.on("Head")
    c, r = f.head_c, f.hr
    rng = random.Random(seed)
    for i in range(n):
        a = math.pi * (-0.62 + 1.24 * i / (n - 1)) + rng.uniform(-0.08, 0.08)
        root = Vector((math.sin(a) * r * 0.9, c.y + r * 0.3, c.z - math.cos(a) * r * 0.88))
        out = Vector((math.sin(a), 0, -math.cos(a)))
        drop = length * rng.uniform(0.75, 1.1)
        tube(s, [root, root + out * 0.05 + Vector((0, -drop * 0.45, 0)),
                 root + out * (0.07 + rng.uniform(-0.02, 0.02)) + Vector((rng.uniform(-0.02, 0.02), -drop, 0))],
             [0.024, 0.022, 0.014], col, sides=5)


def scowl(s, f, skin, iris="DARK", teeth=True):
    """A face that does not want you here: small eyes under a heavy brow, a mouth set hard."""
    s.on("Head")
    c, r = f.head_c, f.hr
    front = c.z + r * 0.9
    ball(s, c, (r * 0.96, r, r * 0.98), skin, segs=14, rings=10)
    ball(s, (0, c.y - r * 0.45, c.z + r * 0.2), (r * 0.8, r * 0.48, r * 0.7), skin, segs=12, rings=7)  # the jaw
    mirror(lambda k: ball(s, (k * r * 0.95, c.y - 0.01, c.z - 0.01), (0.022, 0.045, 0.032), skin, segs=8, rings=5))
    ball(s, (0, c.y - 0.025, front + 0.022), (0.04, 0.032, 0.03), skin, segs=8, rings=6)  # a broad nose

    def eye(k):
        ball(s, (k * 0.058, c.y + 0.02, front - 0.022), (0.036, 0.03, 0.026), "CREAM", segs=10, rings=6)
        ball(s, (k * 0.054, c.y + 0.018, front - 0.0), (0.014, 0.016, 0.01), iris, segs=8, rings=5, outline=False)
        # The brow ridge, down at the middle.
        tube(s, [(k * 0.02, c.y + 0.03, front + 0.0), (k * 0.06, c.y + 0.055, front - 0.008),
                 (k * 0.1, c.y + 0.06, front - 0.035)], [0.018, 0.02, 0.012], skin, sides=6)
    mirror(eye)
    ball(s, (0, c.y - 0.088, front - 0.02), (0.05, 0.016, 0.016), "MOUTH", segs=10, rings=4, outline=False)
    if teeth:
        for t in range(-2, 3):
            ball(s, (t * 0.017, c.y - 0.082, front - 0.008), (0.007, 0.012, 0.006), "BONE", segs=4, rings=3,
                 outline=False)


def bones_worn(s, f, nose=True, ears=True):
    s.on("Head")
    c, r = f.head_c, f.hr
    front = c.z + r * 0.9
    if nose:
        tube(s, [(-0.06, c.y - 0.045, front + 0.03), (0.06, c.y - 0.045, front + 0.03)], [0.009, 0.007], "BONE",
             sides=5)
    if ears:
        mirror(lambda k: tube(s, [(k * r * 0.96, c.y - 0.03, c.z), (k * r * 1.1, c.y - 0.03, c.z)], 0.016, "BONE",
                              sides=6))


def islander(s, f, skin, hide="LEATHER"):
    """Bare skin, a hide loincloth over hide briefs, wraps at wrists and ankles, barefoot."""
    torso(s, f, skin, hide, waist=0.9)
    neck(s, f, skin)
    mirror(lambda k: arm(s, f, k, skin, cuff=hide))
    mirror(lambda k: leg(s, f, k, skin, hide, pants_to=0.84, hem=False))
    mirror(lambda k: shoe(s, f, k, "barefoot", skin=skin))
    for k, side in ((-1, "Left"), (1, "Right")):
        s.on(side + "Leg")
        tube(s, [(k * f.leg_x, 0.13, 0), (k * f.leg_x, 0.2, 0)], [0.055, 0.058], hide, sides=7, outline=False)
    loincloth(s, f, hide)


def native_hunter(s):
    """Dreadlocks, white clay drawn down the chest and round the arms, a red ochre band across the eyes."""
    f = Frame(girth=0.94, head=0.16, arms=0.96, shoulders=0.205)
    skin = "SKIN_TAN"
    islander(s, f, skin)
    scowl(s, f, skin)
    hair_cap(s, f, "BLACK_HAIR", fringe=0.5, bumps=0.06, seed=31, puff=1.03)
    dreads(s, f, "BLACK_HAIR", n=13, length=0.3, seed=32)
    bones_worn(s, f, nose=False)
    c, r = f.head_c, f.hr
    front = c.z + r
    s.on("Head")
    smear(s, [(math.sin(a) * (r + 0.05), c.y + 0.028, c.z + math.cos(a) * (r + 0.05))
               for a in (math.radians(d) for d in range(-80, 81, 20))], "OCHRE", skin, w=0.024, seed=1)
    s.on("Spine1", "Spine2")
    for k in (-1, 1):
        for i in range(3):
            x0 = k * (0.045 + i * 0.032)
            smear(s, [(x0, 1.34, 0.2), (x0 + k * 0.018, 1.22, 0.2), (x0 + k * 0.03, 1.07, 0.2)], "BONE", skin,
                  w=0.0085, seed=10 + i + k)
    for k, side in ((-1, "Left"), (1, "Right")):
        s.on(side + "Arm", side + "ForeArm")
        for j, x in enumerate((0.3, 0.355, 0.56)):
            smear(s, ring(k * x, f.sh_y, 0.06, n=12, seed=j + k), "BONE", skin, w=0.008, seed=20 + j)
    necklace(s, f, "LEATHER", n=11, tooth="BONE", seed=33)
    return f


def native_mud(s):
    """Shaved but for a topknot, a bone through the nose, mud to the thighs and a red hand on the chest."""
    f = Frame(girth=1.02, head=0.162, arms=1.04, shoulders=0.21)
    skin = "SKIN_BROWN"
    islander(s, f, skin, hide="WOOD")
    scowl(s, f, skin, iris="DARK")
    bones_worn(s, f)
    c, r = f.head_c, f.hr
    front = c.z + r
    s.on("Head")
    ball(s, (0, c.y + r * 1.0, c.z - r * 0.25), (0.06, 0.07, 0.06), "BLACK_HAIR", segs=10, rings=6, bumps=0.08,
         seed=41)
    tube(s, [(0, c.y + r * 1.05, c.z - r * 0.3), (0, c.y + r * 1.3, c.z - r * 0.45)], [0.022, 0.018], "LEATHER",
         sides=6, outline=False)
    # White dots under the eyes and up the brow, the way you would put them on with a fingertip.
    for k in (-1, 1):
        smear(s, [(k * (0.058 + math.cos(a) * 0.056), c.y + 0.02 + math.sin(a) * 0.05, front + 0.06)
                  for a in (math.radians(d) for d in range(-75, 50, 20))], "BONE", skin, w=0.0075, dots=True)
    smear(s, [(0, c.y + 0.07 + i * 0.024, front + 0.06) for i in range(4)], "BONE", skin, w=0.0075, dots=True)
    # A red hand pressed on the chest: the palm, four fingers and a thumb.
    s.on("Spine1", "Spine2")
    hx, hy = 0.075, 1.17
    smear(s, [(hx + dx, hy + dy, 0.22) for dx, dy in ((-0.015, -0.012), (0.015, -0.012), (0, 0.008), (-0.017, 0.018),
                                                      (0.017, 0.018), (0, -0.03))], "OCHRE", skin, w=0.022, dots=True)
    for i, a in enumerate((-0.45, -0.15, 0.15, 0.45)):
        smear(s, [(hx + math.sin(a) * 0.04, hy + math.cos(a) * 0.04, 0.22),
                  (hx + math.sin(a) * (0.09 - abs(a) * 0.03), hy + math.cos(a) * (0.09 - abs(a) * 0.03), 0.22)],
              "OCHRE", skin, w=0.009, seed=30 + i)
    smear(s, [(hx - 0.035, hy - 0.01, 0.22), (hx - 0.075, hy + 0.025, 0.22)], "OCHRE", skin, w=0.01, seed=35)
    necklace(s, f, "BONE", n=9, size=0.02, seed=42)
    daub(s, skin, lambda p: "SKIN_DARK" if p.y < 0.55 - 0.12 * noise(p, 3.0) else None)
    return f


def native_skull(s):
    """The headhunter: an animal's skull over the face, horns and all, ash rubbed over chest and arms."""
    f = Frame(girth=1.06, head=0.165, arms=1.06, shoulders=0.215)
    skin = "SKIN_DARK"
    islander(s, f, skin)
    head(s, f, skin, eyes=None, brows=None, mouth=None, nose=None)
    hair_cap(s, f, "BLACK_HAIR", bumps=0.06, seed=12, puff=1.02)
    dreads(s, f, "BLACK_HAIR", n=9, length=0.22, seed=14)
    s.on("Spine1", "Spine2")
    for k in (-1, 1):
        for i in range(4):
            x0 = k * (0.07 + i * 0.03)
            smear(s, [(x0 + k * 0.02, 1.37, 0.2), (x0, 1.24, 0.2), (x0 - k * 0.025, 1.1, 0.2)], "GREY_HAIR", skin,
                  w=0.011, seed=20 + i + 4 * k)
    for k, side in ((-1, "Left"), (1, "Right")):
        s.on(side + "ForeArm")
        for j in range(3):
            smear(s, [(k * 0.48, f.sh_y + 0.09, (j - 1) * 0.03), (k * 0.66, f.sh_y + 0.09, (j - 1) * 0.025)],
                  "GREY_HAIR", skin, w=0.01, seed=40 + j + k)
    necklace(s, f, "LEATHER", n=13, tooth="BONE", seed=13)
    s.on("Head")
    c, r = f.head_c, f.hr
    # A long-faced beast's skull, a goat's or a deer's: cranium, a snout tapering to a point, the
    # sockets and the nasal slits dark, teeth along the jaw, horns sweeping back.
    ball(s, (0, c.y + 0.02, c.z + r * 0.4), (r * 0.94, r * 0.9, r * 0.8), "BONE", segs=12, rings=8)
    tube(s, [(0, c.y + 0.005, c.z + r * 0.85), (0, c.y - 0.035, c.z + r * 1.4), (0, c.y - 0.075, c.z + r * 1.8)],
         [(0.075, 0.062), (0.052, 0.042), (0.02, 0.016)], "BONE", sides=8)
    z = c.z + r * 1.12
    mirror(lambda k: ball(s, (k * 0.064, c.y + 0.03, z - 0.012), (0.05, 0.044, 0.018), "DARK", segs=8, rings=5,
                          outline=False))
    mirror(lambda k: ball(s, (k * 0.012, c.y - 0.005, c.z + r * 1.5), (0.006, 0.004, 0.03), "DARK", segs=5, rings=3,
                          outline=False))
    for t in range(5):
        mirror(lambda k: ball(s, (k * (0.05 - t * 0.0065), c.y - 0.07 - t * 0.008, c.z + r * (0.95 + t * 0.15)),
                              (0.007, 0.014, 0.007), "BONE", segs=4, rings=3, outline=False))
    mirror(lambda k: tube(s, [(k * 0.1, c.y + 0.1, c.z + 0.06), (k * 0.15, c.y + 0.19, c.z + 0.0),
                              (k * 0.17, c.y + 0.2, c.z - 0.1), (k * 0.15, c.y + 0.14, c.z - 0.17)],
                          [0.03, 0.024, 0.014, 0.003], "BONE", sides=7))
    return f


def native_brute(s):
    """The big one: pale, the face daubed white with the eyes blacked in, a matted beard, mud to the knee."""
    f = Frame(girth=1.22, head=0.172, arms=1.18, shoulders=0.215)
    skin = "SKIN_PALE"
    islander(s, f, skin, hide="LEATHER")
    scowl(s, f, skin, teeth=False)
    hair_cap(s, f, "BROWN_HAIR", fringe=0.55, bumps=0.1, seed=51, puff=1.04)
    dreads(s, f, "BROWN_HAIR", n=11, length=0.26, seed=52)
    beard(s, f, "BROWN_HAIR", long=0.08, seed=53)
    c, r = f.head_c, f.hr
    front = c.z + r
    s.on("Spine1", "Spine2")
    smear(s, [(0, 1.37, 0.24), (0.005, 1.22, 0.24), (-0.005, 1.04, 0.24)], "OCHRE", skin, w=0.016, seed=2)
    for k in (-1, 1):
        smear(s, [(k * 0.06, 1.3, 0.24), (k * 0.13, 1.2, 0.24)], "OCHRE", skin, w=0.011, seed=3 + k)
    daub(s, skin, lambda p: "BONE" if p.y > c.y - r * 0.75 and p.z > c.z + r * 0.3
         else "SKIN_TAN" if p.y < 0.5 - 0.1 * noise(p, 3.0) else None)
    # The white mask's eyes, blacked in with soot.
    s.on("Head")
    for k in (-1, 1):
        smear(s, [(k * 0.058 + math.cos(a) * rad, c.y + 0.02 + math.sin(a) * rad * 0.9, front + 0.06)
                  for rad in (0.03, 0.045) for a in (i * TAU / 9 for i in range(9))], "DARK", "BONE", w=0.014,
              dots=True)
    necklace(s, f, "BONE", n=11, tooth="BONE", seed=54)
    return f


# --------------------------------------------------------------------------------- the castaway

def castaway(s):
    """Years on the beach: a torn shirt, cut-off trousers, a beard to the chest, wild hair, barefoot."""
    f = Frame(girth=0.92, head=0.165, arms=0.9)
    skin = "SKIN_TAN"
    rows = torso(s, f, "CREAM", "DENIM", waist=0.95, chest=0.96)
    s.on("Hips", "Spine")
    rng = random.Random(21)
    hem = [Vector((rows[2][1] * 1.04 * math.cos(k * TAU / 14), 0.95 + rng.uniform(-0.05, 0.02),
                   rows[2][3] + rows[2][2] * 1.04 * math.sin(k * TAU / 14))) for k in range(14)]
    loft(s, [[Vector((p.x, 1.0, p.z)) for p in hem], hem], "CREAM", caps=(False, False))
    neck(s, f, skin)
    mirror(lambda k: arm(s, f, k, skin, sleeve="CREAM", sleeve_to=0.27))
    mirror(lambda k: leg(s, f, k, skin, "DENIM", pants_to=0.36))
    belt(s, f, "STRAW", buckle=None)
    mirror(lambda k: shoe(s, f, k, "barefoot", skin=skin))
    head(s, f, skin, eyes="round", brows="BROWN_HAIR", mouth=None)
    hair_cap(s, f, "BROWN_HAIR", bumps=0.12, seed=22, puff=1.1)
    beard(s, f, "BROWN_HAIR", long=0.14, seed=23)
    s.on("Head")
    c, r = f.head_c, f.hr
    ball(s, (0, c.y - r * 0.2, c.z - r * 0.55), (r * 1.0, r * 1.1, r * 0.6), "BROWN_HAIR", segs=10, rings=7, bumps=0.1,
         seed=24)
    return f


# ------------------------------------------------------------------------------------- the barman

def barman(s):
    """The casino's barman: a red waistcoat over a white shirt, a bow tie, slick hair and a moustache."""
    f = Frame(girth=1.02, head=0.16, arms=0.98)
    skin = "SKIN_PALE"
    vest = lambda y: "WHITE" if y > 1.33 else None  # noqa: E731
    torso(s, f, "RED", "DARK", waist=0.93, paint=vest)
    s.on("Spine", "Spine1", "Spine2")
    tube(s, [(0, 0.97, 0.142), (0, 1.34, 0.138)], (0.032, 0.006), "WHITE", sides=4, outline=False)
    for i in range(4):
        ball(s, (0, 1.0 + i * 0.075, 0.148), 0.012, "GOLD", segs=6, rings=4, outline=False)
    neck(s, f, skin)
    s.on("Spine2", "Neck")
    mirror(lambda k: ball(s, (k * 0.04, 1.4, 0.075), (0.04, 0.028, 0.015), "DARK", segs=8, rings=4))
    ball(s, (0, 1.4, 0.082), 0.015, "DARK", segs=6, rings=4)
    collar(s, f, "WHITE")
    mirror(lambda k: arm(s, f, k, skin, sleeve="WHITE", sleeve_to=0.62))
    mirror(lambda k: leg(s, f, k, skin, "DARK", pants_to=0.1, hem=False))
    mirror(lambda k: shoe(s, f, k, "shoe", col="DARK", trim="DARK"))
    head(s, f, skin, eyes="sleepy", brows="BLACK_HAIR", mouth="smile", nose="long")
    moustache(s, f, "BLACK_HAIR", droop=-0.012)
    hair_cap(s, f, "BLACK_HAIR", puff=0.99)
    s.on("Head")
    c, r = f.head_c, f.hr
    ball(s, (0.02, c.y + r * 0.82, c.z + r * 0.3), (r * 0.7, r * 0.3, r * 0.6), "BLACK_HAIR", segs=10, rings=6)
    return f


# ----------------------------------------------------------------------------------------- Radu

def radu(s):
    """Radu Voinea, bush pilot, 1957 (#275): a leather flying jacket zipped to a white silk scarf,
    khaki breeches into boots, a leather flying helmet with its goggles pushed up, a pencil moustache."""
    f = Frame(girth=1.0, head=0.165, arms=1.0, shoulders=0.205)
    skin = "SKIN_PALE"
    torso(s, f, "LEATHER", "KHAKI", waist=0.95, chest=1.02, paint=lambda y: "DARK" if 0.97 < y < 1.01 else None)
    s.on("Spine", "Spine1", "Spine2")
    tube(s, [(0, 1.0, 0.142), (0, 1.36, 0.13)], 0.006, "GOLD", sides=4, outline=False)
    mirror(lambda k: ball(s, (k * 0.09, 1.22, 0.135), (0.045, 0.04, 0.012), "DARK", segs=6, rings=4, outline=False))
    neck(s, f, skin)
    collar(s, f, "LEATHER")
    mirror(lambda k: arm(s, f, k, skin, sleeve="LEATHER", sleeve_to=0.66, cuff="DARK"))
    mirror(lambda k: leg(s, f, k, skin, "KHAKI", pants_to=0.1, hem=False))
    mirror(lambda k: shoe(s, f, k, "boot", col="LEATHER", trim="DARK"))
    belt(s, f, "DARK", y=0.94)
    head(s, f, skin, eyes="round", nose="long", brows="BROWN_HAIR", mouth="smile")
    moustache(s, f, "BROWN_HAIR", droop=-0.012)
    # The scarf: a loop round the neck, one end down the front of the jacket.
    s.on("Spine2", "Neck")
    loft(s, ellipse_rings([(1.39, 0.086, 0.08, 0.012), (1.45, 0.074, 0.07, 0.014)]), "WHITE")
    tube(s, [(0.035, 1.41, 0.085), (0.06, 1.3, 0.15), (0.05, 1.17, 0.158)], [0.03, 0.028, 0.02], "WHITE", sides=5)
    # The helmet, cut away round the face like hair, its ear flaps, and the goggles on the brow.
    hair_cap(s, f, "LEATHER", low=-0.6, fringe=0.5, puff=1.05)
    s.on("Head")
    c, r = f.head_c, f.hr
    mirror(lambda k: ball(s, (k * r * 1.0, c.y - r * 0.3, c.z - r * 0.02), (r * 0.16, r * 0.45, r * 0.38), "LEATHER",
                          segs=8, rings=5))
    tube(s, [(-r * 1.04, c.y + r * 0.55, c.z - r * 0.1), (-r * 0.7, c.y + r * 0.62, c.z + r * 0.72),
             (r * 0.7, c.y + r * 0.62, c.z + r * 0.72), (r * 1.04, c.y + r * 0.55, c.z - r * 0.1)], 0.012, "DARK",
         sides=4, outline=False)
    mirror(lambda k: ball(s, (k * 0.056, c.y + r * 0.66, c.z + r * 0.88), (0.042, 0.034, 0.022), "GOLD", segs=10,
                          rings=5))
    mirror(lambda k: ball(s, (k * 0.056, c.y + r * 0.66, c.z + r * 0.92), (0.032, 0.025, 0.012), "TEAL", segs=8,
                          rings=4, outline=False))
    return f


BODIES = [
    ("Player_Gus", gus), ("Player_Kiki", kiki), ("Player_Rex", rex), ("Player_Mo", mo),
    ("Native_Hunter", native_hunter), ("Native_Mud", native_mud), ("Native_Skull", native_skull),
    ("Native_Brute", native_brute), ("Castaway", castaway), ("Barman", barman), ("Pilot_Radu", radu),
]


# ------------------------------------------------------------------------------------- rigging

def seg_dist(p, a, b):
    ab = b - a
    t = min(max((p - a).dot(ab) / max(ab.length_squared, 1e-9), 0.0), 1.0)
    return (p - (a + ab * t)).length


def finish(s, f, name, mat, outline=0.01):
    # A frame may bring its own skeleton and height: the animals do (animals.py).
    order, parents, tall = getattr(f, "order", BONES), getattr(f, "parents", PARENT), getattr(f, "tall", 1.85)
    bm = s.bm
    bm.normal_update()
    # Shaded by height over the whole body and by which way the surface faces, not per part as the
    # props are: a sleeve and the shoulder under it are one surface and must not meet at a seam.
    uv = bm.loops.layers.uv.new("UVMap")
    for face in bm.faces:
        for loop in face.loops:
            n = loop.vert.normal
            v = 0.2 + 0.3 * min(loop.vert.co.z / tall, 1.0) + 0.3 * (0.5 + 0.5 * n.z) + 0.1 * max(0.0, -n.y)
            loop[uv].uv = P.uv_on(COLUMNS, face[s.col], 0.5, min(max(v, 0.0), 1.0))

    faces = [x for x in bm.faces if x[s.part] in s.outlined]
    ret = bmesh.ops.duplicate(bm, geom=faces)
    shell = [g for g in ret["geom"] if isinstance(g, bmesh.types.BMFace)]
    verts = {v for x in shell for v in x.verts}
    bm.normal_update()
    normals = {v: v.normal.copy() for v in verts}
    for v in verts:
        v.co += normals[v] * outline
    bmesh.ops.reverse_faces(bm, faces=shell)
    for x in shell:
        x.smooth = True
        x[s.col] = COL["OUTLINE"]
        for loop in x.loops:
            loop[uv].uv = P.uv_on(COLUMNS, COL["OUTLINE"], 0.5, 0.5)

    # Weights: each vertex to the nearest of its part's bones.
    bones = f.bones()
    deform = bm.verts.layers.deform.verify()
    part = {}
    for face in bm.faces:
        for v in face.verts:
            part.setdefault(v, face[s.part])
    for v, p in part.items():
        q = unity(v.co)
        ws = sorted(((1.0 / (seg_dist(q, *bones[b]) + 0.012) ** 6, b) for b in s.rig[p]), reverse=True)[:3]
        total = sum(w for w, _ in ws)
        ws = [(w / total, b) for w, b in ws if w / total > 0.03]
        total = sum(w for w, _ in ws)
        for w, b in ws:
            v[deform][order.index(b)] = w / total

    bmesh.ops.triangulate(bm, faces=[x for x in bm.faces if len(x.verts) > 4])
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(mat)
    body = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(body)
    for b in order:
        body.vertex_groups.new(name=b)

    arm = bpy.data.armatures.new(name + "_Armature")
    rig = bpy.data.objects.new("Armature", arm)
    bpy.context.scene.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    for b in order:
        eb = arm.edit_bones.new(b)
        eb.head, eb.tail = u(*bones[b][0]), u(*bones[b][1])
        eb.roll = 0.0
    for b in order:
        if b in parents:
            arm.edit_bones[b].parent = arm.edit_bones[parents[b]]
    bpy.ops.object.mode_set(mode="OBJECT")
    body.parent = rig
    body.modifiers.new("Armature", "ARMATURE").object = rig
    return rig, body


def tri_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def main(out_dir=None, only=None):
    P.clear()
    for block in (bpy.data.armatures,):
        for item in list(block):
            block.remove(item)
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "characters")
    mat = material(paint_texture(folder))
    made = []
    for i, (name, make) in enumerate(BODIES):
        if only and name not in only:
            continue
        s = Fig()
        f = make(s)
        rig, body = finish(s, f, "Body_" + name, mat)
        print(f"[characters] Body_{name}: {tri_count(body)} tris")
        if out_dir is None:
            rig.location = (-(len(made)) * 1.8, 0, 0)
            rig.name = "Body_" + name + "_Rig"
            made.append(rig)
            continue
        bpy.ops.object.select_all(action="DESELECT")
        rig.select_set(True)
        body.select_set(True)
        bpy.context.view_layer.objects.active = rig
        bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, f"Body_{name}.fbx"), use_selection=True,
                                 object_types={"ARMATURE", "MESH"}, apply_unit_scale=True,
                                 apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
                                 mesh_smooth_type="FACE", add_leaf_bones=False, bake_anim=False,
                                 use_armature_deform_only=True, path_mode="RELATIVE")
        for obj in (body, rig):
            data = obj.data
            bpy.data.objects.remove(obj, do_unlink=True)
            (bpy.data.meshes if isinstance(data, bpy.types.Mesh) else bpy.data.armatures).remove(data)
        made.append(name)
    print(f"[characters] {len(made)} bodies" + (f" to {out_dir}" if out_dir else ""))
    return made


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
