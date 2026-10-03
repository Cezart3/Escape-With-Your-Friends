"""The animals, modelled, rigged and animated from code (epic #287).

    blender -b --factory-startup -P tools/art/animals.py -- <absolute path>/Assets/_Project/Art/Models/Animals

Writes `Animal_<Name>.fbx` for the boar, the deer, the stag, the jaguar and the gull, and
Textures/Animals.png, the characters' ramp sheet under the name the material is given. No folder:
every animal side by side in the open Blender, at rest.

Each body is drawn and weighted as the people are (characters.py: parts on lists of bones, inverse
distance to the sixth power, the inverted-hull outline), at the size of its species' body box in
AnimalFactory, nose to +z, feet at 0. The skeleton is the animal's own, so the rig is Generic and
every clip lives in the same file: Idle, Walk, Run (Fly for the gull) and Death, which AnimalArt
finds by name. A clip is a function of time in [0, 1] keyed on every frame, so a loop's last frame
is its first.
"""

import math
import os
import shutil
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import characters as C  # noqa: E402
import palms as P  # noqa: E402

TAU = math.tau
FPS = 30
I3 = Matrix.Identity(3)
u = C.u


# ------------------------------------------------------------------------------------- the frames

def legs(prefix):
    return [f"{prefix}{s}{i}" for s in "LR" for i in (1, 2, 3)]


class Quad:
    """Joints in Unity metres for a four-legged animal: (y, z) pairs, legs mirrored at +-x.

    A leg is its top, two joints and the ground; the bones run top to joint to joint to the ground,
    and the hoof (or paw) is the last stretch of the third."""

    order = ["Hips", "Chest", "Neck", "Head", "Tail1", "Tail2"] + legs("Hind") + legs("Front")
    parents = {"Chest": "Hips", "Neck": "Chest", "Head": "Neck", "Tail1": "Hips", "Tail2": "Tail1"}
    for _s in "LR":
        parents.update({f"Hind{_s}1": "Hips", f"Hind{_s}2": f"Hind{_s}1", f"Hind{_s}3": f"Hind{_s}2",
                        f"Front{_s}1": "Chest", f"Front{_s}2": f"Front{_s}1", f"Front{_s}3": f"Front{_s}2"})

    def __init__(self, tall, hip, shoulder, hind, front, hx, fx, neck, head, nose, tail, belly):
        self.tall, self.hip, self.shoulder = tall, hip, shoulder
        self.hind, self.front, self.hx, self.fx = hind, front, hx, fx
        self.neck, self.head, self.nose, self.tail = neck, head, nose, tail
        # The half height of the body at the hips, which is how far it falls when it dies on its side.
        self.belly = belly

    def leg(self, end, k):
        """Top, the two joints, a point above the ground and the ground, in Unity points."""
        top, joints, x = (self.hip, self.hind, self.hx) if end == "Hind" else (self.shoulder, self.front, self.fx)
        (y1, z1), (y2, z2), (_, z3) = joints
        return [(k * x, top[0], top[1]), (k * x, y1, z1), (k * x, y2, z2),
                (k * x, 0.07 * self.tall / 1.3, z3 - 0.01), (k * x, 0.0, z3)]

    def bones(self):
        hy, hz = self.hip
        sy, sz = self.shoulder
        mid = (0, (hy + sy) / 2, (hz + sz) / 2)
        b = {
            "Hips": ((0, hy, hz), mid),
            "Chest": (mid, (0, sy, sz)),
            "Neck": ((0, *self.neck), (0, *self.head)),
            "Head": ((0, *self.head), (0, *self.nose)),
            "Tail1": ((0, *self.tail[0]), (0, *self.tail[1])),
            "Tail2": ((0, *self.tail[1]), (0, *self.tail[2])),
        }
        for s, k in (("L", -1), ("R", 1)):
            for end in ("Hind", "Front"):
                p = self.leg(end, k)
                b[f"{end}{s}1"] = (p[0], p[1])
                b[f"{end}{s}2"] = (p[1], p[2])
                b[f"{end}{s}3"] = (p[2], p[4])
        return {k: (Vector(a), Vector(c)) for k, (a, c) in b.items()}


class Bird:
    """The gull: a body, a neck and a head, a tail, two wings of two bones folded along the body and
    two legs."""

    order = ["Body", "Neck", "Head", "Tail", "WingL1", "WingL2", "WingR1", "WingR2", "LegL", "LegR"]
    parents = {"Neck": "Body", "Head": "Neck", "Tail": "Body", "WingL1": "Body", "WingL2": "WingL1",
               "WingR1": "Body", "WingR2": "WingR1", "LegL": "Body", "LegR": "Body"}
    tall = 0.32
    belly = 0.075

    def bones(self):
        b = {
            "Body": ((0, 0.17, -0.12), (0, 0.18, 0.07)),
            "Neck": ((0, 0.20, 0.07), (0, 0.25, 0.11)),
            "Head": ((0, 0.25, 0.11), (0, 0.25, 0.245)),
            "Tail": ((0, 0.18, -0.12), (0, 0.17, -0.27)),
        }
        for s, k in (("L", -1), ("R", 1)):
            b[f"Wing{s}1"] = ((k * 0.08, 0.20, 0.07), (k * 0.08, 0.195, -0.08))
            b[f"Wing{s}2"] = ((k * 0.08, 0.195, -0.08), (k * 0.08, 0.19, -0.28))
            b[f"Leg{s}"] = ((k * 0.03, 0.12, 0.0), (k * 0.03, 0.01, 0.01))
        return {k: (Vector(a), Vector(c)) for k, (a, c) in b.items()}


# ------------------------------------------------------------------------------------- the parts

def trunk(s, rows, col, n=12, paint=None, caps=(True, True), outline=True):
    """[(z, centre height, half width, half height)] as rings across the body, joined nose-ward."""
    rings = [[(rx * math.cos(k * TAU / n), y + ry * math.sin(k * TAU / n), z) for k in range(n)]
             for z, y, rx, ry in rows]
    return C.loft(s, rings, col, paint=paint, caps=caps, outline=outline)


def top_of(rows, z):
    """The height of the back above z, from the trunk's rows."""
    for (z0, y0, _, r0), (z1, y1, _, r1) in zip(rows, rows[1:]):
        if z0 <= z <= z1:
            t = (z - z0) / (z1 - z0)
            return y0 + r0 + (y1 + r1 - y0 - r0) * t
    return rows[-1][1] + rows[-1][3]


def eyes(s, f, at, r, col="DARK", glint=True):
    """A pair of eyes at (x, y, z) and its mirror, with a glint so they are alive at ten metres."""
    x, y, z = at
    s.on("Head")
    for k in (-1, 1):
        C.ball(s, (k * x, y, z), r, col, segs=6, rings=4, outline=False)
        if glint:
            C.ball(s, (k * (x + 0.25 * r), y + 0.3 * r, z + 0.6 * r), 0.25 * r, "WHITE", segs=4, rings=3,
                   outline=False)


def four_legs(s, f, col, hind, front, thighs, low="DARK", sides=6):
    """Each leg a tube from inside the body to the ground, its radii per point, and a thigh of the
    given half sizes where it leaves the body; `low` paints the hoof, the stretch below the last
    joint's point above the ground."""
    last = 3
    for end, radii, thigh in (("Hind", hind, thighs[0]), ("Front", front, thighs[1])):
        body = "Hips" if end == "Hind" else "Chest"
        for side, k in (("L", -1), ("R", 1)):
            p = f.leg(end, k)
            s.on(body, f"{end}{side}1", f"{end}{side}2", f"{end}{side}3")
            C.tube(s, p, radii, col, sides=sides, paint=(lambda i: low if i == last else None) if low else None)
            s.on(body, f"{end}{side}1")
            C.ball(s, tuple(Vector(p[0]).lerp(Vector(p[1]), 0.4)), thigh, col, segs=7, rings=4)


def deer_like(s, coat, rows, f, ruff=None):
    """The deer and the stag: the same long legs and narrow head, a pale rump and throat."""
    s.on("Hips", "Chest")
    trunk(s, rows, coat)
    # Pale underneath and round the tail, as a deer is.
    C.daub(s, coat, lambda p: "CREAM" if p.y < 0.70 or p.z < -0.57 else None)

    s.on("Chest", "Neck", "Head")
    C.tube(s, [(0, 0.93, 0.36), (0, 1.06, 0.50), (0, 1.17, 0.60)],
           [(0.10, 0.13), (0.075, 0.085), (0.065, 0.07)], ruff or coat, sides=8)
    s.on("Head")
    C.ball(s, (0, 1.20, 0.65), (0.075, 0.08, 0.10), coat, segs=10, rings=7)
    C.ball(s, (0, 1.14, 0.79), (0.05, 0.055, 0.085), coat, segs=8, rings=6)
    C.daub(s, coat, lambda p: "CREAM" if p.z > 0.6 and p.y < 1.135 else None)
    C.ball(s, (0, 1.145, 0.873), (0.03, 0.024, 0.02), "DARK", segs=6, rings=4, outline=False)
    eyes(s, f, (0.068, 1.225, 0.70), 0.022)
    for k in (-1, 1):
        C.tube(s, [(k * 0.05, 1.26, 0.62), (k * 0.12, 1.31, 0.60), (k * 0.20, 1.34, 0.58)],
               [(0.025, 0.012), (0.045, 0.015), (0.012, 0.006)], coat, sides=6)

    s.on("Hips", "Tail1", "Tail2")
    C.tube(s, [(0, 0.95, -0.57), (0, 0.90, -0.64), (0, 0.82, -0.68)], [0.04, 0.045, 0.02], coat, sides=6,
           paint=lambda i: "CREAM" if i == 1 else None)
    four_legs(s, f, coat, [0.09, 0.06, 0.035, 0.03, 0.034], [0.07, 0.05, 0.035, 0.03, 0.034],
              ((0.065, 0.14, 0.10), (0.06, 0.11, 0.08)))


DEER_ROWS = [(-0.64, 0.88, 0.05, 0.06), (-0.58, 0.88, 0.12, 0.15), (-0.44, 0.87, 0.16, 0.19),
             (-0.20, 0.85, 0.15, 0.18), (0.05, 0.85, 0.16, 0.20), (0.26, 0.87, 0.17, 0.22),
             (0.40, 0.92, 0.14, 0.18), (0.47, 0.98, 0.08, 0.10)]


def deer_frame(tall):
    return Quad(tall, hip=(0.90, -0.40), shoulder=(0.86, 0.30),
                hind=[(0.62, -0.30), (0.36, -0.50), (0.0, -0.44)],
                front=[(0.58, 0.25), (0.30, 0.30), (0.0, 0.31)], hx=0.11, fx=0.12,
                neck=(0.95, 0.40), head=(1.18, 0.60), nose=(1.12, 0.88),
                tail=[(0.95, -0.57), (0.90, -0.64), (0.82, -0.68)], belly=0.17)


def deer(s):
    f = deer_frame(1.35)
    deer_like(s, "WOOD", DEER_ROWS, f)
    return f


def stag(s):
    """The deer's bigger brother: darker, a shaggy neck, and the antlers the rifle is for."""
    f = deer_frame(1.6)
    rows = [(z, y, rx + 0.02, ry + 0.01) for z, y, rx, ry in DEER_ROWS]
    deer_like(s, "LEATHER", rows, f, ruff="BROWN_HAIR")
    s.on("Head")
    for k in (-1, 1):
        C.tube(s, [(k * 0.045, 1.27, 0.63), (k * 0.10, 1.36, 0.60), (k * 0.17, 1.45, 0.55), (k * 0.22, 1.53, 0.53),
                   (k * 0.24, 1.60, 0.57)], [0.022, 0.019, 0.016, 0.012, 0.005], "BONE", sides=5)
        for a, b in (((k * 0.07, 1.31, 0.62), (k * 0.09, 1.37, 0.73)),
                     ((k * 0.15, 1.43, 0.56), (k * 0.17, 1.51, 0.67)),
                     ((k * 0.21, 1.51, 0.53), (k * 0.30, 1.57, 0.49))):
            C.tube(s, [a, b], [0.012, 0.004], "BONE", sides=4)
    return f


def boar(s):
    """Low, heavy in front, a black bristle ridge, tusks, and a temper."""
    rows = [(-0.66, 0.54, 0.07, 0.08), (-0.60, 0.54, 0.17, 0.20), (-0.42, 0.54, 0.22, 0.25),
            (-0.15, 0.54, 0.23, 0.27), (0.12, 0.56, 0.25, 0.30), (0.32, 0.57, 0.24, 0.29),
            (0.46, 0.56, 0.19, 0.23)]
    f = Quad(0.9, hip=(0.54, -0.44), shoulder=(0.56, 0.28),
             hind=[(0.38, -0.36), (0.20, -0.50), (0.0, -0.46)],
             front=[(0.34, 0.25), (0.16, 0.29), (0.0, 0.30)], hx=0.12, fx=0.13,
             neck=(0.58, 0.40), head=(0.58, 0.52), nose=(0.43, 0.86),
             tail=[(0.60, -0.64), (0.52, -0.69), (0.44, -0.71)], belly=0.24)
    s.on("Hips", "Chest")
    trunk(s, rows, "BROWN_HAIR")

    # The bristles: a jagged blade along the spine, from the rump to between the ears.
    ridge = []
    for i in range(14):
        z = -0.32 + i * 0.06
        y = top_of(rows, min(z, 0.46)) - 0.03
        h = (0.05 if i % 2 else 0.10) * (1.0 - abs(i - 8) / 12)
        ridge.append([(-0.03, y, z), (0, y + h, z - 0.02), (0.03, y, z)])
    s.on("Hips", "Chest", "Neck")
    C.loft(s, ridge, "BLACK_HAIR")

    s.on("Neck", "Head")
    head = [(0.40, 0.58, 0.17, 0.20), (0.54, 0.55, 0.15, 0.17), (0.66, 0.49, 0.10, 0.11),
            (0.78, 0.44, 0.065, 0.07), (0.84, 0.43, 0.06, 0.065)]
    trunk(s, head, "BROWN_HAIR", n=10)
    s.on("Head")
    C.ball(s, (0, 0.43, 0.85), (0.064, 0.06, 0.022), "SKIN_DARK", segs=10, rings=5)
    for k in (-1, 1):
        C.ball(s, (k * 0.022, 0.43, 0.869), 0.012, "DARK", segs=5, rings=3, outline=False)
        C.tube(s, [(k * 0.055, 0.41, 0.76), (k * 0.09, 0.44, 0.78), (k * 0.10, 0.50, 0.76)],
               [0.016, 0.011, 0.003], "BONE", sides=5)
        C.tube(s, [(k * 0.08, 0.70, 0.48), (k * 0.12, 0.78, 0.44), (k * 0.13, 0.84, 0.40)],
               [(0.035, 0.012), (0.03, 0.01), (0.006, 0.004)], "BROWN_HAIR", sides=6)
    eyes(s, f, (0.098, 0.605, 0.625), 0.016)

    s.on("Hips", "Tail1", "Tail2")
    C.tube(s, [(0, 0.60, -0.64), (0, 0.52, -0.69), (0, 0.45, -0.71)], 0.015, "BROWN_HAIR", sides=5)
    s.on("Tail2")
    C.ball(s, (0, 0.43, -0.715), (0.025, 0.04, 0.025), "BLACK_HAIR", segs=6, rings=4)
    four_legs(s, f, "BROWN_HAIR", [0.10, 0.07, 0.045, 0.04, 0.042], [0.09, 0.065, 0.045, 0.04, 0.042],
              ((0.09, 0.12, 0.12), (0.085, 0.11, 0.10)))
    return f


def jaguar(s):
    """Long and low, a heavy head, a long tail, and the coat the trader pays for."""
    rows = [(-0.54, 0.50, 0.06, 0.07), (-0.48, 0.50, 0.13, 0.15), (-0.30, 0.49, 0.16, 0.17),
            (-0.05, 0.47, 0.15, 0.16), (0.20, 0.50, 0.17, 0.19), (0.36, 0.53, 0.15, 0.17),
            (0.44, 0.56, 0.09, 0.11)]
    f = Quad(0.8, hip=(0.50, -0.38), shoulder=(0.50, 0.30),
             hind=[(0.33, -0.24), (0.16, -0.46), (0.0, -0.40)],
             front=[(0.30, 0.24), (0.09, 0.30), (0.0, 0.33)], hx=0.11, fx=0.12,
             neck=(0.54, 0.38), head=(0.62, 0.56), nose=(0.60, 0.80),
             tail=[(0.53, -0.52), (0.38, -0.78), (0.34, -1.0)], belly=0.16)
    s.on("Hips", "Chest")
    trunk(s, rows, "GOLD")
    C.daub(s, "GOLD", lambda p: "CREAM" if p.y < 0.40 else None)

    s.on("Chest", "Neck", "Head")
    C.tube(s, [(0, 0.53, 0.36), (0, 0.58, 0.48), (0, 0.62, 0.56)], [(0.11, 0.13), (0.09, 0.10), (0.08, 0.085)],
           "GOLD", sides=8)
    s.on("Head")
    # Broad and flat-topped, the ears small and wide apart: a big cat, not a bear.
    C.ball(s, (0, 0.625, 0.62), (0.11, 0.075, 0.10), "GOLD", segs=10, rings=7)
    C.ball(s, (0, 0.59, 0.715), (0.065, 0.045, 0.07), "GOLD", segs=8, rings=5)
    C.ball(s, (0, 0.555, 0.70), (0.045, 0.025, 0.05), "CREAM", segs=6, rings=4)
    C.ball(s, (0, 0.607, 0.783), (0.03, 0.018, 0.016), "SUNBURN", segs=6, rings=4, outline=False)
    for k in (-1, 1):
        C.ball(s, (k * 0.09, 0.685, 0.57), (0.028, 0.026, 0.012), "GOLD", segs=6, rings=4)
        C.ball(s, (k * 0.052, 0.66, 0.695), (0.02, 0.016, 0.012), "YELLOW", segs=6, rings=4, outline=False)
        C.ball(s, (k * 0.054, 0.66, 0.705), (0.006, 0.012, 0.005), "DARK", segs=4, rings=3, outline=False)

    s.on("Hips", "Tail1", "Tail2")
    C.tube(s, [(0, 0.53, -0.50), (0, 0.46, -0.64), (0, 0.38, -0.78), (0, 0.32, -0.92), (0, 0.34, -1.02),
               (0, 0.40, -1.07)], [0.05, 0.045, 0.04, 0.036, 0.034, 0.03], "GOLD", sides=6,
           paint=lambda i: "DARK" if i in (2, 4) else None)

    four_legs(s, f, "GOLD", [0.08, 0.06, 0.045, 0.04, 0.04], [0.075, 0.06, 0.045, 0.04, 0.04],
              ((0.075, 0.12, 0.11), (0.07, 0.11, 0.085)), low=None)
    for end in ("Hind", "Front"):
        for side, k in (("L", -1), ("R", 1)):
            p = f.leg(end, k)[-1]
            s.on(f"{end}{side}3")
            C.ball(s, (p[0], 0.035, p[2] + 0.03), (0.045, 0.035, 0.055), "GOLD", segs=6, rings=4)

    # The rosettes on the flanks and back: a broken ring each, two strokes with gaps, which is what
    # tells a jaguar from a cheetah. Plain dots down the legs and on the head.
    s.on("Hips", "Chest")
    for i in range(7):
        z = -0.40 + i * 0.12
        y, rx, ry = next((y, rx, ry) for z1, y, rx, ry in reversed(rows) if z1 <= z)
        for j in range(6):
            a = math.radians(-30 + j * 44 + (22 if i % 2 else 0))
            c = Vector((rx * 1.1 * math.cos(a), y + ry * 1.1 * math.sin(a), z))
            n = Vector((math.cos(a), math.sin(a), 0))
            side = n.cross(Vector((0, 0, 1)))
            r = 0.026 + 0.006 * ((i * 7 + j * 3) % 3)
            for start in (0.3, 3.5):
                arc = [c + (side * math.cos(start + q * 0.75) + Vector((0, 0, 1)) * math.sin(start + q * 0.75)) * r
                       for q in range(4)]
                C.smear(s, [tuple(v) for v in arc], "DARK", "GOLD", w=0.008, step=0.03, seed=i * 7 + j)
    spots = []
    for end, k in (("Hind", -1), ("Hind", 1), ("Front", -1), ("Front", 1)):
        p = f.leg(end, k)
        for t in (0.35, 0.7):
            q = Vector(p[1]).lerp(Vector(p[2]), t)
            spots.append((q.x + k * 0.06, q.y, q.z))
    for y, z in ((0.70, 0.58), (0.68, 0.64), (0.60, 0.47), (0.56, 0.42)):
        spots += [(-0.07, y, z), (0.07, y, z)]
    s.on("Hips", "Chest", "Neck", "Head", "HindL1", "HindR1", "HindL2", "HindR2", "FrontL1", "FrontR1",
         "FrontL2", "FrontR2")
    C.smear(s, spots, "DARK", "GOLD", w=0.022, dots=True, seed=4)
    return f


def gull(s):
    f = Bird()
    s.on("Body")
    C.ball(s, (0, 0.17, -0.02), (0.075, 0.072, 0.15), "WHITE", segs=10, rings=7)
    s.on("Body", "Neck", "Head")
    C.tube(s, [(0, 0.20, 0.06), (0, 0.25, 0.11)], 0.045, "WHITE", sides=8)
    s.on("Head")
    C.ball(s, (0, 0.265, 0.12), (0.048, 0.05, 0.058), "WHITE", segs=8, rings=6)
    C.tube(s, [(0, 0.258, 0.165), (0, 0.253, 0.21), (0, 0.245, 0.245)],
           [(0.016, 0.016), (0.011, 0.012), (0.003, 0.005)], "YELLOW", sides=6)
    C.ball(s, (0, 0.246, 0.215), 0.007, "RED", segs=4, rings=3, outline=False)
    eyes(s, f, (0.035, 0.28, 0.145), 0.009)
    s.on("Body", "Tail")
    trunk(s, [(-0.26, 0.17, 0.045, 0.008), (-0.20, 0.175, 0.05, 0.012), (-0.12, 0.18, 0.04, 0.02)], "WHITE", n=8)
    for side, k in (("L", -1), ("R", 1)):
        s.on("Body", f"Wing{side}1", f"Wing{side}2")
        rows = [(0.07, 0.205, 0.035), (0.0, 0.20, 0.05), (-0.10, 0.195, 0.045), (-0.18, 0.19, 0.03),
                (-0.25, 0.19, 0.012), (-0.28, 0.19, 0.003)]
        rings = [[(k * (0.08 + 0.012 * math.cos(a * TAU / 8)), y + h * math.sin(a * TAU / 8), z) for a in range(8)]
                 for z, y, h in rows]
        C.loft(s, rings, "GREY_HAIR", paint=lambda i: "DARK" if i >= 3 else None)
        s.on(f"Leg{side}")
        C.tube(s, [(k * 0.03, 0.12, 0.0), (k * 0.03, 0.06, 0.005), (k * 0.03, 0.012, 0.01)], 0.008, "ORANGE", sides=5)
        C.ball(s, (k * 0.03, 0.006, 0.03), (0.022, 0.005, 0.03), "ORANGE", segs=6, rings=3)
    return f


# ------------------------------------------------------------------------------------- the clips

def pitch(a):
    """Degrees about the animal's side-to-side axis: a leg hanging down swings back, a bone pointing
    forward tips its end down."""
    return Matrix.Rotation(math.radians(a), 3, "X")


def yaw(a):
    """Degrees about the vertical: a look round, a tail's swish."""
    return Matrix.Rotation(math.radians(a), 3, "Z")


def roll(a):
    """Degrees about the long axis of the body."""
    return Matrix.Rotation(math.radians(a), 3, "Y")


def ease(t):
    t = min(max(t, 0.0), 1.0)
    return t * t * (3 - 2 * t)


def window(t, a, b, c, d):
    """0 before a, up to 1 by b, held to c, back to 0 by d."""
    return ease((t - a) / (b - a)) * (1 - ease((t - c) / (d - c)))


def up(y):
    return u(0, y, 0)


def gait(t, g, phases):
    """Every leg at time t: swinging back and forth by its amplitude, folding as it comes forward."""
    p = {}
    for end, (a_amp, fold) in (("Hind", g[0]), ("Front", g[1])):
        for side in "LR":
            q = (t - phases[end + side]) % 1.0
            swing = math.cos(TAU * q) * a_amp
            lift = max(0.0, math.sin(TAU * q)) * fold
            if end == "Hind":
                p[f"Hind{side}1"] = pitch(swing - 0.2 * lift)
                p[f"Hind{side}2"] = pitch(lift)
                p[f"Hind{side}3"] = pitch(-1.3 * lift)
            else:
                p[f"Front{side}1"] = pitch(swing - 0.3 * lift)
                p[f"Front{side}2"] = pitch(-0.3 * lift)
                p[f"Front{side}3"] = pitch(1.6 * lift)
    return p


WALK = {"HindL": 0.0, "FrontL": 0.25, "HindR": 0.5, "FrontR": 0.75}


def quad_clips(f, g):
    def idle(t):
        graze = window(t, 0.22, 0.34, 0.62, 0.76)
        look = math.sin(TAU * 2 * t) * (1 - graze)
        p = {"loc": up(0.004 * math.sin(TAU * 4 * t)),
             "Neck": pitch(g["graze"] * graze + 4 * math.sin(TAU * 4 * t)) @ yaw(g["look"] * look),
             "Head": pitch(g["graze"] * 0.35 * graze + g["chew"] * math.sin(TAU * 12 * t) * graze),
             "Tail1": yaw(g["flick"] * math.sin(TAU * 10 * t) * window(t, 0.82, 0.86, 0.92, 0.96)
                         + g["swish"] * math.sin(TAU * t)),
             "Tail2": yaw(g["swish"] * 1.4 * math.sin(TAU * t - 1.0))}
        return p

    def walk(t):
        p = gait(t, g["walk"], WALK)
        p["loc"] = up(-g["bob"] * math.cos(TAU * 2 * t))
        p["Hips"] = roll(2.5 * math.sin(TAU * t))
        p["Neck"] = pitch(5 * math.sin(TAU * 2 * t + 0.6))
        p["Tail1"] = yaw(10 * math.sin(TAU * t)) @ pitch(-5)
        p["Tail2"] = yaw(14 * math.sin(TAU * t - 0.8))
        return p

    def run(t):
        p = gait(t, g["run"], g["lead"])
        flex = g["spine"]
        p["loc"] = up(g["bob"] * 3 * math.sin(TAU * t))
        p["Hips"] = pitch(flex * math.cos(TAU * t))
        p["Chest"] = pitch(-flex * 1.4 * math.cos(TAU * t))
        p["Neck"] = pitch(-flex * 0.4 * math.cos(TAU * t) + g["run_head"])
        p["Head"] = pitch(flex * 0.5 * math.cos(TAU * t))
        p["Tail1"] = pitch(-g["run_tail"] + 8 * math.sin(TAU * t))
        p["Tail2"] = pitch(10 * math.sin(TAU * t - 1.0))
        return p

    def death(t):
        e = ease(t / 0.55)
        thud = 0.012 * math.sin(math.pi * min(max((t - 0.55) / 0.15, 0.0), 1.0))
        drop = f.hip[0] - f.belly
        p = {"loc": up(-drop * e + thud), "Hips": roll(88 * e) @ pitch(-4 * e),
             "Chest": pitch(6 * e), "Neck": pitch(28 * ease((t - 0.2) / 0.5)) @ yaw(-20 * e),
             "Head": pitch(18 * ease((t - 0.3) / 0.5)), "Tail1": pitch(25 * e), "Tail2": pitch(15 * e)}
        for side, k in (("L", 1), ("R", -1)):
            p[f"Hind{side}1"] = pitch(-22 * e) @ roll(8 * k * e)
            p[f"Hind{side}2"] = pitch(20 * e)
            p[f"Front{side}1"] = pitch(28 * e) @ roll(8 * k * e)
            p[f"Front{side}3"] = pitch(35 * e)
        return p

    return [("Idle", g["idle_s"], idle), ("Walk", g["walk_s"], walk), ("Run", g["run_s"], run),
            ("Death", 1.2, death)]


GAITS = {
    "Deer": dict(walk=((22, 35), (20, 40)), run=((48, 70), (45, 80)), bob=0.012, spine=9, run_head=-10,
                 run_tail=-25, lead={"HindL": 0.0, "HindR": 0.06, "FrontL": 0.5, "FrontR": 0.56},
                 graze=62, look=25, chew=3, flick=30, swish=4, idle_s=5.0, walk_s=0.9, run_s=0.5),
    "Boar": dict(walk=((20, 30), (18, 35)), run=((36, 55), (34, 60)), bob=0.01, spine=6, run_head=-6,
                 run_tail=-20, lead={"HindL": 0.0, "HindR": 0.15, "FrontL": 0.5, "FrontR": 0.65},
                 graze=22, look=20, chew=9, flick=0, swish=25, idle_s=4.0, walk_s=0.7, run_s=0.42),
    "Jaguar": dict(walk=((24, 40), (22, 45)), run=((50, 75), (48, 80)), bob=0.01, spine=16, run_head=-12,
                   run_tail=-10, lead={"HindL": 0.0, "HindR": 0.1, "FrontL": 0.5, "FrontR": 0.6},
                   graze=0, look=30, chew=0, flick=0, swish=14, idle_s=4.0, walk_s=1.0, run_s=0.5),
}
GAITS["Stag"] = dict(GAITS["Deer"], walk_s=1.0, run_s=0.55, graze=55)


def bird_clips(f):
    def wing_pose(p, flap, outer, spread):
        for side, bx in (("L", 1), ("R", -1)):  # the gull's left is Blender's +x
            open_ = yaw(-85 * spread * bx) @ roll(90 * spread * bx)
            r1 = roll(-flap * bx) @ open_
            p[f"Wing{side}1"] = r1
            p[f"Wing{side}2"] = r1.inverted() @ roll(-outer * bx) @ r1

    def idle(t):
        look = 40 * (window(t, 0.15, 0.2, 0.38, 0.43) - window(t, 0.55, 0.6, 0.75, 0.8))
        p = {"loc": up(0.002 * math.sin(TAU * 3 * t)), "Neck": yaw(look) @ pitch(3 * math.sin(TAU * 3 * t)),
             "Tail": pitch(-6 * math.sin(TAU * 6 * t))}
        ruffle = window(t, 0.86, 0.89, 0.93, 0.97)
        wing_pose(p, 15 * ruffle * math.sin(TAU * 20 * t), 0, 0.08 * ruffle)
        return p

    def walk(t):
        p = {"loc": up(0.006 * abs(math.sin(TAU * t))), "Body": roll(9 * math.sin(TAU * t)),
             "LegL": pitch(32 * math.cos(TAU * t)), "LegR": pitch(-32 * math.cos(TAU * t)),
             "Neck": pitch(12 * math.sin(TAU * 2 * t)), "Tail": yaw(8 * math.sin(TAU * t))}
        wing_pose(p, 0, 0, 0)
        return p

    def fly(t):
        beat = math.sin(TAU * 2 * t)
        p = {"loc": up(0.22 + 0.02 * math.sin(TAU * 2 * t - 1.2)), "Body": pitch(-6),
             "LegL": pitch(75), "LegR": pitch(75), "Neck": pitch(-8 - 4 * beat), "Tail": pitch(-4 * beat)}
        wing_pose(p, 38 * beat, 22 * math.sin(TAU * 2 * t - 0.7), 1.0)
        return p

    def death(t):
        e = ease(t / 0.5)
        p = {"loc": up(-(0.17 - f.belly) * e), "Body": roll(95 * e), "Neck": pitch(40 * e) @ yaw(30 * e),
             "LegL": pitch(-40 * e), "LegR": pitch(-25 * e), "Tail": pitch(10 * e)}
        wing_pose(p, 0, 0, 0.12 * e)
        return p

    return [("Idle", 3.0, idle), ("Walk", 0.5, walk), ("Fly", 0.67, fly), ("Death", 1.0, death)]


def animate(rig, clips):
    """Each clip an action keyed on every frame: a rotation per bone in the armature's axes, relative
    to its parent, and the root's offset under "loc"."""
    rig.animation_data_create()
    root = rig.pose.bones[0]
    actions = []
    for name, seconds, pose in clips:
        frames = max(2, round(seconds * FPS))
        act = bpy.data.actions.new(name)
        act.use_fake_user = True
        rig.animation_data.action = act
        last = {}
        for fr in range(frames + 1):
            want = pose(fr / frames)
            for pb in rig.pose.bones:
                pb.rotation_mode = "QUATERNION"
                m = pb.bone.matrix_local.to_3x3()
                q = (m.transposed() @ want.get(pb.name, I3) @ m).to_quaternion()
                if pb.name in last and last[pb.name].dot(q) < 0:
                    q.negate()
                last[pb.name] = q
                pb.rotation_quaternion = q
                pb.keyframe_insert("rotation_quaternion", frame=fr, group=pb.name)
            root.location = root.bone.matrix_local.to_3x3().transposed() @ want.get("loc", Vector())
            root.keyframe_insert("location", frame=fr, group=root.name)
        actions.append(act)
    rig.animation_data.action = None
    for pb in rig.pose.bones:
        pb.rotation_quaternion = (1, 0, 0, 0)
        pb.location = (0, 0, 0)
    return actions


ANIMALS = [("Boar", boar), ("Deer", deer), ("Stag", stag), ("Jaguar", jaguar), ("Gull", gull)]


def main(out_dir=None, only=None, pose=None):
    """`pose` = (clip, time in [0, 1]) leaves every animal posed there, to look at."""
    P.clear()
    for block in (bpy.data.armatures, bpy.data.actions):
        for item in list(block):
            block.remove(item)
    bpy.context.scene.render.fps = FPS
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "animals")
    sheet = C.paint_texture(folder)
    texture = os.path.join(folder, "Animals.png")
    shutil.move(sheet, texture)
    mat = C.material(texture)
    mat.name = "Animals"
    made = []
    for name, make in ANIMALS:
        if only and name not in only:
            continue
        s = C.Fig()
        f = make(s)
        rig, body = C.finish(s, f, "Animal_" + name, mat, outline=0.004 if isinstance(f, Bird) else 0.008)
        print(f"[animals] Animal_{name}: {C.tri_count(body)} tris")
        clips = bird_clips(f) if isinstance(f, Bird) else quad_clips(f, GAITS[name])
        actions = animate(rig, clips)
        if out_dir is None:
            rig.location = (-len(made) * 2.2, 0, 0)
            rig.name = f"Animal_{name}_Rig"
            if pose:
                clip = next((a for a in actions if a.name.startswith(pose[0])), actions[2])  # Fly is the Run
                rig.animation_data.action = clip
                bpy.context.scene.frame_set(round(pose[1] * (clip.frame_range[1])))
            made.append(rig)
            continue
        bpy.ops.object.select_all(action="DESELECT")
        rig.select_set(True)
        body.select_set(True)
        bpy.context.view_layer.objects.active = rig
        bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, f"Animal_{name}.fbx"), use_selection=True,
                                 object_types={"ARMATURE", "MESH"}, apply_unit_scale=True,
                                 apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
                                 mesh_smooth_type="FACE", add_leaf_bones=False, use_armature_deform_only=True,
                                 bake_anim=True, bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False,
                                 bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0,
                                 path_mode="RELATIVE")
        for obj in (body, rig):
            data = obj.data
            bpy.data.objects.remove(obj, do_unlink=True)
            (bpy.data.meshes if isinstance(data, bpy.types.Mesh) else bpy.data.armatures).remove(data)
        for act in actions:
            bpy.data.actions.remove(act)
        made.append(name)
    print(f"[animals] {len(made)} animals" + (f" to {out_dir}" if out_dir else ""))
    return made


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
