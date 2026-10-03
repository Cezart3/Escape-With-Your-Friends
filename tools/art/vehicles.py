"""The plane, its three parts, the boat and the buggy, from code (#249).

    blender -b --factory-startup -P tools/art/vehicles.py -- <absolute path>/Assets/_Project/Art/Casino/Models

Writes Vehicles.fbx, painted from the slots' ramp sheet like everything else. Every mesh is drawn in
its prefab's own space, in metres, so the builders put it at the origin and nothing is scaled:

- `Veh_Plane` is the airframe round PlaneBuilder's boxes: nose +z, wheels on the strip, open cockpits
  where the four seats are. `Veh_Plane_Engine`, `_Wing` and `_Propeller` are the three holes, still in
  the plane's space, so each hangs under its `Fitted.*` box and PlaneAssembly shows it with the box.
- `Veh_Part_Engine`, `_Wing` and `_Propeller` are the same three lying on the ground, centred on x and
  z and standing on y 0: what PlanePartBuilder gives a player to carry.
- `Veh_Boat` round BoatBuilder's hull box, keel on y 0, bow +z.
- `Veh_Buggy` round VehicleBuilder's chassis, and `Veh_Buggy_Wheel`, one tyre on its own with its axle
  along y, the way CarController lays the wheel visuals before it rolls them.

The riders stand on their seat anchors (there is no sitting pose yet), so every seat is open: a body
shows from the knees up out of a cockpit, over a gunwale, inside a roll bar.
"""

import math
import os
import sys

import bmesh
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
shaped = I.shaped
rx = I.rx
at = I.at
TAU = math.tau


# -------------------------------------------------------------------------------------- helpers

def loft(s, rings, col, closed=True, caps=True, paint=None, outline=True, smooth=True):
    """Rings of Unity points joined in order. paint(i, k, centre) may repaint the face between ring i
    and i + 1 at edge k, given its centre in Unity space."""
    before = s.begin()
    vs = [[s.bm.verts.new(u(*p)) for p in ring] for ring in rings]
    n = len(rings[0])
    edges = n if closed else n - 1
    grid = []
    for i in range(len(vs) - 1):
        row = []
        for k in range(edges):
            j = (k + 1) % n
            row.append(s.bm.faces.new((vs[i][k], vs[i][j], vs[i + 1][j], vs[i + 1][k])))
        grid.append(row)
    if caps and closed:
        s.bm.faces.new(vs[0][::-1])
        s.bm.faces.new(vs[-1])
    faces = s.end(before, col, smooth, outline)
    bmesh.ops.recalc_face_normals(s.bm, faces=faces)
    if paint:
        for i, row in enumerate(grid):
            for k, f in enumerate(row):
                c = paint(i, k, B.unity(f.calc_center_median()))
                if c:
                    f[s.col] = S.COL[c]
    return grid


def lerp_table(table, z):
    """Linear through [(z, value...)] sorted by z."""
    if z <= table[0][0]:
        return table[0][1:]
    for a, b in zip(table, table[1:]):
        if z <= b[0]:
            f = (z - a[0]) / (b[0] - a[0])
            return tuple(x + (y - x) * f for x, y in zip(a[1:], b[1:]))
    return table[-1][1:]


def turned(s, profile, col, z0=0.0, x=0.0, y=0.0, segs=12, outline=True, smooth=True):
    """A lathe [(radius, length)] along +z from (x, y, z0)."""
    shaped(s, lambda: I.lathe(s, profile, col, segs=segs, outline=outline, smooth=smooth), rx(90), at(x, y, z0))


def axle_x(s, profile, col, c, segs=14, outline=True, smooth=True):
    """A lathe [(radius, offset)] round a line along x through c: a wheel on an axle."""
    S.lathe(s, profile, col, segs=segs, c=u(*c), axis="x", outline=outline, smooth=smooth)


def ring_tube(s, pts, r, col, sides=6, outline=True):
    """A closed loop of Unity points as a tube."""
    S.tube(s, [u(*p) for p in pts + pts[:1]], r, col, sides=sides, outline=outline, cap=False)


def tri_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


# ------------------------------------------------------------------------------------------ plane

# PlaneBuilder's boxes: fuselage 1.3 x 1.3 x 7 round (0, 1.6, 0), engine box ahead of z 3.5, the
# propeller turning round (0, 1.7, 4.7), the wings at y 1.6 and the four seats on y 2.15.
PLANE_BODY = [  # z, centre y, half width, half height
    (-3.62, 2.12, 0.04, 0.05), (-3.45, 2.1, 0.13, 0.16), (-3.0, 2.04, 0.25, 0.29), (-2.4, 1.95, 0.37, 0.41),
    (-1.7, 1.86, 0.5, 0.55), (-1.0, 1.79, 0.6, 0.65), (-0.3, 1.74, 0.67, 0.71), (0.5, 1.72, 0.7, 0.74),
    (1.5, 1.71, 0.71, 0.75), (2.4, 1.71, 0.7, 0.73), (3.0, 1.7, 0.66, 0.68), (3.5, 1.7, 0.61, 0.62),
]
PLANE_Z = [-3.62, -3.45, -3.0, -2.4, -1.9, -1.4, -1.0, -0.75, -0.5, -0.3, -0.05, 0.15, 0.4, 0.85, 1.05, 1.3,
           1.55, 1.75, 2.1, 2.5, 3.0, 3.3, 3.5]
COCKPITS = [(1.3, 0.56, 0.47), (-0.3, 0.56, 0.47)]  # centre z, half width, half length
AROUND = 18


def fuselage_at(x, z, top=True):
    """The fuselage's surface y over (x, z), or None off its side."""
    yc, a, b = lerp_table(PLANE_BODY, z)
    if abs(x) >= a:
        return None
    return yc + (1 if top else -1) * b * math.sqrt(1 - (x / a) ** 2)


def in_cockpit(x, z, grow=0.0):
    return any((x / (a + grow)) ** 2 + ((z - c) / (b + grow)) ** 2 < 1 for c, a, b in COCKPITS)


def plane(s):
    rings = []
    for z in PLANE_Z:
        yc, a, b = lerp_table(PLANE_BODY, z)
        rings.append([(a * math.sin(k * TAU / AROUND), yc + b * math.cos(k * TAU / AROUND), z) for k in range(AROUND)])

    def paint(i, k, c):
        yc, a, b = lerp_table(PLANE_BODY, c.z)
        up = (c.y - yc) / max(b, 1e-3)
        if up > 0.3 and in_cockpit(c.x, c.z):
            return "DARK"
        if up > 0.62:
            return None  # the yellow top
        if up > 0.18:
            return "RED"  # the cheat line, nose to tail
        return "CREAM"
    loft(s, rings, "YELLOW", paint=paint)

    # The cockpits: a leather rim round each hole, a headrest behind each seat, a windscreen in front.
    for c, a, b in COCKPITS:
        rim = []
        for k in range(20):
            t = k * TAU / 20
            x, z = a * math.cos(t), c + b * math.sin(t)
            rim.append((x, fuselage_at(x * 0.999, z) + 0.02, z))
        ring_tube(s, rim, 0.055, "BROWN", sides=6)
        for x in (-0.3, 0.3):
            ball(s, (x, fuselage_at(x, c - 0.36) + 0.12, c - 0.36), (0.17, 0.2, 0.07), "BROWN", segs=10, rings=6)
        zf = c + b + 0.04
        span = [-a + 0.08 + k * (2 * a - 0.16) / 6 for k in range(7)]
        screen = []
        for x in span:
            yb = fuselage_at(x, zf) - 0.02
            screen.append([(x, yb, zf), (x, yb + 0.32, zf - 0.13), (x, yb + 0.32, zf - 0.155), (x, yb, zf - 0.025)])
        loft(s, list(screen), "SKY", smooth=False)
        S.tube(s, [u(x, fuselage_at(x, zf) + 0.31, zf - 0.13) for x in span], 0.018, "METAL", sides=5)

    # Lettering down each side, between the cheat line and the tail.
    for side in (-1, 1):
        B.sign_text(s, "SOS", 0.42, (side * 0.6, 1.78, -1.1), (side, 0), "NAVY", depth=0.04, res=3)

    # The firewall the engine bolts to, with the mounts sticking out for it.
    turned(s, [(0.0, 0.0), (0.5, 0.0), (0.5, 0.04), (0.0, 0.04)], "METAL", z0=3.48, y=1.7, segs=AROUND,
           smooth=False)
    for k in range(4):
        t = TAU * (k + 0.5) / 4
        log(s, (0.38 * math.sin(t), 1.7 + 0.38 * math.cos(t), 3.5), (0.22 * math.sin(t), 1.7 + 0.22 * math.cos(t), 3.85),
            0.03, "DARK", sides=5)

    # The port wing, and the starboard root the other one bolts to.
    wing(s, -1, 0.45, 5.6)
    wing(s, 1, 0.45, 0.85, tip=False)
    for z in (0.0, 0.7):
        ball(s, (0.86, 1.62, z), 0.045, "METAL", segs=6, rings=4, outline=False)

    # The tail: fin and rudder, tailplane and elevators.
    I.side(s, [(-3.36, 2.1), (-2.35, 2.15), (-2.9, 2.6), (-3.18, 3.35), (-3.32, 3.6), (-3.36, 3.6)], 0.12, "YELLOW",
           bevel=0.02)
    I.side(s, [(-3.72, 2.2), (-3.38, 2.1), (-3.38, 3.6), (-3.55, 3.62), (-3.7, 3.45)], 0.1, "RED", bevel=0.02)
    for k, y in enumerate((2.75, 3.05, 3.3)):
        box(s, (-0.055, y, -3.7 + k * 0.03), (0.055, y + 0.12, -3.4), "CREAM")
    shaped(s, lambda: I.flat(s, [(-1.64, -3.42), (-1.4, -3.1), (-0.3, -2.75), (0.3, -2.75), (1.4, -3.1), (1.64, -3.42)],
                             0.08, "YELLOW"), at(0, 2.08, 0))
    shaped(s, lambda: I.flat(s, [(-1.6, -3.72), (-1.64, -3.44), (1.64, -3.44), (1.6, -3.72)], 0.07, "RED"),
           at(0, 2.085, 0))

    # The undercarriage: two legs splayed to the wheels on the strip, and the tail wheel.
    for side in (-1, 1):
        x = side * 1.1
        log(s, (side * 0.35, 1.05, 0.65), (x, 0.36, 0.6), 0.05, "DARK", sides=6, outline=True)
        log(s, (side * 0.35, 1.05, 0.05), (x, 0.36, 0.58), 0.035, "DARK", sides=6, outline=True)
        wheel(s, (x, 0.35, 0.6), 0.35, 0.18)
    # The tail is high on the strip, so its wheel hangs off a fin rather than a stick.
    I.side(s, [(-2.6, 1.6), (-3.05, 1.82), (-3.25, 0.5), (-3.12, 0.46)], 0.08, "RED", bevel=0.015)
    for x in (-0.07, 0.07):
        log(s, (x, 0.55, -3.18), (x, 0.2, -3.22), 0.025, "DARK", sides=5)
    wheel(s, (0, 0.2, -3.22), 0.2, 0.1)


def wheel(s, c, r, w, tyre="DARK", hub="CREAM"):
    h = w / 2
    axle_x(s, [(r * 0.55, -h), (r * 0.85, -h), (r, -h * 0.5), (r, h * 0.5), (r * 0.85, h), (r * 0.55, h)], tyre, c)
    axle_x(s, [(0.0, -h * 0.8), (r * 0.58, -h * 0.8), (r * 0.58, h * 0.8), (0.0, h * 0.8)], hub, c, smooth=False)
    axle_x(s, [(0.0, -h * 1.15), (r * 0.18, -h * 1.15), (r * 0.18, h * 1.15), (0.0, h * 1.15)], "METAL", c, segs=8,
           outline=False)


AIRFOIL = [(1.0, 0.0), (0.7, 0.055), (0.42, 0.095), (0.2, 0.1), (0.07, 0.075), (0.0, 0.02), (0.0, -0.02),
           (0.06, -0.045), (0.25, -0.05), (0.6, -0.03)]


def wing(s, side, x0, x1, tip=True):
    """A wing from x0 to x1 out on `side`: chord 1.5 tapering to 1.15, leading edge at z 1.15."""
    rings = []
    xs = [x0] + [x for x in (1.4, 2.6, 3.8, 4.8, 5.3) if x0 < x < x1] + [x1]
    if tip:
        xs[-1:] = [x1 - 0.12, x1]
    for x in xs:
        f = (x - 0.45) / 5.15
        chord = 1.5 - 0.35 * f
        le = 1.15 - 0.12 * f
        end = tip and x == x1
        k = (0.45 if end else 1.0) * (1 - 0.25 * f)
        rings.append([(side * x, 1.6 + y * chord * k, le - c * chord * (0.92 if end else 1.0)) for c, y in AIRFOIL])

    def paint(i, k, c):
        if abs(c.x) > 4.75:
            return "RED"
        if k in (0, 8, 9) and 2.4 < abs(c.x) < 4.6:
            return "CREAM"  # the aileron
        return None
    loft(s, rings, "YELLOW", paint=paint)
    if tip:
        ball(s, (side * (x1 + 0.02), 1.6, 0.55), (0.05, 0.05, 0.08), "RED" if side < 0 else "LIME", segs=8, rings=4,
             outline=False)


def plane_engine(s):
    """The radial engine and its cowling, ahead of the firewall at z 3.5."""
    turned(s, [(0.6, 0.0), (0.64, 0.25), (0.63, 0.65), (0.57, 0.95), (0.5, 0.97), (0.53, 0.6), (0.53, 0.1)], "RED",
           z0=3.52, y=1.7, segs=AROUND)
    turned(s, [(0.0, 0.0), (0.54, 0.0), (0.54, 0.04), (0.0, 0.04)], "DARK", z0=3.75, y=1.7, segs=AROUND, smooth=False,
           outline=False)
    for k in range(7):
        t = TAU * k / 7
        d = Vector((math.sin(t), math.cos(t), 0))
        base = Vector((0, 1.7, 4.15))
        for j in range(4):
            r0 = 0.2 + j * 0.07
            p0, p1 = base + d * r0, base + d * (r0 + 0.045)
            log(s, tuple(p0), tuple(p1), 0.1 - j * 0.008, "METAL", sides=8)
        log(s, tuple(base + d * 0.46), tuple(base + d * 0.5), 0.07, "DARK", sides=8)
    ball(s, (0, 1.7, 4.22), (0.24, 0.24, 0.16), "DARK", segs=12, rings=6)
    turned(s, [(0.06, 0.0), (0.06, 0.4), (0.0, 0.4)], "METAL", z0=4.3, y=1.7, segs=8, outline=False)
    for side in (-1, 1):
        log(s, (side * 0.45, 1.25, 3.9), (side * 0.52, 1.05, 3.3), 0.05, "DARK", sides=6, outline=True)


def plane_propeller(s):
    """Two blades across x round the hub at (0, 1.7, 4.7), a red spinner on the front."""
    turned(s, [(0.21, 0.0), (0.21, 0.08), (0.17, 0.22), (0.09, 0.34), (0.0, 0.4)], "RED", z0=4.62, y=1.7, segs=12)
    blade = [(0.12, -0.07), (0.45, -0.1), (0.95, -0.085), (1.15, -0.05), (1.2, 0.0), (1.15, 0.06), (0.9, 0.095),
             (0.45, 0.11), (0.12, 0.08)]
    for side in (-1, 1):
        before = len(s.bm.faces)
        pts = [(-side * x, y) for x, y in blade]
        twist = side * 0.35
        shaped(s, lambda: S.extrude(s, pts, 0.03, "DARK", outline=True),
               lambda p: (p[0], p[1] * math.cos(twist * (1 - abs(p[0]) / 1.2)), p[1] * math.sin(twist * (1 - abs(p[0]) / 1.2)) + p[2]),
               at(0, 1.7, 4.72))
        for f in list(s.bm.faces)[before:]:
            if abs(B.unity(f.calc_center_median()).x) > 1.0:
                f[s.col] = S.COL["YELLOW"]


def plane_wing(s):
    wing(s, 1, 0.85, 5.6)


# What lies on the ground: each hole's drawing turned to rest flat, then centred and stood on y 0.
PARTS = {
    "Engine": (plane_engine, lambda p: (p[0], p[1], p[2])),
    "Wing": (plane_wing, B.turn_y(-90)),
    "Propeller": (plane_propeller, rx(-90)),
}


def grounded(s):
    """Centred on x and z, standing on y 0. Prints its size, which PlanePartBuilder's collider wants."""
    pts = [B.unity(v.co) for v in s.bm.verts]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    shift = Vector(((lo.x + hi.x) / 2, lo.y, (lo.z + hi.z) / 2))
    for v in s.bm.verts:
        v.co = u(*(B.unity(v.co) - shift))
    return hi - lo


# ------------------------------------------------------------------------------------------- boat

# BoatBuilder's hull box is 2.4 x 1 x 6 on the keel; the bow box reaches z 4.2. The riders stand on
# the deck at y 0.88 and the transom bench takes the body at z -2.1.
HULL = [(0.0, 0.0), (0.45, 0.1), (0.95, 0.3), (1.08, 0.5), (1.15, 0.72), (1.19, 0.92), (1.2, 1.15)]
HULL_Z = [-3.0, -2.2, -1.2, 0.0, 1.0, 1.8, 2.5, 3.1, 3.6, 3.95, 4.18]
DECK = 0.88


def hull_point(i, z, inner=False):
    x, y = HULL[i]
    w = 1.0 if z <= 1.0 else math.sqrt(max(0.0, 1 - ((z - 1.0) / 3.25) ** 2))
    keel = 0.0 if z <= 1.5 else 0.85 * ((z - 1.5) / 2.7) ** 2
    sheer = 0.0 if z <= 0.0 else 0.32 * (z / 4.2) ** 2
    t = y / 1.15
    y = y + keel * (1 - t) + sheer * t
    x *= w
    if inner:
        x = max(x - 0.07, 0.0)
        y += 0.07 * (1 - t)
    return x, y


def hull_x(y, z):
    """How wide the outer hull is at height y, at z."""
    pts = [hull_point(i, z) for i in range(len(HULL))]
    for (xa, ya), (xb, yb) in zip(pts, pts[1:]):
        if ya <= y <= yb:
            return xa + (xb - xa) * (y - ya) / max(yb - ya, 1e-4)
    return pts[-1][0]


def boat(s):
    n = len(HULL)
    rings = []
    for z in HULL_Z:
        outer = [(-hull_point(i, z)[0], hull_point(i, z)[1], z) for i in range(n - 1, 0, -1)]
        outer += [(hull_point(i, z)[0], hull_point(i, z)[1], z) for i in range(n)]
        inner = [(hull_point(i, z, True)[0], hull_point(i, z, True)[1], z) for i in range(n - 1, 0, -1)]
        inner += [(-hull_point(i, z, True)[0], hull_point(i, z, True)[1], z) for i in range(n)]
        rings.append(outer + inner)
    m = 2 * n - 1  # points on each skin

    def paint(i, k, c):
        if k == m - 1 or k == 2 * m - 1:
            return "BROWN"  # the gunwale rail
        if k >= m:
            return None  # inside
        j = (n - 2 - k) if k < n - 1 else k - (n - 1)
        return ("RED", "RED", "CREAM", "CREAM", "NAVY", "CREAM")[min(j, 5)]
    loft(s, rings, "CREAM", caps=False, paint=paint)
    # The transom, a strip from the outer skin to the inner one.
    stern = rings[0]
    before = s.begin()
    vs = [s.bm.verts.new(u(*p)) for p in stern]
    for k in range(m - 1):
        s.bm.faces.new((vs[k], vs[k + 1], vs[2 * m - 2 - k], vs[2 * m - 1 - k]))
    faces = s.end(before, "CREAM", False, outline=True)
    bmesh.ops.recalc_face_normals(s.bm, faces=faces)
    bow = rings[-1]
    log(s, (0, bow[n - 1][1], 4.18), (0, bow[0][1] + 0.02, 4.18), 0.06, "METAL", sides=6, outline=True)

    # The deck, planked, wall to wall at its own height.
    zs = [-2.93, -2.0, -1.0, 0.0, 1.0, 1.8, 2.5, 3.0, 3.35]
    plan = [(hull_x(DECK, z) - 0.08, z) for z in zs]
    outline = [(x, z) for x, z in plan] + [(-x, z) for x, z in reversed(plan)]
    B.prism(s, [(x, DECK - 0.06, z) for x, z in outline], [(x, DECK, z) for x, z in outline], "BROWN")
    for x in (-0.66, -0.22, 0.22, 0.66):
        box(s, (x - 0.012, DECK, -2.9), (x + 0.012, DECK + 0.006, 2.6 if abs(x) < 0.5 else 1.8), "DARK")

    # The helm: a console in front of the driver, its wheel, a windscreen; a grab rail for the other.
    B.box(s, (-0.82, DECK, 1.45), (-0.1, 1.62, 1.88), "CREAM", outline=True)
    shaped(s, lambda: B.box(s, (-0.84, 0.0, -0.12), (-0.08, 0.06, 0.32), "NAVY", outline=True), rx(-20),
           at(0, 1.62, 1.52))
    for k, col in enumerate(("LIME", "YELLOW", "RED")):
        ball(s, (-0.7 + k * 0.12, 1.71, 1.62), 0.03, col, segs=6, rings=4, outline=False)
    ring_tube(s, [(-0.45 + 0.17 * math.cos(t * TAU / 12), 1.62 + 0.17 * math.sin(t * TAU / 12) * 0.85,
                   1.42 - 0.17 * math.sin(t * TAU / 12) * 0.5) for t in range(12)], 0.022, "DARK", sides=5)
    log(s, (-0.45, 1.62, 1.42), (-0.45, 1.58, 1.52), 0.03, "METAL")
    screen = [[(x, 1.78, 1.86), (x, 2.12, 1.74), (x, 2.12, 1.72), (x, 1.78, 1.84)]
              for x in (-0.86, -0.6, -0.32, -0.06)]
    loft(s, screen, "SKY", smooth=False)
    log(s, (-0.86, 2.12, 1.73), (-0.06, 2.12, 1.73), 0.02, "METAL", sides=5)
    S.tube(s, [u(0.95, DECK, 1.3), u(0.95, 1.55, 1.35), u(0.75, 1.75, 1.45), u(0.25, 1.75, 1.45), u(0.15, DECK, 1.5)],
           0.025, "METAL", sides=5, outline=True)

    # The transom bench the cargo lies on, cushioned.
    B.box(s, (-1.0, DECK, -2.88), (1.0, 1.12, -2.42), "CREAM", outline=True)
    B.box(s, (-0.96, 1.12, -2.86), (0.96, 1.22, -2.44), "RED", outline=True)
    B.box(s, (-0.96, 1.12, -2.98), (0.96, 1.5, -2.86), "RED", outline=True)

    # The outboard on its bracket, down into the water.
    B.box(s, (-0.12, 0.85, -3.18), (0.12, 1.2, -3.0), "DARK", outline=True)
    ball(s, (0, 1.42, -3.42), (0.25, 0.3, 0.34), "RED", segs=12, rings=8)
    box(s, (-0.255, 1.36, -3.6), (0.255, 1.42, -3.2), "CREAM")
    log(s, (0, 1.15, -3.38), (0, -0.2, -3.38), 0.07, "DARK", sides=6, outline=True)
    turned(s, [(0.0, 0.0), (0.09, 0.08), (0.1, 0.3), (0.06, 0.48), (0.0, 0.5)], "DARK", z0=-3.62, y=-0.25, segs=8)
    for k in range(3):
        t = TAU * k / 3
        ball(s, (0.17 * math.sin(t), -0.25 + 0.17 * math.cos(t), -3.66), (0.06 + 0.04 * abs(math.sin(t)),
             0.06 + 0.04 * abs(math.cos(t)), 0.02), "METAL", segs=6, rings=4, outline=False)
    log(s, (0, 1.6, -3.55), (-0.3, 1.25, -2.95), 0.025, "METAL")

    # The bow: a rail round it, a cleat, port and starboard lights, the name either side.
    for side in (-1, 1):
        rail = []
        for z in (1.6, 2.3, 2.9, 3.4, 3.8, 4.1):
            x = hull_x(1.2, z) - 0.05
            rail.append((side * x, hull_point(len(HULL) - 1, z)[1] + 0.3, z))
        S.tube(s, [u(*p) for p in rail], 0.025, "METAL", sides=5, outline=True)
        for p in rail[::2]:
            log(s, (p[0], p[1] - 0.3, p[2]), p, 0.02, "METAL", sides=4)
        y = hull_point(len(HULL) - 1, 3.3)[1]
        ball(s, (side * (hull_x(1.2, 3.3) + 0.01), y - 0.08, 3.3), 0.05, "RED" if side < 0 else "LIME", segs=6,
             rings=4, outline=False)
        z = 0.45
        x = hull_x(0.62, z) + 0.02
        B.sign_text(s, "LUCKY 7", 0.26, (side * x, 0.64, z), (side, 0), "NAVY", depth=0.03, res=3)
        for z in (-2.1, -0.9):
            ball(s, (side * (hull_x(0.75, z) + 0.1), 0.75, z), (0.1, 0.2, 0.1), "WHITE", segs=8, rings=6)
    B.box(s, (-0.06, hull_point(len(HULL) - 1, 3.9)[1], 3.8), (0.06, hull_point(len(HULL) - 1, 3.9)[1] + 0.05, 4.0),
          "METAL")


# ------------------------------------------------------------------------------------------ buggy

# VehicleBuilder's chassis is 1.9 x 0.7 x 3.6 round (0, 0.75, 0); wheels of 0.45 at x 0.78, z 1.25;
# riders' feet at y 0.95, front pair at z 0.55, rear at -0.75; the bed at z -1.55.
def buggy(s):
    B.box(s, (-0.5, 0.42, -1.85), (0.5, 0.92, 1.9), "DARK", outline=True)
    for side in (-1, 1):
        x0, x1 = sorted((side * 0.48, side * 0.64))
        T.ubox(s, (x0, 0.55, -1.0), (x1, 1.28, 0.85), "ORANGE", bevel=0.03)
        box(s, (x0 - 0.004, 1.05, -1.0), (x1 + 0.004, 1.12, 0.85), "CREAM")
    # The bonnet, sloping to the nose, with a chip on it.
    hood = [(0.85, 0.6), (0.85, 1.38), (1.15, 1.36), (1.95, 1.08), (2.0, 0.85), (1.85, 0.6)]
    I.side(s, hood, 1.26, "ORANGE", bevel=0.03)
    slope = math.degrees(math.atan2(1.36 - 1.08, 1.95 - 1.15))
    shaped(s, lambda: I.lathe(s, [(0.0, 0.0), (0.24, 0.0), (0.24, 0.025), (0.0, 0.025)], "CREAM", segs=16,
                              smooth=False), rx(slope), at(0, 1.24, 1.55))
    shaped(s, lambda: B.sign_text(s, "7", 0.3, (0, 0, 0.035), (0, 1), "RED", depth=0.02, res=3), rx(-90 + slope),
           at(0, 1.24, 1.55))
    for x in (-0.42, 0.42):
        turned(s, [(0.0, 0.0), (0.11, 0.0), (0.12, 0.06), (0.0, 0.07)], "CREAM", z0=1.94, x=x, y=1.02, segs=10)
        turned(s, [(0.12, -0.03), (0.13, 0.02)], "METAL", z0=1.94, x=x, y=1.02, segs=10, outline=False)
    # Bull bar and bumper.
    S.tube(s, [u(-0.65, 0.62, 2.0), u(-0.62, 0.62, 2.08), u(0.62, 0.62, 2.08), u(0.65, 0.62, 2.0)], 0.045, "METAL",
           sides=6)
    for x in (-0.3, 0.3):
        S.tube(s, [u(x, 0.62, 2.08), u(x, 1.0, 2.12), u(x * 0.9, 1.15, 2.02)], 0.035, "METAL", sides=6)
    # Fenders over all four wheels.
    for side in (-1, 1):
        for z in (-1.25, 1.25):
            rings = []
            for k in range(9):
                t = math.pi * (0.1 + 0.8 * k / 8)
                cz, cy = z + 0.6 * math.cos(t), 0.45 + 0.6 * math.sin(t)
                oz, oy = z + 0.66 * math.cos(t), 0.45 + 0.66 * math.sin(t)
                xa, xb = side * 0.58, side * 0.98
                rings.append([(xa, cy, cz), (xb, cy, cz), (xb, oy, oz), (xa, oy, oz)])
            loft(s, rings, "ORANGE", smooth=False)
            log(s, (side * 0.55, 1.2, z - 0.12 * (1 if z > 0 else -1)), (side * 0.72, 0.52, z), 0.03, "YELLOW",
                sides=5, outline=True)
    # Dash and wheel.
    B.box(s, (-0.6, 1.28, 0.82), (0.6, 1.45, 0.95), "DARK", outline=True)
    ring_tube(s, [(-0.42 + 0.17 * math.cos(t * TAU / 12), 1.58 + 0.17 * math.sin(t * TAU / 12) * 0.8,
                   0.8 - 0.17 * math.sin(t * TAU / 12) * 0.6) for t in range(12)], 0.024, "DARK", sides=5)
    log(s, (-0.42, 1.58, 0.8), (-0.42, 1.4, 0.92), 0.03, "METAL")
    # Bucket seat backs behind each rider.
    for x in (-0.42, 0.42):
        for z in (0.55, -0.75):
            T.ubox(s, (x - 0.22, 0.92, z - 0.44), (x + 0.22, 1.62, z - 0.3), "RED", bevel=0.05)
            box(s, (x - 0.06, 0.95, z - 0.425), (x + 0.06, 1.6, z - 0.295), "CREAM")
    # The roll bar behind the back seats, a light bar on it.
    hoop = [(-0.6, 0.92, -1.2), (-0.58, 1.9, -1.12), (-0.48, 2.12, -1.08), (0.48, 2.12, -1.08), (0.58, 1.9, -1.12),
            (0.6, 0.92, -1.2)]
    S.tube(s, [u(*p) for p in hoop], 0.045, "METAL", sides=6, outline=True)
    for side in (-1, 1):
        log(s, (side * 0.58, 1.9, -1.12), (side * 0.6, 0.98, -1.82), 0.035, "METAL", outline=True)
    box(s, (-0.42, 2.17, -1.16), (0.42, 2.27, -1.0), "DARK")
    for k in range(4):
        turned(s, [(0.0, 0.0), (0.05, 0.0), (0.055, 0.04), (0.0, 0.045)], "YELLOW", z0=-1.0, x=-0.3 + k * 0.2, y=2.22,
               segs=8, outline=False)
    # The bed: planks and low sides; a spare tyre; two exhausts curling up out of the back.
    B.box(s, (-0.6, 0.92, -1.85), (0.6, 1.0, -1.22), "BROWN", outline=True)
    for side in (-1, 1):
        x0, x1 = sorted((side * 0.52, side * 0.62))
        T.ubox(s, (x0, 0.6, -1.88), (x1, 1.3, -1.0), "ORANGE", bevel=0.03)
    T.ubox(s, (-0.52, 0.6, -1.9), (0.52, 1.25, -1.8), "ORANGE", bevel=0.03)
    for side in (-1, 1):
        S.tube(s, [u(side * 0.3, 0.6, -1.8), u(side * 0.32, 0.6, -2.0), u(side * 0.34, 0.9, -2.12),
                   u(side * 0.34, 1.35, -2.08)], 0.05, "METAL", sides=6)
        ball(s, (side * 0.34, 1.37, -2.08), (0.065, 0.03, 0.065), "DARK", segs=8, rings=4, outline=False)


def buggy_wheel(s):
    """One tyre, axle along y, centred: CarController lays it on its side and rolls it."""
    I.lathe(s, [(0.26, -0.17), (0.39, -0.17), (0.45, -0.11), (0.45, 0.11), (0.39, 0.17), (0.26, 0.17)], "DARK",
            segs=16)
    I.lathe(s, [(0.0, -0.14), (0.27, -0.14), (0.27, 0.14), (0.0, 0.14)], "YELLOW", segs=12, smooth=False)
    I.lathe(s, [(0.0, -0.19), (0.08, -0.19), (0.08, 0.19), (0.0, 0.19)], "METAL", segs=8, smooth=False,
            outline=False)
    for k in range(14):
        shaped(s, lambda: box(s, (-0.05, -0.155, 0.43), (0.05, 0.155, 0.475), "DARK"), B.turn_y(k * 360 / 14))
    for k in range(5):
        for y in (-0.145, 0.145):
            shaped(s, lambda y=y: box(s, (-0.025, y - 0.006, 0.08), (0.025, y + 0.006, 0.25), "DARK"),
                   B.turn_y(k * 72))


# ------------------------------------------------------------------------------------------- main

MODELS = [
    ("Plane", plane), ("Plane_Engine", plane_engine), ("Plane_Wing", plane_wing),
    ("Plane_Propeller", plane_propeller),
    ("Boat", boat), ("Buggy", buggy), ("Buggy_Wheel", buggy_wheel),
]


def main(out_dir=None, only=None):
    S.P.clear()
    folder = os.path.join(out_dir, "Textures") if out_dir else os.path.join(bpy.app.tempdir or ".", "slots")
    mat = S.material(S.paint_texture(folder))
    jobs = [(name, make, None) for name, make in MODELS]
    jobs += [("Part_" + name, make, turn) for name, (make, turn) in PARTS.items()]
    objects = []
    for i, (name, make, turn) in enumerate(jobs):
        if only and name not in only:
            continue
        s = S.Sym()
        make(s)
        if turn:
            for v in s.bm.verts:
                v.co = u(*turn(B.unity(v.co)))
            size = grounded(s)
            print(f"[vehicles] Veh_{name} size {size.x:.2f} x {size.y:.2f} x {size.z:.2f}")
        obj = S.finish(s, "Veh_" + name, mat, fit=False, outline=0.012)
        print(f"[vehicles] {obj.name}: {tri_count(obj)} tris")
        if out_dir is None:
            obj.location = (-(i % 4) * 9, (i // 4) * 9, 0)
        objects.append(obj)

    if out_dir is None:
        return objects

    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "Vehicles.fbx"), use_selection=True,
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z",
                             axis_up="Y", mesh_smooth_type="FACE", bake_space_transform=True, path_mode="RELATIVE")
    print(f"[vehicles] exported {len(objects)} meshes to {out_dir}")
    return objects


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    main(argv[0] if argv else None)
