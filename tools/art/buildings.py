"""The six landmarks' buildings, modelled from code (#78).

    blender -b --factory-startup -P tools/art/buildings.py -- <absolute path>/Assets/_Project/Art/Casino/Models

Writes Buildings.fbx, painted from the slots' ramp sheet (Textures/Symbols.png, written by slots.py), so
every building wears the slots' one material. One mesh per landmark, on the landmark's origin, fitted to
GreyboxBuilder's boxes, which stay behind as the colliders:

- Bld_Casino, and Bld_Casino_Bulbs: the lit bulbs, which wear the slots' glowing material.
- Bld_Shop, Bld_BaseCamp, Bld_NativeVillage (five huts, the totem, the fire), Bld_Wreck, Bld_Cave.
- Bld_Cage_Chips, Bld_Cage_Cash and Bld_VipDoor, with their bulbs: CasinoFactory's cashier windows and
  VIP door, on those prefabs' origins.

Everything is drawn in Unity's frame through tables.u(), so the numbers are GreyboxBuilder's.
"""

import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import slots as S  # noqa: E402
import tables as T  # noqa: E402

u = T.u
TAU = math.tau


# -------------------------------------------------------------------------------------- helpers

def unity(co):
    return Vector((-co.x, co.z, -co.y))


def moved(s, before, fn):
    """Every vertex added since `before` (a set of verts), moved by fn in Unity's frame."""
    for v in s.bm.verts:
        if v not in before:
            v.co = u(*fn(unity(v.co)))


def turn_y(deg, at=(0, 0, 0)):
    """Unity yaw: clockwise from +z, seen from above."""
    a = math.radians(deg)
    c, s_ = math.cos(a), math.sin(a)

    def fn(p):
        x, z = p.x - at[0], p.z - at[2]
        return (at[0] + x * c + z * s_, p.y, at[2] - x * s_ + z * c)
    return fn


def turn_z(deg, at=(0, 0, 0)):
    a = math.radians(deg)
    c, s_ = math.cos(a), math.sin(a)

    def fn(p):
        x, y = p.x - at[0], p.y - at[1]
        return (at[0] + x * c - y * s_, at[1] + x * s_ + y * c, p.z)
    return fn


def box(s, lo, hi, col, outline=False):
    return T.ubox(s, lo, hi, col, outline=outline)


def prism(s, lo, hi, col, outline=False, smooth=False):
    """Two matching rings of Unity points joined into a closed solid."""
    before = s.begin()
    a = [s.bm.verts.new(u(*p)) for p in lo]
    b = [s.bm.verts.new(u(*p)) for p in hi]
    n = len(lo)
    for k in range(n):
        j = (k + 1) % n
        s.bm.faces.new((a[k], a[j], b[j], b[k]))
    s.bm.faces.new(a[::-1])
    s.bm.faces.new(b)
    faces = [f for f in s.bm.faces if f not in before]
    bmesh.ops.recalc_face_normals(s.bm, faces=faces)
    return s.end(before, col, smooth, outline)


def board(s, quad, off, col, outline=False):
    """A board whose one face is `quad`, `off` thick."""
    return prism(s, [tuple(Vector(p) + Vector(off)) for p in quad], quad, col, outline)


def log(s, a, b, r, col, sides=6, outline=False):
    return S.tube(s, [u(*a), u(*b)], r, col, sides=sides, outline=outline)


def rope(s, pts, r=0.02, col="STRAW"):
    return S.tube(s, [u(*p) for p in pts], r, col, sides=4, outline=False)


def lathe(s, profile, col, at, segs=8, smooth=True, outline=False):
    return T.ulathe(s, profile, col, at, segs=segs, smooth=smooth, outline=outline)


def sign_text(s, body, size, at, facing, col, depth=0.03):
    """Upright lettering read by someone standing on the `facing` side (a Unity (x, z) direction)."""
    cu = bpy.data.curves.new("txt", "FONT")
    cu.body = body
    cu.size = size
    cu.align_x = "CENTER"
    cu.align_y = "CENTER"
    cu.extrude = depth / 2
    ob = bpy.data.objects.new("txt", cu)
    bpy.context.scene.collection.objects.link(ob)
    ev = ob.evaluated_get(bpy.context.evaluated_depsgraph_get())
    me = ev.to_mesh()
    fx, fz = facing
    right = Vector((-fz, 0, fx))
    out = Vector((fx, 0, fz))
    up = Vector((0, 1, 0))
    base = Vector(at)
    before = s.begin()
    verts = [s.bm.verts.new(u(*(base + right * v.co.x + up * v.co.y + out * v.co.z))) for v in me.vertices]
    for p in me.polygons:
        try:
            s.bm.faces.new([verts[i] for i in p.vertices])
        except ValueError:
            pass
    ev.to_mesh_clear()
    bpy.data.objects.remove(ob)
    bpy.data.curves.remove(cu)
    faces = s.end(before, col, False, outline=False)
    # The font's caps and sides come apart; welded, the letters are closed and face outward.
    bmesh.ops.remove_doubles(s.bm, verts=verts, dist=1e-5)
    faces = [f for f in faces if f.is_valid]
    bmesh.ops.recalc_face_normals(s.bm, faces=faces)
    return faces


def rock(s, lo, hi, rng, out=0.45, inward=(), cuts=4, col="STONE", moss=0.62, rnd=0.4):
    """A box gone lumpy: every face pushed out by up to `out`, except the faces named in `inward`
    ("+x", "-y", ...), which only ever sink, so a rock never stands in front of its collider's open
    side. Faces that end up looking at the sky are moss."""
    before = s.begin()
    bmesh.ops.create_cube(s.bm, size=1.0)
    faces = [f for f in s.bm.faces if f not in before]
    bmesh.ops.subdivide_edges(s.bm, edges=list({e for f in faces for e in f.edges}), cuts=cuts, use_grid_fill=True)
    faces = [f for f in s.bm.faces if f not in before]
    lo, hi = Vector(lo), Vector(hi)
    for v in sorted({v for f in faces for v in f.verts}, key=lambda v: tuple(v.co)):
        t = unity(v.co) + Vector((0.5, 0.5, 0.5))
        # Corners rounded part of the way to a sphere, so the boxes stop reading as boxes.
        q = t * 2 - Vector((1, 1, 1))
        x2, y2, z2 = q.x * q.x, q.y * q.y, q.z * q.z
        sph = Vector((q.x * math.sqrt(max(0.0, 1 - y2 / 2 - z2 / 2 + y2 * z2 / 3)),
                      q.y * math.sqrt(max(0.0, 1 - z2 / 2 - x2 / 2 + z2 * x2 / 3)),
                      q.z * math.sqrt(max(0.0, 1 - x2 / 2 - y2 / 2 + x2 * y2 / 3))))
        q = q.lerp(sph, rnd)
        p = Vector([lo[i] + (q[i] + 1) / 2 * (hi[i] - lo[i]) for i in range(3)])
        for i, axis in enumerate("xyz"):
            for side, edge in (("-", 0.0), ("+", 1.0)):
                if abs(t[i] - edge) < 1e-4:
                    k = rng.uniform(0.0, out)
                    if side + axis in inward:
                        k = -rng.uniform(0.0, out * 0.6)
                    p[i] += k if side == "+" else -k
                elif 0.0 < t[i] < 1.0:
                    p[i] += rng.uniform(-out, out) * 0.25
        v.co = u(*p)
    s.end(before, col, False, outline=False)
    for f in faces:
        f.normal_update()
        if f.normal.z > moss:
            f[s.col] = S.COL["LEAF"]
    return faces


def stone(s, c, r, rng, col="STONE"):
    return S.sphere(s, u(*c), (r * rng.uniform(0.9, 1.3), r * rng.uniform(0.9, 1.2), r * rng.uniform(0.6, 0.9)),
                    col, segs=6, rings=4, smooth=False, outline=False, bumps=0.18, seed=rng.randrange(1 << 30))


def bulb(g, at, r=0.035):
    S.sphere(g, u(*at), r, "CREAM", segs=6, rings=4, outline=False)


def bottle(s, at, col, h=0.3, r=0.05):
    lathe(s, [(0.0, 0.0), (r, 0.0), (r, h * 0.6), (r * 0.45, h * 0.74), (r * 0.35, h * 0.92), (r * 0.42, h),
              (0.0, h)], col, at, segs=8)


def barrel(s, at, r, h, col="BROWN", hoop="METAL"):
    lathe(s, [(0.0, 0.0), (r * 0.86, 0.0), (r, h * 0.5), (r * 0.86, h), (0.0, h)], col, at, segs=10, smooth=False)
    for y in (0.14, 0.86):
        rr = r * (0.86 + 0.14 * (1 - abs(y - 0.5) * 2)) + 0.008
        lathe(s, [(rr, h * y - 0.025), (rr, h * y + 0.025)], hoop, at, segs=10, smooth=False)


def crate(s, lo, hi, col="BROWN", frame="STRAW"):
    box(s, lo, hi, col)
    (x0, y0, z0), (x1, y1, z1), t = lo, hi, 0.05
    for x in (x0 - 0.01, x1 - t + 0.01):
        for z in (z0 - 0.01, z1 - t + 0.01):
            box(s, (x, y0, z), (x + t, y1, z + t), frame)
    for y in (y0, y1 - t):
        box(s, (x0 - 0.01, y, z0 - 0.01), (x1 + 0.01, y + t, z1 + 0.01), frame)


def torch(s, at, h=1.7):
    """A tiki torch: a bamboo pole, a cup, a flame that does not move."""
    x, y, z = at
    lathe(s, [(0.0, 0.0), (0.045, 0.0), (0.04, h), (0.0, h)], "STRAW", at, segs=6)
    for k in range(1, 4):
        lathe(s, [(0.05, h * k / 4 - 0.02), (0.05, h * k / 4 + 0.02)], "BROWN", at, segs=6, smooth=False)
    lathe(s, [(0.0, h), (0.07, h), (0.11, h + 0.18), (0.0, h + 0.16)], "BROWN", at, segs=8, smooth=False)
    lathe(s, [(0.0, h + 0.14), (0.085, h + 0.2), (0.05, h + 0.34), (0.0, h + 0.48)], "LAVA", at, segs=6)


def fringed(P, a, b, t0, t1, lip, fringe, rng):
    """A row's outline from a to b, its lower edge ragged by up to `fringe` (in t) when it is thatch."""
    lower = [P(a, t0, lip)]
    if fringe:
        n = max(2, int(abs(b - a) / 0.35))
        lower += [P(a + (b - a) * j / n, t0 - (fringe * rng.uniform(0.4, 1.0) if j % 2 else 0.0), lip)
                  for j in range(1, n)]
    return lower + [P(b, t0, lip), P(b, t1), P(a, t1)]


def gable(s, x0, x1, zc, half, y0, rise, rows, cols, rng, over=0.6, thick=0.1, lip=0.07, pieces=1, fringe=0.0):
    """A ridge along x: rows of boards from each eave up to the ridge, each row lapping the one below.
    `cols` is sampled per board, so a thatch is one colour and a tin roof a patchwork."""
    L = math.hypot(half, rise)
    t_lo = -over / L
    xs = [x0 - over + (x1 - x0 + 2 * over) * k / pieces for k in range(pieces + 1)]
    for side in (-1, 1):
        for k in range(rows):
            t0 = t_lo + (1 - t_lo) * k / rows
            t1 = min(1.0, t_lo + (1 - t_lo) * (k + 1) / rows + 0.05)

            def P(x, t, lift=0.0):
                return (x, y0 + rise * t + lift, zc + side * half * (1 - t))
            for a, b in zip(xs, xs[1:]):
                board(s, fringed(P, a, b, t0, t1, lip, fringe, rng), (0, -thick, 0), rng.choice(cols))
    log(s, (x0 - over - 0.1, y0 + rise + 0.05, zc), (x1 + over + 0.1, y0 + rise + 0.05, zc), 0.16, "BROWN", sides=6)


def hip(s, c, half, y0, rise, rows, col, rng, over=0.5, thick=0.1, lip=0.07, fringe=0.0):
    """A square pyramid of thatch rows over a square of `half`, apex `rise` above the eaves."""
    cx, cz = c
    for k in range(rows):
        t0 = k / rows
        t1 = min(1.0, (k + 1) / rows + 0.06)

        def a(t):
            return (half + over) * (1 - t)

        def y(t):
            return y0 - over * rise / half + (rise + over * rise / half) * t
        for q in range(4):
            ang = q * 90

            def P(sx, t, lift=0.0, ang=ang):
                r = a(t)
                p = turn_y(ang)(Vector((sx * r, y(t) + lift, r)))
                return (cx + p[0], p[1], cz + p[2])
            board(s, fringed(P, -1, 1, t0, t1, lip, fringe, rng), (0, -thick, 0), col)


def plank_wall(s, x0, z0, x1, z1, y0, y1, rng, cols=("BROWN",) * 9 + ("STRAW", "STRAW", "TEAL"), base=1.0, thick=0.3):
    """A straight wall from (x0, z0) to (x1, z1): two courses of rough stone, lapped planks above,
    a beam on top. Every piece goes right through, so it reads from both sides."""
    along = Vector((x1 - x0, 0, z1 - z0))
    n = along.length
    d = along / n
    side = Vector((-d.z, 0, d.x)) * (thick / 2)
    o = Vector((x0, 0, z0))

    def piece(a, b, ya, yb, grow, col):
        pa, pb = o + d * a, o + d * b
        quad = [pa - side * grow + Vector((0, ya, 0)), pb - side * grow + Vector((0, ya, 0)),
                pb - side * grow + Vector((0, yb, 0)), pa - side * grow + Vector((0, yb, 0))]
        prism(s, quad, [q + side * 2 * grow for q in quad], col)

    for course, (ya, yb) in enumerate(((y0 - 0.3, y0 + base * 0.5), (y0 + base * 0.5, y0 + base))):
        a = -rng.uniform(0, 0.4) if course else 0.0
        while a < n:
            b = min(n, a + rng.uniform(0.45, 0.9))
            before = set(s.bm.verts)
            piece(max(a, 0.0), b, ya, yb, 1.12, "STONE")
            for v in s.bm.verts:
                if v not in before:
                    v.co += Vector([rng.uniform(-0.025, 0.025) for _ in range(3)])
            a = b
    rows = max(1, round((y1 - 0.15 - y0 - base) / 0.36))
    h = (y1 - 0.15 - y0 - base) / rows
    for r in range(rows):
        ya = y0 + base + r * h - 0.03
        a = 0.0
        while a < n - 0.05:
            b = min(n, a + rng.uniform(1.6, 4.5))
            piece(a, b, ya, ya + h + 0.03, 1.0 + rng.uniform(0.02, 0.12), rng.choice(cols))
            a = b
    piece(-0.1, n + 0.1, y1 - 0.15, y1 + 0.05, 1.25, "BROWN")


def gable_end(s, x, z0, z1, y0, rise, rng, cols=("BROWN", "BROWN", "STRAW"), thick=0.3):
    """The triangle under a roof's end, in planks: the wall's own line at x, from z0 to z1."""
    zc, half = (z0 + z1) / 2, (z1 - z0) / 2
    rows = max(1, round(rise / 0.38))
    for r in range(rows):
        ya, yb = y0 + rise * r / rows, y0 + rise * (r + 1) / rows
        wa, wb = half * (1 - r / rows), half * (1 - (r + 1) / rows)
        quad = [(x - thick / 2, ya, zc - wa), (x - thick / 2, ya, zc + wa), (x - thick / 2, yb, zc + wb),
                (x - thick / 2, yb, zc - wb)]
        prism(s, quad, [(q[0] + thick, q[1], q[2]) for q in quad], rng.choice(cols))


def window(s, c, w, h, facing, shutters="TEAL", both=True, box_col=None, rng=None):
    """A painted window on a wall: frame, dark glass, a cross, and open shutters on the `facing` side."""
    x, y, z = c
    fx, fz = facing
    right = Vector((-fz, 0, fx))
    out = Vector((fx, 0, fz))
    C = Vector(c)

    def rect(cx, cy, hw, hh, d0, d1, col):
        p = [C + right * (cx - hw) + Vector((0, cy - hh, 0)), C + right * (cx + hw) + Vector((0, cy - hh, 0)),
             C + right * (cx + hw) + Vector((0, cy + hh, 0)), C + right * (cx - hw) + Vector((0, cy + hh, 0))]
        prism(s, [q + out * d0 for q in p], [q + out * d1 for q in p], col)

    back = -0.2 if both else 0.0
    rect(0, 0, w / 2, h / 2, back, 0.17, "NAVY")
    t = 0.08
    for cx, cy, hw, hh in ((0, h / 2, w / 2 + t, t / 2), (0, -h / 2, w / 2 + t, t / 2), (-w / 2, 0, t / 2, h / 2),
                           (w / 2, 0, t / 2, h / 2), (0, 0, 0.025, h / 2), (0, 0, w / 2, 0.025)):
        rect(cx, cy, hw, hh, back - 0.01, 0.2, "CREAM")
    if shutters:
        for side in (-1, 1):
            rect(side * (w / 2 + t + w / 4), 0, w / 4, h / 2, 0.15, 0.2, shutters)
            for k in (-1, 1):
                rect(side * (w / 2 + t + w / 4), k * h / 4, w / 4 - 0.03, 0.02, 0.2, 0.215, "DARK")
    if box_col:
        rect(0, -h / 2 - 0.16, w / 2 + 0.05, 0.11, 0.15, 0.42, "BROWN")
        for k in range(int(w / 0.18)):
            p = C + right * (-w / 2 + 0.09 + k * 0.18) + Vector((0, -h / 2 - 0.02, 0)) + out * 0.29
            S.sphere(s, u(*p), 0.085, "LEAF", segs=6, rings=4, outline=False, bumps=0.2, seed=k)
            if k % 2 == 0:
                S.sphere(s, u(*(p + Vector((0, 0.07, 0)) + out * 0.04)), 0.045, rng.choice(box_col), segs=6, rings=4,
                         outline=False)


def pendant(s, g, at, col, roof_y):
    x, y, z = at
    rope(s, [(x, roof_y, z), (x, y + 0.24, z)], 0.012, "DARK")
    lathe(s, [(0.02, y + 0.26), (0.06, y + 0.24), (0.2, y + 0.04), (0.21, y + 0.02), (0.0, y + 0.22)], col, at=(x, 0, z),
          segs=10, smooth=False)
    bulb(g, (x, y + 0.05, z), 0.06)


def symbol(s, make, scale, at, facing=(0, 1)):
    """A slot symbol stood upright, its face to `facing`. Symbols are drawn facing Unity +z."""
    before = set(s.bm.verts)
    make(s)
    yaw = math.degrees(math.atan2(facing[0], facing[1]))
    tf = turn_y(yaw)
    for v in s.bm.verts:
        if v not in before:
            p = unity(v.co) * scale
            q = tf(p)
            v.co = u(q[0] + at[0], q[1] + at[1], q[2] + at[2])


# ---------------------------------------------------------------------------------------- casino

def casino(s, g):
    rng = random.Random(65)

    # The floor: a dark bed, mismatched decking across the front room, a carpet in the VIP room.
    box(s, (-10, -0.02, -10), (10, 0.035, 9.15), "DARK")
    x = -10.0
    while x < 9.99:
        z = -2.5 - rng.uniform(0, 1.5)
        w = 0.48
        while z < 9.1:
            z1 = min(9.1, z + rng.uniform(1.5, 3.6))
            col = "STRAW" if rng.random() < 0.06 else "TEAL" if rng.random() < 0.02 else "BROWN"
            box(s, (x + 0.01, 0.035, max(z, -2.5) + 0.01), (x + w - 0.01, 0.065 + rng.uniform(-0.004, 0.004), z1 - 0.01),
                col)
            z = z1
        x += 0.5
    for i in range(-4, 4):
        box(s, (i * 0.5 + 0.01, -0.03, 9.17), (i * 0.5 + 0.49, 0.05, 10.3), "BROWN")
    box(s, (-9.85, 0.035, -9.85), (9.85, 0.072, -2.65), "RUBY")
    for lo, hi in (((-9.5, 0.072, -9.5), (9.5, 0.076, -9.38)), ((-9.5, 0.072, -3.12), (9.5, 0.076, -3.0)),
                   ((-9.5, 0.072, -9.5), (-9.38, 0.076, -3.0)), ((9.38, 0.072, -9.5), (9.5, 0.076, -3.0))):
        box(s, lo, hi, "GOLD")
    for k in range(5):
        cx = -6 + k * 3
        prism(s, [(cx - 0.6, 0.072, -6.25), (cx, 0.072, -5.4), (cx + 0.6, 0.072, -6.25), (cx, 0.072, -7.1)],
              [(cx - 0.6, 0.078, -6.25), (cx, 0.078, -5.4), (cx + 0.6, 0.078, -6.25), (cx, 0.078, -7.1)], "GOLD")

    # Walls. The front stops either side of a three-metre doorway; the lintel closes it overhead.
    plank_wall(s, -10, -10, 10, -10, 0, 4, rng)
    plank_wall(s, -10, -10, -10, 9, 0, 4, rng)
    plank_wall(s, 10, 9, 10, -10, 0, 4, rng)
    plank_wall(s, -10, 9, -1.5, 9, 0, 4, rng)
    plank_wall(s, 1.5, 9, 10, 9, 0, 4, rng)
    for r in range(3):
        box(s, (-1.6, 3.0 + r * 0.33, 8.85 - r * 0.01), (1.6, 3.36 + r * 0.33, 9.15 + r * 0.01), rng.choice(("BROWN", "STRAW")))
    for cx, cz in ((-10, -10), (10, -10), (-10, 9), (10, 9)):
        lathe(s, [(0.0, -0.3), (0.24, -0.3), (0.22, 4.15), (0.0, 4.2)], "BROWN", (cx, 0, cz), segs=8, smooth=False)
    for cx in (-1.6, 1.6):
        lathe(s, [(0.0, -0.1), (0.18, -0.1), (0.16, 4.1), (0.0, 4.15)], "BROWN", (cx, 0, 9.0), segs=8, smooth=False)
        S.sphere(s, u(cx, 4.2, 9.0), 0.2, "GOLD", segs=8, rings=5, outline=False)

    # The front's windows with shutters and flower boxes, and blind ones down the sides.
    for cx in (-5.75, 5.75):
        window(s, (cx, 2.4, 9.0), 2.5, 1.2, (0, 1), box_col=("PINK", "YELLOW", "RED", "PURPLE"), rng=rng)
    for cz in (6.8, -6.5):
        window(s, (-10, 2.4, cz), 1.6, 1.1, (-1, 0), shutters="RED")
        window(s, (10, 2.4, cz), 1.6, 1.1, (1, 0), shutters="RED")

    # The partition: red lacquer with gold trim, gold frames where the glass is, VIP over the door.
    for x0, x1 in ((-10, -4), (4, 10)):
        x = x0
        while x < x1 - 0.01:
            box(s, (x + 0.01, 0.0, -2.66), (x + 0.49, 3.9, -2.34), "RUBY")
            x += 0.5
        box(s, (x0, 0.0, -2.7), (x1, 0.18, -2.3), "DARK")
        box(s, (x0, 1.0, -2.69), (x1, 1.07, -2.31), "GOLD")
    box(s, (-4, 2.6, -2.66), (4, 3.9, -2.34), "RUBY")
    box(s, (-10, 3.85, -2.7), (10, 4.0, -2.3), "GOLD")
    for x in (-2.4, 2.4):
        for lo, hi in (((x - 1.6, 2.56, -2.56), (x + 1.6, 2.64, -2.44)), ((x - 1.6, 0.02, -2.56), (x + 1.6, 0.14, -2.44)),
                       ((x - 0.03, 0.0, -2.54), (x + 0.03, 2.6, -2.46))):
            box(s, lo, hi, "GOLD")
    for x in (-4.0, -0.8, 0.8, 4.0):
        box(s, (x - 0.08, 0.0, -2.62), (x + 0.08, 2.62, -2.38), "GOLD")
    for z, f in ((-2.32, 1), (-2.68, -1)):
        sign_text(s, "VIP", 0.62, (0.0, 3.27, z), (0, f), "GOLD", depth=0.05)
        for k in range(13):
            a = TAU * k / 13
            bulb(g, (0.95 * math.cos(a), 3.27 + 0.42 * math.sin(a), z + f * 0.03), 0.03)

    # The roof: thatch, rafters underneath, plank gables at the two ends.
    gable(s, -10.3, 10.3, -0.5, 9.8, 4.0, 3.0, 10, ("STRAW",), rng, over=0.7, thick=0.16, lip=0.12, fringe=0.03)
    for x in (-10.0, 10.0):
        gable_end(s, x, -10.0, 9.0, 4.0, 2.9, rng)
    for k in range(10):
        x = -9.0 + k * 2.0
        for zs in (-10.0, 9.0):
            log(s, (x, 3.95, zs), (x, 6.85, -0.5), 0.07, "BROWN", sides=4)
    log(s, (-10, 6.8, -0.5), (10, 6.8, -0.5), 0.12, "BROWN", sides=6)

    # The sign, tilted the way the greybox tilted it, with a plaque, gold letters and a ring of bulbs.
    before_s, before_g = set(s.bm.verts), set(g.bm.verts)
    for r in range(4):
        box(s, (-2.9 - rng.uniform(0, 0.12), 4.0 + r * 0.38, 9.14), (2.9 + rng.uniform(0, 0.12), 4.37 + r * 0.38, 9.26),
            ("STRAW", "BROWN", "STRAW", "BROWN")[r])
    box(s, (-2.55, 4.2, 9.25), (2.55, 5.32, 9.29), "RUBY")
    sign_text(s, "CASINO", 0.95, (0.0, 4.76, 9.3), (0, 1), "GOLD", depth=0.06)
    for k in range(20):
        bulb(g, (-2.75 + k * 5.5 / 19, 5.43, 9.3), 0.045)
        bulb(g, (-2.75 + k * 5.5 / 19, 4.1, 9.3), 0.045)
    for k in range(4):
        bulb(g, (-2.75, 4.37 + k * 0.27, 9.3), 0.045)
        bulb(g, (2.75, 4.37 + k * 0.27, 9.3), 0.045)
    for x in (-2.0, 2.0):
        log(s, (x, 4.0, 9.1), (x, 5.4, 8.3), 0.07, "BROWN", sides=4)
    moved(s, before_s, turn_z(-4, (0, 4.6, 9.2)))
    moved(g, before_g, turn_z(-4, (0, 4.6, 9.2)))

    # Banners and torches either side of the door.
    for side in (-1, 1):
        x = side * 2.6
        log(s, (x - 0.6, 3.15, 9.32), (x + 0.6, 3.15, 9.32), 0.025, "GOLD", sides=4)
        prism(s, [(x - 0.5, 3.1, 9.2), (x + 0.5, 3.1, 9.2), (x + 0.5, 1.6, 9.2), (x, 1.3, 9.2), (x - 0.5, 1.6, 9.2)],
              [(x - 0.5, 3.1, 9.26), (x + 0.5, 3.1, 9.26), (x + 0.5, 1.6, 9.26), (x, 1.3, 9.26), (x - 0.5, 1.6, 9.26)], "RED")
        for lo, hi in (((x - 0.5, 3.0, 9.26), (x + 0.5, 3.05, 9.27)), ((x - 0.45, 1.65, 9.26), (x + 0.45, 1.7, 9.27))):
            box(s, lo, hi, "GOLD")
        symbol(s, S.star_sym if side < 0 else S.seven, 0.6, (x, 2.3, 9.3))
        torch(s, (side * 2.1, 0.0, 9.6))

    # The bar: a bamboo front, a hardwood top, bottles on it, shelves of them behind, stools that
    # are barrels, and a crate of empties at the end.
    box(s, (-8.05, 0.95, -1.8), (-4.95, 1.1, -0.9), "BROWN", outline=True)
    box(s, (-8.05, 1.02, -0.92), (-4.95, 1.08, -0.88), "GOLD")
    box(s, (-8.0, 0.0, -1.7), (-5.0, 0.95, -1.05), "DARK")
    k = 0
    while -7.95 + k * 0.1 < -5.0:
        lathe(s, [(0.0, 0.0), (0.05, 0.0), (0.05, 0.95), (0.0, 0.95)], "STRAW", (-7.95 + k * 0.1, 0, -0.99), segs=6)
        k += 1
    glass = ("LIME", "AMBER", "RUBY", "TEAL", "BLUE", "GOLD", "PURPLE", "SEA")
    for i in range(6):
        bottle(s, (-7.75 + i * 0.5, 1.1, -1.45), glass[i], h=0.3 + 0.04 * (i % 2))
    for y in (1.45, 1.95):
        box(s, (-8.4, y - 0.04, -2.34), (-4.6, y, -2.16), "BROWN")
        for k in range(12):
            bottle(s, (-8.25 + k * 0.32, y, -2.25), rng.choice(glass), h=rng.uniform(0.22, 0.36), r=0.045)
    sign_text(s, "BAR", 0.45, (-6.5, 2.75, -2.31), (0, 1), "GOLD", depth=0.05)
    for k in range(9):
        bulb(g, (-7.0 + k * 0.125, 3.08, -2.3), 0.025)
        bulb(g, (-7.0 + k * 0.125, 2.42, -2.3), 0.025)
    for i in range(4):
        x = -7.7 + i * 0.8
        barrel(s, (x, 0.0, -0.4), 0.27, 0.55)
        lathe(s, [(0.0, 0.55), (0.26, 0.55), (0.27, 0.6), (0.22, 0.65), (0.0, 0.66)], "RED", (x, 0, -0.4), segs=10)
    crate(s, (-4.95, 0.0, -2.15), (-3.85, 0.7, -1.05))
    for k in range(6):
        bottle(s, (-4.75 + (k % 3) * 0.35, 0.55, -1.85 + (k // 3) * 0.45), "LIME", h=0.32)

    # String lights across the room with bottles hung off them, and a shade over every lamp.
    pts = [(-7.0 + 14.0 * k / 28, 3.95 - 0.25 * math.sin(math.pi * ((k % 7) / 7)), 3.0) for k in range(29)]
    rope(s, pts, 0.012, "DARK")
    for k, p in enumerate(pts):
        if k % 2:
            bulb(g, (p[0], p[1] - 0.05, p[2]), 0.04)
    for i in range(7):
        x = -6.0 + i * 2.0
        rope(s, [(x, 3.9, 3.0), (x, 3.72, 3.0)], 0.008, "DARK")
        S.lathe(s, [(0.0, 0.0), (0.05, 0.02), (0.05, 0.18), (0.02, 0.26), (0.0, 0.27)], glass[i], segs=8,
                c=u(x, 3.45, 3.0), smooth=True, outline=False)

    def roof_y(z):
        return 4.0 + 3.0 * (1 - abs(z + 0.5) / 9.8) - 0.15
    for at, col in (((0, 3.4, 3), "MAGENTA"), ((-6.5, 3.2, -1.2), "TEAL"), ((0, 3.3, 8.4), "GOLD"),
                    ((-8.4, 3.5, 5), "LIME"), ((8.4, 3.5, 2), "PURPLE"), ((-4, 3.4, -6.5), "RED"), ((4, 3.4, -6.5), "GOLD")):
        pendant(s, g, at, col, roof_y(at[2]))

    # The VIP room's walls: two portraits in gold frames, a seven and a crown.
    for x, make in ((-6.5, S.seven), (7.2, S.crown)):
        box(s, (x - 0.75, 1.3, -9.85), (x + 0.75, 3.1, -9.8), "NAVY")
        for lo, hi in (((x - 0.85, 3.0, -9.85), (x + 0.85, 3.15, -9.75)), ((x - 0.85, 1.25, -9.85), (x + 0.85, 1.4, -9.75)),
                       ((x - 0.85, 1.25, -9.85), (x - 0.7, 3.15, -9.75)), ((x + 0.7, 1.25, -9.85), (x + 0.85, 3.15, -9.75))):
            box(s, lo, hi, "GOLD", outline=True)
        symbol(s, make, 1.1, (x, 2.2, -9.72))


# ------------------------------------------------------------------------------------------ shop

def shop(s, g):
    rng = random.Random(48)
    x = -3.2
    while x < 3.19:
        box(s, (x + 0.01, -0.1, -3.3), (x + 0.39, 0.08 + rng.uniform(-0.005, 0.005), 1.3), rng.choice(("BROWN",) * 4 + ("STRAW",)))
        x += 0.4
    plank_wall(s, -3, -3.1, 3, -3.1, 0, 2.8, rng, base=0.6, thick=0.25)
    plank_wall(s, -2.9, -3.1, -2.9, 0, 0, 2.8, rng, base=0.6, thick=0.25)
    plank_wall(s, 2.9, 0, 2.9, -3.1, 0, 2.8, rng, base=0.6, thick=0.25)
    for cx, cz in ((-2.95, -3.15), (2.95, -3.15), (-2.95, 0), (2.95, 0)):
        lathe(s, [(0.0, -0.1), (0.15, -0.1), (0.13, 2.9), (0.0, 2.95)], "BROWN", (cx, 0, cz), segs=8, smooth=False)

    # The back door and its windows, on the side you walk round to.
    box(s, (-0.6, 0.0, -3.32), (0.6, 2.1, -3.2), "CREAM")
    for k in range(4):
        box(s, (-0.52 + k * 0.26, 0.05, -3.36), (-0.28 + k * 0.26, 2.0, -3.3), "TEAL")
    S.sphere(s, u(0.35, 1.0, -3.4), 0.04, "GOLD", segs=6, rings=4, outline=False)
    for cx in (-2.1, 2.1):
        window(s, (cx, 1.9, -3.1), 1.0, 0.8, (0, -1), shutters="RED", both=False)
    for cx, f in ((-2.9, -1), (2.9, 1)):
        window(s, (cx, 1.9, -1.6), 1.0, 0.8, (f, 0), shutters="RED")

    # A patchwork tin roof, red and rust, on the hut and two posts out front.
    gable(s, -3.5, 3.5, -1.4, 2.2, 2.9, 1.3, 5, ("RED", "RED", "ORANGE", "MELON"), rng, over=0.35, thick=0.06, pieces=6)
    for x in (-2.9, 2.9):
        gable_end(s, x, -3.6, 0.8, 2.85, 1.3, rng)
    for x in (-3.3, 3.3):
        lathe(s, [(0.0, -0.1), (0.12, -0.1), (0.11, 2.95), (0.0, 3.0)], "BROWN", (x, 0, 0.85), segs=8, smooth=False)
    log(s, (-3.5, 2.85, 0.85), (3.5, 2.85, 0.85), 0.1, "BROWN", sides=6)

    # The counter: a hardwood top on a front of crates, goods behind it, the sign hung off the beam.
    box(s, (-2.55, 0.98, 0.15), (2.55, 1.12, 1.12), "BROWN", outline=True)
    for i in range(5):
        crate(s, (-2.45 + i * 1.0, 0.0, 0.92), (-1.55 + i * 1.0, 0.96, 1.08), col=("BROWN", "TEAL", "BROWN", "RED", "BROWN")[i])
    box(s, (-2.0, 1.0, -0.45), (-1.2, 1.1, 0.05), "BROWN")
    barrel(s, (-1.6, 1.1, -0.2), 0.25, 0.5)
    crate(s, (1.3, 1.1, -0.45), (1.9, 1.5, 0.05))
    for k in range(3):
        bottle(s, (1.42 + k * 0.18, 1.5, -0.2), ("LIME", "AMBER", "BLUE")[k], h=0.28)
    for y in (1.3, 1.85):
        box(s, (-2.6, y - 0.04, -2.95), (2.6, y, -2.7), "BROWN")
        for k in range(14):
            xk = -2.45 + k * 0.36
            if rng.random() < 0.5:
                bottle(s, (xk, y, -2.82), rng.choice(("LIME", "AMBER", "RUBY", "TEAL")), h=rng.uniform(0.2, 0.32), r=0.05)
            else:
                crate(s, (xk - 0.13, y, -2.92), (xk + 0.13, y + rng.uniform(0.15, 0.3), -2.72), col="STRAW", frame="BROWN")
    for x in (-0.9, 0.9):
        rope(s, [(x, 2.85, 0.95), (x, 2.78, 0.95)], 0.015)
    box(s, (-1.2, 2.23, 0.92), (1.2, 2.78, 0.98), "STRAW", outline=True)
    sign_text(s, "TRADER", 0.36, (0.0, 2.5, 0.99), (0, 1), "DARK", depth=0.03)
    for k, x in enumerate((-2.4, -2.0, 2.0, 2.4)):
        rope(s, [(x, 2.8, 0.85), (x, 2.45, 0.85)], 0.012)
        if k % 2:
            for j in range(5):
                S.lathe(s, [(0.0, 0.0), (0.035, 0.05), (0.03, 0.2), (0.0, 0.24)], "YELLOW", segs=6,
                        c=u(x + (j - 2) * 0.04, 2.25, 0.85), smooth=True, outline=False)
        else:
            S.sphere(s, u(x, 2.3, 0.85), (0.06, 0.06, 0.18), "TEAL", segs=8, rings=5, outline=False)
    torch(s, (-2.9, 0.0, 1.5))
    torch(s, (2.9, 0.0, 1.5))


# ------------------------------------------------------------------------------------- base camp

def fire_pit(s, at, r, rng):
    x, y, z = at
    lathe(s, [(0.0, 0.01), (r * 0.85, 0.01), (r * 0.85, 0.03), (0.0, 0.04)], "DARK", at, segs=12, smooth=False)
    n = int(TAU * r / 0.32)
    for k in range(n):
        a = TAU * k / n
        stone(s, (x + r * math.sin(a), 0.08, z + r * math.cos(a)), 0.16, rng)
    for k in range(4):
        a = TAU * k / 4 + 0.4
        log(s, (x + 0.55 * r * math.sin(a), 0.06, z + 0.55 * r * math.cos(a)), (x, 0.35, z), 0.07, "BROWN", sides=6)
    for k in range(5):
        S.sphere(s, u(x + rng.uniform(-0.2, 0.2) * r, 0.06, z + rng.uniform(-0.2, 0.2) * r), 0.07, "LAVA", segs=6,
                 rings=4, outline=False, bumps=0.2, seed=k)


def base_camp(s, g):
    rng = random.Random(41)
    # The shelter: bamboo posts, a ridge pole and an orange tarp lashed over it.
    for i in range(4):
        px, pz = -4 + (-2.2 if i % 2 == 0 else 2.2), (-2.2 if i < 2 else 2.2)
        lathe(s, [(0.0, -0.1), (0.08, -0.1), (0.07, 2.4), (0.0, 2.45)], "STRAW", (px, 0, pz), segs=6)
        for k in (0.6, 1.2, 1.8):
            lathe(s, [(0.085, k - 0.02), (0.085, k + 0.02)], "BROWN", (px, 0, pz), segs=6, smooth=False)
    for px in (-6.2, -1.8):
        lathe(s, [(0.0, -0.1), (0.08, -0.1), (0.07, 2.95), (0.0, 3.0)], "STRAW", (px, 0, 0), segs=6)
    log(s, (-6.5, 2.92, 0), (-1.5, 2.92, 0), 0.07, "STRAW", sides=6)
    n = 6
    for side in (-1, 1):
        for k in range(n):
            def P(x, t):
                return (x, 2.98 - 0.6 * t - 0.12 * math.sin(math.pi * (x + 6.6) / 5.2) * math.sin(math.pi * t),
                        side * 2.6 * t)
            xs = [-6.6 + 5.2 * j / 4 for j in range(5)]
            for a, b in zip(xs, xs[1:]):
                t0, t1 = k / n, (k + 1) / n
                board(s, [P(a, t0), P(b, t0), P(b, t1), P(a, t1)], (0, -0.03, 0), "ORANGE")
    for i in range(4):
        px, pz = -4 + (-2.2 if i % 2 == 0 else 2.2), (-2.2 if i < 2 else 2.2)
        rope(s, [(px, 2.45, pz), (px + (0.3 if px > -4 else -0.3), 2.42, pz * 1.13)], 0.015)

    # Two bedrolls with their rolls at the head, a blanket on one.
    for x, col in ((-5.2, "BLUE"), (-2.8, "RED")):
        box(s, (x - 0.45, 0.0, -1.0), (x + 0.45, 0.1, 1.0), col)
        log(s, (x - 0.43, 0.2, -0.85), (x + 0.43, 0.2, -0.85), 0.13, "CREAM", sides=8)
        for z in (-0.4, 0.4):
            box(s, (x - 0.46, 0.0, z - 0.04), (x + 0.46, 0.11, z + 0.04), "BROWN")

    # The storage chest: a sea chest, bound in iron, with a lock.
    box(s, (2.4, 0.0, -3.6), (4.6, 1.05, -2.4), "BROWN", outline=True)
    S.lathe(s, [(0.0, -1.12), (0.62, -1.12), (0.62, 1.12), (0.0, 1.12)], "BROWN", segs=8, axis="x", c=u(3.5, 1.05, -3.0),
            rot=S.rotx(0), smooth=False, outline=False)
    for x in (2.55, 3.5, 4.45):
        box(s, (x - 0.06, 0.0, -3.63), (x + 0.06, 1.1, -2.37), "METAL")
    box(s, (3.38, 0.7, -2.38), (3.62, 0.98, -2.33), "GOLD")

    # The workbench: a thick top, splayed legs, a shelf, a vice, a hammer and a saw.
    box(s, (2.2, 0.83, 1.95), (4.8, 0.98, 3.05), "BROWN", outline=True)
    for i in range(4):
        px, pz = 3.5 + (-1.1 if i % 2 == 0 else 1.1), 2.5 + (-0.4 if i < 2 else 0.4)
        log(s, (px * 1.0 + (0.08 if px < 3.5 else -0.08), 0.83, pz), (px, 0.0, pz + (0.08 if pz > 2.5 else -0.08)), 0.06,
            "BROWN", sides=4)
    box(s, (2.5, 0.3, 2.15), (4.5, 0.35, 2.85), "STRAW")
    box(s, (3.95, 0.98, 2.3), (4.45, 1.1, 2.7), "METAL")
    box(s, (4.05, 1.1, 2.25), (4.35, 1.26, 2.4), "METAL")
    box(s, (4.05, 1.1, 2.6), (4.35, 1.26, 2.75), "METAL")
    log(s, (4.2, 1.18, 2.1), (4.2, 1.18, 2.9), 0.015, "DARK", sides=4)
    log(s, (2.7, 1.01, 2.3), (3.1, 1.01, 2.6), 0.02, "STRAW", sides=4)
    box(s, (3.05, 0.98, 2.55), (3.2, 1.08, 2.7), "METAL")
    prism(s, [(2.6, 0.99, 2.8), (3.4, 0.99, 2.85), (3.4, 0.99, 2.7), (2.6, 0.99, 2.75)],
          [(2.6, 1.0, 2.8), (3.4, 1.0, 2.85), (3.4, 1.0, 2.7), (2.6, 1.0, 2.75)], "METAL")
    box(s, (2.4, 0.98, 2.65), (2.65, 1.1, 2.9), "BROWN")

    fire_pit(s, (0, 0, 0), 0.95, rng)
    for k in range(3):
        a = TAU * k / 3 + 0.2
        log(s, (0.85 * math.sin(a), 0.0, 0.85 * math.cos(a)), (0.0, 1.45, 0.0), 0.035, "BROWN", sides=5)
    rope(s, [(0, 1.42, 0), (0, 0.78, 0)], 0.01, "DARK")
    lathe(s, [(0.0, 0.45), (0.17, 0.47), (0.22, 0.6), (0.2, 0.74), (0.23, 0.76), (0.0, 0.76)], "DARK", (0, 0, 0), segs=10)
    for k in range(3):
        a = TAU * k / 3 + 0.5
        log(s, (2.0 * math.sin(a) - 0.4, 0.18, 2.0 * math.cos(a)), (2.0 * math.sin(a) + 0.4, 0.18, 2.0 * math.cos(a) + 0.15),
            0.17, "BROWN", sides=8)


# ---------------------------------------------------------------------------------------- village

def hut(s, rng):
    """A native hut on its own origin, door to +z: log palisade, a hide curtain, a hip of thatch."""
    for side in range(4):
        tf = turn_y(side * 90)
        k = 0
        while -1.9 + k * 0.25 <= 1.9:
            x = -1.9 + k * 0.25
            k += 1
            if side == 0 and abs(x) < 0.5:
                continue
            h = 2.3 + rng.uniform(0, 0.15)
            p = tf(Vector((x, 0, 1.9)))
            lathe(s, [(0.0, -0.1), (0.13, -0.1), (0.12, h), (0.0, h + 0.25)], rng.choice(("BROWN", "BROWN", "STRAW")),
                  (p[0], 0, p[2]), segs=6, smooth=False)
        for y in (0.7, 2.0):
            a, b = tf(Vector((-2.02, y, 2.02))), tf(Vector((2.02, y, 2.02)))
            rope(s, [a, b], 0.035)
    box(s, (-0.5, 0.0, 1.75), (0.5, 2.0, 1.8), "DARK")
    log(s, (-0.6, 2.05, 1.95), (0.6, 2.05, 1.95), 0.09, "BROWN", sides=6)
    prism(s, [(-0.45, 1.95, 1.96), (0.45, 1.95, 1.96), (0.3, 0.2, 2.02), (-0.05, 0.1, 2.0)],
          [(-0.45, 1.95, 2.0), (0.45, 1.95, 2.0), (0.3, 0.2, 2.06), (-0.05, 0.1, 2.04)], "SKIN")
    symbol(s, S.mask, 0.45, (0.0, 2.22, 2.12))
    hip(s, (0, 0), 2.1, 2.5, 2.1, 6, "STRAW", rng, over=0.55, fringe=0.05)
    lathe(s, [(0.0, 4.4), (0.22, 4.45), (0.12, 4.8), (0.0, 4.85)], "BROWN", (0, 0, 0), segs=6, smooth=False)
    for k in range(4):
        a = TAU * k / 4 + 0.6
        log(s, (0, 4.5, 0), (0.45 * math.sin(a), 5.1, 0.45 * math.cos(a)), 0.03, "BROWN", sides=4)


def face(s, y, f, col, rng, k):
    """A carved face on a totem block, on the side facing `f` (+1 is +z)."""
    z = f * 0.42
    for x in (-0.18, 0.18):
        S.sphere(s, u(x, y + 0.25, z), (0.13, 0.05, 0.1), "WHITE", segs=8, rings=5, outline=False)
        S.sphere(s, u(x, y + 0.25, z + f * 0.035), (0.06, 0.03, 0.06), "DARK", segs=6, rings=4, outline=False)
        box(s, (x - 0.17, y + 0.4, z - 0.05), (x + 0.17, y + 0.47, z + f * 0.06), ("GOLD", "DARK", "RED")[k % 3])
    prism(s, [(-0.07, y + 0.15, z), (0.07, y + 0.15, z), (0.0, y - 0.1, z)],
          [(-0.07, y + 0.15, z + f * 0.12), (0.07, y + 0.15, z + f * 0.12), (0.0, y - 0.1, z + f * 0.03)], col)
    box(s, (-0.26, y - 0.42, z - 0.02), (0.26, y - 0.2, z + f * 0.03), "DARK")
    for t in range(5):
        x = -0.2 + t * 0.1
        box(s, (x - 0.03, y - 0.27, z), (x + 0.03, y - 0.2, z + f * 0.04), "CREAM")
        box(s, (x - 0.03, y - 0.42, z), (x + 0.03, y - 0.35, z + f * 0.04), "CREAM")


def totem(s, rng):
    box(s, (-0.55, -0.1, -0.55), (0.55, 0.4, 0.55), "STONE", outline=True)
    for k, (y0, col) in enumerate(((0.4, "BROWN"), (1.9, "TEAL"), (3.4, "RED"), (4.9, "BROWN"))):
        y1 = y0 + 1.45
        S.box(s, tuple(map(min, u(-0.4, y0, -0.4), u(0.4, y1, 0.4))), tuple(map(max, u(-0.4, y0, -0.4), u(0.4, y1, 0.4))),
              col, bevel=0.06, outline=True)
        box(s, (-0.43, y1 - 0.06, -0.43), (0.43, y1 + 0.03, 0.43), "GOLD")
        for f in (1, -1):
            face(s, (y0 + y1) / 2, f, col, rng, k)
        for x in (-0.46, 0.46):
            S.sphere(s, u(x, (y0 + y1) / 2 + 0.15, 0), (0.08, 0.14, 0.2), col, segs=6, rings=4, outline=True)
    # Wings for arms, in three layers of feathers, and a bird's head on top.
    for side in (-1, 1):
        for layer, (col, reach, drop) in enumerate((("RED", 1.65, 0.0), ("GOLD", 1.35, 0.12), ("TEAL", 1.0, 0.22))):
            pts = [(0.3, 6.85 - drop), (reach, 6.95 - drop), (reach - 0.05, 6.75 - drop)]
            for j in range(4):
                xx = reach - 0.1 - j * (reach - 0.4) / 4
                pts += [(xx - 0.08, 6.35 - drop - 0.1 * (3 - j) * 0.3), (xx - 0.2, 6.6 - drop)]
            pts += [(0.3, 6.45 - drop)]
            prism(s, [(side * x, y, -0.08 + layer * 0.03) for x, y in pts], [(side * x, y, 0.08 - layer * 0.03) for x, y in pts],
                  col, outline=layer == 0)
    S.box(s, tuple(map(min, u(-0.38, 6.35, -0.38), u(0.38, 7.6, 0.38))), tuple(map(max, u(-0.38, 6.35, -0.38), u(0.38, 7.6, 0.38))),
          "GOLD", bevel=0.08, outline=True)
    for f in (1, -1):
        for x in (-0.2, 0.2):
            S.sphere(s, u(x, 7.25, f * 0.38), (0.11, 0.05, 0.11), "WHITE", segs=8, rings=5, outline=False)
            S.sphere(s, u(x, 7.25, f * 0.41), (0.05, 0.03, 0.05), "DARK", segs=6, rings=4, outline=False)
        prism(s, [(-0.16, 7.05, f * 0.38), (0.16, 7.05, f * 0.38), (0.0, 6.85, f * 0.38)],
              [(-0.02, 6.95, f * 0.8), (0.02, 6.95, f * 0.8), (0.0, 6.85, f * 0.7)], "ORANGE", outline=True)
    for k in range(5):
        a = (k - 2) * 0.35
        prism(s, [(-0.05 + math.sin(a) * 0.1, 7.6, -0.05), (0.05 + math.sin(a) * 0.1, 7.6, -0.05),
                  (0.05 + math.sin(a) * 0.1, 7.6, 0.05), (-0.05 + math.sin(a) * 0.1, 7.6, 0.05)],
              [(math.sin(a) * 0.75 - 0.03, 7.6 + math.cos(a) * 0.65, -0.02), (math.sin(a) * 0.75 + 0.03, 7.6 + math.cos(a) * 0.65, -0.02),
               (math.sin(a) * 0.75 + 0.03, 7.6 + math.cos(a) * 0.65, 0.02), (math.sin(a) * 0.75 - 0.03, 7.6 + math.cos(a) * 0.65, 0.02)],
              ("RED", "GOLD", "TEAL", "GOLD", "RED")[k], outline=True)


def village(s, g):
    rng = random.Random(108)
    for i in range(5):
        a = i * TAU / 5
        cx, cz = math.cos(a) * 11, math.sin(a) * 11
        # The door to whichever side of the square looks most at the totem.
        yaw = round(math.degrees(math.atan2(-cx, -cz)) / 90) * 90
        before = set(s.bm.verts)
        hut(s, rng)
        tf = turn_y(yaw)
        moved(s, before, lambda p, tf=tf, cx=cx, cz=cz: (tf(p)[0] + cx, p.y, tf(p)[2] + cz))
    totem(s, rng)
    fire_pit(s, (0, 0, 3), 1.15, rng)
    for k in range(6):
        a = TAU * k / 6 + 0.3
        log(s, (2.6 * math.sin(a) - 0.4, 0.18, 3 + 2.6 * math.cos(a)), (2.6 * math.sin(a) + 0.4, 0.18, 3 + 2.6 * math.cos(a)),
            0.17, "BROWN", sides=8)


# ------------------------------------------------------------------------------------------ wreck

PROFILE = [(0.0, -1.5), (1.15, -1.35), (1.85, -0.9), (2.2, -0.2), (2.27, 0.6), (2.22, 1.5)]


def hull_at(t, z):
    """A point on the hull's half-section: t from 0 (keel) to 1 (gunwale), z along the keel."""
    t = min(max(t, 0.0), 1.0) * (len(PROFILE) - 1)
    i = min(int(t), len(PROFILE) - 2)
    f = t - i
    x = PROFILE[i][0] + (PROFILE[i + 1][0] - PROFILE[i][0]) * f
    y = PROFILE[i][1] + (PROFILE[i + 1][1] - PROFILE[i][1]) * f
    bow = max(0.0, (z - 2.5) / 4.6)
    x *= math.sqrt(max(0.0, 1 - bow * bow))
    y += 0.5 * bow * bow
    return x, y


def wreck(s, g):
    rng = random.Random(17)
    before = set(s.bm.verts)
    n = 10
    for side in (-1, 1):
        for k in range(n):
            t0, t1 = k / n, (k + 1) / n - 0.012
            z_start = -7 + (rng.uniform(0, 2.8) if k > 1 else rng.uniform(0, 0.8))
            hole = (rng.uniform(-3, 4), rng.uniform(0.6, 2.2)) if rng.random() < 0.35 and k > 2 else None
            zs = [z_start + j * 0.5 for j in range(int((6.95 - z_start) / 0.5) + 1)] + [6.95]
            col = rng.choice(("BROWN", "BROWN", "BROWN", "STRAW"))
            run = []
            for z in zs:
                if hole and hole[0] < z < hole[0] + hole[1]:
                    if len(run) > 1:
                        strake(s, run, t0, t1, side, col)
                    run = []
                    continue
                run.append(z)
            if len(run) > 1:
                strake(s, run, t0, t1, side, col)
    for j in range(13):
        z = -6 + j
        broken = z < -3.5 and rng.random() < 0.6
        top = 1.1 if not broken else rng.uniform(1.2, 1.9)
        for side in (-1, 1):
            pts = []
            for k in range(11):
                t = k / 10 * top
                x, y = hull_at(min(t, 1.0), z)
                if t > 1.0:
                    x, y = hull_at(1.0, z)
                    y += (t - 1.0) * 3.0
                pts.append((side * (x - 0.1), y + 0.05, z))
            S.tube(s, [u(*p) for p in pts], 0.07, "BROWN", sides=4, outline=False, smooth=False)
    box(s, (-0.18, -1.7, -6.5), (0.18, -1.4, 7.0), "DARK")
    S.tube(s, [u(0, -1.5 + 0.6 * (k / 6) ** 2 + 0.3 * k / 6, 6.9 + 0.5 * math.sin(k / 6 * 1.4)) for k in range(7)], 0.15,
           "BROWN", sides=6, outline=True)
    for j in range(9):
        x = -1.9 + j * 0.47
        if rng.random() < 0.3:
            continue
        za, zb = -2.0 + rng.uniform(-0.6, 0.6), 6.0 - rng.uniform(0, 1.5)
        box(s, (x, 0.85, za), (x + 0.43, 0.95, zb), "BROWN")
    # Drawn round the hull box's centre; listed 28 degrees like the box, and lifted onto it.
    moved(s, before, lambda p: (lambda q: (q[0], q[1] + 1.6, q[2]))(turn_z(28)(p)))

    # The mast, down across the sand, with its yard and a torn sail.
    d = Vector((math.sin(math.radians(34)), 0, math.cos(math.radians(34))))
    c = Vector((4, 0.8, -2))
    a, b = c - d * 5.5, c + d * 5.5
    log(s, tuple(a), tuple(b), 0.25, "BROWN", sides=8, outline=True)
    for t in (-3.2, 3.6):
        p = c + d * t
        lathe(s, [(0.27, p.y - 0.06), (0.27, p.y + 0.06)], "METAL", (p.x, 0, p.z), segs=8, smooth=False)
    yard = c + d * 2.5
    side = Vector((d.z, 0, -d.x))
    log(s, tuple(yard - side * 2.2 + Vector((0, -0.45, 0))), tuple(yard + side * 2.4 + Vector((0, -0.2, 0))), 0.1, "BROWN")
    sail = [yard + side * 2.3 + Vector((0, -0.25, 0)), yard - side * 1.8 + Vector((0, -0.45, 0)),
            yard - side * 2.6 - d * 1.6 + Vector((0, -0.75, 0)), yard + side * 0.6 - d * 2.2 + Vector((0, -0.78, 0)),
            yard + side * 1.7 - d * 1.1 + Vector((0, -0.6, 0))]
    prism(s, [tuple(p) for p in sail], [tuple(p + Vector((0, 0.03, 0))) for p in sail], "CREAM")

    # What spilled: crates, barrels, a coil of rope, planks.
    for i in range(4):
        x, z = -5 + i * 2.4, 6 + (i % 2) * 2
        if i == 1:
            S.lathe(s, [(0.0, -0.5), (0.42, -0.5), (0.5, 0.0), (0.42, 0.5), (0.0, 0.5)], "BROWN", segs=10, axis="x",
                    c=u(x, 0.45, z), smooth=False, outline=True)
        elif i == 2:
            barrel(s, (x, 0.0, z), 0.42, 0.95)
        else:
            before = set(s.bm.verts)
            crate(s, (x - 0.55, 0.0, z - 0.55), (x + 0.55, 1.0, z + 0.55))
            moved(s, before, turn_y(20 + i * 17, (x, 0, z)))
    for k in range(3):
        lathe(s, [(0.25 + k * 0.08, 0.03 + k * 0.01), (0.32 + k * 0.08, 0.03 + k * 0.01)], "STRAW", (-3.2, 0, 3.5), segs=12,
              smooth=False)
        lathe(s, [(0.24 + k * 0.08, 0.0), (0.24 + k * 0.08, 0.08 + k * 0.02), (0.32 + k * 0.08, 0.08 + k * 0.02),
                  (0.32 + k * 0.08, 0.0)], "STRAW", (-3.2, 0, 3.5), segs=12, smooth=False)
    for k in range(5):
        before = set(s.bm.verts)
        x, z = rng.uniform(-7, 7), rng.uniform(4, 10)
        box(s, (x - 1.0, 0.0, z - 0.12), (x + 1.0, 0.06, z + 0.12), "BROWN")
        moved(s, before, turn_y(rng.uniform(0, 180), (x, 0, z)))


def strake(s, zs, t0, t1, side, col):
    """One run of hull planking along z, 8 cm thick, from t0 to t1 up the section."""
    outer_lo, outer_hi, inner_lo, inner_hi = [], [], [], []
    for z in zs:
        (xa, ya), (xb, yb) = hull_at(t0, z), hull_at(t1, z)
        outer_lo.append(s.bm.verts.new(u(side * xa, ya, z)))
        outer_hi.append(s.bm.verts.new(u(side * xb, yb, z)))
        inner_lo.append(s.bm.verts.new(u(side * (xa - 0.08), ya, z)))
        inner_hi.append(s.bm.verts.new(u(side * (xb - 0.08), yb, z)))
    before = s.begin()
    for j in range(len(zs) - 1):
        for a, b in ((outer_lo, outer_hi), (outer_hi, inner_hi), (inner_hi, inner_lo), (inner_lo, outer_lo)):
            s.bm.faces.new((a[j], a[j + 1], b[j + 1], b[j]))
    for j in (0, len(zs) - 1):
        s.bm.faces.new((outer_lo[j], outer_hi[j], inner_hi[j], inner_lo[j]))
    faces = [f for f in s.bm.faces if f not in before]
    bmesh.ops.recalc_face_normals(s.bm, faces=faces)
    s.end(before, col, False, outline=False)


# ------------------------------------------------------------------------------------------- cave

def cave(s, g):
    rng = random.Random(23)
    rock(s, (-7.0, -0.5, -4.6), (-1.5, 5.3, 4.4), rng, inward=("+x",))
    rock(s, (1.5, -0.5, -4.6), (7.0, 5.1, 4.4), rng, inward=("-x",))
    rock(s, (-1.8, 3.4, -4.2), (1.8, 5.4, 4.3), rng, inward=("-y",), out=0.3, rnd=0.2)
    rock(s, (-7.0, -0.5, -7.6), (7.0, 5.4, -4.0), rng, inward=("+z",), cuts=5)
    rock(s, (-5.0, 4.4, -6.8), (5.0, 7.0, 3.0), rng, out=0.5, cuts=5, rnd=0.6)
    for k in range(9):
        a = rng.uniform(0, TAU)
        r = rng.uniform(7.2, 9.0)
        x, z = r * math.sin(a), r * math.cos(a) - 1.5
        if abs(x) < 2.5 and z > 0:
            continue
        stone(s, (x, 0.2, z), rng.uniform(0.35, 0.9), rng)
    box(s, (-1.5, -0.05, -4.0), (1.5, 0.02, 4.0), "DARK")
    # Ore: gold and amber crystals growing out of the back wall where the ore box is.
    for k in range(7):
        h = rng.uniform(0.35, 0.8)
        tilt = (rng.uniform(-0.35, 0.35), rng.uniform(0.1, 0.5))
        base = Vector((1.6 + rng.uniform(-0.45, 0.45), rng.uniform(0.0, 1.0), -4.0))
        tip = base + Vector((tilt[0], 0.5, tilt[1])).normalized() * h
        r = rng.uniform(0.07, 0.14)
        ring = [base + Vector((r * math.cos(TAU * j / 6), 0, r * math.sin(TAU * j / 6))) for j in range(6)]
        prism(s, [tuple(p) for p in ring], [tuple(p + (tip - base) * 0.75) for p in ring], rng.choice(("GOLD", "AMBER")))
        before = s.begin()
        top = [s.bm.verts.new(u(*(p + (tip - base) * 0.75))) for p in ring]
        apex = s.bm.verts.new(u(*tip))
        for j in range(6):
            s.bm.faces.new((top[j], top[(j + 1) % 6], apex))
        faces = [f for f in s.bm.faces if f not in before]
        bmesh.ops.recalc_face_normals(s.bm, faces=faces)
        s.end(before, "YELLOW", False, outline=False)


# ------------------------------------------------------------------------------- casino fittings

def cage(word, sign, stripe, money):
    """A cashier's window on CasinoFactory's booth: the player's side is +z, the cashier's bars at z -0.35."""
    def make(s, g):
        box(s, (-0.9, 0.0, -0.4), (0.9, 1.09, 0.4), "BROWN", outline=True)
        box(s, (-0.92, 0.0, -0.42), (0.92, 0.12, 0.42), "DARK")
        for x in (-0.42, 0.42):
            box(s, (x - 0.36, 0.2, 0.4), (x + 0.36, 0.95, 0.43), sign)
            for lo, hi in (((x - 0.38, 0.18, 0.42), (x + 0.38, 0.22, 0.45)), ((x - 0.38, 0.93, 0.42), (x + 0.38, 0.97, 0.45)),
                           ((x - 0.38, 0.18, 0.42), (x - 0.34, 0.97, 0.45)), ((x + 0.34, 0.18, 0.42), (x + 0.38, 0.97, 0.45))):
                box(s, lo, hi, "GOLD")
        box(s, (-1.0, 1.09, -0.5), (1.0, 1.19, 0.5), "CREAM", outline=True)
        box(s, (-1.01, 1.12, 0.49), (1.01, 1.16, 0.52), "GOLD")
        for x in (-0.9, 0.9):
            lathe(s, [(0.0, 1.19), (0.05, 1.19), (0.045, 2.55), (0.0, 2.6)], "GOLD", (x, 0, -0.35), segs=8)
        log(s, (-0.92, 2.5, -0.35), (0.92, 2.5, -0.35), 0.04, "GOLD", sides=6)
        log(s, (-0.92, 1.45, -0.35), (0.92, 1.45, -0.35), 0.03, "GOLD", sides=6)
        for k in range(15):
            x = -0.84 + k * 0.12
            log(s, (x, 1.45, -0.35), (x, 2.5, -0.35), 0.012, "GOLD", sides=5)
        box(s, (-0.62, 2.28, -0.39), (0.62, 2.72, -0.31), "GOLD", outline=True)
        box(s, (-0.57, 2.32, -0.4), (0.57, 2.68, -0.3), sign)
        for f in (1, -1):
            sign_text(s, word, 0.26, (0.0, 2.5, -0.35 + f * 0.05), (0, f), "CREAM", depth=0.03)
        for k in range(8):
            bulb(g, (-0.55 + k * 1.1 / 7, 2.75, -0.35), 0.025)
        for k in range(9):
            x0 = -1.1 + k * 2.2 / 9
            board(s, [(x0, 3.0, -0.48), (x0 + 2.2 / 9, 3.0, -0.48), (x0 + 2.2 / 9, 2.72, 0.62), (x0, 2.72, 0.62)], (0, -0.03, 0),
                  stripe if k % 2 else "CREAM")
        for x in (-1.0, 1.0):
            log(s, (x, 1.19, 0.45), (x, 2.72, 0.58), 0.025, "GOLD", sides=5)
        money(s)
    return make


def chip_stacks(s):
    for k, col in enumerate(("RED", "BLUE", "LIME", "PURPLE", "GOLD")):
        T.chips(s, (-0.55 + k * 0.12, 1.19, -0.2 + (k % 2) * 0.08), col, 4 + k % 3, r=0.035)


def cash_bundles(s):
    for k in range(5):
        x, z = -0.55 + k * 0.13, -0.18 + (k % 2) * 0.06
        for j in range(1 + k % 3):
            box(s, (x - 0.05, 1.19 + j * 0.03, z - 0.025), (x + 0.05, 1.215 + j * 0.03, z + 0.025), "LIME")
            box(s, (x - 0.012, 1.19 + j * 0.03, z - 0.027), (x + 0.012, 1.218 + j * 0.03, z + 0.027), "CREAM")


def vip_door(s, g):
    """CasinoFactory's VIP door: a gold double door with red panels, the same both sides, a crown on top."""
    box(s, (-0.8, 0.0, -0.075), (0.8, 2.4, 0.075), "GOLD", outline=True)
    box(s, (-0.012, 0.02, -0.08), (0.012, 2.38, 0.08), "AMBER")
    for f in (1, -1):
        for x in (-0.4, 0.4):
            for y0, y1 in ((0.2, 1.0), (1.2, 1.9)):
                box(s, (x - 0.28, y0, -0.09 if f < 0 else 0.075), (x + 0.28, y1, -0.075 if f < 0 else 0.09), "RUBY")
            S.sphere(s, u(x * 0.3, 1.1, f * 0.12), 0.05, "GOLD", segs=8, rings=5, outline=False)
            for y in (0.25, 0.95, 1.25, 1.85):
                for xx in (x - 0.23, x + 0.23):
                    S.sphere(s, u(xx, y, f * 0.095), 0.015, "GOLD", segs=4, rings=3, outline=False)
        box(s, (-0.6, 2.0, -0.09 if f < 0 else 0.075), (0.6, 2.32, -0.075 if f < 0 else 0.09), "RUBY")
        sign_text(s, "VIP", 0.24, (0.0, 2.16, f * 0.09), (0, f), "CREAM", depth=0.02)
    lathe(s, [(0.0, 2.4), (0.95, 2.4), (0.95, 2.5), (0.0, 2.5)], "GOLD", (0, 0, 0), segs=4, smooth=False)
    symbol(s, S.crown, 0.5, (0.0, 2.78, 0.0))
    for k in range(7):
        a = math.pi * k / 6
        bulb(g, (0.62 * math.cos(a), 2.55 + 0.28 * math.sin(a), 0.09), 0.025)
        bulb(g, (0.62 * math.cos(a), 2.55 + 0.28 * math.sin(a), -0.09), 0.025)


# ---------------------------------------------------------------------------------------- export

BUILDINGS = [("Casino", casino), ("Shop", shop), ("BaseCamp", base_camp), ("NativeVillage", village),
             ("Wreck", wreck), ("Cave", cave), ("Cage_Chips", cage("CHIPS", "RUBY", "RED", chip_stacks)),
             ("Cage_Cash", cage("CASH", "SEA", "JADE", cash_bundles)), ("VipDoor", vip_door)]


def main(out_dir=None, only=None):
    S.P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "slots")
    mat = S.material(S.paint_texture(folder))
    objects = []
    for i, (name, make) in enumerate(BUILDINGS):
        if only and name not in only:
            continue
        s, g = S.Sym(), S.Sym()
        make(s, g)
        made = [S.finish(s, "Bld_" + name, mat, fit=False, outline=0.02)]
        if g.bm.faces:
            made.append(S.finish(g, f"Bld_{name}_Bulbs", mat, fit=False))
        for obj in made:
            tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
            print(f"[buildings] {obj.name}: {tris} tris")
            if out_dir is None:
                obj.location = (-(i % 3) * 30, (i // 3) * 30, 0)
        objects += made

    if out_dir is None:
        return objects

    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "Buildings.fbx"), use_selection=True,
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z",
                             axis_up="Y", mesh_smooth_type="FACE", bake_space_transform=True, path_mode="RELATIVE")
    print(f"[buildings] exported {len(objects)} meshes to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
