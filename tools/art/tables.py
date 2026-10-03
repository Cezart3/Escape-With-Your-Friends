"""The casino's table games, modelled from code (#252).

    blender -b --factory-startup -P tools/art/tables.py -- <absolute path>/Assets/_Project/Art/Casino/Models

Writes CasinoTables.fbx, painted from the slots' ramp sheet (Textures/Symbols.png, written by slots.py),
so the tables wear the slots' one material:

- Rou_Table: legs, apron, rail, felt and the wheel's bowl, on the table's origin.
- Rou_Rotor: the turning part, 37 pockets in European order, on the wheel's origin.
- Rou_Ball: the ball.
- Rou_Spot_<Label>: one bet square each, centred on its spot.
- Bj_Table: the blackjack table, a D with the dealer on the flat side, chip tray and shoe.

Everything is drawn in Unity's frame through `u()`: Unity (x, y, z) is Blender (-x, -z, y), which is
what the FBX export turns back. So the numbers here are the ones CasinoFactory and BlackjackFactory
use.
"""

import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import slots as S  # noqa: E402

TAU = math.tau


def u(x, y, z):
    return Vector((-x, -z, y))


def ubox(s, lo, hi, col, bevel=0.0, outline=True):
    a, b = u(*lo), u(*hi)
    return S.box(s, tuple(map(min, a, b)), tuple(map(max, a, b)), col, bevel=bevel, outline=outline)


def ulathe(s, profile, col, at, **kw):
    """A lathe round Unity's y at Unity point `at`; profile is [(radius, height above at)]."""
    return S.lathe(s, profile, col, c=u(*at), **kw)


def yaw(a, r, h, at=(0, 0, 0)):
    """Unity yaw `a` (radians, clockwise from +z seen from above) at radius r, height h."""
    return u(at[0] + r * math.sin(a), at[1] + h, at[2] + r * math.cos(a))


def ring_faces(s, r0, r1, a0, a1, h0, h1, col, n=3, at=(0, 0, 0), outline=False):
    """A flat-ish annular sector facing up, from yaw a0 to a1."""
    before = s.begin()
    inner = [s.bm.verts.new(yaw(a0 + (a1 - a0) * k / n, r0, h0, at)) for k in range(n + 1)]
    outer = [s.bm.verts.new(yaw(a0 + (a1 - a0) * k / n, r1, h1, at)) for k in range(n + 1)]
    for k in range(n):
        s.bm.faces.new((inner[k], outer[k], outer[k + 1], inner[k + 1]))
    faces = s.end(before, col, False, outline)
    for f in faces:
        f.normal_update()
        if f.normal.z < 0:
            f.normal_flip()
    return faces


def text(s, body, size, at, col, depth=0.002, flip=False):
    """Flat lettering on a table, read from the -z side, or from +z when flipped: Blender's own font."""
    cu = bpy.data.curves.new("txt", "FONT")
    cu.body = body
    cu.size = size
    cu.align_x = "CENTER"
    cu.align_y = "CENTER"
    ob = bpy.data.objects.new("txt", cu)
    bpy.context.scene.collection.objects.link(ob)
    dg = bpy.context.evaluated_depsgraph_get()
    ev = ob.evaluated_get(dg)
    me = ev.to_mesh()
    # Font space: x right, y up, z out of the page. Lying on the table, read from Unity -z: the
    # reader's right is Unity +x and the text's top points away from them, to Unity +z.
    before = s.begin()
    base = u(*at)
    k = -1 if flip else 1
    verts = [s.bm.verts.new(base + Vector((-k * v.co.x, -k * v.co.y, v.co.z + depth))) for v in me.vertices]
    for p in me.polygons:
        try:
            s.bm.faces.new([verts[i] for i in p.vertices])
        except ValueError:
            pass
    ev.to_mesh_clear()
    bpy.data.objects.remove(ob)
    bpy.data.curves.remove(cu)
    faces = s.end(before, col, False, outline=False)
    bmesh.ops.recalc_face_normals(s.bm, faces=faces)
    return faces


def flat(s, make, scale, at, outline=True):
    """A slot symbol laid on the felt, face up, its top away from the -z side."""
    before = set(s.bm.verts)
    make(s)
    rm = S.rotz(180) @ S.rotx(-90)
    for v in s.bm.verts:
        if v not in before:
            v.co = rm @ (v.co * scale) + u(*at)


# -------------------------------------------------------------------------------------- roulette

ORDER = [0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10,
         5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26]
REDS = {1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36}

WHEEL = (-1.0, 0.94, 0.0)   # the rotor's origin on the table: CasinoFactory.WheelAt
FELT = 0.94
STEP = TAU / 37


def pocket_colour(n):
    return "JADE" if n == 0 else "RED" if n in REDS else "DARK"


def roulette_table(s):
    # Four turned legs, an apron with a gold band, the top, the felt and a padded rail.
    for x in (-1.4, 1.4):
        for z in (-0.68, 0.68):
            ulathe(s, [(0.0, 0.0), (0.07, 0.0), (0.07, 0.05), (0.045, 0.1), (0.06, 0.3), (0.04, 0.5),
                       (0.055, 0.66), (0.06, 0.74), (0.0, 0.74)], "BROWN", (x, 0, z), segs=10)
    ubox(s, (-1.5, 0.7, -0.8), (1.5, 0.88, 0.8), "BROWN", bevel=0.015)
    ubox(s, (-1.505, 0.74, -0.805), (1.505, 0.76, 0.805), "GOLD", outline=False)
    ubox(s, (-1.6, 0.86, -0.9), (1.6, 0.93, 0.9), "BROWN", bevel=0.02)
    ubox(s, (-1.5, 0.93, -0.8), (1.5, FELT, 0.8), "JADE", outline=False)
    for lo, hi in (((-1.6, 0.93, -0.9), (1.6, 0.99, -0.8)), ((-1.6, 0.93, 0.8), (1.6, 0.99, 0.9)),
                   ((-1.6, 0.93, -0.8), (-1.5, 0.99, 0.8)), ((1.5, 0.93, -0.8), (1.6, 0.99, 0.8))):
        ubox(s, lo, hi, "RUBY", bevel=0.025)

    # The layout's frame: thin cream lines round the betting squares.
    x0, x1, z0, z1, t = -0.42, 1.42, -0.64, 0.64, 0.01
    for lo, hi in (((x0, FELT, z0), (x1, FELT + 0.002, z0 + t)), ((x0, FELT, z1 - t), (x1, FELT + 0.002, z1)),
                   ((x0, FELT, z0), (x0 + t, FELT + 0.002, z1)), ((x1 - t, FELT, z0), (x1, FELT + 0.002, z1)),
                   ((x0, FELT, -t / 2), (x1, FELT + 0.002, t / 2))):
        ubox(s, lo, hi, "CREAM", outline=False)

    # The bowl the rotor turns in: a deflector slope, the ball track, a wall and a lip.
    ulathe(s, [(0.41, 0.0), (0.41, 0.03), (0.46, 0.06), (0.48, 0.065), (0.485, 0.1), (0.52, 0.105),
               (0.53, 0.09), (0.53, 0.0)], "BROWN", WHEEL, segs=40)
    for k in range(8):
        a = k * TAU / 8 + STEP / 2
        S.sphere(s, yaw(a, 0.437, 0.05, WHEEL), (0.012, 0.012, 0.006), "GOLD", segs=4, rings=3,
                 smooth=False, outline=False)


def roulette_rotor(s):
    for k, n in enumerate(ORDER):
        a0, a1 = (k - 0.5) * STEP, (k + 0.5) * STEP
        col = pocket_colour(n)
        ring_faces(s, 0.25, 0.355, a0, a1, 0.012, 0.012, col)          # the pocket floor
        ring_faces(s, 0.355, 0.405, a0, a1, 0.036, 0.03, col)          # the number ring
        # A brass fret on the pocket's leading edge.
        a = a0
        c = yaw(a, 0.30, 0.024)
        before = s.begin()
        ret = bmesh.ops.create_cube(s.bm, size=1.0)
        rm = Matrix.Rotation(-a, 3, "Z")
        for v in ret["verts"]:
            v.co = rm @ Vector((v.co.x * 0.006, v.co.y * 0.105, v.co.z * 0.024)) + c
        s.end(before, "GOLD", False, False)
    # The wall inside the pockets and the one outside them, the cone, the turret and its handles.
    S.lathe(s, [(0.25, 0.012), (0.25, 0.036)], "BROWN", segs=37, smooth=False, outline=False)
    S.lathe(s, [(0.355, 0.012), (0.355, 0.036)], "GOLD", segs=37, smooth=False, outline=False)
    S.lathe(s, [(0.405, 0.03), (0.41, 0.0), (0.0, 0.0)], "BROWN", segs=37, smooth=False, outline=False)
    S.lathe(s, [(0.25, 0.036), (0.2, 0.05), (0.12, 0.07), (0.05, 0.085), (0.0, 0.088)], "BROWN", segs=24,
            outline=False)
    S.lathe(s, [(0.05, 0.08), (0.03, 0.095), (0.016, 0.13), (0.028, 0.15), (0.012, 0.17), (0.0, 0.18)],
            "GOLD", segs=12)
    for k in range(4):
        a = k * TAU / 4
        S.tube(s, [u(0, 0.15, 0), yaw(a, 0.08, 0.15)], 0.008, "GOLD", sides=6, outline=False)
        S.sphere(s, yaw(a, 0.085, 0.15), 0.016, "GOLD", segs=8, rings=5, outline=False)


def ball(s):
    S.sphere(s, (0, 0, 0), 0.016, "PEARL", segs=10, rings=6, outline=False)


SPOT = (0.36, 0.56)   # CasinoFactory.SpotSize, x by z


def spot(label):
    def make(s):
        w, d = SPOT[0] / 2, SPOT[1] / 2
        ubox(s, (-w, -0.004, -d), (w, 0.004, d), "SEA", outline=False)
        t = 0.012
        for lo, hi in (((-w, 0.004, -d), (w, 0.006, -d + t)), ((-w, 0.004, d - t), (w, 0.006, d)),
                       ((-w, 0.004, -d), (-w + t, 0.006, d)), ((w - t, 0.004, -d), (w, 0.006, d))):
            ubox(s, lo, hi, "GOLD", outline=False)
        if label == "Seven":
            flat(s, S.seven, 0.3, (0, 0.006, 0), outline=False)
        elif label in ("Red", "Black"):
            col = "RED" if label == "Red" else "DARK"
            S.extrude(s, [(0, 0.2), (0.11, 0), (0, -0.2), (-0.11, 0)], 0.01, col, outline=False,
                      rot=S.rotx(-90), c=u(0, 0.011, 0))
        else:
            words = {"Odd": "ODD", "Even": "EVEN", "Low": "1-18", "High": "19-36",
                     "Dozen1": "1st 12", "Dozen2": "2nd 12", "Dozen3": "3rd 12"}[label]
            text(s, words, 0.09 if len(words) <= 4 else 0.075, (0, 0.006, 0), "CREAM")
    return make


BUTTONS = [("Bet", "GOLD", "BET"), ("Hit", "LIME", "HIT"), ("Stand", "RED", "STAND"), ("Double", "BLUE", "x2"),
           ("Split", "PURPLE", "SPLIT")]


def button(col, word):
    """A seat's button, origin at its centre 2 cm over the felt: a chip with its word on it, the bet a stack."""
    def make(s):
        bet = word == "BET"
        r, n = (0.05, 3) if bet else (0.036, 1)
        for k in range(n):
            ulathe(s, [(0.0, 0.0), (r, 0.0), (r * 1.02, 0.004), (r, 0.008), (0.0, 0.008)], col, (0, -0.02 + k * 0.009, 0),
                   segs=16, smooth=False, outline=k == n - 1)
        top = -0.02 + n * 0.009 - 0.001
        ulathe(s, [(r * 0.72, 0.0), (r * 0.78, 0.0)], "WHITE", (0, top, 0), segs=16, smooth=False, outline=False)
        text(s, word, {2: 0.026, 3: 0.02}.get(len(word), 0.014), (0, top, 0), "WHITE", flip=True)
    return make


SPOTS = ["Seven", "Red", "Black", "Odd", "Even", "Low", "High", "Dozen1", "Dozen2", "Dozen3"]


# -------------------------------------------------------------------------------------- blackjack

TOP = 0.92        # BlackjackFactory.Top: the felt the cards lie on
SEAT = 0.44       # BlackjackFactory.SeatStep


def d_shape(n=40, a=0.95, b=0.9, z0=-0.45, inset=0.0):
    """The table's outline: flat along the dealer's side (z0), a squared-off half-round (a superellipse,
    power 6) to the players, full enough at the corners for the outer seats' buttons."""
    pts = []
    for k in range(n + 1):
        t = math.pi * k / n
        c, s_ = math.cos(t), math.sin(t)
        x = (a - inset) * math.copysign(abs(c) ** (1 / 3), c)
        z = z0 + inset + (b - 2 * inset) * abs(s_) ** (1 / 3)
        pts.append((x, z))
    return pts


def slab(s, pts, y0, y1, col, outline=True):
    """An outline in Unity (x, z), extruded up from y0 to y1."""
    before = s.begin()
    lo = [s.bm.verts.new(u(x, y0, z)) for x, z in pts]
    hi = [s.bm.verts.new(u(x, y1, z)) for x, z in pts]
    n = len(pts)
    for k in range(n):
        j = (k + 1) % n
        s.bm.faces.new((lo[k], lo[j], hi[j], hi[k]))
    s.bm.faces.new(hi)
    s.bm.faces.new(lo[::-1])
    faces = [f for f in s.bm.faces if f not in before]
    bmesh.ops.recalc_face_normals(s.bm, faces=faces)
    bmesh.ops.triangulate(s.bm, faces=faces)
    return s.end(before, col, False, outline)


def chips(s, at, col, n, r=0.02):
    """A stack of n chips: rims in the colour, white edge spots."""
    for k in range(n):
        y = at[1] + k * 0.007
        ulathe(s, [(0.0, 0.0), (r, 0.0), (r, 0.006), (0.0, 0.006)], col, (at[0], y, at[2]), segs=12,
               smooth=False, outline=False)
    ulathe(s, [(r * 0.6, 0.0), (r * 0.62, 0.001)], "WHITE", (at[0], at[1] + n * 0.007 - 0.001, at[2]),
           segs=12, smooth=False, outline=False)


def blackjack_table(s):
    outline = d_shape()
    # A pedestal pair under the apron, rather than four legs a seated player would kick.
    for x in (-0.5, 0.5):
        ulathe(s, [(0.0, 0.0), (0.2, 0.0), (0.2, 0.04), (0.08, 0.08), (0.06, 0.4), (0.09, 0.66), (0.0, 0.7)],
               "BROWN", (x, 0, 0), segs=12)
    slab(s, d_shape(inset=0.06), 0.68, 0.86, "BROWN")
    slab(s, d_shape(inset=0.059), 0.72, 0.74, "GOLD", outline=False)
    slab(s, outline, 0.86, 0.9, "BROWN")
    slab(s, d_shape(inset=0.07), 0.9, TOP - 0.002, "SEA", outline=False)

    # The padded rail round the players' edge.
    rail = [u(x, TOP + 0.01, z) for x, z in d_shape(inset=0.035)[1:-1]]
    S.tube(s, rail, 0.035, "RUBY", sides=8)
    ubox(s, (-0.95, 0.9, -0.45), (0.95, TOP + 0.02, -0.39), "BROWN", bevel=0.01)

    # A betting circle per seat, the payout line, and the dealer's things.
    for seat in range(4):
        x = (seat - 1.5) * SEAT
        ulathe(s, [(0.068, TOP - 0.001), (0.076, TOP - 0.001)], "GOLD", (x - 0.05, 0, -0.01), segs=24, smooth=False,
               outline=False)
    text(s, "BLACKJACK PAYS 3 TO 2", 0.035, (0.0, TOP - 0.002, -0.075), "GOLD", flip=True)
    text(s, "DEALER STANDS ON 17", 0.025, (0.0, TOP - 0.002, -0.12), "CREAM", flip=True)

    # The chip tray where the factory's tray was: six wells of chips.
    ubox(s, (-0.24, TOP - 0.01, -0.39), (0.24, TOP + 0.012, -0.29), "BROWN", bevel=0.005)
    for k, col in enumerate(("RED", "BLUE", "LIME", "DARK", "PURPLE", "GOLD")):
        x = -0.2 + k * 0.08
        for j in range(2):
            chips(s, (x, TOP + 0.004, -0.36 + j * 0.045), col, 3 + (k + j) % 3)

    # The shoe on the dealer's right and the discard rack on the left.
    S.extrude(s, [(-0.07, 0.0), (0.07, 0.0), (0.07, 0.07), (-0.07, 0.11)], 0.11, "DARK",
              rot=S.rotz(90) @ Matrix.Identity(3), c=u(0.62, TOP, -0.33), bevel=0.006)
    ubox(s, (0.565, TOP + 0.02, -0.325), (0.675, TOP + 0.09, -0.27), "CREAM", outline=False)
    ubox(s, (-0.68, TOP, -0.38), (-0.56, TOP + 0.06, -0.28), "DARK", bevel=0.006)


# -------------------------------------------------------------------------------------- export

def main(out_dir=None, only=None):
    S.P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "slots")
    mat = S.material(S.paint_texture(folder))

    parts = [("Rou_Table", roulette_table, 0.012), ("Rou_Rotor", roulette_rotor, 0.006), ("Rou_Ball", ball, 0.0),
             ("Bj_Table", blackjack_table, 0.012)]
    parts += [("Rou_Spot_" + label, spot(label), 0.004) for label in SPOTS]
    parts += [("Bj_" + name, button(col, word), 0.003) for name, col, word in BUTTONS]

    objects = []
    for i, (name, make, width) in enumerate(parts):
        if only and name not in only:
            continue
        s = S.Sym()
        make(s)
        obj = S.finish(s, name, mat, fit=False, outline=width)
        tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        print(f"[tables] {name}: {tris} tris")
        objects.append(obj)
        if out_dir is None and name.startswith("Rou_Spot"):
            obj.location = (-(i - 4) * 0.45, 2.0, 0)

    if out_dir is None:
        return objects

    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "CasinoTables.fbx"), use_selection=True,
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z",
                             axis_up="Y", mesh_smooth_type="FACE", bake_space_transform=True, path_mode="RELATIVE")
    print(f"[tables] exported {len(objects)} meshes to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
